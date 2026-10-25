using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The observer one statement's planning arms: it keeps what the three sites write, and reads nothing of the facts while
    /// planning runs, so that it opens no context planning did not.
    /// </summary>
    internal sealed class RenderingRecorder : ICosmosRenderingObserver
    {

        readonly List<CaptureRecord> _records = [];
        readonly List<CosmosFilterStatement> _read = [];
        readonly List<(CosmosFilterStatement Statement, bool Implementing)> _admissions = [];

        /// <summary>Gets the records, in the order written.</summary>
        public IReadOnlyList<CaptureRecord> Records
        {
            get
            {
                lock (_records)
                    return [.. _records];
            }
        }

        /// <summary>Gets every statement whose rounds were admitted while armed, in the order first asked, a conjunct rendered or none.</summary>
        public IReadOnlyList<CosmosFilterStatement> Read
        {
            get
            {
                lock (_records)
                    return [.. _read];
            }
        }

        /// <summary>
        /// Gets each admission a statement began while armed, its first round, and whether implementation had begun: a
        /// statement admits once, in planning (<c>ImplementationNeverAdmitsAgain</c>).
        /// </summary>
        public IReadOnlyList<(CosmosFilterStatement Statement, bool Implementing)> Admissions
        {
            get
            {
                lock (_records)
                    return [.. _admissions];
            }
        }

        /// <summary>Whether the flow's plan is being implemented; set once planning is done.</summary>
        public bool Implementing { get; set; }

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

        // The list also holds what the attempts before the weakening bound, so its own are again those its text names.
        public void Weakened(CosmosFilterStatement statement, int conjunct, string text, CosmosParameterList parameters)
        {
            var names = CosmosSqlFormReader.Names(text);
            Add(new(3, statement, conjunct, text, [.. parameters.Parameters.Where(p => names.Contains(p.Name))],
                [.. parameters.Comparisons.Where(c => names.Contains(c.Parameter))], []));
        }

        void ICosmosRenderingObserver.Read(CosmosFilterStatement statement, AdmissionRound round)
        {
            lock (_records)
            {
                if (_read.Contains(statement) == false)
                    _read.Add(statement);

                // An admission begins at its first round; the rounds run again where S's composite fails are the same one.
                if (round is { Number: 1, Again: false, Closing: false })
                    _admissions.Add((statement, Implementing));
            }
        }

        void Add(CaptureRecord record)
        {
            lock (_records)
                _records.Add(record);
        }

    }

}
