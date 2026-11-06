using System;
using System.Collections.Generic;
using System.Globalization;
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

        /// <summary>Whether a function key's answer on a target is other than Calcite's (h10): what disagrees in a set serving it.</summary>
        public static Primitive<CosmosFunctionAnswer, bool> IsNotSame { get; } = Primitive.Create<CosmosFunctionAnswer, bool>("is-not-same", a => a != CosmosFunctionAnswer.Same);

        /// <summary>Every truth of the first set is in the second.</summary>
        public static Primitive<Truths, Truths, bool> TruthsWithin { get; } = Primitive.Create<Truths, Truths, bool>("truths-within", (a, b) => (a & ~b) == Truths.None);

        /// <summary>Every outcome of the first set is in the second.</summary>
        public static Primitive<ServiceOutcomes, ServiceOutcomes, bool> OutcomesWithin { get; } =
            Primitive.Create<ServiceOutcomes, ServiceOutcomes, bool>("outcomes-within", (a, b) => (a & ~b) == ServiceOutcomes.None);

        /// <summary>A set of classes holds one: the existential over cells.</summary>
        public static Primitive<ValueSet<CellClass>, bool> SetNonempty { get; } = Primitive.Create<ValueSet<CellClass>, bool>("set-nonempty", s => s.IsEmpty == false);

        /// <summary>A form tests one path, and has no parts.</summary>
        public static Primitive<ServiceForm, bool> FormIsLeaf { get; } = Primitive.Create<ServiceForm, bool>("form-is-leaf", f => f.Members.Count == 0);

        /// <summary>A form is a connective over parts: never a ternary, which S composes from its parts' agreements and never evaluates.</summary>
        public static Primitive<ServiceForm, bool> FormIsCompound { get; } = Primitive.Create<ServiceForm, bool>("form-is-compound", f => f.Members.Count > 0 && f.Kind != ServiceFormKind.Choose);

        /// <summary>Two prices name one form: one form exact at two positions.</summary>
        public static Primitive<Priced, Priced, bool> SameForm { get; } = Primitive.Create<Priced, Priced, bool>("same-form", (a, b) => a.Form is { } f && f.Equals(b.Form));

        /// <summary>No class of a set is one Calcite answers UNKNOWN on: the universal over an atom's cells, two-valuedness.</summary>
        public static Primitive<ValueSet<CellClass>, bool> NoCalciteUnknown { get; } = Primitive.Create<ValueSet<CellClass>, bool>("no-calcite-unknown", s =>
            s.AsEnumerable().All(k => (k.Calcite & Truths.Unknown) == Truths.None));

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
        /// The stored spelling splits an affix's pattern (<see cref="AffixPattern.StoredAlike"/>, the writer's spelling read of
        /// it): an affix cut of a range, made of strings as written, does not say what a matcher of that spelling matches.
        /// </summary>
        public static Primitive<ServiceForm, bool> SpellingSplits { get; } = Primitive.Create<ServiceForm, bool>("spelling-splits", f =>
            f.Kind == ServiceFormKind.Affix && f.Pattern is { } pattern && AffixPattern.StoredAlike(pattern) == false);

        /// <summary>The Rex builder writes a form over the atom's reader: every comparison, membership and test of it is in the grammar.</summary>
        public static Primitive<ServiceForm, PredicateAtom, RenderingRexGrammar.Value, bool> RexWritable { get; } =
            Primitive.Create<ServiceForm, PredicateAtom, RenderingRexGrammar.Value, bool>("rex-writable", (f, a, g) => g.Writes(f, a.Reader));

        /// <summary>
        /// A cast to <c>DECIMAL(p, s)</c> holds a number's whole digits (G8): it is zero, or its digits before the point number
        /// no more than <c>p - s</c>. Calcite raises on a cast they do not fit (<c>Calcite.ReturningDecimalParsesTheRendering</c>,
        /// <c>DECIMAL(19, 18) = 50</c>), so a number read through one that does not denotes nothing.
        /// </summary>
        public static Primitive<string, long, long, bool> WholeDigitsFit { get; } = Primitive.Create<string, long, long, bool>("whole-digits-fit", (c, p, s) =>
            JsonValues.TryNumberOf(c, out var x) && (x == 0 || WholeDigits(x) <= p - s));

        /// <summary>A number has no more places than a scale: a cast to it keeps the number as it is.</summary>
        public static Primitive<string, long, bool> PlacesWithin { get; } = Primitive.Create<string, long, bool>("places-within", (c, s) =>
            JsonValues.TryNumberOf(c, out var x) && Places(x) <= s);

        /// <summary>A number has more places than a scale: a cast to it truncates the number toward zero, by less than one.</summary>
        public static Primitive<string, long, bool> PlacesBeyond { get; } = Primitive.Create<string, long, bool>("places-beyond", (c, s) =>
            JsonValues.TryNumberOf(c, out var x) && Places(x) > s);

        /// <summary>
        /// The digits a number has before its point, as a <c>BigDecimal</c>'s precision less its scale counts them: the
        /// whole part's, and below one less a digit for each zero after the point, so <c>0.05</c> has -1. Zero has none.
        /// </summary>
        static int WholeDigits(decimal value)
        {
            var x = Math.Abs(value);
            if (x >= 1)
                return decimal.Truncate(x).ToString(CultureInfo.InvariantCulture).Length;

            var digits = 0;
            for (; x != 0 && x < 0.1m; x *= 10)
                digits--;

            return digits;
        }

        /// <summary>The places a number has after its point, its trailing zeros apart.</summary>
        static int Places(decimal value) => (value / 1.000000000000000000000000000000000m).Scale;

        static IEnumerable<ServiceForm> FlatParts(ServiceForm form, ServiceFormKind kind) =>
            form.Kind == kind && kind is ServiceFormKind.And or ServiceFormKind.Or ? form.Flat : [form];

    }

}
