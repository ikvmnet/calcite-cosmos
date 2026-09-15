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
    /// <b>The same argument reaches instants, and reaches further.</b> Cosmos has no date type either,
    /// so a path read as a <c>TIMESTAMP</c> holds a string and a comparison against one has no form
    /// the service can evaluate. Where the declared shape is one fixed ISO-8601 UTC spelling, the
    /// lexical order of the stored strings <em>is</em> the chronological order, so a range lowers as
    /// well as an equality — see <see cref="TryLowerInstant"/>.
    /// </para>
    /// <para>
    /// <b>Which of the two a form licenses is not a matter of degree.</b> Calcite compares UUIDs as two
    /// <em>signed</em> 64-bit halves, so the lexical order of the canonical string is not its order
    /// unless the schema also confines the first hex digit — see <see cref="CosmosStoredForms"/>. A
    /// form may therefore preserve equality and not order, and lowering a range on one would return
    /// the wrong rows. So the two comparisons are gated on the two properties separately, on exactly
    /// what <see cref="CosmosRepresentation"/> carries, and a UUID reaches the equality alone.
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

            // By name rather than by ordinal: a kind's position among SqlKind's 355 values is
            // not an API, and a cast from the ordinal keeps compiling when one is inserted.
            var kind = call.getKind().name();

            if (kind == nameof(SqlKind.__Enum.AND))
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

            if (call.getOperands().size() != 2)
                return null;

            var left = (RexNode)call.getOperands().get(0);
            var right = (RexNode)call.getOperands().get(1);

            // A UUID has no order to lower — see the remarks — so only the equality reaches it. The
            // flipped orientation needs no reversed operator, equality being symmetric.
            if (kind == nameof(SqlKind.__Enum.EQUALS))
            {
                var lowered = TryLower(left, right, translator, known, rootAlias, rexBuilder)
                    ?? TryLower(right, left, translator, known, rootAlias, rexBuilder);

                if (lowered is not null)
                    return lowered;
            }

            // An instant does have one, where the stored form is a fixed shape. Reading the operand
            // on the right means reading the comparison backwards, so the operator is reversed with
            // it: `<literal> > <path>` is `<path> < <literal>`, and keeping the operator as written
            // would select the complement.
            if (ComparisonOf(kind) is not SqlOperator comparison)
                return null;

            return TryLowerInstant(left, right, comparison, translator, known, rootAlias, rexBuilder)
                ?? TryLowerInstant(right, left, Reverse(comparison), translator, known, rootAlias, rexBuilder);
        }

        /// <summary>
        /// Maps a comparison kind onto the operator that rebuilds it, or <c>null</c> where the kind is
        /// not one an ordering lowers.
        /// </summary>
        /// <param name="kind">The kind name.</param>
        /// <returns>The operator, or <c>null</c>.</returns>
        static SqlOperator? ComparisonOf(string kind) => kind switch
        {
            nameof(SqlKind.__Enum.EQUALS) => SqlStdOperatorTable.EQUALS,
            nameof(SqlKind.__Enum.NOT_EQUALS) => SqlStdOperatorTable.NOT_EQUALS,
            nameof(SqlKind.__Enum.GREATER_THAN) => SqlStdOperatorTable.GREATER_THAN,
            nameof(SqlKind.__Enum.GREATER_THAN_OR_EQUAL) => SqlStdOperatorTable.GREATER_THAN_OR_EQUAL,
            nameof(SqlKind.__Enum.LESS_THAN) => SqlStdOperatorTable.LESS_THAN,
            nameof(SqlKind.__Enum.LESS_THAN_OR_EQUAL) => SqlStdOperatorTable.LESS_THAN_OR_EQUAL,
            _ => null,
        };

        /// <summary>
        /// Returns the operator that means the same thing with its operands the other way round.
        /// </summary>
        /// <param name="comparison">The operator as written.</param>
        /// <returns>Its reverse.</returns>
        static SqlOperator Reverse(SqlOperator comparison)
        {
            if (comparison == SqlStdOperatorTable.GREATER_THAN)
                return SqlStdOperatorTable.LESS_THAN;

            if (comparison == SqlStdOperatorTable.GREATER_THAN_OR_EQUAL)
                return SqlStdOperatorTable.LESS_THAN_OR_EQUAL;

            if (comparison == SqlStdOperatorTable.LESS_THAN)
                return SqlStdOperatorTable.GREATER_THAN;

            if (comparison == SqlStdOperatorTable.LESS_THAN_OR_EQUAL)
                return SqlStdOperatorTable.GREATER_THAN_OR_EQUAL;

            // `=` and `<>` are their own reverse.
            return comparison;
        }

        /// <summary>
        /// Lowers <c>CAST(&lt;path&gt; AS UUID) = UUID'…'</c> to a string equality, where the path's
        /// stored form is declared.
        /// </summary>
        static RexNode? TryLower(RexNode castNode, RexNode literalNode, CosmosRexTranslator translator, CosmosFactSet known, string rootAlias, RexBuilder rexBuilder)
        {
            if (literalNode is not RexLiteral literal || UuidOf(literal) is not Guid value)
                return null;

            if (castNode is not RexCall cast)
                return null;

            var kind = cast.getKind().name();
            if (kind != nameof(SqlKind.__Enum.CAST) && kind != nameof(SqlKind.__Enum.SAFE_CAST))
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
            if (representation.PreservesEquality == false || CosmosStoredForms.RenderUuid(representation, value) is not string stored)
                return null;

            return rexBuilder.makeCall(SqlStdOperatorTable.EQUALS, operand, rexBuilder.makeLiteral(stored));
        }

        /// <summary>
        /// Lowers a comparison between an instant read out of a path and a temporal literal into a
        /// comparison between the stored strings, where the path's declared form licenses it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Equality and ordering ask different things of the form.</b> Equality needs one spelling
        /// per value, which every form here has. An ordering needs the lexical order of the stored
        /// strings to be the chronological order, which only a fixed shape gives — so the two are
        /// gated separately, on exactly the two properties
        /// <see cref="CosmosRepresentation"/> carries.
        /// </para>
        /// <para>
        /// <b>The literal is rendered into the path's own shape, and refused where it will not fit.</b>
        /// <see cref="CosmosStoredForms.RenderDateTime"/> is what decides that, and why truncating
        /// would not be sound is recorded there.
        /// </para>
        /// </remarks>
        static RexNode? TryLowerInstant(RexNode temporalNode, RexNode literalNode, SqlOperator comparison, CosmosRexTranslator translator, CosmosFactSet known, string rootAlias, RexBuilder rexBuilder)
        {
            if (literalNode is not RexLiteral literal || InstantOf(literal) is not DateTime value)
                return null;

            if (TextAccessorOf(temporalNode, rexBuilder) is not RexNode accessor)
                return null;

            if (translator.TryResolvePath(accessor, out var path) == false || path is null)
                return null;

            if (string.Equals(path.Alias, rootAlias, StringComparison.Ordinal) == false)
                return null;

            if (CosmosDocumentPath.From(path) is not CosmosDocumentPath document)
                return null;

            if (known.RepresentationOf(document) is not CosmosRepresentation representation)
                return null;

            var ordering = comparison != SqlStdOperatorTable.EQUALS && comparison != SqlStdOperatorTable.NOT_EQUALS;

            if (ordering ? representation.PreservesOrder == false : representation.PreservesEquality == false)
                return null;

            if (CosmosStoredForms.RenderDateTime(representation, value) is not string stored)
                return null;

            return rexBuilder.makeCall(comparison, accessor, rexBuilder.makeLiteral(stored));
        }

        /// <summary>
        /// Returns the accessor answering the stored text underneath an expression that reads a path
        /// as an instant, or <c>null</c> where the expression is not one of those.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two spellings reach here, and they arrive shaped differently.
        /// <c>CAST(JSON_VALUE(…) AS TIMESTAMP)</c> carries the text accessor as its operand, so the
        /// operand is the answer — the same one the UUID lowering takes.
        /// <c>JSON_VALUE(…, '$.p' RETURNING TIMESTAMP)</c> carries none: the <c>RETURNING</c> clause
        /// types the call itself, and by the time the planner has simplified it the node is a
        /// two-operand accessor whose only mark of being temporal is its type. Rebuilding the call
        /// without the clause is what recovers the text, the default return type being the text.
        /// </para>
        /// <para>
        /// The type test is what keeps the two apart from an ordinary <c>VARCHAR</c> accessor, which
        /// needs no lowering and must not be given one.
        /// </para>
        /// </remarks>
        /// <param name="node">The expression.</param>
        /// <param name="rexBuilder">Builds the rebuilt accessor.</param>
        /// <returns>The text accessor, or <c>null</c>.</returns>
        static RexNode? TextAccessorOf(RexNode node, RexBuilder rexBuilder)
        {
            if (node is not RexCall call)
                return null;

            var type = call.getType()?.getSqlTypeName();
            if (type != SqlTypeName.TIMESTAMP && type != SqlTypeName.DATE)
                return null;

            var kind = call.getKind().name();

            if ((kind == nameof(SqlKind.__Enum.CAST) || kind == nameof(SqlKind.__Enum.SAFE_CAST)) && call.getOperands().size() == 1)
                return (RexNode)call.getOperands().get(0);

            if (string.Equals(call.getOperator().getName(), "JSON_VALUE", StringComparison.Ordinal) && call.getOperands().size() >= 2)
                return rexBuilder.makeCall(SqlStdOperatorTable.JSON_VALUE, (RexNode)call.getOperands().get(0), (RexNode)call.getOperands().get(1));

            return null;
        }

        /// <summary>
        /// Reads the instant a temporal literal carries.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The value rather than a spelling, for the reason <see cref="UuidOf"/> gives: which spelling
        /// a conforming document stores is the path form to say, and
        /// <see cref="CosmosStoredForms.RenderDateTime"/> is what says it.
        /// </para>
        /// <para>
        /// <b>Read from the calendar rather than from its text.</b> Measured: <c>getValue</c> answers a
        /// <c>GregorianCalendar</c>, whose <c>toString</c> is the Java dump — <c>areFieldsSet</c> and
        /// all — and parses as nothing. The calendar is pinned to UTC and its epoch milliseconds are
        /// the wall clock the query wrote, read as UTC, which is exactly the reading these forms
        /// store. Anything else yields no rewrite rather than a guess.
        /// </para>
        /// </remarks>
        /// <param name="literal">The literal.</param>
        /// <returns>The value, or <c>null</c> where the literal is not one this can read.</returns>
        static DateTime? InstantOf(RexLiteral literal)
        {
            if (literal.isNull())
                return null;

            var type = literal.getTypeName();
            if (type != SqlTypeName.TIMESTAMP && type != SqlTypeName.DATE)
                return null;

            if (literal.getValue() is not java.util.Calendar calendar)
                return null;

            return DateTimeOffset.FromUnixTimeMilliseconds(calendar.getTimeInMillis()).UtcDateTime;
        }

        /// <summary>
        /// Reads the value a UUID literal carries.
        /// </summary>
        /// <remarks>
        /// The value and not a spelling: which spelling a conforming document stores is the path form
        /// to say, and <see cref="CosmosStoredForms.RenderUuid"/> is what says it. The fallback exists
        /// because the boxed representation of a literal is Calcite business rather than this adapter
        /// business, and text that will not parse yields no rewrite rather than a guess.
        /// </remarks>
        /// <param name="literal">The literal.</param>
        /// <returns>The value, or <c>null</c> where the literal is not a UUID this can read.</returns>
        static Guid? UuidOf(RexLiteral literal)
        {
            if (literal.isNull() || literal.getTypeName() != SqlTypeName.UUID)
                return null;

            var text = literal.getValue() is java.util.UUID uuid ? uuid.toString() : literal.getValue()?.ToString();

            return text is not null && Guid.TryParse(text, out var value) ? value : null;
        }

    }

}
