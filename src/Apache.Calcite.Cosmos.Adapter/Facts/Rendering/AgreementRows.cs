using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// What a position keeps, as data: the boxes a class of a cell must lie in for each verdict, the boxes in which it
    /// definitely refutes one, and the space of classes they range over. S's rules derive which classes are allowed and
    /// which refute from these rows; a brute-force test holds them to the definition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The definition.</b> At <see cref="RenderPosition.Pos"/> Calcite keeps K = {T} and drops N = {F, U}; the service
    /// keeps Ks = {t} and drops Ns = {f, u}. At <see cref="RenderPosition.Neg"/> K = {F}, N = {T, U}, Ks = {f}, Ns = {t, u}.
    /// A cell's class is a set of each: any pair of them may meet on some document of the cell. <see cref="Verdict.Equal"/>
    /// holds on it where every pair keeps alike, <see cref="Verdict.Implies"/> where the service keeps every row Calcite
    /// keeps.
    /// </para>
    /// <para>
    /// <b>Decision 2.</b> A row Calcite raises on fails its query, so nothing written for it is wrong: an allowing box tests
    /// Calcite's truths with the raise set aside (<c>tolerated</c>), and a cell it raises on alone allows anything but a
    /// failure. A failure of the service fails the statement, so no allowing box admits <c>E</c>, and one where Calcite
    /// answers refutes. A refuting box tests Calcite's truths as they are: a cell that may raise refutes nothing.
    /// </para>
    /// </remarks>
    internal static class AgreementRows
    {

        const Truths T = Truths.True;
        const Truths F = Truths.False;
        const Truths U = Truths.Unknown;
        const ServiceOutcomes t = ServiceOutcomes.True;
        const ServiceOutcomes f = ServiceOutcomes.False;
        const ServiceOutcomes u = ServiceOutcomes.Undefined;
        const ServiceOutcomes E = ServiceOutcomes.Fails;

        /// <summary>Every class: each non-empty set of Calcite's truths beside each non-empty set of outcomes, 225 in all.</summary>
        public static ValueSet<CellClass> Classes { get; } = ValueSet.From(
            from c in Enumerable.Range(1, 15) from s in Enumerable.Range(1, 15) select new CellClass((Truths)c, (ServiceOutcomes)s));

        /// <summary>The boxes a class must lie in for a verdict to hold on its cells, at each position.</summary>
        public static IReadOnlyList<(RenderPosition, Verdict, Box)> Allowing { get; } =
        [
            .. Allowed(RenderPosition.Pos, T, F | U, t, f | u, "Service.BarePathKeepsOnlyTrue", "Service.ConnectivesReadNonBooleansAsUnknown"),
            .. Allowed(RenderPosition.Neg, F, T | U, f, t | u, "Service.NotOverAPathKeepsOnlyFalse", "Service.NotOverUndefinedIsUndefined"),
        ];

        /// <summary>The boxes a class lies in where every pair on its cells parts the two sides for a verdict.</summary>
        public static IReadOnlyList<(RenderPosition, Verdict, Box)> Refuting { get; } =
        [
            .. Refuted(RenderPosition.Pos, T, F | U, t, f | u),
            .. Refuted(RenderPosition.Neg, F, T | U, f, t | u),
        ];

        static IEnumerable<(RenderPosition, Verdict, Box)> Allowed(RenderPosition at, Truths k, Truths n, ServiceOutcomes ks, ServiceOutcomes ns, string kept, string dropped)
        {
            // Both keep every row; both drop every row; Calcite raises on every row, so anything but a failure refines it.
            yield return (at, Verdict.Equal, new Box(k, ks, ValueSet.Of(kept)));
            yield return (at, Verdict.Equal, new Box(n, ns, ValueSet.Of(dropped)));
            yield return (at, Verdict.Equal, new Box(Truths.None, t | f | u, ValueSet<string>.Empty));

            // The service keeps every row; Calcite keeps none; Calcite raises on every row.
            yield return (at, Verdict.Implies, new Box(T | F | U, ks, ValueSet<string>.Empty));
            yield return (at, Verdict.Implies, new Box(n, t | f | u, ValueSet<string>.Empty));
            yield return (at, Verdict.Implies, new Box(Truths.None, t | f | u, ValueSet<string>.Empty));
        }

        static IEnumerable<(RenderPosition, Verdict, Box)> Refuted(RenderPosition at, Truths k, Truths n, ServiceOutcomes ks, ServiceOutcomes ns)
        {
            foreach (var verdict in new[] { Verdict.Equal, Verdict.Implies })
            {
                // Calcite keeps every row and the service none; the service fails where Calcite answers.
                yield return (at, verdict, new Box(k, ns | E, ValueSet<string>.Empty));
                yield return (at, verdict, new Box(T | F | U, E, ValueSet<string>.Empty));
            }

            // Calcite drops every row and the service keeps every row, or fails.
            yield return (at, Verdict.Equal, new Box(n, ks | E, ValueSet<string>.Empty));
        }

    }

}
