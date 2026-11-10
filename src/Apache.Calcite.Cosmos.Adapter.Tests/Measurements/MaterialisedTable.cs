using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Data;

using org.apache.calcite.rel.type;
using org.apache.calcite.schema;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// An in-process table whose columns hold what the adapter's reader materialises, <c>CosmosJson.GetValue</c> over
    /// each value's JSON, beside a key <c>k</c> and the row's document as text, <c>d</c>.
    /// </summary>
    /// <remarks>
    /// A value is a JSON text, or <c>null</c> for an absent path, which the reader reads as SQL null. It is read when the
    /// table is scanned, so a value the reader refuses fails the statement, as it fails a query. No expression Calcite
    /// evaluates over a JSON literal yields a <c>VARIANT</c>, an <c>ANY</c> or a typed column's value, which is why the
    /// raw readings and the column reader are measured through this rather than over <c>VALUES</c>.
    /// </remarks>
    sealed class MaterialisedTable : org.apache.calcite.schema.impl.AbstractTable, ScannableTable
    {

        static int _named;

        readonly IReadOnlyList<(string Name, SqlTypeName Type)> _columns;
        readonly IReadOnlyList<(string Document, string?[] Values)> _rows;

        public MaterialisedTable(IReadOnlyList<(string Name, SqlTypeName Type)> columns, IReadOnlyList<(string Document, string?[] Values)> rows)
        {
            _columns = columns;
            _rows = rows;
        }

        /// <summary>A table of one column <c>v</c>, a row a value.</summary>
        public static MaterialisedTable Of(SqlTypeName type, IEnumerable<string?> values) =>
            new(new[] { ("v", type) }, values.Select(v => ("{}", new[] { v })).ToArray());

        /// <summary>A table of documents alone, read through <c>d</c>.</summary>
        public static MaterialisedTable Of(IEnumerable<string> documents) =>
            new(Array.Empty<(string, SqlTypeName)>(), documents.Select(d => (d, Array.Empty<string?>())).ToArray());

        /// <summary>What the reader makes of a value: the value, or the exception that fails the statement.</summary>
        public static object? Read(string? json, SqlTypeName type) =>
            json is null ? null : CosmosJson.GetValue(JsonDocument.Parse(json).RootElement.Clone(), type);

        public override RelDataType getRowType(RelDataTypeFactory typeFactory)
        {
            var builder = typeFactory.builder()
                .add("k", typeFactory.createSqlType(SqlTypeName.INTEGER))
                .add("d", typeFactory.createSqlType(SqlTypeName.VARCHAR));
            foreach (var (name, type) in _columns)
                builder = builder.add(name, typeFactory.createTypeWithNullability(typeFactory.createSqlType(type), true));

            return builder.build();
        }

        public org.apache.calcite.linq4j.Enumerable scan(org.apache.calcite.DataContext root)
        {
            var rows = new java.util.ArrayList(_rows.Count);
            for (var k = 0; k < _rows.Count; k++)
            {
                var row = new object?[_columns.Count + 2];
                row[0] = java.lang.Integer.valueOf(k);
                row[1] = _rows[k].Document;
                for (var c = 0; c < _columns.Count; c++)
                    row[c + 2] = Read(_rows[k].Values[c], _columns[c].Type);

                rows.add(row);
            }

            return org.apache.calcite.linq4j.Linq4j.asEnumerable(rows);
        }

        /// <summary>Opens a connection tables are asked over, with the function libraries <paramref name="fun"/> names, or the standard ones.</summary>
        public static DbConnection Open(string? fun = null)
        {
            var builder = new CalciteConnectionStringBuilder();
            if (fun is not null)
                builder.Fun = fun;

            var connection = new CalciteConnection(builder.ConnectionString);
            connection.Open();
            return connection;
        }

        /// <summary>
        /// Asks predicates of every row in one statement, each projected: a string a predicate of <c>T</c>, <c>F</c> and
        /// <c>U</c>, a letter a row in key order. A statement that fails throws.
        /// </summary>
        public string[] Ask(DbConnection connection, params string[] predicates)
        {
            var name = "m" + Interlocked.Increment(ref _named).ToString(CultureInfo.InvariantCulture);
            ((SchemaPlus)((CalciteConnection)connection).RootSchema).add(name, this);

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT \"k\", {string.Join(", ", predicates.Select(p => "(" + p + ")"))} FROM \"{name}\"";

            var letters = predicates.Select(_ => new char[_rows.Count]).ToArray();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var k = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
                for (var p = 0; p < predicates.Length; p++)
                    letters[p][k] = reader.IsDBNull(p + 1) ? 'U' : Convert.ToBoolean(reader.GetValue(p + 1), CultureInfo.InvariantCulture) ? 'T' : 'F';
            }

            return letters.Select(l => new string(l)).ToArray();
        }

        /// <summary>The keys of the rows a predicate keeps as a <c>WHERE</c>, in key order. A statement that fails throws.</summary>
        public int[] Keeps(DbConnection connection, string predicate)
        {
            var name = "m" + Interlocked.Increment(ref _named).ToString(CultureInfo.InvariantCulture);
            ((SchemaPlus)((CalciteConnection)connection).RootSchema).add(name, this);

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT \"k\" FROM \"{name}\" WHERE {predicate}";

            var kept = new List<int>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                kept.Add(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));

            return [.. kept.Order()];
        }

        /// <summary>
        /// What an expression is over each row, in key order, <c>null</c> where it is null; a row whose evaluation raises
        /// is asked alone, and its value is the innermost exception.
        /// </summary>
        public object?[] Values(DbConnection connection, string expression)
        {
            var name = "m" + Interlocked.Increment(ref _named).ToString(CultureInfo.InvariantCulture);
            ((SchemaPlus)((CalciteConnection)connection).RootSchema).add(name, this);

            var values = new object?[_rows.Count];
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT \"k\", ({expression}) FROM \"{name}\"";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    values[Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture)] = reader.IsDBNull(1) ? null : reader.GetValue(1);
            }
            catch (Exception)
            {
                for (var k = 0; k < _rows.Count; k++)
                {
                    try
                    {
                        using var command = connection.CreateCommand();
                        command.CommandText = $"SELECT ({expression}) FROM \"{name}\" WHERE \"k\" = {k}";
                        using var reader = command.ExecuteReader();
                        values[k] = reader.Read() && !reader.IsDBNull(0) ? reader.GetValue(0) : null;
                    }
                    catch (Exception e)
                    {
                        while (e.InnerException is Exception inner)
                            e = inner;

                        values[k] = e;
                    }
                }
            }

            return values;
        }

        /// <summary>
        /// Returns the innermost exception asking a predicate fails with, at validation, at code generation or at run
        /// time; or <c>null</c> where it answers.
        /// </summary>
        public Exception? Failure(DbConnection connection, string predicate)
        {
            try
            {
                Ask(connection, predicate);
                return null;
            }
            catch (Exception e)
            {
                while (e.InnerException is Exception inner)
                    e = inner;

                return e;
            }
        }

    }

}
