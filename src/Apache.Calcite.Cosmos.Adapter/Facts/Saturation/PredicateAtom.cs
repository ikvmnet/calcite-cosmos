using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Rendering;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// A predicate over one path: how a value is read from the path, and what is asked of what is read. The atom is the
    /// meaning of a <see cref="PredicateRules"/> comparison, membership or null test, gathered from its terms into one
    /// value that two questions are asked of: which documents a row with a given truth value can be
    /// (<see cref="Kept(bool, ReadingTable, RawTruths)"/>), and which truth values the documents a context admits can give it (<see cref="Over"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both answers over-approximate, and that is what makes them sound.</b> <see cref="Kept(bool, ReadingTable, RawTruths)"/> may name documents a
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
    /// <c>RETURNING T</c>. <c>cast:T:r</c> is a cast to <c>T</c> over reading <c>r</c>: a numeric cast, the cast to
    /// text over a raw value, which renders an object and an array as well, or the text's parse as a boolean.
    /// </para>
    /// </remarks>
    /// <param name="Reader">How the value is read: <c>text</c>, <c>raw</c>, <c>typed:T</c> or <c>cast:T:r</c>.</param>
    /// <param name="Test">What is asked: a comparison operator, <c>in</c>, <c>is null</c>, <c>is defined</c>, a type test, or <c>LIKE</c>.</param>
    /// <param name="Operand">The literal compared with, in <see cref="JsonValues"/>' spelling, <c>LIKE</c>'s pattern as a string; <c>null</c> for a parameter and a test.</param>
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

        public const string LikeTest = "LIKE";

        public const string TextReader = "text";
        public const string RawReader = "raw";

        /// <summary>Every <c>RETURNING</c>, whatever its type, as a row names them all (<c>testreads</c>, G8).</summary>
        public const string TypedReaders = "typed";

        /// <summary>Every computed place's call, whatever its function, as a row names them all (<c>testreads</c>).</summary>
        public const string CallReaders = "call";

        /// <summary>
        /// What a one-place test's body reads over a reading it cannot take apart, as a row says (<c>testreads</c>): nothing,
        /// a raise on every value, as over a field of every type and a call, measured in process (rows2).
        /// </summary>
        public const string RaisesReader = "raises";

        /// <summary>
        /// The forms a reading is written as apart from its accessor (<c>SqlValue.Form</c>, h15b), each keyed by its own row of
        /// what a one-place test's body reads over it (<c>testreads</c>): a field of the input, an item chain, a cast to <c>ANY</c>,
        /// and a cast of the text to <c>VARCHAR</c> a test is asked over.
        /// </summary>
        public const string FieldForm = "field", ItemForm = "item", AnyCastForm = "cast:ANY", TextCastForm = "cast:VARCHAR";

        /// <summary>
        /// A reading of a path holding an element that no row measured as of a path holding it: an accessor the readings of an
        /// index do not name (<c>asofapath</c>), and any reading written apart (h15b). It reads its rows alone, which the cells
        /// read (<c>calcitereads</c>), a row no run measured as anything and a raise (<c>Calcite.ItemIsNullWhereNoElementIs</c>).
        /// </summary>
        public const string ElementReader = "element";

        /// <summary>
        /// <c>JSON_QUERY(DOC, path)</c> with no clause (G8): what it answers of each class is its row's alone
        /// (<c>Calcite.JsonQueryAnswersStructureOnly</c>, <c>calcitereads</c>), and no evaluator of its own reads it.
        /// </summary>
        public const string QueryReader = "query";

        /// <summary>Returns the reader of a <c>RETURNING</c> of a SQL type.</summary>
        public static string TypedReader(string type) => TypedReaders + ":" + type;

        /// <summary>Returns the reader of a cast to a SQL type over another reader.</summary>
        public static string CastReader(string target, string inner) => "cast:" + target + ":" + inner;

        /// <summary>
        /// Returns the reading a reader applies last, which its value is compared as: a cast's target over whatever it
        /// converts (<c>cast:DOUBLE</c> of <c>cast:DOUBLE:raw:ANY</c>), and any other reader itself. S keys what a reading
        /// compares a number Calcite wrote cast with by it (<c>RenderingTheory.TakesConstant</c>, G8).
        /// </summary>
        public static string Outermost(string reader) =>
            reader.StartsWith("cast:", StringComparison.Ordinal) && reader.IndexOf(':', "cast:".Length) is var colon and > 0 ? reader.Substring(0, colon) : reader;

        /// <summary>
        /// The columns a raw value is read through, as <c>Calcite.RawNullIsSqlNull</c> names them: a promoted path's
        /// <c>VARIANT</c>, an <c>ANY</c>, a field typed <c>VARCHAR</c>.
        /// </summary>
        public static readonly IReadOnlyList<string> RawColumns = ["VARIANT", "ANY", "VARCHAR"];

        /// <summary>
        /// The columns a raw value is read through that no row measured, each read as the measured columns' union until one
        /// does: a field typed <c>CHAR</c>, whose padding no row measured. S's rules give each the union's rows (rawtyped).
        /// </summary>
        public static readonly IReadOnlyList<string> UnionColumns = ["CHAR"];

        /// <summary>Every column a raw value is read through: the measured ones, and those read as their union.</summary>
        public static IEnumerable<string> Columns => RawColumns.Concat(UnionColumns);

        /// <summary>
        /// Returns a reader read through one raw column: <c>raw:VARIANT</c> for <c>raw</c>, <c>cast:VARCHAR:raw:ANY</c> for
        /// <c>cast:VARCHAR:raw</c>. The raw reading is always innermost, so the column is the reader's last segment.
        /// </summary>
        public static string InColumn(string reader, string column) => reader + ":" + column;

        /// <summary>Returns the raw value's reader through one column: <c>raw:VARIANT</c>.</summary>
        public static string RawIn(string column) => InColumn(RawReader, column);

        /// <summary>The raw column a reader is read through, or <c>null</c> for a reader naming none.</summary>
        public static string? ColumnOf(string reader)
        {
            foreach (var column in Columns)
                if (reader == RawReader + ":" + column || reader.EndsWith(":" + RawReader + ":" + column, StringComparison.Ordinal))
                    return column;

            return null;
        }

        /// <summary>A reader with its raw column dropped: what the host's tables key a reading by, its column apart.</summary>
        public static string Columnless(string reader) => ColumnOf(reader) is { } column ? reader.Substring(0, reader.Length - column.Length - 1) : reader;

        /// <summary>Whether a reader is the raw value itself, read through the union or through one column.</summary>
        public static bool IsRaw(string reader) => Columnless(reader) == RawReader;

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

        /// <summary>
        /// A comparison with SQL's null by an operator its row says answers null over a null operand (<c>strictop</c>), as S's
        /// rule reads it (G8): UNKNOWN wherever the reading is read, raising where reading it raises. F never makes one.
        /// </summary>
        public static PredicateAtom CompareNull(string reader, string op) => new(reader, op, null, null, false) { NullOperand = true };

        /// <summary>Whether the value is compared with SQL's null (<see cref="CompareNull"/>); no other atom is.</summary>
        public bool NullOperand { get; init; }

        /// <summary>
        /// A comparison of two readings, over S's pair of their places (h11): the left read by <see cref="Reader"/>, the right
        /// by <paramref name="against"/>. F never makes one.
        /// </summary>
        public static PredicateAtom CompareReadings(string reader, string op, string against) => new(reader, op, null, null, false) { Against = against };

        /// <summary>
        /// A comparison of two readings with what each reading's order holds in process, as S's rows say and its pair rule
        /// hands them (<c>readerorder</c>, G3): <see cref="Itself"/>, <see cref="Reflexive"/>, or neither.
        /// </summary>
        public static PredicateAtom OrderedBy(PredicateAtom atom, ValueSet<string> left, ValueSet<string> right) => atom with { LeftOrders = left, RightOrders = right };

        /// <summary>The right reading of a comparison of two readings; <c>null</c> for every other atom.</summary>
        public string? Against { get; init; }

        /// <summary>What the left reading's order holds in process, of a comparison of two readings; none for every other atom.</summary>
        public ValueSet<string> LeftOrders { get; init; } = ValueSet<string>.Empty;

        /// <summary>What the right reading's order holds in process, of a comparison of two readings; none for every other atom.</summary>
        public ValueSet<string> RightOrders { get; init; } = ValueSet<string>.Empty;

        /// <summary>A reading's order holds this where the reading of a string is the string, so two read so compare as the strings do.</summary>
        public const string Itself = "itself";

        /// <summary>A reading's order holds this where two equal values read so compare equal.</summary>
        public const string Reflexive = "reflexive";

        /// <summary>A membership in a set of literals.</summary>
        public static PredicateAtom In(string reader, ValueSet<string> points) => new(reader, InTest, null, points, false);

        /// <summary>A null test.</summary>
        public static PredicateAtom IsNull(string reader) => new(reader, NullTest, null, null, false);

        /// <summary>
        /// <c>IS_DEFINED</c> of the path the reader reads: what it asks is the document's, so it means one thing whatever
        /// the reading (<c>Service.IsDefined</c>, and the in-process body reads the document too).
        /// </summary>
        public static PredicateAtom IsDefined(string reader) => new(reader, DefinedTest, null, null, false);

        /// <summary><see cref="IsDefined(string)"/> asked over a reading its body reads as <paramref name="body"/> (<see cref="Body"/>).</summary>
        public static PredicateAtom IsDefined(string reader, string body) => IsDefined(reader) with { Body = body };

        /// <summary>
        /// A type test of the path the reader reads: what it asks is the document's kind, whatever the reading, as
        /// <see cref="IsDefined(string)"/>'s is; TRUE of the kinds its row says (<c>testkinds</c>, G7), absence never.
        /// </summary>
        public static PredicateAtom TypeTest(string reader, string test, ValueSet<string> kinds) => new(reader, test, null, null, false) { Kinds = kinds };

        /// <summary><see cref="TypeTest(string, string, ValueSet{string})"/> asked over a reading its body reads as <paramref name="body"/> (<see cref="Body"/>).</summary>
        public static PredicateAtom TypeTest(string reader, string test, ValueSet<string> kinds, string body) => TypeTest(reader, test, kinds) with { Body = body };

        /// <summary>The JSON kinds a type test is TRUE of, as its row says (<c>Calcite.TypeTestsAreTheirBodies</c>); none for every other atom.</summary>
        public ValueSet<string>? Kinds { get; init; }

        /// <summary>
        /// What a one-place test's in-process body reads in place of <see cref="Reader"/>, the reading it is asked over, as its
        /// row says (<c>testreads</c>): the raw value, or <see cref="RaisesReader"/>; <c>null</c> for a test read as its own
        /// reading, and for every other atom.
        /// </summary>
        public string? Body { get; init; }

        /// <summary>
        /// A match of the text read against a literal pattern: <c>LIKE</c>, TRUE or FALSE where the reading is not null and
        /// UNKNOWN where it is, matched as Calcite's runtime matches.
        /// </summary>
        public static PredicateAtom Affix(string reader, string op, string pattern) => new(reader, op, Of(pattern), null, false);

        /// <summary>Whether a string names a comparison operator.</summary>
        public static bool IsComparison(string op) => op is Equal or NotEqual or Less or LessOrEqual or Greater or GreaterOrEqual;

        /// <summary>
        /// Whether the facts graph reads the atom as Calcite does on TRUE and FALSE, by the rows of the readings it reads so
        /// (<c>readsexactly</c>, <c>Calcite.TheFactsGraphReadsTheseReadingsExactly</c>): a presence test by the presence tests'
        /// row and a type test by the type tests', whatever reads its path, and any other by its reading, its raw column apart,
        /// beside the class of what it is compared with, <c>Number</c> where that holds a number and <c>None</c> otherwise.
        /// </summary>
        /// <param name="exact">The readings the rows say the facts graph reads exactly, beside each class.</param>
        public bool ReadExactly(ValueSet<(string, CosmosValueClass)> exact) =>
            exact.Contains(Test == DefinedTest ? (RenderingFamily.PresenceTest, CosmosValueClass.None) : Kinds is not null ? (RenderingFamily.TypeTest, CosmosValueClass.None)
                : (Columnless(Reader), Operand is { } operand && IsNumber(operand) || Points is { } points && points.AsEnumerable().Any(IsNumber) ? CosmosValueClass.Number : CosmosValueClass.None));

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

        /// <summary>The test the rows key an ordering by, and an equality by its own.</summary>
        static CosmosServiceTest ServiceTestOf(string op) => op switch
        {
            Less => CosmosServiceTest.Less,
            LessOrEqual => CosmosServiceTest.LessOrEqual,
            Greater => CosmosServiceTest.Greater,
            GreaterOrEqual => CosmosServiceTest.GreaterOrEqual,
            _ => CosmosServiceTest.Equal,
        };

        /// <summary>
        /// The readings by name, parsed once: a record's fields are its equality, so the reading is not one of them.
        /// </summary>
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Reading?> Parsed = new(StringComparer.Ordinal);

        /// <summary>
        /// Returns what a path holds in every document on which the atom takes the given truth value: the documents a
        /// row it keeps (<paramref name="truth"/> <c>true</c>), or a row its negation keeps, can be. Each class of value is
        /// read as the readings rules say (<paramref name="readings"/>, G7): by the reading the reader is evaluated as there;
        /// and a cast of a raw value to text by what its column answers (<paramref name="columns"/>), as a cell of it reads.
        /// </summary>
        /// <param name="truth">Whether the atom is TRUE of the row, or FALSE.</param>
        /// <param name="readings">Every reading's rows, the readings vocabulary's table.</param>
        /// <param name="columns">The raw columns' union, the readings vocabulary's <c>rawunion</c>.</param>
        /// <returns>An over-approximation of those documents.</returns>
        public PathValues Kept(bool truth, ReadingTable readings, RawTruths columns) => Kept(truth, readings, columns, AllTypes);

        /// <summary>
        /// What <see cref="Kept(bool, ReadingTable, RawTruths)"/> says of a path that holds one of these types where it is
        /// there: each case it joins met with them first, so a case of another type drops out rather than widening the
        /// rest, as a numbers' window beside a string would lose the string's value.
        /// </summary>
        public PathValues Kept(bool truth, ReadingTable readings, RawTruths columns, ValueSet<string> types)
        {
            var among = PathValues.OfTypes(types);

            // True where the path is there, a null included, and false only where it is not.
            if (Test == DefinedTest)
                return PathValues.Meet(truth ? PathValues.AnythingThere : PathValues.OfKinds(ValueSet.Of(PathValues.AbsentKind)), among);

            if (Kinds is { } kinds)
                return PathValues.Meet(PathValues.OfKinds(truth ? kinds : ValueSet.From(PathValues.AllKinds.Except(kinds))), among);

            if (Against is not null)
                return among;

            // Each reading the reader is evaluated as keeps the kinds of the classes it is evaluated over; its own the rest.
            var aliases = readings[Reader].As.AsEnumerable().ToList();
            var own = KeptBy(Evaluate(Reader, readings), truth, columns, types);
            if (aliases.Count == 0)
                return own;

            var kept = PathValues.Meet(own, PathValues.OfKinds(KindsOf(CosmosValueClasses.Kinds.AsEnumerable().Except(aliases.Select(a => a.Kind)))));
            foreach (var alias in aliases.GroupBy(a => a.Reader))
                kept = PathValues.Join(kept, PathValues.Meet(KeptBy(Evaluate(alias.Key, readings), truth, columns, types), PathValues.OfKinds(KindsOf(alias.Select(a => a.Kind)))));

            return kept;
        }

        /// <summary>The JSON kinds the classes hold.</summary>
        static ValueSet<string> KindsOf(IEnumerable<CosmosValueClass> classes) =>
            ValueSet.From(classes.SelectMany(c => CosmosValueClasses.JsonKinds.Where(k => k.Class == c).Select(k => k.Kind)));

        /// <summary>
        /// What a path of these types holds where the atom, evaluated by the reading, takes the truth value: an equality's
        /// cases, and a membership's, met with them before they are joined.
        /// </summary>
        PathValues KeptBy(Reading reading, bool truth, RawTruths columns, ValueSet<string> types) =>
            PathValues.Meet(KeptBy(reading, truth, columns, literal => reading.KeptEqual(literal, columns, types)), PathValues.OfTypes(types));

        /// <summary>What a path holds where the atom, evaluated by the reading, takes the truth value, an equality TRUE by <paramref name="equal"/>.</summary>
        PathValues KeptBy(Reading reading, bool truth, RawTruths columns, Func<string, PathValues> equal)
        {
            // TRUE or FALSE only where the value matched is not null.
            if (Test == LikeTest)
                return reading.KeptNull(false, columns);

            if (Parameter)
                return reading.KeptParameter(columns);

            // Never TRUE nor FALSE: no document is kept either way.
            if (NullOperand)
                return PathValues.OfKinds(ValueSet<string>.Empty);

            switch (Test)
            {
                case Equal:
                    return truth ? equal(Operand!) : reading.KeptUnequal(Operand!, columns);
                case NotEqual:
                    return truth ? reading.KeptUnequal(Operand!, columns) : equal(Operand!);
                case Less or LessOrEqual or Greater or GreaterOrEqual:
                    return reading.KeptOrder(truth ? Test : Negate(Test), Operand!, columns);
                case InTest when Points is { Count: > 0 }:
                    return truth
                        ? Points.Select(equal).Aggregate(Reading.Either)
                        : Points.Select(point => reading.KeptUnequal(point, columns)).Aggregate(PathValues.Meet);
                case NullTest:
                    return reading.KeptNull(truth, columns);
                default:
                    return PathValues.Anything;
            }
        }

        /// <summary>
        /// Returns the truth values the atom may take over the documents a path's knowledge admits.
        /// </summary>
        /// <param name="possible">What the path may hold.</param>
        /// <param name="readings">Every reading's rows, the readings vocabulary's table.</param>
        /// <returns>An over-approximation of those truth values; <see cref="Truths.None"/> where no document is admitted.</returns>
        public Truths Over(PathValues possible, ReadingTable readings)
        {
            var truths = Truths.None;
            foreach (var state in State.Enumerate(possible))
            {
                truths |= On(state, readings);
                if ((truths & Truths.Any) == Truths.Any)
                    break;
            }

            return truths;
        }

        /// <summary>Whether the atom is TRUE of every document the knowledge admits.</summary>
        public bool AlwaysTrue(PathValues possible, ReadingTable readings) => (Over(possible, readings) & ~Truths.True) == Truths.None;

        /// <summary>Whether the atom is FALSE, never UNKNOWN, of every document the knowledge admits.</summary>
        public bool AlwaysFalse(PathValues possible, ReadingTable readings) => (Over(possible, readings) & ~Truths.False) == Truths.None;

        /// <summary>Whether the atom is TRUE of no document the knowledge admits, and raises on none.</summary>
        public bool NeverTrue(PathValues possible, ReadingTable readings) => (Over(possible, readings) & (Truths.True | Truths.Raises)) == Truths.None;

        /// <summary>
        /// Determines whether Calcite may raise on some document holding a value the path's knowledge admits: the raise
        /// evaluator, which reads every state the path may hold through its cell (<see cref="PathCell.Of"/>,
        /// <see cref="OnCell"/>), whose answers carry the raises the coarse readings leave out, and never stops while a state
        /// is left. A pattern the units' rows leave unread, U+0000 among them, may raise; an answer that decides nothing,
        /// exactly <see cref="Truths.Any"/>, counts as one; and a comparison with a parameter raises where its reading's
        /// answer to one raises (<see cref="ParameterOn"/>), and nowhere else, since its undecided truth value is no raise.
        /// </summary>
        /// <param name="possible">What the path may hold.</param>
        /// <param name="columns">The raw columns' union, the readings vocabulary's <c>rawunion</c>.</param>
        /// <param name="readings">Every reading's rows, the readings vocabulary's table.</param>
        public bool RaisesOver(PathValues possible, RawTruths columns, ReadingTable readings)
        {
            // LIKE answers TRUE and FALSE for a pattern it leaves unread, neither a raise nor undecided, so it is asked here.
            if (Test == LikeTest && LikePattern.Read(StringOf(Operand!), readings.Units) is null)
                return true;

            foreach (var state in State.Enumerate(possible))
            {
                var cell = PathCell.Of(state);
                var truths = Parameter ? ParameterOn(cell, columns, readings) : OnCell(cell, columns, readings);
                if ((truths & Truths.Raises) != 0 || Parameter == false && truths == Truths.Any)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Determines whether Calcite may answer UNKNOWN on some document holding a value the path's knowledge admits: read as
        /// <see cref="RaisesOver"/> reads a raise, every state through its cell, an answer that decides nothing among them.
        /// A comparison with a parameter may, the parameter bound to a null.
        /// </summary>
        /// <param name="possible">What the path may hold.</param>
        /// <param name="columns">The raw columns' union, the readings vocabulary's <c>rawunion</c>.</param>
        /// <param name="readings">Every reading's rows, the readings vocabulary's table.</param>
        public bool UnknownOver(PathValues possible, RawTruths columns, ReadingTable readings)
        {
            if (Parameter)
                return true;

            foreach (var state in State.Enumerate(possible))
                if ((OnCell(PathCell.Of(state), columns, readings) & Truths.Unknown) != 0)
                    return true;

            return false;
        }

        /// <summary>
        /// The truth values a comparison with a parameter may take on a cell, beside a raise wherever Calcite may raise
        /// whatever the parameter is bound to (<c>Calcite.AParameterComparesAsItsComparand</c>): the reading evaluated as the
        /// readings rules say over the cell's class, as <see cref="OnCell"/> evaluates it, its own answer beside its null
        /// test's and, for a raw value or its cast to text, its column's answer beside a parameter.
        /// </summary>
        internal Truths ParameterOn(PathCell cell, RawTruths columns, ReadingTable readings) =>
            EvaluatorOf(Reader, readings, CosmosValueClasses.Of(cell)).Reading.ParameterOn(cell, Test, columns);

        /// <summary>The truth values the atom may take on one kind of document, its class read as the readings rules say.</summary>
        internal Truths On(State state, ReadingTable readings)
        {
            // A one-place test answers the document's kind through what reads it (Calcite.TypeTestsAreTheirBodies, rows2):
            // the reading it is asked over, then its body's, raising where either raises, a body that raises everywhere among them.
            if (Test == DefinedTest || Kinds is not null)
                return WhereRead(EvaluatorOf(Reader, readings, State.ClassOf(state)).Reading.Null(state), WhereRead(Evaluate(Body ?? Reader, readings).Null(state), ByKind(state)));

            if (Against is not null || state is State.Unknown)
                return Truths.Any;

            var (by, reading) = EvaluatorOf(Reader, readings, State.ClassOf(state));
            if (Test == LikeTest)
                return Matched(reading.Null(state), state is State.Scalar { Value: var v } && IsString(v) && readings[by].ReadsAs(CosmosValueClass.String, CalciteRead.Itself) ? v : null, readings.Units);

            if (Parameter)
                return reading.Parameter(state);

            if (NullOperand)
                return ComparedWithNull(reading.Null(state));

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

        /// <summary>What a one-place test asks of a kind of document: presence, or a kind its row says it is TRUE of.</summary>
        Truths ByKind(State state) => Kinds is not { } kinds
            ? state is State.Absent ? Truths.False : state is State.Unknown ? Truths.True | Truths.False : Truths.True
            : state switch
            {
                State.Absent => Truths.False,
                State.Null => kinds.Contains(NullType) ? Truths.True : Truths.False,
                State.Scalar s => kinds.Contains(TypeOf(s.Value)) ? Truths.True : Truths.False,
                State.AnyString => kinds.Contains(StringType) ? Truths.True : Truths.False,
                State.AnyNumber n => kinds.Contains(n.Integral ? IntegerType : FractionType) ? Truths.True : Truths.False,
                State.Structure s => kinds.Contains(s.Type) ? Truths.True : Truths.False,
                _ => Truths.True | Truths.False,
            };

        /// <summary>An answer through a reading, by its null test's truths: the answer where it reads a value or null, a raise where it raises.</summary>
        static Truths WhereRead(Truths isNull, Truths answer) => (isNull & Truths.Raises) | ((isNull & (Truths.True | Truths.False)) != 0 ? answer : Truths.None);

        /// <summary>
        /// The truth values the atom may take on one cell of a path's import: <see cref="On"/>'s, through the reading's cell
        /// methods. The rendering check's Calcite side. The reading is evaluated as the readings rules say over the cell's
        /// class (<paramref name="readings"/>, G7), the table the facts graph reads: as the reading a row says it is read as
        /// over a class both read alike, and as itself over any other; <c>LIKE</c> where the rows it is evaluated by read a
        /// string as itself.
        /// </summary>
        internal Truths OnCell(PathCell cell, RawTruths columns, ReadingTable readings)
        {
            if (Against is not null)
                return cell is PathCell.Pair pair ? OnPair(pair, columns, readings) : Truths.Any;

            if (Test == DefinedTest || Kinds is not null || cell is PathCell.Unknown)
                return On(cell.Coarse, readings);

            var (by, reading) = EvaluatorOf(Reader, readings, CosmosValueClasses.Of(cell));
            var rows = readings[by];
            if (Test == LikeTest)
                return rows.ReadsAs(CosmosValueClass.String, CalciteRead.Itself) ? LikeOn(cell, reading.NullOn(cell, columns), rows, readings.Units) : Truths.Any;

            if (Parameter)
                return reading.Parameter(cell.Coarse) | ComparedWhereNotNull(reading.NullOn(cell, columns));

            if (NullOperand)
                return ComparedWithNull(reading.NullOn(cell, columns));

            switch (Test)
            {
                case Equal:
                    return reading.EqualOn(cell, Operand!, columns);
                case NotEqual:
                    return Swap(reading.EqualOn(cell, Operand!, columns));
                case Less or LessOrEqual or Greater or GreaterOrEqual:
                    return reading.OrderOn(cell, Test, Operand!, columns);
                case InTest when Points is { Count: > 0 }:
                    return Points.Select(point => reading.EqualOn(cell, point, columns)).Aggregate(Or);
                case NullTest:
                    return reading.NullOn(cell, columns);
                default:
                    return On(cell.Coarse, readings);
            }
        }

        /// <summary>
        /// A comparison of two readings on a cell of their pair: raising where either read raises, UNKNOWN where either reads
        /// null, and where both read a value, the order the cell parts them by where both readings' orders hold that each
        /// reads a string as itself and the cell holds strings on both sides, or where one reading reads two values the cell
        /// holds equal and its order holds that equal values read so compare equal (<see cref="LeftOrders"/>,
        /// <see cref="RightOrders"/>, which S's rows say, G3); TRUE or FALSE otherwise.
        /// </summary>
        Truths OnPair(PathCell.Pair pair, RawTruths columns, ReadingTable readings)
        {
            if (pair.Left is PathCell.Unknown || pair.Right is PathCell.Unknown)
                return Truths.Any;

            var (left, right) = (EvaluatorOf(Reader, readings, CosmosValueClasses.Of(pair.Left)).Reading, EvaluatorOf(Against!, readings, CosmosValueClasses.Of(pair.Right)).Reading);
            var (l, r) = (left.NullOn(pair.Left, columns), right.NullOn(pair.Right, columns));
            var truths = ((l | r) & Truths.Raises) | (((l | r) & Truths.True) != 0 ? Truths.Unknown : 0);
            if ((l & Truths.False) == 0 || (r & Truths.False) == 0)
                return truths;

            var placed = pair.Order is int order && (Reader == Against && order == 0 && LeftOrders.Contains(Reflexive)
                || LeftOrders.Contains(Itself) && RightOrders.Contains(Itself) && HoldsStrings(pair.Left) && HoldsStrings(pair.Right));
            return truths | (placed ? Ordered(Test, pair.Order!.Value) : Truths.True | Truths.False);
        }

        /// <summary>A cell of strings, or a point holding one.</summary>
        static bool HoldsStrings(PathCell cell) => cell is PathCell.Strings || cell is PathCell.Point { Value: var v } && IsString(v);

        static Truths Ordered(string op, int order) => (op switch
        {
            Equal => order == 0,
            NotEqual => order != 0,
            Less => order < 0,
            LessOrEqual => order <= 0,
            Greater => order > 0,
            _ => order >= 0,
        }) ? Truths.True : Truths.False;

        /// <summary>
        /// The <c>Calcite.*</c> rows the reading's answers on a cell rest on: the rows naming the reading and each it is
        /// evaluated as, and what each one's evaluation rests on beside them; a match, <c>LIKE</c>'s units' too; a comparison
        /// of two readings, both's.
        /// </summary>
        internal ValueSet<string> RestsOn(ReadingTable readings) => Kinds is not null ? readings.TestRows(Test)
            : Against is not null ? RestsOf(Reader, readings).Union(RestsOf(Against, readings))
            : Test == DefinedTest ? ValueSet<string>.Empty
            : Test == LikeTest ? RestsOf(Reader, readings).Union(readings.UnitRows) : RestsOf(Reader, readings);

        /// <summary>What a reader's answers rest on: its rows', and each reading's it is evaluated as.</summary>
        static ValueSet<string> RestsOf(string reader, ReadingTable readings) =>
            readings[reader].As.AsEnumerable().Select(a => a.Reader).Prepend(reader).Distinct()
                .Aggregate(ValueSet<string>.Empty, (rests, by) => rests.Union(readings[by].Cites).Union(Evaluate(by, readings).Rests));

        /// <summary>
        /// The reading a reader is evaluated as over a class, and that reading's name: the one the readings rules say it is
        /// read as there, or its own, with the rows they hand it (G7); a reading nothing defines is anything and a raise.
        /// </summary>
        static (string By, Reading Reading) EvaluatorOf(string reader, ReadingTable readings, CosmosValueClass? kind)
        {
            var by = kind is { } k && readings[reader].AliasOf(k) is { } alias ? alias : reader;
            return (by, Evaluate(by, readings));
        }

        /// <summary>A reading evaluated as itself, with its rows.</summary>
        static Reading Evaluate(string reader, ReadingTable readings) => (Parsed.GetOrAdd(reader, Reading.Parse) ?? Reading.Unread).With(readings[reader]);

        /// <summary>
        /// <c>LIKE</c>'s truths from the null test's of the same value: UNKNOWN where the value is null, raising where reading
        /// it raises, and where it is not, the match, decided of a known string the rows read as itself.
        /// </summary>
        Truths Matched(Truths isNull, string? literal, ValueSet<LikeUnit> units) =>
            (isNull & Truths.Raises) | ((isNull & Truths.True) != 0 ? Truths.Unknown : 0)
            | ((isNull & Truths.False) == 0 ? 0
                : literal is not null && Like(StringOf(literal), StringOf(Operand!), units) is bool match ? match ? Truths.True : Truths.False
                : Truths.True | Truths.False);

        /// <summary>
        /// <c>LIKE</c> on a cell, S's (h07): UNKNOWN where the null test finds null, raising where it raises, and where it
        /// finds a value, the match, its pattern read by Calcite's rows of its units (G7), so one they leave unread is either.
        /// A string the reading's rows read as itself is matched; a cell an affix cut by the pattern's spelling matches as
        /// cut where each unit read as a run or one reads the cell's classes as the spelling does (<see cref="LikePattern.OnLike"/>,
        /// <see cref="LikePattern.OnNotLike"/>), and by <c>%</c>, which no cut parts, as the half every string is in, a number's
        /// rendering of ASCII and a structure's of any class alike. A class the rows read as rendered is matched as its
        /// rendering: a boolean as <c>true</c> or <c>false</c>, a number in its grammar's units (<see cref="Renderings.MayBeNumberUnit"/>),
        /// so a pattern holding another unit as itself misses every number, and a structure between its brackets
        /// (<see cref="Renderings.MayRenderStructureLike"/>). Anything else is either.
        /// </summary>
        Truths LikeOn(PathCell cell, Truths isNull, ReadingRows rows, ValueSet<LikeUnit> units)
        {
            var truths = (isNull & Truths.Raises) | ((isNull & Truths.True) != 0 ? Truths.Unknown : 0);
            if ((isNull & Truths.False) == 0)
                return truths;

            var pattern = LikePattern.Read(StringOf(Operand!), units);
            var universal = pattern is not null && AffixPattern.Universal(pattern.Spelled);
            var renders = CosmosValueClasses.Of(cell) is { } kind && rows.ReadsAs(kind, CalciteRead.Renders);
            static Truths Of(bool? match) => match is bool m ? m ? Truths.True : Truths.False : Truths.True | Truths.False;
            return truths | (pattern is null ? Truths.True | Truths.False : cell switch
            {
                PathCell.Point { Value: var v } when IsString(v) => Of(pattern.Matches(StringOf(v))),
                PathCell.Strings s when universal => Of(pattern.OnLike(s.Classes)),
                PathCell.Strings s when s.Like?.Contains(pattern.Spelled) == true => Of(pattern.OnLike(s.Classes)),
                PathCell.Strings s when s.NotLike?.Contains(pattern.Spelled) == true => Of(pattern.OnNotLike(s.Classes)),
                PathCell.Strings => Truths.True | Truths.False,
                PathCell.Point { Value: var v } when renders && TypeOf(v) == BooleanType => Of(pattern.Matches(Renderings.BooleanText(v))),
                PathCell.Point { Value: var v } when renders && IsNumber(v) => Of(universal ? pattern.OnLike(NoBand) : MayBeNumber(pattern) ? null : false),
                PathCell.Numbers when renders => Of(universal ? pattern.OnLike(NoBand) : MayBeNumber(pattern) ? null : false),
                PathCell.Structure { Type: var type } when renders => Of(universal ? pattern.OnLike(AllBands) : Renderings.MayRenderStructureLike(type, pattern) ? null : false),
                _ => Truths.True | Truths.False,
            });
        }

        /// <summary>Whether a pattern may match a number's rendering: each unit it reads as itself is one of the grammar's.</summary>
        static bool MayBeNumber(LikePattern pattern) =>
            pattern.Units.All(u => u.Row.Reads != LikeUnit.Itself || u.Unit.Length == 1 && Renderings.MayBeNumberUnit(u.Unit[0]));

        /// <summary>
        /// Whether a pattern matches text as <c>LIKE</c> reads each unit of it (<paramref name="units"/>, G7, <see cref="LikePattern"/>):
        /// a run of any characters, one code point in no class its row names, or the character itself; a surrogate pair is
        /// one unit of either. <c>null</c> for a pattern holding a unit no row reads, or read where its row was not measured.
        /// </summary>
        internal static bool? Like(string text, string pattern, ValueSet<LikeUnit> units) => LikePattern.Read(pattern, units)?.Matches(text);

        /// <summary>A comparison with SQL's null by an operator null over a null operand: UNKNOWN, beside a raise where reading the value raises.</summary>
        static Truths ComparedWithNull(Truths isNull) => (isNull & Truths.Raises) | Truths.Unknown;

        /// <summary>
        /// What a comparison with a parameter answers where a null test answers so: it reads the value as the null test
        /// does, so raises where that read does, and is anything where that read finds a value.
        /// </summary>
        static Truths ComparedWhereNotNull(Truths isNull) =>
            (isNull & Truths.Raises) | ((isNull & Truths.False) != 0 ? Truths.True | Truths.False | Truths.Unknown : 0);

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

            /// <summary>The class a state is of, which the rows key a reading by; <c>null</c> for one of no one class.</summary>
            public static CosmosValueClass? ClassOf(State state) => state switch
            {
                Absent => CosmosValueClass.Absent,
                Null => CosmosValueClass.Null,
                Structure { Type: var type } => CosmosValueClasses.OfKind(type),
                Scalar { Value: var v } => CosmosValueClasses.Of(v),
                AnyString => CosmosValueClass.String,
                AnyNumber => CosmosValueClass.Number,
                _ => null,
            };

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
                if (reader == TextReader)
                    return new Text(structures: false);

                if (reader == RaisesReader)
                    return new Raising();

                // The raw value, through every column's union or through the one its reader names (rawtyped).
                if (IsRaw(reader))
                    return new Raw(reader);

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
                    var raw = IsRaw(inner);
                    return (target, inner) switch
                    {
                        ("VARCHAR", _) when raw => new RawText(reader),
                        ("CHAR", _) when raw => new Text(structures: true),
                        ("VARCHAR" or "CHAR", TextReader) => new Text(structures: false),
                        ("INTEGER" or "BIGINT" or "DOUBLE", TextReader) => new NumberCast(target),
                        ("BOOLEAN", TextReader) => new BooleanCast(ReadingRows.None),
                        _ => null,
                    };
                }

                return null;
            }

            /// <summary>A reading nothing defines: anything and a raise of every test, and nothing said of a row it keeps.</summary>
            public static readonly Reading Unread = new Unknown();

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

            // Beside the raw columns' truths, which a cast of a raw value to text alone reads: by default, what it keeps without.
            public virtual PathValues KeptEqual(string literal, RawTruths columns) => KeptEqual(literal);

            /// <summary>
            /// What a path of these types holds where an equality with the literal is TRUE: by default what it holds met with
            /// them; by a reading that joins cases of different types, each case met first.
            /// </summary>
            public virtual PathValues KeptEqual(string literal, RawTruths columns, ValueSet<string> types) =>
                PathValues.Meet(KeptEqual(literal, columns), PathValues.OfTypes(types));

            public virtual PathValues KeptUnequal(string literal, RawTruths columns) => KeptUnequal(literal);

            public virtual PathValues KeptOrder(string op, string literal, RawTruths columns) => KeptOrder(op, literal);

            public virtual PathValues KeptNull(bool truth, RawTruths columns) => KeptNull(truth);

            public virtual PathValues KeptParameter(RawTruths columns) => KeptParameter();

            /// <summary>
            /// The numbers whose double is a number's, of either kind: its rounding interval (<see cref="DoubleBound.Rounding"/>),
            /// what a reading that compares by a double keeps beside it; every number where no decimal bounds it.
            /// </summary>
            protected static PathValues RoundingTo(double d)
            {
                var kept = PathValues.OfKinds(ValueSet.Of(IntegerType, FractionType));
                var (low, high) = DoubleBound.Rounding(d);
                if (low is decimal l)
                    kept = PathValues.Meet(kept, PathValues.Above(Bound.AtLeast(l)));
                if (high is decimal h)
                    kept = PathValues.Meet(kept, PathValues.Below(Bound.AtMost(h)));

                return kept;
            }

            /// <summary>
            /// The join of two states, a side that holds no number read as bounding none: a bound binds numbers alone, so a
            /// side of strings or booleans takes the other's, and the numbers' bounds survive the join. A side of no kind
            /// holds nothing, and is no side: <see cref="PathValues.Join"/> reads its domain, which none names, as any value.
            /// </summary>
            internal static PathValues Either(PathValues a, PathValues b)
            {
                static bool Numbers(PathValues x) => x.Kinds.Contains(IntegerType) || x.Kinds.Contains(FractionType);

                if (a.Kinds.Count == 0)
                    return b;
                if (b.Kinds.Count == 0)
                    return a;

                if (Numbers(a) == false)
                    a = a with { Lower = b.Lower, Upper = b.Upper };
                if (Numbers(b) == false)
                    b = b with { Lower = a.Lower, Upper = a.Upper };

                return PathValues.Join(a, b);
            }

            /// <summary>
            /// The numbers that may render as a text: an integer as its digits, which hold no point, and any other number as
            /// Java spells its double, which always holds one and parses back to it, so every number whose double the
            /// text's is; none where the text is no number's rendering. Digits are every number whose double is theirs too,
            /// since the service returns a whole double as its digits (6.0000000000000001 as <c>6</c>).
            /// <paramref name="digits"/> is the integers digits render: any, or those within a long, past which a reader
            /// boxes a double.
            /// </summary>
            protected static PathValues RenderedAs(string text, bool digits)
            {
                if (Renderings.MayBeNumberText(text) == false)
                    return PathValues.OfKinds(ValueSet<string>.Empty);

                if (text.Contains('.'))
                    return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? RoundingTo(d) : PathValues.OfKinds(ValueSet.Of(IntegerType, FractionType));

                if (text.IndexOfAny(['e', 'E']) >= 0)
                    return PathValues.OfKinds(ValueSet<string>.Empty);

                if (Renderings.TryExactNumber(text, out var x) == false)
                    return digits ? PathValues.OfKinds(ValueSet.Of(IntegerType, FractionType)) : PathValues.OfKinds(ValueSet<string>.Empty);

                return digits || x >= long.MinValue && x <= long.MaxValue ? RoundingTo(DoubleBound.Of(x)) : PathValues.OfKinds(ValueSet<string>.Empty);
            }

            /// <summary>The rows the cell methods rest on.</summary>
            public virtual ValueSet<string> Rests => ValueSet<string>.Empty;

            /// <summary>The reading as it reads S's rows of it (G7): itself, for a reading that reads none.</summary>
            public virtual Reading With(ReadingRows rows) => this;

            // A reading's answers on a cell: by default, its answers on the state the cell lies in.
            public virtual Truths EqualOn(PathCell cell, string literal) => Equal(cell.Coarse, literal);

            public virtual Truths OrderOn(PathCell cell, string op, string literal) => Order(cell.Coarse, op, literal);

            public virtual Truths NullOn(PathCell cell) => Null(cell.Coarse);

            // Beside the raw columns' truths, S's union (h12), which a raw reading alone reads.
            public virtual Truths EqualOn(PathCell cell, string literal, RawTruths columns) => EqualOn(cell, literal);

            public virtual Truths OrderOn(PathCell cell, string op, string literal, RawTruths columns) => OrderOn(cell, op, literal);

            public virtual Truths NullOn(PathCell cell, RawTruths columns) => NullOn(cell);

            /// <summary>
            /// The truth values of a comparison by the operator with a parameter on a cell, beside a raise wherever Calcite
            /// may raise whatever the parameter is bound to (<c>Calcite.AParameterComparesAsItsComparand</c>): by default the
            /// reading's own answer to a parameter, beside its null test's, whose read of the value the comparison makes.
            /// </summary>
            public virtual Truths ParameterOn(PathCell cell, string op, RawTruths columns) => Parameter(cell.Coarse) | ComparedWhereNotNull(NullOn(cell, columns));

            /// <summary>
            /// The test a comparison with a parameter reads its column's key by: an ordering's own, and <c>=</c>'s for
            /// <c>&lt;&gt;</c>, which no key holds, as a comparison with a literal swaps <c>=</c>'s answer for it.
            /// </summary>
            protected static string KeyedBy(string op) => op == NotEqual ? PredicateAtom.Equal : op;

            /// <summary>Whether a comparison holds of a value that compares with the literal as the sign says.</summary>
            static bool Ordered(string op, int sign) => op switch
            {
                PredicateAtom.Equal => sign == 0,
                Less => sign < 0,
                LessOrEqual => sign <= 0,
                Greater => sign > 0,
                _ => sign >= 0,
            };

            /// <summary>Whether a cell holds numbers alone.</summary>
            static bool Numeric(PathCell cell) => cell is PathCell.Numbers || cell is PathCell.Point { Value: var v } && IsNumber(v);

            /// <summary>What a comparison answers of values on these sides of the literal, beside what else it may; either where a side is not told.</summary>
            static Truths Placed(string op, IEnumerable<int?> sides, Truths beside) => sides.Aggregate(beside, (truths, side) => truths | (side is int sign ? Ordered(op, sign) ? T : F : T | F));

            /// <summary>
            /// A reading nothing defines (<see cref="Unread"/>); read with rows that say what a test answers over a class
            /// whatever the value, the reading its rows alone define (<see cref="Answered"/>).
            /// </summary>
            sealed class Unknown : Reading
            {

                public override Reading With(ReadingRows rows) => rows.Whole.Count == 0 && rows.Compared.Count == 0 ? this : new Answered(rows);

                public override Truths Equal(State state, string literal) => Truths.Any | R;

                public override Truths Order(State state, string op, string literal) => Truths.Any | R;

                public override Truths Null(State state) => Truths.Any | R;

                public override Truths Parameter(State state) => Truths.Any | R;

                public override PathValues KeptEqual(string literal) => PathValues.Anything;

                public override PathValues KeptUnequal(string literal) => PathValues.Anything;

                public override PathValues KeptOrder(string op, string literal) => PathValues.Anything;

                public override PathValues KeptNull(bool truth) => PathValues.Anything;

                public override PathValues KeptParameter() => PathValues.Anything;

            }

            /// <summary>
            /// A reading no evaluator reads but its rows (G8), as <c>Calcite.JsonQueryAnswersStructureOnly</c>'s reading of a
            /// <c>JSON_QUERY</c>: what a test answers over a class is what its rows say it answers whatever the value, and
            /// anything and a raise over a class they leave to the value or say nothing of. Of a row it keeps, only where its
            /// null test answers is said; a comparison keeps anything.
            /// </summary>
            sealed class Answered(ReadingRows rows) : Reading
            {

                public override Reading With(ReadingRows rows) => new Answered(rows);

                /// <summary>What the test answers over the class, whatever the value, where the rows say; anything and a raise otherwise.</summary>
                Truths Answer(CosmosValueClass? kind, CosmosServiceTest test) =>
                    kind is { } k && rows.ComparedOf(k, test).Count == 0 && rows.WholeOf(k, test) is var whole && whole != Truths.None ? whole : Truths.Any | R;

                public override Truths Equal(State state, string literal) => Answer(State.ClassOf(state), CosmosServiceTest.Equal);

                public override Truths Order(State state, string op, string literal) => Answer(State.ClassOf(state), ServiceTestOf(op));

                public override Truths Null(State state) => Answer(State.ClassOf(state), CosmosServiceTest.IsNull);

                public override Truths Parameter(State state) => Answer(State.ClassOf(state), CosmosServiceTest.Equal) | ComparedWhereNotNull(Null(state));

                public override PathValues KeptEqual(string literal) => PathValues.Anything;

                public override PathValues KeptUnequal(string literal) => PathValues.Anything;

                public override PathValues KeptOrder(string op, string literal) => PathValues.Anything;

                public override PathValues KeptNull(bool truth) =>
                    CosmosValueClasses.Kinds.AsEnumerable().Where(kind => (Answer(kind, CosmosServiceTest.IsNull) & (truth ? T : F)) != 0)
                        .Aggregate(PathValues.OfKinds(ValueSet<string>.Empty), (kept, kind) => PathValues.Join(kept, PathValues.OfKinds(KindsOf([kind]))));

                public override PathValues KeptParameter() => PathValues.Anything;

            }

            /// <summary>What a body that raises over every value reads (<see cref="RaisesReader"/>): nothing, so it keeps no row either way.</summary>
            sealed class Raising : Reading
            {

                public override Truths Equal(State state, string literal) => R;

                public override Truths Order(State state, string op, string literal) => R;

                public override Truths Null(State state) => R;

                public override Truths Parameter(State state) => R;

                public override PathValues KeptEqual(string literal) => PathValues.OfKinds(ValueSet<string>.Empty);

                public override PathValues KeptUnequal(string literal) => PathValues.OfKinds(ValueSet<string>.Empty);

                public override PathValues KeptOrder(string op, string literal) => PathValues.OfKinds(ValueSet<string>.Empty);

                public override PathValues KeptNull(bool truth) => PathValues.OfKinds(ValueSet<string>.Empty);

                public override PathValues KeptParameter() => PathValues.OfKinds(ValueSet<string>.Empty);

            }

            /// <summary>
            /// <c>JSON_VALUE</c> read as text, and the cast to text over a raw value as the facts graph reads it, an
            /// <c>ANY</c>'s rendering, which renders an object and an array as well and whose null it leaves open.
            /// </summary>
            /// <remarks>
            /// A string renders as itself and a boolean as Calcite's lowercase <c>true</c> or <c>false</c>. A number
            /// renders as Java spells what the service returns, a whole number within a long as its digits however it was
            /// written, <c>30.0</c> as <c>30</c> (<c>Service.IntegralNumbersReadBackAsHeld</c>), and the theory spells both
            /// <c>integer|30</c>. The reading reads no spelling, only the number's double
            /// (<see cref="Renderings.MayRenderAs"/>): a number whose double is the text's may render as the text or not,
            /// and one whose double is not never does, which is sound and loose, <c>'30.0'</c> admitting a stored 30
            /// (<c>TODO.md</c>). That is the rendering preimage: <c>'30'</c> admits the string <c>"30"</c> and every number
            /// whose double is 30, and a declared string leaves only the first. The service holds a number not written as
            /// an int64 as its double, so <c>'0.1'</c> admits a stored <c>0.10000000000000001</c> and <c>'6'</c> a stored
            /// 6.0000000000000001, each beside every number of its rounding interval.
            /// </remarks>
            class Text : Reading
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

                public override PathValues KeptEqual(string literal) => KeptEqualAmong(literal, AllTypes);

                public override PathValues KeptEqual(string literal, RawTruths columns, ValueSet<string> types) => KeptEqualAmong(literal, types);

                /// <summary>
                /// What a path of these types holds where an equality with the literal is TRUE: the scalars the text is, the
                /// structures that may render as it and the numbers that may, each case met with the types before they are
                /// joined, so a declared string keeps the text's value beside the numbers' window.
                /// </summary>
                PathValues KeptEqualAmong(string literal, ValueSet<string> types)
                {
                    var among = PathValues.OfTypes(types);
                    if (IsString(literal) == false)
                        return among;

                    var text = StringOf(literal);
                    var kinds = new List<string> { StringType };
                    var values = new List<string> { Of(text) };

                    if (text is "true" or "false")
                    {
                        kinds.Add(BooleanType);
                        values.Add(Of(text == "true"));
                    }

                    if (_structures && text == "null")
                    {
                        kinds.Add(NullType);
                        values.Add(NullValue);
                    }

                    var kept = PathValues.Meet(PathValues.Meet(PathValues.OfKinds(ValueSet.From(kinds)), PathValues.OneOf(ValueSet.From(values))), among);
                    if (_structures && text.StartsWith('['))
                        kept = Either(kept, PathValues.Meet(PathValues.OfKinds(ValueSet.Of(ArrayType)), among));
                    if (_structures && text.StartsWith('{'))
                        kept = Either(kept, PathValues.Meet(PathValues.OfKinds(ValueSet.Of(ObjectType)), among));

                    return Renderings.MayBeNumberText(text) ? Either(kept, PathValues.Meet(RenderedAs(text, digits: true), among)) : kept;
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

                /// <summary>
                /// An interval of strings holds the literal or not; a cell of numbers whose doubles all lie to one side of the
                /// text's renders none as it, a number rendering as its double (<see cref="Renderings.MayRenderAs"/>); anything
                /// else answers as its state.
                /// </summary>
                public override Truths EqualOn(PathCell cell, string literal) => cell switch
                {
                    PathCell.Strings when IsString(literal) => cell.Contains(literal) ? T | F : F,
                    PathCell.Numbers n when IsString(literal) && Renderings.MayBeNumberText(StringOf(literal))
                        && double.TryParse(StringOf(literal), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && n.DoubleSide(d) is -1 or 1 => F,
                    _ => Equal(cell.Coarse, literal),
                };

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
            /// The cast to text of a raw value on a cell: its columns' union (<c>Calcite.CastOfARawValueToText</c>), or the one
            /// column its reader names, S's. The facts graph's coarse verdicts read the rendering alone (<c>TODO.md</c>); what a
            /// row it keeps holds is its column's answer, as a cell reads it.
            /// </summary>
            /// <param name="castOfRaw">The reading's spelling, which keys its columns' truths.</param>
            sealed class RawText(string castOfRaw) : Text(structures: true)
            {

                static readonly ValueSet<string> Rows = ValueSet.Of("Calcite.CastOfARawValueToText", "Calcite.TextOrderIsByCodeUnit");

                public override ValueSet<string> Rests => Rows;

                /// <summary>
                /// A comparison on a cell through the union, as S's answer on the cell's key says (G5): what it states whole, and
                /// where a column compares what it casts the value to, the rendering compared on the cell. The lattice's top for
                /// a cell of no one class.
                /// </summary>
                Truths AsText(PathCell cell, string op, string? literal, RawTruths columns, Func<Truths> rendered) =>
                    RawKey.On(castOfRaw, cell, op, literal) is { } key ? columns[key].On(rendered) : RawAnswer.Unanswered.Whole;

                /// <summary>
                /// Whether a number of the cell may render as the text through an <c>ANY</c>: a long as its digits, which hold no
                /// point, and any other number as Java spells its double, which always holds one and parses back to the double;
                /// and a number whose double is whole and within a long as that double's digits, as the service returns it.
                /// </summary>
                static bool MayRender(PathCell cell, string text)
                {
                    if (Renderings.MayBeNumberText(text) == false)
                        return false;

                    if (text.Contains('.'))
                        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) == false || cell switch
                        {
                            PathCell.Point { Value: var v } => DoubleBound.Of(v) == d,
                            PathCell.Numbers n => n.DoubleSide(d) is not (-1 or 1),
                            _ => true,
                        };

                    return text.IndexOfAny(['e', 'E']) < 0 && Renderings.TryExactNumber(text, out var x) && x >= long.MinValue && x <= long.MaxValue && cell switch
                    {
                        PathCell.Point { Value: var v } => TryNumberOf(v, out var y) && y == x || DoubleBound.Of(v) == DoubleBound.Of(x),
                        PathCell.Numbers n => n.Integral && n.Contains(Of(x)) || n.DoubleSide(DoubleBound.Of(x)) is not (-1 or 1),
                        _ => true,
                    };
                }

                public override Truths EqualOn(PathCell cell, string literal, RawTruths columns) => IsString(literal) == false ? Equal(cell.Coarse, literal)
                    : AsText(cell, PredicateAtom.Equal, literal, columns, () => Numeric(cell) ? MayRender(cell, StringOf(literal)) ? T | F : F : base.EqualOn(cell, literal));

                public override Truths OrderOn(PathCell cell, string op, string literal, RawTruths columns) => IsString(literal) == false ? Order(cell.Coarse, op, literal)
                    : AsText(cell, op, literal, columns, () => base.OrderOn(cell, op, literal));

                public override Truths NullOn(PathCell cell, RawTruths columns) => AsText(cell, NullTest, null, columns, () => T | F);

                /// <summary>
                /// What a row the cast takes a truth value on holds, class by class, as its column's answer on the class's key
                /// says (G5), as <see cref="AsText"/> reads a cell's: the class where the truths stated whole hold the truth,
                /// those of the class the rendering keeps where the column compares the rendering on the cell, and nothing where
                /// it fails or raises alone. A key no column answers holds anything, so keeps the class.
                /// </summary>
                PathValues ByColumn(CosmosServiceTest test, string? literal, Truths truth, RawTruths columns, Func<CosmosValueClass, PathValues> rendered) =>
                    ByColumn(test, literal, truth, columns, rendered, AllTypes);

                /// <summary>What a row of these types holds, as <see cref="ByColumn(CosmosServiceTest, string?, Truths, RawTruths, Func{CosmosValueClass, PathValues})"/> says, each class met with them before the classes are joined.</summary>
                PathValues ByColumn(CosmosServiceTest test, string? literal, Truths truth, RawTruths columns, Func<CosmosValueClass, PathValues> rendered, ValueSet<string> types)
                {
                    var among = PathValues.OfTypes(types);
                    var kept = PathValues.OfKinds(ValueSet<string>.Empty);
                    foreach (var kind in CosmosValueClasses.Kinds)
                    {
                        var answer = columns[RawKey.Of(castOfRaw, kind, test, literal is null ? CosmosValueClass.None : CosmosValueClasses.Of(literal))];
                        if ((answer.Whole & truth) != 0)
                            kept = Either(kept, PathValues.Meet(Holding(kind), among));
                        else if ((answer.Compared & truth) != 0)
                            kept = Either(kept, PathValues.Meet(PathValues.Meet(Holding(kind), rendered(kind)), among));
                    }

                    return kept;
                }

                /// <summary>The values of a class: its kinds, and of a boolean its one value.</summary>
                static PathValues Holding(CosmosValueClass kind) => kind switch
                {
                    CosmosValueClass.True or CosmosValueClass.False => PathValues.Meet(PathValues.OfKinds(ValueSet.Of(BooleanType)), PathValues.OneOf(ValueSet.Of(Of(kind == CosmosValueClass.True)))),
                    _ => PathValues.OfKinds(KindsOf([kind])),
                };

                /// <summary>
                /// Equal where the column's answer is, a number through an <c>ANY</c> where its rendering may be the text
                /// (<see cref="MayRender"/>): a long or a whole double as its digits, any other number as its double's spelling;
                /// and a string where it is the text, which a class's own keeps apart from the numbers' window.
                /// </summary>
                public override PathValues KeptEqual(string literal, RawTruths columns) => KeptEqual(literal, columns, AllTypes);

                public override PathValues KeptEqual(string literal, RawTruths columns, ValueSet<string> types) => IsString(literal) == false ? PathValues.Meet(KeptEqual(literal), PathValues.OfTypes(types))
                    : ByColumn(CosmosServiceTest.Equal, literal, T, columns, kind => kind switch
                    {
                        CosmosValueClass.Number => RenderedAs(StringOf(literal), digits: false),
                        CosmosValueClass.String => PathValues.OneOf(ValueSet.Of(literal)),
                        _ => KeptEqual(literal),
                    }, types);

                public override PathValues KeptUnequal(string literal, RawTruths columns) => IsString(literal) == false ? KeptUnequal(literal)
                    : ByColumn(CosmosServiceTest.Equal, literal, F, columns, _ => KeptUnequal(literal));

                public override PathValues KeptOrder(string op, string literal, RawTruths columns) => IsString(literal) == false ? KeptOrder(op, literal)
                    : ByColumn(ServiceTestOf(op), literal, T, columns, _ => KeptOrder(op, literal));

                public override PathValues KeptNull(bool truth, RawTruths columns) => ByColumn(CosmosServiceTest.IsNull, null, truth ? T : F, columns, _ => PathValues.Anything);

                /// <summary>TRUE or FALSE only where the cast is not null, as the null test's FALSE keeps.</summary>
                public override PathValues KeptParameter(RawTruths columns) => KeptNull(false, columns);

                /// <summary>
                /// Beside the reading's own answer, what its column answers beside a parameter, whichever class it is bound to:
                /// the key with no literal's class, which the readings rules fill as the join over every class.
                /// </summary>
                public override Truths ParameterOn(PathCell cell, string op, RawTruths columns) =>
                    base.ParameterOn(cell, op, columns) | AsText(cell, KeyedBy(op), null, columns, () => T | F);

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
            /// <param name="raw">The reading's spelling, which keys its columns' truths: <c>raw</c>, or <c>raw</c> in one column.</param>
            sealed class Raw(string raw) : Reading
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

                /// <summary>
                /// The literal; beside a number, every number whose double is the literal's, since an <c>ANY</c> compares a
                /// number not written as a long by its double (<c>Calcite.RawNullIsSqlNull</c>).
                /// </summary>
                public override PathValues KeptEqual(string literal) =>
                    TypeOf(literal) == NullType ? PathValues.Anything
                    : IsNumber(literal) ? RoundingTo(DoubleBound.Of(literal))
                    : PathValues.Meet(PathValues.OfKinds(ValueSet.Of(TypeOf(literal))), PathValues.OneOf(ValueSet.Of(literal)));

                public override PathValues KeptUnequal(string literal) =>
                    IsNumber(literal) || TypeOf(literal) == NullType ? PathValues.AnythingThere : PathValues.Meet(PathValues.AnythingThere, PathValues.Not(literal));

                public override PathValues KeptOrder(string op, string literal) => PathValues.AnythingThere;

                public override PathValues KeptNull(bool truth) =>
                    truth ? PathValues.OfKinds(ValueSet.Of(PathValues.AbsentKind, NullType)) : PathValues.AnythingThere;

                public override PathValues KeptParameter() => PathValues.AnythingThere;

                static readonly ValueSet<string> Rows = ValueSet.Of("Calcite.RawStringEqualityIsOrdinal", "Calcite.RawNullIsSqlNull");

                public override ValueSet<string> Rests => Rows;

                /// <summary>
                /// A comparison on a cell through the raw columns' union, as S's answer on the cell's key says (G5): what it states
                /// whole, a null's, a raise's and an answer across types among it, and where a column compares the value as one
                /// type with the literal, the comparison on the cell, <paramref name="alike"/>. The lattice's top for a cell of no
                /// one class.
                /// </summary>
                Truths Through(PathCell cell, string op, string? literal, RawTruths columns, Func<Truths> alike) =>
                    RawKey.On(raw, cell, op, literal) is { } key ? columns[key].On(alike) : RawAnswer.Unanswered.Whole;

                /// <summary>
                /// A string is equal to the one with its units alone (<c>Calcite.RawStringEqualityIsOrdinal</c>), and a number
                /// to a number literal through its box: an <c>ANY</c>'s, a long by its value and any other number by its double,
                /// which the theory spells alike written with a point, so both; and a <c>VARIANT</c>'s only where the literal's
                /// box, which the IR does not keep, is its own. So TRUE or FALSE where any reading of the box is equal. A literal
                /// of any class is keyed alike, a null's among them, and reads its key's row or none.
                /// </summary>
                public override Truths EqualOn(PathCell cell, string literal, RawTruths columns) => Through(cell, PredicateAtom.Equal, literal, columns, () => cell switch
                {
                    _ when IsNumber(literal) => TryNumberOf(literal, out var c) && cell.SidesOf(c, byValue: true, byDouble: true) is { } sides && sides.All(s => s is int side && side != 0) ? F : T | F,
                    PathCell.Point { Value: var v } => v == literal ? T : F,
                    _ => cell.Contains(literal) ? T | F : F,
                });

                /// <summary>
                /// Ordered as the type: a string by its UTF-16 units, a boolean false first, a number through its box, where two
                /// doubles compare by <c>Double.compareTo</c>, which puts a stored -0.0 below a double 0 the IR spells as 0
                /// (<c>Calcite.RawNullIsSqlNull</c>).
                /// </summary>
                public override Truths OrderOn(PathCell cell, string op, string literal, RawTruths columns) => Through(cell, op, literal, columns, () => cell switch
                {
                    _ when IsNumber(literal) => TryNumberOf(literal, out var c) && cell.SidesOf(c, byValue: true, byDouble: true) is { } sides
                        ? Placed(op, c == 0 && sides.Contains(0) ? [.. sides, null] : sides, Truths.None) : T | F,
                    PathCell.Point { Value: var v } => Ordered(op, Math.Sign(string.CompareOrdinal(v, literal))) ? T : F,
                    PathCell.Strings { High: string high } when string.CompareOrdinal(high, literal) <= 0 => Ordered(op, -1) ? T : F,
                    PathCell.Strings { Low: string low } when string.CompareOrdinal(low, literal) >= 0 => Ordered(op, 1) ? T : F,
                    _ => T | F,
                });

                /// <summary>As the union says: true of a null alone, and a raise where a column reads no value of the kind.</summary>
                public override Truths NullOn(PathCell cell, RawTruths columns) => Through(cell, NullTest, null, columns, () => T | F);

                /// <summary>
                /// Beside the reading's own answer, what its column answers beside a parameter, whichever class it is bound to:
                /// the key with no literal's class, which the readings rules fill as the join over every class, a
                /// <c>VARIANT</c> ordering's failure to compile, an <c>ANY</c> ordering's raise across types and a
                /// <c>VARCHAR</c> column's over any other value among it.
                /// </summary>
                public override Truths ParameterOn(PathCell cell, string op, RawTruths columns) =>
                    base.ParameterOn(cell, op, columns) | Through(cell, KeyedBy(op), null, columns, () => T | F);

            }

            /// <summary>
            /// <c>JSON_VALUE … RETURNING T</c>, which asserts the stored scalar is of <c>T</c>'s class and raises on anything
            /// else: a stored <c>30</c> under <c>RETURNING INTEGER</c>, which the service returns as <c>30</c> however it was
            /// written, never <c>30.5</c>, <c>"30"</c> or <c>true</c>.
            /// </summary>
            /// <remarks>
            /// So a row it keeps holds a value of the type, and is the literal where it is equal to it. A state is read with no
            /// spelling, so a row that is not absent or null may be any truth value, the one Calcite raises on included; a cell
            /// reads a number as the service returns it (<c>Raises</c>).
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
                    "DECIMAL" => new NumberCast("DECIMAL"),
                    "BOOLEAN" => new Typed(type, ValueSet.Of(BooleanType)),
                    _ => null,
                };

                /// <summary>
                /// A number equal as its cell is (<see cref="Number"/>), as the type and its column read one, beside the raise
                /// of a spelling of another class; anything else of the type as itself.
                /// </summary>
                public override Truths Equal(State state, string literal) => state switch
                {
                    State.Absent or State.Null or State.Structure => U,
                    State.Scalar { Value: var v } when IsNumber(v) && TryNumberOf(literal, out var c) && Number(PathCell.Of(state), PredicateAtom.Equal, c) is Truths truths => truths | R,
                    State.Scalar { Value: var v } when _kinds.Contains(TypeOf(v)) => (Renderings.SameValue(v, literal) ? T : F) | R,
                    _ => Answers(state, T | F),
                };

                public override Truths Order(State state, string op, string literal) => Answers(state, T | F);

                public override Truths Null(State state) => state is State.Absent or State.Null or State.Structure ? T : Answers(state, F);

                public override Truths Parameter(State state) => Answers(state, T | F | U);

                /// <summary>
                /// What a value of the type answers, beside raising where its spelling is of another class, a fraction an
                /// integer type's column reads as its whole double among it (<see cref="Whole"/>); and a value of another
                /// type only raises. Absent, null and a structure read as SQL null.
                /// </summary>
                Truths Answers(State state, Truths ofType) => state switch
                {
                    State.Absent or State.Null or State.Structure => ofType == F ? T : U,
                    State.Scalar { Value: var v } => _kinds.Contains(TypeOf(v)) || Whole(PathCell.Of(state)) != Truths.None ? ofType | R : R,
                    State.AnyString => _kinds.Contains(StringType) ? ofType | R : R,
                    State.AnyNumber n => _kinds.Contains(n.Integral ? IntegerType : FractionType) || Limits is not null ? ofType | R : R,
                    _ => Truths.Any | R,
                };

                /// <summary>
                /// Where the reading is equal to a literal (<c>Calcite.ColumnReadsWholeNumbersAsItsType</c>): under <c>DOUBLE</c>
                /// and <c>FLOAT</c>, which compare doubles, every number whose double is the literal's; under <c>REAL</c>, which
                /// compares floats, every number whose float is; under an integer type, which its column reads a fraction
                /// whose double is whole as, every number whose double is a whole literal within the type, and none beside
                /// any other; a boolean the literal.
                /// </summary>
                public override PathValues KeptEqual(string literal)
                {
                    if (IsNumber(literal) && Limits is var (least, most))
                        return TryNumberOf(literal, out var c) == false ? Read
                            : c == decimal.Truncate(c) && c >= least && c <= most ? PathValues.Meet(Read, RoundingTo(DoubleBound.Of(c))) : PathValues.OfKinds(ValueSet<string>.Empty);

                    if (IsNumber(literal) && _type is "DOUBLE" or "FLOAT")
                        return RoundingTo(DoubleBound.Of(literal));

                    if (IsNumber(literal) && _type == "REAL")
                        return FloatRoundingTo(literal);

                    return PathValues.Meet(PathValues.OfKinds(_kinds.Intersect(ValueSet.Of(TypeOf(literal)))), PathValues.OneOf(ValueSet.Of(literal)));
                }

                /// <summary>
                /// Where the reading is unequal to a literal: every value it reads, but the literal where every spelling of it
                /// reads as what it is compared with. An integer type's column reads a number written with a point as its
                /// double, so 9007199254740993.0 as 9007199254740992 and 6.0000000000000001 as 6, each unequal to itself; and
                /// a <c>REAL</c> casts the literal to a float from its decimal or through its double, which may part at a tie.
                /// </summary>
                public override PathValues KeptUnequal(string literal) => ReadsAsItself(literal) ? PathValues.Meet(Read, PathValues.Not(literal)) : Read;

                bool ReadsAsItself(string literal)
                {
                    if (IsNumber(literal) == false)
                        return true;

                    if (Limits is not null)
                        return TryNumberOf(literal, out var c) && c == decimal.Truncate(c) && Math.Abs(c) <= 9007199254740992m;

                    if (_type == "REAL")
                        return TryNumberOf(literal, out var c) && (float)DoubleBound.Of(c) == float.Parse(c.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);

                    return true;
                }

                public override PathValues KeptOrder(string op, string literal) => Read;

                public override PathValues KeptNull(bool truth) =>
                    truth ? PathValues.OfKinds(ValueSet.Of(PathValues.AbsentKind, NullType, ObjectType, ArrayType)) : Read;

                public override PathValues KeptParameter() => Read;

                /// <summary>
                /// What the reading reads a value of: its kinds; under an integer type, whose column reads a fraction whose
                /// double is whole and within the type, either number within the type's range, a double's rounding beyond it.
                /// </summary>
                PathValues Read => Limits is var (least, most)
                    ? PathValues.Meet(PathValues.Meet(PathValues.OfKinds(ValueSet.Of(IntegerType, FractionType)), Below(RoundingTo(DoubleBound.Of(most)))), Above(RoundingTo(DoubleBound.Of(least))))
                    : PathValues.OfKinds(_kinds);

                static PathValues Below(PathValues window) => window.Upper is Bound upper ? PathValues.Below(upper) : PathValues.Anything;

                static PathValues Above(PathValues window) => window.Lower is Bound lower ? PathValues.Above(lower) : PathValues.Anything;

                /// <summary>
                /// The numbers a <c>REAL</c> reads as the float the literal is cast to: the float nearest each number's double,
                /// so every number whose double lies within the float's rounding interval. The literal is cast once from its
                /// decimal or through its double, which may part at a tie, so both floats are kept.
                /// </summary>
                static PathValues FloatRoundingTo(string literal)
                {
                    var d = DoubleBound.Of(literal);
                    var floats = TryNumberOf(literal, out var c) ? new[] { (float)d, float.Parse(c.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture) } : [(float)d];
                    var kept = PathValues.OfKinds(ValueSet<string>.Empty);
                    foreach (var f in floats)
                    {
                        if (float.IsFinite(f) == false)
                            return PathValues.OfKinds(ValueSet.Of(IntegerType, FractionType));

                        // A float's neighbours' midpoints with it are doubles exactly; a number rounds to a double within them.
                        var low = RoundingTo(((double)MathF.BitDecrement(f) + f) / 2);
                        var high = RoundingTo(((double)f + MathF.BitIncrement(f)) / 2);
                        kept = Either(kept, PathValues.Meet(PathValues.Meet(PathValues.OfKinds(ValueSet.Of(IntegerType, FractionType)), Above(low)), Below(high)));
                    }

                    return kept;
                }

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

                /// <summary>A number <see cref="Reads"/> reads is not null, and raises nowhere; anything else as its state.</summary>
                public override Truths NullOn(PathCell cell) => _type is "INTEGER" or "BIGINT" && Reads(cell) ? F : Null(cell.Coarse) | Unread(cell) | (Whole(cell) & F);

                static Truths Unread(PathCell cell) => cell is PathCell.Structure ? R : Truths.None;

                /// <summary>What an integer type's column may answer of a fraction whose double is whole, which it reads.</summary>
                Truths Whole(PathCell cell) => Limits is var (least, most) && (cell is PathCell.Numbers { Integral: false }
                    || cell is PathCell.Point { Value: var v } && TypeOf(v) == FractionType && DoubleBound.Of(v) is var r && Math.Floor(r) == r && r >= (double)least && r < (double)most + 1) ? T | F : Truths.None;

                (decimal Least, decimal Most)? Limits => IntegerLimits(_type);

                /// <summary>What a comparison answers on a number, or a cell of them; <c>null</c> for a reading this does not place.</summary>
                /// <remarks>
                /// An integer type's <c>RETURNING</c> reads an integer returned as digits within its range, and raises over
                /// anything else, under <c>BIGINT</c> over one an int holds. Its column reads that too, and any other number as its
                /// double where that is whole and within the type: a fraction that rounds to a whole double, and past 2^53 one the
                /// emulator holds as its double (<c>CosmosJson.GetInteger</c>). A <c>DOUBLE</c> or <c>FLOAT</c> compares doubles,
                /// correctly rounded, and <c>RETURNING</c> raises over digits (<see cref="Raises"/>); a <c>REAL</c> the float nearest a
                /// number's double with the literal cast to one, from its decimal or through its double, which may part at a tie,
                /// so a number is placed against both, and a cell of them not at all. Its <c>RETURNING</c> and a <c>FLOAT</c>'s may
                /// raise over any number, unmeasured.
                /// </remarks>
                Truths? Number(PathCell cell, string op, decimal c)
                {
                    if (Limits is null && _type is not ("DOUBLE" or "FLOAT" or "REAL") || cell is not (PathCell.Point or PathCell.Numbers)
                        || cell is PathCell.Point { Value: var v } && IsNumber(v) == false || _type == "REAL" && cell is PathCell.Numbers)
                        return null;

                    var (least, most) = Limits ?? (0m, 0m);

                    var d = DoubleBound.Of(c);
                    var sides = new List<int?>();
                    if (_type is "DOUBLE" or "FLOAT")
                        sides.Add(cell is PathCell.Numbers n0 ? n0.DoubleSide(d) : Math.Sign(DoubleBound.Of(((PathCell.Point)cell).Value).CompareTo(d)));
                    else if (_type == "REAL")
                    {
                        var r = (float)DoubleBound.Of(((PathCell.Point)cell).Value);
                        foreach (var f in new[] { (float)d, float.Parse(c.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture) })
                            sides.Add(Math.Sign(r.CompareTo(f)));
                    }
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

                    return Placed(op, sides, Raises(cell));
                }

                /// <summary>
                /// Where a number of the cell raises, read as the service returns it, whatever was written
                /// (<c>Service.IntegralNumbersReadBackAsHeld</c>): <c>RETURNING DOUBLE</c> over digits, which an integer comes back
                /// as and a fraction whose double is whole may (<see cref="Renderings.MayServeAsDigits"/>); <c>RETURNING INTEGER</c>
                /// and <c>BIGINT</c> but where both read every number as their column does (<see cref="Reads"/>); the others
                /// anywhere, unmeasured.
                /// </summary>
                Truths Raises(PathCell cell) => _type switch
                {
                    "DOUBLE" => cell switch
                    {
                        PathCell.Point { Value: var v } => Renderings.MayServeAsDigits(v) ? R : Truths.None,
                        PathCell.Numbers n => n.Integral || n.MayRoundToWhole(-Math.BitDecrement(Renderings.DigitsBelow), Math.BitDecrement(Renderings.DigitsBelow)) ? R : Truths.None,
                        _ => R,
                    },
                    "INTEGER" or "BIGINT" => Reads(cell) ? Truths.None : R,
                    _ => R,
                };

                /// <summary>
                /// Whether <c>RETURNING INTEGER</c> or <c>BIGINT</c> reads every number of the cell: an integer both targets
                /// return as digits (<see cref="Renderings.ServedAsDigits"/>), which Jackson boxes as an int within an int's range
                /// and as a long beyond it, and each reads its own box alone, within its type, where its column reads it too.
                /// </summary>
                bool Reads(PathCell cell)
                {
                    var (least, most) = ((decimal)int.MinValue, (decimal)int.MaxValue);
                    return (_type, cell) switch
                    {
                        (_, PathCell.Point { Value: var v }) => Renderings.ServedAsDigits(v) && TryNumberOf(v, out var x)
                            && (_type == "INTEGER" ? x >= least && x <= most : x < least || x > most),
                        ("INTEGER", PathCell.Numbers { Integral: true } n) => n.Side(least - 1) == 1 && n.Side(most + 1) == -1,
                        ("BIGINT", PathCell.Numbers { Integral: true } n) => (n.Side(most) == 1 || n.Side(least) == -1)
                            && n.Side((decimal)long.MinValue - 1) == 1 && n.DoubleSide(Renderings.LongCeiling) == -1,
                        _ => false,
                    };
                }

            }

            /// <summary>
            /// A cast of the text accessor to <c>BOOLEAN</c>, which parses the rendering (<c>Calcite.CastToBooleanParses</c>),
            /// as the readings rules say of it (G7), class by class: what a test answers over a class whatever the value
            /// (<see cref="ReadingRows.WholeOf"/>), its own read's and, for the null test, its operand's, beside what the value
            /// decides where the class is read as itself or parsed (<see cref="ReadingRows.ComparedOf"/>); anything and a raise
            /// over a class nothing says. What a value read as itself or parsed answers is the one evaluation: a boolean
            /// compared, a string parsed by Java's trim and an ASCII-case compare with <c>true</c> and <c>false</c>, decided only
            /// over the character classes its witnesses held.
            /// </summary>
            /// <remarks>
            /// A string is decided only within the classes measured: trimmed of every character up to U+0020, as Java's
            /// <c>String.trim</c> trims, and read in any ASCII case (<c>IrEvalCalciteTests.TheCastOfTheTextToBooleanAgreesWithCalcite</c>);
            /// any other string may read as either boolean or raise, no case folding beyond ASCII having been measured but
            /// <c>"falſe"</c>'s, which raises. A declaration that the path holds a boolean is what leaves the cast the stored value.
            /// </remarks>
            /// <param name="rows">The readings rules' rows of the reading.</param>
            sealed class BooleanCast(ReadingRows rows) : Reading
            {

                /// <summary>The classes the parse of a string was measured over, each with its refinements (<see cref="JsonValues.Closed"/>).</summary>
                readonly ValueSet<string> _measured = ClassSet(Closed(ClassMask(rows.BandsOf(CosmosValueClass.String))));

                public override Reading With(ReadingRows rows) => new BooleanCast(rows);

                /// <summary>The boolean a stored string parses as; <c>null</c> where it spells none, or is not decided here.</summary>
                bool? Parsed(string text)
                {
                    if (Decided(text) == false)
                        return null;

                    var trimmed = text.Trim(Renderings.JavaTrims);
                    return string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase) ? true : string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase) ? false : null;
                }

                /// <summary>Whether a stored string is decided to raise: within the classes measured, and spelling no boolean however trimmed and cased.</summary>
                bool Raises(string text) => Decided(text) && Parsed(text) is null;

                /// <summary>Whether a string's parse was measured over every class its characters are in.</summary>
                bool Decided(string text) => BandsOfText(text).AsEnumerable().All(_measured.Contains);

                /// <summary>
                /// What a test answers on a state, as the rows say of its class: what it answers whatever the value, beside what
                /// the value decides by each read the cell decides (<paramref name="boolean"/> of a boolean read, the parse of
                /// a string); anything and a raise of a class nothing says.
                /// </summary>
                Truths Answer(State state, CosmosServiceTest test, Func<bool, Truths> boolean)
                {
                    if (State.ClassOf(state) is not { } kind)
                        return Truths.Any | R;

                    var (whole, compared) = (rows.WholeOf(kind, test), rows.ComparedOf(kind, test));
                    if (whole == Truths.None && compared.Count == 0)
                        return Truths.Any | R;

                    foreach (var answer in compared.AsEnumerable())
                        whole |= answer switch
                        {
                            CalciteRead.Itself when state is State.Scalar { Value: var v } && TypeOf(v) == BooleanType => boolean(v == Of(true)),
                            CalciteRead.Parses when state is State.Scalar { Value: var v } && IsString(v) =>
                                Parsed(StringOf(v)) is bool b ? boolean(b) : Raises(StringOf(v)) ? R : boolean(true) | boolean(false) | R,
                            CalciteRead.Parses when state is State.AnyString => boolean(true) | boolean(false) | R,
                            _ => Truths.Any | R,
                        };

                    return whole;
                }

                public override Truths Equal(State state, string literal) =>
                    TypeOf(literal) == BooleanType ? Answer(state, CosmosServiceTest.Equal, b => b == (literal == Of(true)) ? T : F) : Truths.Any | R;

                // Calcite orders FALSE before TRUE; nothing finer is said of an ordering than that a boolean read gives one.
                public override Truths Order(State state, string op, string literal) =>
                    TypeOf(literal) == BooleanType ? Answer(state, ServiceTestOf(op), _ => T | F) : Truths.Any | R;

                public override Truths Null(State state) => Answer(state, CosmosServiceTest.IsNull, _ => Truths.Any | R);

                // A parameter may itself be null: an equality with it, whatever a boolean read is; and as a cell reads it,
                // anything where the null test, its operand's, finds a value.
                public override Truths Parameter(State state) => Answer(state, CosmosServiceTest.Equal, _ => T | F | U) | ComparedWhereNotNull(Null(state));

                /// <summary>The kinds of the classes over which a test may answer as given, the value read deciding where the cell decides.</summary>
                PathValues KeptWhere(CosmosServiceTest test, Truths truth, Func<CosmosValueClass, string, PathValues> decided) =>
                    CosmosValueClasses.Kinds.AsEnumerable().Aggregate(PathValues.OfKinds(ValueSet<string>.Empty), (kept, kind) =>
                    {
                        var of = PathValues.OfKinds(KindsOf([kind]));
                        if (rows.WholeOf(kind, test) == Truths.None && rows.ComparedOf(kind, test).Count == 0 || (rows.WholeOf(kind, test) & truth) != 0)
                            return PathValues.Join(kept, of);

                        return rows.ComparedOf(kind, test).AsEnumerable().Aggregate(kept, (k, answer) => PathValues.Join(k, PathValues.Meet(of, decided(kind, answer))));
                    });

                public override PathValues KeptEqual(string literal) => TypeOf(literal) == BooleanType
                    ? KeptWhere(CosmosServiceTest.Equal, T, (_, answer) => answer == CalciteRead.Itself ? PathValues.OneOf(ValueSet.Of(literal)) : PathValues.Anything)
                    : PathValues.Anything;

                public override PathValues KeptUnequal(string literal) => TypeOf(literal) == BooleanType
                    ? KeptWhere(CosmosServiceTest.Equal, F, (_, answer) => answer == CalciteRead.Itself ? PathValues.Not(literal) : PathValues.Anything)
                    : PathValues.Anything;

                public override PathValues KeptOrder(string op, string literal) =>
                    TypeOf(literal) == BooleanType ? KeptWhere(ServiceTestOf(op), T, (_, _) => PathValues.Anything) : PathValues.Anything;

                public override PathValues KeptNull(bool truth) => KeptWhere(CosmosServiceTest.IsNull, truth ? T : F, (_, _) => PathValues.Anything);

                // TRUE or FALSE only where the value read is not null.
                public override PathValues KeptParameter() => KeptNull(false);

            }

            /// <summary>
            /// A cast of the text accessor to <c>INTEGER</c>, <c>BIGINT</c> or <c>DOUBLE</c>, the casts a row measured
            /// (<c>measuredcast</c>), which parse a stored number's rendering and a stored string alike; and
            /// <c>RETURNING DECIMAL</c>, which is a parse of the rendering too.
            /// </summary>
            /// <remarks>
            /// <para>
            /// <b>No cast of a raw value is one</b> (h15b): no row measured one, and what it does to a number is no window's,
            /// over an <c>ANY</c> column a long beyond an int wrapping and over a <c>VARIANT</c> a raise.
            /// </para>
            /// <para>
            /// <b>It is a parse of the rendering</b>, which is a measurement
            /// (<c>CalciteJsonValueMeasurementTests.ACastOfTheAccessorToANumberParsesTheRendering</c>): Java's integer parse
            /// for <c>INTEGER</c> and <c>BIGINT</c>, which raises on the rendering <c>30.0</c>, <c>2.5</c>, <c>1.0E30</c> and
            /// anything beyond the type, where a cast of the number would have truncated or saturated. The service returns a
            /// whole number within a long as its digits however it was written, <c>30.0</c> as <c>30</c>
            /// (<c>Service.IntegralNumbersReadBackAsHeld</c>), and the cells read that spelling (<see cref="ParsesWhole"/>,
            /// <see cref="Whole"/>); the coarse answers, which the verdicts read, read none and put the parse's raise beside
            /// every number (<see cref="_parse"/>), so nothing is decided of one there (<c>TODO.md</c>). A double's parse
            /// reads every rendering of a number, and raises on none.
            /// </para>
            /// <para>
            /// <b>What it does to anything else is not</b>, and a string, a boolean, and over a raw value a null or a
            /// structure, may convert to anything or raise. A row that keeps a comparison holds a number within the
            /// window <see cref="Window"/> gives, or something that is not a number at all: the window binds numbers
            /// only, as a bound does. It is loose by a whole unit on purpose: converting a number to a number moves it by
            /// less than one whichever way the conversion rounds, and which way, and the sign, need not be decided to make
            /// it sound.
            /// </para>
            /// </remarks>
            sealed class NumberCast : Reading
            {

                static readonly ValueSet<string> Converts = ValueSet.Of(StringType, IntegerType, FractionType, BooleanType);

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

                static readonly ValueSet<string> DecimalRows = ValueSet.Of("Calcite.JsonValueRendersScalarsAsText", "Calcite.ReturningDecimalParsesTheRendering",
                    "Calcite.ColumnReadsWholeNumbersAsItsType");

                public override ValueSet<string> Rests => _parsesDecimal ? DecimalRows : TextRows;

                public override Truths EqualOn(PathCell cell, string literal) => On(cell, PredicateAtom.Equal, literal);

                public override Truths OrderOn(PathCell cell, string op, string literal) => On(cell, op, literal);

                /// <summary>A number an integer type's parse reads is not null, and raises nowhere (<see cref="ParsesWhole"/>); anything else as its state.</summary>
                public override Truths NullOn(PathCell cell) => ParsesWhole(cell) ? F : Null(cell.Coarse) | (_parsesDecimal && cell is PathCell.Structure ? R : Truths.None);

                /// <summary>
                /// Whether an integer type's parse reads every number of the cell's rendering: an integer within the type both
                /// targets return as digits (<see cref="Renderings.ServedAsDigits"/>), its rendering, however it was written.
                /// </summary>
                bool ParsesWhole(PathCell cell) => _saturates is var (least, most) && cell switch
                {
                    PathCell.Point { Value: var v } => Renderings.ServedAsDigits(v) && TryNumberOf(v, out var x) && x >= least && x <= most,
                    PathCell.Numbers { Integral: true } n => n.Side(least - 1) == 1 && n.Side(most + 1) == -1 && n.DoubleSide(Renderings.LongCeiling) == -1,
                    _ => false,
                };

                /// <summary>
                /// The conversion's answer, and under <c>DECIMAL</c> a <c>DECIMAL</c> column's beside it: the loader writes
                /// <c>typed:DECIMAL</c> for both, so a cell reads their union (decision D12).
                /// </summary>
                Truths On(PathCell cell, string op, string literal) => Converted(cell, op, literal) | (_parsesDecimal ? Column(cell, op, literal) : Truths.None);

                /// <summary>
                /// What a <c>DECIMAL</c> column answers (<c>Calcite.ColumnReadsWholeNumbersAsItsType</c>): a number's text
                /// compared exactly, where the clause reads a fraction through its double's rendering, the text as written
                /// and as each target returns it (<see cref="Renderings.AsServed"/>, <see cref="Renderings.PointedSide"/>); null
                /// as null; and a raise over anything else, which the reader refuses.
                /// </summary>
                static Truths Column(PathCell cell, string op, string literal) => cell switch
                {
                    PathCell.Absent or PathCell.Null => U,
                    PathCell.Point { Value: var v } when IsNumber(v) => TryNumberOf(literal, out var c)
                        ? Placed(op, [TryNumberOf(v, out var x) ? x.CompareTo(c) : DoubleBound.Compare(ApproximateOf(v)!.Value, c), Renderings.AsServed(v)?.CompareTo(c), .. Renderings.PointedSide(v, c)], Truths.None) : T | F,
                    PathCell.Numbers n => TryNumberOf(literal, out var c)
                        ? Placed(op, [n.Side(c), Renderings.IsShortestDouble(c) ? n.DoubleSide(DoubleBound.Of(c)) : null], Truths.None) : T | F,
                    _ => R,
                };

                /// <summary>
                /// A cell's answer where the conversion places it one way: a string or a boolean over text by the reading's own
                /// parse; a number by its double under <c>DOUBLE</c>, by its rendering under <c>DECIMAL</c>, and as written under
                /// an integer type's parse; elsewhere the state's.
                /// </summary>
                Truths Converted(PathCell cell, string op, string literal)
                {
                    // A double compares doubles, the literal's whether a decimal holds it or not.
                    if (_roundsFrom is not null && IsNumber(literal) && Doubled(cell, op, DoubleBound.Of(literal)) is Truths doubled)
                        return doubled;

                    if (TryNumberOf(literal, out var c) == false || _parsesDecimal && c != decimal.Truncate(c))
                        return Compare(cell.Coarse, op, literal);

                    Truths By(int? side, Truths beside) => Placed(op, [side], beside);
                    var (least, most) = _saturates ?? (0m, 0m);
                    switch (cell)
                    {
                        // A boolean renders as text that spells no number, and BIGINT reads text shaped as a timestamp as one.
                        case PathCell.Point { Value: var v } when IsString(v) || TypeOf(v) == BooleanType:
                        {
                            var text = IsString(v) ? StringOf(v) : Renderings.BooleanText(v);
                            if (_saturates?.Max == long.MaxValue && Renderings.ReadsAsTimestamp(text))
                                return T | F | R;

                            return _parsesDecimal ? (Renderings.DecimalSide(text, c) is int side ? By(side, 0) : R)
                                : Renderings.ParseInteger(text, least, most) is decimal w ? By(w.CompareTo(c), 0) : R;
                        }

                        // A number written as an integer renders as its digits, and any other as its double does
                        // (Calcite.JsonValueRendersScalarsAsText), which parses as the literal where both are a whole double.
                        case PathCell.Point { Value: var v } when _parsesDecimal && IsNumber(v):
                            return Rendered(op, c, Math.Sign(DoubleBound.Of(v).CompareTo(DoubleBound.Of(c))), TypeOf(v) == IntegerType && TryNumberOf(v, out var digits) ? [digits.CompareTo(c)] : []);
                        case PathCell.Numbers n when _parsesDecimal:
                            return Rendered(op, c, n.DoubleSide(DoubleBound.Of(c)), n.Integral ? [n.Side(c)] : []);

                        // Java's integer parse of a rendering reads digits, which both targets return an integer as however it
                        // was written, and raises on any other text; and a number whose double is whole as that double, which
                        // the emulator returns as its digits, and Azure was not measured returning, so it may raise.
                        case PathCell.Point { Value: var v } when _saturates is not null && IsNumber(v):
                        {
                            var r = DoubleBound.Of(v);
                            return Placed(op, [
                                .. TryNumberOf(v, out var x) && x == decimal.Truncate(x) && x >= least && x <= most ? [x.CompareTo(c)] : Array.Empty<int?>(),
                                .. Math.Floor(r) == r && r >= (double)least && r < (double)most + 1 ? [DoubleBound.Compare(r, c)] : Array.Empty<int?>()], ParsesWhole(cell) ? Truths.None : R);
                        }
                        // A fraction raises, though one whose double is whole within the type reads as that double, as the
                        // emulator returns it, and so does an integer past the type whose double rounds back onto its limit,
                        // -2^63 under BIGINT: beside the raise, the doubles placed where one may be whole within the type.
                        case PathCell.Numbers { Integral: false } n:
                            return Whole(n, op, c);
                        case PathCell.Numbers n:
                            return n.Side(most) == 1 || n.Side(least) == -1 ? Whole(n, op, c)
                                : n.Side(least - 1) == 1 && n.Side(most + 1) == -1 ? Placed(op, [n.Side(c), .. n.PointedSide(c)], ParsesWhole(cell) ? Truths.None : R)
                                : Compare(cell.Coarse, op, literal);

                        default:
                            return Compare(cell.Coarse, op, literal);
                    }
                }

                /// <summary>
                /// An integer type's parse over a cell whose numbers it reads only where the double is whole within the type:
                /// the raise, beside each side of the literal the whole doubles there may lie on, compared exactly, as a point's
                /// whole double is.
                /// </summary>
                Truths Whole(PathCell.Numbers n, string op, decimal c)
                {
                    if (_saturates is not var (least, most) || n.WholeDoubles((double)least, Math.Min((double)most, Math.BitDecrement(Renderings.LongCeiling))) is not { } whole)
                        return R;

                    var (low, high) = ((decimal)(long)whole.Low, (decimal)(long)whole.High);
                    return Placed(op, [.. low < c ? [-1] : Array.Empty<int?>(), .. low <= c && c <= high && c == decimal.Truncate(c) ? [0] : Array.Empty<int?>(), .. high > c ? [1] : Array.Empty<int?>()], R);
                }

                /// <summary>
                /// The cast to a double's answer on a cell, against the literal's double: a string or a boolean by its rendering's
                /// parse, a NaN comparing as nothing Java says alike, and a raise where it parses as no double; a number by its
                /// double. <c>null</c> for a cell it does not place.
                /// </summary>
                static Truths? Doubled(PathCell cell, string op, double literal)
                {
                    Truths By(int? side) => Placed(op, [side], Truths.None);
                    return cell switch
                    {
                        PathCell.Point { Value: var v } when IsString(v) || TypeOf(v) == BooleanType =>
                            Renderings.ParseDouble(IsString(v) ? StringOf(v) : Renderings.BooleanText(v)) is double d ? double.IsNaN(d) ? T | F : By(d.CompareTo(literal)) : R,
                        PathCell.Point { Value: var v } when IsNumber(v) => By(Math.Sign(DoubleBound.Of(v).CompareTo(literal))),
                        PathCell.Numbers n => By(n.DoubleSide(literal)),
                        _ => null,
                    };
                }

                /// <summary>
                /// What <c>RETURNING DECIMAL</c> answers of numbers read as their double's rendering on the double's side of
                /// the literal's, and as their digits on the sides given; where either is not one way, TRUE or FALSE.
                /// </summary>
                static Truths Rendered(string op, decimal c, int? doubled, int?[] digits) =>
                    Placed(op, [doubled == 0 ? (Math.Abs(c) < 1L << 53 ? 0 : null) : doubled, .. digits], Truths.None);

                public NumberCast(string target)
                {
                    _parsesDecimal = target == "DECIMAL";
                    _saturates = target is "INTEGER" or "BIGINT" ? IntegerLimits(target) : null;
                    _roundsFrom = target == "DOUBLE" ? Renderings.DoubleUnitFrom : null;
                    _parse = _saturates is not null ? R : Truths.None;
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
                        State.Null or State.Structure => U,
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
                    State.Null or State.Structure => T,
                    State.Scalar { Value: var v } when IsNumber(v) => F | _parse,
                    State.AnyNumber => F | _parse,
                    State.Scalar or State.AnyString => F | R,
                    _ => Truths.Any | R,
                };

                public override Truths Parameter(State state) => state switch
                {
                    State.Absent => U,
                    State.Null or State.Structure => U,
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
                /// where under <c>DOUBLE</c> the stored value and the literal each round by up to half of it (measured,
                /// <c>= 2^60</c> keeps a stored 2^60 + 1), and under every parse the emulator returns an integer written with a
                /// point as its double's digits (<see cref="Renderings.PointedSide"/>).
                /// </summary>
                (decimal? Above, decimal? Below) Window(string op, decimal bound)
                {
                    decimal? above = null;
                    decimal? below = null;

                    if (Math.Abs(bound) >= Renderings.DoubleUnitFrom)
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
                /// one double for two numbers. <c>RETURNING DECIMAL</c> and its column compare the number as written, and the
                /// clause a number not written as an int64 as its double's rendering, which the service returns too, so against
                /// a whole literal both are told only where they agree (<see cref="Rendered"/>); one past a decimal is not told.
                /// </remarks>
                bool? Holds(string op, string value, string literal)
                {
                    if (TryNumberOf(value, out var exact))
                    {
                        if (_saturates is { } s)
                        {
                            if (TryNumberOf(literal, out var bound) == false)
                                return null;

                            // As written, and as the service returns one not written as an int64: its double, whose digits
                            // the parse reads where that is whole within the type.
                            var converted = Satisfies(op, Math.Clamp(decimal.Truncate(exact), s.Min, s.Max), bound);
                            var r = DoubleBound.Of(exact);
                            return Math.Floor(r) == r && r >= (double)s.Min && r < (double)s.Max + 1 && Satisfies(op, (decimal)r, bound) != converted ? null : converted;
                        }

                        if (_parsesDecimal)
                        {
                            if (TryNumberOf(literal, out var parsed) == false)
                                return null;

                            // As written and as each target returns it: Azure an int64 as itself and any other number as its
                            // double, the emulator an integer written with a point as its double too.
                            var written = Satisfies(op, exact, parsed);
                            return Renderings.AsServed(value) is decimal served && Satisfies(op, served, parsed) == written
                                && Renderings.PointedSide(value, parsed).All(side => side is int sign && Satisfies(op, sign, 0m) == written) ? written : null;
                        }

                        return Satisfies(op, DoubleBound.Of(exact), DoubleBound.Of(literal));
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
                        return Doubles(op, numbers.Lower is Bound lo ? DoubleBound.Of(lo.Value) : null, numbers.Upper is Bound hi ? DoubleBound.Of(hi.Value) : null, DoubleBound.Of(c));

                    // The range as written, or as the service returns a number not written as an int64: its double's digits.
                    var lower = numbers.Lower is Bound l ? Renderings.ServedFloor(l.Value) : null;
                    var upper = numbers.Upper is Bound h ? Renderings.ServedCeiling(h.Value) : null;
                    decimal? low, high;
                    if (_saturates is { } s)
                    {
                        low = lower is decimal a ? Math.Clamp(decimal.Truncate(a), s.Min, s.Max) : s.Min;
                        high = upper is decimal b ? Math.Clamp(decimal.Truncate(b), s.Min, s.Max) : s.Max;
                    }
                    else
                    {
                        low = lower;
                        high = upper;
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
