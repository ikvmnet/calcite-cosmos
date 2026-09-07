using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;

using org.apache.calcite.schema;
using org.apache.calcite.schema.impl;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// A Cosmos database exposed to Calcite as a schema, with one table per container.
    /// </summary>
    /// <remarks>
    /// Containers must be supplied explicitly rather than discovered by sampling. A container's
    /// declared metadata — its partition key and indexing policy — is read from the container
    /// definition; its document shape is not knowable and is not guessed.
    /// </remarks>
    public class CosmosSchema : AbstractSchema
    {

        readonly java.util.Map _tables = new java.util.LinkedHashMap();

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="containers">The containers to expose.</param>
        /// <param name="executorFactory">
        /// Returns what executes statements against a container. Omit it to expose tables that can be
        /// planned against but not read — which is what a schema built from metadata alone can offer.
        /// </param>
        /// <param name="lookupCacheFactory">
        /// Returns a container's lookup cache across executions, or <c>null</c> for none. One instance
        /// per container: the cache shares the schema's lifetime and its declared freshness policy.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="containers"/> is <c>null</c>.</exception>
        public CosmosSchema(
            IEnumerable<CosmosContainerMetadata> containers,
            Func<CosmosContainerMetadata, ICosmosQueryExecutor>? executorFactory = null,
            Func<CosmosContainerMetadata, CosmosLookupCache?>? lookupCacheFactory = null)
        {
            if (containers is null)
                throw new ArgumentNullException(nameof(containers));

            foreach (var container in containers)
                _tables.put(container.Name, new CosmosTable(container, executorFactory?.Invoke(container), lookupCacheFactory?.Invoke(container)));
        }

        /// <summary>
        /// Forgets every container's fetched row count, so that the next plan to ask reads it again.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The schema is what a host holds across connections, so it is what a host can say this to.
        /// Per container the work is <see cref="CosmosContainerMetadata.RefreshStatistics"/>, which
        /// forgets rather than fetches — so this costs nothing at the moment it is called and is
        /// paid for only by the containers something goes on to plan against.
        /// </para>
        /// <para>
        /// Deliberately not a SQL statement. Drill spells this <c>ANALYZE TABLE COMPUTE STATISTICS</c>
        /// and backs it with a metastore, which decouples the fetch from the query and is the better
        /// shape for a system that has one; here the statistic is two round trips against a live
        /// account rather than a stored artefact, and a method on the object the caller already holds
        /// asks for no parser extension and no new dialect to explain.
        /// </para>
        /// </remarks>
        public void RefreshStatistics()
        {
            var tables = _tables.values().iterator();
            while (tables.hasNext())
                ((CosmosTable)tables.next()).Container.RefreshStatistics();
        }

        /// <inheritdoc />
        protected override java.util.Map getTableMap()
        {
            return _tables;
        }

        /// <summary>
        /// Declares the Cosmos functions, so that a connection can name one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The same operators <see cref="Sql.CosmosOperators.Instance"/> offers, reached the other
        /// way: a validator resolves a name against its operator table chained with the catalog
        /// reader, and the catalog reader resolves the schema's own functions. Chaining the table is
        /// something a host does to a planner it built; this is what an application that opens a
        /// connection against a model document finds without being told.
        /// </para>
        /// <para>
        /// <b>Here as well as on <see cref="CosmosAccountSchema"/>, and that is the decision.</b> The
        /// functions are account-wide in meaning, so declaring them on the account alone would be the
        /// tidier story — but an unqualified name is resolved against the connection's default schema
        /// and the root, and nothing else. A model naming a <c>database</c> roots the connection
        /// here, with no account schema above it to carry them; a model naming an account roots it
        /// there, and a query still has to be able to name one from a database it has descended into.
        /// Declaring them at both levels is what makes the name work wherever a query is rooted, and
        /// costs a resolution that finds the same function twice in no arrangement — the two levels
        /// are never both searched for one unqualified name.
        /// </para>
        /// </remarks>
        protected override com.google.common.collect.Multimap getFunctionMultimap()
        {
            return Sql.CosmosSchemaFunctions.Instance;
        }

    }

}
