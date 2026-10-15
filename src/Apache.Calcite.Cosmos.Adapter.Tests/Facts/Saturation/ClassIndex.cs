using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// A context's classes of the predicate, value and path sorts, each with its e-nodes, and each built back into the IR
    /// from a representative: what a checker evaluates the graph with, since nothing the oracle defines can be extracted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The representative is the class's first node</b>, in the order the engine lists a table's rows, over its own
    /// children's representatives. Where nothing was ever unioned every class has one node, and the representative is
    /// that node; a class with two is where a union happened, and the only place one can be seen.
    /// </para>
    /// <para>
    /// Built from <see cref="Scope.Rows"/>, which lists a context's rows with every class canonical in it.
    /// </para>
    /// </remarks>
    sealed class ClassIndex
    {

        /// <summary>One e-node: its constructor and its arguments, a class's id for a sort and the value otherwise.</summary>
        public sealed record Node(ConstructorHandle Head, IReadOnlyList<object> Keys);

        readonly PredicateRules _rules;
        readonly Dictionary<ClassId, List<Node>> _members = new();
        readonly Dictionary<ClassId, SqlPredicate?> _predicates = new();
        readonly Dictionary<ClassId, SqlValue?> _values = new();
        readonly Dictionary<ClassId, JsonPathSpec?> _paths = new();
        readonly HashSet<ClassId> _building = new();

        public ClassIndex(Scope context, PredicateRules rules)
        {
            _rules = rules;
            foreach (var head in Heads)
                foreach (var row in context.Rows(head))
                {
                    var id = (ClassId)row.Output!;
                    if (_members.TryGetValue(id, out var list) == false)
                        _members[id] = list = new List<Node>();

                    list.Add(new Node(head, row.Keys));
                }
        }

        IEnumerable<ConstructorHandle> Predicates => new ConstructorHandle[] { _rules.Compare, _rules.In, _rules.IsNull, _rules.And, _rules.Or, _rules.Not, _rules.Opaque };

        IEnumerable<ConstructorHandle> Values => new ConstructorHandle[] { _rules.Text, _rules.Raw, _rules.Typed, _rules.Cast, _rules.Literal, _rules.Parameter };

        IEnumerable<ConstructorHandle> PathHeads => new ConstructorHandle[] { _rules.Json.Root, _rules.Json.Prop, _rules.Json.Index, _rules.Json.Each, _rules.Json.Ghost };

        IEnumerable<ConstructorHandle> Heads => Predicates.Concat(Values).Concat(PathHeads);

        /// <summary>Gets every e-node, beside its class.</summary>
        public IEnumerable<(ClassId Class, Node Node)> Nodes => _members.SelectMany(kv => kv.Value.Select(node => (kv.Key, node)));

        /// <summary>Returns a class's representative.</summary>
        public Node Representative(ClassId id) => _members[id][0];

        /// <summary>Whether a node is its class's representative.</summary>
        public bool IsRepresentative(ClassId id, Node node) => ReferenceEquals(Representative(id), node);

        /// <summary>Returns a predicate class as the IR, or <c>null</c> where it is not one or cannot be built.</summary>
        public SqlPredicate? Predicate(ClassId id) => Memo(_predicates, id, () => _members.TryGetValue(id, out var nodes) ? BuildPredicate(nodes[0]) : null);

        /// <summary>Returns a value class as the IR, or <c>null</c>.</summary>
        public SqlValue? Value(ClassId id) => Memo(_values, id, () => _members.TryGetValue(id, out var nodes) ? BuildValue(nodes[0]) : null);

        /// <summary>Returns a path class as the theory spells a path, or <c>null</c>.</summary>
        public JsonPathSpec? Path(ClassId id) => Memo(_paths, id, () => _members.TryGetValue(id, out var nodes) ? BuildPath(nodes[0]) : null);

        /// <summary>Returns a path class as a document path, where it is one: concrete names under the root, and no ghost.</summary>
        public JsonDocumentPath? DocumentPath(ClassId id)
        {
            if (Path(id) is not JsonPathSpec spec || spec.Hidden is not null || spec.Segments.Any(s => s is not string))
                return null;

            return spec.Segments.Cast<string>().Aggregate(JsonDocumentPath.Root, (path, name) => path.Property(name));
        }

        T? Memo<T>(Dictionary<ClassId, T?> memo, ClassId id, Func<T?> build) where T : class
        {
            if (memo.TryGetValue(id, out var known))
                return known;

            // A class reached again while it is being built lies beneath itself, which only a union makes.
            if (_building.Add(id) == false)
                return null;

            try
            {
                return memo[id] = build();
            }
            finally
            {
                _building.Remove(id);
            }
        }

        SqlPredicate? BuildPredicate(Node node)
        {
            var k = node.Keys;
            var head = node.Head;

            if (head == _rules.Compare)
                return Value((ClassId)k[1]) is SqlValue left && Value((ClassId)k[2]) is SqlValue right ? new SqlPredicate.Compare((string)k[0], left, right) : null;
            if (head == _rules.In)
                return Value((ClassId)k[0]) is SqlValue member ? new SqlPredicate.In(member, (ValueSet<string>)k[1]) : null;
            if (head == _rules.IsNull)
                return Value((ClassId)k[0]) is SqlValue tested ? new SqlPredicate.IsNull(tested) : null;
            if (head == _rules.And)
                return Predicate((ClassId)k[0]) is SqlPredicate a && Predicate((ClassId)k[1]) is SqlPredicate b ? new SqlPredicate.And(new[] { a, b }) : null;
            if (head == _rules.Or)
                return Predicate((ClassId)k[0]) is SqlPredicate c && Predicate((ClassId)k[1]) is SqlPredicate d ? new SqlPredicate.Or(new[] { c, d }) : null;
            if (head == _rules.Not)
                return Predicate((ClassId)k[0]) is SqlPredicate operand ? new SqlPredicate.Not(operand) : null;
            if (head == _rules.Opaque)
                return new SqlPredicate.Opaque((long)k[0]);

            return null;
        }

        SqlValue? BuildValue(Node node)
        {
            var k = node.Keys;
            var head = node.Head;

            if (head == _rules.Text)
                return DocumentPath((ClassId)k[0]) is JsonDocumentPath text ? new SqlValue.Text(text) : null;
            if (head == _rules.Raw)
                return DocumentPath((ClassId)k[0]) is JsonDocumentPath raw ? new SqlValue.Raw(raw) : null;
            if (head == _rules.Typed)
                return DocumentPath((ClassId)k[0]) is JsonDocumentPath typed ? new SqlValue.Typed(typed, (string)k[1]) : null;
            if (head == _rules.Cast)
                return Value((ClassId)k[1]) is SqlValue converted ? new SqlValue.Cast((string)k[0], converted) : null;
            if (head == _rules.Literal)
                return new SqlValue.Literal((string)k[0]);
            if (head == _rules.Parameter)
                return new SqlValue.Parameter((long)k[0], (string)k[1]);

            return null;
        }

        JsonPathSpec? BuildPath(Node node)
        {
            var k = node.Keys;
            var head = node.Head;
            var json = _rules.Json;

            if (head == json.Root)
                return JsonPathSpec.Root;
            if (head == json.Prop)
                return Path((ClassId)k[0]) is JsonPathSpec parent && parent.Hidden is null ? parent[(string)k[1]] : null;
            if (head == json.Index)
                return Path((ClassId)k[0]) is JsonPathSpec array && array.Hidden is null ? array[(long)k[1]] : null;
            if (head == json.Each)
                return Path((ClassId)k[0]) is JsonPathSpec elements && elements.Hidden is null ? elements.Elements : null;
            if (head == json.Ghost)
                return JsonPathSpec.HiddenNamed((string)k[0]);

            return null;
        }

        /// <summary>
        /// Evaluates a node over its children's representatives, and its class's representative, on a document: a truth
        /// value for a predicate, a SQL value for a value, the values found for a path. <c>null</c> where one cannot be.
        /// </summary>
        public (string? Member, string? Representative) Denote(ClassId id, Node node, J.Obj document, World world)
        {
            if (Predicates.Contains(node.Head))
                return (BuildPredicate(node) is SqlPredicate member ? IrEval.Truth(member, document, world).ToString() : null,
                    Predicate(id) is SqlPredicate representative ? IrEval.Truth(representative, document, world).ToString() : null);

            if (Values.Contains(node.Head))
                return (BuildValue(node) is SqlValue member ? IrEval.Denote(member, document, world) : null,
                    Value(id) is SqlValue representative ? IrEval.Denote(representative, document, world) : null);

            return (BuildPath(node) is JsonPathSpec path && path.Hidden is null ? Found(path, document) : null,
                Path(id) is JsonPathSpec rep && rep.Hidden is null ? Found(rep, document) : null);
        }

        static string Found(JsonPathSpec path, J document) => "[" + string.Join(", ", Documents.Values(document, path).Select(Documents.Key)) + "]";

        /// <summary>Spells a node over its children's representatives.</summary>
        public string Spell(Node node) =>
            BuildPredicate(node)?.ToString() ?? BuildValue(node)?.ToString() ?? BuildPath(node)?.ToString() ?? node.Head.Name;

    }

}
