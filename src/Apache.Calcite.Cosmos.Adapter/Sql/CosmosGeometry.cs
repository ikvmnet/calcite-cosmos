using org.locationtech.jts.geom;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// Decodes a stored GeoJSON value into the geometry Calcite's spatial library works in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a storage decode, and it is the reason Calcite's spatial functions can run over a
    /// container at all.</b> A Cosmos document holds a geometry as a GeoJSON object, which the row
    /// model materialises as a <see cref="java.util.Map"/> because a container declares no types.
    /// Calcite's <c>ST_WITHIN</c> and its siblings take a JTS <see cref="Geometry"/>. Handed the map
    /// directly they fail — measured, <c>InvalidCastException: Unable to cast object of type
    /// 'java.util.LinkedHashMap' to type 'org.locationtech.jts.geom.Geometry'</c> — because the cast
    /// Calcite inserts is a plain one. So the value has to be decoded, which is what a storage engine
    /// does between its storage representation and the types the engine computes in.
    /// </para>
    /// <para>
    /// <b>Named by the query rather than sniffed.</b> The conversion is an operator a caller writes —
    /// <c>ST_WITHIN(GEOMETRY(c."_MAP"['location']), …)</c> — and not something applied to every object
    /// that happens to carry a <c>type</c> and <c>coordinates</c>. Deciding a value's type by looking
    /// at the value would change what an ordinary projection of that path returns, and would do it on
    /// a guess; the row model refuses that everywhere else and refuses it here.
    /// </para>
    /// <para>
    /// The four types are the ones <see cref="CosmosGeoJson"/> emits, and for the same reason: they are
    /// what Cosmos documents for indexing and querying, so a geometry this decodes is one the service
    /// could also have been asked about. A value that is not one of them decodes to <c>null</c>, which
    /// is what a spatial predicate over a document holding no geometry should see.
    /// </para>
    /// </remarks>
    public static class CosmosGeometry
    {

        static readonly GeometryFactory Factory = new();

        /// <summary>
        /// Decodes a materialised document value as a geometry.
        /// </summary>
        /// <remarks>
        /// Public and static because it is an operator implementation: <c>CosmosOperators.Geometry</c>
        /// wraps this method, and Calcite generates a call to it. Returns <c>null</c> rather than
        /// throwing for anything it does not recognise — a spatial predicate over a document with no
        /// geometry, or with something else at the path, is a row that does not match rather than a
        /// query that fails.
        /// </remarks>
        /// <param name="value">The materialised value, as the row model produced it.</param>
        /// <returns>The geometry, or <c>null</c> where the value is not one.</returns>
        public static Geometry? FromDocument(object? value)
        {
            if (value is not java.util.Map map)
                return null;

            if (map.get("type") is not string type || map.get("coordinates") is not java.util.List coordinates)
                return null;

            return type switch
            {
                "Point" => TryPoint(coordinates),
                "LineString" => TryLineString(coordinates),
                "Polygon" => TryPolygon(coordinates),
                "MultiPolygon" => TryMultiPolygon(coordinates),
                _ => null,
            };
        }

        /// <summary>
        /// Reads one position — a longitude and a latitude, and an altitude this discards.
        /// </summary>
        /// <remarks>
        /// JTS carries a third ordinate but Calcite's planar operations ignore it, so keeping it would
        /// suggest a precision the answers do not have.
        /// </remarks>
        static Coordinate? TryCoordinate(object? value)
        {
            if (value is not java.util.List position || position.size() < 2)
                return null;

            if (TryOrdinate(position.get(0)) is not double x || TryOrdinate(position.get(1)) is not double y)
                return null;

            return new Coordinate(x, y);
        }

        /// <summary>
        /// Reads one ordinate, which the row model produced as a boxed long or double.
        /// </summary>
        static double? TryOrdinate(object? value)
        {
            return value switch
            {
                java.lang.Long l => l.doubleValue(),
                java.lang.Double d => d.doubleValue(),
                java.lang.Number n => n.doubleValue(),
                _ => null,
            };
        }

        /// <summary>
        /// Reads a list of positions, declining the whole list where any of them is not one.
        /// </summary>
        static Coordinate[]? TryCoordinates(java.util.List value, int minimum)
        {
            if (value.size() < minimum)
                return null;

            var coordinates = new Coordinate[value.size()];

            for (var i = 0; i < value.size(); i++)
            {
                if (TryCoordinate(value.get(i)) is not Coordinate coordinate)
                    return null;

                coordinates[i] = coordinate;
            }

            return coordinates;
        }

        static Geometry? TryPoint(java.util.List coordinates)
        {
            return TryCoordinate(coordinates) is Coordinate coordinate ? Factory.createPoint(coordinate) : null;
        }

        static Geometry? TryLineString(java.util.List coordinates)
        {
            return TryCoordinates(coordinates, 2) is Coordinate[] positions ? Factory.createLineString(positions) : null;
        }

        /// <summary>
        /// Reads a polygon, whose first ring is the shell and whose rest are holes.
        /// </summary>
        /// <remarks>
        /// A GeoJSON ring is closed — its last position repeats its first — which is what JTS's
        /// <c>createLinearRing</c> requires, so a ring that is not closed is declined rather than
        /// closed on the caller's behalf. Guessing at the missing position would invent an edge.
        /// </remarks>
        static Geometry? TryPolygon(java.util.List rings)
        {
            if (rings.size() < 1)
                return null;

            var built = new LinearRing[rings.size()];

            for (var i = 0; i < rings.size(); i++)
            {
                if (rings.get(i) is not java.util.List ring || TryCoordinates(ring, 4) is not Coordinate[] positions)
                    return null;

                if (positions[0].equals2D(positions[positions.Length - 1]) == false)
                    return null;

                built[i] = Factory.createLinearRing(positions);
            }

            var holes = new LinearRing[built.Length - 1];
            System.Array.Copy(built, 1, holes, 0, holes.Length);

            return Factory.createPolygon(built[0], holes);
        }

        static Geometry? TryMultiPolygon(java.util.List polygons)
        {
            if (polygons.size() < 1)
                return null;

            var built = new Polygon[polygons.size()];

            for (var i = 0; i < polygons.size(); i++)
            {
                if (polygons.get(i) is not java.util.List rings || TryPolygon(rings) is not Polygon polygon)
                    return null;

                built[i] = polygon;
            }

            return Factory.createMultiPolygon(built);
        }

    }

}
