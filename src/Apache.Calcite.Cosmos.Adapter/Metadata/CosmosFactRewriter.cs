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
    /// <b>Which of the two a form licenses is not a matter of degree.</b> An instant written at mixed
    /// precision has one spelling per value while <c>'…:56.5Z'</c> sorts before <c>'…:56Z'</c>, and an
    /// unpadded integer has one spelling while <c>'9'</c> sorts after <c>'42'</c> — see
    /// <see cref="CosmosStoredForms"/>. A form may therefore preserve equality and not order, and
    /// lowering a range on one would return the wrong rows. So the two comparisons are gated on the
    /// two properties separately, on exactly what <see cref="CosmosRepresentation"/> carries.
    /// </para>
    /// <para>
    /// <b>A UUID used to reach the equality alone, and the reason was the engine's rather than the
    /// spelling's.</b> Calcite compared UUIDs as two <em>signed</em> 64-bit halves, so the lexical
    /// order of the canonical string was not its order unless the schema also confined the first hex
    /// digit. CALCITE-7716 made the comparison unsigned in 1.43 and
    /// <see cref="CosmosUuidForms.CanonicalLower"/> preserves order with it, so
    /// <see cref="TryLowerUuid"/> takes the operator and the reversal exactly as the other two do.
    /// The gate did not move: it is still the two bits, asked separately.
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

            // Where the query's own conjuncts and the container's declaration cannot both hold, no
            // document satisfies the predicate -- so the equivalent predicate is the constant, and
            // every rule that reduces one takes it from there. See CosmosFactSet.IsContradictory.
            if (known.IsContradictory)
                return rexBuilder.makeLiteral(false);

            // An IN arrives folded into a SEARCH over a Sarg, which is one node rather than the
            // equalities it stands for, so nothing below would recognise it. Expanded, it is the
            // disjunction translation would have rendered anyway -- and the expansion is kept only if
            // something was actually lowered, so a predicate this has no opinion about reaches the
            // planner in the shape it arrived.
            RexNode expanded;
            try
            {
                expanded = RexUtil.expandSearch(rexBuilder, null, condition);
            }
            catch (Exception)
            {
                expanded = condition;
            }

            // What holds of every document in the container, whatever this query proved. A
            // conjunct is redundant only against that, never against a fact the query's own
            // conjuncts unlocked -- see IsAlwaysTrue.
            var outright = container.Facts.Derive(null);

            var translator = new CosmosRexTranslator(rexBuilder, fields, new CosmosParameterList());
            var rewritten = Apply(expanded, translator, known, rootAlias, rexBuilder, fields, outright);

            return rewritten ?? condition;
        }

        /// <summary>
        /// Rewrites what it can, returning <c>null</c> where nothing below changed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Conjunctions and disjunctions alike, and the reason it reaches into a disjunction is worth
        /// stating. A rewrite is an equivalence only under the declared fact, and a conditional fact
        /// was proven from the <em>top-level</em> conjuncts — which hold of every row the whole
        /// predicate keeps, whatever shape the rest of it has. So inside <c>A AND (B OR C)</c> the
        /// proof of <c>A</c> is as good in <c>B</c> as it is beside it: over a row where <c>A</c>
        /// fails the conjunction is false however the branch reads, and over one where it holds the
        /// fact holds too.
        /// </para>
        /// <para>
        /// Which is what makes an <c>IN</c> work. Expanded, it is a disjunction of equalities over one
        /// path, each lowered the way a lone equality is — and once the points are stored spellings
        /// rather than casts, <c>CosmosPartitionKeyExtractor</c> can recover them as a set and the
        /// batch point read becomes reachable through a typed column.
        /// </para>
        /// </remarks>
        static RexNode? Apply(RexNode node, CosmosRexTranslator translator, CosmosFactSet known, string rootAlias, RexBuilder rexBuilder, IReadOnlyList<CosmosPath?> fields, CosmosFactSet outright)
        {
            if (node is not RexCall call)
                return null;

            // A comparison the container already guarantees decides nothing, so the equivalent
            // predicate is the one without it. RexUtil drops a TRUE from a conjunction and collapses a
            // disjunction carrying one, so answering the constant here is the whole of it.
            if (IsAlwaysTrue(call, fields, rootAlias, outright))
                return rexBuilder.makeLiteral(true);

            // By name rather than by ordinal: a kind's position among SqlKind's 355 values is
            // not an API, and a cast from the ordinal keeps compiling when one is inserted.
            var kind = call.getKind().name();

            if (kind == nameof(SqlKind.__Enum.AND) || kind == nameof(SqlKind.__Enum.OR))
            {
                var operands = new java.util.ArrayList();
                var changed = false;

                for (var i = 0; i < call.getOperands().size(); i++)
                {
                    var operand = (RexNode)call.getOperands().get(i);
                    var rewritten = Apply(operand, translator, known, rootAlias, rexBuilder, fields, outright);

                    operands.add(rewritten ?? operand);
                    changed |= rewritten is not null;
                }

                if (changed == false)
                    return null;

                return kind == nameof(SqlKind.__Enum.AND)
                    ? RexUtil.composeConjunction(rexBuilder, operands)
                    : RexUtil.composeDisjunction(rexBuilder, operands);
            }

            if (call.getOperands().size() != 2)
                return null;

            var left = (RexNode)call.getOperands().get(0);
            var right = (RexNode)call.getOperands().get(1);

            // Reading the operand on the right means reading the comparison backwards, so the
            // operator is reversed with it: `<literal> > <path>` is `<path> < <literal>`, and keeping
            // the operator as written would select the complement. `=` and `<>` are their own
            // reverse, so the equalities are unaffected by passing through the same machinery.
            if (ComparisonOf(kind) is not SqlOperator comparison)
                return null;

            return TryLowerUuid(left, right, comparison, translator, known, rootAlias, rexBuilder)
                ?? TryLowerUuid(right, left, Reverse(comparison), translator, known, rootAlias, rexBuilder)
                ?? TryLowerInstant(left, right, comparison, translator, known, rootAlias, rexBuilder)
                ?? TryLowerInstant(right, left, Reverse(comparison), translator, known, rootAlias, rexBuilder)
                ?? TryLowerNumber(left, right, comparison, translator, known, rootAlias, rexBuilder)
                ?? TryLowerNumber(right, left, Reverse(comparison), translator, known, rootAlias, rexBuilder);
        }

        /// <summary>
        /// Determines whether a comparison is one every document in the container satisfies, so that
        /// asking it decides nothing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Two claims, and the second is what makes it an equivalence rather than a weakening.</b>
        /// A declared value says what a path holds <em>if it holds anything</em> — the same reading
        /// <see cref="CosmosFact.Entails"/> is careful about, where nothing entails
        /// <see cref="CosmosClaim.Present"/>. So a container declaring <c>kind</c> is <c>"A"</c> and
        /// nothing more still admits a document with no <c>kind</c> at all, over which
        /// <c>kind = 'A'</c> is unknown and the row is dropped. Removing the conjunct would keep that
        /// row. Only a path declared <em>present</em> as well has no such document, and only then is
        /// the comparison true of everything.
        /// </para>
        /// <para>
        /// <b>Outright, never under a guard.</b> A guarded fact holds of the rows a sibling conjunct
        /// keeps, which is enough to <em>rewrite</em> a comparison and not enough to delete one: the
        /// conjunct being deleted may be the very one that proved the guard, and the predicate left
        /// behind would then prove less than it did. Asking only what
        /// <c>Derive(null)</c> knows sidesteps the question rather than reasoning about it.
        /// </para>
        /// <para>
        /// <b>Exactly an equality, and nothing looser.</b> The shape is read here rather than through
        /// <see cref="CosmosFactExtractor"/>, which is deliberately incomplete — it reports what a
        /// predicate <em>proves</em>, and under-reporting is harmless when proving a guard and fatal
        /// when deleting a conjunct, since the part it did not report still constrains.
        /// </para>
        /// </remarks>
        /// <param name="call">The conjunct.</param>
        /// <param name="fields">The ordinal-to-path binding.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <param name="outright">What holds of every document, whatever the query proved.</param>
        /// <returns><c>true</c> where every document satisfies it.</returns>
        static bool IsAlwaysTrue(RexCall call, IReadOnlyList<CosmosPath?> fields, string rootAlias, CosmosFactSet outright)
        {
            if (call.getKind().name() != nameof(SqlKind.__Enum.EQUALS) || call.getOperands().size() != 2)
                return false;

            if (CosmosFactExtractor.TryComparison(call, fields, rootAlias, out var path, out var value) == false || path is null)
                return false;

            return outright.Knows(new CosmosFact(path, new CosmosClaim.Present()))
                && outright.Knows(new CosmosFact(path, new CosmosClaim.EqualTo(value)));
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
        /// Lowers a comparison between a UUID read out of a path and a UUID literal into a comparison
        /// between the stored strings, where the path's declared form licenses it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Equality and ordering ask different things of the form, and the second only recently
        /// gets an answer.</b> Equality needs one spelling per value, which a canonical form has by
        /// being canonical. An ordering needs the lexical order of the stored strings to be the order
        /// the engine compares the values in, and until
        /// <a href="https://issues.apache.org/jira/browse/CALCITE-7716">CALCITE-7716</a> the engine
        /// compared the two 64-bit halves as signed longs, so a canonical form gave that only where
        /// the schema also confined the first hex digit. The comparison is unsigned from 1.43 and
        /// <see cref="CosmosUuidForms.CanonicalLower"/> preserves order with it — but the rows
        /// still carry the two bits separately, and this gates on them separately, because the engine
        /// keeps a switch that puts the old semantics back.
        /// </para>
        /// <para>
        /// <b>The literal is rendered into the path's own spelling</b>, which is the whole of what the
        /// UUID forms differ by; <see cref="CosmosStoredForms.RenderUuid"/> decides it, and refuses
        /// where the form stores something other than a UUID.
        /// </para>
        /// <para>
        /// <b><c>&lt;&gt;</c> comes with the range rather than with the order.</b> It asks the
        /// equality's question and is gated with it, and it lowers now only because the operator is
        /// carried through at all — the previous spelling reached this from the <c>EQUALS</c> branch
        /// alone and left the inequality in process for no reason either bit gives.
        /// </para>
        /// </remarks>
        /// <param name="castNode">The operand that may read a path as a UUID.</param>
        /// <param name="literalNode">The operand that may be the literal.</param>
        /// <param name="comparison">The operator, already reversed where the operands were read backwards.</param>
        /// <param name="translator">Resolves the path underneath.</param>
        /// <param name="known">What the container has been shown to hold.</param>
        /// <param name="rootAlias">The alias a path must be rooted at.</param>
        /// <param name="rexBuilder">Builds the lowered comparison.</param>
        /// <returns>The lowered comparison, or <c>null</c>.</returns>
        static RexNode? TryLowerUuid(RexNode castNode, RexNode literalNode, SqlOperator comparison, CosmosRexTranslator translator, CosmosFactSet known, string rootAlias, RexBuilder rexBuilder)
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

            var ordering = comparison != SqlStdOperatorTable.EQUALS && comparison != SqlStdOperatorTable.NOT_EQUALS;

            if (ordering ? representation.PreservesOrder == false : representation.PreservesEquality == false)
                return null;

            // And the form has to be one whose stored spelling this knows how to write. A
            // representation that pins some other shape is not this rewrite's business, however well
            // declared.
            if (CosmosStoredForms.RenderUuid(representation, value) is not string stored)
                return null;

            return rexBuilder.makeCall(comparison, operand, rexBuilder.makeLiteral(stored));
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
        /// Lowers a comparison between a number read out of a path and a numeric literal into a
        /// comparison between the stored strings, where the path's declared form licenses it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A string spelling a number is the case this exists for.</b> Cosmos has numbers, so a
        /// path holding one needs none of this; what needs it is the very common path holding a
        /// <em>string</em> that spells one — an account number, a code, a zero-padded sequence — which
        /// a caller reaches through <c>CAST(… AS INTEGER)</c> and which was read whole because the
        /// cast has no Cosmos form.
        /// </para>
        /// <para>
        /// <b>The whole question is whether the spelling is faithful, and a pattern can say so.</b>
        /// Measured, Calcite's cast reads <c>'042'</c>, <c>'+42'</c>, <c>' 42'</c> and <c>'42'</c> as
        /// the same number, so a form admitting any two of them gives one value two spellings and a
        /// string equality would miss documents. A pattern that forbids the padding, or fixes it,
        /// admits exactly one — which is what <see cref="CosmosNumericForms.IntegerUnpadded"/> and
        /// <see cref="CosmosNumericForms.IntegerFixedWidth"/> record.
        /// </para>
        /// <para>
        /// <b>Ordering asks the stronger question and only the padded form answers it.</b> A lexical
        /// comparison compares the first differing character, which is a comparison of digits at equal
        /// significance only when the strings are the same length; unpadded, <c>'9'</c> sorts after
        /// <c>'42'</c>. So the two are gated separately on the two properties the form carries, exactly
        /// as the temporal lowering gates them.
        /// </para>
        /// </remarks>
        /// <param name="numericNode">The operand that may read a path as a number.</param>
        /// <param name="literalNode">The operand that may be the literal.</param>
        /// <param name="comparison">The operator, already reversed where the operands were read backwards.</param>
        /// <param name="translator">Resolves the path underneath.</param>
        /// <param name="known">What the container has been shown to hold.</param>
        /// <param name="rootAlias">The alias a path must be rooted at.</param>
        /// <param name="rexBuilder">Builds the lowered comparison.</param>
        /// <returns>The lowered comparison, or <c>null</c>.</returns>
        static RexNode? TryLowerNumber(RexNode numericNode, RexNode literalNode, SqlOperator comparison, CosmosRexTranslator translator, CosmosFactSet known, string rootAlias, RexBuilder rexBuilder)
        {
            if (literalNode is not RexLiteral literal || IntegerOf(literal) is not long value)
                return null;

            if (NumericAccessorOf(numericNode) is not RexNode accessor)
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

            if (CosmosStoredForms.RenderInteger(representation, value) is not string stored)
                return null;

            return rexBuilder.makeCall(comparison, accessor, rexBuilder.makeLiteral(stored));
        }

        /// <summary>
        /// Returns the accessor answering the stored text underneath an expression that reads a path
        /// as an exact number, or <c>null</c> where the expression is not one of those.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only the cast, and only over the text. <c>RETURNING INTEGER</c> is not a second spelling
        /// here the way <c>RETURNING VARCHAR</c> is for a UUID: <c>RETURNING</c> asserts the extracted
        /// type rather than converting to it, and a path holding a string never extracts as a number,
        /// so the clause fails at run time rather than naming this shape.
        /// </para>
        /// <para>
        /// The approximate types are absent deliberately. A stored spelling maps to one
        /// <c>DOUBLE</c>, but a <c>DOUBLE</c> maps back to many spellings — the value is rounded
        /// before it is compared — so the literal could not be rendered into the container's shape
        /// without changing which documents match.
        /// </para>
        /// </remarks>
        /// <param name="node">The expression.</param>
        /// <returns>The text accessor, or <c>null</c>.</returns>
        static RexNode? NumericAccessorOf(RexNode node)
        {
            if (node is not RexCall call || call.getOperands().size() != 1)
                return null;

            var kind = call.getKind().name();
            if (kind != nameof(SqlKind.__Enum.CAST) && kind != nameof(SqlKind.__Enum.SAFE_CAST))
                return null;

            var type = call.getType()?.getSqlTypeName();
            if (type != SqlTypeName.TINYINT && type != SqlTypeName.SMALLINT && type != SqlTypeName.INTEGER
                && type != SqlTypeName.BIGINT && type != SqlTypeName.DECIMAL)
                return null;

            return (RexNode)call.getOperands().get(0);
        }

        /// <summary>
        /// Reads the whole number a numeric literal carries, or <c>null</c> where it carries none.
        /// </summary>
        /// <remarks>
        /// A value with a fractional part has no spelling in an integer form, so it is refused here
        /// rather than rounded into one — the comparison would then select different documents. A
        /// decimal literal that happens to be whole is accepted, <c>42.0</c> and <c>42</c> being the
        /// same number however the query wrote it.
        /// </remarks>
        /// <param name="literal">The literal.</param>
        /// <returns>The value, or <c>null</c>.</returns>
        static long? IntegerOf(RexLiteral literal)
        {
            if (literal.isNull())
                return null;

            var type = literal.getTypeName();
            if (type != SqlTypeName.DECIMAL && type != SqlTypeName.INTEGER && type != SqlTypeName.BIGINT
                && type != SqlTypeName.SMALLINT && type != SqlTypeName.TINYINT)
                return null;

            try
            {
                return literal.getValue() switch
                {
                    java.math.BigDecimal d => d.stripTrailingZeros().scale() <= 0 ? d.longValueExact() : null,
                    java.lang.Long l => l.longValue(),
                    java.lang.Integer i => (long)i.intValue(),
                    _ => null,
                };
            }
            catch (java.lang.ArithmeticException)
            {
                // Beyond a long, which no stored spelling this renders could match anyway.
                return null;
            }
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
