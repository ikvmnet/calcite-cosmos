using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;
using Apache.Calcite.Extensions.Runtime;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using org.apache.calcite.sql.type;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Client
{

    /// <summary>
    /// Covers the lookup join's runtime: what it fetches, and what it pairs up.
    /// </summary>
    /// <remarks>
    /// No service and no planner. What is being checked is that a batch of build rows becomes the right
    /// statement and the right rows — which is where this feature can be wrong in ways that look like a
    /// correct answer.
    /// </remarks>
    public class CosmosLookupTests
    {

        /// <summary>
        /// Answers with documents and records every statement it was given.
        /// </summary>
        sealed class RecordingExecutor : ICosmosQueryExecutor
        {

            readonly Func<CosmosQuery, IEnumerable<string>> _documents;

            public RecordingExecutor(Func<CosmosQuery, IEnumerable<string>> documents)
            {
                _documents = documents;
            }

            public RecordingExecutor(params string[] documents) :
                this(_ => documents)
            {

            }

            public List<CosmosQuery> Executed { get; } = new();

            /// <summary>
            /// The token each fetch was opened under, in order.
            /// </summary>
            public List<CancellationToken> Tokens { get; } = new();

            /// <summary>
            /// The cursor of each fetch, in order.
            /// </summary>
            public List<ListCursor<JsonElement>> Cursors { get; } = new();

            public async ValueTask<IClrCursor<JsonElement>> OpenAsync(CosmosQuery query, PartitionKey? partitionKey = null, CancellationToken cancellationToken = default)
            {
                Executed.Add(query);
                Tokens.Add(cancellationToken);

                await Task.Yield();

                var cursor = ListCursor.Documents(_documents(query));
                Cursors.Add(cursor);
                return cursor;
            }

        }

        static CosmosQuery Query() => new("SELECT VALUE c FROM products c WHERE c.category IN (@k0, @k1, @k2)", Array.Empty<CosmosParameter>());

        /// <summary>The keys a statement was given, in order.</summary>
        static object?[] Keys(CosmosQuery query) => query.Parameters.Where(p => p.Name.StartsWith("@k")).Select(p => p.Value).ToArray();

        static ListCursor<T> Async<T>(params T[] items) => new(items);

        static Task<List<string>> Join(IClrCursor<string> build, RecordingExecutor executor, int batchSize = 3, CosmosQuery? query = null, int cacheSize = 0, CosmosLookupCache? shared = null, org.apache.calcite.DataContext? root = null)
        {
            return Collect(CosmosLookup.Join(
                build,
                executor,
                root!,
                query ?? Query(),
                "@k",
                batchSize,
                b => b,
                element => element.GetProperty("category").GetString()!,
                p => p,
                (b, p) => b + "/" + p,
                SqlTypeName.VARCHAR,
                cacheSize,
                shared));
        }

        static Task<List<string>> Collect(IClrCursor<string> rows) => ListCursor.CollectAsync(rows);

        static IClrCursor<string> Open(IClrCursor<string> build, RecordingExecutor executor, int batchSize = 3) =>
            CosmosLookup.Join(
                build,
                executor,
                null!,
                Query(),
                "@k",
                batchSize,
                b => b,
                element => element.GetProperty("category").GetString()!,
                p => p,
                (b, p) => b + "/" + p,
                SqlTypeName.VARCHAR);

        // ── What it fetches ───────────────────────────────────────────────────────

        [Fact]
        public async Task OnlyTheBatchesKeysAreFetched()
        {
            var executor = new RecordingExecutor("""{"category":"bikes"}""");

            await Join(Async("bikes"), executor);

            executor.Executed.Should().ContainSingle();
            Keys(executor.Executed[0]).Should().Equal("bikes", "bikes", "bikes");
        }

        /// <remarks>
        /// The reason the keys are carried as data rather than rendered into a predicate. A hundred
        /// build rows over two keys is two keys, and the statement is the same length either way.
        /// </remarks>
        [Fact]
        public async Task RepeatedKeysAreFetchedOnce()
        {
            var executor = new RecordingExecutor();

            await Join(Async("bikes", "bikes", "shoes"), executor);

            executor.Executed.Should().ContainSingle();
            Keys(executor.Executed[0]).Distinct().Should().BeEquivalentTo(new[] { "bikes", "shoes" });
        }

        /// <remarks>
        /// The statement carries a fixed number of key parameters because it is rendered once. A short
        /// batch therefore repeats a key it already has, which selects the same documents.
        /// </remarks>
        [Fact]
        public async Task AShortBatchPadsWithAKeyItAlreadyCarries()
        {
            var executor = new RecordingExecutor();

            await Join(Async("bikes"), executor);

            var keys = Keys(executor.Executed[0]);
            keys.Should().HaveCount(3);
            keys.Distinct().Should().Equal("bikes");
        }

        [Fact]
        public async Task RowsBeyondTheBatchSizeFetchAgain()
        {
            var executor = new RecordingExecutor();

            await Join(Async("a", "b", "c", "d"), executor, batchSize: 3);

            executor.Executed.Should().HaveCount(2);
            Keys(executor.Executed[0]).Should().Equal("a", "b", "c");
            Keys(executor.Executed[1]).Should().Equal("d", "d", "d");
        }

        /// <remarks>
        /// A null key joins to nothing, so it contributes no key — and a batch of nothing but those
        /// asks the service for nothing at all, which is the saving this whole path exists for.
        /// </remarks>
        [Fact]
        public async Task ABatchOfOnlyNullKeysFetchesNothing()
        {
            var executor = new RecordingExecutor("""{"category":"bikes"}""");

            var rows = await Collect(CosmosLookup.Join(
                Async<string?>(null, null),
                executor,
                null!,
                Query(),
                "@k",
                3,
                b => b,
                element => element.GetProperty("category").GetString()!,
                p => p,
                (b, p) => (b ?? "null") + "/" + p,
                SqlTypeName.VARCHAR));

            executor.Executed.Should().BeEmpty();
            rows.Should().BeEmpty();
        }

        // ── What it pairs up ──────────────────────────────────────────────────────

        [Fact]
        public async Task MatchingRowsArePaired()
        {
            var executor = new RecordingExecutor("""{"category":"bikes"}""", """{"category":"shoes"}""");

            var rows = await Join(Async("bikes", "shoes"), executor);

            rows.Should().Equal("bikes/bikes", "shoes/shoes");
        }

        [Fact]
        public async Task ABuildRowWithNoMatchYieldsNothing()
        {
            var executor = new RecordingExecutor("""{"category":"bikes"}""");

            var rows = await Join(Async("bikes", "hats"), executor);

            rows.Should().Equal("bikes/bikes");
        }

        [Fact]
        public async Task EveryMatchingDocumentIsPaired()
        {
            var executor = new RecordingExecutor("""{"category":"bikes"}""", """{"category":"bikes"}""");

            var rows = await Join(Async("bikes"), executor);

            rows.Should().Equal("bikes/bikes", "bikes/bikes");
        }

        /// <summary>
        /// A key that is an <see cref="int"/> on one side and a <see cref="long"/> on the other still
        /// matches.
        /// </summary>
        /// <remarks>
        /// The two sides arrive by different routes and JSON has one numeric type, so the boxes will
        /// not agree even when the values do. Comparing them directly would drop matching rows and
        /// report a smaller result as though it were the answer — which is why the keys are normalised
        /// rather than compared as they arrive.
        /// </remarks>
        [Fact]
        public async Task NumericKeysMatchAcrossTheirClrTypes()
        {
            var executor = new RecordingExecutor("""{"n":7}""");

            var rows = await Collect(CosmosLookup.Join<int, long, string>(
                Async(7),
                executor,
                null!,
                Query(),
                "@k",
                3,
                b => b,
                element => element.GetProperty("n").GetInt64(),
                p => p,
                (b, p) => $"{b}/{p}",
                SqlTypeName.BIGINT));

            rows.Should().Equal("7/7");
        }

        /// <summary>
        /// A number is reduced to the form its comparison type compares it in: an integer type a <see cref="long"/>,
        /// <c>DECIMAL</c> its value, and an approximate type a <see cref="double"/>.
        /// </summary>
        [Fact]
        public void NormalizeReducesANumberToItsComparisonTypesForm()
        {
            CosmosLookup.Normalize(7, SqlTypeName.INTEGER).Should().BeOfType<long>().Which.Should().Be(7L);
            CosmosLookup.Normalize(9007199254740993L, SqlTypeName.BIGINT).Should().NotBe(CosmosLookup.Normalize(9007199254740992L, SqlTypeName.BIGINT));
            CosmosLookup.Normalize(9007199254740993L, SqlTypeName.DOUBLE).Should().Be(CosmosLookup.Normalize(9007199254740992L, SqlTypeName.DOUBLE));
            CosmosLookup.Normalize(new java.math.BigDecimal("1.0"), SqlTypeName.DECIMAL).Should().Be(CosmosLookup.Normalize(new java.math.BigDecimal("1.00"), SqlTypeName.DECIMAL));
            CosmosLookup.Normalize(new java.math.BigDecimal("0.00"), SqlTypeName.DECIMAL).Should().Be(CosmosLookup.Normalize(0, SqlTypeName.DECIMAL));
            CosmosLookup.Normalize(1.5f, SqlTypeName.REAL).Should().Be(1.5d);

            // Calcite holds a REAL as a float, and its cast to REAL makes 16777217 the float 16777216: a key not yet cast is
            // compared as that, not as the double 16777217, where FLOAT and DOUBLE keep it.
            CosmosLookup.Normalize(java.lang.Integer.valueOf(16777217), SqlTypeName.REAL).Should().Be(16777216d);
            CosmosLookup.Normalize(16777217L, SqlTypeName.REAL).Should().Be(CosmosLookup.Normalize(16777216f, SqlTypeName.REAL));
            CosmosLookup.Normalize(16777217L, SqlTypeName.DOUBLE).Should().Be(16777217d);
            CosmosLookup.Normalize(16777217L, SqlTypeName.FLOAT).Should().Be(16777217d);
            CosmosLookup.Normalize(java.lang.Float.valueOf(0.1f), SqlTypeName.REAL).Should().Be((double)0.1f);
            BitConverter.DoubleToInt64Bits((double)CosmosLookup.Normalize(-0f, SqlTypeName.REAL)!).Should().Be(0L);

            // Two NaN keys are one key here, where = finds them unequal; no container column reads NaN.
            CosmosLookup.Normalize(java.lang.Double.valueOf(double.NaN), SqlTypeName.DOUBLE).Should().Be(CosmosLookup.Normalize(float.NaN, SqlTypeName.DOUBLE));

            // A CLR byte is a Java byte, Calcite's TINYINT, which is signed.
            CosmosLookup.Normalize(unchecked((byte)0xFF), SqlTypeName.TINYINT).Should().BeOfType<long>().Which.Should().Be(-1L);
            CosmosLookup.Normalize(unchecked((byte)0x80), SqlTypeName.TINYINT).Should().Be(-128L);

            CosmosLookup.Normalize(7, SqlTypeName.BIGINT).Should().Be(CosmosLookup.Normalize(7L, SqlTypeName.BIGINT));
            CosmosLookup.Normalize(7d, SqlTypeName.DOUBLE).Should().BeOfType<double>();
            CosmosLookup.Normalize((decimal)7, SqlTypeName.DECIMAL).Should().Be(new java.math.BigDecimal("7"));
            CosmosLookup.Normalize("7", SqlTypeName.VARCHAR).Should().NotBe(CosmosLookup.Normalize(7, SqlTypeName.INTEGER));
            CosmosLookup.Normalize(null, SqlTypeName.BIGINT).Should().BeNull();
        }

        /// <summary>
        /// A number outside the range of the integer type the join compares in fails by name, as Calcite's cast to that type
        /// fails, where it used to wrap: a key 2^64 + 7 under <c>BIGINT</c> was compared as 7 and joined to the documents
        /// holding 7. Within the range a key is cast as Calcite casts it, a fraction toward zero. Only a direct caller hands
        /// the join a key not yet cast.
        /// </summary>
        [Fact]
        public async Task AKeyOutsideItsIntegerTypesRangeFailsByName()
        {
            foreach (var (key, type) in new (object, SqlTypeName)[]
            {
                (java.lang.Integer.valueOf(300), SqlTypeName.TINYINT),
                (java.lang.Integer.valueOf(-32769), SqlTypeName.SMALLINT),
                (java.lang.Long.valueOf(4294967303L), SqlTypeName.INTEGER),
                (new java.math.BigDecimal("18446744073709551623"), SqlTypeName.BIGINT),
                (100000000000000000000m, SqlTypeName.BIGINT),
                (ulong.MaxValue, SqlTypeName.BIGINT),
                (java.lang.Double.valueOf(1e20), SqlTypeName.BIGINT),
            })
                FluentActions.Invoking(() => CosmosLookup.Normalize(key, type)).Should().Throw<CosmosExecutionException>($"{key} is outside {type}");

            CosmosLookup.Normalize(java.lang.Integer.valueOf(127), SqlTypeName.TINYINT).Should().Be(127L);
            CosmosLookup.Normalize(java.lang.Integer.valueOf(-32768), SqlTypeName.SMALLINT).Should().Be(-32768L);
            CosmosLookup.Normalize(new java.math.BigDecimal("9223372036854775807"), SqlTypeName.BIGINT).Should().Be(long.MaxValue);
            CosmosLookup.Normalize(java.lang.Double.valueOf(7.9d), SqlTypeName.BIGINT).Should().Be(7L);
            CosmosLookup.Normalize(java.lang.Double.valueOf(-7.9d), SqlTypeName.BIGINT).Should().Be(-7L);

            // A batch holding one fails by name and asks the container nothing.
            var executor = new RecordingExecutor("""{"n":7}""");
            await FluentActions.Awaiting(() => NumberJoin([new java.math.BigDecimal("18446744073709551623")], executor, n => java.lang.Long.valueOf(n.GetInt64()), SqlTypeName.BIGINT))
                .Should().ThrowAsync<CosmosExecutionException>();
            executor.Executed.Should().BeEmpty();
        }

        /// <summary>
        /// Java's boxes reduce to the same forms as the CLR's, because they are what a row carries.
        /// </summary>
        /// <remarks>
        /// A container row is read into Calcite's boxes, so an <c>INTEGER</c> key arrives as a
        /// <see cref="java.lang.Integer"/>. Passed through, it equalled only a key of the same width,
        /// and it was what the batch bound: the SDK serialised it as an object, and the service matched
        /// nothing (review probes I4, I12 and I13, measured on the emulator).
        /// </remarks>
        [Fact]
        public void NormalizeReducesJavasBoxesToTheClrForms()
        {
            CosmosLookup.Normalize(java.lang.Integer.valueOf(7), SqlTypeName.INTEGER).Should().Be(CosmosLookup.Normalize(7, SqlTypeName.INTEGER));
            CosmosLookup.Normalize(java.lang.Integer.valueOf(7), SqlTypeName.BIGINT).Should().Be(CosmosLookup.Normalize(java.lang.Long.valueOf(7L), SqlTypeName.BIGINT));
            CosmosLookup.Normalize(java.lang.Short.valueOf((short)7), SqlTypeName.SMALLINT).Should().BeOfType<long>().Which.Should().Be(7L);
            CosmosLookup.Normalize(java.lang.Double.valueOf(7.5d), SqlTypeName.DOUBLE).Should().Be(7.5d);
            CosmosLookup.Normalize(new java.math.BigDecimal("7"), SqlTypeName.BIGINT).Should().Be(CosmosLookup.Normalize(7, SqlTypeName.BIGINT));
            CosmosLookup.Normalize(new java.math.BigDecimal("7.00"), SqlTypeName.DECIMAL).Should().Be(CosmosLookup.Normalize((decimal)7, SqlTypeName.DECIMAL));
            CosmosLookup.Normalize(java.lang.Boolean.TRUE, SqlTypeName.BOOLEAN).Should().Be(true);
            CosmosLookup.Normalize(java.lang.Boolean.FALSE, SqlTypeName.BOOLEAN).Should().Be(CosmosLookup.Normalize(false, SqlTypeName.BOOLEAN));
        }

        /// <summary>A <c>DoubleAdder</c> holding <paramref name="value"/>, a Java number Calcite holds no value in.</summary>
        static java.util.concurrent.atomic.DoubleAdder Adder(double value)
        {
            var adder = new java.util.concurrent.atomic.DoubleAdder();
            adder.add(value);
            return adder;
        }

        /// <summary>
        /// Calcite's cast to <c>BIGINT</c> has no answer for a Java number Calcite holds no value in, a
        /// <see cref="java.math.BigInteger"/> or an <see cref="java.util.concurrent.atomic.AtomicLong"/> a direct caller
        /// hands over, and raises an internal assertion. There alone the key is read by its decimal value, which the cast
        /// takes: within the range it is the value, exactly, outside it the key fails by name, and a number whose text is no
        /// decimal, a <c>DoubleAdder</c> holding NaN, fails as the cast of a <see cref="java.lang.Double"/> NaN fails.
        /// </summary>
        [Fact]
        public void ACastToBigintReadsAJavaNumberCalciteHoldsNoValueInByItsDecimalValue()
        {
            FluentActions.Invoking(() => org.apache.calcite.linq4j.tree.Primitive.integerCast(org.apache.calcite.linq4j.tree.Primitive.LONG, java.math.BigInteger.valueOf(7)))
                .Should().Throw<java.lang.AssertionError>("Calcite's own cast to BIGINT has no answer for a BigInteger");

            CosmosLookup.Normalize(java.math.BigInteger.valueOf(7), SqlTypeName.BIGINT).Should().BeOfType<long>().Which.Should().Be(7L);
            CosmosLookup.Normalize(new java.util.concurrent.atomic.AtomicLong(7), SqlTypeName.BIGINT).Should().Be(7L);
            CosmosLookup.Normalize(new java.math.BigInteger("9007199254740993"), SqlTypeName.BIGINT).Should().Be(9007199254740993L);
            CosmosLookup.Normalize(new java.util.concurrent.atomic.AtomicLong(9007199254740993L), SqlTypeName.BIGINT).Should().Be(9007199254740993L);

            var past = new java.math.BigInteger("18446744073709551623");
            FluentActions.Invoking(() => CosmosLookup.Normalize(past, SqlTypeName.BIGINT)).Should().Throw<CosmosExecutionException>("2^64 + 7 is outside BIGINT");

            // One whose text is no decimal fails as Calcite's cast fails on a Double NaN.
            var nan = FluentActions.Invoking(() => CosmosLookup.Normalize(Adder(double.NaN), SqlTypeName.BIGINT)).Should().Throw<CosmosExecutionException>().Which;
            nan.Message.Should().Contain("cannot be cast to BIGINT");
            nan.InnerException.Should().BeOfType<java.lang.NumberFormatException>();
            FluentActions.Invoking(() => org.apache.calcite.linq4j.tree.Primitive.integerCast(org.apache.calcite.linq4j.tree.Primitive.LONG, java.lang.Double.valueOf(double.NaN)))
                .Should().Throw<java.lang.NumberFormatException>();

            // A failure whose Java exception says nothing is named by the exception's class rather than ending in a colon.
            FluentActions.Invoking(() => CosmosLookup.Normalize(java.lang.Double.valueOf(double.NaN), SqlTypeName.BIGINT))
                .Should().Throw<CosmosExecutionException>().Which.Message.Should().NotEndWith(":").And.NotEndWith(": ");
        }

        /// <summary>
        /// A Java number Calcite holds no value in, which a direct caller may hand over, reaches every comparison type but
        /// <c>BIGINT</c> by Calcite's own conversion of it, as any other number does, and where that conversion fails the key
        /// fails by name; with no comparison type it comes back as it was. Read by its decimal text instead, a
        /// <c>DoubleAdder</c> 0.1 was the <c>DECIMAL</c> 0.1 where Calcite's conversion makes it the double's exact value, an
        /// <c>AtomicLong</c> 2^53 + 1 was 2^53 + 1 where Calcite's conversion makes it 2^53, and a <c>DoubleAdder</c> holding
        /// NaN failed under every type, and under none, where Calcite reads it as NaN, or as 0 cast to <c>INTEGER</c>.
        /// </summary>
        [Fact]
        public void AJavaNumberCalciteHoldsNoValueInIsConvertedAsCalciteConvertsIt()
        {
            static object Integer(java.lang.Number number, org.apache.calcite.linq4j.tree.Primitive primitive) =>
                ((java.lang.Number)org.apache.calcite.linq4j.tree.Primitive.integerCast(primitive, number)).longValue();

            var conversions = new (SqlTypeName Type, Func<java.lang.Number, object> Calcite)[]
            {
                (SqlTypeName.TINYINT, n => Integer(n, org.apache.calcite.linq4j.tree.Primitive.BYTE)),
                (SqlTypeName.SMALLINT, n => Integer(n, org.apache.calcite.linq4j.tree.Primitive.SHORT)),
                (SqlTypeName.INTEGER, n => Integer(n, org.apache.calcite.linq4j.tree.Primitive.INT)),
                (SqlTypeName.DECIMAL, n => org.apache.calcite.runtime.SqlFunctions.toBigDecimal(n).stripTrailingZeros()),
                (SqlTypeName.REAL, n => (double)org.apache.calcite.runtime.SqlFunctions.toFloat(n)),
                (SqlTypeName.FLOAT, n => org.apache.calcite.runtime.SqlFunctions.toDouble(n)),
                (SqlTypeName.DOUBLE, n => org.apache.calcite.runtime.SqlFunctions.toDouble(n)),
            };

            foreach (var number in new java.lang.Number[]
            {
                java.math.BigInteger.valueOf(7),
                new java.math.BigInteger("9007199254740993"),
                new java.math.BigInteger("18446744073709551623"),
                new java.util.concurrent.atomic.AtomicLong(9007199254740993L),
                new java.util.concurrent.atomic.AtomicInteger(-7),
                Adder(0.1),
                Adder(1e39),
                Adder(double.NaN),
                Adder(double.PositiveInfinity),
            })
            {
                var name = $"{number.getClass().getName()} {number}";
                CosmosLookup.Normalize(number).Should().BeSameAs(number, $"{name} under no type is handed back as it came");

                foreach (var (type, calcite) in conversions)
                {
                    object expected;
                    try
                    {
                        expected = calcite(number);
                    }
                    catch (java.lang.RuntimeException e)
                    {
                        FluentActions.Invoking(() => CosmosLookup.Normalize(number, type)).Should().Throw<CosmosExecutionException>($"Calcite's conversion of {name} to {type} fails")
                            .Which.InnerException.Should().BeOfType(e.GetType());
                        continue;
                    }

                    CosmosLookup.Normalize(number, type).Should().Be(expected, $"{name} under {type} is Calcite's conversion of it");
                }
            }
        }

        /// <summary>
        /// A key that is an infinity or NaN under a <c>DECIMAL</c> comparison fails by name, as Calcite's conversion of it to a
        /// decimal fails, where it raised that conversion's <c>NumberFormatException</c> unnamed while every integer type named
        /// its failure. A batch holding one fails before any request. Only a direct caller compares a <c>DECIMAL</c>.
        /// </summary>
        [Fact]
        public async Task ADecimalKeyThatIsNotFiniteFailsByName()
        {
            foreach (var key in new object[] { java.lang.Double.valueOf(double.NaN), java.lang.Double.valueOf(double.PositiveInfinity), double.NaN, float.NegativeInfinity })
            {
                var thrown = FluentActions.Invoking(() => CosmosLookup.Normalize(key, SqlTypeName.DECIMAL)).Should().Throw<CosmosExecutionException>($"{key} has no decimal value").Which;
                thrown.Message.Should().Contain("cannot be cast to DECIMAL");
                thrown.InnerException.Should().BeOfType<java.lang.NumberFormatException>();
            }

            FluentActions.Invoking(() => org.apache.calcite.runtime.SqlFunctions.toBigDecimal(java.lang.Double.valueOf(double.NaN)))
                .Should().Throw<java.lang.NumberFormatException>("Calcite's own conversion fails on it");

            var executor = new RecordingExecutor("""{"n":7}""");
            await FluentActions.Awaiting(() => NumberJoin([double.NaN], executor, n => new java.math.BigDecimal(n.GetRawText()), SqlTypeName.DECIMAL))
                .Should().ThrowAsync<CosmosExecutionException>();
            executor.Executed.Should().BeEmpty();
        }

        /// <remarks>
        /// What reaches the service is the normalised key, so a key read out of a row is bound as a
        /// CLR number of the join's comparison type, which the SDK writes as a JSON number, and matches across the
        /// widths the two sides read it as.
        /// </remarks>
        [Fact]
        public async Task AJavaBoxedKeyIsBoundAsANumberAndMatchesAcrossWidths()
        {
            var executor = new RecordingExecutor("""{"n":7}""");

            var rows = await Collect(CosmosLookup.Join<java.lang.Integer, java.lang.Long, string>(
                Async(java.lang.Integer.valueOf(7)),
                executor,
                null!,
                Query(),
                "@k",
                3,
                b => b,
                element => java.lang.Long.valueOf(element.GetProperty("n").GetInt64()),
                p => p,
                (b, p) => $"{b}/{p}",
                comparison: SqlTypeName.BIGINT));

            rows.Should().Equal("7/7");
            Keys(executor.Executed[0]).Should().AllBeOfType<long>().And.Equal(7L, 7L, 7L);
        }

        // ── A key compared as Calcite's = compares it ─────────────────────────────

        /// <summary>Joins number keys to documents holding <c>n</c>, read by <paramref name="read"/>, under a comparison type.</summary>
        static Task<List<string>> NumberJoin(object[] build, RecordingExecutor executor, Func<JsonElement, object> read, SqlTypeName comparison) =>
            Collect(CosmosLookup.Join<object, object, string>(
                new ListCursor<object>(build),
                executor,
                null!,
                Query(),
                "@k",
                3,
                b => b,
                element => read(element.GetProperty("n")),
                p => p,
                (b, p) => $"{b}/{p}",
                comparison: comparison));

        static readonly string[] AroundTwoTo53 = ["""{"n":9007199254740992}""", """{"n":9007199254740993}"""];

        /// <summary>
        /// Keys 2^53 and 2^53 + 1 are two keys under <c>BIGINT</c> and one under <c>DOUBLE</c>, as Calcite's own join
        /// keeps them (<c>Calcite.AnEquiJoinMatchesKeysAsEquality</c>), and each is bound as a number of the type: the
        /// long exactly, which a double would have sent as 2^53.
        /// </summary>
        [Fact]
        public async Task KeysPastTwoTo53JoinApartUnderBigintAndTogetherUnderDouble()
        {
            var exact = new RecordingExecutor(AroundTwoTo53);
            var rows = await NumberJoin([java.lang.Long.valueOf(9007199254740993L)], exact, n => java.lang.Long.valueOf(n.GetInt64()), SqlTypeName.BIGINT);

            rows.Should().Equal("9007199254740993/9007199254740993");
            Keys(exact.Executed.Single()).Should().AllBeOfType<long>().And.AllBeEquivalentTo(9007199254740993L);

            var approximate = new RecordingExecutor(AroundTwoTo53);
            rows = await NumberJoin([java.lang.Double.valueOf(9007199254740992d)], approximate, n => java.lang.Double.valueOf(n.GetDouble()), SqlTypeName.DOUBLE);

            rows.Should().HaveCount(2, "one double holds both");
            Keys(approximate.Executed.Single()).Should().AllBeOfType<double>().And.AllBeEquivalentTo(9007199254740992d);

            // A BIGINT key a join compares as DOUBLE is the double Calcite casts it to.
            rows = await NumberJoin([java.lang.Long.valueOf(9007199254740993L)], new RecordingExecutor(AroundTwoTo53), n => java.lang.Double.valueOf(n.GetDouble()), SqlTypeName.DOUBLE);
            rows.Should().HaveCount(2);
        }

        /// <summary>
        /// A <c>BIGINT</c> key matches a probe row reading <c>7</c> as an <c>INTEGER</c>, each the long Calcite casts it to.
        /// </summary>
        [Fact]
        public async Task ABigintKeyMatchesAProbesInteger()
        {
            var executor = new RecordingExecutor("""{"n":7}""", """{"n":8}""");

            var rows = await NumberJoin([java.lang.Long.valueOf(7L)], executor, n => java.lang.Integer.valueOf(n.GetInt32()), SqlTypeName.BIGINT);

            rows.Should().Equal("7/7");
            Keys(executor.Executed.Single()).Should().AllBeEquivalentTo(7L);
        }

        /// <summary>
        /// A <c>DECIMAL</c> key matches its value written to another scale, and two such keys are one key of the batch,
        /// fetched once; zero at any scale is zero. A decimal is bound as the number the service compares as it.
        /// </summary>
        [Fact]
        public async Task ADecimalKeyMatchesItsValueAtAnotherScale()
        {
            var executor = new RecordingExecutor("""{"n":1.00}""", """{"n":0.000}""");

            var rows = await NumberJoin([new java.math.BigDecimal("1.0"), new java.math.BigDecimal("1"), new java.math.BigDecimal("0.0")], executor,
                n => new java.math.BigDecimal(n.GetRawText()), SqlTypeName.DECIMAL);

            rows.Should().Equal("1.0/1.00", "1/1.00", "0.0/0.000");
            Keys(executor.Executed.Single()).Should().Equal(1m, 0m, 0m);
        }

        /// <summary>
        /// A <c>DOUBLE</c> key <c>-0.0</c> matches a probe row reading <c>0</c>, as <c>=</c> does, where Calcite's own join
        /// leaves it unmatched; beside <c>0.0</c> it is one key of the batch.
        /// </summary>
        [Fact]
        public async Task ANegativeZeroKeyMatchesZeroAsEqualityDoes()
        {
            var executor = new RecordingExecutor("""{"n":0}""");

            var rows = await NumberJoin([java.lang.Double.valueOf(-0d), java.lang.Double.valueOf(0d)], executor, n => java.lang.Double.valueOf(n.GetDouble()), SqlTypeName.DOUBLE);

            rows.Should().HaveCount(2, "each key matches the document holding 0");
            // One key, padded by repeating it, and sent as plus zero, the 0 the service reads back: no row says how it
            // compares a minus zero sent with a stored 0.
            Keys(executor.Executed.Single()).Select(k => BitConverter.DoubleToInt64Bits((double)k!)).Should().Equal(Enumerable.Repeat(BitConverter.DoubleToInt64Bits(0d), 3), "-0.0 and 0.0 are one key, plus zero");

            // Alone, a minus zero is sent as plus zero too.
            var alone = new RecordingExecutor("""{"n":0}""");
            (await NumberJoin([java.lang.Double.valueOf(-0d)], alone, n => java.lang.Double.valueOf(n.GetDouble()), SqlTypeName.DOUBLE)).Should().ContainSingle();
            Keys(alone.Executed.Single()).Select(k => BitConverter.DoubleToInt64Bits((double)k!)).Should().AllBeEquivalentTo(BitConverter.DoubleToInt64Bits(0d));
            BitConverter.DoubleToInt64Bits((double)CosmosLookup.Normalize(-0f, SqlTypeName.REAL)!).Should().Be(BitConverter.DoubleToInt64Bits(0d));
        }

        /// <summary>
        /// A <c>DECIMAL</c> key is bound as <c>CosmosQueries</c> binds a decimal parameter, a decimal where its double is
        /// right and a long where the double is another whole number; and where neither serves, or where the decimal would
        /// round the key past its 28 places, as its double, which the service holds such a number as.
        /// </summary>
        [Fact]
        public async Task ADecimalKeyIsBoundAsTheNumberTheServiceComparesAsIt()
        {
            // The keys each statement is sent, the build side holding one row per key, joined under DECIMAL.
            static async Task<object?[]> Bound(params string[] keys)
            {
                var executor = new RecordingExecutor();
                await Collect(CosmosLookup.Join<object, object, string>(
                    new ListCursor<object>([.. keys.Select(k => (object)new java.math.BigDecimal(k))]),
                    executor,
                    null!,
                    Query(),
                    "@k",
                    keys.Length,
                    b => b,
                    element => element,
                    p => p,
                    (b, p) => $"{b}/{p}",
                    SqlTypeName.DECIMAL));

                return Keys(executor.Executed.Single());
            }

            (await Bound("1.50", "0.1", "9007199254740993", "100000000000000000001")).Should().Equal(1.5m, 0.1m, 9007199254740993L, 1e20d);

            // A decimal holds 28 places, and rounds a key past them to another number: 1E-30 to 0, which no document holding
            // 1E-30 equals. Each is sent as its double, which the stored number is held as.
            (await Bound("1E-30", "1.23456789E-25", "1.2345678901234567E-13")).Should().Equal(1e-30d, 1.23456789E-25d, 1.2345678901234567E-13d);
        }

        /// <summary>
        /// A <c>DECIMAL</c> key beyond a double's range has no number to be sent as, so the batch holding it is fetched by
        /// the statement without the key restriction: a document whose number reads as the key is matched, as Calcite's
        /// own join matches it, and a left join keeps a row nothing matched. It used to be answered as matching nothing,
        /// unsent, on a premise about what a document may hold.
        /// </summary>
        [Fact]
        public async Task ADecimalKeyBeyondADoublesRangeIsFetchedWithoutTheRestriction()
        {
            var unrestricted = new CosmosQuery("SELECT VALUE c FROM products c", Array.Empty<CosmosParameter>());

            IClrCursor<string> DecimalJoin(RecordingExecutor executor, bool left, CosmosQuery? open, params string[] keys) =>
                CosmosLookup.Join<object, object, string>(
                    new ListCursor<object>([.. keys.Select(k => (object)new java.math.BigDecimal(k))]),
                    executor,
                    null!,
                    Query(),
                    "@k",
                    3,
                    b => b,
                    element => new java.math.BigDecimal(element.GetProperty("n").GetRawText()),
                    p => p,
                    (b, p) => $"{b}/{p}",
                    SqlTypeName.DECIMAL,
                    unmatched: left ? (Func<object, string>)(b => $"{b}/null") : null,
                    unrestricted: open);

            // The batch holding the key is sent without the restriction, and each of its keys pairs with what it fetched.
            var inner = new RecordingExecutor("""{"n":1e400}""", """{"n":1}""", """{"n":2}""");
            (await Collect(DecimalJoin(inner, false, unrestricted, "1e400", "1"))).Should().Equal("1E+400/1E+400", "1/1");
            inner.Executed.Should().ContainSingle().Which.Sql.Should().Be(unrestricted.Sql);
            Keys(inner.Executed.Single()).Should().BeEmpty();

            var outer = new RecordingExecutor("""{"n":1}""");
            (await Collect(DecimalJoin(outer, true, unrestricted, "1e400", "1"))).Should().Equal("1E+400/null", "1/1");
            outer.Executed.Should().ContainSingle().Which.Sql.Should().Be(unrestricted.Sql);

            // A batch without one is restricted as before.
            var plain = new RecordingExecutor("""{"n":1}""");
            (await Collect(DecimalJoin(plain, false, unrestricted, "1", "2"))).Should().Equal("1/1");
            plain.Executed.Should().ContainSingle().Which.Sql.Should().Be(Query().Sql);
            Keys(plain.Executed.Single()).Should().Equal(1m, 2m, 2m);

            // Given no statement without the restriction, such a batch fails by name rather than answering unasked.
            var missing = new RecordingExecutor("""{"n":1e400}""");
            await FluentActions.Awaiting(() => Collect(DecimalJoin(missing, true, null, "1e400"))).Should().ThrowAsync<CosmosExecutionException>();
            missing.Executed.Should().BeEmpty();
        }

        /// <summary>
        /// An infinite or NaN approximate key has no number to be sent as either: JSON holds none, and the SDK writes one as
        /// the string <c>"Infinity"</c>, <c>"-Infinity"</c> or <c>"NaN"</c>, which no stored number equals. So the batch
        /// holding it is fetched by the statement without the key restriction, as a decimal beyond a double's range is: an
        /// infinity pairs with a document its column reads as that infinity, a key not yet cast to <c>REAL</c> among them,
        /// and a NaN with nothing. Sent restricted, an infinity lost the document Calcite's own join matches.
        /// </summary>
        [Fact]
        public async Task ANonFiniteApproximateKeyIsFetchedWithoutTheRestriction()
        {
            var unrestricted = new CosmosQuery("SELECT VALUE c FROM products c", Array.Empty<CosmosParameter>());
            string[] documents = ["""{"id":"pinf","n":1e39}""", """{"id":"pminf","n":-1e39}""", """{"id":"p15","n":1.5}""", """{"id":"p0","n":0}"""];

            object AsReal(JsonElement element) => java.lang.Float.valueOf((float)element.GetProperty("n").GetDouble());
            object AsDouble(JsonElement element) => java.lang.Double.valueOf(element.GetProperty("n").GetDouble());

            // Joins labelled keys to the documents, each read as the column reads its number, and answers the labels paired.
            async Task<(List<string> Rows, CosmosQuery Sent)> Joined(SqlTypeName type, Func<JsonElement, object> read, bool left, params (string Label, object Key)[] keys)
            {
                var executor = new RecordingExecutor(documents);
                var rows = await Collect(CosmosLookup.Join<(string Label, object Key), (string Id, object N), string>(
                    new ListCursor<(string Label, object Key)>(keys),
                    executor,
                    null!,
                    Query(),
                    "@k",
                    3,
                    b => b.Key,
                    element => (element.GetProperty("id").GetString()!, read(element)),
                    p => p.N,
                    (b, p) => $"{b.Label}/{p.Id}",
                    type,
                    unmatched: left ? (Func<(string Label, object Key), string>)(b => $"{b.Label}/null") : null,
                    unrestricted: unrestricted));

                return (rows, executor.Executed.Single());
            }

            var (rows, sent) = await Joined(SqlTypeName.REAL, AsReal, false,
                ("inf", java.lang.Float.valueOf(float.PositiveInfinity)), ("minf", java.lang.Float.valueOf(float.NegativeInfinity)), ("r15", java.lang.Float.valueOf(1.5f)));
            rows.Should().Equal("inf/pinf", "minf/pminf", "r15/p15");
            sent.Sql.Should().Be(unrestricted.Sql);
            Keys(sent).Should().BeEmpty();

            // A direct caller's key not yet cast: Calcite's cast to REAL makes 1e39 an infinity.
            (rows, sent) = await Joined(SqlTypeName.REAL, AsReal, false, ("big", java.lang.Double.valueOf(1e39)));
            rows.Should().Equal("big/pinf");
            sent.Sql.Should().Be(unrestricted.Sql);

            (rows, sent) = await Joined(SqlTypeName.DOUBLE, AsDouble, true, ("nan", java.lang.Double.valueOf(double.NaN)), ("d15", java.lang.Double.valueOf(1.5d)));
            rows.Should().Equal("nan/null", "d15/p15");
            sent.Sql.Should().Be(unrestricted.Sql);
            Keys(sent).Should().BeEmpty();

            // A batch of finite keys is restricted as before.
            (rows, sent) = await Joined(SqlTypeName.REAL, AsReal, false, ("r15", java.lang.Float.valueOf(1.5f)));
            rows.Should().Equal("r15/p15");
            sent.Sql.Should().Be(Query().Sql);
            Keys(sent).Should().Equal(1.5d, 1.5d, 1.5d);
        }

        /// <summary>
        /// A <c>UUID</c> is one value however it was boxed: Calcite's <c>UuidValue</c>, Java's own, or a
        /// <see cref="Guid"/>.
        /// </summary>
        [Fact]
        public void NormalizeReducesEveryUuidToAGuid()
        {
            const string text = "0123456f-89ab-7cde-8f01-23456789abcd";

            CosmosLookup.Normalize(org.apache.calcite.util.UuidValue.fromString(text)).Should().Be(Guid.Parse(text));
            CosmosLookup.Normalize(java.util.UUID.fromString(text)).Should().Be(Guid.Parse(text));
            CosmosLookup.Normalize(Guid.Parse(text)).Should().Be(Guid.Parse(text));
            CosmosLookup.Normalize(text).Should().NotBe(Guid.Parse(text), "text is not a UUID until something says it is one");
        }

        /// <summary>
        /// Under the emulator a string key is sent in the stored spelling, U+0000 as U+FDD0 <c>0</c> and U+FDD0 as U+FDD0
        /// <c>1</c>, which <c>IN</c> compares as the service stores the path; the documents come back decoded, so each
        /// pairs with the key it was fetched for, as written. A key holding neither is sent as it is.
        /// </summary>
        [Fact]
        public async Task AKeyIsSentInTheStoredSpellingWhereTheTargetHasOne()
        {
            var executor = new RecordingExecutor("""{"category":"a\u0000"}""", """{"category":"﷐"}""", """{"category":"bikes"}""");

            var rows = await Collect(CosmosLookup.Join(
                Async("a\0", "﷐", "bikes"),
                executor,
                null!,
                Query(),
                "@k",
                3,
                b => b,
                element => element.GetProperty("category").GetString()!,
                p => p,
                (b, p) => b + "/" + p,
                SqlTypeName.VARCHAR,
                spell: CosmosLookup.SpellStored(null)));

            Keys(executor.Executed.Single()).Should().Equal("a﷐0", "﷐1", "bikes");
            rows.Should().Equal("a\0/a\0", "﷐/﷐", "bikes/bikes");
            CosmosLookup.SpellStored(_ => 7d)("x").Should().Be(7d, "a key another spelling writes as no string is that spelling");
        }

        // ── A probe that sends nothing ─────────────────────────────────────────────

        /// <summary>
        /// A probe whose <c>WHERE</c> the facts graph proved keeps no document and fails on none fetches nothing: every key
        /// is answered absent, as the service would answer it, while the build side is read to its end, every batch, so
        /// what it fails on fails as it would. An inner join keeps no row and a left join every build row unmatched.
        /// </summary>
        [Fact]
        public async Task AProbeThatSendsNothingFetchesNothingAndReadsTheBuildSide()
        {
            var executor = new RecordingExecutor("""{"category":"bikes"}""");
            var build = Async("bikes", "shoes", "hats", "bikes");

            (await Join(build, executor, batchSize: 2, query: Query() with { SendsNothing = true })).Should().BeEmpty();
            build.Read().Should().BeFalse("every build row was read");

            var left = await ListCursor.CollectAsync(CosmosLookup.Join<string?, string, string>(
                new ListCursor<string?>(["bikes", null]), executor, null!, Query() with { SendsNothing = true }, "@k", 3,
                b => b, element => element.GetProperty("category").GetString()!, p => p, (b, p) => b + "/" + p, SqlTypeName.VARCHAR, unmatched: b => (b ?? "null") + "/null"),
                TestContext.Current.CancellationToken);
            left.Should().Equal("bikes/null", "null/null");

            executor.Executed.Should().BeEmpty("no batch is fetched");
        }

        // ── A left join ───────────────────────────────────────────────────────────

        static Task<List<string>> LeftJoin(IClrCursor<string?> build, RecordingExecutor executor, int batchSize = 3, int cacheSize = 0) =>
            ListCursor.CollectAsync(CosmosLookup.Join<string?, string, string>(
                build,
                executor,
                null!,
                Query(),
                "@k",
                batchSize,
                b => b,
                element => element.GetProperty("category").GetString()!,
                p => p,
                (b, p) => b + "/" + p,
                SqlTypeName.VARCHAR,
                cacheSize,
                unmatched: b => (b ?? "null") + "/null"));

        /// <summary>
        /// A left join keeps a build row nothing matched, a null key and a key the container has nothing
        /// for alike, in the build side's order.
        /// </summary>
        [Fact]
        public async Task ALeftJoinKeepsTheRowsNothingMatched()
        {
            var executor = new RecordingExecutor("""{"category":"bikes"}""");

            var rows = await LeftJoin(new ListCursor<string?>(["hats", "bikes", null, "bikes"]), executor, batchSize: 2);

            rows.Should().Equal("hats/null", "bikes/bikes", "null/null", "bikes/bikes");
        }

        /// <summary>
        /// And a batch of only null keys still asks the container nothing: its rows are unmatched without
        /// asking.
        /// </summary>
        [Fact]
        public async Task ALeftJoinOfOnlyNullKeysFetchesNothing()
        {
            var executor = new RecordingExecutor("""{"category":"bikes"}""");

            var rows = await LeftJoin(new ListCursor<string?>([null, null]), executor);

            rows.Should().Equal("null/null", "null/null");
            executor.Executed.Should().BeEmpty();
        }

        /// <summary>
        /// A key remembered as absent is unmatched in a later batch too, and is not asked for again.
        /// </summary>
        [Fact]
        public async Task ALeftJoinKeepsARowWhoseKeyIsRememberedAbsent()
        {
            var executor = new RecordingExecutor("""{"category":"bikes"}""");

            var rows = await LeftJoin(new ListCursor<string?>(["hats", "hats"]), executor, batchSize: 1, cacheSize: 10);

            rows.Should().Equal("hats/null", "hats/null");
            executor.Executed.Should().ContainSingle();
        }

        // ── A UUID key ────────────────────────────────────────────────────────────

        /// <summary>
        /// A <c>UUID</c> key is bound in the container's spelling, and the document it fetches pairs with
        /// the build row it was fetched for.
        /// </summary>
        [Fact]
        public async Task AUuidKeyIsBoundInTheStoredSpelling()
        {
            const string text = "0123456f-89ab-7cde-8f01-23456789abcd";
            var executor = new RecordingExecutor("""{"id":"0123456F-89AB-7CDE-8F01-23456789ABCD"}""");

            var rows = await ListCursor.CollectAsync(CosmosLookup.Join<org.apache.calcite.util.UuidValue, org.apache.calcite.util.UuidValue, string>(
                new ListCursor<org.apache.calcite.util.UuidValue>([org.apache.calcite.util.UuidValue.fromString(text)]),
                executor,
                null!,
                Query(),
                "@k",
                3,
                b => b,
                element => org.apache.calcite.util.UuidValue.fromString(element.GetProperty("id").GetString()),
                p => p,
                (b, p) => "paired",
                SqlTypeName.UUID,
                spell: CosmosLookup.SpellUuid(Adapter.Metadata.CosmosUuidForms.CanonicalUpper)));

            rows.Should().Equal("paired");
            Keys(executor.Executed.Single()).Should().AllBeEquivalentTo("0123456F-89AB-7CDE-8F01-23456789ABCD");
        }

        /// <summary>
        /// A key the form has no spelling for names no document, and is not asked for.
        /// </summary>
        [Fact]
        public async Task AKeyWithNoSpellingIsNotFetched()
        {
            var executor = new RecordingExecutor("""{"category":"bikes"}""");

            var rows = await ListCursor.CollectAsync(CosmosLookup.Join<string, string, string>(
                new ListCursor<string>(["hats"]),
                executor,
                null!,
                Query(),
                "@k",
                3,
                b => b,
                element => element.GetProperty("category").GetString()!,
                p => p,
                (b, p) => b + "/" + p,
                SqlTypeName.VARCHAR,
                unmatched: b => b + "/null",
                spell: _ => null));

            rows.Should().Equal("hats/null");
            executor.Executed.Should().BeEmpty();
        }

        // ── Remembering ───────────────────────────────────────────────────────────

        /// <remarks>
        /// Deduplication only reaches within a batch. Reference data is looked up repeatedly across
        /// them by definition, and this is where the saving is: a thousand build rows over one key
        /// should ask once, not once per batch.
        /// </remarks>
        [Fact]
        public async Task AKeyRepeatedAcrossBatchesIsFetchedOnce()
        {
            var executor = new RecordingExecutor("""{"category":"bikes"}""");

            var rows = await Join(Async("bikes", "bikes", "bikes"), executor, batchSize: 1, cacheSize: 16);

            executor.Executed.Should().ContainSingle("the second and third batches should be answered from memory");
            rows.Should().HaveCount(3, "every build row still joins");
        }

        /// <remarks>
        /// The case a cache most needs to hold. Without remembering absence, a key the container has
        /// nothing for is asked about again in every batch that mentions it, and told nothing again.
        /// </remarks>
        [Fact]
        public async Task AKeyWithNoMatchIsRememberedToo()
        {
            var executor = new RecordingExecutor();

            var rows = await Join(Async("hats", "hats", "hats"), executor, batchSize: 1, cacheSize: 16);

            executor.Executed.Should().ContainSingle("absence is an answer, and it is worth keeping");
            rows.Should().BeEmpty();
        }

        [Fact]
        public async Task WithoutACacheEveryBatchFetches()
        {
            var executor = new RecordingExecutor("""{"category":"bikes"}""");

            await Join(Async("bikes", "bikes", "bikes"), executor, batchSize: 1, cacheSize: 0);

            executor.Executed.Should().HaveCount(3);
        }

        /// <remarks>
        /// The bound is what stops a large build side from being remembered whole. Filled and then
        /// left alone rather than evicted — nothing here knows which key is worth keeping, and a wrong
        /// eviction costs a request.
        /// </remarks>
        [Fact]
        public async Task TheCacheStopsGrowingAtItsBound()
        {
            var executor = new RecordingExecutor();

            // One key fills the cache; the second is never remembered, so it is asked for every time.
            await Join(Async("a", "b", "a", "b"), executor, batchSize: 1, cacheSize: 1);

            var asked = executor.Executed.SelectMany(Keys).Distinct().ToList();
            asked.Should().BeEquivalentTo(new object?[] { "a", "b" });

            executor.Executed.Should().HaveCount(3, "a is remembered after the first batch; b never is");
        }

        // ── The cache across executions ───────────────────────────────────────────

        /// <remarks>
        /// The point of the whole feature: a second execution over the same keys and the same
        /// statement asks the service nothing.
        /// </remarks>
        [Fact]
        public async Task ASecondExecutionIsServedFromTheSharedCache()
        {
            var shared = new CosmosLookupCache(100, TimeSpan.FromMinutes(5));

            var first = new RecordingExecutor("""{"category":"bikes"}""");
            var one = await Join(Async("bikes"), first, shared: shared);

            var second = new RecordingExecutor("""{"category":"bikes"}""");
            var two = await Join(Async("bikes"), second, shared: shared);

            first.Executed.Should().ContainSingle();
            second.Executed.Should().BeEmpty("the answer was remembered across executions");
            two.Should().Equal(one);
        }

        [Fact]
        public async Task AbsenceIsSharedAcrossExecutions()
        {
            var shared = new CosmosLookupCache(100, TimeSpan.FromMinutes(5));

            await Join(Async("missing"), new RecordingExecutor(), shared: shared);

            var second = new RecordingExecutor();
            var rows = await Join(Async("missing"), second, shared: shared);

            second.Executed.Should().BeEmpty("nothing was remembered as the answer, and remembered is remembered");
            rows.Should().BeEmpty();
        }

        /// <remarks>
        /// Two plans rendering different statements must not share answers: the entries are keyed by
        /// the statement as well as the key.
        /// </remarks>
        [Fact]
        public async Task DifferentStatementsDoNotShareTheCache()
        {
            var shared = new CosmosLookupCache(100, TimeSpan.FromMinutes(5));

            await Join(Async("bikes"), new RecordingExecutor("""{"category":"bikes"}"""), shared: shared);

            var other = new CosmosQuery("SELECT VALUE c FROM archive c WHERE c.category IN (@k0, @k1, @k2)", Array.Empty<CosmosParameter>());
            var second = new RecordingExecutor("""{"category":"bikes"}""");
            await Join(Async("bikes"), second, query: other, shared: shared);

            second.Executed.Should().ContainSingle("an answer for one statement is no answer for another");
        }

        /// <remarks>
        /// The per-join cache holds built rows and the shared cache holds JSON; a batch answered from
        /// the shared cache still fills the per-join one, so the same key in a later batch of the
        /// same join costs neither a request nor a rebuild.
        /// </remarks>
        [Fact]
        public async Task ASharedHitStillFillsThePerJoinCache()
        {
            var shared = new CosmosLookupCache(100, TimeSpan.FromMinutes(5));

            await Join(Async("bikes"), new RecordingExecutor("""{"category":"bikes"}"""), shared: shared);

            var second = new RecordingExecutor();
            var rows = await Join(Async("bikes", "bikes"), second, batchSize: 1, cacheSize: 10, shared: shared);

            second.Executed.Should().BeEmpty();
            rows.Should().Equal("bikes/bikes", "bikes/bikes");
        }

        // ── As a cursor ───────────────────────────────────────────────────────────

        /// <remarks>
        /// Nothing is acquired at the open. A batch is read and fetched by the advance that runs out of
        /// rows, so a join opened and never read asks the container nothing.
        /// </remarks>
        [Fact]
        public async Task TheOpenFetchesNothing()
        {
            var executor = new RecordingExecutor("""{"category":"bikes"}""");
            var build = Async("bikes");

            await using var cursor = Open(build, executor);

            executor.Executed.Should().BeEmpty();
            build.Tokens.Should().BeEmpty("the build side is not read at the open either");
        }

        /// <remarks>
        /// <b>The token of the advance that needs a batch is the token its fetch runs under</b>, and it
        /// is the token its build rows are read under too. The same advance that exhausts one batch's
        /// pairs is the one that reads the next batch and fetches for it.
        /// </remarks>
        [Fact]
        public async Task EachBatchIsFetchedUnderTheTokenOfTheAdvanceThatNeedsIt()
        {
            var executor = new RecordingExecutor(query => Keys(query).Distinct().Select(k => $$"""{"category":"{{k}}"}"""));
            var build = Async("a", "b");

            using var first = new CancellationTokenSource();
            using var second = new CancellationTokenSource();

            await using var cursor = Open(build, executor, batchSize: 1);

            (await cursor.ReadAsync(first.Token)).Should().BeTrue();
            cursor.Current.Should().Be("a/a");
            (await cursor.ReadAsync(second.Token)).Should().BeTrue();
            cursor.Current.Should().Be("b/b");

            executor.Tokens.Should().Equal(first.Token, second.Token);
            build.Tokens.Should().Equal(first.Token, second.Token);
        }

        /// <remarks>
        /// A reader that stops within the first batch never pays for the second: the batches are fetched
        /// as they are reached, not all at the open.
        /// </remarks>
        [Fact]
        public async Task AReaderThatStopsEarlyFetchesNoFurtherBatch()
        {
            var executor = new RecordingExecutor(query => Keys(query).Distinct().Select(k => $$"""{"category":"{{k}}"}"""));

            await using (var cursor = Open(Async("a", "b", "c"), executor, batchSize: 1))
                (await cursor.ReadAsync(CancellationToken.None)).Should().BeTrue();

            executor.Executed.Should().ContainSingle();
        }

        /// <remarks>
        /// A synchronous advance reads the build side with <c>Read</c> and waits only for the fetch, so it
        /// is held to the same rows and the same statements as the awaiting one.
        /// </remarks>
        [Fact]
        public void ASynchronousAdvanceJoinsTheSameRows()
        {
            var executor = new RecordingExecutor(query => Keys(query).Distinct().Select(k => $$"""{"category":"{{k}}"}"""));
            var build = Async("a", "b", "hats");

            using var cursor = Open(build, executor, batchSize: 2);

            var rows = new List<string>();
            while (cursor.Read())
                rows.Add(cursor.Current);

            rows.Should().Equal("a/a", "b/b", "hats/hats");
            executor.Executed.Should().HaveCount(2);
            build.Tokens.Should().BeEmpty("the build side was read with Read, not by waiting on ReadAsync");
        }

        [Fact]
        public async Task EveryFetchesCursorIsReleased()
        {
            var executor = new RecordingExecutor(query => Keys(query).Distinct().Select(k => $$"""{"category":"{{k}}"}"""));

            await Join(Async("a", "b"), executor, batchSize: 1);

            executor.Cursors.Should().HaveCount(2).And.OnlyContain(c => c.Disposed);
        }

        [Fact]
        public async Task DisposingTheJoinDisposesItsBuildSide()
        {
            var build = Async("a");

            var cursor = Open(build, new RecordingExecutor());
            await cursor.DisposeAsync();

            build.Disposed.Should().BeTrue();
        }

        [Fact]
        public async Task TheAwaitingOpenAwaitsTheBuildSidesOpen()
        {
            var executor = new RecordingExecutor("""{"category":"bikes"}""");

            static async ValueTask<IClrCursor<string>> OpenBuild()
            {
                await Task.Yield();
                return new ListCursor<string>(["bikes"]);
            }

            var rows = await Collect(await CosmosLookup.JoinAsync(
                OpenBuild(),
                executor,
                null!,
                Query(),
                "@k",
                3,
                b => b,
                element => element.GetProperty("category").GetString()!,
                p => p,
                (b, p) => b + "/" + p,
                SqlTypeName.VARCHAR,
                0,
                null,
                null,
                null,
                null,
                CancellationToken.None));

            rows.Should().Equal("bikes/bikes");
        }

        [Fact]
        public void TheJoinRefusesAMissingArgumentOrAnEmptyBatch()
        {
            var executor = new RecordingExecutor();

            var noBuild = () => CosmosLookup.Join<string, string, string>(null!, executor, null!, Query(), "@k", 3, b => b, e => "", p => p, (b, p) => b, SqlTypeName.VARCHAR);
            var noExecutor = () => CosmosLookup.Join<string, string, string>(Async("a"), null!, null!, Query(), "@k", 3, b => b, e => "", p => p, (b, p) => b, SqlTypeName.VARCHAR);
            var noBatch = () => CosmosLookup.Join<string, string, string>(Async("a"), executor, null!, Query(), "@k", 0, b => b, e => "", p => p, (b, p) => b, SqlTypeName.VARCHAR);

            // The keys are compared in the type Calcite casts them to, and a join with none has no type to compare them in.
            var noComparison = () => CosmosLookup.Join<string, string, string>(Async("a"), executor, null!, Query(), "@k", 3, b => b, e => "", p => p, (b, p) => b, null!);

            noBuild.Should().Throw<ArgumentNullException>();
            noExecutor.Should().Throw<ArgumentNullException>();
            noBatch.Should().Throw<ArgumentOutOfRangeException>();
            noComparison.Should().Throw<ArgumentNullException>().Which.ParamName.Should().Be("comparison");
        }

        // ── Binding ───────────────────────────────────────────────────────────────

        [Fact]
        public void BindKeepsTheStatementsOwnParameters()
        {
            var query = new CosmosQuery("SELECT VALUE c FROM products c WHERE c.price > @p0 AND c.category IN (@k0, @k1)", new[] { new CosmosParameter("@p0", 100) });

            var bound = CosmosLookup.Bind(query, "@k", 2, new object?[] { "bikes" });

            bound.Parameters.Should().Equal(
                new CosmosParameter("@p0", 100),
                new CosmosParameter("@k0", "bikes"),
                new CosmosParameter("@k1", "bikes"));
        }

        [Fact]
        public void BindRefusesMoreKeysThanTheStatementCarries()
        {
            var bind = () => CosmosLookup.Bind(Query(), "@k", 2, new object?[] { "a", "b", "c" });

            bind.Should().Throw<ArgumentException>();
        }

        /// <summary>
        /// A data context that answers the values it was given and nothing else.
        /// </summary>
        sealed class Context : org.apache.calcite.DataContext
        {

            readonly Dictionary<string, object?> _values;

            public Context(params (string Name, object? Value)[] values)
            {
                _values = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
            }

            public org.apache.calcite.schema.SchemaPlus getRootSchema() => null!;

            public org.apache.calcite.adapter.java.JavaTypeFactory getTypeFactory() => null!;

            public org.apache.calcite.linq4j.QueryProvider getQueryProvider() => null!;

            public object get(string name) => _values.TryGetValue(name, out var value) ? value! : null!;

        }

        /// <summary>A statement whose own filter compares with a prepared statement's parameter.</summary>
        static CosmosQuery PricedQuery() => new(
            "SELECT VALUE c FROM products c WHERE c.price > @p0 AND c.category IN (@k0, @k1, @k2)",
            new[] { new CosmosParameter("@p0", new CosmosDynamicValue(0)) });

        static Context Price(int value) => new(("?0", java.lang.Integer.valueOf(value)));

        /// <remarks>
        /// The statement's own slots are closed from the execution's context before the keys are
        /// appended, as a plain statement's are. Left open, the slot itself was sent as the value, and
        /// the comparison it stood in was never true: the join returned nothing, and said nothing. #219.
        /// </remarks>
        [Fact]
        public async Task TheStatementsOwnParametersAreBoundFromTheExecution()
        {
            var executor = new RecordingExecutor("""{"category":"bikes"}""");

            var rows = await Join(Async("bikes"), executor, query: PricedQuery(), root: Price(100));

            executor.Executed.Single().Parameters.Should().Equal(
                new CosmosParameter("@p0", 100L),
                new CosmosParameter("@k0", "bikes"),
                new CosmosParameter("@k1", "bikes"),
                new CosmosParameter("@k2", "bikes"));
            rows.Should().Equal("bikes/bikes");
        }

        /// <remarks>
        /// The shared cache names a statement by its parameters' values, so it must be named by the
        /// values the execution bound rather than by the slot every execution shares: an answer fetched
        /// under one price is no answer under another.
        /// </remarks>
        [Fact]
        public async Task ExecutionsBindingDifferentValuesDoNotShareTheCache()
        {
            var shared = new CosmosLookupCache(100, TimeSpan.FromMinutes(5));

            await Join(Async("bikes"), new RecordingExecutor("""{"category":"bikes"}"""), query: PricedQuery(), shared: shared, root: Price(100));

            var other = new RecordingExecutor();
            await Join(Async("bikes"), other, query: PricedQuery(), shared: shared, root: Price(200));

            var same = new RecordingExecutor();
            await Join(Async("bikes"), same, query: PricedQuery(), shared: shared, root: Price(100));

            other.Executed.Should().ContainSingle("a different value is a different statement");
            other.Executed.Single().Parameters[0].Value.Should().Be(200L);
            same.Executed.Should().BeEmpty("the same value is the same statement, and was remembered");
        }

    }

}
