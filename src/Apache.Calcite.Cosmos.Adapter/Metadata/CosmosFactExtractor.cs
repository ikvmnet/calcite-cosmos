using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Sql;

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
    /// <see cref="CosmosFactTheory.Derive"/> is what turns it into everything that follows.
    /// </para>
    /// <para>
    /// <b>Only conjunctions.</b> A fact is established when it holds of every row the predicate keeps,
    /// and under a disjunction an equality holds of only some of them. So <c>AND</c> is descended into
    /// and nothing else is — the same argument <see cref="CosmosPartitionKeyExtractor"/> makes for
    /// pinning, and the same one <c>CosmosFilterSplitRule</c> makes for weakening.
    /// </para>
    /// <para>
    /// <b>What is read is deliberately narrow.</b> An equality, a disequality, a set membership and a
    /// definedness — the shapes a guard is written in. A range tells the theory nothing it can use,
    /// because no claim in the model is about an interval. Anything unrecognised is skipped, which
    /// loses facts and can only lose pushdowns.
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
        public static IReadOnlyList<CosmosFact> Extract(RexNode? condition, IReadOnlyList<CosmosPath?>? fields, string rootAlias)
        {
            var facts = new List<CosmosFact>();

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

        static void Flatten(RexNode node, List<RexNode> conjuncts)
        {
            if (node is RexCall call && (SqlKind.__Enum)call.getKind().ordinal() == SqlKind.__Enum.AND)
            {
                for (var i = 0; i < call.getOperands().size(); i++)
                    Flatten((RexNode)call.getOperands().get(i), conjuncts);

                return;
            }

            conjuncts.Add(node);
        }

        static void Read(RexNode node, IReadOnlyList<CosmosPath?> fields, string rootAlias, List<CosmosFact> facts)
        {
            if (node is not RexCall call)
                return;

            switch ((SqlKind.__Enum)call.getKind().ordinal())
            {
                case SqlKind.__Enum.EQUALS when call.getOperands().size() == 2:
                    if (TryComparison(call, fields, rootAlias, out var path, out var value))
                        facts.Add(new CosmosFact(path!, new CosmosClaim.EqualTo(value)));
                    break;

                case SqlKind.__Enum.NOT_EQUALS when call.getOperands().size() == 2:
                    if (TryComparison(call, fields, rootAlias, out var excludedPath, out var excluded))
                        facts.Add(new CosmosFact(excludedPath!, new CosmosClaim.NotEqualTo(excluded)));
                    break;

                // A value that is not null is a value the path has. The converse does not hold — a
                // stored JSON null is defined — so this proves presence and nothing more.
                case SqlKind.__Enum.IS_NOT_NULL when call.getOperands().size() == 1:
                    if (TryPath((RexNode)call.getOperands().get(0), fields, rootAlias) is CosmosDocumentPath defined)
                        facts.Add(new CosmosFact(defined, new CosmosClaim.Present()));
                    break;

                // An expanded IN is a disjunction of equalities, and it bounds the value's domain
                // exactly when every branch is an equality on one path.
                case SqlKind.__Enum.OR:
                    if (TryDomain(call, fields, rootAlias, out var domainPath, out var domain))
                        facts.Add(new CosmosFact(domainPath!, new CosmosClaim.OneOf(domain!)));
                    break;

                default:
                    if (ReferenceEquals(call.getOperator(), CosmosOperators.IsDefined) && call.getOperands().size() == 1)
                        if (TryPath((RexNode)call.getOperands().get(0), fields, rootAlias) is CosmosDocumentPath present)
                            facts.Add(new CosmosFact(present, new CosmosClaim.Present()));

                    break;
            }
        }

        /// <summary>
        /// Reads a comparison between a container-rooted path and a constant, either way round.
        /// </summary>
        static bool TryComparison(RexCall call, IReadOnlyList<CosmosPath?> fields, string rootAlias, out CosmosDocumentPath? path, out object? value)
        {
            var left = (RexNode)call.getOperands().get(0);
            var right = (RexNode)call.getOperands().get(1);

            return TrySide(left, right, fields, rootAlias, out path, out value)
                || TrySide(right, left, fields, rootAlias, out path, out value);
        }

        static bool TrySide(RexNode pathNode, RexNode valueNode, IReadOnlyList<CosmosPath?> fields, string rootAlias, out CosmosDocumentPath? path, out object? value)
        {
            path = null;
            value = null;

            if (valueNode is not RexLiteral literal)
                return false;

            // A view gives a document path a SQL type by casting it, and a caller filtering on a
            // discriminator is filtering on a view's column. The one cast shape that selects the same
            // documents with the cast dropped is unwrapped, so the fact lands on the path the schema
            // declared rather than on the rendering.
            if (CosmosRexTranslator.TryTextCastOperand(pathNode, valueNode) is RexNode unwrapped)
                pathNode = unwrapped;

            if (TryPath(pathNode, fields, rootAlias) is not CosmosDocumentPath resolved)
                return false;

            try
            {
                value = CosmosRexTranslator.GetLiteralValue(literal);
            }
            catch (CosmosTranslationException)
            {
                return false;
            }

            path = resolved;
            return true;
        }

        /// <summary>
        /// Reads a disjunction as a domain, where every branch is an equality on the same path.
        /// </summary>
        /// <remarks>
        /// One path, because a disjunction over two of them constrains neither: <c>a = 1 OR b = 2</c>
        /// leaves a row free to satisfy either. Duplicates collapse, <c>IN ('a', 'a')</c> naming one
        /// value.
        /// </remarks>
        static bool TryDomain(RexCall call, IReadOnlyList<CosmosPath?> fields, string rootAlias, out CosmosDocumentPath? path, out IReadOnlyList<object?>? domain)
        {
            path = null;
            domain = null;

            var values = new List<object?>();

            for (var i = 0; i < call.getOperands().size(); i++)
            {
                if (call.getOperands().get(i) is not RexCall branch ||
                    (SqlKind.__Enum)branch.getKind().ordinal() != SqlKind.__Enum.EQUALS ||
                    branch.getOperands().size() != 2)
                    return false;

                if (TryComparison(branch, fields, rootAlias, out var branchPath, out var value) == false)
                    return false;

                if (path is null)
                    path = branchPath;
                else if (path.Equals(branchPath) == false)
                    return false;

                if (CosmosClaim.OneOf.Contains(values, value) == false)
                    values.Add(value);
            }

            domain = values;
            return path is not null && values.Count > 0;
        }

        /// <summary>
        /// Resolves a node to the document path it addresses, or <c>null</c>.
        /// </summary>
        static CosmosDocumentPath? TryPath(RexNode node, IReadOnlyList<CosmosPath?> fields, string rootAlias)
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

            return CosmosDocumentPath.From(path);
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
