using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;

using Eggnog;

namespace EggnogDerive
{

    /// <summary>
    /// The second encoding: a container's rules as <em>rows</em>, read by a fixed set of rules in the root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="JsonFactProgram"/> gives every compiled rule an Eggnog rule of its own, and an engine
    /// evaluates every rule on every iteration whether or not anything it reads changed. Here the rules
    /// are data. Rule <c>r</c>'s body is a chain of atoms; <c>step(r, k)</c> says its first <c>k</c> hold,
    /// and an atom row keyed <c>(r, k)</c> says what extends the chain to <c>k + 1</c>. When the chain is
    /// complete, <c>done(r)</c>, and the head rows of <c>r</c> become facts.
    /// </para>
    /// <para>
    /// The point is the join order a query's facts drive. Pinning <c>kind</c> to one value derives
    /// <c>pinned(kind, v)</c>, whose new row joins the equality atoms on <c>(path, value)</c> through an
    /// index, so the work is the rules that value guards, not the rules there are.
    /// </para>
    /// </remarks>
    sealed class JsonFactData
    {

        readonly Relation<long, long> _step;
        readonly Relation<long, long> _length;
        readonly Relation<long> _done;
        readonly Relation<string, string> _pinned;

        readonly Relation<long, long, string> _atomPresent;
        readonly Relation<long, long, string, string> _atomEqual;
        readonly Relation<long, long, string, ValueSet<string>> _atomIn;
        readonly Relation<long, long, string, ValueSet<string>> _atomType;
        readonly Relation<long, long, string, string> _atomNotEqual;
        readonly Relation<long, long, string, string> _atomForm;

        readonly Relation<long, string> _headPresent;
        readonly Relation<long, string, ValueSet<string>> _headType;
        readonly Relation<long, string, ValueSet<string>> _headDomain;
        readonly Relation<long, string, string> _headNotEqual;
        readonly Relation<long, string, string> _headForm;

        long _next;

        public JsonFactData(EGraph graph)
        {
            Tables = new JsonFactTables(graph);
            var t = Tables;

            _step = graph.DefineRelation<long, long>("step");
            _length = graph.DefineRelation<long, long>("length");
            _done = graph.DefineRelation<long>("done");
            _pinned = graph.DefineRelation<string, string>("pinned");
            _atomPresent = graph.DefineRelation<long, long, string>("atom_present");
            _atomEqual = graph.DefineRelation<long, long, string, string>("atom_equal");
            _atomIn = graph.DefineRelation<long, long, string, ValueSet<string>>("atom_in");
            _atomType = graph.DefineRelation<long, long, string, ValueSet<string>>("atom_type");
            _atomNotEqual = graph.DefineRelation<long, long, string, string>("atom_ne");
            _atomForm = graph.DefineRelation<long, long, string, string>("atom_form");
            _headPresent = graph.DefineRelation<long, string>("head_present");
            _headType = graph.DefineRelation<long, string, ValueSet<string>>("head_type");
            _headDomain = graph.DefineRelation<long, string, ValueSet<string>>("head_dom");
            _headNotEqual = graph.DefineRelation<long, string, string>("head_ne");
            _headForm = graph.DefineRelation<long, string, string>("head_form");

            var rules = t.Schema;

            rules.DefineRule("a singleton domain pins its value", r =>
            {
                var (p, d) = r.Vars<string, ValueSet<string>>("p", "d");
                r.When(d.Is(t.Dom.Of(p)));
                r.Where(d, x => x.Count == 1);
                r.Then(_pinned.Of(p, Compute.Of(d, x => x[0])));
            });

            rules.DefineRule("a present atom", r =>
            {
                var (id, k, p) = r.Vars<long, long, string>("r", "k", "p");
                r.When(_step.Of(id, k), _atomPresent.Of(id, k, p), t.Present.Of(p));
                r.Then(_step.Of(id, Primitives.Add.Of(k, 1L)));
            });
            rules.DefineRule("an equality atom", r =>
            {
                var (id, k, p, v) = r.Vars<long, long, string, string>("r", "k", "p", "v");
                r.When(_pinned.Of(p, v), _atomEqual.Of(id, k, p, v), _step.Of(id, k));
                r.Then(_step.Of(id, Primitives.Add.Of(k, 1L)));
            });
            rules.DefineRule("a domain atom", r =>
            {
                var (id, k, p) = r.Vars<long, long, string>("r", "k", "p");
                var (s, d) = r.Vars<ValueSet<string>, ValueSet<string>>("s", "d");
                r.When(_step.Of(id, k), _atomIn.Of(id, k, p, s), d.Is(t.Dom.Of(p)));
                r.Where(Primitives.IsSubsetOf<string>().Of(d, s));
                r.Then(_step.Of(id, Primitives.Add.Of(k, 1L)));
            });
            rules.DefineRule("a type atom", r =>
            {
                var (id, k, p) = r.Vars<long, long, string>("r", "k", "p");
                var (m, ty) = r.Vars<ValueSet<string>, ValueSet<string>>("m", "ty");
                r.When(_step.Of(id, k), _atomType.Of(id, k, p, m), ty.Is(t.Types.Of(p)));
                r.Where(Primitives.IsSubsetOf<string>().Of(ty, m));
                r.Then(_step.Of(id, Primitives.Add.Of(k, 1L)));
            });
            rules.DefineRule("a stated disequality atom", r =>
            {
                var (id, k, p, v) = r.Vars<long, long, string, string>("r", "k", "p", "v");
                r.When(_step.Of(id, k), _atomNotEqual.Of(id, k, p, v), t.Ne.Of(p, v));
                r.Then(_step.Of(id, Primitives.Add.Of(k, 1L)));
            });
            rules.DefineRule("a disequality atom a domain proves", r =>
            {
                var (id, k, p, v) = r.Vars<long, long, string, string>("r", "k", "p", "v");
                var d = r.Var<ValueSet<string>>("d");
                r.When(_step.Of(id, k), _atomNotEqual.Of(id, k, p, v), d.Is(t.Dom.Of(p)));
                r.Where(Primitives.Contains<string>().Of(d, v), x => x == false);
                r.Then(_step.Of(id, Primitives.Add.Of(k, 1L)));
            });
            rules.DefineRule("a form atom", r =>
            {
                var (id, k, p, f) = r.Vars<long, long, string, string>("r", "k", "p", "f");
                r.When(_step.Of(id, k), _atomForm.Of(id, k, p, f), t.Form.Of(p, f));
                r.Then(_step.Of(id, Primitives.Add.Of(k, 1L)));
            });

            rules.DefineRule("a complete body", r =>
            {
                var (id, n) = r.Vars<long, long>("r", "n");
                r.When(_step.Of(id, n), _length.Of(id, n));
                r.Then(_done.Of(id));
            });

            rules.DefineRule("a present head", r =>
            {
                var (id, p) = r.Vars<long, string>("r", "p");
                r.When(_done.Of(id), _headPresent.Of(id, p));
                r.Then(t.Present.Of(p));
            });
            rules.DefineRule("a type head", r =>
            {
                var (id, p) = r.Vars<long, string>("r", "p");
                var m = r.Var<ValueSet<string>>("m");
                r.When(_done.Of(id), _headType.Of(id, p, m));
                r.Then(t.Types.Of(p).Is(m));
            });
            rules.DefineRule("a domain head", r =>
            {
                var (id, p) = r.Vars<long, string>("r", "p");
                var s = r.Var<ValueSet<string>>("s");
                r.When(_done.Of(id), _headDomain.Of(id, p, s));
                r.Then(t.Dom.Of(p).Is(s));
            });
            rules.DefineRule("a disequality head", r =>
            {
                var (id, p, v) = r.Vars<long, string, string>("r", "p", "v");
                r.When(_done.Of(id), _headNotEqual.Of(id, p, v));
                r.Then(t.Ne.Of(p, v));
            });
            rules.DefineRule("a form head", r =>
            {
                var (id, p, f) = r.Vars<long, string, string>("r", "p", "f");
                r.When(_done.Of(id), _headForm.Of(id, p, f));
                r.Then(t.Form.Of(p, f));
            });
        }

