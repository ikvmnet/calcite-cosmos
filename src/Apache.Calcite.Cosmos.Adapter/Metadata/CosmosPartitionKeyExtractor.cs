using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;
using Truth = Apache.Calcite.Cosmos.Facts.Saturation.Truth;

using org.apache.calcite.rex;
using org.apache.calcite.sql;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// Recovers the partition key value a predicate pins, when it pins one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A query that names its partition key runs against a single physical partition; without one
    /// the service fans the query out across every partition and merges the results. This is
    /// usually the largest single difference in cost, so it is worth recovering even though it
    /// changes nothing about the statement itself.
    /// </para>
    /// <para>
    /// <b>A key is pinned where it is there and holds one value</b>, and that is the predicate
    /// oracle's to say: <see cref="JsonClaim.Present"/> and one value
    /// (<see cref="JsonFactSet.TryGetValue"/>) at the key's path, in the facts the question's licence
    /// admits. An equality with a constant pins its path, an <c>IN</c> of one value does, a declared
    /// <c>const</c> a declaration also requires does, and a comparison over text a number renders as
    /// does not — a stored <c>30</c> and a stored <c>"30"</c> are two partitions and one text. A
    /// disjunction may match several partitions and a range says nothing about which; in either case
    /// the query must fan out, and claiming otherwise would silently lose rows.
    /// </para>
    /// <para>
    /// <b>Two licences, for the two things a key is for.</b> Routing a statement that still applies
    /// its predicate may lean on every conjunct (<see cref="OracleLicence.Kept"/>): the partitions it
    /// skips hold no row the predicate keeps, which is a strengthening's argument. A point read, a
    /// batch of them and a whole-partition delete apply no predicate at all, so they read only the
    /// conjuncts that pin their keys (<see cref="OracleLicence.PointRead"/>) — and every other conjunct
    /// has to be one those pins and the declaration already make true, or the operation answers a
    /// different question than the query asked.
    /// </para>
    /// </remarks>
    public static class CosmosPartitionKeyExtractor
    {

        /// <summary>
        /// The path a point read's <c>id</c> is pinned at, in policy form.
        /// </summary>
        static readonly string IdPolicy = "/" + CosmosContainerMetadata.IdPropertyName;

        /// <summary>
        /// Attempts to recover a complete partition key from a predicate, from the conjuncts that pin it and nothing else.
        /// </summary>
        /// <remarks>
        /// Conservative: this answers for a complete key, and the point read and the whole-partition delete are built on
        /// it. Each replaces the predicate with an operation that applies none, so a key recovered from anything but the
        /// conjuncts that pin it would be exact in a weaker sense than they need. <see cref="TryExtractPrefix"/>, which
        /// only routes, reads every conjunct.
        /// </remarks>
        /// <param name="condition">The predicate, expressed over <paramref name="fields"/>.</param>
        /// <param name="fields">The ordinal-to-path binding of the filtered input.</param>
        /// <param name="readings">How each field is read back, or <c>null</c> where every field is read as its type.</param>
        /// <param name="container">The container whose partition key is sought.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <param name="values">On success, one value per declared partition key path, in order.</param>
        /// <returns><c>true</c> if every partition key path was pinned; otherwise <c>false</c>.</returns>
        /// <exception cref="ArgumentNullException">Any argument but <paramref name="readings"/> is <c>null</c>.</exception>
        public static bool TryExtract(RexNode condition, IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading>? readings, CosmosContainerMetadata container, string rootAlias, out IReadOnlyList<object?> values)
        {
            if (condition is null)
                throw new ArgumentNullException(nameof(condition));
            if (fields is null)
                throw new ArgumentNullException(nameof(fields));
            if (container is null)
                throw new ArgumentNullException(nameof(container));

            values = Array.Empty<object?>();

            // A container always declares a partition key; without metadata for it there is
            // nothing to pin.
            if (container.PartitionKeyPaths.Count == 0)
                return false;

            var read = Read(condition, fields, readings, container, rootAlias, container.PartitionKeyPaths);
            return Complete(read.Pinned(PointRead(container.PartitionKeyPaths)), container, out values);
        }

        /// <summary>
        /// Recovers as much of a hierarchical partition key as a predicate pins, outermost first.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A hierarchical key is up to three paths, and Cosmos routes on any <em>prefix</em> of them: a
        /// query pinning <c>/tenant</c> out of <c>/tenant, /user</c> reaches the subset of physical
        /// partitions holding that tenant rather than every partition in the container. That is a large
        /// saving on a container with many partitions, and <see cref="TryExtract"/> — which answers only
        /// for a complete key — throws it away.
        /// </para>
        /// <para>
        /// <b>Prefix means prefix.</b> Pinning <c>/user</c> without <c>/tenant</c> narrows nothing,
        /// because the routing is on the leading components; so this takes the longest run of pinned
        /// paths starting at the outermost and stops at the first gap. Supplying a prefix restricts
        /// which partitions are visited and filters no rows, so the predicate is unaffected either way.
        /// </para>
        /// <para>
        /// <b>Every conjunct, the predicate being applied.</b> Routing skips partitions, and a partition
        /// it skips holds no row the predicate keeps: so whatever the conjuncts together say of the key
        /// may route — a view's cast to text over it included, where the text is one only a string
        /// renders as.
        /// </para>
        /// </remarks>
        /// <param name="condition">The predicate, expressed over <paramref name="fields"/>.</param>
        /// <param name="fields">The ordinal-to-path binding of the filtered input.</param>
        /// <param name="readings">How each field is read back, or <c>null</c> where every field is read as its type.</param>
        /// <param name="container">The container whose partition key is sought.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <param name="values">On success, the pinned leading values, outermost first.</param>
        /// <param name="complete">On success, whether every declared path was pinned.</param>
        /// <returns><c>true</c> if at least the outermost path was pinned; otherwise <c>false</c>.</returns>
        public static bool TryExtractPrefix(RexNode condition, IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading>? readings, CosmosContainerMetadata container, string rootAlias, out IReadOnlyList<object?> values, out bool complete)
        {
            values = Array.Empty<object?>();
            complete = false;

            if (condition is null || fields is null || container is null || container.PartitionKeyPaths.Count == 0)
                return false;

            var pinned = Read(condition, fields, readings, container, rootAlias, container.PartitionKeyPaths).Pinned(OracleLicence.Kept);
            var prefix = new List<object?>();

            foreach (var path in container.PartitionKeyPaths)
            {
                if (pinned.TryGetValue(path, out var value) == false)
                    break;

                prefix.Add(value);
            }

            if (prefix.Count == 0)
                return false;

            values = prefix;
            complete = prefix.Count == container.PartitionKeyPaths.Count;
            return true;
        }

        /// <summary>
        /// Attempts to recover an <c>id</c> and a complete partition key from a predicate that says
        /// nothing else — the shape a point read can answer.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A point read costs about 1 RU where the same fetch as a query costs 2.3 at best and goes
        /// through the query engine. It is also blind: it takes an <c>id</c> and a partition key and
        /// returns that document, applying no predicate of its own.
        /// </para>
        /// <para>
        /// <b>That is why the predicate must say nothing else.</b> Under
        /// <c>WHERE id = 'x' AND pk = 'y' AND price &gt; 100</c> a point read would return a document
        /// the query excludes — a wrong answer rather than a slow one. So every top-level conjunct has
        /// to be true wherever the pins and the declaration are (<see cref="Truth.Always"/> in the
        /// pins-only scope): an equality that pins <c>id</c> or a partition key path, or one the
        /// container makes true of every document it pins, such as a <c>kind</c> it declares constant.
        /// A residual predicate is left to the query path rather than applied afterwards; reading then
        /// filtering is a different trade and worth making deliberately.
        /// </para>
        /// </remarks>
        /// <param name="condition">The predicate, expressed over <paramref name="fields"/>.</param>
        /// <param name="fields">The ordinal-to-path binding of the filtered input.</param>
        /// <param name="readings">How each field is read back, or <c>null</c> where every field is read as its type.</param>
        /// <param name="container">The container being read.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <param name="values">On success, one value per declared partition key path, in order.</param>
        /// <param name="id">On success, the pinned <c>id</c>.</param>
        /// <returns><c>true</c> if the predicate is exactly an <c>id</c> and a complete partition key.</returns>
        public static bool TryExtractPointRead(RexNode condition, IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading>? readings, CosmosContainerMetadata container, string rootAlias, out IReadOnlyList<object?> values, out string? id)
        {
            values = Array.Empty<object?>();
            id = null;

            if (condition is null || fields is null || container is null || container.PartitionKeyPaths.Count == 0)
                return false;

            var keys = container.PartitionKeyPaths.Append(IdPolicy).ToArray();
            var read = Read(condition, fields, readings, container, rootAlias, keys);
            var licence = PointRead(keys);
            var pinned = read.Pinned(licence);

            // Cosmos types id as a string. Anything else pinned to it is a predicate that matches
            // nothing, and is not something to turn into a read.
            if (pinned.TryGetValue(IdPolicy, out var value) == false || value is not string pinnedId)
                return false;

            if (Complete(pinned, container, out var partitionKey) == false || read.SaysNothingElse(licence) == false)
                return false;

            values = partitionKey;
            id = pinnedId;
            return true;
        }

        /// <summary>
        /// Attempts to recover a set of <c>id</c>s and a complete partition key from a predicate that
        /// says nothing else — the shape a batch of point reads can answer.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>WHERE pk = 'x' AND id IN ('a', 'b', 'c')</c> is <see cref="TryExtractPointRead"/>'s
        /// question asked for several documents at once, and <c>ReadManyItemsAsync</c> answers it
        /// charged as point reads. The same blindness applies: the batch read applies no predicate,
        /// so one conjunct must be the disjunction of <c>id</c> equalities, and every other conjunct
        /// one the partition key's pins and the declaration make true.
        /// </para>
        /// <para>
        /// An <c>IN</c> arrives as a <c>SEARCH</c> over a set, which the oracle's loading expands the
        /// way translation does and reads back as one membership, of the <c>id</c> as stored or as its
        /// text, which for a string is one; the set is that membership's points, each a string. Duplicate
        /// <c>id</c>s collapse: <c>IN ('a', 'a')</c> asks for one document.
        /// </para>
        /// </remarks>
        /// <param name="condition">The predicate, expressed over <paramref name="fields"/>.</param>
        /// <param name="fields">The ordinal-to-path binding of the filtered input.</param>
        /// <param name="readings">How each field is read back, or <c>null</c> where every field is read as its type.</param>
        /// <param name="container">The container being read.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <param name="values">On success, one value per declared partition key path, in order.</param>
        /// <param name="ids">On success, the distinct pinned <c>id</c>s, in the order the membership holds them.</param>
        /// <returns><c>true</c> if the predicate is exactly a set of <c>id</c>s and a complete partition key.</returns>
        public static bool TryExtractPointReadSet(RexNode condition, IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading>? readings, CosmosContainerMetadata container, string rootAlias, out IReadOnlyList<object?> values, out IReadOnlyList<string> ids)
        {
            values = Array.Empty<object?>();
            ids = Array.Empty<string>();

            if (condition is null || fields is null || container is null || container.PartitionKeyPaths.Count == 0)
                return false;

            var read = Read(condition, fields, readings, container, rootAlias, container.PartitionKeyPaths.Append(IdPolicy));
            var licence = PointRead(container.PartitionKeyPaths);

            if (Complete(read.Pinned(licence), container, out var partitionKey) == false)
                return false;

            List<string>? set = null;
            var setAt = -1;
            var conjuncts = read.Statement.Conjuncts;

            for (var i = 0; i < conjuncts.Count; i++)
            {
                if (IdSet(conjuncts[i]) is not List<string> branchIds)
                    continue;

                // Two id sets in one conjunction intersect, which this does not compute.
                if (set is not null)
                    return false;

                set = branchIds;
                setAt = i;
            }

            if (set is null || read.SaysNothingElse(licence, except: setAt) == false)
                return false;

            values = partitionKey;
            ids = set;
            return true;
        }

        /// <summary>
        /// Returns the strings a membership of the <c>id</c> holds it to, or <c>null</c> where the conjunct is no such
        /// membership.
        /// </summary>
        /// <remarks>
        /// Of the <c>id</c> as stored or as its text, which for a string is one; not through a cast. Cosmos types the
        /// <c>id</c> a string, so a point holding anything else makes the membership a predicate rather than a set of
        /// documents.
        /// </remarks>
        static List<string>? IdSet(SqlPredicate conjunct)
        {
            if (conjunct is not SqlPredicate.In { Value: var value, Points: var points } || value is SqlValue.Cast
                || value.PathRead != JsonDocumentPath.Root.Property(CosmosContainerMetadata.IdPropertyName))
                return null;

            var ids = new List<string>();
            foreach (var point in points)
            {
                if (JsonFactEncoding.TryScalar(point, out var scalar) == false || scalar.ToClrValue() is not string id)
                    return null;

                ids.Add(id);
            }

            return ids.Count > 0 ? ids : null;
        }

        /// <summary>
        /// Attempts to recover a complete partition key from a predicate that pins it and says
        /// nothing else — the shape a whole-partition delete answers.
        /// </summary>
        /// <remarks>
        /// The same blindness argument the point read makes, one operation over: the service
        /// removes every document in the partition and applies no predicate, so a residual conjunct
        /// would delete rows the statement did not ask for — which is data loss rather than a slow
        /// plan. Every top-level conjunct must therefore be true wherever the key's pins and the
        /// declaration are, and an <c>id</c> among them disqualifies it: that is a delete of one
        /// document, which the per-row path already does cheaply, and the pins of the key alone do
        /// not make an equality over the <c>id</c> true.
        /// </remarks>
        /// <param name="condition">The predicate, expressed over <paramref name="fields"/>.</param>
        /// <param name="fields">The ordinal-to-path binding of the filtered input.</param>
        /// <param name="readings">How each field is read back, or <c>null</c> where every field is read as its type.</param>
        /// <param name="container">The container being deleted from.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <param name="values">On success, one value per declared partition key path, in order.</param>
        /// <returns><c>true</c> if the predicate is exactly a complete partition key.</returns>
        public static bool TryExtractWholePartition(RexNode condition, IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading>? readings, CosmosContainerMetadata container, string rootAlias, out IReadOnlyList<object?> values)
        {
            values = Array.Empty<object?>();

            if (condition is null || fields is null || container is null || container.PartitionKeyPaths.Count == 0)
                return false;

            var read = Read(condition, fields, readings, container, rootAlias, container.PartitionKeyPaths);
            var licence = PointRead(container.PartitionKeyPaths);

            if (Complete(read.Pinned(licence), container, out var partitionKey) == false || read.SaysNothingElse(licence) == false)
                return false;

            values = partitionKey;
            return true;
        }

        /// <summary>
        /// Determines whether a predicate can match more than one document.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A complete partition key and a pinned <c>id</c> identify at most one document — <c>id</c> is
        /// unique within a partition — and that stays true however much else the predicate says, because
        /// a further conjunct can only remove the one row, never add another. So unlike
        /// <see cref="TryExtractPointRead"/> this does not ask whether the predicate says anything else,
        /// and reads every conjunct for the pins, as routing does. It is not a routing question at all:
        /// it is the row count, and the distinction matters because a residual changes which mechanism
        /// may answer and cannot change how many documents there are to answer with.
        /// </para>
        /// <para>
        /// Calcite would otherwise estimate this the way it estimates any conjunction of equalities, at
        /// a fixed selectivity per conjunct, and arrive at a number of rows that has nothing to do with
        /// the one document actually addressed. Costing the plans around such a lookup — a converter, a
        /// filter finishing above it — then turns on an invented row count rather than on the mechanism,
        /// which is what <c>CosmosPointReadSplitRule</c> needs the planner to be comparing.
        /// </para>
        /// </remarks>
        /// <param name="condition">The predicate, expressed over <paramref name="fields"/>.</param>
        /// <param name="fields">The ordinal-to-path binding of the filtered input.</param>
        /// <param name="readings">How each field is read back, or <c>null</c> where every field is read as its type.</param>
        /// <param name="container">The container being read.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <returns><c>true</c> where the predicate addresses at most one document.</returns>
        public static bool PinsAtMostOneDocument(RexNode condition, IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading>? readings, CosmosContainerMetadata container, string rootAlias)
        {
            if (condition is null || fields is null || container is null || container.PartitionKeyPaths.Count == 0)
                return false;

            var pinned = Read(condition, fields, readings, container, rootAlias, container.PartitionKeyPaths.Append(IdPolicy)).Pinned(OracleLicence.Kept);

            return pinned.TryGetValue(IdPolicy, out var value) && value is string && Complete(pinned, container, out _);
        }

        /// <summary>
        /// Returns a predicate's top-level conjuncts, each with whether a point read accounts for it — an equality pinning
        /// <c>id</c> or one of the container's declared partition key paths.
        /// </summary>
        /// <remarks>
        /// The pins-only scope's own question asked of each conjunct (<see cref="CosmosStatementScope.IsPin"/>), of the
        /// conjuncts the oracle reads, so that a caller can <em>partition</em> a conjunction into the part a point read
        /// answers and the part it does not, instead of only being told that a rest exists. For
        /// <c>CosmosPointReadSplitRule</c>, which builds of the first part a pushed predicate that then satisfies
        /// <see cref="TryExtractPointRead"/> unchanged — the rule relaxes nothing here, it restructures the plan so that
        /// this rule's existing standard is met.
        /// </remarks>
        /// <param name="condition">The predicate, expressed over <paramref name="fields"/>.</param>
        /// <param name="fields">The ordinal-to-path binding of the filtered input.</param>
        /// <param name="readings">How each field is read back, or <c>null</c> where every field is read as its type.</param>
        /// <param name="container">The container being read.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <returns>Each conjunct, in order, and whether a point read accounts for it.</returns>
        internal static IReadOnlyList<(RexNode Conjunct, bool Pins)> PointReadConjuncts(RexNode condition, IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading>? readings, CosmosContainerMetadata container, string rootAlias)
        {
            var keys = container.PartitionKeyPaths.Append(IdPolicy).Select(DocumentPathOf).OfType<JsonDocumentPath>().ToArray();
            var read = CosmosFilterStatement.Of(condition, fields, readings, container, RexBuilderHolder.Value, rootAlias);

            return read.Conjuncts.Select((conjunct, i) => (conjunct, CosmosStatementScope.IsPin(read.Statement.Conjuncts[i], keys))).ToArray();
        }

        /// <summary>
        /// The pins-only licence for some key paths, in policy form.
        /// </summary>
        static OracleLicence PointRead(IEnumerable<string> keys) =>
            OracleLicence.PointRead(keys.Select(DocumentPathOf).OfType<JsonDocumentPath>().ToArray());

        /// <summary>
        /// Reads every declared partition key path's pinned value, in order, or answers <c>false</c> at the first that is
        /// not pinned.
        /// </summary>
        static bool Complete(IReadOnlyDictionary<string, object?> pinned, CosmosContainerMetadata container, out IReadOnlyList<object?> values)
        {
            values = Array.Empty<object?>();
            var resolved = new object?[container.PartitionKeyPaths.Count];

            for (var i = 0; i < resolved.Length; i++)
            {
                if (pinned.TryGetValue(container.PartitionKeyPaths[i], out var value) == false)
                    return false;

                resolved[i] = value;
            }

            values = resolved;
            return true;
        }

        /// <summary>
        /// Reads a predicate as the statement the oracle answers.
        /// </summary>
        /// <remarks>
        /// <see cref="CosmosFilterStatement"/>'s, remembered against the condition as every reader's is: a cost estimate
        /// asks of one filter many times. Over a builder of its own, since this renders nothing and a rule's statement
        /// renders through the rule's builder; the oracle remembers one tree for both, by the statement's digest.
        /// </remarks>
        static Reading Read(RexNode condition, IReadOnlyList<CosmosPath?> fields, IReadOnlyList<CosmosReading>? readings, CosmosContainerMetadata container, string rootAlias, IEnumerable<string> keys) =>
            new(CosmosFilterStatement.Of(condition, fields, readings, container, RexBuilderHolder.Value, rootAlias), keys.ToArray());

        /// <summary>
        /// A predicate as the oracle reads it, and the key paths asked about.
        /// </summary>
        sealed class Reading
        {

            readonly CosmosFilterStatement _statement;
            readonly string[] _keys;

            public Reading(CosmosFilterStatement statement, string[] keys)
            {
                _statement = statement;
                _keys = keys;
            }

            public PredicateStatement Statement => _statement.Statement;

            /// <summary>
            /// Returns each key path the facts a licence admits pin — there in every document they keep, and holding
            /// one value — and the value, as the client builds a partition key from it.
            /// </summary>
            public IReadOnlyDictionary<string, object?> Pinned(OracleLicence licence)
            {
                var pinned = new Dictionary<string, object?>(StringComparer.Ordinal);

                // A scope no document satisfies pins every path to everything; nothing is to be routed on it.
                if (_statement.KeepsNothingUnder(licence))
                    return pinned;

                var facts = _statement.FactsUnder(licence);

                foreach (var key in _keys)
                    if (DocumentPathOf(key) is JsonDocumentPath path && facts.TryGetConstant(path, out var value))
                        pinned[key] = Bound(value);

                return pinned;
            }

            /// <summary>
            /// The value a key is sent as: a whole number as the <see cref="long"/> a literal of it is bound as, any other
            /// number as the <see cref="double"/> the service reads it as, and a string, a boolean or a null as itself.
            /// </summary>
            /// <remarks>
            /// The oracle holds a number as a <see cref="decimal"/>, which a partition key has no component for: the client
            /// adds a number to a key as a double, and a whole-partition delete takes a long or a double and nothing else.
            /// </remarks>
            static object? Bound(JsonScalar value) => value.ToClrValue() switch
            {
                decimal whole when decimal.Truncate(whole) == whole && whole >= long.MinValue && whole <= long.MaxValue => (long)whole,
                decimal number => (double)number,
                var other => other,
            };

            /// <summary>
            /// Determines whether every conjunct but one is true wherever the facts a licence admits hold.
            /// </summary>
            public bool SaysNothingElse(OracleLicence licence, int except = -1)
            {
                for (var i = 0; i < Statement.Conjuncts.Count; i++)
                    if (i != except && _statement.Decide(i, licence) != Truth.Always)
                        return false;

                return true;
            }

        }

        /// <summary>
        /// Reads a policy path — <c>/tenant/user</c> — as the document path the facts are keyed by.
        /// </summary>
        /// <param name="policy">The declared path.</param>
        /// <returns>The path, or <c>null</c> where it names nothing this model can address.</returns>
        static JsonDocumentPath? DocumentPathOf(string policy)
        {
            if (string.IsNullOrEmpty(policy) || policy[0] != '/')
                return null;

            var path = JsonDocumentPath.Root;

            foreach (var segment in policy.Split('/'))
            {
                if (segment.Length == 0)
                    continue;

                path = path.Property(segment);
            }

            return path.IsRoot ? null : path;
        }

        /// <summary>
        /// A builder for the statements this reads, which render nothing.
        /// </summary>
        static class RexBuilderHolder
        {

            internal static readonly RexBuilder Value = new(new org.apache.calcite.jdbc.JavaTypeFactoryImpl());

        }

    }

}
