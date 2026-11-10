using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Rendering.CosmosServiceTest;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Writes the form a template proposes for an atom at a position, from the atom's literal, points and reader and the
    /// spelling of its path: writing, which reads no fact and decides nothing. Which pairs are asked is the rows', and
    /// which forms hold the gate's, so it writes whatever it is asked that it can. Total over every pair, as a rule body's
    /// calls must be, Eggnog 1.1.1 running them in no promised order: a pair it cannot write — no comparison, no literal
    /// rendering a boolean beside A5 — is the sentinel, opaque and so answered widest, which no box allows.
    /// </summary>
    internal static class RenderingTemplates
    {

        /// <summary>A parameter atom's shape, which no <c>fits</c> row names: its value is unknown, so only strictness reads it.</summary>
        public const string ParameterShape = "parameter";

        /// <summary>
        /// A type test's test, a shape's middle part, whichever test it is: one the vocabulary read kinds of (<c>testkinds</c>),
        /// which the service writes by the test's name.
        /// </summary>
        public const string TypeTestShape = "type test";

        /// <summary>
        /// An operand's kinds, a shape's last part: a string, one Calcite renders a boolean as, one spelling a number, a
        /// number, a boolean, an <c>IN</c>'s points, none.
        /// </summary>
        public const string StringKind = "string", TruthKind = "truth", NumeralKind = "numeral", NumberKind = "number", BooleanKind = "boolean", PointsKind = "points", NoOperand = "none";

        /// <summary>A comparison of two readings' last part: the right reading, after this.</summary>
        public const string ReadingKind = "reading:";

        static readonly ServiceForm Unspelled = ServiceForm.OpaqueOf("unspelled", ValueSet<string>.Empty);

        /// <summary>
        /// An atom's shape, read off it: its reader, a raw column apart since a template only proposes, its test, any type
        /// test's <see cref="TypeTestShape"/>, and its operand's kind; <see cref="ParameterShape"/> for a parameter.
        /// </summary>
        public static string ShapeOf(PredicateAtom atom) => atom.Parameter ? ParameterShape
            : PredicateAtom.Columnless(atom.Reader) + "/" + (atom.Kinds is null ? atom.Test : TypeTestShape) + "/" + KindOf(atom);

        static string KindOf(PredicateAtom atom) => atom.Against is { } right ? ReadingKind + PredicateAtom.Columnless(right)
            : atom.Test == PredicateAtom.InTest ? PointsKind : atom.Operand is { } literal ? KindOf(literal) : NoOperand;

        /// <summary>A literal's kind: a string, one rendering a boolean, one spelling a number, a number, or its own type.</summary>
        public static string KindOf(string literal) => JsonValues.IsString(literal)
            ? JsonValues.StringOf(literal) is "true" or "false" ? TruthKind : NumeralOf(literal) is not null ? NumeralKind : StringKind
            : JsonValues.IsNumber(literal) ? NumberKind : JsonValues.TypeOf(literal);

        /// <summary>The number a string literal spells in digits, a sign and a point, as a number literal; <c>null</c> for any other.</summary>
        static string? NumeralOf(string literal) => JsonValues.StringOf(literal) is { Length: > 0 } s && s.All(c => c is (>= '0' and <= '9') or '-' or '.')
            && decimal.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? JsonValues.Of(n) : null;

        /// <summary>An atom beside Calcite's rows of its readings, which a <c>LIKE</c>'s pattern is read by (G7), as the template rule hands it.</summary>
        public static (PredicateAtom, ReadingTable) Read(PredicateAtom atom, ReadingTable readings) => (atom, readings);

        /// <summary>
        /// The form a template proposes for an atom at a position, over its path's spelling, each member carrying the atom's
        /// reader (<see cref="ServiceForm.Read"/>); the sentinel where it proposes none.
        /// </summary>
        public static ServiceForm Spell(TemplateId id, RenderPosition pos, (PredicateAtom Atom, ReadingTable Readings) read, string path) =>
            (read.Atom.Parameter ? null : Write(id, pos == RenderPosition.Pos, read.Atom, read.Readings.Units, path)) is { } form ? ServiceForm.Read(form, read.Atom.Reader) : Unspelled;

        static ServiceForm? Write(TemplateId id, bool pos, PredicateAtom atom, ValueSet<LikeUnit> units, string path)
        {
            // The core is the comparison with its literal, an IN as its equalities, the presence or type it tests; none for a null test.
            var (core, literal) = (Core(atom, path), atom.Operand);
            var (defined, isNull, number) = (ServiceForm.TestOf(IsDefined, path), ServiceForm.TestOf(IsNull, path), ServiceForm.TestOf(IsNumber, path));
            ServiceForm[] typed = RenderingRexGrammar.TypeTests.TryGetValue(atom.Reader, out var t) ? [ServiceForm.TestOf(t, path)] : [], untyped = [.. typed.Select(ServiceForm.NotOf)];
            ServiceForm Completed(IEnumerable<ServiceForm> cores) => Any([.. cores, isNull, ServiceForm.NotOf(defined), .. untyped])!;
            ServiceForm Restricted() => All([defined, ServiceForm.NotOf(isNull), .. typed])!;
            ServiceForm ByKind(CosmosServiceTest kind) => pos ? Any([core!, ServiceForm.TestOf(kind, path)])! : All([core!, ServiceForm.NotOf(ServiceForm.TestOf(kind, path))])!;

            return id switch
            {
                // Over a pair's spelling, its two paths' (PlaceSpellings).
                TemplateId.ComparePaths => atom.Against is not null && Op(atom.Test) is { } compared && PlaceSpellings.Sides(path) is (var left, var right)
                    ? ServiceForm.ComparePathsOf(compared, left, right) : null,
                _ when atom.Against is not null => null,
                _ when AffixOf(id) is var (test, rendered) => atom.Test == PredicateAtom.LikeTest && Affix(test, atom, units, path) is { } matched
                    ? rendered ? All([ServiceForm.NotOf(number), ServiceForm.NotOf(ServiceForm.TestOf(IsBool, path)), matched]) : matched : null,
                TemplateId.NullTest => pos ? Any([ServiceForm.NotOf(defined), isNull, .. untyped]) : ServiceForm.NotOf(Restricted()),
                TemplateId.StructureTest when atom.Test == PredicateAtom.NullTest => ServiceForm.NotOf(Any([ServiceForm.TestOf(IsArray, path), ServiceForm.TestOf(IsObject, path)])!),
                _ when core is null => null,
                TemplateId.Bare => core,
                TemplateId.Restricted => All([.. Restricted().Members, core]),
                TemplateId.Completed when atom.Test == PredicateAtom.InTest => Any(Points(atom).Select(p => Completed([ServiceForm.CompareOf(Equal, path, p)]))),
                TemplateId.Completed => Completed([core]),
                TemplateId.BooleanAlternatives when literal is not null && KindOf(literal) == TruthKind => pos ? Any(Alternatives(core, literal, path)) : Completed(Alternatives(core, literal, path)),
                TemplateId.ByKindBoolean => ByKind(IsBool),
                TemplateId.ByKindNumber => ByKind(IsNumber),
                TemplateId.StringBranch => All([defined, Any([ServiceForm.NotOf(ServiceForm.TestOf(IsString, path)), core])!]),
                TemplateId.KindRestricted => All([ServiceForm.TestOf(IsString, path), core]),
                TemplateId.KindRestrictedNumber => All([number, core]),
                TemplateId.Window => All([defined, Any([ServiceForm.NotOf(number), core])!]),
                TemplateId.TextAlternatives when atom.Test == PredicateAtom.Equal && literal is not null && NumeralOf(literal) is { } n =>
                    Any([core, All([number, ServiceForm.CompareOf(Equal, path, n)])!]),
                _ => null,
            };
        }

        /// <summary>The comparison: itself with its literal, an <c>IN</c> as the equality of each point, or the presence it tests.</summary>
        static ServiceForm? Core(PredicateAtom atom, string path) => atom.Test switch
        {
            PredicateAtom.InTest => Any(Points(atom).Select(p => ServiceForm.CompareOf(Equal, path, p))),
            PredicateAtom.DefinedTest => ServiceForm.TestOf(IsDefined, path),
            _ when atom.Kinds is not null && ServiceForm.TestNamed(atom.Test) is { } test => ServiceForm.TestOf(test, path),
            _ => Op(atom.Test) is { } op && atom.Operand is { } literal ? ServiceForm.CompareOf(op, path, literal) : null,
        };

        static ValueSet<string> Points(PredicateAtom atom) => atom.Points ?? ValueSet<string>.Empty;

        /// <summary>The test an affix template writes a <c>LIKE</c> as, and whether over a rendering; none for any other template (B1).</summary>
        static (CosmosServiceTest Test, bool Rendered)? AffixOf(TemplateId id) => id switch
        {
            TemplateId.AffixEquality => (Equal, false),
            TemplateId.AffixStartsWith => (StartsWith, false),
            TemplateId.AffixEndsWith => (EndsWith, false),
            TemplateId.AffixContains => (Contains, false),
            TemplateId.AffixLike => (Like, false),
            TemplateId.AffixRenderedEquality => (Equal, true),
            TemplateId.AffixRenderedStartsWith => (StartsWith, true),
            TemplateId.AffixRenderedEndsWith => (EndsWith, true),
            TemplateId.AffixRenderedContains => (Contains, true),
            TemplateId.AffixRenderedLike => (Like, true),
            _ => null,
        };

        /// <summary>
        /// A <c>LIKE</c>'s pattern, as Calcite's rows read it (G7), as a test where that test says it (<see cref="AffixPattern.Says"/>):
        /// the equality of its text, an affix of its run, or <c>LIKE</c> of it. None for one the rows leave unread, and none for
        /// one no statement can send, holding an unpaired surrogate: a writer's limit, not a reading.
        /// </summary>
        static ServiceForm? Affix(CosmosServiceTest test, PredicateAtom atom, ValueSet<LikeUnit> units, string path) =>
            atom.Operand is { } operand && JsonValues.StringOf(operand) is var text && JsonValues.IsEncodable(text) && LikePattern.Read(text, units) is { } pattern
                && AffixPattern.Says(test, pattern) is { } argument
                ? test == Equal ? ServiceForm.CompareOf(Equal, path, JsonValues.Of(argument)) : ServiceForm.AffixOf(test, path, argument)
                : null;

        /// <summary>A5's equalities: the string, and the boolean Calcite renders as it.</summary>
        static ServiceForm[] Alternatives(ServiceForm core, string literal, string path) =>
            [core, ServiceForm.CompareOf(Equal, path, JsonValues.Of(JsonValues.StringOf(literal) == "true"))];

        static CosmosServiceTest? Op(string test) => test switch
        {
            PredicateAtom.Equal => Equal,
            PredicateAtom.NotEqual => NotEqual,
            PredicateAtom.Less => Less,
            PredicateAtom.LessOrEqual => LessOrEqual,
            PredicateAtom.Greater => Greater,
            PredicateAtom.GreaterOrEqual => GreaterOrEqual,
            _ => null,
        };

        static ServiceForm? All(IEnumerable<ServiceForm> members) => Junction(And, members);

        static ServiceForm? Any(IEnumerable<ServiceForm> members) => Junction(Or, members);

        static ServiceForm? Junction(CosmosServiceTest connective, IEnumerable<ServiceForm> members) => members.ToList() switch
        {
            [] => null,
            [var one] => one,
            var many => ServiceForm.JunctionOf(connective, many),
        };

    }

}
