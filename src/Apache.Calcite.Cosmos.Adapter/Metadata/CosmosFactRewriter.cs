using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// Rewrites a predicate into the one the service can evaluate, where the container's declared
    /// facts say the two select the same documents.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Cosmos has strings where Calcite has <c>UUID</c>.</b> A comparison against a UUID therefore
    /// has no form the service can evaluate, and the container is read whole. What closes the gap is a
    /// proof about the <em>stored</em> form: if every value at a path is the canonical lowercase
    /// spelling of its UUID, then comparing the stored strings answers exactly what comparing the
    /// values answers, and the comparison lowers to a plain string equality.
    /// </para>
    /// <para>
    /// <b>One rewrite is the whole of it.</b> Once the condition carries a string equality, everything
    /// downstream is machinery that already exists — the translator renders it, the service's index
    /// serves it, <see cref="CosmosPartitionKeyExtractor"/> recovers a partition key from it, and a
    /// point read follows where the predicate says nothing else.
    /// </para>
    /// <para>
    /// <b>Equality only, and that is measured rather than cautious.</b> Calcite compares UUIDs as two
    /// <em>signed</em> 64-bit halves, so the lexical order of the canonical string is not its order
    /// unless the schema also confines the first hex digit — see <see cref="CosmosStoredForms"/>.
    /// Lowering a range or a sort on a representation that only preserves equality would return the
    /// wrong rows, so nothing here reads <see cref="CosmosRepresentation.PreservesOrder"/> yet.
    /// </para>
    /// <para>
    /// <b>Why a guard needs no extra check here.</b> A fact may be conditional — a path holds a UUID
    /// only when a discriminator property has a particular value — and the condition that proved it is
    /// a conjunct of the very predicate being rewritten. It stays there. So over a document the fact
    /// was never claimed for, the conjunct that proved the guard has already excluded the row, and the
    /// rewritten comparison decides nothing. That is the sibling-conjunct case; a sort or a point read
    /// needs the stronger statement-wide argument, and neither is rewritten here.
    /// </para>
    /// </remarks>
    public static class CosmosFactRewriter
    {

        /// <summary>
        /// Returns the predicate with every comparison the declared facts license lowered, or the
        /// predicate unchanged.
        /// </summary>
        /// <param name="condition">The predicate, expressed over <paramref name="fields"/>.</param>
        /// <param name="fields">The ordinal-to-path binding of the filtered input.</param>
        /// <param name="container">The container, carrying whatever the model declared.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <param name="rexBuilder">Builds the replacement nodes.</param>
        /// <returns>The rewritten predicate, or <paramref name="condition"/> where nothing applied.</returns>
        public static RexNode Rewrite(RexNode? condition, IReadOnlyList<CosmosPath?>? fields, CosmosContainerMetadata? container, string rootAlias, RexBuilder rexBuilder)
        {
            if (condition is null || fields is null || rexBuilder is null)
                return condition!;

            // The ordinary case, and the one that has to cost nothing: a container that declares
            // nothing proves nothing, and asking is a field read.
            if (container is null || container.Facts.IsEmpty)
                return condition;

            var established = CosmosFactExtractor.Extract(condition, fields, rootAlias);
            var known = container.Facts.Derive(established);

            var translator = new CosmosRexTranslator(rexBuilder, fields, new CosmosParameterList());
            var rewritten = Apply(condition, translator, known, rootAlias, rexBuilder);

            return rewritten ?? condition;
        }

        /// <summary>
        /// Rewrites what it can, returning <c>null</c> where nothing below changed.
        /// </summary>
        /// <remarks>
        /// Conjunctions are descended into and nothing else is. A rewrite is an equivalence only under
        /// the declared fact, and the fact was proven from the conjuncts beside it; under a
        /// disjunction those conjuncts do not hold of every row the branch keeps, so the proof does
        /// not reach there.
        /// </remarks>
        static RexNode? Apply(RexNode node, CosmosRexTranslator translator, CosmosFactSet known, string rootAlias, RexBuilder rexBuilder)
        {
            if (node is not RexCall call)
                return null;

            var kind = (SqlKind.__Enum)call.getKind().ordinal();

            if (kind == SqlKind.__Enum.AND)
            {
                var operands = new java.util.ArrayList();
                var changed = false;

                for (var i = 0; i < call.getOperands().size(); i++)
                {
                    var operand = (RexNode)call.getOperands().get(i);
                    var rewritten = Apply(operand, translator, known, rootAlias, rexBuilder);

                    operands.add(rewritten ?? operand);
                    changed |= rewritten is not null;
                }

                return changed ? RexUtil.composeConjunction(rexBuilder, operands) : null;
            }

            if (kind != SqlKind.__Enum.EQUALS || call.getOperands().size() != 2)
                return null;

            var left = (RexNode)call.getOperands().get(0);
            var right = (RexNode)call.getOperands().get(1);

            return TryLower(left, right, translator, known, rootAlias, rexBuilder)
                ?? TryLower(right, left, translator, known, rootAlias, rexBuilder);
        }

        /// <summary>
        /// Lowers <c>CAST(&lt;path&gt; AS UUID) = UUID'…'</c> to a string equality, where the path's
        /// stored form is declared.
        /// </summary>
        static RexNode? TryLower(RexNode castNode, RexNode literalNode, CosmosRexTranslator translator, CosmosFactSet known, string rootAlias, RexBuilder rexBuilder)
        {
            if (literalNode is not RexLiteral literal || CanonicalUuid(literal) is not string canonical)
                return null;

            if (castNode is not RexCall cast)
                return null;

            var kind = (SqlKind.__Enum)cast.getKind().ordinal();
            if (kind != SqlKind.__Enum.CAST && kind != SqlKind.__Enum.SAFE_CAST)
                return null;

            if (cast.getType()?.getSqlTypeName() != SqlTypeName.UUID || cast.getOperands().size() != 1)
                return null;

            var operand = (RexNode)cast.getOperands().get(0);

            if (translator.TryResolvePath(operand, out var path) == false || path is null)
                return null;

            if (string.Equals(path.Alias, rootAlias, StringComparison.Ordinal) == false)
                return null;

            if (CosmosDocumentPath.From(path) is not CosmosDocumentPath document)
                return null;

            if (known.RepresentationOf(document) is not CosmosRepresentation representation)
                return null;

            // The form has to be one whose stored spelling this knows how to write, and one whose
            // equality means the value's equality. A representation that pins some other shape is not
            // this rewrite's business, however well declared.
            if (representation.PreservesEquality == false || CosmosStoredForms.IsUuid(representation) == false)
                return null;

            return rexBuilder.makeCall(SqlStdOperatorTable.EQUALS, operand, rexBuilder.makeLiteral(canonical));
        }

        /// <summary>
        /// Reads a UUID literal as the canonical lowercase string a conforming document stores.
        /// </summary>
        /// <remarks>
        /// <c>java.util.UUID.toString</c> is that spelling, and <c>Guid.ToString("D")</c> is the same
        /// one — the fallback exists because the boxed representation of a literal is Calcite's
        /// business rather than this adapter's, and a spelling that will not parse yields no rewrite
        /// rather than a guess.
        /// </remarks>
        static string? CanonicalUuid(RexLiteral literal)
        {
            if (literal.isNull() || literal.getTypeName() != SqlTypeName.UUID)
                return null;

            if (literal.getValue() is java.util.UUID uuid)
                return uuid.toString();

            return literal.getValue()?.ToString() is string text && Guid.TryParseExact(text, "D", out var parsed)
                ? parsed.ToString("D")
                : null;
        }

    }

}
