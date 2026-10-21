using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// The registry's one reader into the rendering graph: each atomic outcome of each row, one measured row per cell and
    /// per target it names. It merges nothing; which targets a key is settled under, and what two that differ mean
    /// together, are S's rules (<c>DESIGN.md</c>, <i>The registry, read into S</i>).
    /// </summary>
    internal static class CosmosAxiomRows
    {

        static readonly (CosmosAxiomTarget Flag, RenderingTarget Target)[] Targets =
            [(CosmosAxiomTarget.Emulator, RenderingTarget.Emulator), (CosmosAxiomTarget.Azure, RenderingTarget.Azure), (CosmosAxiomTarget.Calcite, RenderingTarget.Calcite)];

        /// <summary>One row per cell per target of every outcome that is no composite, citing its axiom.</summary>
        public static IReadOnlyList<MeasuredOutcome> Read(IEnumerable<CosmosAxiom> axioms) =>
        [
            .. from axiom in axioms
               from outcome in axiom.Outcomes ?? []
               where outcome.Formula is null
               from target in Targets
               where outcome.On.HasFlag(target.Flag)
               from cell in outcome.Cells
               select new MeasuredOutcome(axiom.Id, MeasuredKey.Of(outcome.Test, outcome.Operand, cell.Cell), target.Target, cell.Answer),
        ];

        /// <summary>
        /// The cells of a key some outcome answers for a target that no outcome covers for it: unmeasured there, so no
        /// row, and S reads the service as answering anything. Declared, so that a cell is owed on purpose and one
        /// dropped from a vector is caught.
        /// </summary>
        public static IReadOnlyList<(MeasuredKey Key, RenderingTarget Target)> Owed { get; } =
        [
            // What the emulator measures beside a key the probe account answered: the witnesses Azure ran none of, a string
            // beside "true" and a number beside 1 among them; another type against an order's literal; a range outside its
            // bounds, above them included, and over another type; and NOT over undefined.
            .. Cells(RenderingTarget.Azure, "Equal/True Bare/None Not/None", "String Number Object Array"),
            .. Cells(RenderingTarget.Azure, "Greater/String Range/Parted", "Absent Null True False Number Object Array"),
            .. Cells(RenderingTarget.Azure, "Range/Parted", "Outside"),
            .. Cells(RenderingTarget.Azure, "Not/None", "Undefined"),
        ];

        /// <summary>Each cell of each key, spelled <c>test/operand</c>, owed on a target.</summary>
        static IEnumerable<(MeasuredKey, RenderingTarget)> Cells(RenderingTarget target, string keys, string cells) =>
            from key in keys.Split(' ')
            let parts = key.Split('/')
            from cell in cells.Split(' ')
            select (MeasuredKey.Of(Enum.Parse<CosmosServiceTest>(parts[0]), Enum.Parse<CosmosValueClass>(parts[1]), Enum.Parse<CosmosValueClass>(cell)), target);

    }

}
