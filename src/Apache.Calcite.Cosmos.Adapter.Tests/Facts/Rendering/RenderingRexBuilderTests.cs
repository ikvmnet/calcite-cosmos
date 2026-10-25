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
            if (build(form) is null)
                return ([$"not built: {form}"], null, 0, 0);

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
                        if (written.Equals(f) == false)
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
        /// A form claimed for an IR at a position under a set, each path either side reads imported as given, or as
        /// anything, its characters kept to those given; at <c>Neg</c>, the negation of each at <c>Pos</c>.
        /// </summary>
        internal static RenderingVerdict Claim(ServiceForm form, SqlPredicate ir, RenderPosition pos, Verdict verdict, RenderingSet set,
            IReadOnlyDictionary<string, RenderingMemo.Imported>? imports = null, ValueSet<string>? chars = null)
        {
            var (claimed, root) = pos == RenderPosition.Pos ? (form, ir) : (ServiceForm.Negated(form), (SqlPredicate)new SqlPredicate.Not(ir));
            using var bundle = ProbeRows.ProbeTheory.Open(set);
            var term = bundle.Write(root);
            foreach (var path in root.Paths.Concat(form.Paths.Select(CosmosEval.PathOf)).Distinct())
                if (imports?.GetValueOrDefault(SqlValue.Spell(path)) is { } import)
                    bundle.Import(ProbeRows.Source, path, import.Values, chars is null ? import.Chars : ValueSet.From(import.Chars.Where(chars.Contains)));
                else
                    bundle.Import(ProbeRows.Source, path, PathValues.Anything, chars ?? JsonValues.AllBands);

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

        static readonly JavaTypeFactoryImpl Types = new();

        static readonly RexBuilder Rex = new(Types);

        static readonly CosmosPath Document = CosmosPath.Root(CosmosImplementor.DefaultRootAlias);

        /// <summary>The document, and a column of each kind a view binds: <c>VARIANT</c>, text, an <c>INTEGER</c> and a raw string.</summary>
        static readonly IReadOnlyList<CosmosPath?> Fields = [Document, Document.Property("v"), Document.Property("t"), Document.Property("i"), Document.Property("r")];

        static readonly IReadOnlyList<CosmosReading> Readings = [CosmosReading.Typed, CosmosReading.Typed, CosmosReading.Text, CosmosReading.Typed, CosmosReading.Typed];

        static org.apache.calcite.rel.type.RelDataType Nullable(SqlTypeName type) => Types.createTypeWithNullability(Types.createSqlType(type), true);

        static RexNode JsonValue(string path, SqlTypeName type)
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
        /// Calcite renders a boolean as with <c>=</c>, which alone a template reads apart (A5); a whole number with every
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
                Rex.makeIn(accessor, Java(Rex.makeLiteral("bikes"), Rex.makeLiteral("tools"))),
                Rex.makeIn(accessor, Java(Rex.makeExactLiteral(java.math.BigDecimal.valueOf(30)), Rex.makeExactLiteral(java.math.BigDecimal.valueOf(31)))),
                Rex.makeCall(SqlStdOperatorTable.IS_NULL, accessor), Rex.makeCall(CosmosOperators.IsDefined, accessor),
            ])
                .Select(conjunct => (Conjunct: conjunct, Atom: loading.Read(conjunct)))
                .Where(c => c.Atom.Subterms.Any(t => t is SqlPredicate.Opaque or SqlPredicate.And or SqlPredicate.Or or SqlPredicate.Not) == false)
                .DistinctBy(c => c.Atom.Digest)]);

        static java.util.List Java(params RexNode[] nodes)
        {
            var list = new java.util.ArrayList();
            foreach (var node in nodes)
                list.add(node);
            return list;
        }

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
        /// Every shape over every atom under each asserted set: each template's form at each position S proposes it, and
        /// each S holds implied, presence among them, over a path that may hold anything, its characters any but U+0000
        /// under <c>Emulator</c>, whose order parts from Calcite's there (<c>TODO.md</c>); an accessor's atoms in one bundle.
        /// A form outside the grammar builds nothing; every other builds; one S holds implied round trips, and is written as
        /// one its atom implies.
        /// </summary>
        internal static (int Shapes, int Exact, int Refused, int Implied, int Own, List<string> Problems) Shapes(Func<Builder, Builder>? plant = null, bool first = false)
        {
            var loading = new CosmosPredicateLoader.Loading(Fields, Readings, CosmosImplementor.DefaultRootAlias);
            var (shapes, exact, refused, implied, own, problems) = (0, 0, 0, 0, 0, new List<string>());

            // A part many shapes of one atom share, their core or a test, is claimed once a set.
            var claims = new Dictionary<(string, string, RenderPosition, Verdict, RenderingSet), RenderingVerdict>();
            RenderingVerdict Claimed(ServiceForm f, SqlPredicate ir, RenderPosition at, Verdict verdict, RenderingSet set, ValueSet<string> chars) =>
                claims.TryGetValue((f.Text, ir.Digest, at, verdict, set), out var known) ? known : claims[(f.Text, ir.Digest, at, verdict, set)] = Claim(f, ir, at, verdict, set, chars: chars);

            foreach (var atoms in Atoms(loading).TakeWhile(_ => first == false || problems.Count == 0))
                foreach (var set in RenderingGenerateRun.AssertedSets)
                {
                    var chars = Ordered(set);
                    using var bundle = ProbeRows.ProbeTheory.Open(set);
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
                            (own, exact) = (own + mine, exact + (form.Equals(written) ? 1 : 0));
                            if (written is not null && written.Equals(form) == false && Claim(written, atom, pos, Verdict.Implies, set, chars: chars) is { Kind: not RenderingVerdictKind.Proven } answer)
                                found.Add($"written {written}, which its atom does not imply: {answer.Kind} {answer.Reason}");

                            problems.AddRange(found.Select(p => $"{shown}: {p}"));
                        }
                    }
                }

            return (shapes, exact, refused, implied, own, problems);
        }

        /// <summary>
        /// Exhaustive over the grammar, not the corpus: every shape a template proposes, or S holds implied, for every atom
        /// the loader reads over every reader, literal class and position, and presence, under each asserted set.
        /// </summary>
        [Fact]
        public void EveryWritableShapeRoundTrips()
        {
            GC.KeepAlive(ProbeRows.ProbeTheory);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var (shapes, exact, refused, implied, own, problems) = Shapes();
            TestContext.Current.TestOutputHelper?.WriteLine($"{shapes} shapes: {refused} outside the grammar, built as nothing; {shapes - refused} written, {implied} of them S holds "
                + $"implied, round tripped and written as one their atom implies: {exact} as themselves, the rest under the translator's guard; {own} comparisons the atom's own; "
                + $"{System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s with S defined");
            foreach (var problem in problems)
                TestContext.Current.TestOutputHelper?.WriteLine(problem);

            (shapes - refused).Should().BeGreaterThan(500);
            refused.Should().BeGreaterThan(0, "a number over a cast, RETURNING DECIMAL or a raw value is outside the grammar");
            problems.Should().BeEmpty();
        }

        // ── Every weakening the check run's pass would push ────────────────────────────────────────────────────────────

        /// <summary>S's weakenings of the pull request's run under the asserted sets, once a statement, conjunct, form and set.</summary>
        internal static IReadOnlyList<RenderingGenerateRun.Weakened> Weakenings() =>
            [.. RenderingCheckRun.PerPullRequest.Value.Shadow!.Weakenings.Where(w => w.Mine is not null && w.Source is not null && RenderingGenerateRun.AssertedSets.Contains(w.Set))
                .DistinctBy(w => (w.Source, w.Conjunct, w.Mine!.Text, w.Set))];

        /// <summary>
        /// Every weakening S gives a residual over the pull request's inputs, built over its conjunct's own accessors and
        /// held to the round trip under its set, at the import of the bundle it was read from, a string's characters as
        /// <see cref="Ordered"/> keeps them.
        /// </summary>
        internal static (int Weakenings, int Exact, int Own, int Guarded, List<string> Problems) Pushed(Func<Builder, Builder>? plant = null, bool first = false)
        {
            var (weakenings, exact, own, guarded, problems) = (Weakenings(), 0, 0, 0, new List<string>());
            foreach (var w in weakenings.TakeWhile(_ => first == false || problems.Count == 0))
            {
                var statement = w.Source!;
                var accessors = statement.AccessorsOf(w.Conjunct);
                var loading = new CosmosPredicateLoader.Loading(statement.BindingForCheck.Fields, statement.BindingForCheck.Readings, CosmosImplementor.DefaultRootAlias);
                Builder built = form => RenderingRexBuilder.Build(form, statement.RexBuilderForCheck, accessors);
                var atoms = statement.Statement.Conjuncts[w.Conjunct].Subterms.Select(t => t.Digest).ToHashSet(StringComparer.Ordinal);
                var (found, written, mine, guards) = RoundTrip(w.Mine!, RenderPosition.Pos, plant?.Invoke(built) ?? built, p => statement.TranslatorFor(w.Conjunct, p), loading,
                    (f, ir, pos, verdict) => Claim(f, ir, pos, verdict, w.Set, w.Claim?.Imports, Ordered(w.Set)), ir => atoms.Contains(ir.Digest));
                (own, guarded) = (own + mine, guarded + guards);
                exact += w.Mine!.Equals(written) ? 1 : 0;
                problems.AddRange(found.Select(p => $"{w.Set} {w.Input} {w.Name}/{w.Statement}/{w.Conjunct} {w.Mine}: {p}"));
            }

            return (weakenings.Count, exact, own, guarded, problems);
        }

        /// <summary>Every weakening S gives over the pull request's inputs round trips, riding the check run's pass.</summary>
        [Fact]
        public void EveryPushedWeakeningRoundTrips()
        {
            var (weakenings, exact, own, guarded, problems) = Pushed();
            TestContext.Current.TestOutputHelper?.WriteLine($"{weakenings} weakenings: {exact} written as themselves, the rest with {guarded} comparisons under the translator's guard; "
                + $"{own} comparisons the conjunct's own");
            foreach (var problem in problems)
                TestContext.Current.TestOutputHelper?.WriteLine(problem);

            weakenings.Should().BeGreaterThan(300);
            problems.Should().BeEmpty();
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
                var beneath = CosmosFilterStatement.ConjunctsOf(RexUtil.composeConjunction(statement.RexBuilderForCheck, Java(rex)));
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
