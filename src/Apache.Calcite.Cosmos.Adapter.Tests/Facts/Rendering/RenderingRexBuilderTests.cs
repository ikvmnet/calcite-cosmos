using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using org.apache.calcite.jdbc;
using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The Rex builder (spec g14; line 13). Every shape the grammar writes — each template's form, at each position it is
    /// asked, for every atom the loader reads over every reader, literal and position, and presence — and every weakening
    /// the check run's pass would push, is written as a Rex the filter beneath reads back as the form: since the switch (h15)
    /// that filter's statement admits it through S and prints S's form, so S holds the form <c>Equal</c> to the loader's
    /// reading of the Rex where it stands, and the printer's printing of it over that reading's places reads back as the
    /// form. A form outside the grammar builds nothing; one form builds one Rex; a weakening pushed is applied beneath; a
    /// test over a path read only through a field builds nothing, and is not pushed.
    /// </summary>
    public sealed class RenderingRexBuilderTests
    {

        /// <summary>A builder: a form's Rex, or none.</summary>
        internal delegate RexNode? Builder(ServiceForm form);

        /// <summary>Claims a form for an IR at a position, answering the verdict under the set and import a round trip is held under.</summary>
        internal delegate RenderingVerdict Claimer(ServiceForm form, SqlPredicate ir, RenderPosition pos, Verdict claim);

        /// <summary>
        /// A printer (line 13, h15): a form's text as the statement the Rex is pushed in prints it, over the places of the
        /// loader's reading of that Rex, or <c>null</c> where it prints none. <c>RenderingPrinter</c> is static, so the round
        /// trip takes this in its place.
        /// </summary>
        internal delegate string? Printer(ServiceForm form, SqlPredicate read);

        // ── The round trip ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The printer as the filter beneath prints a form: over each place the loader read of the Rex, computed ones among them.</summary>
        internal static Func<CosmosParameterList, Printer> Printed { get; } = parameters => (form, read) =>
            RenderingPrinter.Print(form, read.Places.Distinct().ToDictionary(SqlValue.Spell, StringComparer.Ordinal), CosmosImplementor.DefaultRootAlias, parameters,
                computed: RenderingPrinterTests.ComputedOf([read]));

        /// <summary>
        /// What one round trip found wrong, each a line, the form the filter beneath sends, read back from its printed text,
        /// and the comparisons of the conjunct's own the Rex reads, for a form standing at a position. The filter the Rex is
        /// pushed in reads it through the loader and admits it through S (h15), so what is sent is what Calcite reads of it:
        /// the form itself where S holds it <c>Equal</c> to that reading where it stands. Where S does not — Calcite's
        /// <c>&lt;&gt;</c> keeps no null the service's <c>!=</c> keeps, so a bare <c>!=</c> at <c>Pos</c> is read tighter — the
        /// filter sends S's best exact form of the reading, which the atom must imply, or the weakening would cost a row it
        /// keeps: the form itself up to absorption, which generate mode proved the reading of and S gave as implied by the
        /// atom, where the check's claim mode proves no further over a wide junction; else one the check proves implied; else,
        /// where the check leaves it unproven, as it leaves a junction of two atoms of one path (<c>TODO.md</c>) or one over
        /// an opaque term's strict paths, one no document <paramref name="witness"/> draws keeps the atom's row and drops
        /// it there, as source A holds a form.
        /// Either is printed over the reading's places and must read back as itself. Unproven with no reason, over a
        /// <c>CASE</c>, is S declining the filter beneath, which keeps it in process and costs RU alone; a <c>CASE</c> S proves
        /// beneath its arm's licence is sent as its best, as any other (h15).
        /// </summary>
        internal static (List<string> Problems, ServiceForm? Written, int Own) RoundTrip(ServiceForm form, RenderPosition at, Builder build,
            Func<CosmosParameterList, Printer> printer, CosmosPredicateLoader.Loading loading, Claimer claim, Func<SqlPredicate, bool> own,
            SqlPredicate atom, Func<SqlPredicate, ServiceForm?> sent, Func<ServiceForm, string?>? witness = null)
        {
            if (build(form) is not { } rex)
                return ([$"not built: {form}"], null, 0);

            var read = loading.Read(rex);
            var mine = read.Subterms.Count(t => t is SqlPredicate.Compare or SqlPredicate.In && own(t));
            if (read.Subterms.Any(t => t is SqlPredicate.Opaque))
                return ([$"{form} read as {read}"], null, mine);

            var sending = form;
            switch (claim(form, read, at, Verdict.Equal))
            {
                case { Kind: RenderingVerdictKind.Proven }:
                    break;
                case { Kind: RenderingVerdictKind.Unproven, Reason: null } when IsCased(form):
                    return ([], null, mine);
                case var answer when at == RenderPosition.Pos && sent(read) is { } best:
                    if (Absorbed(best).Key != Absorbed(form).Key && claim(best, atom, at, Verdict.Implies) is { Kind: not RenderingVerdictKind.Proven } implied
                        && (implied.Kind == RenderingVerdictKind.Unproven && witness is not null ? witness(best) : "") is { } kept)
                        return ([$"altered: {form} at {at} built as {rex}, read {read}, not held Equal ({answer.Kind}), and sent as {best}, which its atom does not imply: {implied.Kind} {implied.Reason} {kept}"], null, mine);
                    sending = best;
                    break;
                // S proves no exact form of a reading that is the atom itself, or it beside tests of a path, each read exactly
                // (testreads): the filter beneath keeps it in process, which costs RU alone (a number past a double's integers).
                case { Kind: RenderingVerdictKind.Unproven or RenderingVerdictKind.Refuted } when HoldsTheAtom(read, atom) && sent(read) is null:
                    return ([], null, mine);
                case var answer:
                    return ([$"altered: {form} at {at} built as {rex}, read {read}, not held Equal: {answer.Kind} {answer.Reason}"], null, mine);
            }

            var parameters = new CosmosParameterList();
            string? text;
            try
            {
                text = printer(parameters)(sending, read);
            }
            catch (Exception e) when (e is ArgumentException or CosmosTranslationException)
            {
                return ([$"not printed: {sending} over {read}: {e.Message}"], null, mine);
            }

            if (text is null)
                return ([$"not printed: {sending} over {read}"], null, mine);

            var computed = RenderingPrinterTests.ComputedOf([read]);
            var back = CosmosSqlFormReader.Read(text, parameters.Parameters, places: computed.Count == 0 ? null : CosmosSqlFormReader.PlacesOf(computed.Values));
            return back.Form.Text == sending.Text && back.Written == text ? ([], back.Form, mine) : ([$"altered: {sending} printed {text}, read back as {back.Form}"], back.Form, mine);
        }

        /// <summary>Whether a reading is the atom, or the atom beside tests of a path, each of which the testreads row reads exactly.</summary>
        static bool HoldsTheAtom(SqlPredicate read, SqlPredicate atom) => read.Digest == atom.Digest
            || read is SqlPredicate.And and && and.Arms.Any(a => a.Digest == atom.Digest)
                && and.Arms.All(a => a.Digest == atom.Digest || a is SqlPredicate.IsDefined or SqlPredicate.TypeTest or SqlPredicate.Not { Operand: SqlPredicate.IsDefined or SqlPredicate.TypeTest });

        /// <summary>
        /// S's best exact form of an IR at <c>Pos</c> under a set, each path imported as <see cref="Claim"/> imports it and an
        /// arm's atom stated as <see cref="Claim"/> states it, as the arm's licence states it in production (h15): what the
        /// filter beneath sends of a Rex the loader reads as the IR, or <c>null</c> where S proves none, which keeps it in
        /// process.
        /// </summary>
        internal static ServiceForm? Sent(SqlPredicate ir, RenderingSet set, IReadOnlyDictionary<string, RenderingMemo.Imported>? imports = null, ValueSet<string>? chars = null)
        {
            using var bundle = ProbeRows.ProbeTheory.Open(set);
            var term = bundle.Write(ir);
            var imported = new Dictionary<JsonDocumentPath, PathValues>();
            foreach (var path in ir.Paths.Distinct())
                if (imports?.GetValueOrDefault(SqlValue.Spell(path)) is { } import)
                    bundle.Import(ProbeRows.Source, path, imported[path] = import.Values, chars is null ? import.Chars : ValueSet.From(import.Chars.Where(chars.Contains)));
                else
                    bundle.Import(ProbeRows.Source, path, imported[path] = PathValues.Anything, chars ?? JsonValues.AllBands);

            StateArms(bundle, imported);
            bundle.Generate(term);
            bundle.RunGenerate(RenderingBundle.WidthOf([ir]));
            return bundle.ReadBest(term) is { Kind: RenderingVerdictKind.Proven, Best: { } best } ? best : null;
        }

        /// <summary>
        /// Whether a form holds a test, or a guarded pair, over a path with no accessor the body takes apart: one the conjunct
        /// reads only through a field, an item, a cast or a call, where the input holds no document column to re-type the
        /// path's accessor over (<see cref="RenderingRexBuilder.Accessor.Tested"/>, h15b). The builder writes the test over the
        /// node the path has, which the filter beneath reads back as that node's test, its body's raise a row S declines.
        /// </summary>
        internal static bool TestsAField(ServiceForm form, IReadOnlyDictionary<string, RenderingRexBuilder.Accessor> accessors) =>
            form.Subforms.Any(f => f.Kind == ServiceFormKind.Test && accessors.GetValueOrDefault(f.Path!) is { } a && OverARaisingNode(a))
            || IsCased(form) && form.Subforms.Any(f => f.Kind == ServiceFormKind.Compare && accessors.GetValueOrDefault(f.Path!) is { } a && OverARaisingNode(a));

        /// <summary>Whether a path's test is written over a node no accessor the in-process body takes apart: a field, an item, a cast or a call.</summary>
        static bool OverARaisingNode(RenderingRexBuilder.Accessor accessor) =>
            accessor.Tested is not RexCall call || call.getOperator().getName() is not ("JSON_VALUE" or "JSON_QUERY");

        /// <summary>
        /// Whether the builder writes a form with a <c>CASE</c>: a ternary, or a comparison a guarded pair holds over a reader
        /// that parses a string (<see cref="RenderingRexGrammar.Guarded"/>), which the translator writes no text of.
        /// </summary>
        internal static bool IsCased(ServiceForm form) => form.Subforms.Any(f => f.Kind == ServiceFormKind.Choose
            || f.Kind is ServiceFormKind.And or ServiceFormKind.Or && RenderingRexGrammar.GuardsOf(f) is var guards && f.Members.Any(m => m.Kind == ServiceFormKind.Compare
                && guards.Contains(m.Path!) && m.Reader is { } r && RenderingRexGrammar.ClassOf(r) == r && RenderingRexGrammar.Written.IsGuarded(r, m.Literal!)));

        /// <summary>A form up to absorption: its text as <see cref="Absorbed(ServiceForm)"/> keys it, and a junction's members.</summary>
        internal sealed record Absorption(string Key, ServiceFormKind Kind, IReadOnlyList<Absorption> Members);

        /// <summary>
        /// A form's text up to absorption, each junction's members in order of their own: a junction flattened into its own
        /// connective, a member it repeats dropped, and a member taken out where one of its arms is a sibling or a junction of
        /// siblings alone (<c>x AND (x OR y)</c> is <c>x</c>, and dually). Each step is an identity of a distributive lattice,
        /// which the service's three-valued connectives are, so equal keys are equal forms; a member any other step alters
        /// keeps its own key and parts them (line 13). Anything else is its text.
        /// </summary>
        internal static Absorption Absorbed(ServiceForm form)
        {
            switch (form.Kind)
            {
                case ServiceFormKind.Not:
                    var operand = Absorbed(form.Members[0]);
                    return new("NOT " + operand.Key, form.Kind, [operand]);

                case ServiceFormKind.And or ServiceFormKind.Or:
                    var members = form.Members.Select(Absorbed).SelectMany(m => m.Kind == form.Kind ? m.Members : [m]).DistinctBy(m => m.Key).ToList();
                    while (members.FirstOrDefault(m => members.Where(o => ReferenceEquals(o, m) == false).Select(o => o.Key).ToHashSet() is var others
                        && m.Kind is ServiceFormKind.And or ServiceFormKind.Or && m.Kind != form.Kind
                        && m.Members.Any(a => others.Contains(a.Key) || a.Kind == form.Kind && a.Members.All(b => others.Contains(b.Key)))) is { } absorbed)
                        members.Remove(absorbed);

                    return members is [var one] ? one : new(form.Test + "(" + string.Join(", ", members.Select(m => m.Key).Order(StringComparer.Ordinal)) + ")", form.Kind, members);

                default:
                    return new(form.Text, form.Kind, []);
            }
        }

        /// <summary>
        /// Whether S may decline a guarded pair at the filter beneath, where it holds the pair implied: past 2^53, where the
        /// cut the service's doubles give leaves its arm's cells apart, and over <c>RETURNING DECIMAL</c>, which parses the
        /// rendering. h15's site asks S of the filter beneath before it pushes, and pushes no such pair.
        /// </summary>
        internal static bool DeclinedByTheCut(ServiceForm form) => form.Subforms.Any(m => m.Reader == PredicateAtom.TypedReader("DECIMAL")
            || m.Kind == ServiceFormKind.Compare && JsonValues.IsNumber(m.Literal!) && Math.Abs(JsonValues.NumberOf(m.Literal!)) >= 9007199254740992m);

        static readonly PathValues Numbers = PathValues.OfKinds(ValueSet.Of(JsonValues.IntegerType, JsonValues.FractionType));

        /// <summary>
        /// A form claimed for an IR at a position under a set, each path either side reads imported as given, or as
        /// anything, its characters kept to those given; at <c>Neg</c>, the negation of each at <c>Pos</c>. An atom read
        /// beneath an arm whose conditions hold <c>IS_NUMBER</c> of its path is stated there as a number, which is what the
        /// facts graph reads of that condition exactly.
        /// </summary>
        internal static RenderingVerdict Claim(ServiceForm form, SqlPredicate ir, RenderPosition pos, Verdict verdict, RenderingSet set,
            IReadOnlyDictionary<string, RenderingMemo.Imported>? imports = null, ValueSet<string>? chars = null)
        {
            var (claimed, root) = pos == RenderPosition.Pos ? (form, ir) : (ServiceForm.Negated(form), (SqlPredicate)new SqlPredicate.Not(ir));
            using var bundle = ProbeRows.ProbeTheory.Open(set);
            var term = bundle.Write(root);
            var imported = new Dictionary<JsonDocumentPath, PathValues>();
            foreach (var path in root.Paths.Concat(form.Paths.Select(CosmosEval.PathOf)).Distinct())
                if (imports?.GetValueOrDefault(SqlValue.Spell(path)) is { } import)
                    bundle.Import(ProbeRows.Source, path, imported[path] = import.Values, chars is null ? import.Chars : ValueSet.From(import.Chars.Where(chars.Contains)));
                else
                    bundle.Import(ProbeRows.Source, path, imported[path] = PathValues.Anything, chars ?? JsonValues.AllBands);

            StateArms(bundle, imported);

            // A CASE is read by generate mode alone (spec h08): Equal where S's best exact form is the one claimed up to
            // absorption, a junction's members in any order. A weakening may carry a member its siblings absorb, and S's
            // best one nested otherwise (partitions 1967); neither is a minimal form, and both are the same form.
            if (verdict == Verdict.Equal && root.Subterms.Any(t => t is SqlPredicate.Case))
            {
                bundle.Generate(term);
                bundle.RunGenerate(RenderingBundle.WidthOf([root]));
                var answer = bundle.ReadBest(term);
                return answer is { Kind: RenderingVerdictKind.Proven, Best: { } best } && Absorbed(best).Key != Absorbed(claimed).Key
                    ? new(RenderingVerdictKind.Unproven, null, null, $"S's best exact form is {best}") : new(answer.Kind, answer.Proof, null, answer.Reason);
            }

            bundle.Claim(term, claimed);
            bundle.Run(RenderingCheckRun.CheckLimit([claimed]));
            return bundle.Read(term, claimed, verdict);
        }

        /// <summary>
        /// States each atom a bundle reads beneath an arm whose conditions hold <c>IS_NUMBER</c> of its path as a number, which
        /// is what the facts graph reads of that condition exactly.
        /// </summary>
        static void StateArms(RenderingBundle bundle, IReadOnlyDictionary<JsonDocumentPath, PathValues> imported)
        {
            foreach (var beneath in bundle.Atoms.OfType<SqlPredicate.Beneath>().ToList())
                if (beneath.Leaf.Paths.ToList() is [var leaf] && bundle.Strata[beneath.Stratum].Any(c => c is SqlPredicate.TypeTest { Test: "IS_NUMBER" } t && t.Paths.SequenceEqual([leaf])))
                    bundle.Assume(ProbeRows.Source with { Stratum = beneath.Stratum }, beneath, PathValues.Meet(Numbers, imported[leaf]));
        }

        /// <summary>
        /// The characters a string is claimed over under a set: under <c>Emulator</c> any but U+0000, where its order parts
        /// from Calcite's for every Rex, the residue S's own admission declines (<c>TODO.md</c>).
        /// </summary>
        static ValueSet<string> Ordered(RenderingSet set) =>
            set == RenderingSet.Emulator ? JsonValues.ClassSet(JsonCharacterBand.Any & ~JsonCharacterBand.Nul) : JsonValues.AllBands;

        // ── Every shape the grammar writes ─────────────────────────────────────────────────────────────────────────────

        internal static readonly JavaTypeFactoryImpl Types = new();

        internal static readonly RexBuilder Rex = new(Types);

        static readonly CosmosPath Document = CosmosPath.Root(CosmosImplementor.DefaultRootAlias);

        /// <summary>The document, and a column of each kind a view binds: <c>VARIANT</c>, text, an <c>INTEGER</c> and a raw string.</summary>
        internal static readonly IReadOnlyList<CosmosPath?> Fields = [Document, Document.Property("v"), Document.Property("t"), Document.Property("i"), Document.Property("r")];

        internal static readonly IReadOnlyList<CosmosReading> Readings = [CosmosReading.Typed, CosmosReading.Typed, CosmosReading.Text, CosmosReading.Typed, CosmosReading.Typed];

        internal static org.apache.calcite.rel.type.RelDataType Nullable(SqlTypeName type) => Types.createTypeWithNullability(Types.createSqlType(type), true);

        internal static RexNode JsonValue(string path, SqlTypeName type)
        {
            var operands = new java.util.ArrayList();
            operands.add(Rex.makeInputRef(Types.createSqlType(SqlTypeName.VARCHAR), 0));
            operands.add(Rex.makeLiteral("$." + path));
            return Rex.makeCall(Nullable(type), SqlStdOperatorTable.JSON_VALUE, operands);
        }

        /// <summary>
        /// Each accessor the loader reads a path through, one path each: text, raw, a <c>RETURNING</c> of each kind of number
        /// (a 32- and a 64-bit integer, a float, a double), a boolean and a decimal, a cast of text to a number and of a raw
        /// value to text, and the view's columns. The integer types and <c>FLOAT</c> beside these are of their class.
        /// </summary>
        static IEnumerable<RexNode> Accessors() =>
        [
            JsonValue("text", SqlTypeName.VARCHAR), JsonValue("raw", SqlTypeName.ANY),
            .. new[] { SqlTypeName.INTEGER, SqlTypeName.BIGINT, SqlTypeName.REAL, SqlTypeName.DOUBLE, SqlTypeName.BOOLEAN, SqlTypeName.DECIMAL }
                .Select(t => JsonValue("typed" + t.getName(), t)),
            .. new[] { SqlTypeName.INTEGER, SqlTypeName.BIGINT, SqlTypeName.DOUBLE }.Select(t => Rex.makeCast(Nullable(t), JsonValue("cast" + t.getName(), SqlTypeName.VARCHAR))),
            Rex.makeCast(Nullable(SqlTypeName.VARCHAR), JsonValue("castRaw", SqlTypeName.ANY)),
            Rex.makeInputRef(Nullable(SqlTypeName.VARIANT), 1), Rex.makeInputRef(Nullable(SqlTypeName.VARCHAR), 2), Rex.makeInputRef(Nullable(SqlTypeName.INTEGER), 3),
            Rex.makeInputRef(Nullable(SqlTypeName.VARCHAR), 4),
        ];

        /// <summary>
        /// A comparison with a literal of each class the inputs hold, over an accessor: text with every operator, and text
        /// Calcite renders a boolean as with <c>=</c>, which alone a template reads apart (A5), and over text a numeral with
        /// <c>=</c>, which a template reads as the string or the number (h09); a whole number with every
        /// operator, and over an accessor typed as a number, which alone the grammar compares with one, a fraction and one
        /// past 2^53 too; a boolean with <c>=</c> and <c>&lt;&gt;</c>, and over <c>RETURNING BOOLEAN</c> every operator.
        /// </summary>
        static IEnumerable<RexNode> Compared(RexNode accessor)
        {
            var reader = RenderingRexGrammar.ClassOf(PredicateAtom.TypedReader(accessor.getType().getSqlTypeName().getName()));
            var number = reader == RenderingRexGrammar.NumberReader && accessor is RexCall;
            foreach (var op in Comparisons)
            {
                yield return Rex.makeCall(op, accessor, Rex.makeLiteral("bikes"));
                yield return Rex.makeCall(op, accessor, Rex.makeExactLiteral(new java.math.BigDecimal("30")));
                if (op == SqlStdOperatorTable.EQUALS)
                    yield return Rex.makeCall(op, accessor, Rex.makeLiteral("true"));
                if (op == SqlStdOperatorTable.EQUALS && accessor.getType().getSqlTypeName() == SqlTypeName.VARCHAR)
                    yield return Rex.makeCall(op, accessor, Rex.makeLiteral("30"));
                if (op == SqlStdOperatorTable.EQUALS || op == SqlStdOperatorTable.NOT_EQUALS || reader == RenderingRexGrammar.BooleanReader)
                    yield return Rex.makeCall(op, accessor, Rex.makeLiteral(true));
                if (number)
                {
                    yield return Rex.makeCall(op, accessor, Rex.makeExactLiteral(new java.math.BigDecimal("2.5")));
                    yield return Rex.makeCall(op, accessor, Rex.makeExactLiteral(new java.math.BigDecimal("9007199254740993")));
                }
            }
        }

        static readonly SqlOperator[] Comparisons = [SqlStdOperatorTable.EQUALS, SqlStdOperatorTable.NOT_EQUALS, SqlStdOperatorTable.LESS_THAN,
            SqlStdOperatorTable.LESS_THAN_OR_EQUAL, SqlStdOperatorTable.GREATER_THAN, SqlStdOperatorTable.GREATER_THAN_OR_EQUAL];

        /// <summary>
        /// Every conjunct over each accessor the loader reads as an atom, an accessor's together: each comparison with each
        /// literal, an <c>IN</c>, a null test and presence.
        /// </summary>
        static IEnumerable<IReadOnlyList<(RexNode Conjunct, SqlPredicate Atom)>> Atoms(CosmosPredicateLoader.Loading loading) => Accessors().Select(accessor =>
            (IReadOnlyList<(RexNode Conjunct, SqlPredicate Atom)>)[.. Compared(accessor).Concat(
            [
                Rex.makeIn(accessor, java.util.Arrays.asList(Rex.makeLiteral("bikes"), Rex.makeLiteral("tools"))),
                Rex.makeIn(accessor, java.util.Arrays.asList(Rex.makeExactLiteral(java.math.BigDecimal.valueOf(30)), Rex.makeExactLiteral(java.math.BigDecimal.valueOf(31)))),
                Rex.makeCall(SqlStdOperatorTable.IS_NULL, accessor), Rex.makeCall(CosmosOperators.IsDefined, accessor),
            ])
                .Select(conjunct => (Conjunct: conjunct, Atom: loading.Read(conjunct)))
                .Where(c => c.Atom.Subterms.Any(t => t is SqlPredicate.Opaque or SqlPredicate.And or SqlPredicate.Or or SqlPredicate.Not) == false)
                .DistinctBy(c => c.Atom.Digest)]);

        /// <summary>An atom's reader, by the grammar's rows.</summary>
        static string ReaderOf(SqlPredicate atom) => RenderingFamily.ReaderOf(atom switch
        {
            SqlPredicate.Compare c => c.Left,
            SqlPredicate.In i => i.Value,
            SqlPredicate.IsNull n => n.Value,
            SqlPredicate.IsDefined d => d.Value,
            _ => throw new ArgumentException(atom.ToString()),
        })!;

        /// <summary>
        /// Every shape over every atom under each asserted set, in production's S, which asks every template since the switch
        /// (h15), unless given: each template's form at each position S proposes it, and each S holds implied, presence among
        /// them, over a path that may hold anything, its characters any but U+0000 under <c>Emulator</c>, whose order parts
        /// from Calcite's there (<c>TODO.md</c>); an accessor's atoms in one bundle. A form outside the grammar builds nothing;
        /// every other builds, one testing a path with no accessor re-typed <c>ANY</c> (<see cref="TestsAField"/>) counted
        /// apart, the filter beneath's to decline; one S holds implied round trips.
        /// </summary>
        internal static (int Shapes, int Exact, int Guarded, int Refused, int Implied, int Own, int Cased, List<string> Declined, List<string> Fields, List<string> Problems) Shapes(Func<Builder, Builder>? plant = null,
            bool first = false, RenderingTheory? theory = null)
        {
            theory ??= ProbeRows.ProbeTheory;
            var loading = new CosmosPredicateLoader.Loading(Fields, Readings, CosmosImplementor.DefaultRootAlias);
            var (shapes, exact, guarded, refused, implied, own, cased, declined, fields, problems) = (0, 0, 0, 0, 0, 0, 0, new List<string>(), new List<string>(), new List<string>());

            // A part many shapes of one atom share, their core or a test, is claimed once a set.
            var claims = new Dictionary<(string, string, RenderPosition, Verdict, RenderingSet), RenderingVerdict>();
            RenderingVerdict Claimed(ServiceForm f, SqlPredicate ir, RenderPosition at, Verdict verdict, RenderingSet set, ValueSet<string> chars) =>
                claims.TryGetValue((f.Text, ir.Digest, at, verdict, set), out var known) ? known : claims[(f.Text, ir.Digest, at, verdict, set)] = Claim(f, ir, at, verdict, set, chars: chars);

            foreach (var atoms in Atoms(loading).TakeWhile(_ => first == false || problems.Count == 0))
                foreach (var set in RenderingGenerateRun.AssertedSets)
                {
                    var chars = Ordered(set);
                    using var bundle = theory.Open(set);
                    var terms = atoms.Select(a => bundle.Write(a.Atom)).ToList();
                    foreach (var path in atoms.SelectMany(a => a.Atom.Paths).Distinct())
                        bundle.Import(ProbeRows.Source, path, PathValues.Anything, chars);
                    terms.ForEach(bundle.Generate);
                    bundle.RunGenerate(RenderingBundle.WidthOf(atoms.Select(a => a.Atom))).Should().Be(new RenderingRun(RunStopReason.Completed, true, false), atoms[0].Atom.ToString());

                    for (var i = 0; i < atoms.Count; i++)
                    {
                        var (conjunct, atom, term) = (atoms[i].Conjunct, atoms[i].Atom, terms[i]);
                        var accessors = loading.AccessorsOf(conjunct, Rex);
                        Builder built = form => RenderingRexBuilder.Build(form, Rex, accessors);
                        var build = plant?.Invoke(built) ?? built;
                        var proposed = bundle.Scope.Rows(bundle.Theory.Candidate).Where(r => term.Id.Equals(r.Keys[0])).Select(r => ((ServiceForm)r.Keys[2], (RenderPosition)r.Keys[1]));
                        var held = Enum.GetValues<RenderPosition>().SelectMany(pos => (bundle.Scope.TryGet(bundle.Theory.Implied.Of(term, pos), out var forms) ? forms : ValueSet<ServiceForm>.Empty)
                            .Select(f => (f, pos))).ToHashSet();
                        foreach (var (form, pos) in proposed.Concat(held).Where(s => s.Item1.Kind != ServiceFormKind.Opaque).Distinct())
                        {
                            shapes++;
                            var shown = $"{set} {atom} {pos} {form}";
                            if (RenderingRexGrammar.Written.Writes(form, ReaderOf(atom)) == false)
                            {
                                refused++;
                                if (built(form) is { } outside)
                                    problems.Add($"{shown}: built outside the grammar as {outside}");
                                continue;
                            }

                            // A shape S cannot hold is written all the same, the builder being total over the grammar; one testing
                            // a path over a node the body raises over is the filter beneath's to decline; one it holds, as it may
                            // push it, round trips.
                            if (TestsAField(form, accessors))
                            {
                                (build(form) is null ? problems : fields).Add(build(form) is null ? $"{shown}: not built" : shown);
                                continue;
                            }

                            if (held.Contains((form, pos)) == false)
                            {
                                if (build(form) is null)
                                    problems.Add($"{shown}: not built");
                                continue;
                            }

                            implied++;
                            var (found, written, mine) = RoundTrip(form, pos, build, Printed, loading, (f, ir, at, verdict) => Claimed(f, ir, at, verdict, set, chars), ir => ir.Digest == atom.Digest,
                                atom, ir => Sent(ir, set, chars: chars));
                            (own, exact, guarded, cased) = (own + mine, exact + (form.Text == written?.Text ? 1 : 0), guarded + (written is not null && form.Text != written.Text ? 1 : 0), cased + (IsCased(form) ? 1 : 0));
                            if (IsCased(form) && written is null && found.Count == 0)
                                (DeclinedByTheCut(form) ? declined : found).Add(DeclinedByTheCut(form) ? shown : "declined beneath, short of the cut");
                            else if (written is null && found.Count == 0)
                                declined.Add(shown + ": no exact form of its reading, not pushed");

                            problems.AddRange(found.Select(p => $"{shown}: {p}"));
                        }
                    }
                }

            return (shapes, exact, guarded, refused, implied, own, cased, declined, fields, problems);
        }

        /// <summary>
        /// Exhaustive over the grammar, not the corpus: every shape a template proposes, or S holds implied, for every atom
        /// the loader reads over every reader, literal class and position, and presence, under each asserted set; the
        /// guarded comparison's among them (spec h09), each <c>CASE</c> read back by the loader and proven by S. A test over
        /// a field, a raw column's among them, is written over the path's accessor re-typed <c>ANY</c> (h15b), and one of a
        /// path with none over the field or the call itself, reported, the filter beneath's to decline.
        /// </summary>
        [Fact]
        public void EveryWritableShapeRoundTrips()
        {
            GC.KeepAlive(ProbeRows.ProbeTheory);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var (shapes, exact, guarded, refused, implied, own, cased, declined, fields, problems) = Shapes();
            TestContext.Current.TestOutputHelper?.WriteLine($"{shapes} shapes: {refused} outside the grammar, built as nothing; {fields.Count} holding a test of a path over a field or "
                + $"a call, the filter beneath's to decline; the rest written, {implied} of them S holds implied and round tripped: {exact} read back as themselves, {guarded} sent as S's form of what Calcite "
                + $"reads of them, {cased} with a CASE ({declined.Count} of them declined beneath); {own} comparisons the atom's own; "
                + $"{System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s with S defined");
            foreach (var shape in declined)
                TestContext.Current.TestOutputHelper?.WriteLine($"declined beneath: {shape}");
            foreach (var shape in fields.Take(20))
                TestContext.Current.TestOutputHelper?.WriteLine($"a test of a path over a field or a call: {shape}");
            foreach (var problem in problems)
                TestContext.Current.TestOutputHelper?.WriteLine(problem);

            (shapes - refused).Should().BeGreaterThan(500);
            refused.Should().BeGreaterThan(0, "a number over a cast, RETURNING DECIMAL or a raw value is outside the grammar bare, and a test over a raw column");
            cased.Should().BeGreaterThan(declined.Count, "a number over a cast, RETURNING DECIMAL or the text is written as its guarded pair");
            (exact + guarded).Should().Be(implied - declined.Count, "every shape S holds and the filter beneath admits is printed as itself, or as S's form of what Calcite reads of it");
            problems.Should().BeEmpty();
        }

        // ── Every weakening the check run's pass would push ────────────────────────────────────────────────────────────

        /// <summary>S's weakenings of the pull request's run under the asserted sets, once a statement, conjunct, form and set.</summary>
        internal static IReadOnlyList<RenderingGenerateRun.Weakened> Weakenings() =>
            [.. RenderingCheckRun.PerPullRequest.Value.Generated!.Weakenings.Where(w => w.Mine is not null && w.Source is not null && RenderingGenerateRun.AssertedSets.Contains(w.Set))
                .DistinctBy(w => (w.Source, w.Conjunct, w.Mine!.Text, w.Set))];

        /// <summary>
        /// Every weakening S gives a residual over the pull request's inputs, built over its conjunct's own accessors and
        /// held to the round trip under its set, at the import of the bundle it was read from, a string's characters as
        /// <see cref="Ordered"/> keeps them, and printed as the filter beneath prints it (line 13, h15). One holding a test
        /// of a path written over a field or a call, read back as that node's test, which S declines beneath, is counted apart:
        /// production does not push it; and one read back over an element's raw value, which S declines beneath (h15b).
        /// </summary>
        internal static (int Weakenings, int Exact, int Guarded, int Own, int Cased, List<string> Declined, List<string> Fields, List<string> Elements, List<string> Problems) Pushed(Func<Builder, Builder>? plant = null, bool first = false)
        {
            var (weakenings, exact, guarded, own, cased, declined, fields, elements, problems) = (Weakenings(), 0, 0, 0, 0, new List<string>(), new List<string>(), new List<string>(), new List<string>());
            foreach (var w in weakenings.TakeWhile(_ => first == false || problems.Count == 0))
            {
                var statement = w.Source!;
                var accessors = statement.AccessorsOf(w.Conjunct);
                var shown = $"{w.Set} {w.Input} {w.Name}/{w.Statement}/{w.Conjunct} {w.Mine}";
                var loading = new CosmosPredicateLoader.Loading(statement.BindingForCheck.Fields, statement.BindingForCheck.Readings, CosmosImplementor.DefaultRootAlias);
                Builder built = form => RenderingRexBuilder.Build(form, statement.RexBuilderForCheck, accessors);
                // A weakening testing a path over a node the body raises over, a field or a call, read back as that node's test:
                // S declines it beneath, and the split rule drops what that filter declines, RU, never rows.
                if (TestsAField(w.Mine!, accessors) && built(w.Mine!) is { } tested && Sent(loading.Read(tested), w.Set, w.Claim?.Imports, Ordered(w.Set)) is null)
                {
                    fields.Add(shown);
                    continue;
                }

                // A weakening read back over an element's raw value, the element reading no row measured (h15b): S proves no form
                // of it beneath, and the split rule drops what that filter declines, RU, never rows.
                if (built(w.Mine!) is { } over && loading.Read(over) is var back && ReadsTheElementReading(back) && Sent(back, w.Set, w.Claim?.Imports, Ordered(w.Set)) is null)
                {
                    elements.Add(shown);
                    continue;
                }

                var conjunct = statement.Statement.Conjuncts[w.Conjunct];
                var atoms = conjunct.Subterms.Select(t => t.Digest).ToHashSet(StringComparer.Ordinal);

                // Source A over the weakening's bundle: a document drawn from its import the conjunct keeps and the form drops.
                // A conjunct holding an opaque term, which the IR's evaluator answers by a coin, is Calcite's own in process.
                string? Witness(ServiceForm sent)
                {
                    if (w.Claim is null)
                        return "no claim to draw documents from";

                    if (w.Opaque is { } opaque)
                        return InProcess(w, sent, opaque.Conjunct, opaque.Binding.Fields, opaque.Binding.Readings);

                    var (tally, world, measured) = (new RenderingSoundnessTests.Tally(), new World(0), 0);
                    foreach (var document in RenderingDocumentSampler.Sample(w.Claim))
                        measured += tally.Judge(RenderingSoundnessTests.Tally.Calcite(conjunct, document, world), conjunct, sent, Verdict.Implies, RenderPosition.Pos, document,
                            ProbeRows.Measured.Serves[w.Set], shown) ? 1 : 0;
                    return tally.Witnesses.FirstOrDefault() ?? (measured == 0 ? "no document drawn measures it" : null);
                }

                var (found, written, mine) = RoundTrip(w.Mine!, RenderPosition.Pos, plant?.Invoke(built) ?? built, Printed, loading,
                    (f, ir, pos, verdict) => Claim(f, ir, pos, verdict, w.Set, w.Claim?.Imports, Ordered(w.Set)), ir => atoms.Contains(ir.Digest),
                    conjunct, ir => Sent(ir, w.Set, w.Claim?.Imports, Ordered(w.Set)), Witness);
                (own, exact, guarded, cased) = (own + mine, exact + (w.Mine!.Text == written?.Text ? 1 : 0), guarded + (written is not null && w.Mine!.Text != written.Text ? 1 : 0), cased + (IsCased(w.Mine!) ? 1 : 0));
                if (IsCased(w.Mine!) && written is null && found.Count == 0)
                    (DeclinedByTheCut(w.Mine!) ? declined : found).Add(DeclinedByTheCut(w.Mine!) ? shown : "declined beneath, short of the cut");
                else if (written is null && found.Count == 0)
                    declined.Add(shown + ": no exact form of its reading, not pushed");
                problems.AddRange(found.Select(p => $"{shown}: {p}"));
            }

            return (weakenings.Count, exact, guarded, own, cased, declined, fields, elements, problems);
        }

        /// <summary>
        /// Whether a predicate reads a value the shared vocabulary reads as the element reading (h15b): a raw value of a path
        /// holding an element, however written, or a reading written apart of one.
        /// </summary>
        static bool ReadsTheElementReading(SqlPredicate read) => read.Subterms.SelectMany(term => term switch
        {
            SqlPredicate.Compare c => [c.Left, c.Right],
            SqlPredicate.In i => [i.Value],
            SqlPredicate.IsNull n => [n.Value],
            SqlPredicate.IsDefined d => [d.Value],
            SqlPredicate.TypeTest t => [t.Value],
            SqlPredicate.Affix a => [a.Value],
            _ => Array.Empty<SqlValue>(),
        }).Any(v => v.PathRead is { HasElement: true } && (v is SqlValue.Raw || v.Form is not null));

        /// <summary>
        /// Source A for a conjunct holding an opaque term, as line 1c judges one (<see cref="RenderingSoundnessTests.NoOpaqueWeakeningHasAWitnessInProcess"/>):
        /// documents drawn from the weakening's import, widened to the paths the form reads, beside the literals the conjunct
        /// compares with, each judged by Calcite in process. Returns a document Calcite keeps and the form drops on a target the
        /// set serves; why nothing was judged where no document was; or <c>null</c>.
        /// </summary>
        static string? InProcess(RenderingGenerateRun.Weakened w, ServiceForm sent, RexNode conjunct, IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading> readings)
        {
            if (CalciteInProcess.Compile(conjunct, fields, readings) is not { } calcite)
                return "a field with no path, or a conjunct Calcite cannot compile alone";

            var imports = w.Claim!.Imports.ToDictionary(i => i.Key, i => i.Value);
            foreach (var path in sent.Paths.Where(p => imports.ContainsKey(p) == false))
                imports[path] = new RenderingMemo.Imported(PathValues.Anything, JsonValues.AllBands);

            var beside = CalciteInProcess.Literals(conjunct).Distinct().Select(l => ServiceForm.CompareOf(CosmosServiceTest.Equal, sent.Paths.First(), l)).ToList();
            var claim = w.Claim with { Imports = imports, Form = beside.Count == 0 ? sent : ServiceForm.JunctionOf(CosmosServiceTest.And, [sent, .. beside]) };
            var judged = 0;
            foreach (var document in RenderingDocumentSampler.Sample(claim))
            {
                if (calcite(document) is not { } answer)
                    continue;

                judged++;
                if (answer == Truths.True)
                    foreach (var target in ProbeRows.Measured.Serves[w.Set])
                        if (CosmosEval.On(sent, document, target) is { } service && service != ServiceOutcomes.True)
                            return $"Calcite keeps {Documents.Json(document)} in process, {target} {service}";
            }

            return judged == 0 ? "no document drawn measures it" : null;
        }

        /// <summary>
        /// Every weakening S gives over the pull request's inputs round trips, riding the check run's pass: production's S,
        /// which asks every template since the switch (h15), the guarded comparison's among them, each <c>CASE</c> read back
        /// by the loader and proven by S, and each printed as the filter beneath prints it.
        /// </summary>
        [Fact]
        public void EveryPushedWeakeningRoundTrips()
        {
            var (weakenings, exact, guarded, own, cased, declined, fields, elements, problems) = Pushed();
            TestContext.Current.TestOutputHelper?.WriteLine($"{weakenings} weakenings: {exact} printed and read back as themselves, {guarded} sent as S's form of what Calcite "
                + $"reads of them, {cased} with a CASE ({declined.Count} of them declined beneath), {fields.Count} holding a test of a path over a field or a call, declined beneath, "
                + $"{elements.Count} read beneath over an element's raw value, which S declines; {own} comparisons the conjunct's own");
            foreach (var weakening in declined)
                TestContext.Current.TestOutputHelper?.WriteLine($"declined beneath: {weakening}");
            foreach (var weakening in fields)
                TestContext.Current.TestOutputHelper?.WriteLine($"a test of a path over a field or a call: {weakening}");
            foreach (var weakening in elements)
                TestContext.Current.TestOutputHelper?.WriteLine($"over an element's raw value: {weakening}");
            foreach (var problem in problems)
                TestContext.Current.TestOutputHelper?.WriteLine(problem);

            weakenings.Should().BeGreaterThan(300);
            problems.Should().BeEmpty();
            cased.Should().BeGreaterThan(declined.Count, "the corpus compares numbers through casts of the text and the text with numerals");
        }

        /// <summary>
        /// A <c>CASE</c>'s round trip holds S's best form to the one claimed up to absorption, and no further: partitions
        /// 1967's weakening, whose <c>NOT IS_STRING(big) OR big != '9'</c> its sibling <c>big != '9'</c> absorbs, is the best
        /// form that nests the member beneath <c>IS_DEFINED</c> and <c>NOT IS_NULL</c>; altered in one literal, or with
        /// the absorbing sibling gone, it is another form.
        /// </summary>
        [Fact]
        public void ACaseRoundTripIsHeldUpToAbsorptionAlone()
        {
            var (big, price) = (SqlValue.Spell(JsonDocumentPath.Root.Property("big")), SqlValue.Spell(JsonDocumentPath.Root.Property("price")));
            ServiceForm And(params ServiceForm[] m) => ServiceForm.JunctionOf(CosmosServiceTest.And, m);
            ServiceForm Or(params ServiceForm[] m) => ServiceForm.JunctionOf(CosmosServiceTest.Or, m);
            ServiceForm Test(CosmosServiceTest test) => ServiceForm.TestOf(test, big);
            ServiceForm Other(string literal) => ServiceForm.CompareOf(CosmosServiceTest.NotEqual, big, JsonValues.Of(literal));
            var (defined, notNull, primitive, notString) = (Test(CosmosServiceTest.IsDefined), ServiceForm.NotOf(Test(CosmosServiceTest.IsNull)), Test(CosmosServiceTest.IsPrimitive),
                ServiceForm.NotOf(Test(CosmosServiceTest.IsString)));
            var priced = And(ServiceForm.TestOf(CosmosServiceTest.IsDefined, price),
                Or(ServiceForm.NotOf(ServiceForm.TestOf(CosmosServiceTest.IsNumber, price)), ServiceForm.CompareOf(CosmosServiceTest.Less, price, JsonValues.Of(30m))));

            var claimed = Or(priced, And(Other("9"), defined, Or(notString, Other("9")), notNull, primitive, Other("b")));
            var best = Or(And(primitive, defined, notNull, Other("9"), Other("b"), Or(And(defined, notNull, Other("9")), notString)), priced);

            Absorbed(best).Key.Should().Be(Absorbed(claimed).Key);
            Absorbed(claimed).Key.Should().Be(Absorbed(Or(priced, And(Other("9"), defined, notNull, primitive, Other("b")))).Key, "the absorbed member is the only one dropped");
            Absorbed(best).Key.Should().NotBe(Absorbed(Or(And(primitive, defined, notNull, Other("9"), Other("b"), Or(And(defined, notNull, Other("8")), notString)), priced)).Key,
                "an arm altered in a literal absorbs nothing");
            Absorbed(best).Key.Should().NotBe(Absorbed(Or(And(primitive, defined, notNull, Other("b"), Or(And(defined, notNull, Other("9")), notString)), priced)).Key,
                "nor one whose sibling is gone: over a number, big != '9' is undefined where NOT IS_STRING(big) is true");
        }

        /// <summary>
        /// A path read through typed accessors of two SQL types writes each member over the reader S judged it over (spec
        /// h09, the member carries its reader): <c>RETURNING INTEGER</c> raises where <c>BIGINT</c> reads 3000000000, so a
        /// comparison is written through its own accessor, never one the builder chose; one judged over no reader writes
        /// nothing. Its type tests and string comparisons are still written over its raw value.
        /// </summary>
        [Fact]
        public void APathReadThroughTwoTypesWritesEachMemberOverItsOwn()
        {
            var loading = new CosmosPredicateLoader.Loading(Fields, Readings, CosmosImplementor.DefaultRootAlias);
            var (integer, bigint, p) = (JsonValue("p", SqlTypeName.INTEGER), JsonValue("p", SqlTypeName.BIGINT), SqlValue.Spell(JsonDocumentPath.Root.Property("p")));
            var two = Rex.makeCall(SqlStdOperatorTable.OR, Rex.makeCall(SqlStdOperatorTable.EQUALS, integer, Rex.makeExactLiteral(java.math.BigDecimal.ONE)),
                Rex.makeCall(SqlStdOperatorTable.GREATER_THAN, bigint, Rex.makeExactLiteral(new java.math.BigDecimal("3000000000"))));
            var accessors = loading.AccessorsOf(two, Rex);
            var compared = ServiceForm.CompareOf(CosmosServiceTest.Greater, p, JsonValues.Of(3000000000m));
            RexNode? Over(string? reader) => RenderingRexBuilder.Build(reader is null ? compared : ServiceForm.Read(compared, reader), Rex, accessors) is RexCall call ? (RexNode)call.getOperands().get(0) : null;

            accessors[p].Readers.Keys.Should().BeEquivalentTo([PredicateAtom.TypedReader("INTEGER"), PredicateAtom.TypedReader("BIGINT")]);
            accessors[p].Raw.Should().NotBeNull();
            (Over(PredicateAtom.TypedReader("BIGINT"))?.Equals(bigint), Over(PredicateAtom.TypedReader("INTEGER"))?.Equals(integer)).Should().Be((true, true));
            Over(null).Should().BeNull("a number judged over no reader is written over none");
            RenderingRexBuilder.Build(ServiceForm.TestOf(CosmosServiceTest.IsDefined, p), Rex, accessors).Should().NotBeNull();
        }

        /// <summary>
        /// One form builds one Rex, equal by Calcite's equality and read as one term, however often its accessors are read:
        /// the loop guard (<c>IsApplied</c>, <c>AlreadyApplied</c>) finds a weakening beneath by identity, one holding a test
        /// over a field or a call (<see cref="TestsAField"/>) among them.
        /// </summary>
        [Fact]
        public void TheSameFormBuildsTheSameRex()
        {
            var parted = new List<string>();
            foreach (var w in Weakenings())
            {
                var statement = w.Source!;
                var (first, again) = (RenderingRexBuilder.Build(w.Mine!, statement.RexBuilderForCheck, statement.AccessorsOf(w.Conjunct)),
                    RenderingRexBuilder.Build(w.Mine!, statement.RexBuilderForCheck, statement.AccessorsOf(w.Conjunct)));
                var loading = new CosmosPredicateLoader.Loading(statement.BindingForCheck.Fields, statement.BindingForCheck.Readings, CosmosImplementor.DefaultRootAlias);
                if (first is null || again is null || first.Equals(again) == false || first.toString() != again.toString()
                    || new HashSet<RexNode> { first }.Contains(again) == false || loading.Read(first).Digest != loading.Read(again).Digest)
                    parted.Add($"{w.Input} {w.Name}/{w.Statement}/{w.Conjunct} {w.Mine}: {first} and {again}");
            }

            parted.Should().BeEmpty();
        }

        /// <summary>
        /// A weakening pushed is applied beneath (#210, over S): the residual's statement, asked again with the conjuncts of
        /// the filter the split rule builds beneath it, finds it applied, so the rule matching the residual next time pushes
        /// it no more.
        /// </summary>
        [Fact]
        public void AWeakeningIsPushedOnce()
        {
            var (pushed, again) = (0, new List<string>());
            foreach (var w in Weakenings())
            {
                // One the builder writes is pushed at most once, whatever the filter beneath then admits of it.
                var statement = w.Source!;
                if (RenderingRexBuilder.Build(w.Mine!, statement.RexBuilderForCheck, statement.AccessorsOf(w.Conjunct)) is not { } rex || statement.IsApplied(rex, []))
                    continue;

                pushed++;
                var beneath = CosmosFilterStatement.ConjunctsOf(RexUtil.composeConjunction(statement.RexBuilderForCheck, java.util.Collections.singletonList(rex)));
                var rebuilt = RenderingRexBuilder.Build(w.Mine!, statement.RexBuilderForCheck, statement.AccessorsOf(w.Conjunct))!;
                if (statement.IsApplied(rebuilt, beneath) == false)
                    again.Add($"{w.Input} {w.Name}/{w.Statement}/{w.Conjunct} {w.Mine}: {rebuilt} beneath {string.Join(", ", beneath)}");
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"{pushed} weakenings pushed; {again.Count} pushed again");
            pushed.Should().BeGreaterThan(100);
            again.Should().BeEmpty();
        }

    }

}
