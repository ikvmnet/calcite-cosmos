using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;
using FluentAssertions.Execution;

using org.apache.calcite.sql.type;

using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Client
{

    /// <summary>
    /// Covers the binding of a statement's parameters at execution: the values a plan left open, read
    /// out of the data context and shaped into what the service is to receive.
    /// </summary>
    public class CosmosQueriesTests
    {

        /// <summary>
        /// A data context that answers the values it was given and nothing else.
        /// </summary>
        sealed class Context : org.apache.calcite.DataContext
        {

            readonly Dictionary<string, object?> _values;

            public Context(params (string Name, object? Value)[] values)
            {
                _values = new Dictionary<string, object?>(StringComparer.Ordinal);

                foreach (var (name, value) in values)
                    _values[name] = value;
            }

            public org.apache.calcite.schema.SchemaPlus getRootSchema() => null!;

            public org.apache.calcite.adapter.java.JavaTypeFactory getTypeFactory() => null!;

            public org.apache.calcite.linq4j.QueryProvider getQueryProvider() => null!;

            public object get(string name) => _values.TryGetValue(name, out var value) ? value! : null!;

        }

        const string PointJson = "{\"type\":\"Point\",\"coordinates\":[-111.5,38.3]}";

        static org.locationtech.jts.geom.Geometry Point() =>
            Apache.Calcite.Geography.Runtime.GeographyFunctions.FromGeoJson(PointJson);

        static CosmosQuery Query(params CosmosParameter[] parameters) =>
            new("SELECT VALUE { \"d\": ST_DISTANCE(c.location, @p0) } FROM products c", parameters);

        static CosmosQuery BindPoint() =>
            CosmosQueries.Bind(
                Query(new CosmosParameter("@p0", new CosmosDynamicValue(0))),
                new Context(("?0", Point())));

        /// <summary>
        /// A statement with nothing open is handed back as it stands.
        /// </summary>
        [Fact]
        public void AStatementWithNoOpenSlotIsUnchanged()
        {
            var query = Query(new CosmosParameter("@p0", 42L));

            CosmosQueries.Bind(query, new Context()).Should().Be(query);
        }

        /// <remarks>
        /// <para>
        /// A string holding a lone surrogate cannot be sent: the SDK's UTF-8 encoder refuses it while it writes the
        /// statement, and threw <c>EncoderFallbackException</c>, which names nothing a caller wrote
        /// (<c>Service.LoneSurrogateParameterIsNeverSent</c>). Bound when the statement runs, the parameter is refused by
        /// name before the encoder is reached. A pair is a character and is sent.
        /// </para>
        /// </remarks>
        [Fact]
        public void AParameterHoldingALoneSurrogateIsRefusedByName()
        {
            var query = new CosmosQuery("SELECT VALUE c.id FROM c WHERE (c.s = @p0)", new[] { new CosmosParameter("@p0", new CosmosDynamicValue(0)) });

            var act = () => CosmosQueries.Bind(query, new Context(("?0", "a\uD83D")));
            act.Should().Throw<CosmosExecutionException>().WithMessage("Parameter ?0 holds a lone surrogate*cannot be sent*");

            CosmosQueries.Bind(query, new Context(("?0", "a😀"))).Parameters[0].Value.Should().Be("a😀");
        }

        /// <remarks>
        /// The widths Calcite boxes a number in are collapsed to the two a statement carries, so that
        /// a value arriving late is indistinguishable from one written into the statement.
        /// </remarks>
        [Fact]
        public void ANumberBindsAsTheWidthAStatementCarries()
        {
            var bound = CosmosQueries.Bind(
                Query(new CosmosParameter("@p0", new CosmosDynamicValue(0))),
                new Context(("?0", java.lang.Integer.valueOf(7))));

            bound.Parameters[0].Value.Should().Be(7L);
        }

        /// <summary>
        /// An exact number arriving late binds as a literal of it would: a decimal where its double serves, a long where
        /// the double would be another whole number, and nothing where neither is the number.
        /// </summary>
        /// <remarks>
        /// The service reads a decimal as a double, and 2^60 + 1 sent as one matched the document holding 2^60 —
        /// <c>CosmosRexLiterals.TryGetExactNumberValue</c>. The plan was made to send the value, so one no value serves
        /// fails the execution rather than being sent as a number it is not.
        /// </remarks>
        [Fact]
        public void AnExactNumberBindsAsALiteralOfItWould()
        {
            CosmosQuery Bind(string number) => CosmosQueries.Bind(
                Query(new CosmosParameter("@p0", new CosmosDynamicValue(0))),
                new Context(("?0", new java.math.BigDecimal(number))));

            Bind("2.5").Parameters[0].Value.Should().BeOfType<decimal>().Which.Should().Be(2.5m);
            Bind("1152921504606846977").Parameters[0].Value.Should().BeOfType<long>().Which.Should().Be(1152921504606846977L);

            FluentActions.Invoking(() => Bind("1.9999999999999999")).Should().Throw<CosmosExecutionException>();
        }

        // -- The parameter's type ------------------------------------------------------------------

        /// <summary>Binds one value into a slot of a type, through a data context holding it as it stands.</summary>
        static object? BindAs(SqlTypeName? type, object? held, int? length = null, bool padded = false) => CosmosQueries.Bind(
            Query(new CosmosParameter("@p0", new CosmosDynamicValue(0) { TypeName = type, Length = length, Padded = padded })),
            new Context(("?0", held))).Parameters[0].Value;

        /// <summary>
        /// A value is sent as Calcite compares it, converted to the parameter's type, whichever class the data context
        /// holds: each case <c>CalciteParameterMeasurementTests.AParameterIsConvertedToItsTypeWhenRead</c> measures, as
        /// Calcite's own driver leaves the caller's class there and as the ADO.NET driver writes the type's own.
        /// </summary>
        /// <remarks>
        /// Sent as the data context held it, a long bound to a <c>DOUBLE</c> parameter past 2^53 was compared by the
        /// service as another number than Calcite's, 3,000,000,000 bound to an <c>INTEGER</c> one unwrapped, 1.5 bound to
        /// a <c>BIGINT</c> one uncut, the string <c>'2'</c> bound to a number as a string the service leaves undefined
        /// against a number, and the long 30 bound to text as a number it leaves undefined against a string
        /// (<c>Calcite.AParameterIsConvertedToItsTypeWhenRead</c>).
        /// </remarks>
        [Fact]
        public void AValueIsSentAsCalciteConvertsItToTheParametersType()
        {
            var cases = new (string Case, SqlTypeName Type, object Held, int? Length, object Sent)[]
            {
                // Calcite's own driver: the caller's class.
                ("DOUBLE, long 2^53 + 1", SqlTypeName.DOUBLE, java.lang.Long.valueOf(9007199254740993L), null, 9007199254740992d),
                ("INTEGER, long 3,000,000,000", SqlTypeName.INTEGER, java.lang.Long.valueOf(3000000000L), null, -1294967296L),
                ("SMALLINT, long 65,537", SqlTypeName.SMALLINT, java.lang.Long.valueOf(65537L), null, 1L),
                ("TINYINT, long 257", SqlTypeName.TINYINT, java.lang.Long.valueOf(257L), null, 1L),
                ("REAL, long 2^24 + 1", SqlTypeName.REAL, java.lang.Long.valueOf(16777217L), null, 16777216d),
                ("BIGINT, double 1.5", SqlTypeName.BIGINT, java.lang.Double.valueOf(1.5), null, 1L),
                ("BIGINT, string '2'", SqlTypeName.BIGINT, "2", null, 2L),
                ("DECIMAL, string '2.50'", SqlTypeName.DECIMAL, "2.50", null, 2.5m),
                ("RETURNING INTEGER, string '2'", SqlTypeName.INTEGER, "2", null, 2L),
                ("VARCHAR, long 30", SqlTypeName.VARCHAR, java.lang.Long.valueOf(30L), null, "30"),
                ("VARCHAR, double 1e20", SqlTypeName.VARCHAR, java.lang.Double.valueOf(1e20), null, "1.0E20"),
                ("JSON_VALUE's VARCHAR(2000), long 30", SqlTypeName.VARCHAR, java.lang.Long.valueOf(30L), 2000, "30"),
                ("CHAR(1), long 17", SqlTypeName.CHAR, java.lang.Long.valueOf(17L), 1, "1"),
                ("projected DOUBLE, long 2^53 + 1", SqlTypeName.DOUBLE, java.lang.Long.valueOf(9007199254740993L), null, 9007199254740992d),

                // The ADO.NET driver: the type's own class, which converts to itself.
                ("DOUBLE, double 2^53", SqlTypeName.DOUBLE, java.lang.Double.valueOf(9007199254740992d), null, 9007199254740992d),
                ("REAL, float 2^24", SqlTypeName.REAL, java.lang.Float.valueOf(16777216f), null, 16777216d),
                ("BIGINT, long 2", SqlTypeName.BIGINT, java.lang.Long.valueOf(2L), null, 2L),
                ("DECIMAL, decimal 2.50", SqlTypeName.DECIMAL, new java.math.BigDecimal("2.50"), null, 2.5m),
                ("INTEGER, integer 2", SqlTypeName.INTEGER, java.lang.Integer.valueOf(2), null, 2L),
                ("BOOLEAN, boolean true", SqlTypeName.BOOLEAN, java.lang.Boolean.TRUE, null, true),
                ("VARCHAR, string '30'", SqlTypeName.VARCHAR, "30", null, "30"),
                ("VARCHAR, string '1E+20'", SqlTypeName.VARCHAR, "1E+20", null, "1E+20"),
            };

            using var scope = new AssertionScope();
            foreach (var (name, type, held, length, sent) in cases)
                BindAs(type, held, length).Should().Be(sent, name);

            BindAs(SqlTypeName.CHAR, java.lang.Long.valueOf(7L), length: 3, padded: true).Should().Be("7  ", "the text of 7 padded to a CHAR(3)");
        }

        /// <summary>
        /// A value Calcite cannot convert to the parameter's type fails the statement by name, as Calcite's own plan fails
        /// reading it: a NaN bound to a <c>BIGINT</c> or a <c>DOUBLE</c> parameter, an infinity bound to a <c>DOUBLE</c>,
        /// a string that spells no number bound to a <c>BIGINT</c>, and a value of another class than the one Calcite holds
        /// the type in, which Calcite's cast refuses: a string bound to a <c>BOOLEAN</c>, a string, a
        /// <c>java.util.UUID</c> or a <see cref="Guid"/> bound to a <c>UUID</c>, an integer bound to a <c>TIMESTAMP</c>,
        /// a long bound to a <c>DATE</c>, and text bound to a <c>GEOMETRY</c>; and a null bound to an <c>INTERVAL DAY</c>,
        /// which the step after the conversion unboxes to multiply, as Calcite's code does.
        /// </summary>
        [Fact]
        public void AValueCalciteCannotConvertFailsByName()
        {
            var cases = new (SqlTypeName Type, object? Held, string Raised)[]
            {
                (SqlTypeName.BIGINT, java.lang.Double.valueOf(double.NaN), "NumberFormatException"),
                (SqlTypeName.DOUBLE, java.lang.Double.valueOf(double.NaN), "NumberFormatException"),
                (SqlTypeName.DOUBLE, java.lang.Double.valueOf(double.NegativeInfinity), "NumberFormatException"),
                (SqlTypeName.DOUBLE, java.lang.Double.valueOf(double.PositiveInfinity), "NumberFormatException"),
                (SqlTypeName.BIGINT, "x", "NumberFormatException"),
                (SqlTypeName.BOOLEAN, "true", "ClassCastException"),
                (SqlTypeName.UUID, "123e4567-e89b-12d3-a456-426614174000", "ClassCastException"),
                (SqlTypeName.UUID, java.util.UUID.fromString("123e4567-e89b-12d3-a456-426614174000"), "ClassCastException"),
                (SqlTypeName.UUID, Guid.Parse("123e4567-e89b-12d3-a456-426614174000"), "ClassCastException"),
                (SqlTypeName.TIMESTAMP, java.lang.Integer.valueOf(20667), "ClassCastException"),
                (SqlTypeName.DATE, java.lang.Long.valueOf(1785672000000L), "ClassCastException"),
                (SqlTypeName.GEOMETRY, "{\"type\":\"Point\",\"coordinates\":[0,0]}", "ClassCastException"),
                (SqlTypeName.INTERVAL_DAY, null, "NullReferenceException"),
            };

            using var scope = new AssertionScope();
            foreach (var (type, held, raised) in cases)
                FluentActions.Invoking(() => BindAs(type, held)).Should().Throw<CosmosExecutionException>()
                    .WithMessage($"Parameter ?0 cannot be converted to its type, {type.getName()}, as Calcite converts it when a row reads it ({raised}: *");
        }

        /// <summary>
        /// A slot of a type whose class Calcite's type factory does not answer from the type's name alone, a <c>ROW</c>,
        /// fails the statement by name, whatever is bound, as the slot cannot be converted as Calcite converts it.
        /// </summary>
        /// <remarks>
        /// Asked before the conversion, the factory's own failure, a cast to a record type, escaped unnamed.
        /// </remarks>
        [Fact]
        public void ASlotWhoseClassItsTypesNameDoesNotGiveFailsByName()
        {
            foreach (var held in new object?[] { java.lang.Long.valueOf(1L), "x", null })
                FluentActions.Invoking(() => BindAs(SqlTypeName.ROW, held)).Should().Throw<CosmosExecutionException>()
                    .WithMessage("Parameter ?0 is of type ROW, for which Calcite's type factory answers no class from the type's name alone (*");
        }

        /// <summary>
        /// The conversion comes before the type's length holds a string and before a bound is written from it, and a slot of
        /// a type Calcite reads as it is, or of none, sends the value as it arrived.
        /// </summary>
        [Fact]
        public void TheConvertedValueIsHeldToItsLengthAndAnyPassesAsItIs()
        {
            BindAs(SqlTypeName.VARCHAR, java.lang.Long.valueOf(30L), length: 1).Should().Be("3", "the text '30' held to one unit");
            BindAs(SqlTypeName.ANY, java.lang.Long.valueOf(30L)).Should().Be(30L, "an ANY parameter is read as the class it holds");
            BindAs(SqlTypeName.ANY, "30").Should().Be("30");
            BindAs(null, "2").Should().Be("2", "a slot made without a type binds the value as it arrives");
            BindAs(SqlTypeName.BIGINT, null).Should().BeNull("a null is no value to convert");

            var bounded = CosmosQueries.Bind(
                Query(new CosmosParameter("@p0", new CosmosDynamicValue(0) { TypeName = SqlTypeName.VARCHAR, Bound = CosmosAnchorBound.Lower })),
                new Context(("?0", java.lang.Long.valueOf(30L))));
            bounded.Parameters[0].Value.Should().Be(CosmosServiceOrder.LowerBound("30"), "the bound is written from the converted text");
        }

        /// <summary>
        /// An unsigned type, in Calcite's numeric family, is converted through <c>toBigDecimal</c> and
        /// <c>UnsignedType</c>'s own conversion, as Calcite's generated code converts it, and a value past it fails by name.
        /// </summary>
        [Fact]
        public void AnUnsignedTypeIsConvertedByCalcitesUnsignedConversion()
        {
            BindAs(SqlTypeName.UTINYINT, java.lang.Long.valueOf(5L)).Should().Be(5m);
            BindAs(SqlTypeName.UINTEGER, "4294967295").Should().Be(4294967295m);
            FluentActions.Invoking(() => BindAs(SqlTypeName.UTINYINT, java.lang.Long.valueOf(300L))).Should().Throw<CosmosExecutionException>()
                .WithMessage("Parameter ?0 cannot be converted to its type, UTINYINT, *");
        }

        /// <summary>
        /// The conversion is the code Calcite generates for a dynamic parameter: over a parameter of the numeric, character,
        /// binary, boolean, datetime, interval, <c>UUID</c>, geometry, collection, <c>ANY</c> and <c>VARIANT</c> types, each
        /// slotted as the translator slots it, and over the classes a caller of Calcite's own driver binds and null, it
        /// answers what Calcite's code for a <c>RexDynamicParam</c> of the type answers, the value in the same class, or
        /// fails where that code fails, with the same exception.
        /// </summary>
        /// <remarks>
        /// The leaf conversions and the classes are Calcite's, called, and the dispatch between them, the length a slot
        /// carries and what it holds a value to are written in C# (<c>CosmosRexTranslator.SlotOf</c>,
        /// <c>CosmosQueries.Converted</c>), so this holds them to what Calcite's code generator makes of the same parameter
        /// (<c>RexExecutorImpl.getExecutable</c>), wherever a later Calcite moves it: a type's length, a <c>CHAR</c>'s
        /// padding, and an interval's multiplier among it. Left out at 16 types, the binary types and every interval that
        /// does not end in a month were bound as another value than Calcite reads, the bytes uncut and the number
        /// unmultiplied; with no null among the values, a null bound to an interval Calcite multiplies was bound as null,
        /// where Calcite's code raises unboxing it.
        /// </remarks>
        [Fact]
        public void TheConversionIsTheCodeCalciteGeneratesForAParameter()
        {
            var types = new org.apache.calcite.jdbc.JavaTypeFactoryImpl();
            var rex = new org.apache.calcite.rex.RexBuilder(types);

            var typed = new List<org.apache.calcite.rel.type.RelDataType>
            {
                types.createSqlType(SqlTypeName.TINYINT),
                types.createSqlType(SqlTypeName.SMALLINT),
                types.createSqlType(SqlTypeName.INTEGER),
                types.createSqlType(SqlTypeName.BIGINT),
                types.createSqlType(SqlTypeName.UTINYINT),
                types.createSqlType(SqlTypeName.USMALLINT),
                types.createSqlType(SqlTypeName.UINTEGER),
                types.createSqlType(SqlTypeName.UBIGINT),
                types.createSqlType(SqlTypeName.DECIMAL),
                types.createSqlType(SqlTypeName.DECIMAL, 10, 2),
                types.createSqlType(SqlTypeName.REAL),
                types.createSqlType(SqlTypeName.FLOAT),
                types.createSqlType(SqlTypeName.DOUBLE),
                types.createSqlType(SqlTypeName.CHAR),
                types.createSqlType(SqlTypeName.CHAR, 3),
                types.createSqlType(SqlTypeName.VARCHAR),
                types.createSqlType(SqlTypeName.VARCHAR, 2),
                types.createSqlType(SqlTypeName.BINARY),
                types.createSqlType(SqlTypeName.BINARY, 2),
                types.createSqlType(SqlTypeName.VARBINARY),
                types.createSqlType(SqlTypeName.VARBINARY, 2),
                types.createSqlType(SqlTypeName.BOOLEAN),
                types.createSqlType(SqlTypeName.DATE),
                types.createSqlType(SqlTypeName.TIME),
                types.createSqlType(SqlTypeName.TIME, 3),
                types.createSqlType(SqlTypeName.TIMESTAMP),
                types.createSqlType(SqlTypeName.TIMESTAMP, 3),
                types.createSqlType(SqlTypeName.TIMESTAMP_WITH_LOCAL_TIME_ZONE, 3),
                types.createSqlType(SqlTypeName.UUID),
                types.createSqlType(SqlTypeName.GEOMETRY),
                types.createArrayType(types.createSqlType(SqlTypeName.INTEGER), -1),
                types.createMapType(types.createSqlType(SqlTypeName.VARCHAR), types.createSqlType(SqlTypeName.INTEGER)),
                types.createMultisetType(types.createSqlType(SqlTypeName.INTEGER), -1),
                types.createSqlType(SqlTypeName.ANY),
                types.createSqlType(SqlTypeName.VARIANT),
            };

            // Every interval, from its start and end units.
            foreach (var name in SqlTypeName.INTERVAL_TYPES.toArray())
            {
                var interval = (SqlTypeName)name;
                var end = interval.getEndUnit() == interval.getStartUnit() ? null : interval.getEndUnit();
                typed.Add(types.createSqlIntervalType(new org.apache.calcite.sql.SqlIntervalQualifier(interval.getStartUnit(), end, org.apache.calcite.sql.parser.SqlParserPos.ZERO)));
            }

            var list = new java.util.ArrayList();
            list.add(java.lang.Integer.valueOf(1));
            var map = new java.util.HashMap();
            map.put("a", java.lang.Integer.valueOf(1));

            var held = new object?[]
            {
                java.lang.Long.valueOf(4294967298L), java.lang.Long.valueOf(-129L), java.lang.Long.valueOf(9007199254740993L), java.lang.Long.valueOf(long.MinValue),
                java.lang.Long.valueOf(7L), java.lang.Integer.valueOf(70000), java.lang.Integer.valueOf(7), java.lang.Integer.valueOf(int.MinValue),
                java.lang.Short.valueOf((short)300), java.lang.Byte.valueOf(unchecked((byte)(sbyte)-7)),
                java.lang.Double.valueOf(1.9), java.lang.Double.valueOf(-1.9), java.lang.Double.valueOf(1e20), java.lang.Double.valueOf(0.1), java.lang.Double.valueOf(-0.0),
                java.lang.Double.valueOf(4.9e-324), java.lang.Double.valueOf(1e300), java.lang.Double.valueOf(double.NaN), java.lang.Double.valueOf(double.PositiveInfinity),
                java.lang.Float.valueOf(0.1f), java.lang.Float.valueOf(3.4e38f), java.lang.Float.valueOf(-0.0f),
                new java.math.BigDecimal("2.555"), new java.math.BigDecimal("1E+3"), new java.math.BigDecimal("123456789012345678901234567890.5"), new java.math.BigDecimal("-0.000"),
                new java.math.BigInteger("18446744073709551617"),
                " 7 ", "2.5e1", "x", "ab", "abcd", "a😀", "1e400", "-1e-400", "+3", "NaN", "Infinity", "0x10", "", "true",
                java.lang.Boolean.TRUE, java.util.UUID.fromString("123e4567-e89b-12d3-a456-426614174000"),
                org.apache.calcite.util.UuidValue.fromString("123e4567-e89b-12d3-a456-426614174000"), java.lang.Character.valueOf('7'),
                new org.apache.calcite.avatica.util.ByteString([1]), new org.apache.calcite.avatica.util.ByteString([1, 2]), new org.apache.calcite.avatica.util.ByteString([1, 2, 3]),
                list, map, Point(), null,
            };

            using var scope = new AssertionScope();
            foreach (var type in typed)
            {
                var parameter = rex.makeDynamicParam(types.createTypeWithNullability(type, true), 0);
                var code = org.apache.calcite.rex.RexExecutorImpl.getExecutable(rex, java.util.Collections.singletonList(parameter), types.builder().build());
                var slot = CosmosRexTranslator.SlotOf(parameter);

                foreach (var value in held)
                {
                    var calcite = Outcome(() =>
                    {
                        code.setDataContext(new Context(("?0", value)));
                        return code.execute()[0];
                    });

                    Outcome(() => CosmosQueries.Converted(value, slot)).Should().Be(calcite, $"{parameter.getType().getFullTypeString()} bound to {Outcome(() => value)}");
                }
            }

            // The value and its class, or the innermost exception's class: the conversion's own, beneath the one naming it. A
            // cast that fails is one exception, which IKVM raises as InvalidCastException from compiled code and as Java's
            // ClassCastException from Class.cast.
            static string Outcome(Func<object?> read)
            {
                try
                {
                    return read() switch
                    {
                        null => "null",
                        string text => "java.lang.String " + text,
                        java.lang.Object value => value.getClass().getName() + " " + value.toString(),
                        var other => other.GetType().FullName + " " + other,
                    };
                }
                catch (Exception e)
                {
                    while (e.InnerException is { } inner)
                        e = inner;

                    return "raises " + (e is InvalidCastException ? typeof(java.lang.ClassCastException).FullName : e.GetType().FullName);
                }
            }
        }

        /// <summary>
        /// A slot written in a path's stored form converts the value to its type first: the epoch milliseconds a
        /// <c>TIMESTAMP</c> is held in and the days a <c>DATE</c> is are written, a <c>UuidValue</c> is spelled, and a value
        /// of another class fails by name, as Calcite's cast fails it.
        /// </summary>
        [Fact]
        public void AStoredFormValueIsConvertedToItsTypeFirst()
        {
            object? Stored(SqlTypeName type, Adapter.Metadata.CosmosRepresentation form, object held) => CosmosQueries.Bind(
                Query(new CosmosParameter("@p0", new CosmosDynamicValue(0) { Form = form, TypeName = type })),
                new Context(("?0", held))).Parameters[0].Value;

            var instant = Adapter.Metadata.CosmosTemporalForms.Iso8601UtcMilliseconds;
            Stored(SqlTypeName.TIMESTAMP, instant, java.lang.Long.valueOf(1785672000000L)).Should().Be("2026-08-02T12:00:00.000Z");
            Stored(SqlTypeName.DATE, instant, java.lang.Integer.valueOf(20667)).Should().Be("2026-08-02T00:00:00.000Z");
            Stored(SqlTypeName.UUID, Adapter.Metadata.CosmosUuidForms.CanonicalLower, org.apache.calcite.util.UuidValue.fromString("123E4567-E89B-12D3-A456-426614174000"))
                .Should().Be("123e4567-e89b-12d3-a456-426614174000");

            using var scope = new AssertionScope();
            foreach (var (type, form, held) in new (SqlTypeName, Adapter.Metadata.CosmosRepresentation, object)[]
            {
                (SqlTypeName.TIMESTAMP, instant, java.lang.Integer.valueOf(20667)),
                (SqlTypeName.DATE, instant, java.lang.Long.valueOf(1785672000000L)),
                (SqlTypeName.UUID, Adapter.Metadata.CosmosUuidForms.CanonicalLower, "123e4567-e89b-12d3-a456-426614174000"),
                (SqlTypeName.UUID, Adapter.Metadata.CosmosUuidForms.CanonicalLower, Guid.Parse("123e4567-e89b-12d3-a456-426614174000")),
            })
                FluentActions.Invoking(() => Stored(type, form, held)).Should().Throw<CosmosExecutionException>()
                    .WithMessage($"Parameter ?0 cannot be converted to its type, {type.getName()}, as Calcite converts it when a row reads it (ClassCastException: *");
        }

        /// <summary>
        /// A row count's slot is bound as the rows Calcite's limit takes for the value: the ceiling of the decimal it reads,
        /// held to the most the statement writes, under the rounding policy the planner's context held; a value the limit
        /// refuses fails by name, as Calcite's own plan fails.
        /// </summary>
        /// <remarks>
        /// Bound as it was held, 2.5 was sent as a count the service refuses, and read by the ordered cursor as the 2 .NET
        /// rounds it to, where Calcite takes three rows (<c>Calcite.ARowCountIsTakenToItsCeiling</c>).
        /// </remarks>
        [Fact]
        public void ARowCountIsBoundAsTheRowsCalcitesLimitTakes()
        {
            object? Count(object? held, org.apache.calcite.adapter.enumerable.FetchOffsetRoundingPolicy? rounding = null) => CosmosQueries.Bind(
                Query(new CosmosParameter("@p0", new CosmosDynamicValue(0) { RowCount = "FETCH", RowCountRounding = rounding })),
                new Context(("?0", held))).Parameters[0].Value;

            using (new AssertionScope())
            {
                Count(java.lang.Double.valueOf(2.5)).Should().Be(3L);
                Count(java.lang.Double.valueOf(0.5)).Should().Be(1L);
                Count(new java.math.BigDecimal("2.01")).Should().Be(3L);
                Count(java.lang.Integer.valueOf(2)).Should().Be(2L);
                Count(java.lang.Double.valueOf(2.0)).Should().Be(2L);
                Count(java.lang.Long.valueOf(0L)).Should().Be(0L);
                Count(java.lang.Long.valueOf(3000000000L)).Should().Be((long)int.MaxValue, "a count past an int reads every row, as the most the statement writes does");
                Count(java.lang.Double.valueOf(2.5), new Floor()).Should().Be(2L, "the planner's rounding policy comes first");
            }

            using var scope = new AssertionScope();
            foreach (var (held, raised) in new (object?, string)[]
            {
                ("2", "IllegalArgumentException"),
                (java.lang.Double.valueOf(double.NaN), "NumberFormatException"),
                (java.lang.Long.valueOf(-1L), "IllegalArgumentException"),
                (null, "IllegalArgumentException"),
            })
                FluentActions.Invoking(() => Count(held)).Should().Throw<CosmosExecutionException>()
                    .WithMessage($"Parameter ?0 cannot be read as the FETCH count, as Calcite's limit reads it ({raised}: *");
        }

        /// <summary>A rounding policy that takes a count down to its whole part.</summary>
        sealed class Floor : org.apache.calcite.adapter.enumerable.FetchOffsetRoundingPolicy
        {

            public java.math.BigDecimal round(java.math.BigDecimal value) => value.setScale(0, java.math.RoundingMode.FLOOR);

        }

        // -- A non-finite value ----------------------------------------------------------------------

        /// <summary>
        /// A value that is NaN or an infinity once converted fails the statement by name, whatever its type and wherever
        /// the statement reads it: a finite value Calcite's conversion carries past an approximate type's range, as
        /// <c>CalciteParameterMeasurementTests.ANonFiniteDoubleParameterRaises</c> measures it, and a non-finite double an
        /// <c>ANY</c>, a <c>VARIANT</c> or an untyped slot holds as it arrived.
        /// </summary>
        /// <remarks>
        /// A bridge: the SDK writes the value as a JSON string (<see cref="ANonFiniteDoubleIsNoJsonNumber"/>), which the
        /// service reads as a string where Calcite reads the number.
        /// </remarks>
        [Fact]
        public void ANonFiniteValueIsRefusedByName()
        {
            var cases = new (string Case, SqlTypeName? Type, object Held, string Spelled)[]
            {
                ("DOUBLE, string '1e400'", SqlTypeName.DOUBLE, "1e400", "Infinity"),
                ("DOUBLE, string '-1e400'", SqlTypeName.DOUBLE, "-1e400", "-Infinity"),
                ("FLOAT, string '1e400'", SqlTypeName.FLOAT, "1e400", "Infinity"),
                ("REAL, double 1e39", SqlTypeName.REAL, java.lang.Double.valueOf(1e39), "Infinity"),
                ("REAL, double -1e39", SqlTypeName.REAL, java.lang.Double.valueOf(-1e39), "-Infinity"),
                ("ANY, double NaN", SqlTypeName.ANY, java.lang.Double.valueOf(double.NaN), "NaN"),
                ("VARIANT, double +Infinity", SqlTypeName.VARIANT, java.lang.Double.valueOf(double.PositiveInfinity), "Infinity"),
                ("untyped, double +Infinity", null, java.lang.Double.valueOf(double.PositiveInfinity), "Infinity"),
            };

            using var scope = new AssertionScope();
            foreach (var (name, type, held, spelled) in cases)
                FluentActions.Invoking(() => BindAs(type, held)).Should().Throw<CosmosExecutionException>(name)
                    .WithMessage($"Parameter ?0 holds {spelled}, which the Cosmos DB SDK writes as a JSON string rather than a number, so the statement cannot be sent*");
        }

        /// <summary>
        /// A finite value is sent, −0.0 as the 0.0 Calcite converts it to, and a text slot holding <c>'NaN'</c> is a string;
        /// a NaN bound to a numeric slot fails by the conversion's name, as Calcite's conversion fails it.
        /// </summary>
        [Fact]
        public void AFiniteValueIsSent()
        {
            BindAs(SqlTypeName.DOUBLE, java.lang.Double.valueOf(1.5)).Should().Be(1.5d);
            BindAs(SqlTypeName.DOUBLE, java.lang.Double.valueOf(-0.0d)).Should().BeOfType<double>().Which.Should().Match(d => d == 0d && double.IsNegative(d) == false, "−0.0 is finite, and sent as the 0.0 Calcite's conversion reads it as through a BigDecimal, which has no −0.0");
            BindAs(SqlTypeName.VARCHAR, "NaN").Should().Be("NaN", "the text 'NaN' is a string");
            FluentActions.Invoking(() => BindAs(SqlTypeName.DOUBLE, java.lang.Double.valueOf(double.NaN))).Should().Throw<CosmosExecutionException>()
                .WithMessage("Parameter ?0 cannot be converted to its type, DOUBLE, *");
        }

        /// <summary>
        /// A NaN or an infinity is no JSON number: Newtonsoft's serializer, the SDK's default, writes each as a string, and
        /// System.Text.Json refuses each.
        /// </summary>
        [Fact]
        public void ANonFiniteDoubleIsNoJsonNumber()
        {
            Newtonsoft.Json.JsonConvert.SerializeObject(double.NaN).Should().Be("\"NaN\"");
            Newtonsoft.Json.JsonConvert.SerializeObject(double.PositiveInfinity).Should().Be("\"Infinity\"");
            Newtonsoft.Json.JsonConvert.SerializeObject(double.NegativeInfinity).Should().Be("\"-Infinity\"");
            FluentActions.Invoking(() => System.Text.Json.JsonSerializer.Serialize(double.PositiveInfinity)).Should().Throw<ArgumentException>();
        }

        // -- A geography ---------------------------------------------------------------------------

        /// <summary>
        /// A geography binds as the GeoJSON object a statement means by one, which is what #154 said
        /// it did not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The value the context hands over is a JTS geometry, and a JTS geometry is not a document
        /// value.</b> Left alone it reached <c>QueryDefinition.WithParameter</c> as itself and the
        /// SDK's serializer wrote the IKVM object graph -- <c>$0</c>-keyed, assembly-qualified, three
        /// kilobytes for this point -- so the service received that in place of a shape. Where the
        /// statement also projected the parameter it came back and was read as a geography, which is
        /// the stack the issue reports.
        /// </para>
        /// <para>
        /// A geography is the one type whose constant form is not a bound value at all: a geography in
        /// a Cosmos statement <em>is</em> a GeoJSON object, so <c>WriteGeographyLiteral</c> writes one
        /// into the SQL. That is why the agreement between a literal and a parameter has to be reached
        /// here rather than inherited from <c>GetLiteralValue</c>, which refuses a <c>GEOMETRY</c>
        /// literal outright.
        /// </para>
        /// </remarks>
        [Fact]
        public void AGeographyBindsAsGeoJson()
        {
            var bound = BindPoint();

            bound.Parameters[0].Value.Should().NotBeAssignableTo<org.locationtech.jts.geom.Geometry>(
                "a geometry is not a document value, and the SDK would serialize its object graph");

            var shape = bound.Parameters[0].Value.Should().BeOfType<Dictionary<string, object?>>().Subject;

            shape["type"].Should().Be("Point");
            shape.Should().NotContainKey("crs",
                "the constant form sends none, and a geography has no second reference system to be in");
        }

        /// <summary>
        /// What the service receives is the object, under either serializer.
        /// </summary>
        /// <remarks>
        /// The parameter goes to <c>QueryDefinition.WithParameter</c> and is written by whichever
        /// serializer the client carries, so the bound value is a dictionary and a list rather than
        /// one serializer's own tree. Both are asserted because which one runs is the caller's choice.
        /// </remarks>
        [Fact]
        public void TheBoundValueSerializesAsTheShape()
        {
            var bound = BindPoint();

            Newtonsoft.Json.JsonConvert.SerializeObject(bound.Parameters[0].Value).Should().Be(PointJson);
            System.Text.Json.JsonSerializer.Serialize(bound.Parameters[0].Value).Should().Be(PointJson);
        }

        /// <summary>
        /// And it is what the definition the executor builds carries.
        /// </summary>
        [Fact]
        public void TheQueryDefinitionCarriesTheShape()
        {
            var definition = CosmosQueryExecutor.CreateDefinition(BindPoint());

            foreach (var parameter in definition.GetQueryParameters())
                parameter.Value.Should().BeOfType<Dictionary<string, object?>>();
        }

        /// <summary>
        /// The shape the service echoes back reads as the geometry that was bound.
        /// </summary>
        /// <remarks>
        /// The half #154 reports, closed. A statement that also projects its parameter gets the value
        /// returned to it, and that column is read by <c>CosmosJson.GetGeography</c>; sending the
        /// shape is what makes it a round trip rather than a failure.
        /// </remarks>
        [Fact]
        public void TheShapeReadsBackAsTheGeometryThatWasBound()
        {
            var point = Point();
            var sent = Newtonsoft.Json.JsonConvert.SerializeObject(BindPoint().Parameters[0].Value);

            using var echoed = System.Text.Json.JsonDocument.Parse(sent);
            var read = CosmosJson.GetValue(echoed.RootElement, org.apache.calcite.sql.type.SqlTypeName.GEOMETRY);

            var geometry = read.Should().BeAssignableTo<org.locationtech.jts.geom.Geometry>().Subject;

            geometry.equalsExact(point).Should().BeTrue();
            geometry.getSRID().Should().Be(4326);
        }

        /// <summary>
        /// A parameter the statement names as stored (<see cref="CosmosQuery.Stored"/>) is sent in the emulator's spelling,
        /// U+0000 as U+FDD0 <c>0</c> and U+FDD0 as U+FDD0 <c>1</c>, after its type's length holds it as Calcite does, which
        /// truncates what it was given; any other is sent as it arrived.
        /// </summary>
        [Fact]
        public void AStoredParameterIsSentInTheStoredSpellingAfterItsLengthHoldsIt()
        {
            var query = new CosmosQuery("SELECT VALUE c.id FROM c WHERE (c.s = @p0) AND (c.t = @p1)",
                [new("@p0", new CosmosDynamicValue(0) { TypeName = SqlTypeName.VARCHAR, Length = 3 }), new("@p1", new CosmosDynamicValue(1))], Stored: ["@p0"]);

            var bound = CosmosQueries.Bind(query, new Context(("?0", "\0﷐\0x"), ("?1", "a\0")));
            bound.Parameters[0].Value.Should().Be("﷐0﷐1﷐0", "truncated to three units, then each escaped");
            bound.Parameters[1].Value.Should().Be("a\0", "a parameter not named stored is sent as it arrived");

            CosmosQueries.Bind(query, new Context(("?0", "abc"), ("?1", null))).Parameters[0].Value.Should().Be("abc", "a value holding neither is its own spelling");
        }

/// <remarks>
        /// A slot the context has no value for is a null parameter, not a shape.
        /// </remarks>
        [Fact]
        public void AnAbsentValueBindsAsNull()
        {
            var bound = CosmosQueries.Bind(
                Query(new CosmosParameter("@p0", new CosmosDynamicValue(0))),
                new Context());

            bound.Parameters[0].Value.Should().BeNull();
        }

        /// <summary>
        /// A guard's slot binds whether its value is there, and never the value: a UUID as Calcite boxes it is no type
        /// the service has.
        /// </summary>
        [Fact]
        public void APresenceSlotBindsTrueForAValueAndNullForNone()
        {
            CosmosQuery Bind(object? value) => CosmosQueries.Bind(
                Query(new CosmosParameter("@p0", new CosmosDynamicValue(0) { Presence = true })),
                value is null ? new Context() : new Context(("?0", value)));

            Bind(java.util.UUID.fromString("3a900000-0000-4000-8000-000000000001")).Parameters[0].Value.Should().Be(true);
            Bind("x").Parameters[0].Value.Should().Be(true);
            Bind(null).Parameters[0].Value.Should().BeNull();
        }

    }

}
