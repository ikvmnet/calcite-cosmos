using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// One connective over two concrete vectors (<see cref="ServiceCompound.On"/>): a failure survives it, a value that is
    /// no boolean is keyed by its own class, and without its rows it composes nothing.
    /// </summary>
    public sealed class ServiceCompoundTests
    {

        static readonly string S = SqlValue.Spell(JsonDocumentPath.Root.Property("s"));

        static readonly CellPartition Cells = PathCell.Split(new PathFacts(S, PathValues.Anything, AllBands), ValueSet.Of(new CutPoint(Of("bikes"), CutKind.Literal)));

        static readonly ServiceForm Leaf = ServiceForm.BareOf(S);

        static readonly ServiceForm[] Compounds =
        [
            ServiceForm.NotOf(Leaf),
            ServiceForm.JunctionOf(CosmosServiceTest.And, [Leaf, ServiceForm.TestOf(CosmosServiceTest.IsNull, S)]),
            ServiceForm.JunctionOf(CosmosServiceTest.Or, [Leaf, ServiceForm.TestOf(CosmosServiceTest.IsNull, S)]),
        ];

        static readonly ServiceTable Empty = ServiceTable.Of(ValueSet<MeasuredEntry>.Empty);

        static OutcomeVector Random(Random random, bool fails) => new(Cells, Cells.Cells.Select(_ =>
            new CellOutcome((ServiceOutcomes)random.Next(1, fails ? 16 : 8), random.Next(4) == 0 ? CosmosValueClass.String : CosmosValueClass.None)).ToList(), ValueSet<string>.Empty);

        static List<string> Lost(Func<ServiceForm, OutcomeVector, OutcomeVector, ServiceTable, OutcomeVector> compound)
        {
            var random = new Random(20_261_020);
            var lost = new List<string>();
            for (var trial = 0; trial < 50; trial++)
            {
                foreach (var form in Compounds)
                {
                    var first = Random(random, true);
                    var rest = form.Kind == ServiceFormKind.Not ? first : Random(random, true);
                    foreach (var table in new[] { ProbeRows.Table, Empty })
                    {
                        var result = compound(form, first, rest, table);
                        for (var i = 0; i < Cells.Cells.Count; i++)
                            if (((first.Outcomes[i].Outcomes | rest.Outcomes[i].Outcomes) & ServiceOutcomes.Fails) != 0 && result.Outcomes[i].Outcomes.HasFlag(ServiceOutcomes.Fails) == false)
                                lost.Add($"{form} loses a failure on {Cells.Cells[i]}");
                    }
                }
            }

            return lost;
        }

        /// <summary>
        /// A side that may fail the statement lets the compound fail it, through NOT, AND and OR alike, and never makes it:
        /// a side that only fails, beside one that decides the connective alone, may be skipped.
        /// </summary>
        [Fact]
        public void FailsSurvivesEveryConnective()
        {
            static OutcomeVector All(ServiceOutcomes outcomes) => new(Cells, Cells.Cells.Select(_ => new CellOutcome(outcomes, CosmosValueClass.None)).ToList(), ValueSet<string>.Empty);
            ServiceCompound.On(Compounds[1], All(ServiceOutcomes.False), All(ServiceOutcomes.Fails), ProbeRows.Table).Outcomes
                .Should().OnlyContain(o => o.Outcomes == (ServiceOutcomes.False | ServiceOutcomes.Fails));

            Lost(ServiceCompound.On).Should().BeEmpty();
            Lost((form, first, rest, table) =>
            {
                var planted = ServiceCompound.On(form, first, rest, table);
                return form.Kind != ServiceFormKind.Not ? planted : planted with { Outcomes = planted.Outcomes.Select(o => o with { Outcomes = o.Outcomes & ~ServiceOutcomes.Fails }).ToList() };
            }).Should().NotBeEmpty("the plant's NOT drops a failure");
        }

        /// <summary>
        /// A path written bare carries the class of a value that is no boolean, and a connective over it reads that class's
        /// row, not unknown's: planted rows that answer otherwise show which was read.
        /// </summary>
        [Fact]
        public void NonBooleansReadTheirOwnKeys()
        {
            var not = MeasuredKey.Of(CosmosServiceTest.Not, CosmosValueClass.None, CosmosValueClass.String);
            var and = MeasuredKey.Of(CosmosServiceTest.And, CosmosValueClass.String, CosmosValueClass.True);
            var kinds = new[] { CosmosValueClass.Absent, CosmosValueClass.Null, CosmosValueClass.True, CosmosValueClass.False, CosmosValueClass.Number, CosmosValueClass.String, CosmosValueClass.Object, CosmosValueClass.Array };
            var table = ProbeRows.TableOf(ProbeRows.Rows.Where(row => row.Target != RenderingTarget.Calcite && row.Key != not && row.Key != and)
                .Concat(kinds.Select(kind => new MeasuredOutcome("Service.BarePathKeepsOnlyTrue", MeasuredKey.Of(CosmosServiceTest.Bare, CosmosValueClass.None, kind), RenderingTarget.Emulator,
                    kind == CosmosValueClass.True ? CosmosAnswer.True : kind == CosmosValueClass.False ? CosmosAnswer.False : CosmosAnswer.Undefined)))
                .Append(new MeasuredOutcome("Service.Planted", not, RenderingTarget.Emulator, CosmosAnswer.True))
                .Append(new MeasuredOutcome("Service.Planted", and, RenderingTarget.Emulator, CosmosAnswer.False)));

            // The cells in order: absent, array, false, true, fractions, integers, null, object, 'bikes', the strings either side.
            var bare = ServiceLeaf.On(Leaf, Cells, table, ValueSet<string>.Empty);
            bare.Outcomes.Select(o => o.NonBoolean.ToString()).Should().Equal("Absent", "Array", "None", "None", "Number", "Number", "Null", "Object", "String", "String", "String");

            var truths = new OutcomeVector(Cells, Cells.Cells.Select(_ => new CellOutcome(ServiceOutcomes.True, CosmosValueClass.None)).ToList(), ValueSet<string>.Empty);

            var negated = ServiceCompound.On(Compounds[0], bare, bare, table);
            var joined = ServiceCompound.On(Compounds[1], bare, truths, table);
            for (var i = 0; i < Cells.Cells.Count; i++)
            {
                if (bare.Outcomes[i].NonBoolean == CosmosValueClass.String)
                    (negated.Outcomes[i].Outcomes, joined.Outcomes[i].Outcomes).Should().Be((ServiceOutcomes.True, ServiceOutcomes.False), Cells.Cells[i].Text);
                else if (bare.Outcomes[i].NonBoolean != CosmosValueClass.None)
                    (negated.Outcomes[i].Outcomes, joined.Outcomes[i].Outcomes).Should().Be((ServiceOutcomes.Undefined, ServiceOutcomes.Undefined), Cells.Cells[i].Text);
            }

            negated.Rows.Should().Contain("Service.Planted");
        }

        /// <summary>With no rows, a connective answers everything, whatever its sides.</summary>
        [Fact]
        public void AnEmptyTableAnswersWidest()
        {
            var random = new Random(20_261_020);
            for (var trial = 0; trial < 50; trial++)
            {
                foreach (var form in Compounds)
                {
                    var first = Random(random, false);
                    ServiceCompound.On(form, first, form.Kind == ServiceFormKind.Not ? first : Random(random, false), Empty).Outcomes
                        .Should().OnlyContain(o => o.Outcomes == ServiceOutcomes.Widest && o.NonBoolean == CosmosValueClass.None);
                }
            }
        }

    }

}
