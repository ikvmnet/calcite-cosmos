using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The filters S's rules apply, named so that each renders by name in <c>Rule.ToString()</c> and the order test can
    /// read what a rule tests. None reads a table; each decides a property of the values it is handed, and the forms' only
    /// by their structure.
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

        /// <summary>
        /// The connective whose laws evaluating a form by its first part and its rest takes for granted: a junction's own
        /// where it has more than two members, which the rest regroups and rids of copies; none otherwise.
        /// </summary>
        public static Primitive<ServiceForm, ValueSet<CosmosServiceTest>> FormRegroups { get; } = Primitive.Create<ServiceForm, ValueSet<CosmosServiceTest>>("form-regroups", f =>
            f.Kind is ServiceFormKind.And or ServiceFormKind.Or && f.Members.Count > 2 ? ValueSet.Of(f.Test) : ValueSet<CosmosServiceTest>.Empty);

        /// <summary>
        /// A form compares a path with a string, which places an interval by the side it lies on, and the service's order
        /// and Calcite's may rank that side apart.
        /// </summary>
        public static Primitive<ServiceForm, bool> StringComparison { get; } = Primitive.Create<ServiceForm, bool>("string-comparison", f =>
            f.Kind == ServiceFormKind.Compare && JsonValues.IsString(f.Literal!));

        /// <summary>A form orders a path against a string, which the service's order and Calcite's may rank apart.</summary>
        public static Primitive<ServiceForm, bool> StringOrdering { get; } = Primitive.Create<ServiceForm, bool>("string-ordering", f =>
            f.Kind == ServiceFormKind.Compare && f.Test is CosmosServiceTest.Less or CosmosServiceTest.LessOrEqual or CosmosServiceTest.Greater or CosmosServiceTest.GreaterOrEqual
            && JsonValues.IsString(f.Literal!));

        /// <summary>A form is a junction of the connective.</summary>
        public static Primitive<ServiceForm, CosmosServiceTest, bool> FormHeads { get; } = Primitive.Create<ServiceForm, CosmosServiceTest, bool>("form-heads", (f, j) =>
            f.Kind is ServiceFormKind.And or ServiceFormKind.Or && f.Test == j);

        /// <summary>A junction is the one form joined with the other, in either order: one is a member, the other the rest.</summary>
        public static Primitive<ServiceForm, ServiceForm, ServiceForm, bool> FormDecomposes { get; } = Primitive.Create<ServiceForm, ServiceForm, ServiceForm, bool>("form-decomposes", (f, l, m) =>
            ServiceForm.Decomposes(f, l, m) || ServiceForm.Decomposes(f, m, l));

    }

}
