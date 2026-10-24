using System;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// An exact form's price, the value <c>bestexact</c>'s <c>Min</c> keeps one of: the form's nodes, the shortest SQL a
    /// static size can honestly claim, and its text, so the order is total and one value survives whatever order rows
    /// arrive in. No registry row ranks a filter form by what it costs the service, so nothing above the size is held.
    /// </summary>
    /// <remarks>
    /// <see cref="None"/> is the one price with no form: no exact form found, greater than every real price, so that a
    /// node's lattice value is complete from the start and nothing reads an absence.
    /// </remarks>
    internal sealed record Priced(int Nodes, ServiceForm? Form) : IComparable<Priced>
    {

        /// <summary>The form a price without one reads as: opaque, answered widest, a failure among it, so no box allows it.</summary>
        static readonly ServiceForm Unpriced = ServiceForm.OpaqueOf("unpriced", ValueSet<string>.Empty);

        /// <summary>No exact form: greater than every real price.</summary>
        public static Priced None { get; } = new(int.MaxValue, null);

        /// <summary>Prices a form by its nodes.</summary>
        public static Priced Of(ServiceForm form) => new(form.Nodes, form);

        /// <summary>The form priced, or the inert sentinel for <see cref="None"/>: total, as a rule body's calls must be.</summary>
        public static ServiceForm FormOf(Priced price) => price.Form ?? Unpriced;

        /// <inheritdoc />
        public int CompareTo(Priced? other)
        {
            if (other is null)
                return 1;

            if ((Form is null) != (other.Form is null))
                return Form is null ? 1 : -1;

            var order = Nodes.CompareTo(other.Nodes);
            return order != 0 || Form is null ? order : Form.CompareTo(other.Form);
        }

    }

}
