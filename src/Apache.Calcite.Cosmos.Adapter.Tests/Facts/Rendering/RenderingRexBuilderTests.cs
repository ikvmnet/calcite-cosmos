using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
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
    /// the check run's pass would push, is written as a Rex the translator writes back as the form: each comparison as it
    /// stands, but where the translator writes its null guard (a <c>!=</c> at <c>Pos</c>, any comparison at <c>Neg</c>),
    /// every other member, grouping and order the form's own. S proves each comparison's text <c>Equal</c> to the loader's
    /// reading of its Rex, and the member no tighter; and what a shape's Rex is written as implied by its atom wherever S
    /// holds the shape implied, so the guard costs no row the atom keeps and, through the connectives, none a conjunct
    /// keeps. A form outside the grammar builds nothing; one form builds one Rex; a weakening pushed is applied beneath.
    /// </summary>
    public sealed class RenderingRexBuilderTests
    {

        /// <summary>A builder: a form's Rex, or none.</summary>
        internal delegate RexNode? Builder(ServiceForm form);

        /// <summary>Claims a form for an IR at a position, answering the verdict under the set and import a round trip is held under.</summary>
        internal delegate RenderingVerdict Claimer(ServiceForm form, SqlPredicate ir, RenderPosition pos, Verdict claim);

        // ── The round trip ─────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// What one round trip found wrong, each a line, and the form the Rex's text reads back as, for a form standing at a
        /// position. The text must be the form with each comparison as the translator writes its own Rex where it stands.
        /// A comparison's text is <c>Equal</c> to the loader's reading of its Rex, so what is sent is what Calcite reads;
        /// but a comparison whose Rex is one of the conjunct's own atoms, which Calcite reads as the conjunct does and S
        /// holds to the conjunct directly. Where its text is not the comparison it is the translator's guard, at a
        /// <c>!=</c> at <c>Pos</c> or a comparison at <c>Neg</c>, and the comparison implies the Rex. A type test is
        /// itself, <c>IS_DEFINED</c> <c>Equal</c> to its reading, every other with an in-process body.
        /// </summary>
        internal static (List<string> Problems, ServiceForm? Written, int Own, int Guarded) RoundTrip(ServiceForm form, RenderPosition at, Builder build,
            Func<CosmosParameterList, CosmosRexTranslator> translator, CosmosPredicateLoader.Loading loading, Claimer claim, Func<SqlPredicate, bool> own)
        {
            var (problems, owned, guarded, unwritten) = (new List<string>(), 0, 0, false);
            if (build(form) is not { } rex)
                return ([$"not built: {form}"], null, 0, 0);

            // A CASE, which only S reads (spec h09): the loader's reading of the whole Rex, what Calcite computes, is held
            // Equal to the form, what S writes to the service from h15, each arm beneath IS_NUMBER(p) reading p a number.
            if (IsCased(form))
            {
                var read = loading.Read(rex);
                if (read.Subterms.Any(t => t is SqlPredicate.Opaque))
                    return ([$"{form} read as {read}"], null, 0, 0);

                // Unproven with no form found is S declining the filter beneath, which h15's site asks before it pushes.
                return claim(form, read, at, Verdict.Equal) switch
                {
                    { Kind: RenderingVerdictKind.Proven } => (problems, form, 0, 0),
                    { Kind: RenderingVerdictKind.Unproven, Reason: null } => (problems, null, 0, 0),
                    var answer => ([$"altered: {form} at {at} cased as {rex}, read {read}, not held Equal: {answer.Kind} {answer.Reason}"], null, 0, 0),
                };
            }

            // The translator's text of a part's Rex where it stands: beneath a NOT at Neg, which is then stripped.
            ServiceForm? Written(ServiceForm f, RenderPosition pos)
            {
                var parameters = new CosmosParameterList();
                if (build(pos == RenderPosition.Pos ? f : ServiceForm.NotOf(f)) is not { } node || translator(parameters).TryTranslate(node, out var text) == false)
                    return null;

                var read = CosmosSqlFormReader.Read(text!, parameters.Parameters).Form;
                return pos == RenderPosition.Pos ? read : read.Kind == ServiceFormKind.Not ? read.Members[0] : null;
            }

            void Claimed(ServiceForm f, SqlPredicate ir, RenderPosition pos, Verdict verdict, string whose)
            {
                if (claim(f, ir, pos, verdict) is { Kind: not RenderingVerdictKind.Proven } answer)
                    problems.Add($"{pos}: {whose} {f} claimed {verdict} for {ir}: {answer.Kind} {answer.Reason}");
            }

            ServiceForm? Expected(ServiceForm f, RenderPosition pos)
            {
                switch (f.Kind)
                {
                    case ServiceFormKind.Test:
                        if (Written(f, pos) is not { } test || test.Equals(f) == false)
                            problems.Add($"{f} at {pos} written as {Written(f, pos)}");
                        if (loading.Read(build(f)!) is var read && read is SqlPredicate.Opaque)
                        {
                            if (CosmosOperators.HasInProcessBody(ServiceForm.NameOf(f.Test)) == false)
                                problems.Add($"{f} has no in-process body");
                        }
                        else
                            Claimed(f, read, pos, Verdict.Equal, "the test");
                        return f;

                    case ServiceFormKind.Compare or ServiceFormKind.In:
                        var ir = loading.Read(build(f)!);
                        if (ir.Subterms.Any(t => t is SqlPredicate.Opaque))
                        {
                            problems.Add($"{f} read as {ir}");
                            return null;
                        }

                        var mine = own(ir);
                        owned += mine ? 1 : 0;
                        if (Written(f, pos) is not { } written)
                        {
                            if (mine)
                                unwritten = true;
                            else
                                problems.Add($"{f} at {pos} written by no translator");
                            return null;
                        }

                        if (mine == false)
                            Claimed(written, ir, pos, Verdict.Equal, "the translator's");
                        if (written.Text != f.Text)
                        {
                            guarded++;
                            if (f.Kind == ServiceFormKind.Compare && (f.Test == CosmosServiceTest.NotEqual) == (pos == RenderPosition.Neg))
                                problems.Add($"{f} at {pos}, where the translator writes no guard, written {written}");
                            Claimed(f, ir, pos, Verdict.Implies, "the member");
                        }

                        return written;

                    case ServiceFormKind.Constant:
                        return f;

                    case ServiceFormKind.Not:
                        return Expected(f.Members[0], pos == RenderPosition.Pos ? RenderPosition.Neg : RenderPosition.Pos) is { } operand ? ServiceForm.NotOf(operand) : null;

                    case ServiceFormKind.And or ServiceFormKind.Or:
                        var members = f.Members.Select(m => Expected(m, pos)).ToList();
                        return members.All(m => m is not null) ? ServiceForm.JunctionOf(f.Test, members!) : null;

                    default:
                        problems.Add($"{f} is outside the grammar");
                        return null;
                }
            }

            // A form that is the conjunct's own comparison and nothing else, which the translator does not write, is the
            // conjunct itself: never pushed beneath itself, and admitted or left whole.
            var expected = Expected(form, at);
            var whole = Written(form, at);
            if (whole is null && (unwritten == false || form.Kind is not (ServiceFormKind.Compare or ServiceFormKind.In)))
                problems.Add($"not translated: {form} as {build(form)}");
            else if (whole is not null && expected is not null && whole.Equals(expected) == false)
                problems.Add($"altered: {form} written {whole}, its parts {expected}");

            return (problems, whole, owned, guarded);
        }

        /// <summary>
        /// Whether the builder writes a form with a <c>CASE</c>: a ternary, or a comparison a guarded pair holds over a reader
        /// that parses a string (<see cref="RenderingRexGrammar.Guarded"/>), which the translator writes no text of.
        /// </summary>
        internal static bool IsCased(ServiceForm form) => form.Subforms.Any(f => f.Kind == ServiceFormKind.Choose
            || f.Kind is ServiceFormKind.And or ServiceFormKind.Or && RenderingRexGrammar.GuardsOf(f) is var guards && f.Members.Any(m => m.Kind == ServiceFormKind.Compare
                && guards.Contains(m.Path!) && m.Reader is { } r && RenderingRexGrammar.ClassOf(r) == r && RenderingRexGrammar.Written.IsGuarded(r, m.Literal!)));

        /// <summary>A form's text with each junction's members in order of their own.</summary>
        static string Unordered(ServiceForm form) => form.Kind switch
        {
            ServiceFormKind.And or ServiceFormKind.Or => form.Test + "(" + string.Join(", ", form.Members.Select(Unordered).Order(StringComparer.Ordinal)) + ")",
            ServiceFormKind.Not => "NOT " + Unordered(form.Members[0]),
            _ => form.Text,
        };

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

            foreach (var beneath in bundle.Atoms.OfType<SqlPredicate.Beneath>().ToList())
                if (beneath.Leaf.Paths.ToList() is [var leaf] && bundle.Strata[beneath.Stratum].Any(c => c is SqlPredicate.TypeTest { Test: "IS_NUMBER" } t && t.Paths.SequenceEqual([leaf])))
                    bundle.Assume(ProbeRows.Source with { Stratum = beneath.Stratum }, beneath, PathValues.Meet(Numbers, imported[leaf]));

            // A CASE is read by generate mode alone (spec h08): Equal where S's best exact form is the one claimed, a
            // junction's members in any order.
            if (verdict == Verdict.Equal && root.Subterms.Any(t => t is SqlPredicate.Case))
            {
                bundle.Generate(term);
                bundle.RunGenerate(RenderingBundle.WidthOf([root]));
                var answer = bundle.ReadBest(term);
                return answer is { Kind: RenderingVerdictKind.Proven, Best: { } best } && Unordered(best) != Unordered(claimed)
                    ? new(RenderingVerdictKind.Unproven, null, null, $"S's best exact form is {best}") : new(answer.Kind, answer.Proof, null, answer.Reason);
            }

            bundle.Claim(term, claimed);
            bundle.Run(RenderingCheckRun.CheckLimit([claimed]));
            return bundle.Read(term, claimed, verdict);
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
        /// Every shape over every atom under each asserted set, in S defined with every template (<see cref="ProbeRows.Switched"/>)
        /// unless given: each template's form at each position S proposes it, and
        /// each S holds implied, presence among them, over a path that may hold anything, its characters any but U+0000
        /// under <c>Emulator</c>, whose order parts from Calcite's there (<c>TODO.md</c>); an accessor's atoms in one bundle.
        /// A form outside the grammar builds nothing; every other builds; one S holds implied round trips, and is written as
        /// one its atom implies.
        /// </summary>
        internal static (int Shapes, int Exact, int Refused, int Implied, int Own, int Cased, List<string> Declined, List<string> Problems) Shapes(Func<Builder, Builder>? plant = null, bool first = false,
            RenderingTheory? theory = null)
        {
            theory ??= ProbeRows.SwitchedTheory;
            var loading = new CosmosPredicateLoader.Loading(Fields, Readings, CosmosImplementor.DefaultRootAlias);
            var (shapes, exact, refused, implied, own, cased, declined, problems) = (0, 0, 0, 0, 0, 0, new List<string>(), new List<string>());

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

                            // A shape S cannot hold is written all the same, the builder being total over the grammar; one it
                            // holds, as it may push it, round trips. Written as itself it is the form S holds; under the
                            // translator's guard, S is asked of what is written.
                            if (held.Contains((form, pos)) == false)
                            {
                                if (build(form) is null)
                                    problems.Add($"{shown}: not built");
                                continue;
                            }

                            implied++;
                            var (found, written, mine, _) = RoundTrip(form, pos, build, p => new CosmosRexTranslator(Rex, Fields, p, JsonFactSet.Empty, null, null, Readings), loading,
                                (f, ir, at, verdict) => Claimed(f, ir, at, verdict, set, chars), ir => ir.Digest == atom.Digest);
                            (own, exact, cased) = (own + mine, exact + (form.Text == written?.Text ? 1 : 0), cased + (IsCased(form) ? 1 : 0));
                            if (IsCased(form) && written is null && found.Count == 0)
                                (DeclinedByTheCut(form) ? declined : found).Add(DeclinedByTheCut(form) ? shown : "declined beneath, short of the cut");
                            if (written is not null && written.Text != form.Text && Claim(written, atom, pos, Verdict.Implies, set, chars: chars) is { Kind: not RenderingVerdictKind.Proven } answer)
                                found.Add($"written {written}, which its atom does not imply: {answer.Kind} {answer.Reason}");

                            problems.AddRange(found.Select(p => $"{shown}: {p}"));
                        }
                    }
                }

            return (shapes, exact, refused, implied, own, cased, declined, problems);
        }

        /// <summary>
        /// Exhaustive over the grammar, not the corpus: every shape a template proposes, or S holds implied, for every atom
        /// the loader reads over every reader, literal class and position, and presence, under each asserted set; the
        /// guarded comparison's among them (spec h09), each <c>CASE</c> read back by the loader and proven by S.
        /// </summary>
        [Fact]
        public void EveryWritableShapeRoundTrips()
        {
            GC.KeepAlive(ProbeRows.SwitchedTheory);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var (shapes, exact, refused, implied, own, cased, declined, problems) = Shapes();
            TestContext.Current.TestOutputHelper?.WriteLine($"{shapes} shapes: {refused} outside the grammar, built as nothing; {shapes - refused} written, {implied} of them S holds "
                + $"implied, round tripped and written as one their atom implies: {exact} as themselves, {cased} with a CASE ({declined.Count} of them declined beneath), the rest "
                + $"under the translator's guard; {own} comparisons the atom's own; {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s with S defined");
            foreach (var shape in declined)
                TestContext.Current.TestOutputHelper?.WriteLine($"declined beneath: {shape}");
            foreach (var problem in problems)
                TestContext.Current.TestOutputHelper?.WriteLine(problem);

            (shapes - refused).Should().BeGreaterThan(500);
            refused.Should().BeGreaterThan(0, "a number over a cast, RETURNING DECIMAL or a raw value is outside the grammar bare");
            cased.Should().BeGreaterThan(declined.Count, "a number over a cast, RETURNING DECIMAL or the text is written as its guarded pair");
            problems.Should().BeEmpty();
        }

        // ── Every weakening the check run's pass would push ────────────────────────────────────────────────────────────

        /// <summary>
        /// S's weakenings of the pull request's run under the asserted sets, or of the run over <see cref="ProbeRows.Switched"/>,
        /// once a statement, conjunct, form and set.
        /// </summary>
        internal static IReadOnlyList<RenderingGenerateRun.Weakened> Weakenings(bool switched = false) =>
            [.. (switched ? RenderingCheckRun.Switched : RenderingCheckRun.PerPullRequest).Value.Generated!.Weakenings.Where(w => w.Mine is not null && w.Source is not null && RenderingGenerateRun.AssertedSets.Contains(w.Set))
                .DistinctBy(w => (w.Source, w.Conjunct, w.Mine!.Text, w.Set))];

        /// <summary>
        /// Every weakening S gives a residual over the pull request's inputs, built over its conjunct's own accessors and
        /// held to the round trip under its set, at the import of the bundle it was read from, a string's characters as
        /// <see cref="Ordered"/> keeps them.
        /// </summary>
        internal static (int Weakenings, int Exact, int Own, int Guarded, int Cased, List<string> Declined, List<string> Problems) Pushed(Func<Builder, Builder>? plant = null, bool first = false,
            bool switched = false)
        {
            var (weakenings, exact, own, guarded, cased, declined, problems) = (Weakenings(switched), 0, 0, 0, 0, new List<string>(), new List<string>());
            foreach (var w in weakenings.TakeWhile(_ => first == false || problems.Count == 0))
            {
                var statement = w.Source!;
                var accessors = statement.AccessorsOf(w.Conjunct);
                var loading = new CosmosPredicateLoader.Loading(statement.BindingForCheck.Fields, statement.BindingForCheck.Readings, CosmosImplementor.DefaultRootAlias);
                Builder built = form => RenderingRexBuilder.Build(form, statement.RexBuilderForCheck, accessors);
                var atoms = statement.RenderingStatement.Conjuncts[w.Conjunct].Subterms.Select(t => t.Digest).ToHashSet(StringComparer.Ordinal);
                var (found, written, mine, guards) = RoundTrip(w.Mine!, RenderPosition.Pos, plant?.Invoke(built) ?? built, p => statement.TranslatorFor(w.Conjunct, p), loading,
                    (f, ir, pos, verdict) => Claim(f, ir, pos, verdict, w.Set, w.Claim?.Imports, Ordered(w.Set)), ir => atoms.Contains(ir.Digest));
                (own, guarded) = (own + mine, guarded + guards);
                (exact, cased) = (exact + (w.Mine!.Text == written?.Text ? 1 : 0), cased + (IsCased(w.Mine!) ? 1 : 0));
                if (IsCased(w.Mine!) && written is null && found.Count == 0)
                    (DeclinedByTheCut(w.Mine!) ? declined : found).Add(DeclinedByTheCut(w.Mine!) ? $"{w.Set} {w.Input} {w.Name}/{w.Statement}/{w.Conjunct} {w.Mine}" : "declined beneath, short of the cut");
                problems.AddRange(found.Select(p => $"{w.Set} {w.Input} {w.Name}/{w.Statement}/{w.Conjunct} {w.Mine}: {p}"));
            }

            return (weakenings.Count, exact, own, guarded, cased, declined, problems);
        }

        /// <summary>
        /// Every weakening S gives over the pull request's inputs round trips, riding the check run's pass; and over the
        /// same inputs in S defined with every template (spec h09), the guarded comparison's among them, each <c>CASE</c>
        /// read back by the loader and proven by S.
        /// </summary>
        [Fact]
        public void EveryPushedWeakeningRoundTrips()
        {
            foreach (var switched in new[] { false, true })
            {
                var (weakenings, exact, own, guarded, cased, declined, problems) = Pushed(switched: switched);
                TestContext.Current.TestOutputHelper?.WriteLine($"{(switched ? "every template" : "production")}: {weakenings} weakenings: {exact} written as themselves, {cased} with a CASE "
                    + $"({declined.Count} of them declined beneath), the rest with {guarded} comparisons under the translator's guard; {own} comparisons the conjunct's own");
                foreach (var weakening in declined)
                    TestContext.Current.TestOutputHelper?.WriteLine($"declined beneath: {weakening}");
                foreach (var problem in problems)
                    TestContext.Current.TestOutputHelper?.WriteLine(problem);

                weakenings.Should().BeGreaterThan(300);
                problems.Should().BeEmpty();
                if (switched)
                    cased.Should().BeGreaterThan(declined.Count, "the corpus compares numbers through casts of the text and the text with numerals");
                else
                    cased.Should().Be(0, "production asks the guarded comparison's templates only from the switch");
            }
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
        /// the loop guard (<c>IsApplied</c>, <c>AlreadyApplied</c>) finds a weakening beneath by identity.
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
                var statement = w.Source!;
                var rex = RenderingRexBuilder.Build(w.Mine!, statement.RexBuilderForCheck, statement.AccessorsOf(w.Conjunct))!;
                if (statement.IsApplied(rex, []))
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
