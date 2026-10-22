using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// A predicate over one path: how a value is read from the path, and what is asked of what is read. The atom is the
    /// meaning of a <see cref="PredicateRules"/> comparison, membership or null test, gathered from its terms into one
    /// value that two questions are asked of: which documents a row with a given truth value can be
    /// (<see cref="Kept"/>), and which truth values the documents a context admits can give it (<see cref="Over"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both answers over-approximate, and that is what makes them sound.</b> <see cref="Kept"/> may name documents a
    /// row cannot be — it is what an assumed predicate establishes, so a wider answer is a weaker fact.
    /// <see cref="Over"/> may name truth values no row takes — it is what a verdict is read from, so a wider answer is
    /// a verdict not reached. Where a reading's behaviour has not been measured the answer is the widest, and a
    /// literal of a kind the reading is never compared with answers the same: the loader does not build one, and
    /// knowing nothing is the safe way to meet one anyway.
    /// </para>
    /// <para>
    /// <b>A row Calcite raises on is no truth value</b> (<see cref="Truths.Raises"/>). A <c>RETURNING</c> asserts its
    /// type and throws on anything else, and a cast throws on a string it cannot parse. Such a row is not one a
    /// predicate keeps, so it constrains nothing an assumed predicate establishes: that is the asymmetry
    /// <c>DESIGN.md</c> records for <c>RETURNING</c>, the caller's declaration held rather than checked. It supports no
    /// verdict either, because a verdict that deleted the predicate would answer where Calcite fails.
    /// </para>
    /// <para>
    /// <b>The readings</b>, by name: <c>text</c> is <c>JSON_VALUE</c> read as text, whose rendering of a scalar is the
    /// accessor's and which answers SQL null for an absent path, a null, an object and an array. <c>raw</c> is the value
    /// the service holds: a promoted column, the split rule's <c>ANY</c> marker, <c>id</c>. <c>typed:T</c> is a
    /// <c>RETURNING T</c>. <c>cast:T:r</c> is a cast to <c>T</c> over reading <c>r</c>: a numeric cast, or the cast to
    /// text over a raw value, which renders an object and an array as well.
    /// </para>
    /// </remarks>
    /// <param name="Reader">How the value is read: <c>text</c>, <c>raw</c>, <c>typed:T</c> or <c>cast:T:r</c>.</param>
    /// <param name="Test">What is asked: a comparison operator, <c>in</c>, <c>is null</c>, or <c>is defined</c>.</param>
    /// <param name="Operand">The literal compared with, in <see cref="JsonValues"/>' spelling; <c>null</c> for a parameter and a null test.</param>
    /// <param name="Points">The literals of an <c>in</c>.</param>
    /// <param name="Parameter">Whether the value is compared with a parameter, whose value is not known.</param>
    internal sealed record PredicateAtom(string Reader, string Test, string? Operand, ValueSet<string>? Points, bool Parameter)
    {

        public const string Equal = "=";
        public const string NotEqual = "<>";
        public const string Less = "<";
        public const string LessOrEqual = "<=";
        public const string Greater = ">";
        public const string GreaterOrEqual = ">=";
        public const string InTest = "in";
        public const string NullTest = "is null";
        public const string DefinedTest = "is defined";

        public const string TextReader = "text";
        public const string RawReader = "raw";

        /// <summary>Returns the reader of a <c>RETURNING</c> of a SQL type.</summary>
        public static string TypedReader(string type) => "typed:" + type;

        /// <summary>Returns the reader of a cast to a SQL type over another reader.</summary>
        public static string CastReader(string target, string inner) => "cast:" + target + ":" + inner;

        /// <summary>An integer type's limits, or <c>null</c>: what its readings raise or saturate past, and S's cells are cut at.</summary>
        public static (decimal Least, decimal Most)? IntegerLimits(string type) => type switch
        {
            "TINYINT" => (sbyte.MinValue, sbyte.MaxValue),
            "SMALLINT" => (short.MinValue, short.MaxValue),
            "INTEGER" => (int.MinValue, int.MaxValue),
            "BIGINT" => (long.MinValue, long.MaxValue),
            _ => null,
        };

        /// <summary>A comparison with a literal.</summary>
        public static PredicateAtom Compare(string reader, string op, string literal) => new(reader, op, literal, null, false);

        /// <summary>A comparison with a parameter.</summary>
        public static PredicateAtom CompareParameter(string reader, string op) => new(reader, op, null, null, true);

        /// <summary>A membership in a set of literals.</summary>
        public static PredicateAtom In(string reader, ValueSet<string> points) => new(reader, InTest, null, points, false);

        /// <summary>A null test.</summary>
        public static PredicateAtom IsNull(string reader) => new(reader, NullTest, null, null, false);

        /// <summary>
        /// <c>IS_DEFINED</c> of the path the reader reads: what it asks is the document's, so it means one thing whatever
        /// the reading (<c>Service.IsDefined</c>, and the in-process body reads the document too).
        /// </summary>
        public static PredicateAtom IsDefined(string reader) => new(reader, DefinedTest, null, null, false);

        /// <summary>Whether a string names a comparison operator.</summary>
        public static bool IsComparison(string op) => op is Equal or NotEqual or Less or LessOrEqual or Greater or GreaterOrEqual;

        /// <summary>The operator whose truth is this one's falsehood, on a row where either is known.</summary>
        public static string Negate(string op) => op switch
        {
            Equal => NotEqual,
            NotEqual => Equal,
            Less => GreaterOrEqual,
            LessOrEqual => Greater,
            Greater => LessOrEqual,
            GreaterOrEqual => Less,
            _ => throw new ArgumentOutOfRangeException(nameof(op), op, null),
        };

        /// <summary>The operator that means the same with its operands the other way round.</summary>
        public static string Mirror(string op) => op switch
        {
            Less => Greater,
            LessOrEqual => GreaterOrEqual,
            Greater => Less,
            GreaterOrEqual => LessOrEqual,
            _ => op,
        };

        /// <summary>
        /// The readings by name, parsed once: a record's fields are its equality, so the reading is not one of them.
        /// </summary>
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Reading?> Parsed = new(StringComparer.Ordinal);

        Reading? Reads => Parsed.GetOrAdd(Reader, Reading.Parse);

        /// <summary>
        /// Returns what a path holds in every document on which the atom takes the given truth value: the documents a
        /// row it keeps (<paramref name="truth"/> <c>true</c>), or a row its negation keeps, can be.
        /// </summary>
        /// <param name="truth">Whether the atom is TRUE of the row, or FALSE.</param>
        /// <returns>An over-approximation of those documents.</returns>
        public PathValues Kept(bool truth)
        {
            // True where the path is there, a null included, and false only where it is not.
            if (Test == DefinedTest)
                return truth ? PathValues.AnythingThere : PathValues.OfKinds(ValueSet.Of(PathValues.AbsentKind));

            if (Reads is not Reading reading)
                return PathValues.Anything;

            if (Parameter)
                return reading.KeptParameter();

            switch (Test)
            {
                case Equal:
                    return truth ? reading.KeptEqual(Operand!) : reading.KeptUnequal(Operand!);
                case NotEqual:
                    return truth ? reading.KeptUnequal(Operand!) : reading.KeptEqual(Operand!);
                case Less or LessOrEqual or Greater or GreaterOrEqual:
                    return reading.KeptOrder(truth ? Test : Negate(Test), Operand!);
                case InTest when Points is { Count: > 0 }:
                    return truth
                        ? Points.Select(reading.KeptEqual).Aggregate(PathValues.Join)
                        : Points.Select(reading.KeptUnequal).Aggregate(PathValues.Meet);
                case NullTest:
                    return reading.KeptNull(truth);
                default:
                    return PathValues.Anything;
            }
        }

        /// <summary>
        /// Returns the truth values the atom may take over the documents a path's knowledge admits.
        /// </summary>
        /// <param name="possible">What the path may hold.</param>
        /// <returns>An over-approximation of those truth values; <see cref="Truths.None"/> where no document is admitted.</returns>
        public Truths Over(PathValues possible)
        {
            var truths = Truths.None;
            foreach (var state in State.Enumerate(possible))
            {
                truths |= On(state);
                if ((truths & Truths.Any) == Truths.Any)
                    break;
            }

            return truths;
        }

        /// <summary>Whether the atom is TRUE of every document the knowledge admits.</summary>
        public bool AlwaysTrue(PathValues possible) => (Over(possible) & ~Truths.True) == Truths.None;

        /// <summary>Whether the atom is FALSE, never UNKNOWN, of every document the knowledge admits.</summary>
        public bool AlwaysFalse(PathValues possible) => (Over(possible) & ~Truths.False) == Truths.None;

        /// <summary>Whether the atom is TRUE of no document the knowledge admits, and raises on none.</summary>
        public bool NeverTrue(PathValues possible) => (Over(possible) & (Truths.True | Truths.Raises)) == Truths.None;

        /// <summary>The truth values the atom may take on one kind of document.</summary>
        internal Truths On(State state)
        {
            if (Test == DefinedTest)
                return state is State.Absent ? Truths.False : state is State.Unknown ? Truths.True | Truths.False : Truths.True;

            if (Reads is not Reading reading || state is State.Unknown)
                return Truths.Any;

            if (Parameter)
                return reading.Parameter(state);

            switch (Test)
            {
                case Equal:
                    return reading.Equal(state, Operand!);
                case NotEqual:
                    return Swap(reading.Equal(state, Operand!));
                case Less or LessOrEqual or Greater or GreaterOrEqual:
                    return reading.Order(state, Test, Operand!);
                case InTest when Points is { Count: > 0 }:
                    return Points.Select(point => reading.Equal(state, point)).Aggregate(Or);
                case NullTest:
                    return reading.Null(state);
                default:
                    return Truths.Any;
            }
        }

        /// <summary>
        /// The truth values the atom may take on one cell of a path's import: <see cref="On"/>'s, through the reading's cell
        /// methods. The rendering check's Calcite side, which the facts graph never reads (<c>DESIGN.md</c>).
        /// </summary>
        internal Truths OnCell(PathCell cell)
        {
            if (Test == DefinedTest || Reads is not Reading reading || cell is PathCell.Unknown)
                return On(cell.Coarse);

            // A comparison with a parameter reads the value as the null test does: it raises where that read does, and is
            // anything where that read finds a value.
            if (Parameter)
                return reading.Parameter(cell.Coarse) | (reading.NullOn(cell) & Truths.Raises) | ((reading.NullOn(cell) & Truths.False) != 0 ? Truths.True | Truths.False | Truths.Unknown : 0);

            switch (Test)
            {
                case Equal:
                    return reading.EqualOn(cell, Operand!);
                case NotEqual:
                    return Swap(reading.EqualOn(cell, Operand!));
                case Less or LessOrEqual or Greater or GreaterOrEqual:
                    return reading.OrderOn(cell, Test, Operand!);
                case InTest when Points is { Count: > 0 }:
                    return Points.Select(point => reading.EqualOn(cell, point)).Aggregate(Or);
                case NullTest:
                    return reading.NullOn(cell);
                default:
                    return On(cell.Coarse);
            }
        }

        /// <summary>The <c>Calcite.*</c> rows the reading's answers on a cell rest on.</summary>
        internal ValueSet<string> Rests => Test != DefinedTest && Reads is Reading reading ? reading.Rests : ValueSet<string>.Empty;

        /// <summary>The truth values of <c>NOT</c> over these.</summary>
        public static Truths Swap(Truths truths) =>
            (truths & (Truths.Unknown | Truths.Raises)) | ((truths & Truths.True) != 0 ? Truths.False : 0) | ((truths & Truths.False) != 0 ? Truths.True : 0);

        /// <summary>The truth values of an <c>OR</c> of two predicates of one row, taking these; it may raise where either may.</summary>
        public static Truths Or(Truths a, Truths b)
        {
            var result = (a | b) & Truths.Raises;
            foreach (var x in Members(a))
                foreach (var y in Members(b))
                    result |= x == Truths.True || y == Truths.True ? Truths.True
                        : x == Truths.False && y == Truths.False ? Truths.False
                        : Truths.Unknown;

            return result;
        }

        static IEnumerable<Truths> Members(Truths truths)
        {
            if ((truths & Truths.True) != 0)
                yield return Truths.True;
            if ((truths & Truths.False) != 0)
                yield return Truths.False;
            if ((truths & Truths.Unknown) != 0)
                yield return Truths.Unknown;
        }

        /// <summary>
        /// One kind of document, as far as a path is concerned: what the documents a path's knowledge admits are
        /// enumerated as. A scalar a domain names is itself; otherwise a kind stands for every value of it the knowledge
        /// allows.
        /// </summary>
        internal abstract record State
        {

            public sealed record Absent : State;

            public sealed record Null : State;

            /// <summary>A scalar other than null, in <see cref="JsonValues"/>' spelling.</summary>
            public sealed record Scalar(string Value) : State;

            /// <summary>Any string but these.</summary>
            public sealed record AnyString(ValueSet<string> Except) : State;

            /// <summary>Any number of the kind within the bounds, which bind its value whether a decimal holds it or not.</summary>
            public sealed record AnyNumber(bool Integral, Bound? Lower, Bound? Upper) : State;

            /// <summary>An object or an array.</summary>
            public sealed record Structure(string Type) : State;

            /// <summary>A kind this does not know, of which nothing is said.</summary>
            public sealed record Unknown : State;

            public static readonly State AbsentState = new Absent();

            public static readonly State NullState = new Null();

            public static IEnumerable<State> Enumerate(PathValues x)
            {
                foreach (var kind in x.Kinds)
                {
                    switch (kind)
                    {
                        case PathValues.AbsentKind:
                            yield return AbsentState;
                            break;

                        case NullType:
                            if (x.Admits(NullValue))
                                yield return NullState;
                            break;

                        case ObjectType or ArrayType:
                            // A domain is a set of scalars, so a path with one holds no structure.
                            if (x.Values is null)
                                yield return new Structure(kind);
                            break;

                        case BooleanType:
                            foreach (var value in new[] { Of(true), Of(false) })
                                if (x.Admits(value))
                                    yield return new Scalar(value);
                            break;

                        case StringType or IntegerType or FractionType when x.Values is not null:
                            foreach (var value in x.Values)
                                if (TypeOf(value) == kind)
                                    yield return new Scalar(value);
                            break;

                        case StringType:
                            yield return new AnyString(ValueSet.From(x.Excluded.Where(IsString)));
                            break;

                        case IntegerType or FractionType:
                            yield return new AnyNumber(kind == IntegerType, x.Lower, x.Upper);
                            break;

                        default:
                            yield return new Unknown();
                            break;
                    }
                }
            }

        }

        /// <summary>
        /// How one reading of a path answers each test, on each kind of document, and what a row on which it answers says
        /// of the document.
        /// </summary>
        abstract class Reading
        {

            const Truths T = Truths.True;
            const Truths F = Truths.False;
            const Truths U = Truths.Unknown;
            const Truths R = Truths.Raises;

            public static Reading? Parse(string reader)
            {
                switch (reader)
                {
                    case TextReader:
                        return new Text(structures: false);
                    case RawReader:
                        return new Raw();
                }

                if (reader.StartsWith("typed:", StringComparison.Ordinal))
                    return Typed.For(reader.Substring("typed:".Length));

                if (reader.StartsWith("cast:", StringComparison.Ordinal))
                {
                    var rest = reader.Substring("cast:".Length);
                    var colon = rest.IndexOf(':');
                    if (colon < 0)
                        return null;

                    var target = rest.Substring(0, colon);
                    var inner = rest.Substring(colon + 1);
                    return (target, inner) switch
                    {
                        ("VARCHAR" or "CHAR", RawReader) => new Text(structures: true),
                        ("VARCHAR" or "CHAR", TextReader) => new Text(structures: false),
                        ("INTEGER" or "BIGINT" or "DOUBLE", TextReader or RawReader) => new NumberCast(target, inner == TextReader),
                        _ => null,
                    };
                }

                return null;
            }

            /// <summary>The truth values of an equality between the value read and a literal.</summary>
            public abstract Truths Equal(State state, string literal);

            /// <summary>The truth values of an ordering between the value read and a literal.</summary>
            public abstract Truths Order(State state, string op, string literal);

            /// <summary>The truth values of <c>IS NULL</c> of the value read.</summary>
            public abstract Truths Null(State state);

            /// <summary>The truth values of any comparison of the value read with a parameter, which may itself be null.</summary>
            public abstract Truths Parameter(State state);

            /// <summary>What a path holds where an equality with the literal is TRUE.</summary>
            public abstract PathValues KeptEqual(string literal);

            /// <summary>What a path holds where an equality with the literal is FALSE.</summary>
            public abstract PathValues KeptUnequal(string literal);

            /// <summary>What a path holds where an ordering with the literal is TRUE.</summary>
            public abstract PathValues KeptOrder(string op, string literal);

            /// <summary>What a path holds where <c>IS NULL</c> takes the truth value.</summary>
            public abstract PathValues KeptNull(bool truth);

            /// <summary>What a path holds where a comparison with a parameter is TRUE or FALSE.</summary>
            public abstract PathValues KeptParameter();

            /// <summary>The rows the cell methods rest on.</summary>
            public virtual ValueSet<string> Rests => ValueSet<string>.Empty;

            // A reading's answers on a cell: by default, its answers on the state the cell lies in.
            public virtual Truths EqualOn(PathCell cell, string literal) => Equal(cell.Coarse, literal);

            public virtual Truths OrderOn(PathCell cell, string op, string literal) => Order(cell.Coarse, op, literal);

            public virtual Truths NullOn(PathCell cell) => Null(cell.Coarse);

            /// <summary>Whether a comparison holds of a value that compares with the literal as the sign says.</summary>
            static bool Ordered(string op, int sign) => op switch
            {
                PredicateAtom.Equal => sign == 0,
                Less => sign < 0,
                LessOrEqual => sign <= 0,
                Greater => sign > 0,
                _ => sign >= 0,
            };

            /// <summary>What a comparison answers of values on these sides of the literal, beside what else it may; either where a side is not told.</summary>
            static Truths Placed(string op, IEnumerable<int?> sides, Truths beside) => sides.Aggregate(beside, (truths, side) => truths | (side is int sign ? Ordered(op, sign) ? T : F : T | F));

            /// <summary>
            /// <c>JSON_VALUE</c> read as text, and the cast to text over a raw value, which renders an object and an array
            /// as well and whose rendering of a null has not been measured.
            /// </summary>
            /// <remarks>
            /// A string renders as itself and a boolean as Calcite's lowercase <c>true</c> or <c>false</c>. A number
            /// renders as Java spells it, which is one of several texts with the number's value: a stored <c>30</c> reads
            /// <c>30</c> and a stored <c>30.0</c> reads <c>30.0</c>, and the theory spells both <c>integer|30</c>. So a
            /// number whose value is the text's may render as the text or not, and one whose value is not never does.
            /// That is the rendering preimage: <c>'30'</c> admits the string <c>"30"</c> and the number 30, and a declared
            /// string leaves only the first.
            /// </remarks>
            sealed class Text : Reading
            {

                readonly bool _structures;

                public Text(bool structures)
                {
                    _structures = structures;
                }

                ValueSet<string> Renders => _structures ? PathValues.Rendered.Add(ObjectType).Add(ArrayType).Add(NullType) : PathValues.Rendered;

                public override Truths Equal(State state, string literal)
                {
                    if (IsString(literal) == false)
                        return Truths.Any;

                    var text = StringOf(literal);
                    return state switch
                    {
                        State.Absent => U,
                        State.Null => _structures ? (text == "null" ? Truths.Any : F | U) : U,
                        State.Structure s => _structures ? (Renderings.MayRenderStructure(s.Type, text) ? T | F : F) : U,
                        State.Scalar { Value: var v } when IsString(v) => StringOf(v) == text ? T : F,
                        State.Scalar { Value: var v } when TypeOf(v) == BooleanType => Renderings.BooleanText(v) == text ? T : F,
                        State.Scalar { Value: var v } when IsNumber(v) => Renderings.MayRenderAs(v, text) ? T | F : F,
                        State.AnyString s => s.Except.Contains(Of(text)) ? F : T | F,
                        State.AnyNumber n => Renderings.MayRenderWithin(text, n.Integral, n.Lower, n.Upper) ? T | F : F,
                        _ => Truths.Any,
                    };
                }

                public override Truths Order(State state, string op, string literal)
                {
                    if (IsString(literal) == false)
                        return Truths.Any;

                    // The renderings compare as text, by Calcite's string order, which has not been measured against
                    // anything here: a row that renders is TRUE or FALSE, and nothing finer is said.
                    return state switch
                    {
                        State.Absent => U,
                        State.Null => _structures ? Truths.Any : U,
                        State.Structure => _structures ? T | F : U,
                        _ => T | F,
                    };
                }

                public override Truths Null(State state) => state switch
                {
                    State.Absent => T,
                    State.Null => _structures ? T | F : T,
                    State.Structure => _structures ? F : T,
                    _ => F,
                };

                public override Truths Parameter(State state) => state switch
                {
                    State.Absent => U,
                    State.Null or State.Structure => _structures ? Truths.Any : U,
                    _ => Truths.Any,
                };

                public override PathValues KeptEqual(string literal)
                {
                    if (IsString(literal) == false)
                        return PathValues.Anything;

                    var text = StringOf(literal);
                    var kinds = new List<string> { StringType };
                    var values = new List<string> { Of(text) };
                    var finite = true;

                    if (text is "true" or "false")
                    {
                        kinds.Add(BooleanType);
                        values.Add(Of(text == "true"));
                    }

                    if (Renderings.MayBeNumberText(text))
                    {
                        if (Renderings.TryExactNumber(text, out var number))
                        {
                            var value = Of(number);
                            kinds.Add(TypeOf(value));
                            values.Add(value);
                        }
                        else
                        {
                            kinds.Add(IntegerType);
                            kinds.Add(FractionType);
                            finite = false;
                        }
                    }

                    if (_structures)
                    {
                        if (text.StartsWith('['))
                        {
                            kinds.Add(ArrayType);
                            finite = false;
                        }

                        if (text.StartsWith('{'))
                        {
                            kinds.Add(ObjectType);
                            finite = false;
                        }

                        if (text == "null")
                        {
                            kinds.Add(NullType);
                            values.Add(NullValue);
                        }
                    }

                    var kept = PathValues.OfKinds(ValueSet.From(kinds));
                    return finite ? PathValues.Meet(kept, PathValues.OneOf(ValueSet.From(values))) : kept;
                }

                public override PathValues KeptUnequal(string literal)
                {
                    if (IsString(literal) == false)
                        return PathValues.Anything;

                    // The string the text spells always renders as it, so never makes the equality false.
                    return PathValues.Meet(PathValues.OfKinds(Renders), PathValues.Not(literal));
                }

                public override PathValues KeptOrder(string op, string literal) =>
                    IsString(literal) ? PathValues.OfKinds(Renders) : PathValues.Anything;

                public override PathValues KeptNull(bool truth) => truth
                    ? PathValues.OfKinds(_structures ? ValueSet.Of(PathValues.AbsentKind, NullType) : ValueSet.Of(PathValues.AbsentKind, NullType, ObjectType, ArrayType))
                    : PathValues.OfKinds(Renders);

                public override PathValues KeptParameter() => PathValues.OfKinds(Renders);

                static readonly ValueSet<string> Rows = ValueSet.Of("Calcite.JsonValueRendersScalarsAsText", "Calcite.JsonValueReadsStructureAsNull", "Calcite.TextOrderIsByCodeUnit");

                public override ValueSet<string> Rests => Rows;

                /// <summary>An interval of strings holds the literal or not; anything else answers as its state.</summary>
                public override Truths EqualOn(PathCell cell, string literal) =>
                    cell is PathCell.Strings && IsString(literal) ? (cell.Contains(literal) ? T | F : F) : Equal(cell.Coarse, literal);

                /// <summary>
                /// Text orders by UTF-16 unit (<c>Calcite.TextOrderIsByCodeUnit</c>): a string or a boolean is placed exactly, an
                /// interval wholly to one side of the literal is placed there, and a number by the first unit of its rendering,
                /// which is <c>-</c> or a digit whatever its spelling (<c>Calcite.JsonValueRendersScalarsAsText</c>).
                /// </summary>
                public override Truths OrderOn(PathCell cell, string op, string literal)
                {
                    if (IsString(literal) == false)
                        return Order(cell.Coarse, op, literal);

                    var text = StringOf(literal);
                    int? side = cell switch
                    {
                        PathCell.Point { Value: var v } when IsString(v) => Math.Sign(string.CompareOrdinal(StringOf(v), text)),
                        PathCell.Point { Value: var v } when TypeOf(v) == BooleanType => Math.Sign(string.CompareOrdinal(Renderings.BooleanText(v), text)),
                        PathCell.Point { Value: var v } when IsNumber(v) => FirstUnit(text),
                        PathCell.Numbers => FirstUnit(text),
                        PathCell.Strings { High: string high } when string.CompareOrdinal(StringOf(high), text) <= 0 => -1,
                        PathCell.Strings { Low: string low } when string.CompareOrdinal(StringOf(low), text) >= 0 => 1,
                        _ => null,
                    };

                    return side is int sign ? (Ordered(op, sign) ? T : F) : Order(cell.Coarse, op, literal);
                }

                /// <summary>
                /// Which side of text every rendering of a number lies: above where the text is empty or starts below
                /// <c>+</c>, below where it starts above <c>9</c>, and either otherwise.
                /// </summary>
                static int? FirstUnit(string text) => text.Length == 0 || text[0] < '+' ? 1 : text[0] > '9' ? -1 : null;

            }

            /// <summary>
            /// The value the service holds, compared as a value of its own type.
            /// </summary>
            /// <remarks>
            /// Strings and booleans compare as themselves. Numbers compare as Calcite compares two <c>VARIANT</c>s of
            /// possibly different numeric types, which has not been measured: an equal number may or may not be equal,
            /// and an unequal one is not, or the comparison is unknown. A value of another type is not equal, or the
            /// comparison is unknown, for the same reason.
            /// </remarks>
            sealed class Raw : Reading
            {

                public override Truths Equal(State state, string literal) => state switch
                {
                    State.Absent => U,
                    State.Null or State.Structure => F | U,
                    State.Scalar { Value: var v } when IsNumber(literal) => IsNumber(v) && Renderings.SameNumber(v, literal) ? Truths.Any : F | U,
                    State.Scalar { Value: var v } => v == literal ? T : TypeOf(v) == TypeOf(literal) ? F : F | U,
                    State.AnyString s when IsString(literal) => s.Except.Contains(literal) ? F : T | F,
                    State.AnyNumber n when IsNumber(literal) => Renderings.NumberWithin(literal, n.Integral, n.Lower, n.Upper) ? Truths.Any : F | U,
                    State.AnyString or State.AnyNumber => F | U,
                    _ => Truths.Any,
                };

                public override Truths Order(State state, string op, string literal) => state is State.Absent ? U : Truths.Any;

                public override Truths Null(State state) => state switch
                {
                    State.Absent => T,
                    State.Null => T | F,
                    _ => F,
                };

                public override Truths Parameter(State state) => state is State.Absent ? U : Truths.Any;

                public override PathValues KeptEqual(string literal) =>
                    TypeOf(literal) == NullType ? PathValues.Anything : PathValues.Meet(PathValues.OfKinds(ValueSet.Of(TypeOf(literal))), PathValues.OneOf(ValueSet.Of(literal)));

                public override PathValues KeptUnequal(string literal) =>
                    IsNumber(literal) || TypeOf(literal) == NullType ? PathValues.AnythingThere : PathValues.Meet(PathValues.AnythingThere, PathValues.Not(literal));

                public override PathValues KeptOrder(string op, string literal) => PathValues.AnythingThere;

                public override PathValues KeptNull(bool truth) =>
                    truth ? PathValues.OfKinds(ValueSet.Of(PathValues.AbsentKind, NullType)) : PathValues.AnythingThere;

                public override PathValues KeptParameter() => PathValues.AnythingThere;

                static readonly ValueSet<string> Rows = ValueSet.Of("Calcite.RawStringEqualityIsOrdinal", "Calcite.RawNullIsSqlNull");

                public override ValueSet<string> Rests => Rows;

                /// <summary>
                /// The union of the three columns a raw value is read through (<c>Calcite.RawStringEqualityIsOrdinal</c>,
                /// <c>Calcite.RawNullIsSqlNull</c>): a null is SQL null in each, a string is equal to the one with its units
                /// alone, and a value of another type is unequal in a <c>VARIANT</c> and an <c>ANY</c> and fails a
                /// <c>VARCHAR</c> column's read — which is raw beside a string literal alone. A number literal is left coarse:
                /// a <c>VARIANT</c> is equal to a number of its own SQL type alone, which the IR does not keep.
                /// </summary>
                public override Truths EqualOn(PathCell cell, string literal) => IsNumber(literal) || TypeOf(literal) == NullType ? Equal(cell.Coarse, literal) : cell switch
                {
                    PathCell.Absent or PathCell.Null => U,
                    PathCell.Point { Value: var v } when TypeOf(v) == TypeOf(literal) => v == literal ? T : F,
                    PathCell.Strings => IsString(literal) && cell.Contains(literal) ? T | F : F,
                    PathCell.Point { Value: var v } when IsString(v) => F,
                    _ => IsString(literal) ? F | R : F,
                };

                /// <summary>A <c>VARIANT</c> has no ordering, and an <c>ANY</c> raises on one across types: the state's answer, or the raise.</summary>
                public override Truths OrderOn(PathCell cell, string op, string literal) => Order(cell.Coarse, op, literal) | R;

                /// <summary>True of a null alone; and a <c>VARCHAR</c> column fails to read anything but a string.</summary>
                public override Truths NullOn(PathCell cell) => cell switch
                {
                    PathCell.Absent or PathCell.Null => T,
                    PathCell.Strings => F,
                    PathCell.Point { Value: var v } when IsString(v) => F,
                    _ => F | R,
                };

            }

            /// <summary>
            /// <c>JSON_VALUE … RETURNING T</c>, which asserts the stored scalar is of <c>T</c>'s class and raises on anything
            /// else: a stored <c>30</c> under <c>RETURNING INTEGER</c>, never <c>30.0</c>, <c>"30"</c> or <c>true</c>.
            /// </summary>
            /// <remarks>
            /// So a row it keeps holds a value of the type, and is the literal where it is equal to it. The theory spells
            /// <c>30</c> and <c>30.0</c> alike, and only one of them is of the class, so no verdict is read off a value: a
            /// row that is not absent or null may be any truth value, the one Calcite raises on included.
            /// </remarks>
            sealed class Typed : Reading
            {

                readonly string _type;
                readonly ValueSet<string> _kinds;

                Typed(string type, ValueSet<string> kinds)
                {
                    _type = type;
                    _kinds = kinds;
                }

                /// <remarks>
                /// <c>DECIMAL</c> is the one that converts: it reads the value's rendering as a <c>BigDecimal</c>, a string
                /// of digits among them, and raises only on what does not parse
                /// (<c>CalciteJsonValueMeasurementTests.ReturningDecimalParsesTheRendering</c>). That is a cast of the text,
                /// whose parse reads every rendering of a number, compared with a literal cast to its scale of zero.
                /// </remarks>
                public static Reading? For(string type) => type switch
                {
                    "TINYINT" or "SMALLINT" or "INTEGER" or "BIGINT" => new Typed(type, ValueSet.Of(IntegerType)),
                    "FLOAT" or "REAL" or "DOUBLE" => new Typed(type, ValueSet.Of(IntegerType, FractionType)),
                    "DECIMAL" => new NumberCast("DECIMAL", overText: true),
                    "BOOLEAN" => new Typed(type, ValueSet.Of(BooleanType)),
                    _ => null,
                };

                public override Truths Equal(State state, string literal) => state switch
                {
                    State.Absent or State.Null or State.Structure => U,
                    State.Scalar { Value: var v } when _kinds.Contains(TypeOf(v)) => (Renderings.SameValue(v, literal) ? T : F) | R,
                    _ => Answers(state, T | F),
                };

                public override Truths Order(State state, string op, string literal) => Answers(state, T | F);

                public override Truths Null(State state) => state is State.Absent or State.Null or State.Structure ? T : Answers(state, F);

                public override Truths Parameter(State state) => Answers(state, T | F | U);

                /// <summary>
                /// What a value of the type answers, beside raising where its spelling is of another class; and a value of
                /// another type only raises. Absent, null and a structure read as SQL null.
                /// </summary>
                Truths Answers(State state, Truths ofType) => state switch
                {
                    State.Absent or State.Null or State.Structure => ofType == F ? T : U,
                    State.Scalar { Value: var v } => _kinds.Contains(TypeOf(v)) ? ofType | R : R,
                    State.AnyString => _kinds.Contains(StringType) ? ofType | R : R,
                    State.AnyNumber n => _kinds.Contains(n.Integral ? IntegerType : FractionType) ? ofType | R : R,
                    _ => Truths.Any | R,
                };

                public override PathValues KeptEqual(string literal) =>
                    PathValues.Meet(PathValues.OfKinds(_kinds.Intersect(ValueSet.Of(TypeOf(literal)))), PathValues.OneOf(ValueSet.Of(literal)));

                public override PathValues KeptUnequal(string literal) => PathValues.Meet(PathValues.OfKinds(_kinds), PathValues.Not(literal));

                public override PathValues KeptOrder(string op, string literal) => PathValues.OfKinds(_kinds);

                public override PathValues KeptNull(bool truth) =>
                    truth ? PathValues.OfKinds(ValueSet.Of(PathValues.AbsentKind, NullType, ObjectType, ArrayType)) : PathValues.OfKinds(_kinds);

                public override PathValues KeptParameter() => PathValues.OfKinds(_kinds);

                static readonly ValueSet<string> Rows = ValueSet.Of("Calcite.ReturningAsserts", "Calcite.ColumnReadsWholeNumbersAsItsType");

                public override ValueSet<string> Rests => Rows;

                /// <summary>
                /// The union of <c>RETURNING</c> and the column the reader fills (<c>Calcite.ColumnReadsWholeNumbersAsItsType</c>):
                /// a number of the type or a cell of them compared as either reads it, beside the raise where either may raise; a
                /// boolean as itself; and over a structure the column fails the statement, where <c>RETURNING</c> answers null.
                /// </summary>
                public override Truths EqualOn(PathCell cell, string literal) => OrderOn(cell, PredicateAtom.Equal, literal);

                public override Truths OrderOn(PathCell cell, string op, string literal) => cell switch
                {
                    PathCell.Point { Value: var v } when _type == "BOOLEAN" && op == PredicateAtom.Equal && TypeOf(v) == BooleanType => v == literal ? T : F,
                    _ when TryNumberOf(literal, out var c) && Number(cell, op, c) is Truths truths => truths,
                    _ => (op == PredicateAtom.Equal ? Equal(cell.Coarse, literal) : Order(cell.Coarse, op, literal)) | Unread(cell) | Whole(cell),
                };

                public override Truths NullOn(PathCell cell) => Null(cell.Coarse) | Unread(cell) | (Whole(cell) & F);

                static Truths Unread(PathCell cell) => cell is PathCell.Structure ? R : Truths.None;

                /// <summary>What an integer type's column may answer of a fraction whose double is whole, which it reads.</summary>
                Truths Whole(PathCell cell) => Limits is var (least, most) && (cell is PathCell.Numbers { Integral: false }
                    || cell is PathCell.Point { Value: var v } && TypeOf(v) == FractionType && DoubleBound.Of(v) is var r && Math.Floor(r) == r && r >= (double)least && r < (double)most + 1) ? T | F : Truths.None;

                (decimal Least, decimal Most)? Limits => IntegerLimits(_type);

                /// <summary>What a comparison answers on a number, or a cell of them; <c>null</c> for a reading this does not place.</summary>
                /// <remarks>
                /// An integer type's <c>RETURNING</c> reads an integer written so within its range, and raises over anything
                /// else, under <c>BIGINT</c> over one an int holds. Its column reads that too, and any other number as its
                /// double where that is whole and within the type: 42.0, a fraction that rounds to a whole double, and past
                /// 2^53 another integer (<c>CosmosJson.GetInteger</c>). A <c>DOUBLE</c> compares doubles, correctly rounded, and
                /// <c>RETURNING</c> raises over an integer written so.
                /// </remarks>
                Truths? Number(PathCell cell, string op, decimal c)
                {
                    if (Limits is null && _type != "DOUBLE" || cell is not (PathCell.Point or PathCell.Numbers) || cell is PathCell.Point { Value: var v } && IsNumber(v) == false)
                        return null;

                    var (least, most) = Limits ?? (0m, 0m);

                    var d = DoubleBound.Of(c);
                    var sides = new List<int?>();
                    var integral = cell is PathCell.Numbers { Integral: true } || cell is PathCell.Point { Value: var v0 } && TypeOf(v0) == IntegerType;
                    if (_type == "DOUBLE")
                        sides.Add(cell is PathCell.Numbers n0 ? n0.DoubleSide(d) : Math.Sign(DoubleBound.Of(((PathCell.Point)cell).Value).CompareTo(d)));
                    else if (cell is PathCell.Numbers n)
                    {
                        // Written so and within the type, read exactly; straddling a limit, not placed.
                        if (n.Integral && n.Side(least - 1) == 1 && n.Side(most + 1) == -1)
                            sides.Add(n.Side(c));
                        else if (n.Integral && n.Side(most) != 1 && n.Side(least) != -1)
                            sides.Add(null);

                        if (n.DoubleSide(DoubleBound.Of(least)) != -1 && n.DoubleSide(DoubleBound.Of(most + 1)) is not (0 or 1))
                            sides.Add(n.RoundedSide(c));
                    }
                    else
                    {
                        var value = ((PathCell.Point)cell).Value;
                        if (TryNumberOf(value, out var x) && x == decimal.Truncate(x) && x >= least && x <= most)
                            sides.Add(x.CompareTo(c));

                        var r = DoubleBound.Of(value);
                        if (Math.Floor(r) == r && r >= (double)least && r < (double)most + 1)
                            sides.Add(DoubleBound.Compare(r, c));
                    }

                    return Placed(op, sides, _type != "DOUBLE" || integral ? R : Truths.None);
                }

            }

            /// <summary>
            /// A cast to <c>INTEGER</c>, <c>BIGINT</c> or <c>DOUBLE</c> over a document value, which converts a stored
            /// number and a stored string alike; and <c>RETURNING DECIMAL</c>, which is a parse of the rendering too.
            /// </summary>
            /// <remarks>
            /// <para>
            /// <b>What it does to a number was measured</b> (<c>CosmosRexTranslator.TryNumericCastOperand</c>):
            /// <c>INTEGER</c> and <c>BIGINT</c> truncate toward zero and saturate at their limits, and <c>DOUBLE</c> is the
            /// number. So a number's converted value is known, and a range of numbers converts to a range.
            /// </para>
            /// <para>
            /// <b>Over the text accessor it is a parse of the rendering</b>, which is another measurement
            /// (<c>CalciteJsonValueMeasurementTests.ACastOfTheAccessorToANumberParsesTheRendering</c>): Java's integer parse
            /// for <c>INTEGER</c> and <c>BIGINT</c>, which raises on <c>30.0</c>, <c>2.5</c>, <c>1.0E30</c> and anything
            /// beyond the type, where a cast of the number would have truncated or saturated. The theory spells a stored
            /// <c>30</c> and <c>30.0</c> alike, so any number may raise there, and nothing is decided of one. A double's
            /// parse reads every rendering of a number, and raises on none.
            /// </para>
            /// <para>
            /// <b>What it does to anything else is not</b>, and a string, a boolean, and over a raw value a null or a
            /// structure, may convert to anything or raise. A row that keeps a comparison holds a number within the
            /// window <see cref="Window"/> gives, or something that is not a number at all: the window binds numbers
            /// only, as a bound does. It is <c>CosmosNumericWindow</c>'s, and so is the reason it is loose.
            /// </para>
            /// </remarks>
            sealed class NumberCast : Reading
            {

                static readonly ValueSet<string> Converts = ValueSet.Of(StringType, IntegerType, FractionType, BooleanType);

                readonly bool _overText;
                readonly (decimal Min, decimal Max)? _saturates;

                /// <summary>
                /// Whether the conversion is the parse of a decimal: <c>RETURNING DECIMAL</c>, which compares the number the
                /// rendering spells exactly, and types the value <c>DECIMAL(19, 0)</c>, so that Calcite casts a literal
                /// compared with it to that scale.
                /// </summary>
                readonly bool _parsesDecimal;

                /// <summary>
                /// Where the conversion may move a stored number by a whole unit, beyond which no window holds: a double's,
                /// from <see cref="Renderings.DoubleUnitFrom"/>.
                /// </summary>
                readonly decimal? _roundsFrom;

                /// <summary>
                /// What a stored number may do beside converting: raise, where the rendering is parsed as an integer.
                /// </summary>
                readonly Truths _parse;

                static readonly ValueSet<string> TextRows = ValueSet.Of("Calcite.JsonValueRendersScalarsAsText", "Calcite.CastOfTheAccessorParsesTheRendering");

                static readonly ValueSet<string> DecimalRows = ValueSet.Of("Calcite.JsonValueRendersScalarsAsText", "Calcite.ReturningDecimalParsesTheRendering");

                public override ValueSet<string> Rests => _parsesDecimal ? DecimalRows : _overText ? TextRows : ValueSet<string>.Empty;

                public override Truths EqualOn(PathCell cell, string literal) => On(cell, PredicateAtom.Equal, literal);

                public override Truths OrderOn(PathCell cell, string op, string literal) => On(cell, op, literal);

                /// <summary>
                /// A cell's answer where the conversion places it one way: a string or a boolean over text by the reading's own
                /// parse; a number by its double under <c>DOUBLE</c>, by value under <c>DECIMAL</c>, as written under an integer
                /// type's parse, and truncated and held at the limits under its cast; elsewhere the state's.
                /// </summary>
                Truths On(PathCell cell, string op, string literal)
                {
                    if (TryNumberOf(literal, out var c) == false || _parsesDecimal && c != decimal.Truncate(c))
                        return Compare(cell.Coarse, op, literal);

                    Truths By(int? side, Truths beside) => Placed(op, [side], beside);
                    var (least, most) = _saturates ?? (0m, 0m);
                    switch (cell)
                    {
                        // A boolean renders as text that spells no number, and a NaN compares as nothing Java says alike.
                        case PathCell.Point { Value: var v } when _overText && (IsString(v) || TypeOf(v) == BooleanType):
                        {
                            var text = IsString(v) ? StringOf(v) : Renderings.BooleanText(v);
                            return _roundsFrom is not null ? (Renderings.ParseDouble(text) is double d ? double.IsNaN(d) ? T | F : By(d.CompareTo(DoubleBound.Of(c)), 0) : R)
                                : _parsesDecimal ? (Renderings.ParseDecimal(text) is string n ? By(TryNumberOf(n, out var e) ? e.CompareTo(c) : DoubleBound.Compare(ApproximateOf(n)!.Value, c), 0) : R)
                                : Renderings.ParseInteger(text, least, most) is decimal w ? By(w.CompareTo(c), 0) : R;
                        }

                        case PathCell.Point { Value: var v } when _roundsFrom is not null && IsNumber(v):
                            return By(Math.Sign(DoubleBound.Of(v).CompareTo(DoubleBound.Of(c))), 0);
                        case PathCell.Numbers n when _roundsFrom is not null:
                            return By(n.DoubleSide(DoubleBound.Of(c)), 0);

                        // A number written as an integer renders as its digits, and any other as its double does
                        // (Calcite.JsonValueRendersScalarsAsText), which parses as the literal where both are a whole double.
                        case PathCell.Point { Value: var v } when _parsesDecimal && IsNumber(v):
                            return Rendered(op, c, Math.Sign(DoubleBound.Of(v).CompareTo(DoubleBound.Of(c))), TypeOf(v) == IntegerType && TryNumberOf(v, out var digits) ? [digits.CompareTo(c)] : []);
                        case PathCell.Numbers n when _parsesDecimal:
                            return Rendered(op, c, n.DoubleSide(DoubleBound.Of(c)), n.Integral ? [n.Side(c)] : []);

                        // Java's integer parse of a rendering reads an integer written so, and raises on any other.
                        case PathCell.Point { Value: var v } when _overText && _saturates is not null && IsNumber(v):
                            return TryNumberOf(v, out var x) && x == decimal.Truncate(x) && x >= least && x <= most ? By(x.CompareTo(c), R) : R;
                        case PathCell.Numbers n when _overText:
                            return n.Integral == false || n.Side(most) == 1 || n.Side(least) == -1 ? R
                                : n.Side(least - 1) == 1 && n.Side(most + 1) == -1 ? By(n.Side(c), R) : Compare(cell.Coarse, op, literal);

                        // Truncation toward zero never falls as a number rises: the least and the most a cell converts to.
                        case PathCell.Numbers n:
                        {
                            static bool Whole(decimal x) => x == decimal.Truncate(x);
                            var low = n.Lower is not Bound l ? least : Math.Clamp(n.Integral ? (l.Open ? Math.Floor(l.Value) + 1 : Math.Ceiling(l.Value))
                                : l.Value < 0 && Whole(l.Value) ? l.Value + 1 : decimal.Truncate(l.Value), least, most);
                            var high = n.Upper is not Bound u ? most : Math.Clamp(n.Integral ? (u.Open ? Math.Ceiling(u.Value) - 1 : Math.Floor(u.Value))
                                : u.Value > 0 && Whole(u.Value) ? u.Value - 1 : decimal.Truncate(u.Value), least, most);
                            return Between(op, low, high, c, Whole(c));
                        }

                        default:
                            return Compare(cell.Coarse, op, literal);
                    }
                }

                /// <summary>
                /// What <c>RETURNING DECIMAL</c> answers of numbers read as their double's rendering on the double's side of
                /// the literal's, and as their digits on the sides given; where either is not one way, TRUE or FALSE.
                /// </summary>
                static Truths Rendered(string op, decimal c, int? doubled, int?[] digits) =>
                    Placed(op, [doubled == 0 ? (Math.Abs(c) < 1L << 53 ? 0 : null) : doubled, .. digits], Truths.None);

                public NumberCast(string target, bool overText)
                {
                    _overText = overText;
                    _parsesDecimal = target == "DECIMAL";
                    _saturates = target is "INTEGER" or "BIGINT" ? IntegerLimits(target) : null;
                    _roundsFrom = target == "DOUBLE" ? Renderings.DoubleUnitFrom : null;
                    _parse = overText && _saturates is not null ? R : Truths.None;
                }

                public override Truths Equal(State state, string literal) => Compare(state, PredicateAtom.Equal, literal);

                public override Truths Order(State state, string op, string literal) => Compare(state, op, literal);

                Truths Compare(State state, string op, string literal)
                {
                    if (IsNumber(literal) == false)
                        return Truths.Any;

                    // The parse of a decimal casts a literal compared with it to its scale of zero: `= 2.5` is FALSE of a
                    // stored 2.5. What a literal with a fraction is compared as is not decided here; the window around it
                    // holds either way.
                    var rescaled = _parsesDecimal && (TryNumberOf(literal, out var exact) == false || exact != decimal.Truncate(exact));

                    return state switch
                    {
                        State.Absent => U,
                        State.Null or State.Structure => _overText ? U : U | R,
                        State.Scalar { Value: var v } when IsNumber(v) && rescaled => T | F,
                        State.Scalar { Value: var v } when IsNumber(v) => (Holds(op, v, literal) is bool holds ? (holds ? T : F) : T | F) | _parse,
                        State.AnyNumber when rescaled => T | F,
                        State.AnyNumber n => Range(op, n, literal) | _parse,
                        State.Scalar or State.AnyString => T | F | R,
                        _ => Truths.Any | R,
                    };
                }

                public override Truths Null(State state) => state switch
                {
                    State.Absent => T,
                    State.Null or State.Structure => _overText ? T : T | R,
                    State.Scalar { Value: var v } when IsNumber(v) => F | _parse,
                    State.AnyNumber => F | _parse,
                    State.Scalar or State.AnyString => F | R,
                    _ => Truths.Any | R,
                };

                public override Truths Parameter(State state) => state switch
                {
                    State.Absent => U,
                    State.Null or State.Structure => _overText ? U : U | R,
                    State.Scalar { Value: var v } when IsNumber(v) => T | F | U | _parse,
                    State.AnyNumber => T | F | U | _parse,
                    _ => Truths.Any | R,
                };

                public override PathValues KeptEqual(string literal) => KeptOrder(PredicateAtom.Equal, literal);

                public override PathValues KeptUnequal(string literal) => IsNumber(literal) ? PathValues.OfKinds(Converts) : PathValues.Anything;

                public override PathValues KeptOrder(string op, string literal)
                {
                    if (IsNumber(literal) == false)
                        return PathValues.Anything;

                    var kept = PathValues.OfKinds(Converts);
                    if (TryNumberOf(literal, out var bound) == false)
                        return kept;

                    var (above, below) = Window(op, bound);
                    if (above is decimal lower)
                        kept = PathValues.Meet(kept, PathValues.Above(Bound.Above(lower)));
                    if (below is decimal upper)
                        kept = PathValues.Meet(kept, PathValues.Below(Bound.Below(upper)));

                    return kept;
                }

                public override PathValues KeptNull(bool truth) =>
                    truth ? PathValues.OfKinds(ValueSet.Of(PathValues.AbsentKind, NullType, ObjectType, ArrayType)) : PathValues.OfKinds(Converts);

                public override PathValues KeptParameter() => PathValues.OfKinds(Converts);

                /// <summary>
                /// The window a TRUE comparison puts a stored number in: above one less than the bound, and below one more.
                /// A side holds only where the cast's saturation limit fails the comparison, every stored number beyond a
                /// limit converting to it; and neither holds around a bound a double's unit in the last place is whole at,
                /// where the stored value and the literal each round by up to half of it (<c>CosmosNumericWindow</c>).
                /// </summary>
                (decimal? Above, decimal? Below) Window(string op, decimal bound)
                {
                    decimal? above = null;
                    decimal? below = null;

                    if (_roundsFrom is decimal rounds && Math.Abs(bound) >= rounds)
                        return (null, null);

                    if (op is PredicateAtom.Equal or Greater or GreaterOrEqual && bound > decimal.MinValue + 1)
                        if (_saturates is not { } s || Satisfies(op, s.Min, bound) == false)
                            above = bound - 1;

                    if (op is PredicateAtom.Equal or Less or LessOrEqual && bound < decimal.MaxValue - 1)
                        if (_saturates is not { } s || Satisfies(op, s.Max, bound) == false)
                            below = bound + 1;

                    return (above, below);
                }

                /// <summary>Whether a stored number, converted, satisfies the comparison; <c>null</c> where it cannot be told.</summary>
                /// <remarks>
                /// A cast to <c>DOUBLE</c> compares two doubles, the stored number's and the literal's, which past 2^53 may be
                /// one double for two numbers. <c>RETURNING DECIMAL</c> compares the number the rendering parses as, exactly,
                /// with the literal; one past a decimal is not told.
                /// </remarks>
                bool? Holds(string op, string value, string literal)
                {
                    if (TryNumberOf(value, out var exact))
                    {
                        if (_saturates is { } s)
                        {
                            var converted = Math.Clamp(decimal.Truncate(exact), s.Min, s.Max);
                            return TryNumberOf(literal, out var bound) ? Satisfies(op, converted, bound) : (bool?)null;
                        }

                        if (_parsesDecimal)
                            return TryNumberOf(literal, out var parsed) ? Satisfies(op, exact, parsed) : (bool?)null;

                        return Satisfies(op, (double)exact, Renderings.DoubleOf(literal));
                    }

                    if (_parsesDecimal)
                        return null;

                    if (ApproximateOf(value) is double approximate)
                    {
                        if (_saturates is { } s)
                        {
                            var converted = approximate >= (double)s.Max ? s.Max : approximate <= (double)s.Min ? s.Min : Math.Abs(approximate) < 1 ? 0m : (decimal?)null;
                            return converted is decimal c && TryNumberOf(literal, out var bound) ? Satisfies(op, c, bound) : (bool?)null;
                        }

                        return Satisfies(op, approximate, Renderings.DoubleOf(literal));
                    }

                    return null;
                }

                /// <summary>
                /// The truth values over a range of numbers: what the converted range can be compared with the literal.
                /// </summary>
                /// <remarks>
                /// A conversion that truncates is monotone, so a range converts to the range between its converted ends,
                /// read as closed. A conversion to a double is monotone too, and rounds: the range converts to the doubles
                /// of its ends, compared with the literal's double, every one read as closed, because past 2^53 two numbers
                /// a unit apart may be one double. Both read more than the conversion yields, which is the safe side: a
                /// verdict is never reached that a narrower reading would refuse.
                /// </remarks>
                Truths Range(string op, State.AnyNumber numbers, string literal)
                {
                    if (TryNumberOf(literal, out var c) == false)
                        return Truths.Any;

                    if (_roundsFrom is not null)
                        return Doubles(op, numbers.Lower is Bound l ? (double)l.Value : null, numbers.Upper is Bound h ? (double)h.Value : null, (double)c);

                    decimal? low, high;
                    if (_saturates is { } s)
                    {
                        low = numbers.Lower is Bound l ? Math.Clamp(decimal.Truncate(l.Value), s.Min, s.Max) : s.Min;
                        high = numbers.Upper is Bound h ? Math.Clamp(decimal.Truncate(h.Value), s.Min, s.Max) : s.Max;
                    }
                    else
                    {
                        low = numbers.Lower?.Value;
                        high = numbers.Upper?.Value;
                    }

                    return Between(op, low, high, c, _saturates is null || c == decimal.Truncate(c));
                }

                /// <summary>What a comparison answers of the numbers between two ends, read as closed; an end not given is open.</summary>
                static Truths Between(string op, decimal? low, decimal? high, decimal c, bool whole)
                {
                    bool SomeBelow(bool strict) => low is not decimal a || (strict ? a < c : a <= c);
                    bool SomeAbove(bool strict) => high is not decimal b || (strict ? b > c : b >= c);
                    return op switch
                    {
                        PredicateAtom.Equal => (whole && SomeBelow(false) && SomeAbove(false) ? T : 0) | (low == c && high == c ? 0 : F),
                        NotEqual => (whole && SomeBelow(false) && SomeAbove(false) ? F : 0) | (low == c && high == c ? 0 : T),
                        Less => (SomeBelow(true) ? T : 0) | (SomeAbove(false) ? F : 0),
                        LessOrEqual => (SomeBelow(false) ? T : 0) | (SomeAbove(true) ? F : 0),
                        Greater => (SomeAbove(true) ? T : 0) | (SomeBelow(false) ? F : 0),
                        GreaterOrEqual => (SomeAbove(false) ? T : 0) | (SomeBelow(true) ? F : 0),
                        _ => Truths.Any,
                    };
                }

                /// <summary>
                /// The truth values of a comparison between the doubles of a range, read as closed, and a literal's double.
                /// </summary>
                static Truths Doubles(string op, double? low, double? high, double c)
                {
                    bool SomeBelow(bool strict) => low is not double a || (strict ? a < c : a <= c);
                    bool SomeAbove(bool strict) => high is not double b || (strict ? b > c : b >= c);

                    return op switch
                    {
                        PredicateAtom.Equal => (SomeBelow(false) && SomeAbove(false) ? T : 0) | (low == c && high == c ? 0 : F),
                        NotEqual => (SomeBelow(false) && SomeAbove(false) ? F : 0) | (low == c && high == c ? 0 : T),
                        Less => (SomeBelow(true) ? T : 0) | (SomeAbove(false) ? F : 0),
                        LessOrEqual => (SomeBelow(false) ? T : 0) | (SomeAbove(true) ? F : 0),
                        Greater => (SomeAbove(true) ? T : 0) | (SomeBelow(false) ? F : 0),
                        GreaterOrEqual => (SomeAbove(false) ? T : 0) | (SomeBelow(true) ? F : 0),
                        _ => Truths.Any,
                    };
                }

                static bool Satisfies(string op, decimal x, decimal c) => op switch
                {
                    PredicateAtom.Equal => x == c,
                    NotEqual => x != c,
                    Less => x < c,
                    LessOrEqual => x <= c,
                    Greater => x > c,
                    GreaterOrEqual => x >= c,
                    _ => false,
                };

                static bool? Satisfies(string op, double x, double c) => double.IsNaN(x) || double.IsNaN(c) ? null : op switch
                {
                    PredicateAtom.Equal => x == c,
                    NotEqual => x != c,
                    Less => x < c,
                    LessOrEqual => x <= c,
                    Greater => x > c,
                    GreaterOrEqual => x >= c,
                    _ => null,
                };

            }

        }

    }

}
