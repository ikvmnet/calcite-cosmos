using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;

using com.google.common.collect;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.type;
using org.apache.calcite.schema;
using org.apache.calcite.schema.impl;
using org.apache.calcite.sql.type;
using org.apache.calcite.util;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// A Cosmos container exposed to Calcite as a table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row type is one document column carrying the whole document, plus promoted scalar columns
    /// for paths the service guarantees or the container declares. A Cosmos query returns exactly one
    /// JSON value per row, so the document column is the faithful representation and everything inside
    /// a document is addressed from it with the SQL/JSON functions; the promoted columns exist so that
    /// planner metadata expressed over field ordinals — keys, collations, distribution — has something
    /// to refer to, which no call can carry.
    /// </para>
    /// <para>
    /// Nothing here is inferred from sampling documents. <c>id</c> and the system properties keep the
    /// names the service gives them; a declared path is named for the JSON path it addresses, so a
    /// nested one such as <c>/inventory/sku</c> promotes as <c>$.inventory.sku</c> rather than not at
    /// all.
    /// </para>
    /// </remarks>
    public class CosmosTable : AbstractTable, TranslatableTable
    {

        readonly CosmosContainerMetadata _container;
        readonly CosmosConvention _convention;
        readonly ICosmosQueryExecutor? _executor;
        readonly CosmosColumnStrategies _strategies;
        readonly Client.CosmosLookupCache? _lookupCache;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="container">The container this table exposes.</param>
        /// <param name="executor">Executes statements against the container, or <c>null</c> to expose a table that can be planned against but not read.</param>
        /// <param name="lookupCache">The lookup join's cache across executions, or <c>null</c> where none is configured.</param>
        /// <exception cref="ArgumentNullException"><paramref name="container"/> is <c>null</c>.</exception>
        public CosmosTable(CosmosContainerMetadata container, ICosmosQueryExecutor? executor = null, Client.CosmosLookupCache? lookupCache = null)
        {
            _container = container ?? throw new ArgumentNullException(nameof(container));
            _convention = CosmosConvention.Create(container);
            _executor = executor;
            _strategies = new CosmosColumnStrategies(this);
            _lookupCache = lookupCache;
        }

        /// <summary>
        /// Gets the lookup join's cache across executions, or <c>null</c> where none is configured.
        /// </summary>
        /// <remarks>
        /// Owned here for the reason the executor is: the compiled plan navigates to this table
        /// through the schema the query executes against, and what hangs off the table shares the
        /// schema's lifetime and its declared policy. See <c>DESIGN.md</c> under <em>The lookup
        /// join's caches</em>.
        /// </remarks>
        public Client.CosmosLookupCache? LookupCache => _lookupCache;

        /// <inheritdoc />
        /// <remarks>
        /// Overridden for one class only. <see cref="AbstractTable"/> answers by asking whether the
        /// table <em>is</em> what was requested, and an <c>INSERT</c> needs the table to supply an
        /// <c>InitializerExpressionFactory</c> — which this is not and should not become, the two
        /// having nothing to do with each other beyond both being facts about the same columns.
        /// Without it a write does not reach a rule; see <see cref="CosmosColumnStrategies"/>.
        /// </remarks>
        public override object? unwrap(java.lang.Class clazz)
        {
            if (clazz is not null && clazz.isInstance(_strategies))
                return _strategies;

            return base.unwrap(clazz);
        }

        /// <summary>
        /// Gets the container this table exposes.
        /// </summary>
        public CosmosContainerMetadata Container => _container;

        /// <summary>
        /// Gets what executes statements against this container, or <c>null</c> when nothing can.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Read by the converters out of the Cosmos convention, and read <em>when the plan runs</em> rather
        /// than when it is built: the expression they emit navigates to this table through the schema the
        /// query is executing against. A live client written into the compiled plan would tie that plan —
        /// which Calcite caches and re-executes — to whichever schema instance happened to be current when
        /// it was compiled.
        /// </para>
        /// <para>
        /// A table built from metadata alone has none. Planning is unaffected, since nothing about a
        /// statement or its cost depends on who runs it; enumerating the result of such a plan is what
        /// fails, and it fails saying so.
        /// </para>
        /// </remarks>
        public ICosmosQueryExecutor? Executor => _executor;

        /// <summary>
        /// Gets the calling convention for this container.
        /// </summary>
        /// <remarks>
        /// Held for the life of the table rather than created per scan. Conventions are traits and
        /// compare by identity, so a fresh instance per <see cref="toRel"/> would make two scans of
        /// the same container look like two different conventions and provoke converters between
        /// them.
        /// </remarks>
        public CosmosConvention Convention => _convention;

        /// <summary>
        /// Returns the names of the columns promoted alongside the document column, in order.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two kinds, and they are named by different rules because they come from different places.
        /// The service's own properties keep the names the service gives them — <c>id</c>, <c>_ts</c>,
        /// <c>_etag</c> — which is what every caller already writes. A declared path is named for the
        /// JSON path it addresses, <c>$.inventory.sku</c>, so that a nested one has a name at all.
        /// </para>
        /// <para>
        /// A declared path that is already one of the three is not promoted twice.
        /// </para>
        /// <para>
        /// <b>They exist for the planner, not for addressing.</b> Everything inside a document is
        /// reachable through the document column with the SQL/JSON functions. What those cannot carry
        /// is metadata: a Calcite key is an <c>ImmutableBitSet</c> over field ordinals, and
        /// nullability and predicate flow follow a <c>RexInputRef</c> rather than a call. So a path
        /// the container <em>declares or guarantees</em> — the only paths anything is known about —
        /// gets an ordinal to hang that on.
        /// </para>
        /// </remarks>
        /// <returns>The promoted column names.</returns>
        public IReadOnlyList<string> GetPromotedColumnNames()
        {
            var names = new List<string>
            {
                CosmosContainerMetadata.IdPropertyName,
                CosmosContainerMetadata.TimestampPropertyName,
                CosmosContainerMetadata.ETagPropertyName,
            };

            foreach (var path in _container.PartitionKeyPaths)
            {
                var trimmed = path.TrimStart('/');
                if (trimmed.Length == 0)
                    continue;

                // A declared path naming one of the service's own properties is that column, not a
                // second one under a derived name.
                if (trimmed.Contains('/') == false && names.Contains(trimmed))
                    continue;

                var name = CosmosImplementor.PromotedColumnName(path);
                if (names.Contains(name) == false)
                    names.Add(name);
            }

            return names;
        }

        /// <inheritdoc />
        public override RelDataType getRowType(RelDataTypeFactory typeFactory)
        {
            var varchar = typeFactory.createSqlType(SqlTypeName.VARCHAR);
            var any = typeFactory.createSqlType(SqlTypeName.ANY);

            var builder = typeFactory.builder();

            // The document itself, and the only way into what is inside it. Every Cosmos query
            // returns exactly one value per row and this is it; the promoted columns below are
            // projections of the same document, carried for the planner rather than for addressing.
            // NOT NULL because every row is a document.
            builder.add(CosmosImplementor.DocumentColumnName, varchar);

            foreach (var name in GetPromotedColumnNames())
            {
                // Only the service-generated properties are guaranteed present on every item.
                // A declared path is declared, but a document may omit it — such items land in the
                // "none" logical partition. Declaring it non-nullable would licence the planner to
                // rewrite COUNT(x) into COUNT(*) and to reason about null placement in ways the data
                // does not support.
                var type = name switch
                {
                    CosmosContainerMetadata.TimestampPropertyName => typeFactory.createSqlType(SqlTypeName.BIGINT),
                    CosmosContainerMetadata.IdPropertyName => varchar,
                    CosmosContainerMetadata.ETagPropertyName => varchar,
                    _ => typeFactory.createTypeWithNullability(any, true),
                };

                builder.add(name, type);
            }

            return builder.build();
        }

        /// <summary>
        /// Returns the ordinal of the column carrying a declared path, or <c>-1</c>.
        /// </summary>
        /// <remarks>
        /// The document column occupies ordinal zero, so promoted columns begin at one. A path is
        /// looked up under both rules: the service's own properties keep their names, and everything
        /// else is derived.
        /// </remarks>
        /// <param name="policyPath">A path in policy form, such as <c>/inventory/sku</c>.</param>
        /// <returns>The field ordinal, or <c>-1</c>.</returns>
        public int GetColumnOrdinal(string policyPath)
        {
            if (string.IsNullOrEmpty(policyPath))
                return -1;

            var trimmed = policyPath.TrimStart('/');
            if (trimmed.Length == 0)
                return -1;

            var derived = CosmosImplementor.PromotedColumnName(policyPath);
            var promoted = GetPromotedColumnNames();

            for (var i = 0; i < promoted.Count; i++)
                if (string.Equals(promoted[i], trimmed, StringComparison.Ordinal)
                    || string.Equals(promoted[i], derived, StringComparison.Ordinal))
                    return i + 1;

            return -1;
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// Derived entirely from declared facts. <c>id</c> is unique within a logical partition, so
        /// the partition key together with <c>id</c> is unique across the container. Every declared
        /// path is promoted, nested ones included, so the key is expressible wherever a partition key
        /// is declared at all — which it was not while a column name was a path's last segment.
        /// </para>
        /// <para>
        /// Row count comes from the service where the container was read from one, and is left unknown
        /// otherwise. It is a measurement rather than a sample: the rule against inferring from
        /// documents is about the <em>shape</em> of the data, where a wrong guess costs correctness. A
        /// wrong row count costs speed.
        /// </para>
        /// </remarks>
        public override Statistic getStatistic()
        {
            var keys = new java.util.ArrayList();

            var partitionOrdinals = new java.util.ArrayList();
            var partitionPromoted = _container.PartitionKeyPaths.Count > 0;

            foreach (var path in _container.PartitionKeyPaths)
            {
                var ordinal = GetColumnOrdinal(path);
                if (ordinal < 0)
                {
                    partitionPromoted = false;
                    break;
                }

                partitionOrdinals.add(java.lang.Integer.valueOf(ordinal));
            }

            if (partitionPromoted)
            {
                var idOrdinal = GetColumnOrdinal("/" + CosmosContainerMetadata.IdPropertyName);
                if (idOrdinal >= 0)
                {
                    var bits = new java.util.ArrayList(partitionOrdinals);
                    bits.add(java.lang.Integer.valueOf(idOrdinal));
                    keys.add(ImmutableBitSet.builder().addAll(bits).build());
                }
            }

            // NO COLLATIONS ARE REPORTED, and a composite index is not one.
            //
            // A statistic's collations are the order a scan's rows already arrive in —
            // RelOptTableImpl.getCollationList returns them and RelMdCollation reports them as the
            // collation of the scan — so claiming one licences the planner to drop a Sort that asked
            // for it. Cosmos guarantees no order without an ORDER BY, whatever is indexed: a composite
            // index makes a multi-key ORDER BY *legal*, it does not sort a query that has none.
            //
            // That legality is a question for the rule that decides whether the sort may be pushed, and
            // CosmosSortRule already asks it through CosmosContainerMetadata.IsSortSupported. Cassandra
            // does the same with its clustering order, which unlike this really is the storage order.

            // A row count where the service gave one. It is approximate and lags, which is what a
            // planner row count is allowed to be; what it must not be is invented, and this is read
            // rather than sampled. Without it the planner compares plans with no sense of scale.
            var rowCount = _container.Statistics is CosmosContainerStatistics statistics
                ? java.lang.Double.valueOf(statistics.DocumentCount)
                : null;

            return Statistics.of(rowCount, keys, java.util.Collections.emptyList(), java.util.Collections.emptyList());
        }

        /// <inheritdoc />
        public RelNode toRel(RelOptTable.ToRelContext context, RelOptTable relOptTable)
        {
            var cluster = context.getCluster();
            return new CosmosTableScan(cluster, cluster.traitSetOf(_convention), relOptTable);
        }

    }

}
