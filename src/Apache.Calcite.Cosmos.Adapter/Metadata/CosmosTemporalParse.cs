using System;

using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// Reads a call that parses a stored string into an instant, and says which text it reads and
    /// with which format.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the structural half of a form-preserving chain, and only the structural half.</b>
    /// What it answers is that an expression is <c>PARSE_x(&lt;format&gt;, &lt;text&gt;)</c>, or a cast
    /// carrying a format, over a literal format — which says nothing yet about whether the parse means anything for the path
    /// underneath. Whether that particular format reads that particular stored shape faithfully is
    /// <see cref="CosmosTemporalForms.ParsesExactly"/>, a pure lookup the callers make for
    /// themselves; keeping the two apart is what lets a rule and an implementation ask the same
    /// question without a walk between them, exactly as <c>CosmosProject.OrderingCandidateOf</c> and
    /// <c>CosmosProject.IsOrderable</c> already do.
    /// </para>
    /// <para>
    /// <b>The <c>PARSE_</c> family and nothing else, and the reason is a name collision.</b>
    /// <c>TO_DATE</c> and <c>TO_TIMESTAMP</c> are each <em>two</em> operators — Calcite registers
    /// <c>TO_DATE</c> beside <c>TO_DATE_PG</c> under one SQL name, implemented by
    /// <c>SqlFunctions.DateFormatFunction</c> and <c>DateFormatFunctionPg</c> respectively — and which
    /// of them a query resolves to depends on the libraries the connection enabled, which a
    /// <c>RexCall</c>'s name does not say. Only the first was measured, so neither is read here. The
    /// <c>PARSE_</c> names are unambiguous, and measured to agree with it.
    /// </para>
    /// <para>
    /// <b>And <c>CAST(&lt;text&gt; AS TIMESTAMP FORMAT '&lt;format&gt;')</c>, the standard spelling of
    /// the same parse.</b> Calcite plans it as a <c>CAST</c> with the format as a second operand, and
    /// reads the format with the same model the <c>PARSE_</c> family does — measured, every spelling
    /// <see cref="CosmosTemporalForms"/> lists reads its shape to the same instant either way, which is
    /// what lets one table answer for both. What it has that the library functions do not is a place
    /// in the core operator table, so it validates inside a model view, which Calcite analyzes under
    /// its default configuration whatever the connection enabled: a federation presenting containers
    /// through views has this spelling and no other.
    /// </para>
    /// <para>
    /// <b><c>PARSE_TIMESTAMP</c> is recognised and goes no further, which is worth stating because it
    /// is the name a caller reaches for first.</b> It answers <c>TIMESTAMP WITH LOCAL TIME ZONE</c>
    /// rather than <c>TIMESTAMP</c>, and that is two separate refusals downstream rather than one
    /// here: a comparison against a zone-less literal is a question about the session's zone, which
    /// no declared form answers, and <see cref="Client.CosmosJson"/> has no reading for the type, so
    /// a projection of one cannot be sent down either. Both are recorded in <c>TODO.md</c>; the shape
    /// is read here so that the refusals are decisions rather than an unrecognised call.
    /// </para>
    /// </remarks>
    public static class CosmosTemporalParse
    {

        /// <summary>
        /// Reads a parse call, returning the text it reads, the format it reads it with, and the
        /// halves of an instant the value it answers can hold.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>By name, and the operands by position.</b> A library function carries the operator
        /// Calcite registered for the name the query wrote, so the name is what identifies it — the
        /// same dispatch the translator makes everywhere else. The format is the first operand in this
        /// family, which is BigQuery's order and not Postgres's; <c>TO_DATE</c>'s opposite order is
        /// the other reason it is absent. A cast is identified by its kind instead, being a core
        /// operator, and carries the format second; a cast without one has a single operand and is
        /// not a parse, its conversion being the engine's own.
        /// </para>
        /// <para>
        /// <b>The format has to be a literal.</b> A format computed per row would have to be read per
        /// row, and there is nothing to read it against — the claim a rewrite needs is about every
        /// document in the container at once.
        /// </para>
        /// <para>
        /// <b>The parts come from the type rather than from the name</b>, which is the reading that
        /// survives a library adding a spelling: what a parse can lose is decided by what the value it
        /// answers holds, and the type is where that is written down.
        /// </para>
        /// </remarks>
        /// <param name="node">The expression.</param>
        /// <param name="text">On success, the operand holding the stored text.</param>
        /// <param name="format">On success, the format the query wrote.</param>
        /// <param name="held">On success, the halves of an instant the result holds.</param>
        /// <returns><c>true</c> if the expression is one of these.</returns>
        public static bool TryRead(RexNode? node, out RexNode? text, out string? format, out CosmosTemporalParts held)
        {
            text = null;
            format = null;
            held = CosmosTemporalParts.None;

            if (node is not RexCall call || call.getOperands().size() != 2)
                return false;

            // Which operand is which is the one thing the spellings disagree on: the PARSE_ family
            // takes the format first, and a cast carries it after the operand it converts.
            int textAt, formatAt;

            if (call.getKind().name() == nameof(SqlKind.__Enum.CAST))
            {
                // The text has to be text: a format on a cast from anything else is not a parse of a
                // stored string, and there would be no shape for it to read.
                var source = ((RexNode)call.getOperands().get(0)).getType()?.getSqlTypeName();
                if (source != SqlTypeName.CHAR && source != SqlTypeName.VARCHAR)
                    return false;

                (textAt, formatAt) = (0, 1);
            }
            else
            {
                var name = call.getOperator()?.getName();
                if (name is null)
                    return false;

                if (string.Equals(name, "PARSE_DATE", StringComparison.Ordinal) == false
                    && string.Equals(name, "PARSE_DATETIME", StringComparison.Ordinal) == false
                    && string.Equals(name, "PARSE_TIME", StringComparison.Ordinal) == false
                    && string.Equals(name, "PARSE_TIMESTAMP", StringComparison.Ordinal) == false)
                    return false;

                (textAt, formatAt) = (1, 0);
            }

            var parts = PartsOf(call.getType()?.getSqlTypeName());
            if (parts == CosmosTemporalParts.None)
                return false;

            if ((RexNode)call.getOperands().get(formatAt) is not RexLiteral literal || literal.isNull())
                return false;

            var literalType = literal.getTypeName();
            if (literalType != SqlTypeName.CHAR && literalType != SqlTypeName.VARCHAR)
                return false;

            if ((literal.getValue() is org.apache.calcite.util.NlsString nls ? nls.getValue() : literal.getValue()?.ToString()) is not string written)
                return false;

            // A cast names its precision, and a type that cannot hold the fraction the format reads
            // makes the parse lose it, however faithfully the format read it -- the same refusal a
            // DATE gets over an instant, one field finer. A view writes TIMESTAMP(3) for milliseconds.
            if (formatAt == 1 && CosmosTemporalForms.FractionDigitsOf(written) > call.getType().getPrecision())
                return false;

            text = (RexNode)call.getOperands().get(textAt);
            format = written;
            held = parts;
            return true;
        }

        /// <summary>
        /// Maps the type a parse answers onto the halves of an instant it holds.
        /// </summary>
        /// <param name="type">The type, or <c>null</c>.</param>
        /// <returns>The parts, or <see cref="CosmosTemporalParts.None"/> for a type that is none of these.</returns>
        public static CosmosTemporalParts PartsOf(SqlTypeName? type)
        {
            if (type == SqlTypeName.DATE)
                return CosmosTemporalParts.Date;

            if (type == SqlTypeName.TIME || type == SqlTypeName.TIME_WITH_LOCAL_TIME_ZONE)
                return CosmosTemporalParts.Time;

            if (type == SqlTypeName.TIMESTAMP || type == SqlTypeName.TIMESTAMP_WITH_LOCAL_TIME_ZONE || type == SqlTypeName.TIMESTAMP_TZ)
                return CosmosTemporalParts.Instant;

            return CosmosTemporalParts.None;
        }

    }

}
