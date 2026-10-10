using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Extensions.Adapter.Cursor;

using java.util.function;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.convert;
using org.apache.calcite.rel.core;
using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Converts a <see cref="Join"/> against a container into a <see cref="CosmosLookupJoin"/>, so that
    /// only the documents the other side's keys could match are fetched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the rule half of Flink's lookup join. Its counterpart there,
    /// <c>LogicalCorrelateToJoinFromTemporalTableRule</c>, is where the correlation variable goes: the
    /// rule consumes it and the physical node never sees one. Here there is not even that much to do,
    /// because the shape arrives as an ordinary equi-join and the keys are already ordinals.
    /// </para>
    /// <para>
    /// Declines far more than it accepts, and each refusal is a case where fetching per batch would
    /// answer a different question than the plan asked:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Inner and left joins only.</b> A left join keeps a build row nothing matched, with nulls for
    /// the container's columns, which a batch can answer as well as an inner join's drop (#193). A right
    /// or full join has to preserve the container's rows with no match, which a fetch by the build
    /// side's keys never reads; and a semi or anti join is a different operator. They fall through to
    /// being joined in process.
    /// </description></item>
    /// <item><description>
    /// <b>One equality, and nothing else in the condition.</b> A residual predicate would have to be
    /// applied after the fetch, and is not.
    /// </description></item>
    /// <item><description>
    /// <b>Nothing above the container's scan that a restriction would change the meaning of.</b> A
    /// <c>LIMIT</c> is the clear case: <c>TOP 5</c> of the container is not <c>TOP 5</c> of each batch,
    /// so running it per batch returns rows the plan did not ask for. Grouping is the same argument —
    /// a count per batch is not a count.
    /// </description></item>
    /// <item><description>
    /// <b>A key that resolves to a document path and can be a parameter.</b> A computed projection has
    /// no path for a <c>WHERE</c> to name, and a type Cosmos has no counterpart for cannot be bound —
    /// with one exception, the <c>UUID</c> cast of a path the container's facts give a canonical UUID
    /// form: that path holds one spelling per value, so a key written in it names exactly the documents
    /// whose cast equals the key. See <see cref="TryProbeKey"/>.
    /// </description></item>
    /// </list>
    /// </remarks>
    public class CosmosLookupJoinRule : ConverterRule
    {

        /// <summary>
        /// Determines whether a value of the given type can be carried as a key parameter.
        /// </summary>
        /// <remarks>
        /// The types Cosmos itself has: a string, a number, or a boolean. A parameter is serialised into
        /// JSON, and a value with no JSON counterpart would either fail there or match nothing.
        /// <para>
        /// <c>VARIANT</c> is refused, which is the type of every promoted partition key column and so
        /// costs this on a join between two containers. Keys are also compared in process once fetched,
        /// and a promoted key's concrete type is learned per row — the row model says only that it is a
        /// scalar the service holds, not which of string, number or boolean it is at this row, which is
        /// what a bound parameter would have to be serialised as. (<c>ANY</c>, the type a re-typed
        /// accessor carries, is refused for the same reason and by the same default.)
        /// </para>
        /// </remarks>
        static bool IsBindableKey(org.apache.calcite.rel.type.RelDataType type)
        {
            return type?.getSqlTypeName()?.getName() switch
            {
                nameof(SqlTypeName.CHAR) or nameof(SqlTypeName.VARCHAR) => true,
                nameof(SqlTypeName.BOOLEAN) => true,
                nameof(SqlTypeName.TINYINT) or nameof(SqlTypeName.SMALLINT) or nameof(SqlTypeName.INTEGER) or nameof(SqlTypeName.BIGINT) => true,
                nameof(SqlTypeName.FLOAT) or nameof(SqlTypeName.REAL) or nameof(SqlTypeName.DOUBLE) or nameof(SqlTypeName.DECIMAL) => true,
                _ => false,
            };
        }

        /// <summary>
        /// How the probe side's key is restricted: the path a <c>WHERE</c> names, and where the key is a
        /// <c>UUID</c>, the form the container stores it in.
        /// </summary>
        /// <param name="Path">The path the restriction names, or <c>null</c> where the key is a column the subtree already binds to one.</param>
        /// <param name="Uuid">The stored form of a <c>UUID</c> key, or <c>null</c> where the key is bound as it is.</param>
        internal readonly record struct ProbeKey(CosmosPath? Path, CosmosRepresentation? Uuid);

        /// <summary>
        /// Resolves how a probe subtree's key column can be restricted, or answers <c>false</c> where it
        /// cannot.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A column bound to a path, of a type Cosmos has,</b> is restricted on that path with the key
        /// bound as it is — what this join always did.
        /// </para>
        /// <para>
        /// <b>A <c>UUID</c> cast of a text accessor</b> binds to no path: the column carries a
        /// conversion. It is restricted on the accessor's path where the container's unconditional facts
        /// give that path a UUID form, which pins one spelling per value — so the key written in that
        /// spelling (<see cref="CosmosUuidForms.Render"/>) matches exactly the documents whose cast equals
        /// it, and no document whose cast would not. The same fact makes a comparison against a literal
        /// lower (<c>CosmosFactRewriter</c>) and a self-join's key count; this asks it of a key that is
        /// data rather than a literal. Asked of the unconditional facts, for the reason the self-join
        /// gives: a guarded fact holds of the documents its guard admits, and nothing here proves the
        /// guard for every document the restriction reaches.
        /// </para>
        /// <para>
        /// Asked of the logical subtree by the rule and of the converted one by the join, which are the
        /// same shape: a projection over filters over the scan.
        /// </para>
        /// </remarks>
        /// <param name="probe">The probe subtree.</param>
        /// <param name="ordinal">The key's ordinal in the subtree's row.</param>
        /// <param name="key">On success, how the key is restricted.</param>
        /// <returns><c>true</c> where the key can be restricted.</returns>
        internal static bool TryProbeKey(RelNode probe, int ordinal, out ProbeKey key)
        {
            key = default;

            if (CosmosImplementor.TryBindOutput(probe, out var fields, out _) == false)
                return false;

            if (ordinal < 0 || ordinal >= fields.Count)
                return false;

            var type = ((org.apache.calcite.rel.type.RelDataTypeField)probe.getRowType().getFieldList().get(ordinal)).getType();

            if (fields[ordinal] is not null)
            {
                if (IsBindableKey(type) == false)
                    return false;

                key = new ProbeKey(null, null);
                return true;
            }

            if (IsUuid(type) == false || FindTable(probe) is not CosmosTable table)
                return false;

            if (TryUuidPath(probe, ordinal, table, probe.getCluster().getRexBuilder(), CosmosPlanMembers.NewSeen()) is not (CosmosPath path, CosmosRepresentation representation))
                return false;

            key = new ProbeKey(path, representation);
            return true;
        }

        /// <summary>
        /// Finds the stored path and form of a <c>UUID</c> key column, reading through filters and
        /// references down to the projection that casts it.
        /// </summary>
        static (CosmosPath, CosmosRepresentation)? TryUuidPath(RelNode node, int ordinal, CosmosTable table, RexBuilder rexBuilder, System.Collections.Generic.HashSet<RelNode> seen)
        {
            foreach (var member in CosmosPlanMembers.Of(node))
            {
                if (seen.Add(member) == false)
                    continue;

                var found = member switch
                {
                    Filter filter => TryUuidPath(filter.getInput(), ordinal, table, rexBuilder, seen),
                    Project project => TryUuidPath(project, ordinal, table, rexBuilder, seen),
                    _ => null,
                };

                if (found is not null)
                    return found;
            }

            return null;
        }

        static (CosmosPath, CosmosRepresentation)? TryUuidPath(Project project, int ordinal, CosmosTable table, RexBuilder rexBuilder, System.Collections.Generic.HashSet<RelNode> seen)
        {
            if (ordinal >= project.getProjects().size())
                return null;

            var expression = (RexNode)project.getProjects().get(ordinal);

            if (expression is RexInputRef reference)
                return TryUuidPath(project.getInput(), reference.getIndex(), table, rexBuilder, seen);

            if (expression is not RexCall cast || cast.getKind() != SqlKind.CAST || cast.getOperands().size() != 1 || IsUuid(cast.getType()) == false)
                return null;

            var text = CosmosRexTranslator.StripRedundantTextCast((RexNode)cast.getOperands().get(0));
            if (CosmosRexTranslator.IsTextJsonValue(text) == false)
                return null;

            if (CosmosImplementor.TryBindOutput(project.getInput(), out var fields, out _) == false)
                return null;

            var translator = new CosmosRexTranslator(rexBuilder, fields, new CosmosParameterList(), null, table.Container);
            if (translator.TryResolvePath(text, out var path) == false || path is null)
                return null;

            if (CosmosDocumentPaths.From(path) is not JsonDocumentPath stored)
                return null;

            if (table.Container.Facts.Derive(null).RepresentationOf(stored) is not CosmosRepresentation representation || CosmosUuidForms.IsUuid(representation) == false)
                return null;

            return (path, representation);
        }

        static bool IsUuid(org.apache.calcite.rel.type.RelDataType? type) =>
            type?.getSqlTypeName()?.getName() == nameof(SqlTypeName.UUID);

        /// <summary>
        /// Determines whether restricting a subtree to a batch's keys leaves it meaning what it meant.
        /// </summary>
        /// <remarks>
        /// A filter, a projection or a traversal restricts row by row, so an added conjunct composes
        /// with it. A sort, a limit or a grouping does not: each is defined over the whole result, and
        /// per batch it would be defined over a fraction of it.
        /// </remarks>
        static bool IsRestrictable(RelNode? node)
        {
            return IsRestrictable(node, CosmosPlanMembers.NewSeen());
        }

        /// <inheritdoc cref="IsRestrictable(RelNode?)" />
        /// <remarks>
        /// Finding the chain in any member establishes it of the relation, the members being
        /// equivalent; not finding it in one establishes nothing about the others. So this asks every
        /// member and takes the first that answers, which is more permissive than taking a
        /// representative and is sound for the same reason. See <see cref="CosmosPlanMembers"/>.
        /// </remarks>
        /// <param name="node">The subtree.</param>
        /// <param name="seen">The expressions already asked, which keeps a graph finite.</param>
        static bool IsRestrictable(RelNode? node, System.Collections.Generic.HashSet<RelNode> seen)
        {
            foreach (var member in CosmosPlanMembers.Of(node))
            {
                if (seen.Add(member) == false)
                    continue;

                var restrictable = member switch
                {
                    TableScan scan => scan.getTable()?.unwrap(typeof(CosmosTable)) is CosmosTable,
                    Filter filter => IsRestrictable(filter.getInput(), seen),
                    Project project => IsRestrictable(project.getInput(), seen),
                    Correlate correlate => IsRestrictable(correlate.getLeft(), seen),
                    _ => false,
                };

                if (restrictable)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns the container a subtree reads, or <c>null</c> where it does not read one.
        /// </summary>
        static CosmosTable? FindTable(RelNode? node)
        {
            return FindTable(node, CosmosPlanMembers.NewSeen());
        }

        /// <inheritdoc cref="FindTable(RelNode?)" />
        /// <remarks>
        /// Every member is asked, not one representative: which container a subtree reads is a
        /// question about its <em>shape</em>, and a member whose type none of the cases mention
        /// answers "none" for a plan that reads one. See <see cref="CosmosPlanMembers"/>.
        /// </remarks>
        /// <param name="node">The subtree.</param>
        /// <param name="seen">The expressions already asked, which keeps a graph finite.</param>
        static CosmosTable? FindTable(RelNode? node, System.Collections.Generic.HashSet<RelNode> seen)
        {
            foreach (var member in CosmosPlanMembers.Of(node))
            {
                if (seen.Add(member) == false)
                    continue;

                var found = member switch
                {
                    TableScan scan => scan.getTable()?.unwrap(typeof(CosmosTable)) as CosmosTable,
                    Filter filter => FindTable(filter.getInput(), seen),
                    Project project => FindTable(project.getInput(), seen),
                    Correlate correlate => FindTable(correlate.getLeft(), seen),
                    _ => null,
                };

                if (found is not null)
                    return found;
            }

            return null;
        }

        /// <summary>
        /// Returns the single pair of key ordinals a join matches on, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// The probe ordinal is returned relative to the probe's own row rather than to the joined row,
        /// which is what <see cref="JoinInfo"/> already gives.
        /// </remarks>
        static (int Build, int Probe)? GetKeys(Join join)
        {
            var info = join.analyzeCondition();

            if (info.isEqui() == false || info.leftKeys.size() != 1 || info.rightKeys.size() != 1)
                return null;

            return (((java.lang.Integer)info.leftKeys.get(0)).intValue(), ((java.lang.Integer)info.rightKeys.get(0)).intValue());
        }

        /// <summary>
        /// Creates a rule instance.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Not bound to a convention</b>, for the reason
        /// <see cref="CosmosTableModifyRule.Create"/> is not: a <see cref="ConverterRule"/>'s
        /// description is derived from the traits it converts between, and this one converts
        /// <c>NONE</c> to <c>CLR_ASYNC_ENUMERABLE</c> — neither of which names a container. One
        /// instance per container therefore gives several rules carrying the same description, and
        /// which of them a planner matches with is then not something the adapter decides.
        /// </para>
        /// <para>
        /// It decided wrongly, measured on a join across three containers: one of the two joins fetched
        /// by key and the other read its container whole. Two containers never showed it, for a reason
        /// that is worth knowing and not worth relying on —
        /// <c>CosmosLookupJoinRuleTests.EveryContainerInAQueryCanBeOnTheProbeSide</c> records both.
        /// </para>
        /// <para>
        /// The binding had nothing to do anyway. The container is named by the probe side, which
        /// <see cref="FindTable"/> must locate regardless in order to establish that the subtree is a
        /// container's at all, so the rule reads the convention from there rather than being told it.
        /// One instance serves every container, and registering it once per convention is then merely
        /// redundant rather than wrong.
        /// </para>
        /// </remarks>
        /// <returns>A configured rule.</returns>
        public static CosmosLookupJoinRule Create()
        {
            static bool IsTranslatable(Join join)
            {
                if (join.getJoinType() != JoinRelType.INNER && join.getJoinType() != JoinRelType.LEFT)
                    return false;

                if (GetKeys(join) is not (int build, int probe))
                    return false;

                var right = join.getRight();

                // The probe side must bottom out at a container's scan — both because there is
                // otherwise nothing to fetch from, and because the convention this converts that side
                // into is the container's own.
                if (FindTable(right) is null)
                    return false;

                if (IsRestrictable(right) == false)
                    return false;

                if (TryProbeKey(right, probe, out var key) == false)
                    return false;

                // The build side's key is compared with the container's once fetched, and bound as a
                // parameter before that: a plain key has to be a type Cosmos has, and a UUID key a UUID,
                // which is what is spelled.
                var buildField = (org.apache.calcite.rel.type.RelDataTypeField)join.getLeft().getRowType().getFieldList().get(build);

                return key.Uuid is null ? IsBindableKey(buildField.getType()) : IsUuid(buildField.getType());
            }

            return (CosmosLookupJoinRule)Config.INSTANCE
                .withConversion(typeof(Join), new DelegatePredicate<Join>(IsTranslatable), Convention.NONE, ClrCursorConvention.Instance, "CosmosLookupJoinRule")
                .withRuleFactory(new DelegateFunction<Config, CosmosLookupJoinRule>(c => new CosmosLookupJoinRule(c)))
                .toRule(typeof(CosmosLookupJoinRule));
        }

        /// <summary>
        /// Initializes a new instance using the supplied rule configuration.
        /// </summary>
        /// <param name="config">The rule configuration produced by <see cref="Create"/>.</param>
        public CosmosLookupJoinRule(Config config) :
            base(config)
        {

        }

        /// <inheritdoc />
        public override RelNode? convert(RelNode rel)
        {
            var join = (Join)rel;

            if (GetKeys(join) is not (int build, int probe))
                return null;

            var left = join.getLeft();
            var right = join.getRight();

            if (FindTable(right) is not CosmosTable table)
                return null;

            return new CosmosLookupJoin(
                join.getCluster(),
                join.getTraitSet().replace(ClrCursorConvention.Instance),
                convert(left, left.getTraitSet().replace(ClrCursorConvention.Instance)),
                convert(right, right.getTraitSet().replace(table.Convention)),
                join.getCondition(),
                build,
                probe,
                join.getJoinType());
        }

    }

}
