using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;

using Eggnog;

namespace EggnogDerive
{

    /// <summary>
    /// Translates the adapter's compiled <see cref="JsonFactRule"/>s into Eggnog, mechanically: an
    /// unconditional rule becomes an assumption, a guarded one a rule whose body asks what
    /// <see cref="JsonFactSet.Knows"/> would ask, and the questions consumers put to a
    /// <see cref="JsonFactSet"/> become reads of a scope.
    /// </summary>
    static class JsonFactProgram
    {

        /// <summary>
        /// Writes a container's rules into a scope: the unconditional ones as facts, the guarded ones as
        /// rules of the <c>schema</c> ruleset — local to <paramref name="scope"/> when <paramref name="local"/>,
        /// global otherwise.
        /// </summary>
        public static void Declare(JsonFactTables t, Scope scope, IReadOnlyList<JsonFactRule> rules, bool local, bool merge = false)
        {
            var facts = new List<Fact>();
            var index = 0;

            // The plan of record's §2.5: rules with identical bodies become one rule with several heads.
            var groups = new List<(IReadOnlyList<JsonFact> Body, List<JsonFact> Heads)>();
            var byBody = new Dictionary<string, int>();

            foreach (var rule in rules)
            {
                if (rule.Body.Count == 0)
                {
                    facts.Add(Head(t, rule.Head));
                    continue;
                }

                var key = merge ? string.Join(" & ", rule.Body.Select(f => f.Path + " " + f.Claim)) : (groups.Count).ToString();
                if (byBody.TryGetValue(key, out var at) == false)
                {
                    byBody[key] = at = groups.Count;
                    groups.Add((rule.Body, new List<JsonFact>()));
                }

                groups[at].Heads.Add(rule.Head);
            }

            foreach (var (body, heads) in groups)
            {
                index++;

                // A NotEqualTo in a body is known two ways — stated, or by a domain that leaves the value
                // out — so a body becomes one Eggnog rule per combination of the ways its atoms are known.
                foreach (var variant in Variants(body))
                {
                    var name = $"r{index}:{string.Join("", variant)}";
                    void Define(RuleBuilder r)
                    {
                        Body(t, r, body, variant);
                        r.Then(heads.Select(head => Head(t, head)).ToArray());
                    }

                    if (local)
                        scope.DefineRule(t.Schema, name, Define);
                    else
                        t.Schema.DefineRule(name, Define);
                }
            }

            if (facts.Count > 0)
                scope.Assume(facts.ToArray());
        }

        /// <summary>What a query established, as assumptions.</summary>
        public static void Establish(JsonFactTables t, Scope scope, IReadOnlyList<JsonFact> established)
        {
            if (established.Count == 0)
                return;

            var facts = new Fact[established.Count];
            for (var i = 0; i < facts.Length; i++)
                facts[i] = Head(t, established[i]);

            scope.Assume(facts);
        }

        /// <summary>
        /// The question <see cref="JsonFactSet.Knows"/> answers, put to a scope. A contradicted scope
        /// knows everything, and is asked about nothing; callers check it first.
        /// </summary>
        public static bool Knows(JsonFactTables t, Scope scope, JsonFact fact)
        {
            var p = JsonFactTables.Path(fact.Path);

            switch (fact.Claim)
            {
                case JsonClaim.Present:
                    return scope.Check(t.Present.Of(p));
                case JsonClaim.OfType typed:
                    return scope.TryGet(t.Types.Of(p), out var types) && types.IsSubsetOf(JsonFactTables.Members(typed.Type, typed.OrNull));
                case JsonClaim.EqualTo equal:
                    return scope.TryGet(t.Dom.Of(p), out var d1) && d1.IsSubsetOf(ValueSet.Of(JsonFactTables.Encode(equal.Value)));
                case JsonClaim.OneOf oneOf:
                    return scope.TryGet(t.Dom.Of(p), out var d2) && d2.IsSubsetOf(JsonFactTables.Encode(oneOf.Values));
                case JsonClaim.NotEqualTo notEqual:
                    var v = JsonFactTables.Encode(notEqual.Value);
                    return scope.Check(t.Ne.Of(p, v)) || scope.TryGet(t.Dom.Of(p), out var d3) && d3.Contains(v) == false;
                case JsonClaim.Represents represents:
                    return scope.Check(t.Form.Of(p, represents.Form.Name));
                default:
                    throw new NotSupportedException(fact.Claim.GetType().Name);
            }
        }

