using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// An exact form's price, the value <c>bestexact</c>'s <c>Min</c> keeps one of: the form's nodes, the shortest SQL a
    /// static size can honestly claim; then its tests' weights (<see cref="Weights"/>, B1); then its groups, the parentheses its junctions print, so that of two forms of one
    /// size the flatter is kept, a junction S's regroup rule flattens never kept over what it flattens to (G4); and its
    /// text, so the order is total and one value survives whatever order rows arrive in. No registry row ranks a filter
    /// form by what it costs the service, so nothing above the size is held.
    /// </summary>
    /// <remarks>
    /// <see cref="None"/> is the one price with no form: no exact form found, greater than every real price, so that a
    /// node's lattice value is complete from the start and nothing reads an absence.
    /// </remarks>
    internal sealed record Priced(int Nodes, ServiceForm? Form) : IComparable<Priced>
    {

        /// <summary>The form a price without one reads as: opaque, answered widest, a failure among it, so no box allows it.</summary>
        static readonly ServiceForm Unpriced = ServiceForm.OpaqueOf("unpriced", ValueSet<string>.Empty);

        /// <summary>
        /// A weight per test a <c>LIKE</c> may be sent as (B1), each template proposing its own: of two forms of one size, the
        /// one asking the narrowest test that says its pattern is kept, <c>STARTSWITH</c>, then <c>ENDSWITH</c>, <c>CONTAINS</c>
        /// and <c>LIKE</c>, an equality and every other test weighing nothing, as the pattern was written before each test
        /// was a template. No row measures what each costs the service, so this orders forms the gate holds alike.
        /// </summary>
        static readonly IReadOnlyDictionary<CosmosServiceTest, int> Weights = new Dictionary<CosmosServiceTest, int>
        {
            [CosmosServiceTest.StartsWith] = 1,
            [CosmosServiceTest.EndsWith] = 2,
            [CosmosServiceTest.Contains] = 3,
            [CosmosServiceTest.Like] = 4,
        };

        /// <summary>A form's tests' weights, each affix's (<see cref="Weights"/>).</summary>
        static int WeightOf(ServiceForm form) => form.Subforms.Where(f => f.Kind == ServiceFormKind.Affix).Sum(f => Weights.GetValueOrDefault(f.Test));

        /// <summary>No exact form: greater than every real price.</summary>
        public static Priced None { get; } = new(int.MaxValue, null);

        /// <summary>Prices a form by its nodes.</summary>
        public static Priced Of(ServiceForm form) => new(form.Nodes, form);

        /// <summary>The form priced, or the inert sentinel for <see cref="None"/>: total, as a rule body's calls must be.</summary>
        public static ServiceForm FormOf(Priced price) => price.Form ?? Unpriced;

        /// <summary>
        /// Of two forms the one priced lower (<see cref="CompareTo"/>), as <c>bestexact</c>'s <c>Min</c> keeps of two prices:
        /// what a node's weakening is kept by, so that one written as its parts stand and the regrouping or fold S's rules
        /// rewrite it to leave the second, fewer nodes or as many in fewer groups (G4). Two forms of one price are one form.
        /// </summary>
        public static ValueLattice<ServiceForm> Cheapest { get; } = new("cheapest", (a, b) => Of(a).CompareTo(Of(b)) <= 0 ? a : b);

        /// <inheritdoc />
        public int CompareTo(Priced? other)
        {
            if (other is null)
                return 1;

            if ((Form is null) != (other.Form is null))
                return Form is null ? 1 : -1;

            var order = Nodes.CompareTo(other.Nodes);
            if (order != 0 || Form is null)
                return order;

            if (WeightOf(Form).CompareTo(WeightOf(other.Form!)) is var weight and not 0)
                return weight;

            return Form.Groups.CompareTo(other.Form!.Groups) is var groups and not 0 ? groups : Form.CompareTo(other.Form);
        }

    }

}
