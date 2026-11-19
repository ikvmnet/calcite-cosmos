using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.plan;
using org.apache.calcite.plan.hep;
using org.apache.calcite.plan.volcano;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rex;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// Binds the columns a correlate's right side adds to its row, for the correlate rule's question alone
    /// (<see cref="CosmosImplementor.TryBindElements"/>): the element a traversal of an array yields, and a lateral's
    /// accessor columns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The element of a traversal.</b> The right side is walked as registered, through projections of input references
    /// (the rename <c>AS u(t)</c> registers) and its own filters, to an <c>Uncollect</c> of one column, with no ordinality,
    /// whose input projects one expression: a plain <c>JSON_QUERY … RETURNING &lt;type&gt; ARRAY</c> over the correlation
    /// variable alone. Resolved over the left's binding, that is the array's path, and the element binds to it with a yielded
    /// step (<see cref="CosmosPathSegment.Yielded"/>) naming the array's component type and the traversal's number, read as
    /// <see cref="CosmosReading.Text"/>. The type is data: what an element of it holds and reads as is the facts graph's rows',
    /// and a type no row names reads nothing. <c>StringToArray</c>'s element is no plain <c>JSON_QUERY</c>'s, and stays
    /// unbound.
    /// </para>
    /// <para>
    /// <b>A lateral's column.</b> Through projections of input references and the lateral's own filters to its projection over
    /// a one-tuple <c>VALUES</c>: a column that reads the correlation variable alone and resolves over the left's binding, the
    /// text accessor, a <c>RETURNING</c>, a plain <c>JSON_QUERY</c> or a field of the left passed through, binds to that path
    /// with the reading the walk's projection arm gives such a column. A field of the correlation variable is the left's field
    /// of that ordinal, so it is read as one, and an accessor over a field the left reads as text is refused as it is over a
    /// field reference. A computed column, and anything else, stays unbound.
    /// </para>
    /// <para>
    /// <b>Numbered in the walk's order.</b> One instance numbers one binding's traversals as the walk meets them, the left's
    /// before the right's, so that one plan shape loads alike and hits the oracle memo. One traversal is one number wherever
    /// its column surfaces, and two traversals of one array are two.
    /// </para>
    /// <para>
    /// Reading in, every piece: which column is which element or path. Nothing about the documents is decided here.
    /// </para>
    /// </remarks>
    internal sealed class CosmosTraversalColumns
    {

        /// <summary>A column bound to no path, read as its type.</summary>
        static (CosmosPath? Path, CosmosReading Reading) Unbound => (null, CosmosReading.Typed);

        readonly Dictionary<RelNode, int> _numbers = new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

        /// <summary>
        /// Binds a correlate's right columns over its left's binding, into <paramref name="paths"/> and
        /// <paramref name="readings"/> from the left's field count on.
        /// </summary>
        /// <param name="correlate">The correlate, as registered.</param>
        /// <param name="left">The left's binding.</param>
        /// <param name="leftReadings">How each of the left's fields is read.</param>
        /// <param name="paths">The correlate's row's binding, the left's fields already in place.</param>
        /// <param name="readings">How each of the row's fields is read, the left's already in place.</param>
        public void Bind(Correlate correlate, IReadOnlyList<CosmosPath?> left, IReadOnlyList<CosmosReading> leftReadings, CosmosPath?[] paths, CosmosReading[] readings)
        {
            var offset = correlate.getLeft().getRowType().getFieldCount();
            for (var i = offset; i < paths.Length; i++)
                (paths[i], readings[i]) = Column(correlate, correlate.getRight(), i - offset, left, leftReadings, CosmosPlanMembers.NewSeen());
        }

        /// <summary>
        /// Follows one column of the right side down to what it holds.
        /// </summary>
        (CosmosPath? Path, CosmosReading Reading) Column(Correlate correlate, RelNode node, int field, IReadOnlyList<CosmosPath?> left, IReadOnlyList<CosmosReading> leftReadings, HashSet<RelNode> seen)
        {
            if (Original(node) is not RelNode member || seen.Add(member) == false)
                return Unbound;

            switch (member)
            {
                case Filter filter:
                    return Column(correlate, filter.getInput(), field, left, leftReadings, seen);

                case Project project when field >= 0 && field < project.getProjects().size():
                {
                    var expression = (RexNode)project.getProjects().get(field);
                    if (expression is RexInputRef reference)
                        return Column(correlate, project.getInput(), reference.getIndex(), left, leftReadings, seen);

                    return OverOneTuple(project.getInput(), CosmosPlanMembers.NewSeen()) ? Lateral(correlate, expression, left, leftReadings) : Unbound;
                }

                case Uncollect uncollect when field == 0:
                    return Element(correlate, uncollect, left, leftReadings);

                default:
                    return Unbound;
            }
        }

        /// <summary>
        /// The element an uncollect yields, where its input projects the array a plain <c>JSON_QUERY</c> returns over the
        /// correlation variable.
        /// </summary>
        (CosmosPath? Path, CosmosReading Reading) Element(Correlate correlate, Uncollect uncollect, IReadOnlyList<CosmosPath?> left, IReadOnlyList<CosmosReading> leftReadings)
        {
            if (uncollect.withOrdinality || uncollect.getRowType().getFieldCount() != 1
                || Original(uncollect.getInput()) is not Project project || project.getProjects().size() != 1)
                return Unbound;

            var array = (RexNode)project.getProjects().get(0);
            if (CosmosRexShapes.IsPlainJsonQuery(array) == false || array.getType()?.getSqlTypeName() != SqlTypeName.ARRAY
                || array.getType().getComponentType()?.getSqlTypeName()?.getName() is not string type
                || Resolve(correlate, array, left, leftReadings) is not (CosmosPath at, _))
                return Unbound;

            if (_numbers.TryGetValue(uncollect, out var number) == false)
                _numbers[uncollect] = number = _numbers.Count;

            return (at.Append(CosmosPathSegment.Yielded(type, number)), CosmosReading.Text);
        }

        /// <summary>
        /// A lateral's column: the document path an expression over the correlation variable alone resolves to, read as the
        /// walk's projection arm reads such a column.
        /// </summary>
        static (CosmosPath? Path, CosmosReading Reading) Lateral(Correlate correlate, RexNode expression, IReadOnlyList<CosmosPath?> left, IReadOnlyList<CosmosReading> leftReadings)
        {
            if (Resolve(correlate, expression, left, leftReadings) is not (CosmosPath path, RexNode own))
                return Unbound;

            // The element a traversal yields passes through as the field it is, and no further: an accessor over it reads its
            // text parsed, which is no path.
            if (path.HasYielded && own is not RexInputRef)
                return Unbound;

            return (path,
                CosmosRexShapes.IsTextJsonValue(own) ? CosmosReading.Text
                : CosmosRexShapes.IsPlainJsonQuery(own) ? CosmosReading.JsonText
                : own is RexInputRef reference && reference.getIndex() < leftReadings.Count ? leftReadings[reference.getIndex()]
                : CosmosReading.Typed);
        }

        /// <summary>
        /// Resolves an expression over the correlation variable alone to the path it addresses over the left's binding, each
        /// field of the correlate's own variable written as the left's field it stands for; <c>null</c> where it reads the right's
        /// own row, another variable, or no path.
        /// </summary>
        static (CosmosPath Path, RexNode Own)? Resolve(Correlate correlate, RexNode expression, IReadOnlyList<CosmosPath?> left, IReadOnlyList<CosmosReading> leftReadings)
        {
            // An input reference here reads the right's own row, the VALUES or the projected array, and not the document.
            if (RelOptUtil.InputFinder.bits(expression).isEmpty() == false)
                return null;

            var own = (RexNode)expression.accept(new OwnRow(correlate.getCorrelationId()));
            return new CosmosRexPaths(left, spelled: false, leftReadings).TryResolvePath(own, out var path) && path is not null ? (path, own) : null;
        }

        /// <summary>
        /// Whether a lateral's projection reads one row: its own filters over a one-tuple <c>VALUES</c>.
        /// </summary>
        static bool OverOneTuple(RelNode node, HashSet<RelNode> seen)
        {
            while (Original(node) is RelNode member && seen.Add(member))
            {
                if (member is Values values)
                    return values.getTuples().size() == 1;

                if (member is not Filter filter)
                    return false;

                node = filter.getInput();
            }

            return false;
        }

        /// <summary>
        /// The node an input stands for as registered: an equivalence set's original, or its best, a vertex's current node, or
        /// the node itself.
        /// </summary>
        static RelNode? Original(RelNode? node) => node switch
        {
            RelSubset subset => subset.getOriginal() ?? subset.getBest(),
            HepRelVertex vertex => vertex.getCurrentRel(),
            _ => node,
        };

        /// <summary>
        /// Writes each field of one correlation variable as the input reference of the same ordinal: the left's field it stands
        /// for, in the correlate's row.
        /// </summary>
        sealed class OwnRow : RexShuttle
        {

            readonly CorrelationId _id;

            public OwnRow(CorrelationId id)
            {
                _id = id;
            }

            public override RexNode visitFieldAccess(RexFieldAccess access)
            {
                if (access.getReferenceExpr() is RexCorrelVariable variable && _id.equals(variable.id))
                    return new RexInputRef(access.getField().getIndex(), access.getType());

                return base.visitFieldAccess(access);
            }

        }

    }

}
