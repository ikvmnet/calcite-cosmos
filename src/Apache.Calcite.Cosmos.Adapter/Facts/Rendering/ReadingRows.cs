using System;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What the readings rules say of one reading, which a cell and the facts graph read (G7, <c>PredicateAtom</c>), class by
    /// class: what it reads each class as, the character classes a parse of a class was measured over, the reading it is
    /// evaluated as over a class a row says it is read as another, what a test answers over a class whatever the value,
    /// which tests the cell decides by what the class is read as, and the rows all of it rests on. A reading no row names
    /// reads nothing here, and is evaluated as itself.
    /// </summary>
    /// <param name="Reads">What it reads each class as, a row each.</param>
    /// <param name="Bands">The character classes its parse of a class was measured over (<c>calcitebands</c>), beyond which none is decided.</param>
    /// <param name="As">The reading it is evaluated as over a class (<c>calcitereadas</c>); none for itself.</param>
    /// <param name="Whole">What a test answers over a class whatever the value (<c>readtruths</c>).</param>
    /// <param name="Compared">The tests the cell decides over a class, by what the class is read as (<c>readcompares</c>).</param>
    /// <param name="Cites">The rows it rests on.</param>
    internal sealed record ReadingRows(ValueSet<ClassRead> Reads, ValueSet<ClassBand> Bands, ValueSet<ClassAlias> As, ValueSet<ClassTruths> Whole, ValueSet<ClassTest> Compared,
        ValueSet<string> Cites)
    {

        /// <summary>No row.</summary>
        public static readonly ReadingRows None = new(ValueSet<ClassRead>.Empty, ValueSet<ClassBand>.Empty, ValueSet<ClassAlias>.Empty, ValueSet<ClassTruths>.Empty,
            ValueSet<ClassTest>.Empty, ValueSet<string>.Empty);

        /// <summary>One row's: the class read as the answer.</summary>
        public static ReadingRows Read(CosmosValueClass kind, string answer) => None with { Reads = ValueSet.Of(new ClassRead(kind, answer)) };

        /// <summary>The character classes a parse of the class was measured over.</summary>
        public static ReadingRows Measured(CosmosValueClass kind, ValueSet<string> bands) => None with { Bands = ValueSet.From(bands.AsEnumerable().Select(b => new ClassBand(kind, b))) };

        /// <summary>Evaluated as another reading over the class.</summary>
        public static ReadingRows Evaluated(CosmosValueClass kind, string reader) => None with { As = ValueSet.Of(new ClassAlias(kind, reader)) };

        /// <summary>What the test answers over the class whatever the value.</summary>
        public static ReadingRows Answering(CosmosValueClass kind, CosmosServiceTest test, Truths truths) => None with { Whole = ValueSet.Of(new ClassTruths(kind, test, truths)) };

        /// <summary>The test the cell decides over the class read as the answer.</summary>
        public static ReadingRows Comparing(CosmosValueClass kind, CosmosServiceTest test, string answer) => None with { Compared = ValueSet.Of(new ClassTest(kind, test, answer)) };

        /// <summary>A row it rests on.</summary>
        public static ReadingRows Cited(string row) => None with { Cites = ValueSet.Of(row) };

        /// <summary>Both's rows; a class evaluated as two readings is a conflict, which fails the run.</summary>
        public static ReadingRows Join(ReadingRows a, ReadingRows b)
        {
            var aliases = a.As.Union(b.As);
            if (aliases.AsEnumerable().GroupBy(x => x.Kind).FirstOrDefault(g => g.Count() > 1) is { } twice)
                throw new InvalidOperationException($"A reading is evaluated as {string.Join(" and ", twice.Select(x => x.Reader))} over {twice.Key}.");

            return new(a.Reads.Union(b.Reads), a.Bands.Union(b.Bands), aliases, a.Whole.Union(b.Whole), a.Compared.Union(b.Compared), a.Cites.Union(b.Cites));
        }

        /// <summary>Whether a row reads the class as the answer.</summary>
        public bool ReadsAs(CosmosValueClass kind, string answer) => Reads.Contains(new ClassRead(kind, answer));

        /// <summary>The character classes a parse of the class was measured over.</summary>
        public ValueSet<string> BandsOf(CosmosValueClass kind) => ValueSet.From(Bands.AsEnumerable().Where(b => b.Kind == kind).Select(b => b.Band));

        /// <summary>The reading it is evaluated as over the class; <c>null</c> for itself.</summary>
        public string? AliasOf(CosmosValueClass kind) => As.AsEnumerable().Where(a => a.Kind == kind).Select(a => a.Reader).FirstOrDefault();

        /// <summary>What the test answers over the class whatever the value; none where nothing says.</summary>
        public Truths WholeOf(CosmosValueClass kind, CosmosServiceTest test) =>
            Whole.AsEnumerable().Where(w => w.Kind == kind && w.Test == test).Aggregate(Truths.None, (t, w) => t | w.Truths);

        /// <summary>What the class is read as where the cell decides the test.</summary>
        public ValueSet<string> ComparedOf(CosmosValueClass kind, CosmosServiceTest test) =>
            ValueSet.From(Compared.AsEnumerable().Where(c => c.Kind == kind && c.Test == test).Select(c => c.Answer));

        /// <summary>The rows spelled as the theory's digest reads a value.</summary>
        public override string ToString() => string.Join("/",
            string.Join(",", Reads.AsEnumerable()), string.Join(",", Bands.AsEnumerable()), string.Join(",", As.AsEnumerable()), string.Join(",", Whole.AsEnumerable()),
            string.Join(",", Compared.AsEnumerable()), string.Join(",", Cites.AsEnumerable()));

    }

}
