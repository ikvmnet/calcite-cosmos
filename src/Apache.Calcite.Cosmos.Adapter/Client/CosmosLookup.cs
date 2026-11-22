using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Extensions.Runtime;

using org.apache.calcite.runtime;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// Joins a sequence to a container by fetching only the documents its keys could match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A relational join is not expressible in Cosmos, so without this both sides are read whole and
    /// joined in process. Here one side pays for the other: a batch of build rows contributes its
    /// distinct keys, and the statement run against the container carries them, so the service returns
    /// the documents that could join rather than all of them.
    /// </para>
    /// <para>
    /// This is Flink's lookup join rather than anything invented here. The correlation variable Calcite
    /// uses to express the shape is consumed by the rule and never reaches this point; what arrives is
    /// key values, which is what <c>asyncLookup(RowData)</c> receives there.
    /// </para>
    /// <para>
    /// The statement is rendered once, with a fixed number of key parameters. Only their values change
    /// per batch — which is why the batch size is fixed and a short batch pads rather than re-renders.
    /// </para>
    /// </remarks>
    public static class CosmosLookup
    {

        /// <summary>
        /// Reduces a key to the form in which two keys are equal exactly where Calcite's <c>=</c> finds them equal,
        /// after Calcite's cast to the join's comparison type.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The two sides arrive by different routes — one from whatever the build side is, the other
        /// from JSON — so the same key can be an <see cref="int"/> on one side and a
        /// <see cref="long"/> or <see cref="double"/> on the other. Comparing the boxes directly would
        /// silently drop matching rows, which is the worst kind of wrong answer this could give.
        /// </para>
        /// <para>
        /// <b>So a number is compared as the join's comparison type compares it</b>, the type Calcite casts both keys to
        /// before its <c>=</c> (<c>Calcite.AnEquiJoinMatchesKeysAsEquality</c>): an integer type as a <see cref="long"/>,
        /// <c>DECIMAL</c> by its value, as a <see cref="java.math.BigDecimal"/> stripped of its trailing zeros so that
        /// <c>1.0</c> and <c>1.00</c> are one key, and an approximate type as a <see cref="double"/>, a <c>REAL</c> by way of
        /// the float Calcite holds it in. Each key reaches the type by Calcite's own conversion of a number (its cast to an
        /// integer type, <see cref="Integral"/>, which fails by name on a number outside the type's range as the cast does,
        /// and <c>SqlFunctions.toBigDecimal</c>, <c>toFloat</c> and <c>toDouble</c>), which is the identity for a key Calcite
        /// has already cast. Every number used to be compared as a double, so two <c>BIGINT</c> keys past 2^53 that one
        /// double holds joined each other's rows, where Calcite keeps them apart.
        /// </para>
        /// <para>
        /// <b>Calcite's own join never matches <c>-0.0</c> with <c>0.0</c>, and this follows <c>=</c>, which does.</b> Its
        /// equi-join in process, a hash join or a merge join, leaves a <c>REAL</c>, <c>FLOAT</c> or <c>DOUBLE</c>
        /// <c>-0.0</c> unmatched against <c>0.0</c>, where the comparison join and <c>=</c> itself match them. A
        /// <see cref="double"/> <c>-0.0</c> equals <c>0.0</c> here as it does under <c>=</c>, hashing alike, and is held as
        /// <c>0.0</c>, so that a batch never sends the minus zero no row says the service compares with a stored <c>0</c>;
        /// <c>TODO.md</c> records the departure.
        /// </para>
        /// <para>
        /// <b>Java's boxes as well as the CLR's, and the Java ones are the ordinary case.</b> A
        /// container row is read into the boxes Calcite holds values in — <see cref="CosmosJson"/>
        /// reads an <c>INTEGER</c> as a <see cref="java.lang.Integer"/> — and a build side computed
        /// by Calcite carries them too. Passed through untouched, a <see cref="java.lang.Integer"/>
        /// equalled only another of the same width, so <c>7</c> read as <c>INTEGER</c> missed
        /// <c>7</c> read as <c>BIGINT</c>; and the normalised key is also what <see cref="Bind"/>
        /// binds, where the SDK serialised the box as an object rather than a number and the service
        /// matched nothing. Measured on the emulator, <c>c.price IN (@k0)</c> bound to a CLR
        /// <c>120</c> found the bike and bound to a <see cref="java.lang.Integer"/> 120 found nothing,
        /// and a lookup on a numeric key, a <c>_ts</c> and a boolean returned no rows where the
        /// in-process join returned them (review probes I4, I12 and I13). Unboxed here, a key is a
        /// CLR <see cref="long"/>, <see cref="double"/>, <see cref="bool"/> or <see cref="string"/>, or a
        /// <see cref="java.math.BigDecimal"/>, on both sides, and <see cref="Bind"/> sends each as
        /// <c>CosmosQueries</c> sends a statement's parameter of its type.
        /// </para>
        /// <para>
        /// A <c>UUID</c> is compared as a <see cref="Guid"/>. Calcite boxes one as
        /// <c>UuidValue</c>, and a host may hand one over as a <c>java.util.UUID</c>; either is the same
        /// value, and a <see cref="Guid"/> is what <see cref="Metadata.CosmosUuidForms.Render"/> spells in
        /// the container's own form. Types outside these never arrive, because the rule declines any other
        /// key.
        /// </para>
        /// </remarks>
        /// <param name="key">The key as either side produced it.</param>
        /// <param name="comparison">
        /// The type the join compares its keys in (<c>CosmosLookupJoinRule.ComparisonType</c>), or <c>null</c> where none
        /// is given, as <c>CosmosQueries</c> reads a <c>UUID</c> parameter by this.
        /// </param>
        /// <returns>The comparable form, or <c>null</c> where the key is absent.</returns>
        /// <exception cref="CosmosExecutionException">Calcite's cast of the key to an integer comparison type fails.</exception>
        public static object? Normalize(object? key, SqlTypeName? comparison = null)
        {
            return key switch
            {
                null => null,
                string or bool or Guid => key,
                java.lang.Boolean flag => flag.booleanValue(),
                org.apache.calcite.util.UuidValue uuid => Guid.Parse(uuid.toString()),
                java.util.UUID uuid => Guid.Parse(uuid.toString()),
                _ when Boxed(key) is java.lang.Number number => comparison?.getName() switch
                {
                    "TINYINT" => Integral(number, org.apache.calcite.linq4j.tree.Primitive.BYTE, comparison!),
                    "SMALLINT" => Integral(number, org.apache.calcite.linq4j.tree.Primitive.SHORT, comparison!),
                    "INTEGER" => Integral(number, org.apache.calcite.linq4j.tree.Primitive.INT, comparison!),
                    "BIGINT" => Integral(number, org.apache.calcite.linq4j.tree.Primitive.LONG, comparison!),
                    "DECIMAL" => SqlFunctions.toBigDecimal(number).stripTrailingZeros(),
                    // Plus zero, which = cannot tell from minus zero, so that a batch sends the 0 the service reads back.
                    "FLOAT" or "DOUBLE" => SqlFunctions.toDouble(number) + 0d,
                    "REAL" => (double)SqlFunctions.toFloat(number) + 0d,

                    // No number arrives under any other type: the rule passes the type Calcite cast both keys to. Under
                    // none, a number read for a UUID parameter passes through, and is refused there by name.
                    _ => key,
                },
                _ => key,
            };
        }

        /// <summary>
        /// Returns a number as Calcite's cast to an integer type makes it, held as a <see cref="long"/>, and fails by name
        /// where that cast fails.
        /// </summary>
        /// <remarks>
        /// <c>Primitive.integerCast</c> is what Calcite's generated code calls for a cast of a number to an integer type,
        /// here with Calcite's default rounding, toward zero, and <paramref name="primitive"/> is the Java type Calcite holds
        /// a value of the type in. It raises on a number outside the type's range, where <c>SqlFunctions.toLong</c> wrapped
        /// one, so that a key 2^64 + 7 under <c>BIGINT</c> was compared as 7.
        /// </remarks>
        /// <param name="number">The key in its Java box.</param>
        /// <param name="primitive">The Java type a value of <paramref name="type"/> is held in.</param>
        /// <param name="type">The integer type the join compares in, which the failure names.</param>
        /// <returns>The value Calcite's cast gives.</returns>
        /// <exception cref="CosmosExecutionException">Calcite's cast of the number to the type fails.</exception>
        static long Integral(java.lang.Number number, org.apache.calcite.linq4j.tree.Primitive primitive, SqlTypeName type)
        {
            try
            {
                return ((java.lang.Number)org.apache.calcite.linq4j.tree.Primitive.integerCast(primitive, number)).longValue();
            }
            catch (java.lang.RuntimeException e)
            {
                throw new CosmosExecutionException($"A lookup key {number} cannot be cast to {type.getName()}, the type the join compares its keys in: {e.getMessage()}", e);
            }
        }

        /// <summary>
        /// Returns a number in the Java box Calcite holds a value of its type in, so that one conversion of Calcite's
        /// reaches every key; or <c>null</c> for anything but a number.
        /// </summary>
        /// <remarks>
        /// A CLR <see cref="byte"/> is Calcite's <c>TINYINT</c>, as <see cref="CosmosJson"/> reads one: a Java byte is
        /// signed and IKVM surfaces it as a CLR byte, so a small side Calcite computes hands <c>-1</c> over as <c>0xFF</c>,
        /// which is boxed as the Java byte it is rather than read as 255.
        /// </remarks>
        static java.lang.Number? Boxed(object key) => key switch
        {
            java.lang.Number number => number,
            sbyte value => java.lang.Byte.valueOf(unchecked((byte)value)),
            short value => java.lang.Short.valueOf(value),
            int value => java.lang.Integer.valueOf(value),
            long value => java.lang.Long.valueOf(value),
            byte value => java.lang.Byte.valueOf(value),
            ushort value => java.lang.Integer.valueOf(value),
            uint value => java.lang.Long.valueOf(value),
            ulong value => new java.math.BigDecimal(value.ToString(CultureInfo.InvariantCulture)),
            float value => java.lang.Float.valueOf(value),
            double value => java.lang.Double.valueOf(value),
            decimal value => new java.math.BigDecimal(value.ToString(CultureInfo.InvariantCulture)),
            _ => null,
        };

        /// <summary>
        /// Returns the value a normalised key is bound as: a <c>DECIMAL</c> key as <c>CosmosQueries</c> sends a decimal
        /// parameter, and every other key as it is; or <c>null</c> for a key with no number to be sent as.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A decimal reaches the service as a JSON number, so it is sent as the value the service compares as that number
        /// (<see cref="CosmosRexLiterals.TryGetExactNumberValue"/>): a decimal where its double is right, a
        /// <see cref="long"/> where the double would be another whole number, which an <c>int64</c> the service holds is
        /// compared with by value (<c>Service.NumbersBeyondLongCompareAsDoubles</c>). This repeats how <c>CosmosQueries</c>
        /// sends a decimal parameter, a bridge <c>TODO.md</c> records until a statement's parameter is converted by its SQL
        /// type, when this should call that conversion and keep only the double below.
        /// </para>
        /// <para>
        /// <b>Where that answers none, or a decimal that is not the key, the key is sent as its double</b>: a whole number
        /// beyond an <c>int64</c>, a fraction beyond a <see cref="decimal"/>, or one past its 28 places, which the decimal
        /// rounds (<c>1E-30</c> to 0). The service holds a number written so as its correctly rounded double, which a key
        /// equal to it rounds to as well. The restriction has only to keep every document the key could match, since the
        /// join compares every fetched key itself, so a document another key rounds alike to is fetched and left
        /// unmatched.
        /// </para>
        /// <para>
        /// <b>A key whose double is an infinity or NaN has no number to be sent as</b>: JSON holds no such number, and the
        /// SDK writes one as the string <c>"Infinity"</c>, <c>"-Infinity"</c> or <c>"NaN"</c>, which no stored number
        /// equals. That is an approximate key that is not finite, and a decimal beyond a double's range. The restriction
        /// cannot name it, so the batch holding it is fetched without the restriction and the join's own comparison of
        /// every document the probe keeps answers it, which rests on nothing about what a document may hold. That costs
        /// request units, for a batch holding such a key.
        /// </para>
        /// </remarks>
        /// <param name="key">The key as <see cref="Normalize"/> made it, and as any spelling wrote it.</param>
        /// <returns>The value bound, or <c>null</c> where the key has none.</returns>
        internal static object? Sent(object? key)
        {
            var sent = key switch
            {
                java.math.BigDecimal value when CosmosRexLiterals.TryGetExactNumberValue(value) is { } exact && (exact is not decimal held || new java.math.BigDecimal(held.ToString(CultureInfo.InvariantCulture)).compareTo(value) == 0) => exact,
                java.math.BigDecimal value => value.doubleValue(),
                _ => key,
            };

            return sent is double number && double.IsFinite(number) == false ? null : sent;
        }

        /// <summary>
        /// Returns the function writing a <c>UUID</c> key in the spelling a container stores it in.
        /// </summary>
        /// <remarks>
        /// A key that has no spelling in the form — one a confined form excludes — names no document, and
        /// answers <c>null</c>, which the join fetches nothing for.
        /// </remarks>
        /// <param name="representation">The form the container's facts give the key's path.</param>
        /// <returns>The spelling function.</returns>
        public static Func<object, object?> SpellUuid(Metadata.CosmosRepresentation representation)
        {
            return key => key is Guid value ? Metadata.CosmosUuidForms.Render(representation, value) : null;
        }

        /// <summary>
        /// Returns the function writing a key in the stored spelling of a target that has one, the emulator's
        /// (<see cref="Facts.EmulatorStoredText"/>), after the spelling it is given: a string key holding U+0000 or U+FDD0
        /// is compared with the path as the service stores it, measured exact for <c>IN</c>
        /// (<c>Service.EmulatorComparesTheSpellingItIsSent</c>), and any other key is its own spelling.
        /// </summary>
        /// <param name="spell">The spelling the key is written in first, or <c>null</c> for the key itself.</param>
        /// <returns>The spelling function.</returns>
        public static Func<object, object?> SpellStored(Func<object, object?>? spell)
        {
            return key => (spell is null ? key : spell(key)) switch
            {
                string text => Facts.EmulatorStoredText.Of(text),
                var other => other,
            };
        }

        /// <summary>
        /// Builds the statement for one batch by binding its keys.
        /// </summary>
        /// <remarks>
        /// Padded to the number of parameters the statement was rendered with, by repeating a key it
        /// already carries. <c>k IN (a, b, b, b)</c> selects what <c>k IN (a, b)</c> selects, so the
        /// padding costs a longer statement and changes no answer — where re-rendering per batch would
        /// mean a statement that varies with the data. Each key is bound as a parameter of the join's comparison type,
        /// which <see cref="Normalize"/> made it, written as <see cref="Sent"/> writes it.
        /// </remarks>
        /// <param name="query">The statement, rendered with <paramref name="parameterCount"/> key parameters.</param>
        /// <param name="prefix">The key parameters' name prefix.</param>
        /// <param name="parameterCount">How many key parameters the statement carries.</param>
        /// <param name="keys">The batch's distinct keys as they are sent, at most <paramref name="parameterCount"/> of them.</param>
        /// <returns>The statement with every parameter bound.</returns>
        public static CosmosQuery Bind(CosmosQuery query, string prefix, int parameterCount, IReadOnlyList<object?> keys)
        {
            if (keys.Count == 0 || keys.Count > parameterCount)
                throw new ArgumentException($"A batch carries between 1 and {parameterCount} keys; {keys.Count} were supplied.", nameof(keys));

            var parameters = new List<CosmosParameter>(query.Parameters.Count + parameterCount);
            parameters.AddRange(query.Parameters);

            for (var i = 0; i < parameterCount; i++)
                parameters.Add(new CosmosParameter(prefix + i.ToString(CultureInfo.InvariantCulture), keys[i < keys.Count ? i : keys.Count - 1]));

            return query with { Parameters = parameters };
        }

        /// <summary>
        /// Joins <paramref name="build"/> to a container on one equality, fetching per batch.
        /// </summary>
        /// <remarks>
        /// <para>
        /// An inner join, or a left join where <paramref name="unmatched"/> is given. Anything else
        /// declines at the rule and is joined in process as before, which is what every other adapter
        /// does with every join.
        /// </para>
        /// <para>
        /// Rows are emitted in build order within a batch. The join is unordered, so this is not a
        /// promise — it is what falls out of probing in the order the batch was read, and keeping it
        /// costs nothing.
        /// </para>
        /// <para>
        /// <b>Nothing is acquired at the open.</b> A batch is read and fetched by the advance that runs
        /// out of rows, under that advance's token, so a join whose reader stops early never asks for the
        /// batches it did not reach. That makes this the open for both bodies: the synchronous one hands
        /// it an input opened synchronously, and <see cref="JoinAsync{TBuild, TProbe, TResult}"/> awaits
        /// the input's open and then calls this. A synchronous advance reads the build side with
        /// <c>Read</c> and blocks only for the fetch.
        /// </para>
        /// </remarks>
        /// <typeparam name="TBuild">The build side's row type.</typeparam>
        /// <typeparam name="TProbe">The container's row type.</typeparam>
        /// <typeparam name="TResult">The joined row type.</typeparam>
        /// <param name="build">The side whose keys are pushed down, already opened; the join owns it.</param>
        /// <param name="executor">Executes the statement.</param>
        /// <param name="root">The context the statement is executing against, whose values close the slots a prepared statement left open.</param>
        /// <param name="query">The statement, rendered with <paramref name="batchSize"/> key parameters.</param>
        /// <param name="prefix">The key parameters' name prefix.</param>
        /// <param name="batchSize">How many build rows one fetch serves, and how many key parameters the statement carries.</param>
        /// <param name="buildKey">Reads the join key from a build row.</param>
        /// <param name="rowBuilder">Builds a container row from the JSON value it arrived as.</param>
        /// <param name="probeKey">Reads the join key from a container row.</param>
        /// <param name="resultSelector">Combines a matching pair.</param>
        /// <param name="comparison">
        /// The type the join compares its keys in, after Calcite's cast (<c>CosmosLookupJoinRule.ComparisonType</c>), which
        /// every key on both sides is normalised by (<see cref="Normalize"/>).
        /// </param>
        /// <param name="cacheSize">
        /// How many keys to remember for the length of this join, or zero to remember none. Reference
        /// data is looked up repeatedly by definition, and a remembered key costs no request units at
        /// all.
        /// </param>
        /// <param name="shared">
        /// The container's cache across executions, or <c>null</c> where none is configured. Consulted
        /// beneath the per-join cache and populated by every fetch; see <c>DESIGN.md</c> under
        /// <em>The lookup join's caches</em>.
        /// </param>
        /// <param name="unmatched">
        /// Builds the joined row for a build row nothing matched, or <c>null</c> for an inner join, which
        /// drops it. A left join's: the build row's fields and a null for each of the container's.
        /// </param>
        /// <param name="spell">
        /// Writes a normalised key as the value its parameter is bound to, or <c>null</c> where the key is
        /// bound as it is. A key spelled as <c>null</c> names no document and is not fetched.
        /// </param>
        /// <param name="unrestricted">
        /// The statement without the key restriction, which a batch holding a key with no number to be sent as
        /// (<see cref="Sent"/>) is fetched by; the planned join always gives it. A batch holding such a key fails by name
        /// where none is given.
        /// </param>
        /// <returns>The joined rows.</returns>
        /// <exception cref="ArgumentNullException">Any required argument is <c>null</c>.</exception>
        public static IClrCursor<TResult> Join<TBuild, TProbe, TResult>(
            IClrCursor<TBuild> build,
            ICosmosQueryExecutor executor,
            org.apache.calcite.DataContext root,
            CosmosQuery query,
            string prefix,
            int batchSize,
            Func<TBuild, object?> buildKey,
            Func<JsonElement, TProbe> rowBuilder,
            Func<TProbe, object?> probeKey,
            Func<TBuild, TProbe, TResult> resultSelector,
            SqlTypeName comparison,
            int cacheSize = 0,
            CosmosLookupCache? shared = null,
            Func<TBuild, TResult>? unmatched = null,
            Func<object, object?>? spell = null,
            CosmosQuery? unrestricted = null)
        {
            if (build is null)
                throw new ArgumentNullException(nameof(build));
            if (executor is null)
                throw new ArgumentNullException(nameof(executor));
            if (buildKey is null)
                throw new ArgumentNullException(nameof(buildKey));
            if (rowBuilder is null)
                throw new ArgumentNullException(nameof(rowBuilder));
            if (probeKey is null)
                throw new ArgumentNullException(nameof(probeKey));
            if (resultSelector is null)
                throw new ArgumentNullException(nameof(resultSelector));
            if (comparison is null)
                throw new ArgumentNullException(nameof(comparison));
            if (batchSize < 1)
                throw new ArgumentOutOfRangeException(nameof(batchSize));

            // Closed once, here, before any batch appends its keys: the values a prepared statement
            // left open belong to the execution, and every batch of it carries the same ones. Left
            // open, a slot reached the service as the slot itself, which a comparison is never true
            // against (#219) — and the shared cache, which names a statement by its values, would
            // have answered one execution with what another fetched. A probe sent nothing binds
            // nothing (CosmosQuery.SendsNothing).
            if (query.SendsNothing == false)
                query = CosmosQueries.Bind(query, root);

            if (unrestricted is { SendsNothing: false } open)
                unrestricted = CosmosQueries.Bind(open, root);

            return new LookupCursor<TBuild, TProbe, TResult>(build, executor, query, prefix, batchSize, buildKey, rowBuilder, probeKey, resultSelector, comparison, cacheSize, shared, unmatched, spell, unrestricted);
        }

        /// <summary>
        /// Awaits the build side's open, and joins it as <see cref="Join{TBuild, TProbe, TResult}"/> does.
        /// </summary>
        /// <remarks>
        /// The awaiting body's open. The token is the open's, and nothing here uses it: the join acquires
        /// nothing at its open, and each fetch runs under the token of the advance that needs it.
        /// </remarks>
        public static async ValueTask<IClrCursor<TResult>> JoinAsync<TBuild, TProbe, TResult>(
            ValueTask<IClrCursor<TBuild>> build,
            ICosmosQueryExecutor executor,
            org.apache.calcite.DataContext root,
            CosmosQuery query,
            string prefix,
            int batchSize,
            Func<TBuild, object?> buildKey,
            Func<JsonElement, TProbe> rowBuilder,
            Func<TProbe, object?> probeKey,
            Func<TBuild, TProbe, TResult> resultSelector,
            SqlTypeName comparison,
            int cacheSize,
            CosmosLookupCache? shared,
            Func<TBuild, TResult>? unmatched,
            Func<object, object?>? spell,
            CosmosQuery? unrestricted,
            CancellationToken cancellationToken)
        {
            return Join(await build.ConfigureAwait(false), executor, root, query, prefix, batchSize, buildKey, rowBuilder, probeKey, resultSelector, comparison, cacheSize, shared, unmatched, spell, unrestricted);
        }

        /// <summary>
        /// The cursor of <see cref="Join{TBuild, TProbe, TResult}"/>: a batch of build rows at a time,
        /// fetched once and then paired up row by row.
        /// </summary>
        sealed class LookupCursor<TBuild, TProbe, TResult> : ClrCursor<TResult>
        {

            readonly IClrCursor<TBuild> _build;
            readonly ICosmosQueryExecutor _executor;
            readonly CosmosQuery _query;
            readonly string _prefix;
            readonly int _batchSize;
            readonly Func<TBuild, object?> _buildKey;
            readonly Func<JsonElement, TProbe> _rowBuilder;
            readonly Func<TProbe, object?> _probeKey;
            readonly Func<TBuild, TProbe, TResult> _resultSelector;
            readonly SqlTypeName _comparison;
            readonly int _cacheSize;
            readonly CosmosLookupCache? _shared;
            readonly Func<TBuild, TResult>? _unmatched;
            readonly Func<object, object?>? _spell;
            readonly CosmosQuery? _unrestricted;

            // Held for the length of this join and no longer. A cache that outlived one execution
            // would have to answer for staleness, and this one cannot be stale in a way the join was
            // not already: the container is read across many requests either way, and remembering an
            // answer makes the result more self-consistent rather than less.
            readonly Dictionary<object, List<TProbe>>? _cache;

            // The identity the shared cache remembers this statement's answers under, computed once:
            // the batches differ only in their keys.
            readonly string? _statement;

            readonly List<TBuild> _batch;

            // What the current batch resolves to, whether remembered or fetched.
            readonly Dictionary<object, List<TProbe>> _lookup = new();

            // Where the pairing of the current batch has got to: the build row, and the next of its
            // matches.
            int _row;
            List<TProbe>? _matches;
            int _match;

            bool _buildDone;
            TResult _current = default!;

            /// <summary>
            /// Initializes a new instance.
            /// </summary>
            public LookupCursor(
                IClrCursor<TBuild> build,
                ICosmosQueryExecutor executor,
                CosmosQuery query,
                string prefix,
                int batchSize,
                Func<TBuild, object?> buildKey,
                Func<JsonElement, TProbe> rowBuilder,
                Func<TProbe, object?> probeKey,
                Func<TBuild, TProbe, TResult> resultSelector,
                SqlTypeName comparison,
                int cacheSize,
                CosmosLookupCache? shared,
                Func<TBuild, TResult>? unmatched,
                Func<object, object?>? spell,
                CosmosQuery? unrestricted)
            {
                _build = build;
                _executor = executor;
                _query = query;
                _prefix = prefix;
                _batchSize = batchSize;
                _buildKey = buildKey;
                _rowBuilder = rowBuilder;
                _probeKey = probeKey;
                _resultSelector = resultSelector;
                _comparison = comparison;
                _cacheSize = cacheSize;
                _shared = shared;
                _unmatched = unmatched;
                _spell = spell;
                _unrestricted = unrestricted;
                _cache = cacheSize > 0 ? new Dictionary<object, List<TProbe>>() : null;
                _statement = shared is null ? null : CosmosLookupCache.Statement(query);
                _batch = new List<TBuild>(batchSize);
            }

            /// <inheritdoc />
            public override TResult Current => _current;

            /// <inheritdoc />
            public override bool Read()
            {
                while (true)
                {
                    if (TryPair())
                        return true;

                    if (_buildDone)
                        return false;

                    _batch.Clear();
                    while (_batch.Count < _batchSize && _build.Read())
                        _batch.Add(_build.Current);

                    if (Filled() == false)
                        return false;

                    CosmosCursors.Wait(FetchAsync);
                }
            }

            /// <inheritdoc />
            public override async ValueTask<bool> ReadAsync(CancellationToken cancellationToken)
            {
                while (true)
                {
                    if (TryPair())
                        return true;

                    if (_buildDone)
                        return false;

                    _batch.Clear();
                    while (_batch.Count < _batchSize && await _build.ReadAsync(cancellationToken).ConfigureAwait(false))
                        _batch.Add(_build.Current);

                    if (Filled() == false)
                        return false;

                    await FetchAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            /// <summary>
            /// Notes whether the build side ran out while the batch was being read, and answers whether
            /// there is a batch to fetch.
            /// </summary>
            bool Filled()
            {
                if (_batch.Count < _batchSize)
                    _buildDone = true;

                return _batch.Count > 0;
            }

            /// <summary>
            /// Moves to the next matching pair of the current batch, if it has one.
            /// </summary>
            bool TryPair()
            {
                while (true)
                {
                    if (_matches is not null && _match < _matches.Count)
                    {
                        _current = _resultSelector(_batch[_row], _matches[_match++]);
                        return true;
                    }

                    if (_matches is not null)
                    {
                        _matches = null;
                        _row++;
                    }

                    if (_row >= _batch.Count)
                        return false;

                    if (Normalize(_buildKey(_batch[_row]), _comparison) is object key && _lookup.TryGetValue(key, out var matches) && matches.Count > 0)
                    {
                        _matches = matches;
                        _match = 0;
                    }
                    else if (_unmatched is not null)
                    {
                        // A left join keeps the row, a null key and a key the container has nothing
                        // for alike: null = x is never true, so neither pairs with anything.
                        _current = _unmatched(_batch[_row++]);
                        return true;
                    }
                    else
                    {
                        _row++;
                    }
                }
            }

            /// <summary>
            /// Resolves the current batch's keys, fetching those nothing remembers.
            /// </summary>
            /// <remarks>
            /// A build row whose key is null matches nothing — <c>null = x</c> is never true — so such rows
            /// contribute no key: an inner join drops them and a left join keeps them unmatched. A batch of
            /// only those fetches nothing at all, which is the whole point of doing this.
            /// </remarks>
            async ValueTask<bool> FetchAsync(CancellationToken cancellationToken)
            {
                _lookup.Clear();
                _row = 0;
                _matches = null;
                _match = 0;

                // Distinct, because the keys are data here rather than a predicate: a hundred build rows
                // over ten keys fetch ten. This is what the statement could not have done for itself.
                var keys = new List<object?>();
                var spelled = new List<object?>();
                var seen = new HashSet<object>();
                var unnamed = false;

                foreach (var row in _batch)
                {
                    if (Normalize(_buildKey(row), _comparison) is not object key || seen.Add(key) == false)
                        continue;

                    if (_cache is not null && _cache.TryGetValue(key, out var remembered))
                    {
                        _lookup[key] = remembered;
                        continue;
                    }

                    // Beneath the per-join cache: an answer another execution fetched, rebuilt through
                    // this plan's own row builder.
                    if (_shared is not null && _shared.TryGet(_statement!, key, out var held))
                    {
                        var rows = new List<TProbe>(held.Count);
                        foreach (var element in held)
                            rows.Add(_rowBuilder(element));

                        _lookup[key] = rows;

                        if (_cache is not null && _cache.Count < _cacheSize)
                            _cache[key] = rows;

                        continue;
                    }

                    // A key with no spelling in the container's form names no document there: it is answered as absent
                    // without asking.
                    var written = _spell is null ? key : _spell(key);
                    if (written is null)
                    {
                        _lookup[key] = new List<TProbe>();
                        continue;
                    }

                    keys.Add(key);

                    // A key with no number to be sent as cannot be named by the restriction, so the batch is fetched
                    // without it.
                    if (Sent(written) is { } value)
                        spelled.Add(value);
                    else
                        unnamed = true;
                }

                // A probe whose WHERE keeps no document the declaration admits and fails on none answers every key as
                // absent, as the service would, without asking; the build side is still read, every batch of it.
                if (_query.SendsNothing)
                    foreach (var key in keys)
                        _lookup[key!] = new List<TProbe>();

                if (keys.Count == 0 || _query.SendsNothing)
                    return true;

                var fetched = new Dictionary<object, List<TProbe>>();

                // What crossed the wire for each key, kept only where a shared cache will remember
                // it. The elements are already standalone: the executor clones what it yields.
                var elements = _shared is null ? null : new Dictionary<object, List<JsonElement>>();

                // Every document the probe keeps, where the restriction cannot name a key of the batch; the join's own
                // comparison below then answers every key of it.
                var statement = unnamed
                    ? _unrestricted ?? throw new CosmosExecutionException("A lookup key has no number it can be sent as, and the join was given no statement to fetch its batch without the key restriction.")
                    : Bind(_query, _prefix, _batchSize, spelled);

                var cursor = await _executor.OpenAsync(statement, cancellationToken: cancellationToken).ConfigureAwait(false);

                await using (cursor.ConfigureAwait(false))
                {
                    while (await cursor.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var element = cursor.Current;
                        var probe = _rowBuilder(element);

                        // A row no key of the batch asked for is left where it is, which keeps a fetch without the
                        // restriction from holding every document it reads.
                        if (Normalize(_probeKey(probe), _comparison) is not object key || seen.Contains(key) == false)
                            continue;

                        if (fetched.TryGetValue(key, out var rows) == false)
                            fetched[key] = rows = new List<TProbe>();

                        rows.Add(probe);

                        if (elements is not null)
                        {
                            if (elements.TryGetValue(key, out var held) == false)
                                elements[key] = held = new List<JsonElement>();

                            held.Add(element);
                        }
                    }
                }

                foreach (var key in keys)
                {
                    if (key is null)
                        continue;

                    // Absence is remembered too. A key the container has nothing for is the case a
                    // cache most needs to hold: without it, every batch mentioning that key asks
                    // again and is told nothing again.
                    var rows = fetched.TryGetValue(key, out var found) ? found : new List<TProbe>();

                    _lookup[key] = rows;

                    // Filled to the bound and then left alone, rather than evicted. Nothing here knows
                    // which key is worth keeping, and a wrong eviction costs a request — so the simple
                    // rule is the honest one, and the bound is what stops a large build side from
                    // being remembered whole.
                    if (_cache is not null && _cache.Count < _cacheSize)
                        _cache[key] = rows;

                    if (_shared is not null)
                        _shared.Set(_statement!, key, elements!.TryGetValue(key, out var held) ? held : Array.Empty<JsonElement>());
                }

                return true;
            }

            /// <inheritdoc />
            public override void Dispose()
            {
                _build.Dispose();
            }

            /// <inheritdoc />
            public override ValueTask DisposeAsync()
            {
                return _build.DisposeAsync();
            }

        }

    }

}
