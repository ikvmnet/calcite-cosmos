using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Rendering.CosmosServiceTest;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// Writes the form a template proposes for an atom at a position, from the atom's literal, points and reader and the
    /// spelling of its path: writing, which reads no fact and decides nothing, the gate disposing of what it writes. Total
    /// over every pair, as a rule body's calls must be, Eggnog 1.1.1 running them in no promised order: a pair outside a
    /// template's own tests, or one it cannot write, is the sentinel, opaque and so answered widest, which no box allows.
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
            // A comparison with its literal, whose core is never null; or an IN, a null test or a presence test.
            var (core, literal, op) = (Core(atom, path), atom.Operand, atom.Operand is null ? null : Op(atom.Test));
            var (ordering, text) = (op is Less or LessOrEqual or Greater or GreaterOrEqual, literal is not null && JsonValues.IsString(literal));
            var (defined, isNull) = (ServiceForm.TestOf(IsDefined, path), ServiceForm.TestOf(IsNull, path));
            ServiceForm[] typed = ReadingTest(atom.Reader) is { } t ? [ServiceForm.TestOf(t, path)] : [], untyped = [.. typed.Select(ServiceForm.NotOf)];
            ServiceForm Completed(IEnumerable<ServiceForm> cores) => Any([.. cores, isNull, ServiceForm.NotOf(defined), .. untyped])!;
            ServiceForm Restricted() => All([defined, ServiceForm.NotOf(isNull), .. typed])!;

            return (id, pos) switch
            {
                (TemplateId.Bare, _) => core,
                (TemplateId.Restricted, true) when op == NotEqual => All([.. Restricted().Members, core!]),
                (TemplateId.Completed, false) when op is Equal || ordering => Completed([core!]),
                (TemplateId.Completed, false) when atom.Test == PredicateAtom.InTest => Any(Points(atom).Select(p => Completed([ServiceForm.CompareOf(Equal, path, p)]))),
                (TemplateId.NullTest, _) when atom.Test == PredicateAtom.NullTest => pos ? Any([ServiceForm.NotOf(defined), isNull, .. untyped]) : ServiceForm.NotOf(Restricted()),
                (TemplateId.BooleanAlternatives, _) when op == Equal && KindOf(literal!) == TruthKind && Alternatives(core!, literal!, path) is var alternatives =>
                    pos ? Any(alternatives) : Completed(alternatives),
                (TemplateId.ByKind, _) when ordering && text => pos
                    ? Any([core!, ServiceForm.TestOf(op is Greater or GreaterOrEqual ? IsBool : IsNumber, path)])
                    : All([core!, ServiceForm.NotOf(ServiceForm.TestOf(op is Greater or GreaterOrEqual ? IsNumber : IsBool, path))]),
                (TemplateId.StringBranch, true) when (op == NotEqual || ordering) && text => All([defined, Any([ServiceForm.NotOf(ServiceForm.TestOf(IsString, path)), core!])!]),
                (TemplateId.Window, true) when (op == Equal || ordering) && Window(op!.Value, literal!, path) is { } window =>
                    All([defined, Any([ServiceForm.NotOf(ServiceForm.TestOf(IsNumber, path)), window])!]),
                (TemplateId.KindRestricted, false) when op is not null && KindTest(atom.Reader) is { } kind => All([ServiceForm.TestOf(kind, path), core!]),
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

        /// <summary>
        /// C2's window on the stored number: each side the bound moved by one, which converting a number moves it by less
        /// than, left out where the service is not sent it as itself; none where no side is left.
        /// </summary>
        static ServiceForm? Window(CosmosServiceTest op, string literal, string path)
        {
            if (JsonValues.TryNumberOf(literal, out var bound) == false)
                return null;

            var above = op is Equal or Greater or GreaterOrEqual ? Moved(bound, -1) : null;
            var below = op is Equal or Less or LessOrEqual ? Moved(bound, 1) : null;
            return All(new[] { above is null ? null : ServiceForm.CompareOf(Greater, path, above), below is null ? null : ServiceForm.CompareOf(Less, path, below) }.OfType<ServiceForm>());
        }

        /// <summary>
        /// A bound moved by one, where the service compares what it is sent as that number, as <c>ServiceLeaf</c> places a
        /// literal: a whole number an int64 holds, sent as one, or a number whose double it is.
        /// </summary>
        static string? Moved(decimal bound, int by)
        {
            if (by < 0 ? bound < decimal.MinValue + 1 : bound > decimal.MaxValue - 1)
                return null;

            var moved = bound + by;
            var sent = moved == decimal.Truncate(moved) && moved >= long.MinValue && moved <= long.MaxValue || DoubleBound.Compare(DoubleBound.Of(moved), moved) == 0;
            return sent ? JsonValues.Of(moved) : null;
        }

        /// <summary><c>T(p)</c>, the reading's type test, as the translator writes it: text's, and a scalar <c>RETURNING</c>'s by its type.</summary>
        static CosmosServiceTest? ReadingTest(string reader) => reader == PredicateAtom.TextReader ? IsPrimitive : TypeOfReading(reader, "typed:") switch
        {
            "BOOLEAN" => IsBool,
            "TINYINT" or "SMALLINT" or "INTEGER" or "BIGINT" or "FLOAT" or "REAL" or "DOUBLE" => IsNumber,
            _ => null,
        };

        /// <summary>C4's kind: a string for text or a text cast, a number for a cast to one.</summary>
        static CosmosServiceTest? KindTest(string reader) => reader == PredicateAtom.TextReader ? IsString : TypeOfReading(reader, "cast:") switch
        {
            "VARCHAR" => IsString,
            "TINYINT" or "SMALLINT" or "INTEGER" or "BIGINT" or "FLOAT" or "REAL" or "DOUBLE" or "DECIMAL" => IsNumber,
            _ => null,
        };

        static string? TypeOfReading(string reader, string prefix) => reader.StartsWith(prefix, StringComparison.Ordinal) ? reader[prefix.Length..].Split(':')[0] : null;

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
