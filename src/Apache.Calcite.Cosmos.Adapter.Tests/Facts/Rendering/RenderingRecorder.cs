using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The observer one statement's planning arms: it keeps what the two sites write, and reads nothing of the facts while
    /// planning runs, so that it opens no context planning did not.
    /// </summary>
    internal sealed class RenderingRecorder : ICosmosRenderingObserver
    {

        readonly List<CaptureRecord> _records = [];

        /// <summary>Gets the records, in the order written.</summary>
        public IReadOnlyList<CaptureRecord> Records
        {
            get
            {
                lock (_records)
                    return [.. _records];
            }
        }

        /// <summary>Records every conjunct the flow writes until disposed.</summary>
        public IDisposable Arm() => CosmosRenderingCapture.Arm(this);

        public void Admitted(CosmosFilterStatement statement, int conjunct, string text, CosmosParameterList parameters) =>
            Add(new(1, statement, conjunct, text, [.. parameters.Parameters], [.. parameters.Comparisons], [.. parameters.Rechecks]));

        // The list is the statement's, so the conjunct's own are those its text names; a name is never bound twice.
        public void Written(CosmosFilterStatement statement, int conjunct, string text, CosmosParameterList parameters)
        {
            var names = CosmosSqlFormReader.Names(text);
            Add(new(2, statement, conjunct, text, [.. parameters.Parameters.Where(p => names.Contains(p.Name))], [], []));
        }

        void Add(CaptureRecord record)
        {
            lock (_records)
                _records.Add(record);
        }

    }

}