        public JsonFactTables Tables { get; }

        /// <summary>Writes a container's rules into a scope as rows, and its unconditional ones as facts.</summary>
        public void Declare(Scope scope, IReadOnlyList<JsonFactRule> rules)
        {
            var facts = new List<Fact>();
            var unconditional = new List<JsonFact>();

            foreach (var rule in rules)
            {
                if (rule.Body.Count == 0)
                {
                    unconditional.Add(rule.Head);
                    continue;
                }

                var id = ++_next;
                facts.Add(_step.Of(id, 0L));
                facts.Add(_length.Of(id, (long)rule.Body.Count));

                for (var k = 0; k < rule.Body.Count; k++)
                {
                    var atom = rule.Body[k];
                    var p = JsonFactTables.Path(atom.Path);
                    long at = k;

                    facts.Add(atom.Claim switch
                    {
                        JsonClaim.Present => _atomPresent.Of(id, at, p),
                        JsonClaim.EqualTo equal => _atomEqual.Of(id, at, p, JsonFactTables.Encode(equal.Value)),
                        JsonClaim.OneOf oneOf => _atomIn.Of(id, at, p, JsonFactTables.Encode(oneOf.Values)),
                        JsonClaim.OfType typed => _atomType.Of(id, at, p, JsonFactTables.Members(typed.Type, typed.OrNull)),
                        JsonClaim.NotEqualTo notEqual => _atomNotEqual.Of(id, at, p, JsonFactTables.Encode(notEqual.Value)),
                        JsonClaim.Represents represents => _atomForm.Of(id, at, p, represents.Form.Name),
                        _ => throw new NotSupportedException(atom.Claim.GetType().Name),
                    });
                }

                var head = JsonFactTables.Path(rule.Head.Path);
                facts.Add(rule.Head.Claim switch
                {
                    JsonClaim.Present => _headPresent.Of(id, head),
                    JsonClaim.OfType typed => _headType.Of(id, head, JsonFactTables.Members(typed.Type, typed.OrNull)),
                    JsonClaim.EqualTo equal => _headDomain.Of(id, head, ValueSet.Of(JsonFactTables.Encode(equal.Value))),
                    JsonClaim.OneOf oneOf => _headDomain.Of(id, head, JsonFactTables.Encode(oneOf.Values)),
                    JsonClaim.NotEqualTo notEqual => _headNotEqual.Of(id, head, JsonFactTables.Encode(notEqual.Value)),
                    JsonClaim.Represents represents => _headForm.Of(id, head, represents.Form.Name),
                    _ => throw new NotSupportedException(rule.Head.Claim.GetType().Name),
                });
            }

            if (facts.Count > 0)
                scope.Assume(facts.ToArray());

            JsonFactProgram.Establish(Tables, scope, unconditional);
        }

    }

}
