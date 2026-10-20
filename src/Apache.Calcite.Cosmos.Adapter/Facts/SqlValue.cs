using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// A value a filter reads or names: the typed IR's predicate subset, the half of a predicate that is not a test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A reading is a constructor of its own, not a SQL type used as a tag.</b> <see cref="Text"/> is the accessor's
    /// rendering of a path, <see cref="Raw"/> the value the service holds there, <see cref="Typed"/> a <c>RETURNING</c>,
    /// and a <see cref="Cast"/> converts one of them. The loader decides which, once, where Calcite types the same
    /// path <c>VARCHAR</c>, <c>ANY</c>, <c>VARIANT</c> or a <c>RETURNING</c> type depending on how it was written.
    /// What each means on every kind of stored value is <c>PredicateAtom</c>'s, in one place.
    /// </para>
    /// <para>
    /// <b>A literal is a JSON scalar</b> in the theory's spelling (<c>JsonValues</c>): <c>string|30</c>,
    /// <c>integer|30</c>, <c>boolean|true</c>. A parameter has no value, only a type and an ordinal.
    /// </para>
    /// </remarks>
    internal abstract record SqlValue
    {

        /// <summary><c>JSON_VALUE(DOC, path)</c> read as text: a scalar's rendering, and SQL null for anything else.</summary>
        public sealed record Text(JsonDocumentPath Path) : SqlValue
        {
            public override string ToString() => "text(" + Spell(Path) + ")";
        }

        /// <summary>The value the service holds at a path: a promoted column, an <c>ANY</c>-typed accessor, <c>id</c>.</summary>
        public sealed record Raw(JsonDocumentPath Path) : SqlValue
        {
            public override string ToString() => "raw(" + Spell(Path) + ")";
        }

        /// <summary><c>JSON_VALUE(DOC, path RETURNING type)</c>: the stored scalar, asserted to be of the type.</summary>
        public sealed record Typed(JsonDocumentPath Path, string Type) : SqlValue
        {
            public override string ToString() => "typed(" + Spell(Path) + " " + Type + ")";
        }

        /// <summary>A cast of a value to a SQL type: a number, or text over a raw value.</summary>
        public sealed record Cast(string Target, SqlValue Value) : SqlValue
        {
            public override string ToString() => "cast(" + Target + ", " + Value + ")";
        }

        /// <summary>A literal: a JSON scalar in the theory's spelling.</summary>
        public sealed record Literal(string Value) : SqlValue
        {

            /// <summary>Gets the literal's JSON type: <c>string</c>, <c>integer</c>, <c>fraction</c>, <c>boolean</c>, <c>null</c>.</summary>
            public string Kind => Saturation.JsonValues.TypeOf(Value);

            /// <summary>Gets whether the literal is a number.</summary>
            public bool IsNumber => Saturation.JsonValues.IsNumber(Value);

            public override string ToString() => "lit(" + Quote(Value) + ")";

        }

        /// <summary>A parameter, of whose value nothing is known: not even that it is not null.</summary>
        public sealed record Parameter(long Ordinal, string Type) : SqlValue
        {
            public override string ToString() => "?" + Ordinal + ":" + Type;
        }

        /// <summary>
        /// A function of values, as the loader reads one: the operator's name, and the operands that are values. A
        /// <c>TRIM</c>'s flag is spelled in its name, and a <c>CASE</c> is read as its arms, the conditions that choose
        /// one being no part of its value. What characters it holds the theory says, of the functions it knows
        /// (<c>StringCalls</c>), and of no other.
        /// </summary>
        public sealed record Call(string Function, IReadOnlyList<SqlValue> Operands) : SqlValue
        {

            public bool Equals(Call? other) => other is not null && Function == other.Function && Operands.SequenceEqual(other.Operands);

            public override int GetHashCode()
            {
                var hash = new HashCode();
                hash.Add(Function);
                foreach (var operand in Operands)
                    hash.Add(operand);

                return hash.ToHashCode();
            }

            public override string ToString() => Function + "(" + string.Join(", ", Operands) + ")";

        }

        /// <summary>
        /// Returns the literal of a scalar.
        /// </summary>
        public static Literal Of(JsonScalar value) => new(JsonFactEncoding.Value(value));

        /// <summary>
        /// Gets the document path the value reads, through any cast, or <c>null</c> where it reads none.
        /// </summary>
        public JsonDocumentPath? PathRead => this switch
        {
            Text t => t.Path,
            Raw r => r.Path,
            Typed t => t.Path,
            Cast c => c.Value.PathRead,
            _ => null,
        };

        /// <summary>
        /// Gets every document path the value reads, a call's operands' among them.
        /// </summary>
        public IEnumerable<JsonDocumentPath> PathsRead => this is Call call ? call.Operands.SelectMany(operand => operand.PathsRead) : PathRead is { } path ? [path] : [];

        /// <summary>
        /// Spells a path unambiguously: every name quoted, so a name holding a dot is not two names.
        /// </summary>
        internal static string Spell(JsonDocumentPath path)
        {
            var builder = new StringBuilder("$");
            foreach (var name in path.Names)
                builder.Append('[').Append(Quote(name)).Append(']');

            return builder.ToString();
        }

        /// <summary>
        /// Quotes text as a JSON string, so that what it spells cannot be read as anything around it.
        /// </summary>
        internal static string Quote(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    }

}
