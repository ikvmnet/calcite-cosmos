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
    /// whether to read the statement or its fallback — see <see cref="CosmosOrderComparison"/> — and
    /// the conjunct each sits in, which the reader rechecks whole (<see cref="CosmosRecheck"/>). They
    /// are kept with the parameters because they are written with them, by whichever translator the
    /// statement's clauses are rendered through.
    /// </para>
    /// </remarks>
    public sealed class CosmosParameterList
    {

        readonly List<CosmosParameter> _parameters = new();
        readonly List<CosmosOrderComparison> _comparisons = new();
        readonly List<CosmosRecheck> _rechecks = new();
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
        /// Gets the conjuncts that hold those comparisons, as the reader rechecks them, each naming its comparisons by
        /// their index in <see cref="Comparisons"/>.
        /// </summary>
        public IReadOnlyList<CosmosRecheck> Rechecks => _rechecks;

        /// <summary>
        /// Binds <paramref name="value"/> and returns the name to emit in its place.
        /// </summary>
        /// <remarks>
        /// <b>Every value a statement carries passes here, and one the SDK cannot send is refused.</b> A string holding an
        /// unpaired surrogate is refused by the SDK's UTF-8 encoder while it writes the statement, on either target, where
        /// Calcite answers it (<c>Service.LoneSurrogateParameterIsNeverSent</c>); only a <c>U&amp;</c> literal holds one,
        /// inside <c>POSITION</c> or <c>REPLACE</c>, or a constant computed from one. Refused here, the expression that
        /// would have sent it stays in process, wherever in the statement it stood (<see cref="CosmosStringFunctions.IsEncodable"/>).
        /// </remarks>
        /// <param name="value">The value to bind.</param>
        /// <returns>The parameter name, including the leading <c>@</c>.</returns>
        /// <exception cref="CosmosTranslationException">The value holds a lone surrogate.</exception>
        public string Add(object? value)
        {
            if (CosmosStringFunctions.IsEncodable(value) == false)
                throw new CosmosTranslationException("A value holding a lone surrogate is refused by the SDK's UTF-8 encoder before the statement is sent, so it is computed in process.");

            var name = _prefix + _parameters.Count.ToString(CultureInfo.InvariantCulture);
            _parameters.Add(new CosmosParameter(name, value));
            return name;
        }

        /// <summary>
        /// Records a comparison written into the statement whose exactness a parameter's value decides.
        /// </summary>
        /// <param name="comparison">The comparison.</param>
        /// <returns>Its index in <see cref="Comparisons"/>, by which a conjunct names it.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="comparison"/> is <c>null</c>.</exception>
        public int Recheck(CosmosOrderComparison comparison)
        {
            _comparisons.Add(comparison ?? throw new ArgumentNullException(nameof(comparison)));
            return _comparisons.Count - 1;
        }

        /// <summary>
        /// Records a conjunct holding comparisons <see cref="Recheck(CosmosOrderComparison)"/> recorded, as the reader
        /// rechecks it.
        /// </summary>
        /// <param name="conjunct">The conjunct.</param>
        /// <exception cref="ArgumentNullException"><paramref name="conjunct"/> is <c>null</c>.</exception>
        public void Recheck(CosmosRecheck conjunct)
        {
            _rechecks.Add(conjunct ?? throw new ArgumentNullException(nameof(conjunct)));
        }

    }

}
