using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Rendering;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Rendering.CosmosValueClass;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The probe's rows (<c>probes/rcore/Program.cs</c>), completed for the cells and connectives the first checks read:
    /// the kinds the probe lacks, <c>IS_DEFINED</c>, <c>NOT</c>, <c>AND</c> and <c>OR</c> over the truths and the six
    /// kinds that are no boolean, the strings' comparator, and <c>=</c> and <c>!=</c> within a type. Each is written for
    /// the emulator and Azure, so that both service sets settle it, and the connectives for Calcite too. Test data, until
    /// the registry replaces it.
    /// </summary>
    internal static class ProbeRows
    {

        static readonly CosmosValueClass[] Kinds = [Absent, Null, True, False, Number, String, Object, Array];

        static readonly CosmosValueClass[] Truths = [True, False, Undefined];

        static readonly CosmosValueClass[] Operands = [True, False, Undefined, Absent, Null, Number, String, Object, Array];

        public static IReadOnlyList<MeasuredOutcome> Rows { get; } = Build();

        /// <summary>The table a service set settles the rows into: one entry a key, both targets agreeing.</summary>
        public static ServiceTable Table { get; } = TableOf(Rows.Where(row => row.Target != RenderingTarget.Calcite));

        /// <summary>One entry a key, holding every answer its rows give and every row's id.</summary>
        public static ServiceTable TableOf(IEnumerable<MeasuredOutcome> rows) => ServiceTable.Of(ValueSet.From(rows.GroupBy(row => row.Key).Select(key =>
            MeasuredEntry.Of(key.Key, ValueSet.From(key.Select(row => row.Answer)), ValueSet.From(key.Select(row => row.AxiomId)), AnswerMeanings.Measured))));

        static List<MeasuredOutcome> Build()
        {
            var rows = new List<MeasuredOutcome>();
            void Service(string id, CosmosServiceTest test, CosmosValueClass operand, CosmosValueClass cell, CosmosAnswer answer)
            {
                rows.Add(new MeasuredOutcome("Service." + id, MeasuredKey.Of(test, operand, cell), RenderingTarget.Emulator, answer));
                rows.Add(new MeasuredOutcome("Service." + id, MeasuredKey.Of(test, operand, cell), RenderingTarget.Azure, answer));
            }

            foreach (var kind in Kinds)
            {
                Service("IsPrimitive", CosmosServiceTest.IsPrimitive, None, kind, kind is Absent or Object or Array ? CosmosAnswer.False : CosmosAnswer.True);
                Service("IsNull", CosmosServiceTest.IsNull, None, kind, kind == Null ? CosmosAnswer.True : CosmosAnswer.False);
                Service("IsDefined", CosmosServiceTest.IsDefined, None, kind, kind == Absent ? CosmosAnswer.False : CosmosAnswer.True);
                if (kind == String)
                    continue;

                Service("EqualityAcrossTypesIsFalse", CosmosServiceTest.Equal, String, kind, kind == Absent ? CosmosAnswer.Undefined : CosmosAnswer.False);
                Service("InequalityOverNullIsTrue", CosmosServiceTest.NotEqual, String, kind, kind == Absent ? CosmosAnswer.Undefined : CosmosAnswer.True);
            }

            Service("StringOrderIsCodePoint", CosmosServiceTest.Order, String, None, CosmosAnswer.CodePoint);
            foreach (var side in new[] { Below, Equal, Above })
            {
                Service("StringOrderIsCodePoint", CosmosServiceTest.Equal, String, side, side == Equal ? CosmosAnswer.True : CosmosAnswer.False);
                Service("StringOrderIsCodePoint", CosmosServiceTest.NotEqual, String, side, side == Equal ? CosmosAnswer.False : CosmosAnswer.True);
            }

            foreach (var x in Operands)
            {
                Service("NotOverUndefinedIsUndefined", CosmosServiceTest.Not, None, x, Kleene(CosmosServiceTest.Not, x, x));
                foreach (var y in Operands)
                    foreach (var connective in new[] { CosmosServiceTest.And, CosmosServiceTest.Or })
                        Service("ConnectivesReadNonBooleansAsUnknown", connective, x, y, Kleene(connective, x, y));
            }

            foreach (var x in Truths)
            {
                rows.Add(new MeasuredOutcome("Calcite.ConnectivesAreKleene", MeasuredKey.Of(CosmosServiceTest.Not, None, x), RenderingTarget.Calcite, Kleene(CosmosServiceTest.Not, x, x)));
                foreach (var y in Truths)
                    foreach (var connective in new[] { CosmosServiceTest.And, CosmosServiceTest.Or })
                        rows.Add(new MeasuredOutcome("Calcite.ConnectivesAreKleene", MeasuredKey.Of(connective, x, y), RenderingTarget.Calcite, Kleene(connective, x, y)));
            }

            return rows;
        }

        /// <summary>Kleene's connectives, a value that is no boolean read as unknown.</summary>
        static CosmosAnswer Kleene(CosmosServiceTest connective, CosmosValueClass x, CosmosValueClass y)
        {
            var (a, b) = (x is True or False ? x : Undefined, y is True or False ? y : Undefined);
            var answer = connective switch
            {
                CosmosServiceTest.Not => a == True ? False : a == False ? True : Undefined,
                CosmosServiceTest.And => a == False || b == False ? False : a == True && b == True ? True : Undefined,
                _ => a == True || b == True ? True : a == False && b == False ? False : Undefined,
            };

            return answer == True ? CosmosAnswer.True : answer == False ? CosmosAnswer.False : CosmosAnswer.Undefined;
        }

    }

}
