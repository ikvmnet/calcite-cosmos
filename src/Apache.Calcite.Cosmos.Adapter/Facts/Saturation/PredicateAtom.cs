using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// The truth values a SQL predicate may take on a row: true, false and unknown, as a set.
    /// </summary>
    [Flags]
    internal enum Truths
    {

        /// <summary>No row: what a context that admits no document says of anything.</summary>
        None = 0,

        /// <summary>TRUE: the row is kept.</summary>
        True = 1,

        /// <summary>FALSE: the row is not kept, and <c>NOT</c> keeps it.</summary>
        False = 2,

        /// <summary>UNKNOWN: the row is not kept, and <c>NOT</c> does not keep it either.</summary>
        Unknown = 4,

        /// <summary>Any of the three.</summary>
        Any = True | False | Unknown,

        /// <summary>
        /// Calcite raises on the row: no truth value at all. The query that reaches it fails, so it is no row a predicate
        /// keeps or its negation keeps, and it supports no verdict.
        /// </summary>
        Raises = 8,

    }

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

                readonly ValueSet<string> _kinds;

                Typed(ValueSet<string> kinds)
                {
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
                    "TINYINT" or "SMALLINT" or "INTEGER" or "BIGINT" => new Typed(ValueSet.Of(IntegerType)),
                    "FLOAT" or "REAL" or "DOUBLE" => new Typed(ValueSet.Of(IntegerType, FractionType)),
                    "DECIMAL" => new NumberCast("DECIMAL", overText: true),
                    "BOOLEAN" => new Typed(ValueSet.Of(BooleanType)),
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

                public NumberCast(string target, bool overText)
                {
                    _overText = overText;
                    _parsesDecimal = target == "DECIMAL";
                    _saturates = target switch
                    {
                        "INTEGER" => (int.MinValue, int.MaxValue),
                        "BIGINT" => (long.MinValue, long.MaxValue),
                        _ => null,
                    };
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

                    bool SomeBelow(bool strict) => low is not decimal a || (strict ? a < c : a <= c);
                    bool SomeAbove(bool strict) => high is not decimal b || (strict ? b > c : b >= c);
                    var whole = _saturates is null || c == decimal.Truncate(c);

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

    /// <summary>
    /// What a stored scalar renders as through <c>JSON_VALUE</c>, read backwards: which values a text could have come
    /// from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A number renders as Java spells it</b>, which parses back to the number: <c>30</c>, <c>30.0</c>,
    /// <c>1.0E30</c>, and from a <c>BigDecimal</c> <c>1E+30</c>. So only text in that grammar can be a number's
    /// rendering, and only the number with the text's value can render as it — in some spellings and not others.
    /// </para>
    /// <para>
    /// <b>A domain names the number only where a decimal holds the text exactly</b>, at most 28 significant digits. Text
    /// longer than that is still a number's rendering, and stands for a number of either kind, not for a value.
    /// </para>
    /// </remarks>
    internal static class Renderings
    {

        /// <summary>
        /// The magnitude from which a double's unit in the last place is whole: 2^52.
        /// </summary>
        /// <remarks>
        /// A cast to <c>DOUBLE</c> rounds a stored integer and the literal it is compared with by up to half of that unit
        /// each, so from here two whole numbers a unit apart may compare equal — measured, <c>= 2^60</c> keeps a stored
        /// 2^60 + 1 — and no window a unit wide holds around a bound. <c>CosmosRexTranslator.TryNumericCastOperand</c>
        /// reports it as where the conversion rounds from; this is the one place it is written.
        /// </remarks>
        public const long DoubleUnitFrom = 1L << 52;

        /// <summary>
        /// Determines whether text is in the grammar a stored number renders in: digits, a point, an exponent, a sign.
        /// </summary>
        public static bool MayBeNumberText(string text)
        {
            var i = 0;
            if (i < text.Length && text[i] is '+' or '-')
                i++;

            var digits = 0;
            while (i < text.Length && text[i] is >= '0' and <= '9')
            {
                i++;
                digits++;
            }

            if (i < text.Length && text[i] == '.')
            {
                i++;
                while (i < text.Length && text[i] is >= '0' and <= '9')
                {
                    i++;
                    digits++;
                }
            }

            if (digits == 0)
                return false;

            if (i < text.Length && text[i] is 'e' or 'E')
            {
                i++;
                if (i < text.Length && text[i] is '+' or '-')
                    i++;

                var exponent = 0;
                while (i < text.Length && text[i] is >= '0' and <= '9')
                {
                    i++;
                    exponent++;
                }

                if (exponent == 0)
                    return false;
            }

            return i == text.Length;
        }

        /// <summary>
        /// Reads number text as the decimal it spells, where a decimal holds it exactly.
        /// </summary>
        public static bool TryExactNumber(string text, out decimal value)
        {
            value = 0;
            if (MayBeNumberText(text) == false || SignificantDigits(text) > 28)
                return false;

            return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>The text Calcite renders a boolean as.</summary>
        public static string BooleanText(string value) => value == Of(true) ? "true" : "false";

        /// <summary>
        /// Determines whether a stored number may render as the text: whether the text is a number's rendering with the
        /// value of this one.
        /// </summary>
        public static bool MayRenderAs(string value, string text)
        {
            if (MayBeNumberText(text) == false)
                return false;

            if (TryNumberOf(value, out var exact) && TryExactNumber(text, out var number))
                return exact == number;

            var stored = TryNumberOf(value, out exact) ? (double)exact : ApproximateOf(value);
            return stored is double d && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && d == parsed;
        }

        /// <summary>
        /// Determines whether some number of the kind within the bounds may render as the text.
        /// </summary>
        public static bool MayRenderWithin(string text, bool integral, Bound? lower, Bound? upper)
        {
            if (MayBeNumberText(text) == false)
                return false;

            if (TryExactNumber(text, out var x) == false)
                return true;

            return (integral == false || x == decimal.Truncate(x))
                && (lower is not Bound l || l.AdmitsAbove(x))
                && (upper is not Bound u || u.AdmitsBelow(x));
        }

        /// <summary>
        /// Determines whether a structure may render as the text: an array as <c>[…]</c>, an object as <c>{…}</c>.
        /// </summary>
        public static bool MayRenderStructure(string type, string text) =>
            type == ArrayType ? text.StartsWith('[') : type == ObjectType && text.StartsWith('{');

        /// <summary>Determines whether two scalars may be one value: numbers by their value, anything else by its spelling.</summary>
        public static bool SameValue(string a, string b) => IsNumber(a) && IsNumber(b) ? SameNumber(a, b) : a == b;

        /// <summary>Determines whether two numbers may be one value.</summary>
        public static bool SameNumber(string a, string b)
        {
            if (TryNumberOf(a, out var x) && TryNumberOf(b, out var y))
                return x == y;

            return DoubleOf(a) == DoubleOf(b);
        }

        /// <summary>Determines whether a number may lie within bounds and be of the kind.</summary>
        public static bool NumberWithin(string value, bool integral, Bound? lower, Bound? upper)
        {
            if (TryNumberOf(value, out var x) == false)
                return true;

            return (integral == false || x == decimal.Truncate(x))
                && (lower is not Bound l || l.AdmitsAbove(x))
                && (upper is not Bound u || u.AdmitsBelow(x));
        }

        /// <summary>A number's value as a double, exact or approximate.</summary>
        public static double DoubleOf(string value) =>
            TryNumberOf(value, out var exact) ? (double)exact : ApproximateOf(value) ?? double.NaN;

        static int SignificantDigits(string text)
        {
            var mantissa = text;
            var e = mantissa.IndexOfAny(new[] { 'e', 'E' });
            if (e >= 0)
                mantissa = mantissa.Substring(0, e);

            var digits = mantissa.Where(char.IsAsciiDigit).SkipWhile(c => c == '0').Count();
            return Math.Max(digits, 1);
        }

    }

}
