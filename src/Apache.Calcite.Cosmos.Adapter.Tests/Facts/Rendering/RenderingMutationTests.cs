using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Sql.CosmosTranslatorFault;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The mutation line (spec §5.4, §6): each of the ten historical translator faults planted at the writer's own site
    /// (<see cref="CosmosTranslatorFaults"/>) and its triggers checked as the check run checks its inputs. The fault fires,
    /// changing what is written; each trigger it was found by is refuted with the cell S names, the least refuting one by
    /// text; and unplanted, the tip writes what it wrote. The controls X1 and X2, and M11 at the split rule's weakening, are
    /// reported beside.
    /// </summary>
    public sealed class RenderingMutationTests
    {

        const string Typed = "SELECT c.\"id\" FROM typed AS c WHERE ", Label = "JSON_VALUE(c.\"DOC\", '$.label')", Products = "SELECT c.\"id\" FROM products AS c WHERE ";

        /// <summary>
        /// Each fault's triggers, the differential's but the one M8 needed: what each must read under the fault, a refutation
        /// spelled as its cells and class, or <c>Exact</c>; or <c>null</c>, reported only, where no cell is definite.
        /// </summary>
        internal static readonly Dictionary<CosmosTranslatorFault, (string Sql, string? Reads)[]> Triggers = new()
        {
            [M1] = [.. new[] { "P1", "P19", "P2", "P3", "P25" }.Select(p => (RenderingRecallTests.Cases[p].Sql, (string?)RenderingRecallTests.Cases[p].Refuted))],
            [M2] = [.. new[] { "P4", "P5", "P6" }.Select(p => (RenderingRecallTests.Cases[p].Sql, (string?)RenderingRecallTests.Cases[p].Refuted))],
            [M3] = [(Products + "c.\"$.category\" <> 'bikes'", "$[\"category\"]=null (U,t)"),
                ("SELECT c.\"$.category\", COUNT(*) AS n FROM products AS c GROUP BY c.\"$.category\" HAVING c.\"$.category\" <> 'bikes'", "$[\"category\"]=null (U,t)")],
            [M4] = [(Products + "NOT (c.\"$.category\" <> 'bikes')", "$[\"category\"]=absent (U,t)"), (RenderingRecallTests.Cases["P4"].Sql, "$[\"label\"]=array (U,t)")],
            [M5] = [(RenderingRecallTests.Cases["P5"].Sql, "$[\"category\"]=null; $[\"label\"]=string|bikes (U,t)"),
                (RenderingRecallTests.Cases["P6"].Sql, "$[\"category\"]=null; $[\"label\"]=boolean|false (U,t)")],
            [M6] = [(Typed + Label + " = 'true'", "$[\"label\"]=boolean|true (T,f)"), (Typed + Label + " = '30'", null)],
            [M7] = [(Typed + $"CAST({Label} AS VARCHAR) = 'true'", "$[\"label\"]=boolean|true (T,f)"), (Typed + $"CAST({Label} AS VARCHAR) = '30'", null)],
            // The first is M8's own, which no list held; under the fault the last is written as the translator's exact form.
            [M8] = [(Typed + Label + " <> 'true'", "$[\"label\"]=boolean|true (F,t)"), (Typed + Label + " <> '30'", null), (Typed + Label + " <> 'bikes'", "Exact")],
            [M9] = [(Typed + Label + " > 'bikes'", "$[\"label\"]=boolean|false (T,u)"), (Typed + Label + " < 'bikes'", "$[\"label\"]=fractions (T,u)"),
                (Typed + Label + " >= '30'", "$[\"label\"]=boolean|false (T,u)")],
            // A DECIMAL column, which typed:DECIMAL is too, raises over the string "2" the clause parses: no cell is definite.
            [M10] = [("SELECT c.\"id\" FROM declared AS c WHERE JSON_VALUE(c.\"DOC\", '$.body' RETURNING DECIMAL) = 2", "Unproven")],
            // Its weakening is what M11 changes, and what it reads is the weakening's (M11IsCaughtAsImpliedToUnproven).
            [M11] = [(Typed + Label + " = '30'", null), (Typed + $"CAST({Label} AS VARCHAR) = '30'", null),
                ("SELECT p.\"id\" FROM (SELECT c.\"id\", JSON_VALUE(c.\"DOC\", '$.label') AS \"Label\" FROM typed AS c) AS p WHERE p.\"Label\" = '30'", null)],
            [X1] = [(Products + "NOT (CASE WHEN c.\"$.category\" <> 'bikes' THEN c.\"id\" = '1' ELSE c.\"id\" = '6' END)", "Skipped"),
                (Products + "NOT (CASE WHEN c.\"$.category\" = 'bikes' THEN c.\"id\" = '1' ELSE c.\"id\" <> '6' END)", "Skipped")],
            [X2] = [(Typed + "JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) <> 30", "Unproven")],
        };

        public static TheoryData<string> Ten => [.. Enumerable.Range(1, 10).Select(i => "M" + i)];

        /// <summary>A fault is caught: it fires, and each trigger it was found by reads as pinned.</summary>
        [Theory]
        [MemberData(nameof(Ten))]
        public void IsCaught(string fault) => Mutated(Enum.Parse<CosmosTranslatorFault>(fault));

        /// <summary>
        /// X1, the plan's named fault: a <c>CASE</c> condition written in the <c>CASE</c>'s position. The loader reads a
        /// <c>CASE</c> in a predicate's place as opaque, so what it writes is skipped outside the IR, as the tip's was:
        /// never proven, never refuted (decision D4).
        /// </summary>
        [Fact]
        public void X1IsSkippedOutsideTheIr() =>
            Mutated(X1).Planted.Where(c => c.Site == 2).Should().OnlyContain(c => c.Skip == "outside the IR");

        /// <summary>
        /// X2: a typed inequality's type test dropped. Of the cells it lets through, a string or a boolean is one where
        /// Calcite raises; but a structure <c>RETURNING</c> reads as null, where the service's <c>!=</c> is true, and the
        /// column fails the statement: the typed reading is their union (decision D12), so the raise stands beside the
        /// unknown, and the check is Unproven, never refuted, where the spec expected Exact.
        /// </summary>
        [Fact]
        public void X2IsNeverRefuted() => Mutated(X2);

        /// <summary>
        /// M11 (8d57df0): a text equality's weakening without its number branch keeps the string alone and loses the number
        /// 30, which Calcite may render as the literal or not, so no cell refutes it. It is caught as Implied → Unproven: the
        /// tip's weakening of each trigger is implied by its conjunct, and the planted one is not.
        /// </summary>
        [Fact]
        public void M11IsCaughtAsImpliedToUnproven()
        {
            var (tip, planted) = Mutated(M11);
            static string Weakenings(IEnumerable<RenderingCheckRun.Checked> run) =>
                string.Join(" | ", run.Where(c => c.Site == 3).OrderBy(c => c.Name).Select(c => c.Name + " " + RenderingWeakeningTests.Read(c)));

            (Weakenings(tip), Weakenings(planted)).Should().Be(("0 Implied | 1 Implied | 2 Implied", "0 Unproven | 1 Unproven | 2 Unproven"));
        }

        /// <summary>
        /// Plants a fault over its triggers between two unplanted runs: what is written changes under it and is the tip's
        /// again after it, and what each trigger sends reads as pinned. Returns the tip's conjuncts and the planted run's.
        /// </summary>
        static (IReadOnlyCollection<RenderingCheckRun.Checked> Tip, IReadOnlyCollection<RenderingCheckRun.Checked> Planted) Mutated(CosmosTranslatorFault fault)
        {
            var statements = Triggers[fault].Select(t => t.Sql).ToArray();
            statements.Except([Triggers[M8][0].Sql]).Should().BeSubsetOf(CosmosDifferentialTests.Statements, "a trigger is one of the inputs");

            var tip = Checked(None, statements);
            var planted = Checked(fault, statements);
            Written(planted).Should().NotEqual(Written(tip), "the fault fired");
            Written(Checked(None, statements)).Should().Equal(Written(tip), "unplanted, the tip writes what it wrote");

            planted.Failures.Should().BeEmpty();
            var reads = Triggers[fault].Select((t, i) => (t.Sql, t.Reads, Read: string.Join(" | ", planted.Conjuncts.Where(c => c.Site < 3 && c.Name == i.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Select(c => c.Skip is null ? Show(c.Entry!.Asserted) : "Skipped").Distinct()))).ToList();
            foreach (var (sql, _, read) in reads)
                TestContext.Current.TestOutputHelper?.WriteLine($"{fault}: {sql}: {read}");

            reads.Where(r => r.Reads is not null).Select(r => (r.Sql, r.Read)).Should().Equal(reads.Where(r => r.Reads is not null).Select(r => (r.Sql, r.Reads!)));
            return (tip.Conjuncts, planted.Conjuncts);
        }

        /// <summary>Plans and checks the statements with the faults planted, as the check run checks its inputs.</summary>
        internal static RenderingCheckRun Checked(CosmosTranslatorFault faults, params string[] statements)
        {
            using (CosmosTranslatorFaults.Plant(faults))
                return RenderingCheckRun.Over(RenderingInputs.Differential("mutation", statements, RenderingInputs.Containers));
        }

        /// <summary>Every conjunct a run's statements wrote, at either site, in order.</summary>
        static IEnumerable<string> Written(RenderingCheckRun run) =>
            run.Conjuncts.OrderBy(c => int.Parse(c.Name, System.Globalization.CultureInfo.InvariantCulture)).ThenBy(c => c.Unit).ThenBy(c => c.Position).Select(c => $"{c.Name}#{c.Unit} {c.Site}: {c.Text}");

        /// <summary>A verdict as the triggers pin it: a refutation's cells and class, or <c>Exact</c>, or its kind.</summary>
        internal static string Show(RenderingVerdict verdict) => RenderingGenerateMutationTests.Show(verdict);

    }

}
