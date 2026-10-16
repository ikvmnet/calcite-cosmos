using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;

using org.apache.calcite.rex;
using org.apache.calcite.sql;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// A filter as the predicate oracle reads it: its conjuncts loaded, admitted stratum by stratum by the translator, and
    /// every question a rule asks of it answered from the facts the question's licence admits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No rule chooses the facts it reads.</b> Each question a filter's rules ask is one of a few kinds, and the kind
    /// names the facts (<see cref="CosmosStatementScope"/>, the licence table):
    /// </para>
    /// <list type="table">
    /// <item><term>whether a conjunct renders, and how (<see cref="Renders"/>, <see cref="IsAdmitted"/>,
    /// <see cref="FactsFor"/>, <see cref="Translate"/>)</term><description>the stratum before the one that admitted it, or
    /// for one no stratum admitted, the last: what its siblings applied beside it establish, never what it does</description></item>
    /// <item><term>a reader that names no filter: routing, a key (<see cref="CosmosOracle.Declared"/>)</term><description>the
    /// declaration</description></item>
    /// </list>
    /// <para>
    /// <b>Remembered by the condition itself.</b> A rule is asked the same filter many times — each time Volcano matches it,
    /// and again when it is implemented — and a condition is an immutable node the planner hands back unchanged, so a
    /// statement is remembered against the node, with the binding it was read through, and released with it. What the
    /// oracle remembers beneath it is keyed by the statement's digest, so two nodes spelling one filter share contexts.
    /// </para>
    /// </remarks>
    internal sealed class CosmosFilterStatement
    {

        /// <summary>
        /// The statements read off each condition, by binding: a condition stands in more than one filter only where a
        /// rule copies one over another input.
        /// </summary>
        static readonly ConditionalWeakTable<RexNode, List<CosmosFilterStatement>> Read = new();

        readonly IReadOnlyList<CosmosPath?> _fields;
        readonly IReadOnlyList<CosmosReading> _readings;
        readonly CosmosContainerMetadata _container;
        readonly RexBuilder _rexBuilder;
        readonly string _rootAlias;
        readonly CosmosPredicateLoader.Loading _loading;
        readonly object _lock = new();
        CosmosStatementScope? _strata;

        CosmosFilterStatement(RexNode condition, IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading> readings, CosmosContainerMetadata container, RexBuilder rexBuilder, string rootAlias)
        {
            Condition = condition;
            _fields = fields;
            _readings = readings;
            _container = container;
            _rexBuilder = rexBuilder;
            _rootAlias = rootAlias;
            _loading = new CosmosPredicateLoader.Loading(fields, readings, rootAlias);

            Conjuncts = ConjunctsOf(condition);
            Statement = _loading.Statement(Conjuncts);
        }

        /// <summary>
        /// Reads a condition's conjuncts as the translator writes a conjunction: every <c>AND</c> taken apart, depth first
        /// and left to right, and everything else a conjunct.
        /// </summary>
        /// <remarks>
        /// Not <c>RelOptUtil.conjunctions</c>, which drops what Calcite calls always true — a literal <c>TRUE</c>, and an
        /// <c>IS NOT NULL</c> over a column it types as never null — which the translator still writes, and must still
        /// render, and the rules must still be told renders.
        /// </remarks>
        internal static IReadOnlyList<RexNode> ConjunctsOf(RexNode condition)
        {
            var conjuncts = new List<RexNode>();
            Decompose(condition, conjuncts);
            return conjuncts;
        }

        static void Decompose(RexNode node, List<RexNode> conjuncts)
        {
            if (node is RexCall call && call.getKind() == SqlKind.AND)
            {
                for (var i = 0; i < call.getOperands().size(); i++)
                    Decompose((RexNode)call.getOperands().get(i), conjuncts);

                return;
            }

            conjuncts.Add(node);
        }

        /// <summary>
        /// Returns the statement a filter's condition is, read through its input's binding: the same one each time the
        /// same condition is asked about through the same binding.
        /// </summary>
        /// <param name="condition">The condition.</param>
        /// <param name="fields">The ordinal-to-path binding of the filtered input.</param>
        /// <param name="readings">How each field is read back, or <c>null</c> where every field is read as its type.</param>
        /// <param name="container">The container, whose declaration the oracle reasons from.</param>
        /// <param name="rexBuilder">A builder over the condition's types, for the translator.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <returns>The statement.</returns>
        public static CosmosFilterStatement Of(RexNode condition, IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading>? readings, CosmosContainerMetadata container, RexBuilder rexBuilder, string rootAlias = CosmosImplementor.DefaultRootAlias)
        {
            if (condition is null)
                throw new ArgumentNullException(nameof(condition));
            if (fields is null)
                throw new ArgumentNullException(nameof(fields));
            if (container is null)
                throw new ArgumentNullException(nameof(container));

            readings ??= Array.Empty<CosmosReading>();

            var read = Read.GetValue(condition, _ => new List<CosmosFilterStatement>());
            lock (read)
            {
                foreach (var statement in read)
                    if (ReferenceEquals(statement._container, container) && statement._rootAlias == rootAlias
                        && statement._fields.SequenceEqual(fields) && statement._readings.SequenceEqual(readings))
                        return statement;

                var made = new CosmosFilterStatement(condition, fields, readings, container, rexBuilder, rootAlias);
                read.Add(made);
                return made;
            }
        }

        /// <summary>
        /// Gets the condition.
        /// </summary>
        public RexNode Condition { get; }

        /// <summary>
        /// Gets the condition's top-level conjuncts: every <c>AND</c> taken apart, depth first.
        /// </summary>
        public IReadOnlyList<RexNode> Conjuncts { get; }

        /// <summary>
        /// Gets the conjuncts in the IR, parallel to <see cref="Conjuncts"/>.
        /// </summary>
        public PredicateStatement Statement { get; }

        CosmosOracle Oracle => CosmosOracle.For(_container.Facts);

        /// <summary>
        /// The statement admitted by the translator, stratum by stratum.
        /// </summary>
        CosmosStatementScope Strata
        {
            get
            {
                lock (_lock)
                    return _strata ??= Oracle.Admit(Statement, (i, facts) => Translator(facts, new CosmosParameterList()).TryTranslate(Conjuncts[i], out _));
            }
        }

        CosmosRexTranslator Translator(JsonFactSet facts, CosmosParameterList parameters) =>
            new(_rexBuilder, _fields, parameters, null, _container, _readings, facts);

        // ── Rendering (L2) ───────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Gets whether every conjunct renders, each from the stratum before the one that admitted it: whether the filter
        /// pushes whole.
        /// </summary>
        public bool Renders => Strata.Residual.Count == 0;

        /// <summary>
        /// Determines whether a stratum admitted a conjunct: whether it reaches the service.
        /// </summary>
        public bool IsAdmitted(int conjunct) => StratumOf(conjunct) > 0;

        /// <summary>
        /// Returns the round that admitted a conjunct, counting from one, or zero where none did: one renders from the
        /// declaration alone, and each round after from what the rounds before it admitted.
        /// </summary>
        public int StratumOf(int conjunct) => Strata.StratumOf(conjunct);

        /// <summary>
        /// Returns the facts a conjunct is rendered from: the stratum before the one that admitted it, or the last for one
        /// no stratum admitted. A weakening of a conjunct left above is rendered from them too, beside every admitted one.
        /// </summary>
        public JsonFactSet FactsFor(int conjunct) => Strata.Facts(OracleLicence.RenderConjunct(conjunct));

        /// <summary>
        /// Returns a translator that renders a conjunct from the facts its licence admits.
        /// </summary>
        public CosmosRexTranslator TranslatorFor(int conjunct, CosmosParameterList parameters) => Translator(FactsFor(conjunct), parameters);

        /// <summary>
        /// Renders the condition, each conjunct from its own stratum, as the translator renders a conjunction: a chain of
        /// <c>AND</c> as written, and each conjunct within it on its own.
        /// </summary>
        /// <param name="parameters">Receives the bound values, in the order the conjuncts are written.</param>
        /// <returns>The Cosmos SQL text.</returns>
        /// <exception cref="CosmosTranslationException">A conjunct has no rendering from the facts its licence admits.</exception>
        public string Translate(CosmosParameterList parameters)
        {
            var builder = new StringBuilder();
            var next = 0;

            void Write(RexNode node)
            {
                if (node is RexCall call && call.getKind() == SqlKind.AND)
                {
                    builder.Append('(');
                    for (var i = 0; i < call.getOperands().size(); i++)
                    {
                        if (i > 0)
                            builder.Append(" AND ");

                        Write((RexNode)call.getOperands().get(i));
                    }

                    builder.Append(')');
                    return;
                }

                // Decompose reads a nested AND depth first, left to right: the order written here.
                var conjunct = next++;
                builder.Append(TranslatorFor(conjunct, parameters).Translate(node));
            }

            Write(Condition);
            return builder.ToString();
        }

    }

}
