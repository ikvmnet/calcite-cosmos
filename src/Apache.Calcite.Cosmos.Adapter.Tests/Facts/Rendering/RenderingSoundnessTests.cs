using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The soundness line (spec §5.5, §6): no proof the check run reports has a witness. Source A samples documents from
    /// each proven check's import; source B plans population D's conjuncts over a container their declaration describes and
    /// holds every proof to the declaration's own documents. Each is judged by <see cref="IrEval"/> against
    /// <see cref="CosmosEval"/> on every target the asserted set serves; a document <c>IrEval</c> tossed a coin on is
    /// unmeasured, and one every typed mode raises on is skipped (decision 2).
    /// </summary>
    public sealed class RenderingSoundnessTests
    {

        /// <summary>Source B's trials every pull request pays, of §10.3's 20 to 50.</summary>
        public const int Trials = 30;

        public const int LongTrials = 5_000;

        static readonly IrEval.TypedAs[] Modes = [IrEval.TypedAs.Returning, IrEval.TypedAs.Column];

        /// <summary>The container's hierarchical key, the paths a raw value can be read at: only a key path is a column.</summary>
        static readonly string[] Keys = ["kind", "a", "n"];

        /// <summary>Every proof the pull request's run reports, held to source A's documents, and source B's trials.</summary>
        [Fact]
        public void NoReportedProofHasAWitness()
        {
            var (a, b) = (SourceA(RenderingCheckRun.PerPullRequest.Value), SourceB(Trials));
            TestContext.Current.TestOutputHelper?.WriteLine($"source A: {a}\nsource B: {b}");
            (a.Proofs, b.Proofs).Should().Match<(int, int)>(p => p.Item1 > 0 && p.Item2 > 0);
            a.Witnesses.Concat(b.Witnesses).Should().BeEmpty();
        }

        [Fact(Explicit = true)]
        public void NoReportedProofHasAWitnessOverALongRun()
        {
            var b = SourceB(LongTrials);
            TestContext.Current.TestOutputHelper?.WriteLine($"source B: {b}");
            b.Witnesses.Should().BeEmpty();
        }

        /// <summary>
        /// Source A: each memo entry with a proof, at the root or of a conjunct at its own position, judged on documents
        /// drawn from its import. A document a stratum's facts rule out beside the conjuncts it assumes is no declared one,
        /// and a conjunct proven beside such a stratum's facts is judged only where those conjuncts hold.
        /// </summary>
        internal static Tally SourceA(RenderingCheckRun run, Tally? tally = null, IReadOnlyList<J.Obj>? declared = null)
        {
            tally ??= new Tally();
            foreach (var check in run.Conjuncts.Where(c => c.Entry?.What is not null).GroupBy(c => c.Entry!.Id).Select(g => g.First()))
                SourceA(tally, check.Entry!, check.Assumes, $"{check.Input} {check.Name}#{check.Unit}, entry {check.Entry!.Id}", declared);

            return tally;
        }

        /// <summary>Source A over one entry; or source B's, over a trial's declared documents, which no stratum's facts rule out.</summary>
        internal static void SourceA(Tally tally, RenderingMemo.Entry entry, IReadOnlyDictionary<string, int[]>? assumed, string where, IReadOnlyList<J.Obj>? declared = null)
        {
            var (claim, world, assumes) = (entry.What!, new World(entry.Id), assumed ?? new Dictionary<string, int[]>());
            bool Holds(J.Obj d, IEnumerable<string> paths) => paths.Where(assumes.ContainsKey).SelectMany(p => assumes[p])
                .All(i => IrEval.Truth(claim.Conjuncts[i], d, world) == Truths.True);

            var proofs = Proofs(entry, claim).ToList();
            tally.Proofs += proofs.Count;
            foreach (var document in proofs.Count == 0 ? [] : declared ?? RenderingDocumentSampler.Sample(claim))
            {
                if (declared is null && assumes.Keys.Any(p => PopulationD.Admits(claim.Imports[p].Assumed, CosmosEval.At(document, p)) == false && Holds(document, [p])))
                    continue;

                tally.Judged++;
                foreach (var (ir, form, verdict, paths) in proofs)
                    if (paths is null || Holds(document, paths))
                        tally.Judge(ir, form, verdict, document, world, where);
            }
        }

        /// <summary>
        /// Source B: population D's declarations, each trial's printable conjuncts written as one Calcite <c>WHERE</c> over a
        /// container the declaration describes, planned and checked as every input is, and every proof judged on the
        /// trial's declared documents.
        /// </summary>
        internal static Tally SourceB(int trials)
        {
            var tally = new Tally();
            for (var trial = 0; trial < trials; trial++)
            {
                var (random, _, _, declaration, documents) = PopulationD.Trial(PredicateSoundnessTests.Seed + trial);
                var printed = Statements.Draw(random, declaration).Select(Sql).OfType<string>().ToList();
                if (printed.Count == 0)
                    continue;

                var container = new CosmosContainerMetadata("d", Keys.Select(k => "/" + k)).WithFacts(declaration.Rules).WithFactGenerations(new JsonFactGenerations());
                var run = RenderingCheckRun.Over(RenderingInputs.Differential("population", [$"SELECT c.\"id\" FROM d AS c WHERE {string.Join(" AND ", printed)}"], [container]));
                tally.Statements++;
                tally.Unplanned += run.Failures.Count;
                SourceA(run, tally, documents);
            }

            return tally;
        }

        /// <summary>
        /// An entry's proofs: of the claim at its root, over every document; and of each conjunct at its own position, with
        /// the paths it reads, over the documents where the conjuncts its strata assume hold.
        /// </summary>
        static IEnumerable<(SqlPredicate Ir, ServiceForm Form, Verdict Claim, IReadOnlyList<string>? Paths)> Proofs(RenderingMemo.Entry entry, RenderingMemo.Claimed claim)
        {
            if (entry.Asserted.Kind == RenderingVerdictKind.Proven)
                yield return (claim.Conjuncts.Count == 1 ? claim.Conjuncts[0] : new SqlPredicate.And(claim.Conjuncts), claim.Form, entry.Key.Claim, null);

            for (var i = 0; i < claim.Conjuncts.Count; i++)
                if (entry.Alone[i] is { Kind: RenderingVerdictKind.Proven } && claim.Members[i] is { } member)
                    yield return (claim.Conjuncts[i], member, entry.Key.Claim, [.. claim.Conjuncts[i].Paths.Select(SqlValue.Spell)]);
        }

        /// <summary>A population D conjunct as Calcite SQL over the container, or <c>null</c> where it holds what no statement here writes.</summary>
        static string? Sql(SqlPredicate predicate) => predicate switch
        {
            SqlPredicate.Not n => Sql(n.Operand) is { } operand ? $"NOT ({operand})" : null,
            SqlPredicate.And a => Joined(a.Arms, " AND "),
            SqlPredicate.Or o => Joined(o.Arms, " OR "),
            SqlPredicate.Compare { Right: SqlValue.Literal l } c when Value(c.Left) is { } v && Literal(l.Value) is { } x => $"{v} {c.Op} {x}",
            SqlPredicate.In i when Value(i.Value) is { } v && i.Points.All(p => Literal(p) is not null) => $"{v} IN ({string.Join(", ", i.Points.Select(Literal))})",
            SqlPredicate.IsNull n when Value(n.Value) is { } v => $"{v} IS NULL",
            _ => null,
        };

        static string? Joined(IEnumerable<SqlPredicate> arms, string connective) =>
            arms.Select(Sql).ToList() is var each && each.All(a => a is not null) ? "(" + string.Join(connective, each) + ")" : null;

        static string? Value(SqlValue value) => value switch
        {
            SqlValue.Text t => $"JSON_VALUE(c.\"DOC\", '$.{string.Join(".", t.Path.Names)}')",
            SqlValue.Typed t => $"JSON_VALUE(c.\"DOC\", '$.{string.Join(".", t.Path.Names)}' RETURNING {t.Type})",
            SqlValue.Raw r when r.Path.Names.Count() == 1 && Keys.Contains(r.Path.Names.Single()) => $"c.\"$.{r.Path.Names.Single()}\"",
            SqlValue.Cast c => Value(c.Value) is { } operand ? $"CAST({operand} AS {c.Target})" : null,
            _ => null,
        };

        static string? Literal(string value) => TypeOf(value) switch
        {
            StringType when StringOf(value).All(c => c >= ' ' && char.IsSurrogate(c) == false) => "'" + StringOf(value).Replace("'", "''", StringComparison.Ordinal) + "'",
            IntegerType or FractionType when IsExactNumber(value) => StringOf(value),
            BooleanType => value == Of(true) ? "TRUE" : "FALSE",
            _ => null,
        };

        /// <summary>What a source judged: proofs, documents, and each document's verdict.</summary>
        internal sealed class Tally
        {

            public int Statements, Unplanned, Proofs, Judged, Agreed, Unmeasured, Skipped;

            public List<string> Witnesses { get; } = [];

            /// <summary>
            /// Judges a proof on a document: every typed mode that answers against every target, a failure never allowed,
            /// <c>Equal</c> keeping alike and <c>Implies</c> keeping at least Calcite's row. A mode <c>IrEval</c> does not
            /// model — the column of a <c>RETURNING DECIMAL</c>, which S reads as the clause's parse — is left out, and a
            /// document no mode measures is unmeasured.
            /// </summary>
            public void Judge(SqlPredicate ir, ServiceForm form, Verdict claim, J.Obj document, World world, string where)
            {
                var (calcite, measured) = (new List<Truths>(), false);
                foreach (var mode in Modes)
                {
                    var tossed = world.Tossed;
                    Truths truth;
                    try
                    {
                        truth = IrEval.Truth(ir, document, world, mode);
                    }
                    catch (NotSupportedException)
                    {
                        continue;
                    }

                    if (world.Tossed != tossed)
                    {
                        Unmeasured++;
                        return;
                    }

                    measured = true;
                    if (truth != Truths.Raises)
                        calcite.Add(truth);
                }

                if (calcite.Count == 0)
                {
                    if (measured)
                        Skipped++;
                    else
                        Unmeasured++;

                    return;
                }

                foreach (var target in RenderingCheckRun.Targets)
                {
                    if (CosmosEval.On(form, document, target) is not { } service)
                    {
                        Unmeasured++;
                        return;
                    }

                    if (calcite.Any(t => service == ServiceOutcomes.Fails || (claim == Verdict.Equal ? (t == Truths.True) != (service == ServiceOutcomes.True) : t == Truths.True && service != ServiceOutcomes.True)))
                    {
                        Witnesses.Add($"{where}: {form.Text} for {ir}, on {Documents.Json(document)}: Calcite {string.Join("|", calcite)}, {target} {service}");
                        return;
                    }
                }

                Agreed++;
            }

            public override string ToString() => $"{Statements} statements ({Unplanned} not planned), {Proofs} proofs over {Judged} documents: {Agreed} agreed, "
                + $"{Unmeasured} unmeasured, {Skipped} skipped, {Witnesses.Count} witnesses";

        }

    }

}
