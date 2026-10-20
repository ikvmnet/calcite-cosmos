using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The filters S's rules apply, named so that each renders by name in <c>Rule.ToString()</c> and the order test can
    /// read what a rule tests. None reads a table; each decides a property of the values it is handed.
    /// </summary>
    internal static class RenderingPrimitives
    {

        /// <summary>Every truth of the first set is in the second.</summary>
        public static Primitive<Truths, Truths, bool> TruthsWithin { get; } = Primitive.Create<Truths, Truths, bool>("truths-within", (a, b) => (a & ~b) == Truths.None);

        /// <summary>Every outcome of the first set is in the second.</summary>
        public static Primitive<ServiceOutcomes, ServiceOutcomes, bool> OutcomesWithin { get; } =
            Primitive.Create<ServiceOutcomes, ServiceOutcomes, bool>("outcomes-within", (a, b) => (a & ~b) == ServiceOutcomes.None);

        /// <summary>A set of classes holds one: the existential over cells.</summary>
        public static Primitive<ValueSet<CellClass>, bool> SetNonempty { get; } = Primitive.Create<ValueSet<CellClass>, bool>("set-nonempty", s => s.IsEmpty == false);

        /// <summary>A form tests one path, and has no parts.</summary>
        public static Primitive<ServiceForm, bool> FormIsLeaf { get; } = Primitive.Create<ServiceForm, bool>("form-is-leaf", f => f.Members.Count == 0);

        /// <summary>A form is a connective over parts.</summary>
        public static Primitive<ServiceForm, bool> FormIsCompound { get; } = Primitive.Create<ServiceForm, bool>("form-is-compound", f => f.Members.Count > 0);

        /// <summary>A form orders a path against a string, which the service's order and Calcite's may rank apart.</summary>
        public static Primitive<ServiceForm, bool> StringOrdering { get; } = Primitive.Create<ServiceForm, bool>("string-ordering", f =>
            f.Kind == ServiceFormKind.Compare && f.Test is CosmosServiceTest.Less or CosmosServiceTest.LessOrEqual or CosmosServiceTest.Greater or CosmosServiceTest.GreaterOrEqual
            && JsonValues.IsString(f.Literal!));

    }

}
