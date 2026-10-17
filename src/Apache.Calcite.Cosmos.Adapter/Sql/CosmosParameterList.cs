using System;
using System.Collections.Generic;
using System.Globalization;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// Accumulates the parameters bound by a query under construction, handing out names as
    /// values are added.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Values that could originate from user input are bound rather than inlined, so that the
    /// generated statement text never varies with the data. Planner-generated constants such as
    /// fetch counts are inlined instead — see <see cref="CosmosSql.WriteLiteral"/>.
    /// </para>
    /// <para>
    /// <b>And the comparisons a bound value decides.</b> A comparison of a path with a parameter is
    /// exact at the service only where the value is an anchor (<see cref="Metadata.CosmosOrderAnchor"/>),
    /// which is known when the statement runs and not before. The translator writes it as it is and
    /// records it here, beside the parameter it names, so that the reader can tell from the value
    /// whether to read the statement or its fallback — see <see cref="CosmosOrderComparison"/>. They
    /// are kept with the parameters because they are written with them, by whichever translator the
    /// statement's clauses are rendered through.
    /// </para>
    /// </remarks>
    public sealed class CosmosParameterList
    {

        readonly List<CosmosParameter> _parameters = new();
        readonly List<CosmosOrderComparison> _comparisons = new();
        readonly string _prefix;

        /// <summary>
        /// Initializes a new instance using the default <c>@p</c> name prefix.
        /// </summary>
        public CosmosParameterList() :
            this("@p")
        {

        }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="prefix">The name prefix, including the leading <c>@</c>. Parameters are named by appending an ordinal.</param>
        /// <exception cref="ArgumentException"><paramref name="prefix"/> is <c>null</c>, empty, or does not begin with <c>@</c>.</exception>
        public CosmosParameterList(string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                throw new ArgumentException($"'{nameof(prefix)}' cannot be null or empty.", nameof(prefix));
            if (prefix[0] != '@')
                throw new ArgumentException($"'{nameof(prefix)}' must begin with '@'.", nameof(prefix));

            _prefix = prefix;
        }

        /// <summary>
        /// Gets the parameters bound so far, in the order they were added.
        /// </summary>
        public IReadOnlyList<CosmosParameter> Parameters => _parameters;

        /// <summary>
        /// Gets the number of parameters bound so far.
        /// </summary>
        public int Count => _parameters.Count;

        /// <summary>
        /// Gets the comparisons written so far whose exactness the value of a parameter decides.
        /// </summary>
        public IReadOnlyList<CosmosOrderComparison> Comparisons => _comparisons;

        /// <summary>
        /// Binds <paramref name="value"/> and returns the name to emit in its place.
        /// </summary>
        /// <param name="value">The value to bind.</param>
        /// <returns>The parameter name, including the leading <c>@</c>.</returns>
        public string Add(object? value)
        {
            var name = _prefix + _parameters.Count.ToString(CultureInfo.InvariantCulture);
            _parameters.Add(new CosmosParameter(name, value));
            return name;
        }

        /// <summary>
        /// Records a comparison written into the statement whose exactness a parameter's value decides.
        /// </summary>
        /// <param name="comparison">The comparison.</param>
        /// <exception cref="ArgumentNullException"><paramref name="comparison"/> is <c>null</c>.</exception>
        public void Recheck(CosmosOrderComparison comparison)
        {
            _comparisons.Add(comparison ?? throw new ArgumentNullException(nameof(comparison)));
        }

    }

}
