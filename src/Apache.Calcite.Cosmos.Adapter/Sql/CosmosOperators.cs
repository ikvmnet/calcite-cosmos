using System;

using org.apache.calcite.sql;
using org.apache.calcite.sql.type;
using org.apache.calcite.sql.util;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// The Cosmos functions that have no counterpart in SQL, and the operator table that makes them
    /// nameable in a query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Full text search is not SQL. Calcite's standard operator table has nothing to map onto
    /// <c>FULLTEXTCONTAINS</c>, so a query cannot name it unless the operator exists — which is what
    /// these are. Chain <see cref="Instance"/> into the operator table the validator is built with:
    /// </para>
    /// <code>
    /// SqlOperatorTables.chain(SqlStdOperatorTable.instance(), CosmosOperators.Instance)
    /// </code>
    /// <para>
    /// The signatures are the service's, taken from the query language reference. The first argument of
    /// every one of them is a <em>property path</em> rather than an arbitrary expression, and the
    /// translator holds them to that: a call over anything it cannot resolve to a path is declined.
    /// </para>
    /// <para>
    /// The scoring functions are here too, and ordering by any of them becomes <c>ORDER BY RANK</c>,
    /// which is the shape <c>CosmosRankRule</c> recognises. They do not all carry the same restriction:
    /// <c>FULLTEXTSCORE</c> and <c>RRF</c> are legal in that clause alone and may not be projected, so
    /// the translator refuses them anywhere else, while <c>VECTORDISTANCE</c> may be projected and
    /// renders like any other function.
    /// </para>
    /// </remarks>
    public static class CosmosOperators
    {

        /// <summary>
        /// <c>FULLTEXTCONTAINS(&lt;property_path&gt;, &lt;string_expr&gt;)</c> — whether the keyword
        /// occurs in the property.
        /// </summary>
        public static readonly SqlFunction FullTextContains = Predicate("FULLTEXTCONTAINS", 2, 2);

        /// <summary>
        /// <c>FULLTEXTCONTAINSALL(&lt;property_path&gt;, &lt;string_expr1&gt;, …)</c> — whether every
        /// keyword occurs in the property.
        /// </summary>
        public static readonly SqlFunction FullTextContainsAll = Predicate("FULLTEXTCONTAINSALL", 2, -1);

        /// <summary>
        /// <c>FULLTEXTCONTAINSANY(&lt;property_path&gt;, &lt;string_expr1&gt;, …)</c> — whether any
        /// keyword occurs in the property.
        /// </summary>
        public static readonly SqlFunction FullTextContainsAny = Predicate("FULLTEXTCONTAINSANY", 2, -1);

        /// <summary>
        /// <c>FULLTEXTSCORE(&lt;property_path&gt;, &lt;string_expr1&gt;, …)</c> — a BM25 relevance score.
        /// </summary>
        /// <remarks>
        /// Legal only in an <c>ORDER BY RANK</c> clause or as an argument to <see cref="Rrf"/>, and
        /// explicitly not in a projection. It exists as an operator so a query can write
        /// <c>ORDER BY FULLTEXTSCORE(…)</c>; <c>CosmosRankRule</c> is what recognises that shape and
        /// turns it into the clause. The translator refuses it everywhere else, so a plan that reached
        /// a <c>WHERE</c> or a select list with one declines rather than emitting a rejected statement.
        /// </remarks>
        public static readonly SqlFunction FullTextScore = Scoring("FULLTEXTSCORE", 2);

        /// <summary>
        /// <c>RRF(&lt;function1&gt;, &lt;function2&gt;, …, &lt;weights&gt;)</c> — a fused score.
        /// </summary>
        /// <remarks>
        /// Combines two or more scoring functions, optionally weighted by a trailing array. Subject to
        /// the same restriction as <see cref="FullTextScore"/>, and additionally cannot be combined with
        /// ordering on other property paths.
        /// </remarks>
        public static readonly SqlFunction Rrf = Scoring("RRF", 2);

        /// <summary>
        /// <c>VECTORDISTANCE(&lt;vector1&gt;, &lt;vector2&gt;, [&lt;brute_force&gt;], [&lt;options&gt;])</c> —
        /// the similarity between two vectors.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="FullTextScore"/> this one <em>may</em> be projected — the reference's own
        /// example selects it as <c>SimilarityScore</c> — so it is an ordinary function that also
        /// happens to be rankable. Ordering by it becomes <c>ORDER BY RANK</c>, and <see cref="Rrf"/>
        /// fuses it with a full text score for hybrid search.
        /// <para>
        /// The optional third argument forces brute force over any vector index; the fourth is an
        /// object literal of options — <c>distanceFunction</c>, <c>dataType</c> and the recall knobs.
        /// </para>
        /// </remarks>
        public static readonly SqlFunction VectorDistance = Scoring("VECTORDISTANCE", 2);

        /// <summary>
        /// <c>IS_DEFINED(&lt;expr&gt;)</c> — whether the property exists on the document at all.
        /// </summary>
        /// <remarks>
        /// The one type test with no SQL counterpart even in spirit. SQL has no <c>undefined</c>: a
        /// column is null or it is not, whereas a Cosmos property may be absent, and only this
        /// distinguishes absent from present-and-null. <c>IS NULL</c> already renders as both together,
        /// so this exists for a query that needs to tell them apart.
        /// </remarks>
        public static readonly SqlFunction IsDefined = TypeTest("IS_DEFINED");

        /// <summary><c>IS_ARRAY(&lt;expr&gt;)</c>.</summary>
        public static readonly SqlFunction IsArray = TypeTest("IS_ARRAY");

        /// <summary><c>IS_BOOL(&lt;expr&gt;)</c>.</summary>
        public static readonly SqlFunction IsBool = TypeTest("IS_BOOL");

        /// <summary><c>IS_NULL(&lt;expr&gt;)</c> — present, and holding JSON null.</summary>
        public static readonly SqlFunction IsNull = TypeTest("IS_NULL");

        /// <summary><c>IS_NUMBER(&lt;expr&gt;)</c>.</summary>
        public static readonly SqlFunction IsNumber = TypeTest("IS_NUMBER");

        /// <summary><c>IS_OBJECT(&lt;expr&gt;)</c>.</summary>
        public static readonly SqlFunction IsObject = TypeTest("IS_OBJECT");

        /// <summary><c>IS_PRIMITIVE(&lt;expr&gt;)</c> — a string, number, boolean or null.</summary>
        public static readonly SqlFunction IsPrimitive = TypeTest("IS_PRIMITIVE");

        /// <summary><c>IS_STRING(&lt;expr&gt;)</c>.</summary>
        public static readonly SqlFunction IsString = TypeTest("IS_STRING");

        /// <summary>
        /// <c>REGEXMATCH(&lt;string&gt;, &lt;pattern&gt; [, &lt;modifiers&gt;])</c>.
        /// </summary>
        /// <remarks>
        /// Under its own name rather than mapped from SQL's <c>REGEXP_LIKE</c>, and deliberately:
        /// regular expression dialects differ in ways a query cannot see — Cosmos documents PCRE
        /// with a stated list of unsupported constructs — so a caller writing this is asking for
        /// Cosmos's regular expressions rather than for SQL's. The <c>LIKE</c> measurement is the
        /// argument: two spellings that agree on most patterns and disagree on some are worse than
        /// two names.
        /// </remarks>
        public static readonly SqlFunction RegexMatch = Predicate("REGEXMATCH", 2, 3);

        /// <summary><c>ToString(&lt;expr&gt;)</c> — the JSON value rendered as a string.</summary>
        public static readonly SqlFunction ToStringFunction = Value("ToString", 1, 1);

        /// <summary><c>StringToNumber(&lt;string&gt;)</c>.</summary>
        public static readonly SqlFunction StringToNumber = Value("StringToNumber", 1, 1);

        /// <summary><c>StringToObject(&lt;string&gt;)</c>.</summary>
        public static readonly SqlFunction StringToObject = Value("StringToObject", 1, 1);

        /// <summary>
        /// <c>StringToArray(&lt;string&gt;)</c> — a JSON array parsed out of a string.
        /// </summary>
        /// <remarks>
        /// <b>Not</b> mapped from the library's <c>STRING_TO_ARRAY</c>, which is Postgres's and
        /// splits a string on a delimiter. Same name, unrelated function: this parses JSON text.
        /// </remarks>
        public static readonly SqlFunction StringToArray = Value("StringToArray", 1, 1);

        /// <summary><c>StringToBoolean(&lt;string&gt;)</c>.</summary>
        public static readonly SqlFunction StringToBoolean = Value("StringToBoolean", 1, 1);

        /// <summary><c>ObjectToArray(&lt;object&gt;)</c> — an object as an array of key/value pairs.</summary>
        public static readonly SqlFunction ObjectToArray = Value("ObjectToArray", 1, 1);

        /// <summary>
        /// <c>ST_DISTANCE(&lt;spatial_expr&gt;, &lt;spatial_expr&gt;)</c> — the distance between two
        /// geometries, <b>in metres</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Under its own name and not mapped from Calcite's spatial library, which spells the same four
        /// functions the same way and answers different numbers. Cosmos's are geodesic over GeoJSON and
        /// report metres; Calcite's are planar over JTS geometries and report the units of the
        /// coordinate system, so over longitude and latitude its answer is in degrees. The
        /// <c>REGEXMATCH</c> decision is the standing argument — two spellings that agree on most inputs
        /// and disagree on some are worse than two names — and here the two do not even agree on the
        /// unit.
        /// </para>
        /// <para>
        /// Because the names <em>do</em> collide, the translator dispatches these on operator identity
        /// rather than on the name it dispatches everything else by: a host that chains Calcite's
        /// spatial table gets Calcite's semantics, evaluated in process, rather than these silently
        /// rendered into a statement measuring something else.
        /// </para>
        /// <para>
        /// Typed as a non-nullable double, which is what makes <c>ORDER BY ST_DISTANCE(…)</c>
        /// expressible: a nullable sort key with a placement Cosmos will not honour is refused, and
        /// there would then be no ascending distance ordering at all. What the declaration costs is
        /// recorded in <c>DESIGN.md</c> under <em>Spatial</em> — a document holding no geometry sorts
        /// first ascending, which is Cosmos's placement and not Calcite's, and the proximity predicate
        /// that makes such a query worth pushing removes those documents anyway.
        /// </para>
        /// </remarks>
        public static readonly SqlFunction StDistance = Spatial("ST_DISTANCE", ReturnTypes.DOUBLE, 2);

        /// <summary>
        /// <c>ST_WITHIN(&lt;spatial_expr&gt;, &lt;spatial_expr&gt;)</c> — whether the first geometry is
        /// inside the second.
        /// </summary>
        public static readonly SqlFunction StWithin = Spatial("ST_WITHIN", ReturnTypes.BOOLEAN_NULLABLE, 2);

        /// <summary>
        /// <c>ST_INTERSECTS(&lt;spatial_expr&gt;, &lt;spatial_expr&gt;)</c> — whether the two geometries
        /// intersect.
        /// </summary>
        public static readonly SqlFunction StIntersects = Spatial("ST_INTERSECTS", ReturnTypes.BOOLEAN_NULLABLE, 2);

        /// <summary>
        /// <c>ST_ISVALID(&lt;spatial_expr&gt;)</c> — whether the value is a valid GeoJSON geometry.
        /// </summary>
        /// <remarks>
        /// Nullable, unlike the <c>IS_*</c> family beside it, because what the service answers for a
        /// property that is not there has not been measured. The type tests are documented as answering
        /// about absence and this one is documented as answering about a geometry, which is not the same
        /// question, so it is not assumed to share their answer.
        /// </remarks>
        public static readonly SqlFunction StIsValid = Spatial("ST_ISVALID", ReturnTypes.BOOLEAN_NULLABLE, 1);

        /// <summary>
        /// Gets an operator table carrying every Cosmos-specific function.
        /// </summary>
        public static SqlOperatorTable Instance { get; } = SqlOperatorTables.of(
            [
                FullTextContains, FullTextContainsAll, FullTextContainsAny,
                FullTextScore, Rrf, VectorDistance,
                IsDefined, IsArray, IsBool, IsNull, IsNumber, IsObject, IsPrimitive, IsString,
                RegexMatch,
                ToStringFunction, StringToNumber, StringToObject, StringToArray, StringToBoolean, ObjectToArray,
                StDistance, StWithin, StIntersects, StIsValid,
            ]);

        /// <summary>
        /// Determines whether an operator is one of the spatial functions defined here.
        /// </summary>
        /// <remarks>
        /// By identity, and that is the whole reason the question is asked this way. Calcite's spatial
        /// library defines <c>ST_Distance</c>, <c>ST_Within</c>, <c>ST_Intersects</c> and
        /// <c>ST_IsValid</c>, whose names differ from these only in case — which SQL does not
        /// distinguish — over a planar geometry model answering in different units. A name comparison
        /// would render one as the other.
        /// </remarks>
        /// <param name="op">The operator to classify.</param>
        /// <returns><c>true</c> where the operator is one of these.</returns>
        public static bool IsSpatial(SqlOperator? op)
        {
            if (op is null)
                return false;

            foreach (var candidate in new[] { StDistance, StWithin, StIntersects, StIsValid })
                if (ReferenceEquals(op, candidate))
                    return true;

            return false;
        }

        /// <summary>
        /// Determines whether an operator is the one <c>ORDER BY</c> may carry as its only key.
        /// </summary>
        /// <remarks>
        /// The service's documented exception to the rule that an <c>ORDER BY</c> item must map to a
        /// document path. Measured, <c>ORDER BY UPPER(c.name)</c> is rejected with
        /// <em>"ORDER BY item expression could not be mapped to a document path"</em> and a distance
        /// ordering is served — and a distance ordering paired with any second key is rejected with that
        /// same message. See <see cref="Rel.CosmosDistanceSort"/>.
        /// </remarks>
        /// <param name="op">The operator to classify.</param>
        /// <returns><c>true</c> where the operator is <c>ST_DISTANCE</c>.</returns>
        public static bool IsDistance(SqlOperator? op) => op is not null && ReferenceEquals(op, StDistance);

        /// <summary>
        /// Determines whether a function name is one of the spatial names, whoever defined the operator
        /// wearing it.
        /// </summary>
        /// <remarks>
        /// The counterpart to <see cref="IsSpatial"/>, and it exists so that the collision produces an
        /// explanation rather than a bare <em>unsupported function</em>. A call reaching the translator
        /// under one of these names but not one of these operators is Calcite's, and declining it is
        /// correct — Calcite then evaluates it in process, with the planar semantics the caller asked
        /// for by naming that operator.
        /// </remarks>
        /// <param name="name">The function name.</param>
        /// <returns><c>true</c> where the name is one of the four.</returns>
        public static bool IsSpatialName(string? name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            foreach (var candidate in new[] { StDistance, StWithin, StIntersects, StIsValid })
                if (string.Equals(name, candidate.getName(), StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        /// <summary>
        /// Determines whether an operator can tell an absent property from a present one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The <c>IS_*</c> family answers <em>about</em> a value, absence included, so each returns a
        /// real boolean where a comparison would return undefined. That distinction is what decides
        /// whether an expression implies the paths it reads are defined: measured against a real
        /// account, neither <c>c.price &gt; 5</c> nor <c>NOT (c.price &gt; 5)</c> matches a document
        /// without a <c>price</c>, while <c>NOT IS_DEFINED(c.price)</c> does.
        /// </para>
        /// <para>
        /// Only <c>IS_DEFINED</c> was measured. The rest of the family is treated the same way because
        /// they answer the same kind of question, and a predicate that uses one is rare enough that
        /// the caution costs nothing worth measuring for.
        /// </para>
        /// </remarks>
        /// <param name="op">The operator to classify.</param>
        /// <returns><c>true</c> where the operator observes absence.</returns>
        public static bool IsAbsenceObserving(SqlOperator? op)
        {
            if (op is null)
                return false;

            foreach (var candidate in new[] { IsDefined, IsArray, IsBool, IsNull, IsNumber, IsObject, IsPrimitive, IsString })
                if (ReferenceEquals(op, candidate))
                    return true;

            return false;
        }

        /// <summary>
        /// Defines a scoring function, whose value ranks a row rather than describing it.
        /// </summary>
        /// <remarks>
        /// Typed as a double so that a query can name it in an <c>ORDER BY</c> and the validator will
        /// accept the sort. Nothing may read the value — Cosmos will not project one — and the
        /// translator enforces that; the type is what makes the clause expressible, not a promise that
        /// a number comes back.
        /// </remarks>
        static SqlFunction Scoring(string name, int min)
        {
            return SqlBasicFunction.create(
                name,
                ReturnTypes.DOUBLE,
                OperandTypes.variadic(SqlOperandCountRanges.from(min)),
                SqlFunctionCategory.SYSTEM);
        }

        /// <summary>
        /// Defines a function returning a value rather than a boolean.
        /// </summary>
        /// <remarks>
        /// Typed <c>ANY</c>, which is what the row model types every document value: these return
        /// arrays, objects, strings and numbers depending on what they were given, and a container
        /// declares none of it. The arity is checked here so a mistyped call fails in the validator
        /// with the message a caller can act on rather than in the translator.
        /// </remarks>
        static SqlFunction Value(string name, int min, int max)
        {
            var range = max < 0
                ? SqlOperandCountRanges.from(min)
                : SqlOperandCountRanges.between(min, max);

            return SqlBasicFunction.create(
                name,
                ReturnTypes.@explicit(SqlTypeName.ANY),
                OperandTypes.variadic(range),
                SqlFunctionCategory.SYSTEM);
        }

        /// <summary>
        /// Defines a unary type test.
        /// </summary>
        /// <remarks>
        /// A container has no row schema, so what a property holds is a question about the document
        /// rather than about the table, and only the service can answer it. These are the functions that
        /// ask. Unlike the full text predicates the argument is an ordinary expression, not a path —
        /// <c>IS_NUMBER(c.a + c.b)</c> is meaningful — so nothing further is required of it.
        /// </remarks>
        static SqlFunction TypeTest(string name)
        {
            return SqlBasicFunction.create(
                name,
                ReturnTypes.BOOLEAN_NOT_NULL,
                OperandTypes.ANY,
                SqlFunctionCategory.SYSTEM);
        }

        /// <summary>
        /// Defines a spatial function of fixed arity.
        /// </summary>
        /// <remarks>
        /// The operand types are left open for the reason the full text predicates leave them open:
        /// every argument has to be a document path or a GeoJSON geometry literal, which is a question
        /// about the <em>expression</em> rather than about its type. The validator cannot ask it and the
        /// translator does.
        /// </remarks>
        static SqlFunction Spatial(string name, SqlReturnTypeInference returnType, int operands)
        {
            return SqlBasicFunction.create(
                name,
                returnType,
                OperandTypes.variadic(SqlOperandCountRanges.between(operands, operands)),
                SqlFunctionCategory.SYSTEM);
        }

        /// <summary>
        /// Defines a boolean full text predicate over a property path and one or more keywords.
        /// </summary>
        /// <remarks>
        /// The operand types are left open. What the first argument has to be is a path, which is a
        /// question about the <em>expression</em> and not about its type, so the validator cannot ask it
        /// and the translator does.
        /// </remarks>
        static SqlFunction Predicate(string name, int min, int max)
        {
            var range = max < 0 ? SqlOperandCountRanges.from(min) : SqlOperandCountRanges.between(min, max);

            return SqlBasicFunction.create(
                name,
                ReturnTypes.BOOLEAN_NULLABLE,
                OperandTypes.variadic(range),
                SqlFunctionCategory.SYSTEM);
        }

    }

}
