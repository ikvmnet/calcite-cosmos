using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// A reading of one of the three columns the IR's raw value is read through — a promoted path's <c>VARIANT</c>, an
    /// <c>ANY</c>, a field typed <c>VARCHAR</c> — the raw value or its cast to text, as a <c>Calcite</c> row measured it in
    /// process: what it reads a stored value of each class as, a stored null and an absent path among them; the literal
    /// classes it is compared beside; which comparisons compile over it; whether it compares a value as its type or as the
    /// text it renders; and what a comparison it compiles answers of a value beside a literal of another type.
    /// <see cref="CosmosAxiomRows.ReadColumns"/> reads it into S one fact a field, and S's rules say what each key answers (G5).
    /// </summary>
    /// <param name="Name">The column, by its SQL type.</param>
    /// <param name="Reader">The reading through the column: <c>raw</c>, or <c>cast:VARCHAR:raw</c>.</param>
    /// <param name="Reads">What it reads a stored value of each class as (<see cref="CalciteRead"/>).</param>
    /// <param name="AsType">Whether it compares a value as the value's type, or as the text it renders.</param>
    /// <param name="Beside">The literal classes it is compared beside.</param>
    /// <param name="Compiles">Whether each comparison compiles over it; one that does not fails the statement.</param>
    /// <param name="Across">What each comparison it compiles answers of a value it reads as itself beside a literal of another type.</param>
    internal sealed record CosmosRawColumn(string Name, string Reader, IReadOnlyDictionary<CosmosValueClass, string> Reads, bool AsType,
        IReadOnlyList<CosmosValueClass> Beside, IReadOnlyDictionary<CosmosServiceTest, bool> Compiles, IReadOnlyDictionary<CosmosServiceTest, Truths> Across)
    {

        /// <summary>The literal classes a column is compared beside where it takes any: every class a literal is of.</summary>
        public static IReadOnlyList<CosmosValueClass> Literals { get; } = [CosmosValueClass.String, CosmosValueClass.Number, CosmosValueClass.True, CosmosValueClass.False,
            CosmosValueClass.Object, CosmosValueClass.Array, CosmosValueClass.Null];

        /// <summary>
        /// One answer a class, as a row states them: a string's, a number's, a boolean's, a structure's, a stored null's and an
        /// absent path's, each measured over its witnesses (true and false, an object and an array).
        /// </summary>
        public static Dictionary<CosmosValueClass, string> Kinds(string text, string number, string boolean, string structure, string nulled, string absent) => new()
        {
            [CosmosValueClass.String] = text, [CosmosValueClass.Number] = number, [CosmosValueClass.True] = boolean, [CosmosValueClass.False] = boolean,
            [CosmosValueClass.Object] = structure, [CosmosValueClass.Array] = structure, [CosmosValueClass.Null] = nulled, [CosmosValueClass.Absent] = absent,
        };

        /// <summary>Whether an equality and each ordering compile, as a row states them.</summary>
        public static Dictionary<CosmosServiceTest, bool> Comparisons(bool equality, bool orderings) => new()
        {
            [CosmosServiceTest.Equal] = equality, [CosmosServiceTest.Less] = orderings, [CosmosServiceTest.LessOrEqual] = orderings,
            [CosmosServiceTest.Greater] = orderings, [CosmosServiceTest.GreaterOrEqual] = orderings,
        };

        /// <summary>What an equality and each ordering answer across types, as a row states them; none where it states none.</summary>
        public static Dictionary<CosmosServiceTest, Truths> AcrossTypes(Truths equality, Truths? orderings = null)
        {
            var across = new Dictionary<CosmosServiceTest, Truths> { [CosmosServiceTest.Equal] = equality };
            if (orderings is { } o)
                foreach (var test in new[] { CosmosServiceTest.Less, CosmosServiceTest.LessOrEqual, CosmosServiceTest.Greater, CosmosServiceTest.GreaterOrEqual })
                    across[test] = o;

            return across;
        }

    }

}
