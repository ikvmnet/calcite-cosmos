using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
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
        /// the form itself where S holds it <c>Equal</c> to that reading where it stands. Every claim, and every best, is asked
        /// over the import the filter beneath's oracle asks it over (<see cref="Import"/>), since what S proves exact of a
        /// comparison of a computed place rests on the place's characters, which that oracle imports and the paths alone do
        /// not hold. Where S does not hold it <c>Equal</c> — Calcite's <c>&lt;&gt;</c> keeps no null the service's <c>!=</c>
        /// keeps, so a bare <c>!=</c> at <c>Pos</c> is read tighter — the filter sends S's best exact form of the reading,
        /// which the atom must imply, or the weakening would cost a row it keeps: the form itself up to absorption, which
        /// generate mode proved the reading of and S gave as implied by the atom, where the check's claim mode proves no
        /// further over a wide junction; else one the check proves implied; else, where the check leaves it unproven, as it
        /// leaves a junction of two atoms of one path (<c>TODO.md</c>) or one over an opaque term's strict paths, one
        /// <paramref name="witness"/> judges on documents drawn for the best and the reading: the best drops no row the atom
        /// keeps on any of them, and the atom keeps a row on one at least, since an implication fails on no other row.
        /// Either is printed over the reading's places and must read back as itself. Unproven with no reason, over a
        /// <c>CASE</c>, is S declining the filter beneath, which keeps it in process and costs RU alone; a <c>CASE</c> S proves
        /// beneath its arm's licence is sent as its best, as any other (h15).
        /// </summary>
        internal static (List<string> Problems, ServiceForm? Written, int Own) RoundTrip(ServiceForm form, RenderPosition at, Builder build,
            Func<CosmosParameterList, Printer> printer, CosmosPredicateLoader.Loading loading, Claimer claim, Func<SqlPredicate, bool> own,
            SqlPredicate atom, Func<SqlPredicate, ServiceForm?> sent, Func<ServiceForm, SqlPredicate, string?>? witness = null)
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
                        && (implied.Kind == RenderingVerdictKind.Unproven && witness is not null ? witness(best, read) : "") is { } kept)
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
        /// Imports a question's IR into a bundle as the filter beneath's oracle imports it (<c>RenderingOracle.Ask</c>): every
        /// place the IR reads, a path read only inside a call among them, and every computed place's characters, which the
        /// importer imports for each call the roots read. Each, and each path beside, is imported as the weakening's bundle
        /// imported it, found by its spelling, its characters kept to those given; and, where that bundle imported none, as
        /// anything over the characters given, or every class. Returns what each path was imported as, which
        /// <see cref="StateArms"/> reads.
        /// </summary>
        static Dictionary<JsonDocumentPath, PathValues> Import(RenderingBundle bundle, SqlPredicate ir, IEnumerable<JsonDocumentPath> beside,
            IReadOnlyDictionary<string, RenderingMemo.Imported>? imports, ValueSet<string>? chars)
        {
            ValueSet<string> Kept(ValueSet<string> held) => chars is null ? held : ValueSet.From(held.Where(chars.Contains));
            var imported = new Dictionary<JsonDocumentPath, PathValues>();
            foreach (var path in ir.Places.Concat(beside).Distinct())
                if (imports?.GetValueOrDefault(SqlValue.Spell(path)) is { } import)
                    bundle.Import(ProbeRows.Source, path, imported[path] = import.Values, Kept(import.Chars));
                else
                    bundle.Import(ProbeRows.Source, path, imported[path] = PathValues.Anything, chars ?? JsonValues.AllBands);

            foreach (var call in ir.Computed.Where(c => c.Spelled is not null).DistinctBy(c => c.Spelled, StringComparer.Ordinal))
                bundle.ImportComputed(call, imports?.GetValueOrDefault(call.Spelled!) is { } import ? Kept(import.Chars) : chars ?? JsonValues.AllBands);

            return imported;
        }

        /// <summary>
        /// S's best exact form of an IR at <c>Pos</c> under a set, each place imported as <see cref="Import"/> imports it, a
        /// computed place's characters among them, and an arm's atom stated as <see cref="Claim"/> states it, as the arm's
        /// licence states it in production (h15): what the filter beneath sends of a Rex the loader reads as the IR, or
        /// <c>null</c> where S proves none, which keeps it in process.
        /// </summary>
        internal static ServiceForm? Sent(SqlPredicate ir, RenderingSet set, IReadOnlyDictionary<string, RenderingMemo.Imported>? imports = null, ValueSet<string>? chars = null)
        {
            using var bundle = ProbeRows.ProbeTheory.Open(set);
            var term = bundle.Write(ir);
            var imported = Import(bundle, ir, [], imports, chars);
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
        /// A form claimed for an IR at a position under a set, each place the IR reads, a computed place's characters among
        /// them, imported as <see cref="Import"/> imports it, and each path the form reads beside them; at <c>Neg</c>, the
        /// negation of each at <c>Pos</c>. An atom read beneath an arm whose conditions hold <c>IS_NUMBER</c> of its path is
        /// stated there as a number, which is what the facts graph reads of that condition exactly.
        /// </summary>
        internal static RenderingVerdict Claim(ServiceForm form, SqlPredicate ir, RenderPosition pos, Verdict verdict, RenderingSet set,
            IReadOnlyDictionary<string, RenderingMemo.Imported>? imports = null, ValueSet<string>? chars = null)
        {
            var (claimed, root) = pos == RenderPosition.Pos ? (form, ir) : (ServiceForm.Negated(form), (SqlPredicate)new SqlPredicate.Not(ir));
            using var bundle = ProbeRows.ProbeTheory.Open(set);
            var term = bundle.Write(root);
            var imported = Import(bundle, root, form.Paths.Select(CosmosEval.PathOf), imports, chars);
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
        /// Six of the gate's statements, planned once a process in a generate run of their own over the differential's
        /// containers, at either size: partitions 2410, 2411, 2419 and 2420, whose weakenings under <c>Production</c>
        /// compare the concatenation <c>id || '!'</c>; 416, whose conjunct reads <c>id</c> only inside an opaque term; and
        /// 166, whose conjuncts under <c>Emulator</c> hold tests Calcite in process does not compile alone.
        /// </summary>
        static readonly Lazy<IReadOnlyList<RenderingGenerateRun.Weakened>> Gate = new(() =>
        {
            (string Name, string Sql)[] statements =
            [
                ("2410", "SELECT c.\"id\" FROM products AS c WHERE ((CASE WHEN LOWER(JSON_VALUE(c.\"DOC\", '$.metadata')) = 'none' THEN JSON_VALUE(c.\"DOC\", '$.metadata') IS NULL "
                    + "ELSE CAST(c.\"$.category\" AS VARCHAR) NOT LIKE '_prin_' END) OR (((JSON_VALUE(c.\"DOC\", '$.price') + 1) / 3) < 340 OR (c.\"id\" || '!') = 'BIKES'))"),
                ("2411", "SELECT c.\"id\" FROM products AS c WHERE NOT (((CASE WHEN LOWER(JSON_VALUE(c.\"DOC\", '$.metadata')) = 'none' THEN JSON_VALUE(c.\"DOC\", '$.metadata') IS NULL "
                    + "ELSE CAST(c.\"$.category\" AS VARCHAR) NOT LIKE '_prin_' END) OR (((JSON_VALUE(c.\"DOC\", '$.price') + 1) / 3) < 340 OR (c.\"id\" || '!') = 'BIKES')))"),
                ("2419", "SELECT c.\"id\" FROM typed AS c WHERE ((c.\"id\" || '!') <> 'TRUE' OR (((CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) * 2E0) < 31E0) IS FALSE))"),
                ("2420", "SELECT c.\"id\" FROM typed AS c WHERE NOT (((c.\"id\" || '!') <> 'TRUE' OR (((CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) * 2E0) < 31E0) IS FALSE)))"),
                ("416", "SELECT c.\"id\" FROM products AS c WHERE (CASE WHEN ((JSON_VALUE(c.\"DOC\", '$.price') + 1) > 0 OR JSON_VALUE(c.\"DOC\", '$.tags[0]') IS NULL) "
                    + "THEN (c.\"id\" < 'outdoor' OR COALESCE(JSON_VALUE(c.\"DOC\", '$.tags[0]'), 'none') LIKE '%e%') ELSE (JSON_VALUE(c.\"DOC\", '$.metadata') || '!') NOT LIKE 'B%' END)"),
                ("166", "SELECT c.\"id\" FROM products AS c WHERE NOT (((JSON_VALUE(c.\"DOC\", '$.metadata') = 'shoes' AND CAST(JSON_VALUE(c.\"DOC\", '$.price') AS DOUBLE) = 120E0) "
                    + "AND JSON_VALUE(c.\"DOC\", '$.tags') < 'Sprint'))"),
            ];

            var generated = new RenderingGenerateRun();
            var containers = generated.Bind(CosmosDifferentialTests.Containers);
            RenderingCheckRun.Over(statements.Select(s => RenderingInputs.Differential("partitions", [s.Sql], containers).Single() with { Name = s.Name }), generated: generated);
            return [.. generated.Weakenings.Where(w => w.Mine is not null && w.Source is not null && RenderingGenerateRun.AssertedSets.Contains(w.Set))
                .DistinctBy(w => (w.Source, w.Conjunct, w.Mine!.Text, w.Set))];
        });

        /// <summary>The weakenings of six of the gate's statements under the asserted sets (<see cref="Gate"/>), as <see cref="Weakenings"/> gives the run's.</summary>
        internal static IReadOnlyList<RenderingGenerateRun.Weakened> GateStatements() => Gate.Value;

        /// <summary>
        /// Every weakening S gives a residual over the pull request's inputs, or each of those given, built over its
        /// conjunct's own accessors and held to the round trip under its set, at the import of the bundle it was read from, a
        /// string's characters as <see cref="Ordered"/> keeps them, and printed as the filter beneath prints it (line 13,
        /// h15). Where S proves no implication of what is sent, documents judge it: source A's over that bundle, mixed and
        /// each value apart, by IrEval wherever it models every reading, and those drawn for what is sent
        /// (<see cref="DrawnFor"/>) by IrEval and by Calcite in process wherever the conjunct compiles alone
        /// (<see cref="InProcess"/>): a witness of either fails it, and so does no document keeping a row its conjunct keeps,
        /// kept in process for a conjunct holding an opaque term. One holding a test of a path written over a field or a call,
        /// read back as that node's test, which S declines beneath, is counted apart: production does not push it; and one read
        /// back over a reading of an element the readings of an index do not name as of a path, which S declines beneath
        /// (h15b).
        /// </summary>
        internal static (int Weakenings, int Exact, int Guarded, int Own, int Cased, List<string> Declined, List<string> Fields, List<string> Elements, List<string> Problems) Pushed(Func<Builder, Builder>? plant = null,
            bool first = false, IReadOnlyList<RenderingGenerateRun.Weakened>? source = null)
        {
            var (weakenings, exact, guarded, own, cased, declined, fields, elements, problems) = (source ?? Weakenings(), 0, 0, 0, 0, new List<string>(), new List<string>(), new List<string>(), new List<string>());
            foreach (var w in weakenings.TakeWhile(_ => first == false || problems.Count == 0))
            {
                var statement = w.Source!;
                var accessors = statement.AccessorsOf(w.Conjunct);
                var shown = $"{w.Set} {w.Input} {w.Name}/{w.Statement}/{w.Conjunct} {w.Mine}";
                var loading = new CosmosPredicateLoader.Loading(statement.BindingForCheck.Fields, statement.BindingForCheck.Readings, CosmosImplementor.DefaultRootAlias);
                Builder built = form => RenderingRexBuilder.Build(form, statement.RexBuilderForCheck, accessors);
                // A weakening over an input holding no document column, of a path read through a field as text alone: no node
                // names its raw value, a cast to ANY being the text Calcite holds, so none is built, and the split rule weakens no
                // filter over a projection (OverProjection), the only input that drops the column.
                if (HoldsNoRawValue(w.Mine!, accessors) && built(w.Mine!) is null)
                {
                    declined.Add(shown + ": no raw value over an input holding no document column, not pushed");
                    continue;
                }

                // A weakening testing a path over a node the body raises over, a field or a call, read back as that node's test:
                // S declines it beneath, and the split rule drops what that filter declines, RU, never rows.
                if (TestsAField(w.Mine!, accessors) && built(w.Mine!) is { } tested && Sent(loading.Read(tested), w.Set, w.Claim?.Imports, Ordered(w.Set)) is null)
                {
                    fields.Add(shown);
                    continue;
                }

                // A weakening read back over the element reading, which no row measured (h15b): S proves no form of it beneath, and
                // the split rule drops what that filter declines, RU, never rows.
                if (built(w.Mine!) is { } over && loading.Read(over) is var back && ReadsTheElementReading(back) && Sent(back, w.Set, w.Claim?.Imports, Ordered(w.Set)) is null)
                {
                    elements.Add(shown);
                    continue;
                }

                var conjunct = statement.Statement.Conjuncts[w.Conjunct];
                var atoms = conjunct.Subterms.Select(t => t.Digest).ToHashSet(StringComparer.Ordinal);

                // Source A over the weakening's bundle, mixed and each value apart, judged by IrEval, and the documents drawn for
                // the form sent (DrawnFor), judged by IrEval and by Calcite in process: a document the conjunct keeps and the form
                // sent drops is a witness of either, IrEval keeping one where it is measured and the conjunct TRUE in some mode. A
                // weakening needs a kept document too, since an implication fails only on a row the conjunct keeps, and one it
                // keeps no row of says nothing of the form; for a conjunct holding an opaque term, kept in process, since IrEval
                // answers one only where the opaque term does not decide, a coin otherwise, so its documents say nothing of the
                // rows that term keeps.
                string? Witness(ServiceForm sent, SqlPredicate read)
                {
                    if (w.Claim is null)
                        return "no claim to draw documents from";

                    var places = conjunct.Computed.Concat(read.Computed).Where(c => c.Spelled is not null).DistinctBy(c => c.Spelled, StringComparer.Ordinal)
                        .ToDictionary(c => c.Spelled!, StringComparer.Ordinal);
                    var drawn = DrawnFor(w, sent, statement.Conjuncts[w.Conjunct], [.. conjunct.Places.Concat(read.Places).Select(SqlValue.Spell).Distinct()]).ToList();
                    var (tally, world, kept) = (new RenderingSoundnessTests.Tally(), new World(0), 0);
                    foreach (var document in RenderingDocumentSampler.Sample(w.Claim).Concat(RenderingDocumentSampler.Apart(w.Claim)).Concat(drawn).DistinctBy(Documents.Json))
                        if (RenderingSoundnessTests.Tally.Calcite(conjunct, document, world) is var calcite
                            && tally.Judge(calcite, conjunct, sent, Verdict.Implies, RenderPosition.Pos, document, ProbeRows.Measured.Serves[w.Set], shown) && calcite!.Contains(Truths.True))
                            kept++;

                    var opaque = conjunct.Subterms.Any(t => t is SqlPredicate.Opaque);
                    var inProcess = InProcess(w.Set, sent, statement.Conjuncts[w.Conjunct], statement.BindingForCheck.Fields, statement.BindingForCheck.Readings, places, drawn);
                    var found = new[] { tally.Witnesses.FirstOrDefault() is { } byIrEval ? "IrEval: " + byIrEval : null, inProcess.Witness }.OfType<string>().ToList();
                    return found.Count > 0 ? string.Join("; ", found)
                        : inProcess.Compiled == false && opaque ? "a field with no path, or a conjunct Calcite cannot compile alone"
                        : (opaque ? inProcess.Kept : kept + inProcess.Kept) == 0 ? "no document drawn keeps a row its conjunct keeps"
                        : null;
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
        /// Whether the shared vocabulary reads an atom of a predicate as the element reading (h15b), asked of its rules over the
        /// registry's rows rather than restated: which accessors of an element it reads so is the readings of an index's
        /// (<c>asofapath</c>), and a reading written apart of one always. Only a value of a path holding an element can.
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
        }).Any(v => v.PathRead is { HasElement: true }) && PredicateVocabularyTests.AtomsIn(read).Any(a => a.Reader == PredicateAtom.ElementReader);

        /// <summary>
        /// Calcite in process over the documents drawn for the form sent (<see cref="DrawnFor"/>), each document Calcite answers
        /// judged as line 1c judges one (<see cref="JudgedInProcess"/>) on every target the set serves, each computed place
        /// given read as the service evaluates it: a form over a place no target reads is answered by none, and a document a
        /// target leaves unanswered is neither judged nor kept. Returns a document Calcite keeps that the form drops on a
        /// target, whatever another answers, and how many were judged and kept; or that the conjunct did not compile, reading a
        /// field with no path or not compiling alone.
        /// </summary>
        static (string? Witness, int Judged, int Kept, bool Compiled) InProcess(RenderingSet set, ServiceForm sent, RexNode conjunct, IReadOnlyList<CosmosPath?> fields,
            IReadOnlyList<CosmosReading> readings, IReadOnlyDictionary<string, SqlValue.Call> places, IEnumerable<J.Obj> documents)
        {
            if (CalciteInProcess.Compile(conjunct, fields, readings) is not { } calcite)
                return (null, 0, 0, false);

            var (judged, kept) = (0, 0);
            foreach (var document in documents)
            {
                if (calcite(document) is not { } answer)
                    continue;

                var (isJudged, isKept, dropped) = JudgedInProcess(answer, sent, document, ProbeRows.Measured.Serves[set], places);
                (judged, kept) = (judged + (isJudged ? 1 : 0), kept + (isKept ? 1 : 0));
                if (dropped.Count > 0)
                    return ($"Calcite keeps {Documents.Json(document)} in process, {dropped[0].Target} {dropped[0].Service}", judged, kept, true);
            }

            return (null, judged, kept, true);
        }

        /// <summary>
        /// One document judged in process, given what Calcite answers of the conjunct on it: each target's answer of the form,
        /// each computed place given read as the service evaluates it. Where Calcite keeps the row, each target answering
        /// other than <c>TRUE</c> drops it, a witness whatever another target answers. The document is judged only where
        /// every target answers, since one a target leaves unanswered is no evidence that target keeps the row, and kept where
        /// it is judged and Calcite answers <c>TRUE</c>. The round trip's in-process judge and line 1c
        /// (<see cref="RenderingSoundnessTests.NoOpaqueWeakeningHasAWitnessInProcess"/>) judge each document so.
        /// </summary>
        internal static (bool Judged, bool Kept, List<(RenderingTarget Target, ServiceOutcomes Service)> Dropped) JudgedInProcess(Truths calcite, ServiceForm form, J.Obj document,
            IEnumerable<RenderingTarget> targets, IReadOnlyDictionary<string, SqlValue.Call> places)
        {
            var answers = targets.Select(t => (Target: t, Service: CosmosEval.On(form, document, t, places))).ToList();
            var judged = answers.All(a => a.Service is not null);
            List<(RenderingTarget Target, ServiceOutcomes Service)> dropped = calcite == Truths.True
                ? [.. answers.Where(a => a.Service is { } service && service != ServiceOutcomes.True).Select(a => (a.Target, a.Service!.Value))]
                : [];
            return (judged, judged && calcite == Truths.True, dropped);
        }

        /// <summary>
        /// The documents drawn for the form sent, which both evaluators judge: source A over the weakening's claim widened to
        /// the paths the form reads, and source A, mixed and each value apart, over it widened to the places given, which hold
        /// a path read only inside a call the form's paths leave out. Each claim stands beside the form and the literals the
        /// conjunct compares with, so a value the form compares with is drawn where the weakening's own claim holds none. The
        /// claim widened to the form's paths is drawn beside the wider one, whose cap over more places may leave out what it
        /// draws.
        /// </summary>
        static IEnumerable<J.Obj> DrawnFor(RenderingGenerateRun.Weakened w, ServiceForm sent, RexNode conjunct, IReadOnlyList<string> widen)
        {
            var (paths, places) = (Widened(w, sent, conjunct, sent.Paths), Widened(w, sent, conjunct, widen));
            return RenderingDocumentSampler.Sample(paths).Concat(RenderingDocumentSampler.Sample(places)).Concat(RenderingDocumentSampler.Apart(places)).DistinctBy(Documents.Json);
        }

        /// <summary>A weakening's claim, each path given that its bundle imported nothing of imported as anything, beside the literals its conjunct compares with.</summary>
        static RenderingMemo.Claimed Widened(RenderingGenerateRun.Weakened w, ServiceForm sent, RexNode conjunct, IEnumerable<string> widen)
        {
            var imports = w.Claim!.Imports.ToDictionary(i => i.Key, i => i.Value);
            foreach (var path in widen.Where(p => imports.ContainsKey(p) == false))
                imports[path] = new RenderingMemo.Imported(PathValues.Anything, JsonValues.AllBands);

            var beside = CalciteInProcess.Literals(conjunct).Distinct().Select(l => ServiceForm.CompareOf(CosmosServiceTest.Equal, sent.Paths.First(), l)).ToList();
            return w.Claim with { Imports = imports, Form = beside.Count == 0 ? sent : ServiceForm.JunctionOf(CosmosServiceTest.And, [sent, .. beside]) };
        }

        /// <summary>
        /// Every weakening S gives over the pull request's inputs round trips, riding the check run's pass: production's S,
        /// which asks every template since the switch (h15), the guarded comparison's among them, each <c>CASE</c> read back
        /// by the loader and proven by S, and each printed as the filter beneath prints it. One sent as S's best of what
        /// Calcite reads of it, whose implication by its conjunct S leaves unproven, is judged on documents drawn mixed and
        /// apart by IrEval and by Calcite in process, and needs a row its conjunct keeps among them.
        /// </summary>
        [Fact]
        public void EveryPushedWeakeningRoundTrips()
        {
            var (weakenings, exact, guarded, own, cased, declined, fields, elements, problems) = Pushed();
            TestContext.Current.TestOutputHelper?.WriteLine($"{weakenings} weakenings: {exact} printed and read back as themselves, {guarded} sent as S's form of what Calcite "
                + $"reads of them, {cased} with a CASE ({declined.Count} of them declined beneath), {fields.Count} holding a test of a path over a field or a call, declined beneath, "
                + $"{elements.Count} read beneath over the element reading, which S declines; {own} comparisons the conjunct's own");
            foreach (var weakening in declined)
                TestContext.Current.TestOutputHelper?.WriteLine($"declined beneath: {weakening}");
            foreach (var weakening in fields)
                TestContext.Current.TestOutputHelper?.WriteLine($"a test of a path over a field or a call: {weakening}");
            foreach (var weakening in elements)
                TestContext.Current.TestOutputHelper?.WriteLine($"over the element reading: {weakening}");
            foreach (var problem in problems)
                TestContext.Current.TestOutputHelper?.WriteLine(problem);

            weakenings.Should().BeGreaterThan(300);
            problems.Should().BeEmpty();
            cased.Should().BeGreaterThan(declined.Count, "the corpus compares numbers through casts of the text and the text with numerals");
        }

        /// <summary>
        /// A weakening comparing a computed place round trips as the filter beneath admits it: over six of the gate's
        /// statements (<see cref="GateStatements"/>), each <c>Production</c> weakening whose form reads a computed place of
        /// its conjunct, partitions 2410's, 2411's, 2419's and 2420's comparison of <c>id || '!'</c>, is held <c>Equal</c> to
        /// the reading of its Rex over the import the filter beneath's oracle asks it over, the place's characters among it,
        /// and is printed and read back as itself; and no weakening of the six is a problem. Asked over the paths alone, with
        /// no computed place, S proves nothing exact of the four, though the filter beneath proves each exact and sends it.
        /// </summary>
        [Fact]
        public void EveryWeakeningOverAComputedPlaceRoundTripsAsTheFilterBeneathAdmitsIt()
        {
            static bool OverAComputedPlace(RenderingGenerateRun.Weakened w) =>
                w.Source!.Statement.Conjuncts[w.Conjunct].Computed.Select(c => c.Spelled).OfType<string>().ToHashSet(StringComparer.Ordinal) is var places && w.Mine!.Paths.Any(places.Contains);

            var weakenings = GateStatements();
            var computed = weakenings.Where(w => w.Set == RenderingSet.Production && OverAComputedPlace(w)).ToList();
            var (all, over) = (Pushed(source: weakenings), Pushed(source: computed));
            TestContext.Current.TestOutputHelper?.WriteLine($"{all.Weakenings} weakenings of the six: {all.Exact} printed and read back as themselves, {all.Guarded} sent as S's form of what "
                + $"Calcite reads of them, {all.Declined.Count} declined beneath; {computed.Count} under Production over a computed place, {over.Exact} of them read back as themselves");
            foreach (var w in computed)
                TestContext.Current.TestOutputHelper?.WriteLine($"over a computed place: {w.Set} {w.Input} {w.Name}/{w.Statement}/{w.Conjunct} {w.Mine}");
            foreach (var problem in all.Problems)
                TestContext.Current.TestOutputHelper?.WriteLine(problem);

            all.Problems.Should().BeEmpty();
            computed.Count.Should().BeGreaterThanOrEqualTo(4, "2410, 2411, 2419 and 2420 each weaken to a comparison of the concatenation under Production");
            (over.Exact, over.Problems.Count).Should().Be((computed.Count, 0), "each is proven exact over the filter beneath's import and printed as itself");
        }

        // ── Builders planted to write a weakening otherwise ────────────────────────────────────────────────────────────

        /// <summary>A form with each leaf a function gives written as it gives it.</summary>
        static ServiceForm Leaves(ServiceForm form, Func<ServiceForm, ServiceForm?> leaf) => form.Kind switch
        {
            ServiceFormKind.Not => ServiceForm.NotOf(Leaves(form.Members[0], leaf)),
            ServiceFormKind.And or ServiceFormKind.Or => ServiceForm.JunctionOf(form.Test, form.Members.Select(m => Leaves(m, leaf))),
            _ => leaf(form) ?? form,
        };

        /// <summary>A comparison made in place of another, over the reader the other was judged over.</summary>
        static ServiceForm Like(ServiceForm made, ServiceForm other) => other.Reader is { } reader ? ServiceForm.Read(made, reader) : made;

        /// <summary>A form with each comparison of a computed place by <c>!=</c> written <c>=</c>.</summary>
        static ServiceForm FlippedPlace(ServiceForm form) => Leaves(form, f => f.Kind == ServiceFormKind.Compare && f.Test == CosmosServiceTest.NotEqual && f.Path!.Contains('(')
            ? Like(ServiceForm.CompareOf(CosmosServiceTest.Equal, f.Path, f.Literal!), f) : null);

        /// <summary>A form with each comparison of a computed place with a string written against <c>''</c>.</summary>
        static ServiceForm OtherLiteral(ServiceForm form) => Leaves(form, f => f.Kind == ServiceFormKind.Compare && f.Path!.Contains('(') && JsonValues.IsString(f.Literal!)
            ? Like(ServiceForm.CompareOf(f.Test, f.Path, JsonValues.Of("")), f) : null);

        /// <summary>G13's plant: a form with the last disjunct of the first <c>OR</c> it meets dropped.</summary>
        static ServiceForm Dropped(ServiceForm form)
        {
            if (form.Kind == ServiceFormKind.Or)
                return form.Members.Count == 2 ? form.Members[0] : ServiceForm.JunctionOf(CosmosServiceTest.Or, form.Members.Take(form.Members.Count - 1));

            for (var i = 0; i < form.Members.Count; i++)
                if (Dropped(form.Members[i]) is var dropped && dropped.Equals(form.Members[i]) == false)
                    return form.Kind == ServiceFormKind.Not ? ServiceForm.NotOf(dropped) : ServiceForm.JunctionOf(form.Test, form.Members.Select((m, j) => j == i ? dropped : m));

            return form;
        }

        /// <summary>A Rex with each comparison of a concatenation with a string written against the string marked: <c>id || '!' = 'BIKES'</c> as <c>id || '!' = 'BIKES!'</c>.</summary>
        static RexNode Marked(RexNode node)
        {
            if (node is not RexCall call)
                return node;

            var operands = new java.util.ArrayList();
            for (var i = 0; i < call.getOperands().size(); i++)
                operands.add(Marked((RexNode)call.getOperands().get(i)));
            if (SqlKind.COMPARISON.contains(call.getKind()) && operands.get(0) is RexCall { } concat && concat.getOperator() == SqlStdOperatorTable.CONCAT
                && operands.get(1) is RexLiteral { } literal && literal.getValue() is org.apache.calcite.util.NlsString text)
                operands.set(1, Rex.makeLiteral(text.getValue() + "!"));
            return call.clone(call.getType(), operands);
        }

        /// <summary>A form with each comparison of a path, no computed place, with a string written against the string marked: <c>metadata = 'shoes'</c> as <c>metadata = 'shoesx'</c>.</summary>
        static ServiceForm MarkedPath(ServiceForm form) => Leaves(form, f => f.Kind == ServiceFormKind.Compare && f.Path!.Contains('(') == false && JsonValues.IsString(f.Literal!)
            ? Like(ServiceForm.CompareOf(f.Test, f.Path, JsonValues.Of(JsonValues.StringOf(f.Literal!) + "x")), f) : null);

        /// <summary>
        /// A planted builder is judged by the rows its conjunct keeps, over six of the gate's statements
        /// (<see cref="GateStatements"/>), on their <c>Production</c> weakenings. One writing 2419's comparison of
        /// <c>id || '!'</c> by <c>=</c> for <c>&lt;&gt;</c> is caught by both evaluators on <c>{"id":""}</c>, which Calcite
        /// keeps and the form drops: IrEval's witness and Calcite's in process, though 2419's conjunct holds no opaque term.
        /// G13's, dropping the last disjunct of the first <c>OR</c>, is caught on 2410's <c>{"category":"","id":""}</c>, its
        /// category drawn apart, beside the other places at their first. One comparing the place with <c>''</c>, which
        /// <c>id || '!'</c> never is, is sound, and 2410's and 2419's are each sent as S's form of what Calcite reads of them.
        /// One writing 2411's literal <c>'BIKES!'</c> drops a row Calcite keeps
        /// (<c>{"id":"BIKES","metadata":"none","price":1019}</c>) and keeps none on any document drawn, so it is caught for want
        /// of a row its conjunct keeps, where a document measured and keeping no row would have passed it.
        /// </summary>
        [Fact]
        public void APlantedBuilderIsJudgedByTheRowsItsConjunctKeeps()
        {
            var weakenings = GateStatements();
            IReadOnlyList<RenderingGenerateRun.Weakened> Of(params string[] names) => [.. weakenings.Where(w => w.Set == RenderingSet.Production && names.Contains(w.Name))];

            var flipped = Pushed(build => form => build(FlippedPlace(form)), source: Of("2419"));
            var dropped = Pushed(build => form => build(Dropped(form)), source: Of("2410"));
            var other = Pushed(build => form => build(OtherLiteral(form)), source: Of("2410", "2419"));
            var marked = Pushed(build => form => build(form) is { } rex ? Marked(rex) : null, source: Of("2411"));
            foreach (var (name, problems) in new[] { ("flipped", flipped.Problems), ("dropped", dropped.Problems), ("other", other.Problems), ("marked", marked.Problems) })
                foreach (var problem in problems)
                    TestContext.Current.TestOutputHelper?.WriteLine($"{name}: {problem}");
            TestContext.Current.TestOutputHelper?.WriteLine($"other: {other.Weakenings} weakenings, {other.Guarded} sent as S's form of what Calcite reads of them");

            flipped.Problems.Should().Contain(p => p.Contains("IrEval: ", StringComparison.Ordinal) && p.Contains("on {\"id\":\"\"}", StringComparison.Ordinal)
                && p.Contains("Calcite keeps {\"id\":\"\"} in process, Azure False", StringComparison.Ordinal), "both evaluators judge a conjunct with no opaque term");
            dropped.Problems.Should().Contain(p => p.Contains("{\"category\":\"\",\"id\":\"\"}", StringComparison.Ordinal), "the category set alone is drawn apart");
            other.Problems.Should().BeEmpty("id || '!' is never ''");
            other.Guarded.Should().BeGreaterThanOrEqualTo(2, "2410's and 2419's are each sent as S's form of the planted reading");
            marked.Problems.Should().Contain(p => p.Contains("no document drawn keeps a row its conjunct keeps", StringComparison.Ordinal),
                "a document whose row the conjunct does not keep says nothing of the form");
        }

        /// <summary>
        /// The in-process judge counts only documents every target answers, over 2419's <c>Production</c> weakening, widened
        /// to its conjunct's places and given its computed place, <c>id || '!'</c>: the form with the place's <c>!=</c>
        /// written <c>=</c> is caught on <c>{"id":""}</c>, which Calcite keeps in process and the service drops; the form as
        /// built finds no witness, and keeps documents. Given no place, no target answers a form over the concatenation, so
        /// the form as built judges and keeps none.
        /// </summary>
        [Fact]
        public void TheInProcessJudgeCountsOnlyDocumentsEveryTargetAnswers()
        {
            var w = GateStatements().First(w => w.Set == RenderingSet.Production && w.Name == "2419");
            var statement = w.Source!;
            var conjunct = statement.Statement.Conjuncts[w.Conjunct];
            var places = conjunct.Computed.Where(c => c.Spelled is not null).DistinctBy(c => c.Spelled, StringComparer.Ordinal).ToDictionary(c => c.Spelled!, StringComparer.Ordinal);
            (string? Witness, int Judged, int Kept, bool Compiled) Over(ServiceForm form, IReadOnlyDictionary<string, SqlValue.Call> given) => InProcess(w.Set, form,
                statement.Conjuncts[w.Conjunct], statement.BindingForCheck.Fields, statement.BindingForCheck.Readings, given,
                DrawnFor(w, form, statement.Conjuncts[w.Conjunct], [.. conjunct.Places.Select(SqlValue.Spell).Distinct()]));

            var (built, flipped, unread) = (Over(w.Mine!, places), Over(FlippedPlace(w.Mine!), places), Over(w.Mine!, new Dictionary<string, SqlValue.Call>(StringComparer.Ordinal)));
            TestContext.Current.TestOutputHelper?.WriteLine($"{w.Mine}: {built}; the place's != written =: {flipped}; with no place given: {unread}");

            places.Should().NotBeEmpty();
            flipped.Witness.Should().Contain("Calcite keeps {\"id\":\"\"} in process", "Calcite keeps a string id with no price, which the flipped form drops");
            built.Witness.Should().BeNull();
            built.Compiled.Should().BeTrue();
            built.Kept.Should().BePositive("the form as built is judged on rows its conjunct keeps");
            unread.Witness.Should().BeNull();
            (unread.Judged, unread.Kept).Should().Be((0, 0), "no target answers a form over a place it cannot read");
        }

        /// <summary>
        /// A row Calcite keeps in process that one target drops is a witness where another target leaves the document
        /// unanswered: under <c>Portable</c>, which serves the probe account and the emulator, a form planted to drop rows
        /// whose category holds a string, by ordering it against a literal holding U+0000, which the probe account answers
        /// and the emulator does not, is caught on <c>{"category":"b"}</c>, given that Calcite keeps it; that document is
        /// neither judged nor kept, since the emulator says nothing of it. One both targets answer, a number's category, is
        /// judged and kept, with no witness.
        /// </summary>
        [Fact]
        public void ARowOneTargetDropsIsAWitnessWhereAnotherLeavesItUnanswered()
        {
            var category = SqlValue.Spell(JsonDocumentPath.Root.Property("category"));
            var planted = ServiceForm.JunctionOf(CosmosServiceTest.And, [ServiceForm.TestOf(CosmosServiceTest.IsDefined, category), ServiceForm.JunctionOf(CosmosServiceTest.Or,
                [ServiceForm.NotOf(ServiceForm.TestOf(CosmosServiceTest.IsString, category)), ServiceForm.CompareOf(CosmosServiceTest.Less, category, JsonValues.Of("\0"))])]);
            var (text, number) = (RenderingDocumentSampler.Document([category], [new J.Str("b")]), RenderingDocumentSampler.Document([category], [new J.Num(1)]));
            var (targets, none) = (ProbeRows.Measured.Serves[RenderingSet.Portable], new Dictionary<string, SqlValue.Call>(StringComparer.Ordinal));
            var (overText, overNumber) = (JudgedInProcess(Truths.True, planted, text, targets, none), JudgedInProcess(Truths.True, planted, number, targets, none));
            TestContext.Current.TestOutputHelper?.WriteLine($"{planted} on {Documents.Json(text)}: {overText.Judged}, {overText.Kept}, [{string.Join(", ", overText.Dropped)}]; "
                + $"on {Documents.Json(number)}: {overNumber.Judged}, {overNumber.Kept}, [{string.Join(", ", overNumber.Dropped)}]");

            targets.Should().Contain(RenderingTarget.Azure).And.Contain(RenderingTarget.Emulator);
            CosmosEval.On(planted, text, RenderingTarget.Azure).Should().Be(ServiceOutcomes.False, "the probe account orders a string against U+0000");
            CosmosEval.On(planted, text, RenderingTarget.Emulator).Should().BeNull("the emulator leaves a string ordered against U+0000 unanswered");
            overText.Dropped.Should().Equal((RenderingTarget.Azure, ServiceOutcomes.False));
            (overText.Judged, overText.Kept).Should().Be((false, false), "a document a target leaves unanswered is no evidence that target keeps the row");
            (overNumber.Judged, overNumber.Kept, overNumber.Dropped.Count).Should().Be((true, true, 0));
        }

        /// <summary>
        /// An opaque conjunct is held to the rows Calcite keeps in process: G13's dropped disjunct over 416, whose conjunct
        /// reads <c>id</c> only inside its opaque term, is a problem under both sets for want of a row its conjunct keeps.
        /// IrEval keeps documents where the opaque term does not decide, which say nothing of the rows that term keeps; Calcite
        /// keeps <c>{"id":"","metadata":"none","tags":["a"]}</c>, which the form drops, and no document drawn sets <c>id</c>,
        /// which Calcite's binding reads never null.
        /// </summary>
        [Fact]
        public void AnOpaqueConjunctIsHeldToTheRowsCalciteKeepsInProcess()
        {
            var weakenings = GateStatements().Where(w => w.Name == "416").ToList();
            var dropped = Pushed(build => form => build(Dropped(form)), source: weakenings);
            foreach (var problem in dropped.Problems)
                TestContext.Current.TestOutputHelper?.WriteLine(problem);

            weakenings.Should().OnlyContain(w => w.Source!.Statement.Conjuncts[w.Conjunct].Subterms.Any(t => t is SqlPredicate.Opaque));
            foreach (var set in RenderingGenerateRun.AssertedSets)
                dropped.Problems.Should().Contain(p => p.StartsWith($"{set} ", StringComparison.Ordinal) && p.Contains("no document drawn keeps a row its conjunct keeps", StringComparison.Ordinal),
                    $"under {set}, IrEval's documents say nothing of the rows the opaque term keeps");
        }

        /// <summary>
        /// IrEval judges the documents drawn for the form sent, as Calcite in process does, beside the weakening's own: over
        /// 166's weakenings beneath under <c>Emulator</c>, whose conjunct holds tests Calcite in process does not compile
        /// alone, so that IrEval judges them alone, a builder writing metadata's <c>'shoes'</c> as <c>'shoesx'</c> drops
        /// <c>{"metadata":"shoesx"}</c>, which the conjunct keeps, and is caught on a document holding the planted literal.
        /// The weakening's claim compares with <c>'shoes'</c>, so source A over it alone draws no such document.
        /// </summary>
        [Fact]
        public void IrEvalJudgesTheDocumentsDrawnForTheFormSent()
        {
            var weakenings = GateStatements().Where(w => w.Name == "166"
                && CalciteInProcess.Compile(w.Source!.Conjuncts[w.Conjunct], w.Source.BindingForCheck.Fields, w.Source.BindingForCheck.Readings) is null).ToList();
            var marked = Pushed(build => form => build(MarkedPath(form)), source: weakenings);
            foreach (var problem in marked.Problems)
                TestContext.Current.TestOutputHelper?.WriteLine(problem);

            weakenings.Should().NotBeEmpty("166's conjuncts hold tests Calcite in process does not compile alone");
            marked.Problems.Should().HaveCount(weakenings.Count).And.OnlyContain(p => p.Contains("IrEval: ", StringComparison.Ordinal)
                && p.Contains("\"metadata\":\"shoesx\"", StringComparison.Ordinal), "IrEval judges documents holding the literal the form sent compares with");
        }

        /// <summary>
        /// Apart sets each place alone beside the others first: over a claim of <c>category</c> and <c>price</c>, which may
        /// hold anything, and <c>id</c>, a string alone, it yields for each value source A draws for <c>category</c> a
        /// document holding it beside <c>id</c>'s first string and no <c>price</c>, <c>{"category":"","id":""}</c> among
        /// them, and every document it yields is one each import admits.
        /// </summary>
        [Fact]
        public void ApartSetsEachPlaceAloneBesideTheOthersFirst()
        {
            var (category, price, id) = (SqlValue.Spell(JsonDocumentPath.Root.Property("category")), SqlValue.Spell(JsonDocumentPath.Root.Property("price")),
                SqlValue.Spell(JsonDocumentPath.Root.Property("id")));
            var imports = new Dictionary<string, RenderingMemo.Imported>(StringComparer.Ordinal)
            {
                [category] = new(PathValues.Anything, JsonValues.AllBands),
                [price] = new(PathValues.Anything, JsonValues.AllBands),
                [id] = new(PathValues.OfKinds(ValueSet.Of(JsonValues.StringType)), JsonValues.AllBands),
            };
            var claim = new RenderingMemo.Claimed([], ServiceForm.TestOf(CosmosServiceTest.IsDefined, category), [], imports, [], null);
            var apart = RenderingDocumentSampler.Apart(claim).Select(Documents.Json).ToList();
            var drawn = RenderingDocumentSampler.Sample(claim).Select(d => CosmosEval.At(d, category)).DistinctBy(v => v is null ? "absent" : Documents.Json(v)).ToList();
            var alone = drawn.Select(v => Documents.Json(RenderingDocumentSampler.Document([category, id], [v, new J.Str("")]))).ToList();
            TestContext.Current.TestOutputHelper?.WriteLine($"{apart.Count} documents apart, {drawn.Count} values of category drawn");

            drawn.Count.Should().BeGreaterThan(10);
            apart.Should().Contain(alone).And.Contain("{\"category\":\"\",\"id\":\"\"}").And.OnlyHaveUniqueItems();
            RenderingDocumentSampler.Apart(claim).Where(d => imports.Any(i => PathCellTests.Admitted((i.Key, i.Value.Values, i.Value.Chars), CosmosEval.At(d, i.Key)) == false))
                .Select(Documents.Json).Should().BeEmpty("every document is one each import admits");
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
                if (first is null && again is null && HoldsNoRawValue(w.Mine!, statement.AccessorsOf(w.Conjunct)))
                    continue;
                var loading = new CosmosPredicateLoader.Loading(statement.BindingForCheck.Fields, statement.BindingForCheck.Readings, CosmosImplementor.DefaultRootAlias);
                if (first is null || again is null || first.Equals(again) == false || first.toString() != again.toString()
                    || new HashSet<RexNode> { first }.Contains(again) == false || loading.Read(first).Digest != loading.Read(again).Digest)
                    parted.Add($"{w.Input} {w.Name}/{w.Statement}/{w.Conjunct} {w.Mine}: {first} and {again}");
            }

            parted.Should().BeEmpty();
        }

        /// <summary>
        /// Whether a weakening reads a path no node names the raw value of, over an input holding no document column: read
        /// through a field as text alone, which no cast to <c>ANY</c> names (<see cref="RenderingRexBuilder.RawValue"/>).
        /// </summary>
        static bool HoldsNoRawValue(ServiceForm form, IReadOnlyDictionary<string, RenderingRexBuilder.Accessor> accessors) =>
            form.Paths.Any(p => accessors.GetValueOrDefault(p) is { Raw: null, Retyped: null });

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