        static Fact Head(JsonFactTables t, JsonFact fact)
        {
            var p = JsonFactTables.Path(fact.Path);

            return fact.Claim switch
            {
                JsonClaim.Present => t.Present.Of(p),
                JsonClaim.OfType typed => t.Types.Of(p).Is(JsonFactTables.Members(typed.Type, typed.OrNull)),
                JsonClaim.EqualTo equal => t.Dom.Of(p).Is(ValueSet.Of(JsonFactTables.Encode(equal.Value))),
                JsonClaim.OneOf oneOf => t.Dom.Of(p).Is(JsonFactTables.Encode(oneOf.Values)),
                JsonClaim.NotEqualTo notEqual => t.Ne.Of(p, JsonFactTables.Encode(notEqual.Value)),
                JsonClaim.Represents represents => t.Form.Of(p, represents.Form.Name),
                _ => throw new NotSupportedException(fact.Claim.GetType().Name),
            };
        }

        static void Body(JsonFactTables t, RuleBuilder r, IReadOnlyList<JsonFact> body, int[] variant)
        {
            var when = new List<Fact>();
            var where = new List<Action>();

            for (var i = 0; i < body.Count; i++)
            {
                var atom = body[i];
                var p = JsonFactTables.Path(atom.Path);

                switch (atom.Claim)
                {
                    case JsonClaim.Present:
                        when.Add(t.Present.Of(p));
                        break;
                    case JsonClaim.OfType typed:
                    {
                        var members = JsonFactTables.Members(typed.Type, typed.OrNull);
                        var types = r.Var<ValueSet<string>>("t" + i);
                        when.Add(types.Is(t.Types.Of(p)));
                        where.Add(() => r.Where(types, x => x.IsSubsetOf(members)));
                        break;
                    }
                    case JsonClaim.EqualTo or JsonClaim.OneOf:
                    {
                        var allowed = atom.Claim is JsonClaim.EqualTo equal
                            ? ValueSet.Of(JsonFactTables.Encode(equal.Value))
                            : JsonFactTables.Encode(((JsonClaim.OneOf)atom.Claim).Values);
                        var domain = r.Var<ValueSet<string>>("d" + i);
                        when.Add(domain.Is(t.Dom.Of(p)));
                        where.Add(() => r.Where(domain, x => x.IsSubsetOf(allowed)));
                        break;
                    }
                    case JsonClaim.NotEqualTo notEqual:
                    {
                        var v = JsonFactTables.Encode(notEqual.Value);
                        if (variant[i] == 0)
                        {
                            when.Add(t.Ne.Of(p, v));
                        }
                        else
                        {
                            var domain = r.Var<ValueSet<string>>("d" + i);
                            when.Add(domain.Is(t.Dom.Of(p)));
                            where.Add(() => r.Where(domain, x => x.Contains(v) == false));
                        }

                        break;
                    }
                    case JsonClaim.Represents represents:
                        when.Add(t.Form.Of(p, represents.Form.Name));
                        break;
                    default:
                        throw new NotSupportedException(atom.Claim.GetType().Name);
                }
            }

            r.When(when.ToArray());
            foreach (var condition in where)
                condition();
        }

        static IEnumerable<int[]> Variants(IReadOnlyList<JsonFact> body)
        {
            var ways = body.Select(atom => atom.Claim is JsonClaim.NotEqualTo ? 2 : 1).ToArray();
            var current = new int[body.Count];

            while (true)
            {
                yield return (int[])current.Clone();

                var i = 0;
                while (i < current.Length && ++current[i] == ways[i])
                    current[i++] = 0;

                if (i == current.Length)
                    yield break;
            }
        }

    }

}
