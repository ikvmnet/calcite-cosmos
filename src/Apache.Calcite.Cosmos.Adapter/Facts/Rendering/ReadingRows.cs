using System;
using System.Linq;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What S's rows say of one reading, which Calcite's side of a cell reads (G7, <c>PredicateAtom.OnCell</c>): what it
    /// reads each class as, the character classes a parse of a string was measured over, and the reading S's rule says it
    /// is evaluated as, where it is another's. A reading no row names reads nothing here, and is evaluated as itself.
    /// </summary>
    /// <param name="Reads">What it reads each class as, a row each.</param>
    /// <param name="Bands">The character classes its parse of a string was measured over (<c>calcitebands</c>), beyond which no parse is decided.</param>
    /// <param name="As">The reading it is evaluated as, over the classes it reads; <c>null</c> for itself.</param>
    internal sealed record ReadingRows(ValueSet<ClassRead> Reads, ValueSet<string> Bands, string? As)
    {

        /// <summary>No row.</summary>
        public static readonly ReadingRows None = new(ValueSet<ClassRead>.Empty, ValueSet<string>.Empty, null);

        /// <summary>One row's: the class read as the answer.</summary>
        public static ReadingRows Read(CosmosValueClass kind, string answer) => None with { Reads = ValueSet.Of(new ClassRead(kind, answer)) };

        /// <summary>The classes a parse was measured over.</summary>
        public static ReadingRows Measured(ValueSet<string> bands) => None with { Bands = bands };

        /// <summary>Evaluated as another reading.</summary>
        public static ReadingRows Evaluated(string reader) => None with { As = reader };

        /// <summary>Both's rows; a reading evaluated as two others is a conflict, which fails the run.</summary>
        public static ReadingRows Join(ReadingRows a, ReadingRows b) =>
            a.As is { } x && b.As is { } y && x != y ? throw new InvalidOperationException($"A reading is evaluated as both {x} and {y}.")
            : new(a.Reads.Union(b.Reads), a.Bands.Union(b.Bands), a.As ?? b.As);

        /// <summary>What it reads a class as: every row's answer for it, none where no row names it.</summary>
        public ValueSet<string> AnswersOf(CosmosValueClass kind) => ValueSet.From(Reads.AsEnumerable().Where(r => r.Kind == kind).Select(r => r.Answer));

        /// <summary>Whether a row reads the class as the answer.</summary>
        public bool ReadsAs(CosmosValueClass kind, string answer) => Reads.Contains(new ClassRead(kind, answer));

        /// <summary>Whether a row names the class.</summary>
        public bool Names(CosmosValueClass kind) => Reads.AsEnumerable().Any(r => r.Kind == kind);

        /// <summary>The rows spelled as the theory's digest reads a value.</summary>
        public override string ToString() => string.Join(",", Reads.AsEnumerable()) + "/" + string.Join(",", Bands.AsEnumerable()) + (As is null ? "" : "/as " + As);

    }

}
