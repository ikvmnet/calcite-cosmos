# 18. Geography

Cosmos stores shapes as GeoJSON and evaluates spatial functions **geodesically**: coordinates are
WGS84 longitude and latitude, and distances are in metres. Calcite's own `ST_*` functions are
**planar** — JTS geometry over an unprojected coordinate system, answering in the units of that
system, here degrees. The two are different questions with the same spelling, and not off by a
factor: one degree of longitude is 111 km at the equator and 19 km at 80° north, and two candidates
can swap order between the two models.

The geodesic reading comes from the `Apache.Calcite.Geography` package, which the adapter depends on.
Its operators are named `CLR_ST_GEOG_*`.

## 18.1 There is no `GEOGRAPHY` type

A geography and a geometry are the same SQL type (`GEOMETRY`), carried by the same class. **The
operator's name is the whole of what says which reading is meant**: `CLR_ST_GEOG_DISTANCE` is geodesic
metres, `ST_DISTANCE` is planar degrees. Calcite's type system has no room for a type of the package's
own that a schema could register, so the type gave way to the registration.

The cost is that a mixed expression is not refused: `CLR_ST_GEOG_DISTANCE(ST_BUFFER(g, 0.1), h)`
buffers by 0.1 *degrees* and then measures in *metres*, and both halves run. Use the `CLR_ST_GEOG_*`
family consistently.

## 18.2 Making the names available

On a connection, register the geography functions on the root schema — through the data source
builder, since a connection's root is read-only:

```csharp
var dataSource = new CalciteDataSourceBuilder(connectionString)
    .ConfigureRootSchema(root => Apache.Calcite.Geography.Schema.GeographySchema.AddTo(root))
    .Build();
```

A host that assembles its own planner chains the operator table instead:

```csharp
SqlOperatorTables.chain(SqlStdOperatorTable.instance(), GeographyOperatorTable.Instance())
```

## 18.3 Reading a stored shape

No column is typed as a geometry. A shape stored in a document reaches an operator by being parsed
from its GeoJSON:

```sql
SELECT c."id"
FROM "products" AS c
WHERE CLR_ST_GEOG_DWITHIN(
        CLR_ST_GEOG_GEOMFROMGEOJSON(JSON_QUERY(c."DOC", '$.location')),
        CLR_ST_GEOG_GEOMFROMGEOJSON('{"type":"Point","coordinates":[-122.3,47.6]}'),
        1000)
→ WHERE ST_DISTANCE(c.location, {"type":"Point","coordinates":[-122.3,47.6]}) <= 1000
```

That pushes, and nothing is parsed: `JSON_QUERY` over `DOC` is a document path, so the constructor
collapses onto it and the statement names the property — the service reads it as the shape. A
constructor over a literal is written into the statement as the GeoJSON object. A constructor over a
*computed* string is declined and evaluated in process.

## 18.4 What pushes

| written | sent as |
| --- | --- |
| `CLR_ST_GEOG_DISTANCE(a, b)` | `ST_DISTANCE(a, b)` |
| `CLR_ST_GEOG_WITHIN(a, b)` | `ST_WITHIN(a, b)` |
| `CLR_ST_GEOG_INTERSECTS(a, b)` | `ST_INTERSECTS(a, b)` |
| `CLR_ST_GEOG_ISVALID(a)` | `ST_ISVALID(a)` |
| `CLR_ST_GEOG_DWITHIN(a, b, d)` | `ST_DISTANCE(a, b) <= d` — inclusive, measured to match the service's own comparison |
| `CLR_ST_GEOG_GEOMETRYTYPE(<stored shape>)` | `c.location.type` — GeoJSON records the type as a member |
| `CLR_ST_GEOG_ASGEOJSON(<stored shape>)` in a select list | the property itself, read as JSON text |
| `CLR_ST_GEOG_GEOMFROMGEOJSON(JSON_QUERY(…))` in a select list | the property, read back into a geometry |

Everything else in the package is evaluated in process. `CLR_ST_GEOG_X` and `CLR_ST_GEOG_Y` are not
pushed: they would be `coordinates[0]` and `[1]` only for a point, and over a polygon the service would
return a ring where the in-process function raises.

Selecting a shape returns a geometry with SRID 4326, built by the package's own GeoJSON reader, so a
pushed column and an in-process one are the same value.

## 18.5 Ordering by distance

```sql
SELECT c."id",
       CLR_ST_GEOG_DISTANCE(CLR_ST_GEOG_GEOMFROMGEOJSON(JSON_QUERY(c."DOC", '$.location')),
                            CLR_ST_GEOG_GEOMFROMGEOJSON('{"type":"Point","coordinates":[-122.3,47.6]}')) AS "metres"
FROM "products" AS c
ORDER BY 2
FETCH FIRST 10 ROWS ONLY
→ … ORDER BY ST_DISTANCE(c.location, {"type":"Point",…}) ASC OFFSET 0 LIMIT 10
```

The distance is written into the statement twice, once selected and once ordered, because Cosmos
cannot order by a projection alias. (This sort is covered by the adapter's own planning tests; the
exact plan shape a `CalciteConnection` presents for it has not yet been separately verified — check
`EXPLAIN PLAN FOR` if a distance-ordered page is slow.)

Cosmos normally refuses an `ORDER BY` on anything but a path; a geodesic distance is the measured
exception. The distance must be the **only** sort key — a second key beside it is rejected by the
service. A distance is nullable (a document may have no location), so this needs
`DefaultNullCollation = "LOW"` (Chapter 9).

A geometry itself is not an orderable value; ordering by a shape runs in process.

## 18.6 Parameters

A parameter whose value is a geometry is bound as the GeoJSON object it represents, exactly as a
literal shape is written into the statement — so a parameterised proximity query behaves like a
literal one. (A *string* parameter passed to `CLR_ST_GEOG_GEOMFROMGEOJSON` is a computed constructor
and is evaluated in process; pass the geometry, or write the GeoJSON as a literal.)

## 18.7 Containers that read coordinates as a plane

What `ST_DISTANCE` means at the service is decided by the container's `geospatialConfig`, not by the
name in the query: over a container configured as `Geometry`, the service answers the planar question,
in the units of the coordinate system, and says nothing about having done so. Measured, the same
statement over the same two points:

| container | `ST_DISTANCE(c.location, <point>)` |
| --- | --- |
| `Geography` | `1342.14…` — metres |
| `Geometry` | `0.01414…` — degrees |

So **a `CLR_ST_GEOG_*` call over a container configured as `Geometry` is refused while planning**,
rather than sent to return a number in the wrong units.

## 18.8 A pushed spatial predicate is not rechecked

Spatial predicates push exactly or not at all; a weakened version with an in-process recheck is not
used. The reason is measured: the service computes distances on the WGS84 ellipsoid and the package's
in-process evaluator on a sphere, and they differ by up to about half a percent. A recheck would discard
rows whose true distance sits within that margin of the threshold.

A consequence: a query whose spatial predicate does *not* push — say, over a computed shape — is
evaluated entirely by the in-process, spherical evaluator, and can disagree with a pushed version of
the same query near a boundary.

## 18.9 Not offered

`ST_ISVALIDDETAILED` (Cosmos-specific, returns a document), a geodesic `ST_AREA`, and sorting by
anything spatial other than a distance.

---

[← Previous: Full text and vector search](17-full-text-and-vector-search.md) · [Contents](README.md) · [Next: Performance and cost →](19-performance.md)
