using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.metadata;
using org.apache.calcite.rel.type;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// Traversal of an array nested within each document, rendered as <c>JOIN alias IN path</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Despite the keyword, Cosmos <c>JOIN</c> is not a relational join — it has no predicate in
    /// the grammar at all. It cross-products a document with one of its own arrays, which is
    /// <c>UNNEST</c> spelled <c>JOIN</c>. This node therefore arises from <c>Uncollect</c> and
    /// <c>Correlate</c>, never from a <c>Join</c>.
    /// </para>
    /// <para>
    /// The output row type is the input's fields followed by one field carrying the array element.
    /// </para>
    /// </remarks>
    public class CosmosUnnest : SingleRel, CosmosRel
    {

        readonly RexNode _array;
        readonly RelDataType _rowType;
        readonly org.apache.calcite.rel.core.CorrelationId _correlationId;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the Cosmos convention.</param>
        /// <param name="input">The input node.</param>
        /// <param name="array">The expression producing the array, addressed against the input's row type.</param>
        /// <param name="rowType">The output row type: the input's fields plus the element field.</param>
        /// <param name="correlationId">
        /// The variable <paramref name="array"/> addresses the input by. A lateral traversal is
        /// correlated on its own input, so this names the row being scanned rather than some other
        /// side of a join — which is the distinction that decides whether the expression resolves to a
        /// document path or to nothing.
        /// </param>
        /// <exception cref="ArgumentNullException">Any of <paramref name="array"/>, <paramref name="rowType"/> or <paramref name="correlationId"/> is <c>null</c>.</exception>
        public CosmosUnnest(RelOptCluster cluster, RelTraitSet traitSet, RelNode input, RexNode array, RelDataType rowType, org.apache.calcite.rel.core.CorrelationId correlationId) :
            base(cluster, traitSet, input)
        {
            _array = array ?? throw new ArgumentNullException(nameof(array));
            _rowType = rowType ?? throw new ArgumentNullException(nameof(rowType));
            _correlationId = correlationId ?? throw new ArgumentNullException(nameof(correlationId));
        }

        /// <summary>
        /// Gets the expression producing the array being traversed.
        /// </summary>
        public RexNode Array => _array;

        /// <inheritdoc />
        protected override RelDataType deriveRowType()
        {
            return _rowType;
        }

        /// <inheritdoc />
        public override RelNode copy(RelTraitSet traitSet, java.util.List inputs)
        {
            return new CosmosUnnest(getCluster(), traitSet, (RelNode)inputs.get(0), _array, _rowType, _correlationId);
        }

        /// <inheritdoc />
        public override RelOptCost? computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq)
        {
            return base.computeSelfCost(planner, mq)?.multiplyBy(CosmosConvention.CostMultiplier);
        }

        /// <inheritdoc />
        public void Implement(CosmosImplementor implementor)
        {
            implementor.Visit(getInput());

            // A traversal multiplies rows. Folding one above an already-applied restriction would
            // traverse and then restrict, where the plan asked for the reverse.
            if (implementor.Query.HasRowLimit || implementor.Query.HasOrderBy || implementor.Query.HasGroupBy)
                throw new CosmosTranslationException("An array traversal cannot be applied above a sort, grouping, or row limit.");

            // DISTINCT de-duplicates what SELECT constructs, which the service does after the JOIN.
            // A traversal above one would therefore de-duplicate the multiplied rows, where the plan
            // asked for the rows of a distinct set to be multiplied.
            if (implementor.Query.Distinct)
                throw new CosmosTranslationException("An array traversal cannot be applied above a pushed-down DISTINCT.");

            if (implementor.CreateTranslator(_correlationId).TryResolvePath(_array, out var path) == false)
                throw new CosmosTranslationException($"The traversed array '{_array}' does not resolve to a document path.");

            var alias = implementor.CreateUnnestAlias();
            implementor.Query.AddUnnest(alias, path!.ToString());

            // The element is addressed by its alias; the input's bindings carry through unchanged.
            var readings = new List<CosmosReading>(implementor.Readings);
            var fields = new List<CosmosPath?>(implementor.Fields) { CosmosPath.Root(alias) };
            implementor.Fields = fields;

            // And so does how each of them is read, which setting Fields has just cleared. The
            // clearing is right in general — a rebinding usually means new output fields, and a
            // reading inherited across one would render a value that was never cast — but this
            // rebinding adds a column rather than replacing any, so the input's readings still
            // describe the input's columns. Without this the JSON column loses its reading here and
            // is read as the VARCHAR it is declared, which refuses the object it carries.
            while (readings.Count < fields.Count - 1)
                readings.Add(CosmosReading.Typed);

            readings.Add(CosmosReading.Typed);
            implementor.Readings = readings;

            // A projection below a traversal was written before the element existed, and Cosmos
            // evaluates SELECT after JOIN — so the object it constructs is still the right one, it
            // is simply a property short. Adding the element completes it. Without this the rows
            // come back missing the very column the traversal was for, which is a wrong answer
            // rather than a failure.
            //
            // Where nothing has projected yet, the converter projects every output field at the end
            // and picks the element up from the binding just set. See CosmosConverters.EnsureProjection.
            if (implementor.Query.HasProjection)
                implementor.Query.SelectProperty(ElementName(), alias);
        }

        /// <summary>
        /// The output field name carrying the array element.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The row type is the input's fields followed by the element's, so the element is the field
        /// after the last of the input's. Read from the row type rather than invented, because the
        /// name is what the converter reads the value back by — a Cosmos object constructor is keyed
        /// by output field name.
        /// </para>
        /// <para>
        /// Exactly one field, because <c>JOIN … IN</c> binds one alias to each element, and one
        /// binding is what <c>Implement</c> appends. An <c>Uncollect</c> yielding two columns per
        /// element — a map's key and value — would leave the second unaccounted for, and a Cosmos
        /// object constructor omits a property it was never given rather than complaining, so the
        /// consequence would be a wrong answer. Calcite names such an uncollect's columns through a
        /// projection, which the rule does not recognise as a traversal, so this is an assertion
        /// about the row type the rule builds rather than a case seen from SQL.
        /// </para>
        /// </remarks>
        /// <exception cref="CosmosTranslationException">The row type does not carry exactly one field for the element.</exception>
        string ElementName()
        {
            var index = getInput().getRowType().getFieldCount();
            var fields = _rowType.getFieldList();

            if (fields.size() != index + 1)
                throw new CosmosTranslationException($"An array traversal binds one column to the element, and this one carries {fields.size() - index}.");

            return ((RelDataTypeField)fields.get(index)).getName();
        }

    }

}
