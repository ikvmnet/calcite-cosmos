using System;

using Apache.Calcite.Cosmos.Adapter.Metadata;

using org.apache.calcite.plan;
using org.apache.calcite.schema;
using org.apache.calcite.sql2rel;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// Says which of a container's columns an <c>INSERT</c> may omit, and which it may not name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this an insert does not reach a conversion rule at all. Every column that is neither
    /// nullable nor defaulted must be supplied, which <c>SqlValidatorImpl.checkFieldCount</c> enforces,
    /// and <c>DOC</c> is declared <c>NOT NULL</c>:
    /// </para>
    /// <code>
    /// Column 'DOC' has no default value and does not allow NULLs
    /// </code>
    /// <para>
    /// The declaration is true — every row is a document — so the answer is not to weaken the row
    /// type. A row type that bends to what a caller wants to omit is a suggestion rather than a
    /// declaration, and it would weaken the planner metadata expressed over it for every read as well.
    /// </para>
    /// <para>
    /// <see cref="ColumnStrategy"/> is Calcite's own separation of the two things being confused here:
    /// <em>not null in the table</em> and <em>optional in an insert</em>. A <c>DEFAULT</c> column may be
    /// omitted and defaults to null, which the write path reads as "not supplied"; a <c>VIRTUAL</c>
    /// column may be read but not written, so the validator refuses to let one be named.
    /// </para>
    /// </remarks>
    class CosmosColumnStrategies : NullInitializerExpressionFactory
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The table whose columns are described.</param>
        /// <exception cref="ArgumentNullException"><paramref name="table"/> is <c>null</c>.</exception>
        public CosmosColumnStrategies(CosmosTable table)
        {
            _ = table ?? throw new ArgumentNullException(nameof(table));
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// <b>The document column is the only one a statement may write.</b> It is <c>DEFAULT</c>;
        /// everything else is <c>STORED</c>, which makes naming one a validation error rather than
        /// something the adapter drops later without comment.
        /// </para>
        /// <para>
        /// Every other column is a projection of the document, so writing one would be describing the
        /// same document twice and asking the adapter to reconcile it. <c>_ts</c> and <c>_etag</c>
        /// could never be written anyway — the service assigns both on every write. The rest could
        /// have been, and are not on purpose: a derived column addresses a path inside the document,
        /// so writing one means building that path into the document being written, which for a
        /// nested declaration is several levels of it. The document column already says all of that,
        /// unambiguously.
        /// </para>
        /// <para>
        /// It is also what the patch tier needs. A targeted change has to arrive as an expression
        /// over the document — <c>SET "DOC" = JSON_SET(c."DOC", '$.category', 'x')</c> — because that
        /// is the form a rule can read a patch operation off. A <c>SET</c> of a projected column
        /// carries the same intent in a shape nothing can decompose.
        /// </para>
        /// <para>
        /// <c>STORED</c> rather than <c>VIRTUAL</c>, and the difference is not cosmetic. Both refuse a
        /// write, but <c>VIRTUAL</c> also means <em>not stored</em>: measured, it makes
        /// <c>RelOptTableImpl.toRel</c> drop the column from the scan and project a literal null in
        /// its place. These columns are read constantly.
        /// </para>
        /// </remarks>
        public override ColumnStrategy generationStrategy(RelOptTable table, int iColumn)
        {
            return iColumn == CosmosImplementor.DocumentColumnOrdinal
                ? ColumnStrategy.DEFAULT
                : ColumnStrategy.STORED;
        }

    }

}
