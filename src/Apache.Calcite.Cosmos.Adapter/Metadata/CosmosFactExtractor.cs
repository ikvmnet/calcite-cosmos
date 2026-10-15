using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;

using org.apache.calcite.rex;
using org.apache.calcite.sql;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// Reads the facts a predicate establishes, which is the half of the question the query supplies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A container's declared facts are mostly conditional: one path holds a canonical UUID only
    /// <em>when</em> a discriminator property has a particular value, a container commonly holding more
    /// than one kind of document. So a rewrite can only use such a fact once the query has proven the
    /// condition. This is where that proof is read off, and
    /// <see cref="JsonFactTheory.Derive"/> is what turns it into everything that follows.
    /// </para>
    /// <para>
    /// <b>Only conjunctions.</b> A fact is established when it holds of every row the predicate keeps,
    /// and under a disjunction an equality holds of only some of them. So <c>AND</c> is descended into
    /// and nothing else is — the same argument <see cref="CosmosPartitionKeyExtractor"/> makes for
    /// pinning, and the same one <c>CosmosFilterSplitRule</c> makes for weakening.
    /// </para>
    /// <para>
    /// <b>What is read is deliberately narrow.</b> An equality, a disequality, a set membership and a
    /// definedness — the shapes a guard is written in — and a range through a numeric cast, which
    /// bounds a number. Anything unrecognised is skipped, which loses facts and can only lose pushdowns.
    /// </para>
    /// <para>
    /// <b>A comparison says its path is there.</b> Over an absent path a comparison is undefined at the
    /// service and unknown in process, and a filter keeps no row for either, so every row the predicate
    /// keeps has the path. That is stated beside the comparison, and it is what turns two claims no one
    /// value satisfies into a contradiction: without it, a declared enum and a query's value outside it
    /// prove only that the path is absent, which a document satisfies by not having it.
    /// </para>
    /// </remarks>
    public static class CosmosFactExtractor
    {

        /// <summary>
        /// Reads what a predicate proves about the documents it keeps.
        /// </summary>
        /// <param name="condition">The predicate, expressed over <paramref name="fields"/>.</param>
        /// <param name="fields">The ordinal-to-path binding of the filtered input.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <returns>The facts, which may be empty.</returns>
        public static IReadOnlyList<JsonFact> Extract(RexNode? condition, IReadOnlyList<CosmosPath?>? fields, string rootAlias)
        {
            var facts = new List<JsonFact>();

            if (condition is null || fields is null || string.IsNullOrEmpty(rootAlias))
                return facts;

            // An IN arrives as a SEARCH over a Sarg and is expanded the way translation expands it, so
            // what is walked here is the shape the statement would carry.
            RexNode expanded;
            try
            {
                expanded = RexUtil.expandSearch(RexBuilderHolder.Value, null, condition);
            }
            catch (Exception)
            {
                expanded = condition;
            }

            var conjuncts = new List<RexNode>();
            Flatten(expanded, conjuncts);

            foreach (var conjunct in conjuncts)
                Read(conjunct, fields, rootAlias, facts);

            return facts;
        }

        /// <summary>
        /// Flattens nested conjunctions into their top-level conjuncts.
        /// </summary>
        /// <param name="node">The predicate, or part of one.</param>
        /// <param name="conjuncts">Collects what the conjunction is made of.</param>
        static void Flatten(RexNode node, List<RexNode> conjuncts)
        {
            if (node is RexCall call && call.getKind().name() == nameof(SqlKind.__Enum.AND))
            {
                for (var i = 0; i < call.getOperands().size(); i++)
                    Flatten((RexNode)call.getOperands().get(i), conjuncts);

                return;
            }

            conjuncts.Add(node);
        }

        /// <summary>
        /// Reads one conjunct, adding whatever it establishes.
        /// </summary>
        /// <remarks>
        /// A shape not recognised adds nothing, which loses facts and can only lose pushdowns.
        /// </remarks>
        /// <param name="node">One top-level conjunct.</param>
        /// <param name="fields">The ordinal-to-path binding of the filtered input.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <param name="facts">Collects what the conjunct proves.</param>
        static void Read(RexNode node, IReadOnlyList<CosmosPath?> fields, string rootAlias, List<JsonFact> facts)
        {
            if (node is not RexCall call)
                return;

            // By name rather than by ordinal. SqlKind has 355 values and a kind's position
            // among them is not an API: a constant added upstream renumbers everything after
            // it, and a cast from the ordinal would go on compiling while meaning something
            // else. Measured on 1.42, where EQUALS is 76 and CAST is 172.
            switch (call.getKind().name())
            {
                case nameof(SqlKind.__Enum.EQUALS) when call.getOperands().size() == 2:
                    if (TryComparison(call, fields, rootAlias, out var path, out var value, out var exact))
                    {
                        // Text another scalar renders as too proves which documents match and not what
                        // they hold: a stored 5 and a stored "5" both read '5'.
                        if (exact)
                            facts.Add(new JsonFact(path!.Value, new JsonClaim.EqualTo(value)));
                        facts.Add(new JsonFact(path!.Value, new JsonClaim.Present()));
                    }
                    else
                    {
                        ReadWindow(call, fields, rootAlias, facts);
                    }
                    break;

                case nameof(SqlKind.__Enum.GREATER_THAN) or nameof(SqlKind.__Enum.GREATER_THAN_OR_EQUAL)
                    or nameof(SqlKind.__Enum.LESS_THAN) or nameof(SqlKind.__Enum.LESS_THAN_OR_EQUAL) when call.getOperands().size() == 2:
                    if (ReadWindow(call, fields, rootAlias, facts) == false)
                        ReadRange(call, fields, rootAlias, facts);
                    break;

                case nameof(SqlKind.__Enum.NOT_EQUALS) when call.getOperands().size() == 2:
                    // A disequality with text holds of the value whatever else renders as it: a stored
                    // "5" reads '5', and is excluded.
                    if (TryComparison(call, fields, rootAlias, out var excludedPath, out var excluded, out _))
                    {
                        facts.Add(new JsonFact(excludedPath!.Value, new JsonClaim.NotEqualTo(excluded)));
                        facts.Add(new JsonFact(excludedPath!.Value, new JsonClaim.Present()));
                    }
                    break;

                // A value that is not null is a value the path has. The converse does not hold — a
                // stored JSON null is defined — so this proves presence and nothing more.
                case nameof(SqlKind.__Enum.IS_NOT_NULL) when call.getOperands().size() == 1:
                    if (TryPath((RexNode)call.getOperands().get(0), fields, rootAlias) is JsonDocumentPath defined)
                        facts.Add(new JsonFact(defined, new JsonClaim.Present()));
                    break;

                // An expanded IN is a disjunction of equalities, and it bounds the value's domain
                // exactly when every branch is an equality on one path.
                case nameof(SqlKind.__Enum.OR):
                    if (TryDomain(call, fields, rootAlias, out var domainPath, out var domain))
                    {
                        if (domain is not null)
                            facts.Add(new JsonFact(domainPath!.Value, new JsonClaim.OneOf(domain)));
                        facts.Add(new JsonFact(domainPath!.Value, new JsonClaim.Present()));
                    }
                    break;

                default:
                    if (ReferenceEquals(call.getOperator(), CosmosOperators.IsDefined) && call.getOperands().size() == 1)
                        if (TryPath((RexNode)call.getOperands().get(0), fields, rootAlias) is JsonDocumentPath present)
                            facts.Add(new JsonFact(present, new JsonClaim.Present()));

                    break;
            }
        }

        /// <summary>
        /// Reads a comparison between a container-rooted path and a constant, either way round.
        /// </summary>
        internal static bool TryComparison(RexCall call, IReadOnlyList<CosmosPath?> fields, string rootAlias, out JsonDocumentPath? path, out JsonScalar value) =>
            TryComparison(call, fields, rootAlias, out path, out value, out _);

        /// <summary>
        /// Reads a comparison between a container-rooted path and a constant, either way round, and
        /// whether an equality between the two says what the path holds.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A comparison over a rendering is a comparison of text.</b> <c>JSON_VALUE</c> reads a stored
        /// number 5 as the text <c>5</c>, so <c>JSON_VALUE(…) = '5'</c> keeps a document storing the
        /// number as well as one storing the string. Reading it as the string <c>"5"</c> would be a fact
        /// false of the first, and beside a declared integer a contradiction that drops it (F2). So an
        /// equality over a rendering is exact only where no other scalar renders as its text —
        /// <see cref="CosmosRexTranslator.IsUnambiguousTextFor"/>, the test the cast spelling passes
        /// before it is unwrapped.
        /// </para>
        /// <para>
        /// <b>A typed <c>RETURNING</c> is exact because it asserts.</b> Every numeric and boolean one
        /// casts the value read to one Java class and raises on any other, so a row an equality through
        /// it keeps holds the literal and a document holding anything else fails the statement. Except
        /// <c>DECIMAL</c>, which parses: <c>= 2</c> through it keeps a stored <c>"2"</c>, and read as
        /// exact it said the path holds the number 2, which beside a declared string was a contradiction
        /// that rewrote the filter to <c>FALSE</c>. It addresses no path —
        /// <see cref="CosmosRexTranslator.IsParsingJsonValue"/> — so it never resolves here, and
        /// <see cref="ReadWindow"/> reads it as the cast it is: the path is there, and a number there is
        /// near the literal.
        /// </para>
        /// </remarks>
        /// <param name="call">The comparison.</param>
        /// <param name="fields">The ordinal-to-path binding of the filtered input.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <param name="path">On success, the document path.</param>
        /// <param name="value">On success, the value the literal carries.</param>
        /// <param name="exact">On success, whether the path holding the value is what equality means.</param>
        /// <returns><c>true</c> where the two sides were a path and a constant.</returns>
        internal static bool TryComparison(RexCall call, IReadOnlyList<CosmosPath?> fields, string rootAlias, out JsonDocumentPath? path, out JsonScalar value, out bool exact)
        {
            var left = (RexNode)call.getOperands().get(0);
            var right = (RexNode)call.getOperands().get(1);

            return TrySide(left, right, fields, rootAlias, out path, out value, out exact)
                || TrySide(right, left, fields, rootAlias, out path, out value, out exact);
        }

        /// <summary>
        /// Reads one side of a comparison as a container-rooted path and the other as a constant.
        /// </summary>
        /// <param name="pathNode">The side expected to address a path.</param>
        /// <param name="valueNode">The side expected to be a literal.</param>
        /// <param name="fields">The ordinal-to-path binding of the filtered input.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <param name="path">On success, the document path.</param>
        /// <param name="value">On success, the value the literal carries.</param>
        /// <param name="exact">On success, whether equality with the value says what the path holds.</param>
        /// <returns><c>true</c> where the two sides were as expected.</returns>
        static bool TrySide(RexNode pathNode, RexNode valueNode, IReadOnlyList<CosmosPath?> fields, string rootAlias, out JsonDocumentPath? path, out JsonScalar value, out bool exact)
        {
            path = null;
            value = JsonScalar.Null;
            exact = false;

            // A comparison with a promoted VARIANT key coerces its literal to CAST(… AS VARIANT); the
            // box carries the value the fact is about. See CosmosRexTranslator.StripVariantCoercion.
            valueNode = CosmosRexTranslator.StripVariantCoercion(valueNode);

            if (valueNode is not RexLiteral literal)
                return false;

            // A view gives a document path a SQL type by casting it, and a caller filtering on a
            // discriminator is filtering on a view's column. The one cast shape that selects the same
            // documents with the cast dropped is unwrapped, so the fact lands on the path the schema
            // declared rather than on the rendering.
            if (CosmosRexTranslator.TryTextCastOperand(pathNode, valueNode) is RexNode unwrapped)
                pathNode = unwrapped;

            if (TryPath(pathNode, fields, rootAlias) is not JsonDocumentPath resolved)
                return false;

            // Converted once, here, into the scalar a claim holds: the engine boxes an exact 1.5 as a
            // decimal where a schema's reader read a double, and the theory compares JSON values, not
            // boxes (#198). A literal that is no JSON scalar -- a UUID, an instant -- proves nothing.
            try
            {
                if (JsonScalar.TryFrom(CosmosRexTranslator.GetLiteralValue(literal), out value) == false)
                    return false;
            }
            catch (CosmosTranslationException)
            {
                return false;
            }

            path = resolved;
            exact = CosmosRexTranslator.IsTextJsonValue(pathNode) == false
                || value.ToClrValue() is not string text
                || CosmosRexTranslator.IsUnambiguousTextFor(pathNode, text);
            return true;
        }

        /// <summary>
        /// Reads a comparison through a numeric cast, or through <c>RETURNING DECIMAL</c>, as the window it
        /// puts the raw value in, or answers <c>false</c> where it is not one.
        /// </summary>
        /// <remarks>
        /// The window binds numbers only — the cast converts a stored string too, so <c>&gt; 500</c> keeps
        /// a document storing <c>"600"</c> — and a bound says exactly that. See
        /// <see cref="CosmosNumericWindow"/> for why each side holds.
        /// </remarks>
        static bool ReadWindow(RexCall call, IReadOnlyList<CosmosPath?> fields, string rootAlias, List<JsonFact> facts)
        {
            if (CosmosNumericWindow.TryRead(call, RexBuilderHolder.Value, out var window) == false)
                return false;

            if (TryPath(window.Value, fields, rootAlias) is not JsonDocumentPath path)
                return false;

            facts.Add(new JsonFact(path, new JsonClaim.Present()));

            if (window.Above is java.math.BigDecimal above && TryDecimal(above) is decimal lower)
                facts.Add(new JsonFact(path, new JsonClaim.LowerBound(lower, Exclusive: true)));

            if (window.Below is java.math.BigDecimal below && TryDecimal(below) is decimal upper)
                facts.Add(new JsonFact(path, new JsonClaim.UpperBound(upper, Exclusive: true)));

            return true;
        }

        /// <summary>
        /// Reads an order comparison between a path and a constant as what it says of every row it keeps:
        /// that the path is there.
        /// </summary>
        /// <remarks>
        /// Not a bound. Through a numeric cast the bound is the window's, which was measured; over a path
        /// read as some other type the comparison is made after a conversion or between values whose
        /// ordering across types is Calcite's, and neither has been measured to keep the stored number on
        /// the comparison's side of the constant.
        /// </remarks>
        static void ReadRange(RexCall call, IReadOnlyList<CosmosPath?> fields, string rootAlias, List<JsonFact> facts)
        {
            if (TryComparison(call, fields, rootAlias, out var path, out _, out _))
                facts.Add(new JsonFact(path!.Value, new JsonClaim.Present()));
        }

        /// <summary>
        /// Converts a bound to a decimal, or answers <c>null</c> where no decimal holds it exactly: a bound
        /// rounded toward the side it binds would claim more than the comparison does.
        /// </summary>
        static decimal? TryDecimal(java.math.BigDecimal bound)
        {
            var text = bound.toPlainString();
            if (decimal.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) == false)
                return null;

            return new java.math.BigDecimal(value.ToString(System.Globalization.CultureInfo.InvariantCulture)).compareTo(bound) == 0 ? value : null;
        }

        /// <summary>
        /// Reads a disjunction as a domain, where every branch is an equality on the same path.
        /// </summary>
        /// <remarks>
        /// One path, because a disjunction over two of them constrains neither: <c>a = 1 OR b = 2</c>
        /// leaves a row free to satisfy either. Duplicates collapse, <c>IN ('a', 'a')</c> naming one
        /// value. Where a branch compares text another scalar renders as, the path is there and the
        /// domain is not known, and the domain is <c>null</c>.
        /// </remarks>
        static bool TryDomain(RexCall call, IReadOnlyList<CosmosPath?> fields, string rootAlias, out JsonDocumentPath? path, out IReadOnlyList<JsonScalar>? domain)
        {
            path = null;
            domain = null;

            var values = new List<JsonScalar>();
            var known = true;

            for (var i = 0; i < call.getOperands().size(); i++)
            {
                if (call.getOperands().get(i) is not RexCall branch ||
                    branch.getKind().name() != nameof(SqlKind.__Enum.EQUALS) ||
                    branch.getOperands().size() != 2)
                    return false;

                if (TryComparison(branch, fields, rootAlias, out var branchPath, out var value, out var exact) == false)
                    return false;

                known &= exact;

                if (path is null)
                    path = branchPath;
                else if (path.Equals(branchPath) == false)
                    return false;

                if (JsonClaim.OneOf.Contains(values, value) == false)
                    values.Add(value);
            }

            domain = known ? values : null;
            return path is not null && values.Count > 0;
        }

        /// <summary>
        /// Resolves a node to the document path it addresses, or <c>null</c>.
        /// </summary>
        static JsonDocumentPath? TryPath(RexNode node, IReadOnlyList<CosmosPath?> fields, string rootAlias)
        {
            // Resolution reuses the translator so the accepted path forms are the ones a statement
            // would address. Parameters are discarded; only the shape matters.
            var translator = new CosmosRexTranslator(RexBuilderHolder.Value, fields, new CosmosParameterList());

            if (translator.TryResolvePath(node, out var path) == false || path is null)
                return null;

            // A path rooted at a traversal alias addresses an element of an array, not the document
            // the schema describes.
            if (string.Equals(path.Alias, rootAlias, StringComparison.Ordinal) == false)
                return null;

            return CosmosDocumentPaths.From(path);
        }

        /// <summary>
        /// A builder used only for resolution and for expanding a search, which constructs nothing the
        /// statement carries.
        /// </summary>
        static class RexBuilderHolder
        {

            internal static readonly RexBuilder Value = new(new org.apache.calcite.jdbc.JavaTypeFactoryImpl());

        }

    }

}
