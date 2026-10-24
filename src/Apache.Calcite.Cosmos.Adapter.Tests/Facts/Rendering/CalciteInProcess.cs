using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Facts.Saturation;

using org.apache.calcite.adapter.java;
using org.apache.calcite.jdbc;
using org.apache.calcite.rex;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// A filter's conjunct evaluated by Calcite in process over one document (spec g05, line 1c): its node compiled by
    /// Calcite's own executor, each field it reads filled as the reader fills it through the filter's binding, the document
    /// column with the document's text. What IrEval cannot judge — an opaque conjunct — Calcite answers here.
    /// </summary>
    internal static class CalciteInProcess
    {

        /// <summary>
        /// A conjunct compiled once through its binding, to be asked of each document: Calcite's truth there, TRUE, FALSE or
        /// UNKNOWN, a raise where it throws; <c>null</c> where the document is no row the binding reads, a field its type
        /// holds never null being absent there. <c>null</c> itself where a field it reads is read otherwise than as its type
        /// or as the document, which this does not fill, or where Calcite cannot compile it alone.
        /// </summary>
        public static Func<J.Obj, Truths?>? Compile(RexNode conjunct, IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading> readings)
        {
            var refs = new Dictionary<int, RexInputRef>();
            Collect(conjunct, refs);
            // The document column is the document's text however it is read; any other field is filled as its type alone.
            if (refs.Keys.Any(i => i >= fields.Count || fields[i] is not { } path || path.IsRoot == false && readings.Count > i && readings[i] != CosmosReading.Typed))
                return null;

            var (width, types) = (refs.Count == 0 ? 1 : refs.Keys.Max() + 1, new JavaTypeFactoryImpl());
            var row = types.builder();
            for (var i = 0; i < width; i++)
                row.add("f" + i, refs.TryGetValue(i, out var r) ? r.getType() : types.createTypeWithNullability(types.createSqlType(SqlTypeName.ANY), true));

            RexExecutable executable;
            try
            {
                executable = RexExecutorImpl.getExecutable(new RexBuilder(types), java.util.Collections.singletonList(conjunct), row.build());
            }
            catch (Exception)
            {
                return null;
            }

            return document =>
            {
                var record = new object?[width];
                foreach (var (index, reference) in refs)
                {
                    // The reader refuses a value its column's type does not hold, which fails the statement: a raise.
                    try
                    {
                        record[index] = fields[index]!.IsRoot ? Documents.Json(document) : Read(fields[index]!, document, reference.getType().getSqlTypeName());
                    }
                    catch (CosmosMaterializationException)
                    {
                        return Truths.Raises;
                    }

                    if (record[index] is null && reference.getType().isNullable() == false)
                        return null;
                }

                try
                {
                    executable.setDataContext(new Record(record, types));
                    return executable.execute()?[0] switch
                    {
                        java.lang.Boolean b => b.booleanValue() ? Truths.True : Truths.False,
                        null => Truths.Unknown,
                        var other => throw new InvalidOperationException($"{conjunct} answered {other}"),
                    };
                }
                catch (Exception e) when (e is not InvalidOperationException)
                {
                    return Truths.Raises;
                }
            };
        }

        /// <summary>What the reader makes of the value at a path: the value read as the field's type, or null where it is absent.</summary>
        static object? Read(CosmosPath path, J.Obj document, SqlTypeName type)
        {
            J? value = document;
            foreach (var segment in path.Segments)
                value = segment.IsIndex ? value is J.Arr a && segment.ArrayIndex < a.Elements.Count ? a.Elements[segment.ArrayIndex] : null
                    : value is J.Obj o && o.Properties.TryGetValue(segment.Name!, out var child) ? child : null;

            return value is null ? null : CosmosJson.GetValue(JsonDocument.Parse(Documents.Json(value)).RootElement.Clone(), type);
        }

        static void Collect(RexNode node, Dictionary<int, RexInputRef> refs)
        {
            if (node is RexInputRef reference)
                refs.TryAdd(reference.getIndex(), reference);
            else if (node is RexCall call)
                for (var i = 0; i < call.getOperands().size(); i++)
                    Collect((RexNode)call.getOperands().get(i), refs);
        }

        /// <summary>The literals a conjunct compares with, in the theory's spelling: what source A draws documents beside.</summary>
        public static IEnumerable<string> Literals(RexNode node) => node switch
        {
            RexLiteral { } literal when literal.getValue() is org.apache.calcite.util.NlsString text => [JsonValues.Of(text.getValue())],
            RexLiteral { } literal when literal.getValue() is java.math.BigDecimal number
                && decimal.TryParse(number.toPlainString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => [JsonValues.Of(d)],
            RexCall call => Enumerable.Range(0, call.getOperands().size()).SelectMany(i => Literals((RexNode)call.getOperands().get(i))),
            _ => [],
        };

        /// <summary>The input record Calcite's executor reads a conjunct's fields from.</summary>
        sealed class Record(object?[] record, JavaTypeFactoryImpl types) : org.apache.calcite.DataContext
        {

            public org.apache.calcite.schema.SchemaPlus getRootSchema() => null!;

            public JavaTypeFactory getTypeFactory() => types;

            public org.apache.calcite.linq4j.QueryProvider getQueryProvider() => null!;

            public object? get(string name) => name == "inputRecord" ? record : null;

        }

    }

}
