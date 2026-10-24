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

        /// <summary>An operand's kinds, a shape's last part: a string, one Calcite renders a boolean as, a number, a boolean, an <c>IN</c>'s points, none.</summary>
        public const string StringKind = "string", TruthKind = "truth", NumberKind = "number", BooleanKind = "boolean", PointsKind = "points", NoOperand = "none";

        static readonly ServiceForm Unspelled = ServiceForm.OpaqueOf("unspelled", ValueSet<string>.Empty);

        /// <summary>An atom's shape, read off it: its reader, its test and its operand's kind; <see cref="ParameterShape"/> for a parameter.</summary>
        public static string ShapeOf(PredicateAtom atom) => atom.Parameter ? ParameterShape : atom.Reader + "/" + atom.Test + "/" + KindOf(atom);

        static string KindOf(PredicateAtom atom) => atom.Test == PredicateAtom.InTest ? PointsKind : atom.Operand is { } literal ? KindOf(literal) : NoOperand;

        /// <summary>A literal's kind: a string, one rendering a boolean, a number, or its own type.</summary>
        public static string KindOf(string literal) => JsonValues.IsString(literal) ? JsonValues.StringOf(literal) is "true" or "false" ? TruthKind : StringKind
            : JsonValues.IsNumber(literal) ? NumberKind : JsonValues.TypeOf(literal);

        /// <summary>The form a template proposes for an atom at a position, over its path's spelling; the sentinel where it proposes none.</summary>
        public static ServiceForm Spell(TemplateId id, RenderPosition pos, PredicateAtom atom, string path) =>
            (atom.Parameter ? null : Write(id, pos == RenderPosition.Pos, atom, path)) ?? Unspelled;

        static ServiceForm? Write(TemplateId id, bool pos, PredicateAtom atom, string path)
        {
            // The core is the comparison with its literal, an IN as its equalities, or the presence it tests; none for a null test.
            var (core, literal) = (Core(atom, path), atom.Operand);
            var (defined, isNull) = (ServiceForm.TestOf(IsDefined, path), ServiceForm.TestOf(IsNull, path));
            ServiceForm[] typed = RenderingRexGrammar.TypeTests.TryGetValue(atom.Reader, out var t) ? [ServiceForm.TestOf(t, path)] : [], untyped = [.. typed.Select(ServiceForm.NotOf)];
            ServiceForm Completed(IEnumerable<ServiceForm> cores) => Any([.. cores, isNull, ServiceForm.NotOf(defined), .. untyped])!;
            ServiceForm Restricted() => All([defined, ServiceForm.NotOf(isNull), .. typed])!;
            ServiceForm ByKind(CosmosServiceTest kind) => pos ? Any([core!, ServiceForm.TestOf(kind, path)])! : All([core!, ServiceForm.NotOf(ServiceForm.TestOf(kind, path))])!;

            return id switch
            {
                TemplateId.NullTest => pos ? Any([ServiceForm.NotOf(defined), isNull, .. untyped]) : ServiceForm.NotOf(Restricted()),
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
                _ => null,
            };
        }

        /// <summary>The comparison: itself with its literal, an <c>IN</c> as the equality of each point, or the presence it tests.</summary>
        static ServiceForm? Core(PredicateAtom atom, string path) => atom.Test switch
        {
            PredicateAtom.InTest => Any(Points(atom).Select(p => ServiceForm.CompareOf(Equal, path, p))),
            PredicateAtom.DefinedTest => ServiceForm.TestOf(IsDefined, path),
            _ => Op(atom.Test) is { } op && atom.Operand is { } literal ? ServiceForm.CompareOf(op, path, literal) : null,
        };

        static ValueSet<string> Points(PredicateAtom atom) => atom.Points ?? ValueSet<string>.Empty;

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
