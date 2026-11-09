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

            /// <summary>
            /// The column the value is read through, where the loader knows it (<c>PredicateAtom.RawColumns</c>): a field
            /// declared <c>VARIANT</c>, <c>ANY</c> or <c>VARCHAR</c>. <c>null</c> reads every column's union (h12).
            /// </summary>
            public string? Column { get; init; }

            public override string ToString() => "raw(" + Spell(Path) + (Column is { } column ? " " + column : "") + ")";

        }

        /// <summary><c>JSON_VALUE(DOC, path RETURNING type)</c>: the stored scalar, asserted to be of the type.</summary>
        public sealed record Typed(JsonDocumentPath Path, string Type) : SqlValue
        {
            public override string ToString() => "typed(" + Spell(Path) + " " + Type + ")";
        }

        /// <summary>
        /// A cast of a value to a SQL type: a number, or text over a raw value; and, where S's reading reads what a number is
        /// compared with (G8), a cast to <c>DOUBLE</c> of a number as Calcite writes it, read as a term whose constant S's
        /// rules derive.
        /// </summary>
        public sealed record Cast(string Target, SqlValue Value) : SqlValue
        {
            public override string ToString() => "cast(" + Target + ", " + Value + ")";
        }

        /// <summary>
        /// A cast of a number to <c>DECIMAL(p, s)</c>, as Calcite writes one around a literal it compares with a number: read
        /// as a term only where S's reading reads what a number is compared with (G8), whether its whole digits fit and its
        /// scale is kept S's rules decide (<c>denotes</c>).
        /// </summary>
        public sealed record DecimalCast(long Precision, long Scale, SqlValue Value) : SqlValue
        {
            public override string ToString() => "cast(DECIMAL(" + Precision + ", " + Scale + "), " + Value + ")";
        }

        /// <summary>
        /// A value cast to <c>VARIANT</c>, as Calcite writes a text it boxes and a literal it compares with a <c>VARIANT</c>: the
        /// SQL type of what it boxes, and that value. What box it is held in, what it equals and where it is null are S's rules
        /// over <c>Calcite.VariantEqualsOnlyItsOwnBox</c>'s rows (<c>boxof</c>, <c>seesthrough</c>, G8); no other rule reads one.
        /// Read only where a loading reads places.
        /// </summary>
        public sealed record Box(string Type, SqlValue Value) : SqlValue
        {
            public override string ToString() => "box(" + Type + ", " + Value + ")";
        }

        /// <summary>
        /// <c>JSON_QUERY(DOC, path)</c> with no clause: what Calcite answers of each class of value is its row's
        /// (<c>Calcite.JsonQueryAnswersStructureOnly</c>, read as the reading's <c>calcitereads</c>), and a null test of it is
        /// written as the structure test its template says (G8). Read only where a loading reads places.
        /// </summary>
        public sealed record Query(JsonDocumentPath Path) : SqlValue
        {
            public override string ToString() => "query(" + Spell(Path) + ")";
        }

        /// <summary>
        /// SQL's null, as Calcite writes a literal of a type holding none: no JSON value, a JSON null being a literal of its
        /// own. What a comparison with it answers is S's rule over the operator's row (<c>strictop</c>, G8). Read only where a
        /// loading reads places.
        /// </summary>
        public sealed record Null(string Type) : SqlValue
        {
            public override string ToString() => "null(" + Type + ")";
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
        /// <c>TRIM</c>'s flag is spelled in its name. What characters it holds the theory says, of the functions it knows
        /// (<c>StringCalls</c>), and of no other.
        /// </summary>
        public sealed record Call(string Function, IReadOnlyList<SqlValue> Operands) : SqlValue
        {

            /// <summary>
            /// The call's SQL type where the loader reads it as a computed place S reads (h10), and <c>null</c> elsewhere;
            /// no part of its identity, which is its function and operands.
            /// </summary>
            public string? Type { get; init; }

            /// <summary>
            /// The place's spelling in S, the service's call over its operands' spellings (<c>CosmosFunctionSpellings</c>),
            /// where <see cref="Type"/> is set; no part of its identity.
            /// </summary>
            public string? Spelled { get; init; }

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
        /// A <c>CASE</c> of values: each condition, and the arm it takes, the last arm the else's. The facts graph reads it as
        /// its arms, a call of <c>CASE</c> (<c>PredicateWriter</c>), whose characters are any arm's; the conditions are kept
        /// for what reads its value (<c>IrEval</c>), and what chooses an arm is no part of its characters.
        /// </summary>
        public sealed record Case(IReadOnlyList<SqlPredicate> Conditions, IReadOnlyList<SqlValue> Arms) : SqlValue
        {

            public bool Equals(Case? other) => other is not null && Conditions.SequenceEqual(other.Conditions) && Arms.SequenceEqual(other.Arms);

            public override int GetHashCode()
            {
                var hash = new HashCode();
                foreach (var condition in Conditions)
                    hash.Add(condition);
                foreach (var arm in Arms)
                    hash.Add(arm);

                return hash.ToHashCode();
            }

            public override string ToString() => "CASE(" + string.Join(", ", Conditions.Select((c, i) => "when " + c + " then " + Arms[i]).Append("else " + Arms[^1])) + ")";

        }

        /// <summary>
        /// Returns the literal of a scalar.
        /// </summary>
        public static Literal Of(JsonScalar value) => new(JsonFactEncoding.Value(value));

        /// <summary>
        /// Gets the document path the value reads, through any cast or box, or <c>null</c> where it reads none.
        /// </summary>
        public JsonDocumentPath? PathRead => this switch
        {
            Text t => t.Path,
            Raw r => r.Path,
            Typed t => t.Path,
            Query q => q.Path,
            Cast c => c.Value.PathRead,
            Box b => b.Value.PathRead,
            _ => null,
        };

        /// <summary>
        /// Gets the paths an atom over the value reads: the path a reading reads, or every path a computed place's call
        /// reads (h10); none for anything else.
        /// </summary>
        public IEnumerable<JsonDocumentPath> PlacesRead => PathRead is { } path ? [path] : this is Call { Type: not null } ? PathsRead : [];

        /// <summary>Every call within a value read as a computed place, the value itself among them.</summary>
        public static IEnumerable<Call> ComputedWithin(SqlValue value) => value switch
        {
            Call call => call.Operands.SelectMany(ComputedWithin).Concat(call.Type is null ? [] : [call]),
            Cast cast => ComputedWithin(cast.Value),
            Box box => ComputedWithin(box.Value),
            _ => [],
        };

        /// <summary>
        /// Gets every document path the value reads, a call's operands' among them.
        /// </summary>
        public IEnumerable<JsonDocumentPath> PathsRead => this switch
        {
            Call call => call.Operands.SelectMany(operand => operand.PathsRead),
            Case value => value.Arms.SelectMany(arm => arm.PathsRead),
            _ => PathRead is { } path ? [path] : [],
        };

        /// <summary>
        /// Spells a path unambiguously: every name quoted, so a name holding a dot is not two names, and an element's index
        /// bare, so it is no name.
        /// </summary>
        internal static string Spell(JsonDocumentPath path)
        {
            var builder = new StringBuilder("$");
            foreach (var segment in path.Segments)
                builder.Append('[').Append(segment is string name ? Quote(name) : ((long)segment).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']');

            return builder.ToString();
        }

        /// <summary>
        /// Quotes text as a JSON string, so that what it spells cannot be read as anything around it.
        /// </summary>
        internal static string Quote(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    }

}
