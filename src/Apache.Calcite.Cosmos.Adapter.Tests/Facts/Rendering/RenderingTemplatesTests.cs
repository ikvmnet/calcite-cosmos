using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.OracleFixture;
using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.ProbeRows;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// Generate mode's values (spec g02): the templates spell a form for every pair they are asked of, or the inert
    /// sentinel, and the translator's own spelling where it writes one; a price orders totally; the weakening's assembly
    /// joins, flattens and folds as Kleene's identities say; and the primitives that hold what may be sent and what the Rex
    /// builder writes follow their rows.
    /// </summary>
    public sealed class RenderingTemplatesTests
    {

        const string Products = "SELECT c.\"id\" FROM products AS c WHERE ", Typed = "SELECT c.\"id\" FROM typed AS c WHERE ";

        const string Category = "c.\"$.category\"", Text = "JSON_VALUE(c.\"DOC\", '$.label')", Price = "JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER)";

        static readonly string Path = SqlValue.Spell(LabelPath);

        /// <summary>Statements whose one conjunct the translator writes, each with the template whose form its text is.</summary>
        static readonly (string Sql, TemplateId Template)[] Written =
        [
            (Products + Category + " = 'bikes'", TemplateId.Bare), (Products + Category + " <> 'bikes'", TemplateId.Restricted), (Products + Category + " < 'm'", TemplateId.Bare),
            (Products + $"NOT ({Category} = 'bikes')", TemplateId.Completed), (Products + $"NOT ({Category} <> 'bikes')", TemplateId.Bare),
            (Products + Category + " IN ('bikes', 'shoes')", TemplateId.Bare),
            (Products + Category + " IS NULL", TemplateId.NullTest), (Products + Category + " IS NOT NULL", TemplateId.NullTest),
            (Typed + Text + " = 'bikes'", TemplateId.Bare), (Typed + $"NOT ({Text} = 'bikes')", TemplateId.Completed),
            (Typed + Text + " IS NULL", TemplateId.NullTest), (Typed + Text + " IS NOT NULL", TemplateId.NullTest),
            (Typed + Price + " <> 30", TemplateId.Restricted), (Typed + Price + " > 30", TemplateId.Bare), (Typed + $"NOT ({Price} = 30)", TemplateId.Completed),
            (Typed + Price + " IS NULL", TemplateId.NullTest),
        ];

        /// <summary>
        /// Every template at both positions over every atom the inputs load, a synthetic atom of every shape and literal
        /// beside them, and a parameter atom, spells without throwing; and every pair the rows ask spells a form, not the
        /// sentinel, over a plain atom of the shape.
        /// </summary>
        [Fact]
        public void SpellIsTotal()
        {
            var loaded = AtomsOf(RenderingCheckRun.PerPullRequest.Value.Irs.Values);
            loaded.Should().NotBeEmpty();
            var atoms = loaded.Concat(Synthetic(plain: false).Select(a => (a, Path))).Append((PredicateAtom.CompareParameter(PredicateAtom.TextReader, "<>"), Path)).ToList();
            var thrown = new List<string>();
            foreach (var (atom, path) in atoms)
                foreach (var id in System.Enum.GetValues<TemplateId>())
                    foreach (var pos in System.Enum.GetValues<RenderPosition>())
                    {
                        try
                        {
                            RenderingTemplates.Spell(id, pos, atom, path);
                        }
                        catch (Exception e)
                        {
                            thrown.Add($"{id} at {pos} over {atom}: {e.Message}");
                        }
                    }

            thrown.Should().BeEmpty();

            var asked = from atom in Synthetic(plain: true) from fit in RenderingTemplateRows.Fits where fit.Shape == RenderingTemplates.ShapeOf(atom)
                        from row in RenderingTemplateRows.Templates where row.Item1 == fit.Item1 select (Id: fit.Item1, Pos: row.Item2, Atom: atom);
            asked.Should().NotBeEmpty();
            asked.Where(a => RenderingTemplates.Spell(a.Id, a.Pos, a.Atom, Path).Kind == ServiceFormKind.Opaque).Select(a => $"{a.Id} at {a.Pos} over {a.Atom}").Should().BeEmpty();
            TestContext.Current.TestOutputHelper?.WriteLine($"{loaded.Count} atoms loaded, {atoms.Count} in all; {asked.Count()} pairs asked of plain atoms; {RenderingTemplateRows.Fits.Count} fits rows");
        }

        /// <summary>A parameter atom has its own shape, which no row names, and every template spells the sentinel for it.</summary>
        [Fact]
        public void NoTemplateFitsAParameterAtom()
        {
            var parameters = Synthetic(plain: true).Select(a => a with { Operand = null, Points = null, Parameter = true }).ToList();
            parameters.Select(RenderingTemplates.ShapeOf).Should().OnlyContain(s => s == RenderingTemplates.ParameterShape);
            RenderingTemplateRows.Fits.Should().NotContain(f => f.Shape == RenderingTemplates.ParameterShape);
            (from atom in parameters from id in System.Enum.GetValues<TemplateId>() from pos in System.Enum.GetValues<RenderPosition>() select RenderingTemplates.Spell(id, pos, atom, Path))
                .Should().OnlyContain(f => f.Kind == ServiceFormKind.Opaque);
        }

        /// <summary>
        /// A1–A4 over single atoms are what the translator writes, beneath its <c>NOT</c> where an atom stands at <c>Neg</c>:
        /// each sample statement's one conjunct, written at admission and read back, is its template's form for the atom
        /// the loader reads; and so is every one-atom conjunct the check run's statements send but the respellings R5
        /// names, an <c>IN</c>'s points in the set's order and <c>NOT (x IS NULL)</c> in <c>IS NOT NULL</c>'s spelling.
        /// </summary>
        [Fact]
        public void EveryTemplateSpellsWhatTheTranslatorWrites()
        {
            foreach (var (sql, template) in Written)
            {
                var recorder = new RenderingRecorder();
                using (recorder.Arm())
                    RenderingInputs.Differential("templates", [sql], RenderingMutationTests.Containers).Single().Plan();

                var admitted = recorder.Records.Where(r => r.Site == 1).DistinctBy(r => r.Conjunct).ToList();
                admitted.Should().ContainSingle(sql);
                var (spelled, read) = Respelled(admitted[0].Statement.Statement.Conjuncts[admitted[0].Conjunct], admitted[0].Text, admitted[0].Parameters);
                spelled.Should().Be(read, $"{sql} wrote {admitted[0].Text}");
                Template(admitted[0].Statement.Statement.Conjuncts[admitted[0].Conjunct]).Should().Be(template, sql);
                GC.KeepAlive(recorder);
            }

            var run = RenderingCheckRun.PerPullRequest.Value;
            var sent = run.Conjuncts.Where(c => c.Site == 2 && c.Skip is null && Atom(run.Irs[c.Ir]) is not (SqlPredicate.And or SqlPredicate.Or or SqlPredicate.Not or SqlPredicate.Opaque))
                .DistinctBy(c => c.Ir + " " + c.Text).Select(c => (c.Input, c.Name, c.Text, Ir: run.Irs[c.Ir], Forms: Respelled(run.Irs[c.Ir], c.Text, c.Bound!))).ToList();
            var respelled = sent.Where(c => c.Forms.Spelled.Equals(c.Forms.Read) == false).ToList();
            TestContext.Current.TestOutputHelper?.WriteLine($"{sent.Count} one-atom conjuncts sent, {respelled.Count} respelled: " + string.Join("; ", respelled.Select(c => $"{c.Input} {c.Name} {c.Text}")));
            sent.Should().HaveCountGreaterThan(100);
            respelled.Where(c => Atom(c.Ir) is SqlPredicate.In
                    ? c.Forms.Read.Members.ToHashSet().SetEquals(c.Forms.Spelled.Members) == false
                    : c.Ir is not SqlPredicate.Not { Operand: SqlPredicate.IsNull } || c.Forms.Read.Equals(Spelled(c.Ir, TemplateId.NullTest, RenderPosition.Pos)) == false)
                .Select(c => $"{c.Input} {c.Name}: {c.Text} read {c.Forms.Read}, spelled {c.Forms.Spelled}").Should().BeEmpty();
        }

        /// <summary>A conjunct's atom: itself, or beneath one <c>NOT</c>.</summary>
        static SqlPredicate Atom(SqlPredicate conjunct) => conjunct is SqlPredicate.Not not ? not.Operand : conjunct;

        /// <summary>The template the translator writes an atom's conjunct by: the inequality and the null test by their own, the rest bare at <c>Pos</c> and completed at <c>Neg</c>.</summary>
        static TemplateId Template(SqlPredicate conjunct) => (conjunct is SqlPredicate.Not, Atom(conjunct)) switch
        {
            (_, SqlPredicate.IsNull) => TemplateId.NullTest,
            (false, SqlPredicate.Compare { Op: PredicateAtom.NotEqual }) => TemplateId.Restricted,
            (true, SqlPredicate.Compare { Op: not PredicateAtom.NotEqual } or SqlPredicate.In) => TemplateId.Completed,
            _ => TemplateId.Bare,
        };

        static ServiceForm Spelled(SqlPredicate conjunct, TemplateId template, RenderPosition pos)
        {
            var (atom, path) = AtomsOf([Atom(conjunct)]).Single();
            return RenderingTemplates.Spell(template, pos, atom, path);
        }

        /// <summary>What the template spells for a conjunct's atom at its position, beside the text read back, beneath its <c>NOT</c> at <c>Neg</c>.</summary>
        static (ServiceForm Spelled, ServiceForm Read) Respelled(SqlPredicate conjunct, string text, IReadOnlyList<Adapter.Sql.CosmosParameter> parameters)
        {
            var (pos, read) = (conjunct is SqlPredicate.Not ? RenderPosition.Neg : RenderPosition.Pos, CosmosSqlFormReader.Read(text, parameters).Form);
            return (Spelled(conjunct, Template(conjunct), pos), pos == RenderPosition.Neg ? ServiceForm.Strip(read) : read);
        }

        /// <summary>
        /// No template writes two string orderings of one path: the pair <c>parted</c> rule reads only a claim's
        /// <c>conjunct</c> rows, so a template that wrote a range would never be held to <c>Range</c>.
        /// </summary>
        [Fact]
        public void NoTemplateWritesTwoOrderingsOfOnePath() => (from atom in Synthetic(plain: false) from id in System.Enum.GetValues<TemplateId>()
                from pos in System.Enum.GetValues<RenderPosition>() select RenderingTemplates.Spell(id, pos, atom, Path))
            .Where(f => f.Subforms.Count(g => RenderingPrimitives.StringOrdering.Function(g)) > 1).Select(f => f.Text).Should().BeEmpty();

        /// <summary>
        /// A price orders by nodes, then text, a total order agreeing with equality, every real price below
        /// <see cref="Priced.None"/>; a form's nodes count its leaves, its negations and its connectives.
        /// </summary>
        [Fact]
        public void PricedIsATotalOrder()
        {
            var (a, b) = (ServiceForm.CompareOf(CosmosServiceTest.Equal, Path, JsonValues.Of("a")), ServiceForm.TestOf(CosmosServiceTest.IsDefined, Path));
            var forms = new[] { a, b, ServiceForm.NotOf(b), All(b, a), Any(b, ServiceForm.NotOf(a)), All(b, Any(a, b), ServiceForm.NotOf(a)), ServiceForm.ConstantOf(true) };
            forms.Select(f => f.Nodes).Should().Equal(1, 1, 2, 3, 4, 8, 1);

            var prices = forms.Select(Priced.Of).Append(Priced.None).Append(new Priced(9, a)).Append(Priced.None with { }).ToList();
            foreach (var p in prices)
                foreach (var q in prices)
                {
                    (p.CompareTo(q) == 0).Should().Be(p.Equals(q), $"{p} against {q}");
                    Math.Sign(p.CompareTo(q)).Should().Be(-Math.Sign(q.CompareTo(p)));
                    foreach (var r in prices.Where(r => p.CompareTo(q) <= 0 && q.CompareTo(r) <= 0))
                        p.CompareTo(r).Should().BeLessThanOrEqualTo(0, $"{p} ≤ {q} ≤ {r}");
                }

            prices.Where(p => p.Form is not null).Should().OnlyContain(p => p.CompareTo(Priced.None) < 0);
            prices.Order().Select(p => p.Form?.Text).Should().Equal(a.Text, b.Text, "true", "(NOT " + b.Text + ")", All(b, a).Text, Any(b, ServiceForm.NotOf(a)).Text, forms[5].Text, a.Text, null, null);
            (RenderingPrimitives.PricedIsReal.Function(Priced.None), RenderingPrimitives.PricedIsReal.Function(Priced.Of(a))).Should().Be((false, true));
            Priced.FormOf(Priced.None).Kind.Should().Be(ServiceFormKind.Opaque);
            Priced.FormOf(Priced.Of(a)).Should().Be(a);

            var implied = ValueSet.Of(a, ServiceForm.NotOf(b));
            foreach (var p in prices)
                (RenderingPrimitives.BestIsImplied.Function(p, implied), RenderingPrimitives.BestIsNotImplied.Function(p, implied)).Should().Be(
                    (p.Form is { } f && implied.Contains(f), p.Form is not { } g || implied.Contains(g) == false), p.ToString());
        }

        /// <summary>
        /// A join flattens a member of its connective, keeps one of each part in the arms' order, and folds Kleene's
        /// constants at either position; its parts are the arms'; and a leaf's fold joins its implied set in order.
        /// </summary>
        [Fact]
        public void JoinedFlattensKeepsOnePerPartAndFoldsConstants()
        {
            var (a, b, c) = (ServiceForm.TestOf(CosmosServiceTest.IsDefined, Path), ServiceForm.CompareOf(CosmosServiceTest.NotEqual, Path, JsonValues.Of("x")), ServiceForm.TestOf(CosmosServiceTest.IsString, Path));
            var (yes, no) = (ServiceForm.ConstantOf(true), ServiceForm.ConstantOf(false));
            var (and, or) = (CosmosServiceTest.And, CosmosServiceTest.Or);

            foreach (var (connective, l, m, joined) in new[]
            {
                (and, All(a, b), All(b, c), All(a, b, c)), (and, a, Any(b, c), All(a, Any(b, c))), (or, Any(c, a), All(a, b), Any(c, a, All(a, b))),
                (and, b, b, b), (and, All(a, b), a, All(a, b)), (or, Any(a, Any(b, c)), c, Any(a, b, c)),
                (and, yes, a, a), (and, a, no, no), (or, no, a, a), (or, a, yes, yes), (and, yes, yes, yes), (or, no, no, no), (and, no, yes, no),
            })
            {
                ServiceForm.Joined(connective, l, m).Should().Be(joined, $"{l} {connective} {m}");
                if (joined.Kind != ServiceFormKind.Constant && l.Kind != ServiceFormKind.Constant && m.Kind != ServiceFormKind.Constant)
                    RenderingPrimitives.FormIsJoinOf.Function(joined, l, m).Should().BeTrue($"{joined} joins {l} and {m}");
            }

            ServiceForm.Joined(CosmosServiceTest.Not, a, b).Kind.Should().Be(ServiceFormKind.Opaque, "only a junction joins");
            RenderingPrimitives.FormIsJoinOf.Function(All(a, b), a, c).Should().BeFalse();
            RenderingPrimitives.FormIsJoinOf.Function(All(a, b, c), All(a, b), a).Should().BeFalse();
            RenderingPrimitives.FormIsJoinOf.Function(Any(a, b), All(a, b), a).Should().BeFalse("an AND is no part of an OR's flattening");

            var implied = ValueSet.Of(b, a, All(a, ServiceForm.NotOf(ServiceForm.TestOf(CosmosServiceTest.IsNull, Path)), b));
            ServiceForm.Folded(implied, RenderPosition.Pos).Should().Be(ServiceForm.Joined(and, ServiceForm.Joined(and, implied[0], implied[1]), implied[2]));
            ServiceForm.Folded(implied, RenderPosition.Neg).Should().Be(Any([.. implied]));
            (ServiceForm.Folded(ValueSet<ServiceForm>.Empty, RenderPosition.Pos), ServiceForm.Folded(ValueSet<ServiceForm>.Empty, RenderPosition.Neg)).Should().Be((yes, no));
            ServiceForm.Folded(ValueSet.Of(a), RenderPosition.Neg).Should().Be(a);
        }

        /// <summary>Negating folds both constants and strips one <c>NOT</c>; presence is <c>IS_DEFINED</c>, beneath a <c>NOT</c> its negation.</summary>
        [Fact]
        public void NegatedFoldsBothConstants()
        {
            var a = ServiceForm.TestOf(CosmosServiceTest.IsDefined, Path);
            (ServiceForm.Negated(ServiceForm.ConstantOf(true)), ServiceForm.Negated(ServiceForm.ConstantOf(false))).Should().Be((ServiceForm.ConstantOf(false), ServiceForm.ConstantOf(true)));
            (ServiceForm.Negated(a), ServiceForm.Negated(ServiceForm.NotOf(a))).Should().Be((ServiceForm.NotOf(a), a));
            (ServiceForm.Presence(Path, RenderPosition.Pos), ServiceForm.Presence(Path, RenderPosition.Neg)).Should().Be((a, ServiceForm.NotOf(a)));
        }

        /// <summary>
        /// A literal with an unpaired surrogate is never sendable, at any depth, as <c>CosmosEncodable</c> says of it; a
        /// string ordering whose literal holds a supplementary or a high-band character is not either, and is beside an
        /// equality, which parts no range.
        /// </summary>
        [Fact]
        public void ALoneSurrogateLiteralIsNotSendable()
        {
            var bands = ValueSet.From(Measured.Parting.SelectMany(p => new[] { p.Item1, p.Item2 }));
            bool Sendable(CosmosServiceTest op, string literal, bool nested = false) =>
                RenderingPrimitives.Sendable.Function(nested ? Any(ServiceForm.TestOf(CosmosServiceTest.IsNull, Path), ServiceForm.CompareOf(op, Path, JsonValues.Of(literal))) : ServiceForm.CompareOf(op, Path, JsonValues.Of(literal)), bands);

            foreach (var text in new[] { "\uD800", "a\uDC00", "\uDE00\uD83D", "x\uD83D" })
            {
                (JsonValues.IsEncodable(text), CosmosEncodable.IsEncodable(text)).Should().Be((false, false));
                (Sendable(CosmosServiceTest.Equal, text), Sendable(CosmosServiceTest.Equal, text, nested: true)).Should().Be((false, false), text);
                RenderingPrimitives.Sendable.Function(ServiceForm.InOf(Path, ValueSet.Of(JsonValues.Of("ok"), JsonValues.Of(text))), bands).Should().BeFalse();
            }

            foreach (var text in new[] { "", "bikes", "a😀b", "", "é" })
                (JsonValues.IsEncodable(text), CosmosEncodable.IsEncodable(text), Sendable(CosmosServiceTest.Equal, text)).Should().Be((true, true, true), text);

            (Sendable(CosmosServiceTest.Less, "a😀b"), Sendable(CosmosServiceTest.GreaterOrEqual, "", nested: true), Sendable(CosmosServiceTest.Greater, "ébikes"))
                .Should().Be((false, false, true), "a supplementary or high-band bound may part the orders; one of U+0080 to U+D7FF does not");
            RenderingPrimitives.Sendable.Function(ServiceForm.CompareOf(CosmosServiceTest.Less, Path, JsonValues.Of(30m)), bands).Should().BeTrue();
        }

        /// <summary>
        /// The Rex builder writes a string comparison over any reader, a number over a <c>RETURNING</c> of a number type,
        /// never over a cast of the text nor <c>RETURNING DECIMAL</c>, which parse a string the service compares as none, a
        /// boolean over <c>RETURNING BOOLEAN</c> alone, every type test, constants and the connectives over them; nothing else.
        /// </summary>
        [Fact]
        public void RexWritableFollowsTheGrammar()
        {
            bool Writes(ServiceForm form, string reader) => RenderingPrimitives.RexWritable.Function(form, PredicateAtom.IsNull(reader), RenderingRexGrammar.Written);
            ServiceForm Compare(string literal) => ServiceForm.CompareOf(CosmosServiceTest.Greater, Path, literal);
            var (text, raw, integer, boolean, cast) = (PredicateAtom.TextReader, PredicateAtom.RawReader, PredicateAtom.TypedReader("INTEGER"), PredicateAtom.TypedReader("BOOLEAN"),
                PredicateAtom.CastReader("DOUBLE", PredicateAtom.TextReader));

            var (s, n, f, t) = (JsonValues.Of("x"), JsonValues.Of(30m), JsonValues.Of(2.5m), JsonValues.Of(true));
            var cases = new (ServiceForm Form, string Reader, bool Writes)[]
            {
                (Compare(s), text, true), (Compare(s), raw, true), (Compare(s), integer, true), (Compare(s), cast, true),
                (Compare(n), integer, true), (Compare(f), PredicateAtom.TypedReader("DOUBLE"), true), (Compare(f), cast, false), (Compare(n), PredicateAtom.TypedReader("DECIMAL"), false),
                (Compare(n), PredicateAtom.CastReader("INTEGER", PredicateAtom.TextReader), false),
                (Compare(n), text, false), (Compare(n), raw, false), (Compare(n), boolean, false), (Compare(n), PredicateAtom.CastReader("VARCHAR", raw), false),
                (Compare(t), boolean, true), (Compare(t), raw, false), (Compare(t), text, false), (Compare(JsonValues.NullValue), raw, false),
                (ServiceForm.InOf(Path, ValueSet.Of(s, JsonValues.Of("y"))), raw, true), (ServiceForm.InOf(Path, ValueSet.Of(s, n)), raw, false), (ServiceForm.InOf(Path, ValueSet.Of(s, n)), integer, true),
                (ServiceForm.ConstantOf(false), raw, true), (ServiceForm.BareOf(Path), boolean, false), (ServiceForm.OpaqueOf("IS_STRING(c.x)", ValueSet.Of(Path)), raw, false),
                (All(ServiceForm.TestOf(CosmosServiceTest.IsNumber, Path), Any(ServiceForm.NotOf(Compare(n)), ServiceForm.ConstantOf(true))), integer, true),
                (All(ServiceForm.TestOf(CosmosServiceTest.IsNumber, Path), Any(ServiceForm.NotOf(Compare(n)), ServiceForm.ConstantOf(true))), cast, false),
                (All(ServiceForm.TestOf(CosmosServiceTest.IsNumber, Path), Any(ServiceForm.NotOf(Compare(n)), ServiceForm.BareOf(Path))), integer, false),
            };

            cases.Select(c => (c.Form.Text, c.Reader, Writes(c.Form, c.Reader))).Should().Equal(cases.Select(c => (c.Form.Text, c.Reader, c.Writes)));
            RenderingRexGrammar.Written.Tests.Should().BeEquivalentTo(System.Enum.GetValues<CosmosServiceTest>().Where(test => test is >= CosmosServiceTest.IsDefined and <= CosmosServiceTest.IsArray),
                "every IS_* test the translator writes has an in-process body");
            RenderingRexGrammar.Written.Tests.Where(test => Writes(ServiceForm.TestOf(test, Path), raw) == false).Should().BeEmpty();
        }

        /// <summary>The sentinels are inert: claimed for an atom, neither agrees nor is refuted, at either verdict.</summary>
        [Fact]
        public void TheSentinelsAgreeWithNothingAndAreRefutedByNothing()
        {
            var atom = Ne(OracleFixture.Text(LabelPath), "bikes");
            foreach (var sentinel in new[] { Priced.FormOf(Priced.None), RenderingTemplates.Spell(TemplateId.BooleanAlternatives, RenderPosition.Neg, PredicateAtom.Compare(PredicateAtom.TextReader, "<>", JsonValues.Of("bikes")), Label) })
            {
                sentinel.Kind.Should().Be(ServiceFormKind.Opaque);
                var (equal, implies) = Check(atom, sentinel);
                (equal.Kind, implies.Kind).Should().Be((RenderingVerdictKind.Unproven, RenderingVerdictKind.Unproven), sentinel.Text);
            }
        }

        /// <summary>Each atom of the conjuncts with its path's spelling, read by S's own analysis in a bundle of its own.</summary>
        internal static IReadOnlyList<(PredicateAtom Atom, string Path)> AtomsOf(IEnumerable<SqlPredicate> conjuncts)
        {
            var theory = ProbeTheory;
            using var bundle = theory.Open(RenderingCheckRun.AssertedSet);
            foreach (var conjunct in conjuncts)
                bundle.Write(conjunct);

            bundle.Scope.Run(Schedule.Saturate(theory.Ir.Analyse));
            var spelled = bundle.Scope.Rows(theory.Spelling).ToDictionary(r => r.Keys[0], r => (string)r.Output!);
            return [.. bundle.Scope.Rows(theory.Ir.Atom).Select(r => ((PredicateAtom)r.Keys[2], spelled[r.Keys[1]])).Distinct()];
        }

        /// <summary>
        /// An atom of every reader, test and kind of operand; plain, one ordinary literal of each kind, or with the literals
        /// that test the edges of numbers and the band and surrogate filters beside them.
        /// </summary>
        static IEnumerable<PredicateAtom> Synthetic(bool plain)
        {
            var readers = ReaderEdgesTable.Rows.Keys.Concat(new[] { PredicateAtom.CastReader("VARCHAR", PredicateAtom.RawReader), PredicateAtom.CastReader("INTEGER", PredicateAtom.RawReader), "typed:DATE" });
            var literals = plain ? new[] { JsonValues.Of("bikes"), JsonValues.Of("true"), JsonValues.Of(30m), JsonValues.Of(true) }
                : new[] { "bikes", "false", "", "\uD800", "x", "a😀" }.Select(JsonValues.Of)
                    .Concat(new[] { 0m, 2.5m, -1m, 9007199254740993m, decimal.MaxValue, decimal.MinValue, 0.1m, 1.9999999999999999m }.Select(JsonValues.Of))
                    .Concat([JsonValues.Of(true), JsonValues.Of(false), JsonValues.NullValue, JsonValues.Of(1e300)]);
            var points = plain ? [ValueSet.Of(JsonValues.Of("bikes"), JsonValues.Of("shoes"))]
                : new[] { ValueSet<string>.Empty, ValueSet.Of(JsonValues.Of("a")), ValueSet.Of(JsonValues.Of("a"), JsonValues.Of(2m), JsonValues.Of(true)) };

            foreach (var reader in readers)
            {
                foreach (var op in new[] { "=", "<>", "<", "<=", ">", ">=" })
                    foreach (var literal in literals)
                        yield return PredicateAtom.Compare(reader, op, literal);

                foreach (var set in points)
                    yield return PredicateAtom.In(reader, set);

                yield return PredicateAtom.IsNull(reader);
                yield return PredicateAtom.IsDefined(reader);
            }
        }

    }

}
