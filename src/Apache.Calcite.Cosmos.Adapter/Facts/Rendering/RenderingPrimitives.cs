using System.Collections.Generic;
using System.Linq;

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

        /// <summary>Two forms are junctions of one connective.</summary>
        public static Primitive<ServiceForm, ServiceForm, bool> FormJoins { get; } = Primitive.Create<ServiceForm, ServiceForm, bool>("form-joins", (f, g) =>
            f.Kind is ServiceFormKind.And or ServiceFormKind.Or && g.Kind == f.Kind);

        /// <summary>A form is a member of another as written.</summary>
        public static Primitive<ServiceForm, ServiceForm, bool> FormHolds { get; } = Primitive.Create<ServiceForm, ServiceForm, bool>("form-holds", (f, h) => f.Members.Contains(h));

        /// <summary>A junction is the one form joined with the other, in either order: one is a part, the other the rest.</summary>
        public static Primitive<ServiceForm, ServiceForm, ServiceForm, bool> FormDecomposes { get; } = Primitive.Create<ServiceForm, ServiceForm, ServiceForm, bool>("form-decomposes", (f, l, m) =>
            ServiceForm.Decomposes(f, l, m) || ServiceForm.Decomposes(f, m, l));

        /// <summary>A form's flat parts, as a set, are the two forms': each side's own where it is a junction of the form's connective.</summary>
        public static Primitive<ServiceForm, ServiceForm, ServiceForm, bool> FormIsJoinOf { get; } = Primitive.Create<ServiceForm, ServiceForm, ServiceForm, bool>("form-is-join-of", (h, l, m) =>
            FlatParts(h, h.Kind).ToHashSet().SetEquals(FlatParts(l, h.Kind).Concat(FlatParts(m, h.Kind))));

        /// <summary>A price is of a form: an exact form was found.</summary>
        public static Primitive<Priced, bool> PricedIsReal { get; } = Primitive.Create<Priced, bool>("priced-is-real", p => p.Form is not null);

        /// <summary>A node's best is real and one of its implied forms.</summary>
        public static Primitive<Priced, ValueSet<ServiceForm>, bool> BestIsImplied { get; } = Primitive.Create<Priced, ValueSet<ServiceForm>, bool>("best-is-implied", (b, s) =>
            b.Form is { } f && s.Contains(f));

        /// <summary>A node has no best, or one none of its implied forms is: the first's negation.</summary>
        public static Primitive<Priced, ValueSet<ServiceForm>, bool> BestIsNotImplied { get; } = Primitive.Create<Priced, ValueSet<ServiceForm>, bool>("best-is-not-implied", (b, s) =>
            b.Form is not { } f || s.Contains(f) == false);

        /// <summary>
        /// A form may be sent: no literal holds an unpaired surrogate, which the SDK refuses to send on either target
        /// (<c>Service.LoneSurrogateParameterIsNeverSent</c>); and no string ordering's literal holds a character of a
        /// parting band, so that nothing S sends is one bound of a range the two orders rank apart.
        /// </summary>
        public static Primitive<ServiceForm, ValueSet<string>, bool> Sendable { get; } = Primitive.Create<ServiceForm, ValueSet<string>, bool>("sendable", (f, bands) =>
            f.Literals.All(l => JsonValues.IsString(l) == false || JsonValues.IsEncodable(JsonValues.StringOf(l))) && f.Subforms.All(g => PartsBands(g, bands) == false));

        /// <summary>The Rex builder writes a form over the atom's reader: every comparison, membership and test of it is in the grammar.</summary>
        public static Primitive<ServiceForm, PredicateAtom, RenderingRexGrammar.Value, bool> RexWritable { get; } =
            Primitive.Create<ServiceForm, PredicateAtom, RenderingRexGrammar.Value, bool>("rex-writable", (f, a, g) => g.Writes(f, a.Reader));

        static IEnumerable<ServiceForm> FlatParts(ServiceForm form, ServiceFormKind kind) =>
            form.Kind == kind && kind is ServiceFormKind.And or ServiceFormKind.Or ? form.Flat : [form];

        static bool PartsBands(ServiceForm form, ValueSet<string> bands) => StringOrdering.Function(form) && JsonValues.CharactersOf(form.Literal!).Overlaps(bands);

    }

}
