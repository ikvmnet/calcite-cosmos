using System;
using System.Collections.Generic;

using com.fasterxml.jackson.databind;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// A JSON Schema, read as a source of facts about a container's documents.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One source among others.</b> What a model file declares is not the only thing knowable about
    /// a container — what the service guarantees about the properties it maintains itself is knowable
    /// too — so this yields <em>rules</em> rather than a theory, and
    /// <see cref="CosmosFactTheory"/> is what a container assembles out of every source it has. They
    /// have to end up in one theory rather than several: a rule's body may be satisfied by a fact
    /// another source stated, and forward chaining only fires such a rule when it sees both at once.
    /// </para>
    /// <para>
    /// The walk carries a <em>guard</em> — the facts that must hold for the branch being walked to be
    /// the one that applies — and every fact it reads becomes a rule with that guard as its body. A
    /// schema with no conditionals produces rules with empty bodies, which is the ordinary case and
    /// costs a dictionary hit to consult.
    /// </para>
    /// <para>
    /// <b>Everything here loses facts rather than inventing them.</b> A keyword that is not understood
    /// is ignored, not refused, so a richer schema stays legal and yields what can be read from it. The
    /// places that matter are called out below, because each one is a point where being clever would be
    /// unsound rather than merely incomplete.
    /// </para>
    /// <para>
    /// See <c>DESIGN.md</c> under <em>Reading a schema: recognition, not inference</em> for the keyword table and the reasoning behind each entry.
    /// </para>
    /// </remarks>
    public static class CosmosSchemaFacts
    {

        /// <summary>
        /// Reads the facts a declared schema states.
        /// </summary>
        /// <param name="schema">The schema document, as the tree the model delivered.</param>
        /// <returns>The rules, which may be empty where nothing could be read.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="schema"/> is <c>null</c>.</exception>
        public static IReadOnlyList<CosmosFactRule> ReadFrom(JsonNode schema)
        {
            if (schema is null)
                throw new ArgumentNullException(nameof(schema));

            var rules = new List<CosmosFactRule>();
            var resolver = new CosmosSchemaResolver(schema);

            Walk(schema, CosmosDocumentPath.Root, Array.Empty<CosmosFact>(), rules, resolver, new HashSet<string>(StringComparer.Ordinal));

            return rules;
        }

        /// <summary>
        /// Reads every fact one schema node states about <paramref name="path"/>, under
        /// <paramref name="guard"/>, and recurses into the nodes below it.
        /// </summary>
        static void Walk(
            JsonNode? node,
            CosmosDocumentPath path,
            IReadOnlyList<CosmosFact> guard,
            List<CosmosFactRule> rules,
            CosmosSchemaResolver resolver,
            HashSet<string> visiting)
        {
            if (node is null || node.isObject() == false)
                return;

            // A reference is followed rather than inlined, and a cycle stops here. A recursive schema
            // is legal and describes an unbounded document; the facts on the way round are already
            // recorded, and going round again would not add one. The guard is keyed by where the
            // reference lands rather than by how it is spelled: in a bundle the same text names
            // different targets from different resources, and different texts name the same one.
            if (Text(node, "$ref") is not null)
            {
                if (resolver.Resolve(node, out var location) is not JsonNode target || visiting.Add(location!) == false)
                    return;

                Walk(target, path, guard, rules, resolver, visiting);
                visiting.Remove(location!);
                return;
            }

            void State(CosmosClaim claim) => rules.Add(new CosmosFactRule(guard, new CosmosFact(path, claim)));

            var declared = ReadType(node);

            if (declared is var (type, orNull))
                State(new CosmosClaim.OfType(type, orNull));

            if (node.get("const") is JsonNode constant && TryLiteral(constant, out var constantValue))
                State(new CosmosClaim.EqualTo(constantValue));

            if (ReadEnum(node) is IReadOnlyList<object?> domain)
                State(new CosmosClaim.OneOf(domain));

            // A pattern constrains a string and is vacuous for anything else, so one written beside no
            // declared type says nothing: a document storing the number 30 at that path conforms to it.
            // Reading it as a stored form anyway would claim the value is a string -- Represents entails
            // as much -- and the guard that admits a non-string would then be dropped from a comparison
            // that still has to decide one. The same shape as properties being vacuous for an absent
            // path, one level over.
            if (declared?.Type == CosmosJsonType.String && CosmosStoredForms.Recognise(Text(node, "pattern")) is CosmosRepresentation representation)
                State(new CosmosClaim.Represents(representation));

            // A geography is proven from the subschema rather than taken from a token, for the reason
            // CosmosGeographyForms gives: GeoJSON validity is not Cosmos measurability, so neither a
            // format of our own nor a $ref to the published schema says what has to be said. What
            // does is the declaration pinning the type name, both members, and each ordinate's range
            // -- which a schema can do for a point and a line and cannot do for a ring.
            if (CosmosGeographyForms.Recognise(node))
                State(new CosmosClaim.Geography());

            // required names the children that are there whenever this object is. The claim is about
            // the child, and it is conditional on the parent: `required` constrains an object, and
            // says nothing at all where there is no object to constrain. So a nested one carries the
            // parent's own presence in its guard, which chains the whole way up; the document itself
            // needs no such guard, being what every path is read out of.
            if (node.get("required") is JsonNode required && required.isArray())
            {
                var carrier = path.IsRoot
                    ? guard
                    : Extend(guard, new CosmosFact(path, new CosmosClaim.Present()));

                for (var i = 0; i < required.size(); i++)
                    if (required.get(i)?.isTextual() == true)
                        rules.Add(new CosmosFactRule(carrier, new CosmosFact(path.Property(required.get(i).asText()), new CosmosClaim.Present())));
            }

            if (node.get("properties") is JsonNode properties && properties.isObject())
            {
                var fields = properties.fields();
                while (fields.hasNext())
                {
                    var field = (java.util.Map.Entry)fields.next();
                    if (field.getKey()?.ToString() is string name)
                        Walk((JsonNode?)field.getValue(), path.Property(name), guard, rules, resolver, visiting);
                }
            }

            // allOf is a conjunction: every branch applies, so every branch's facts hold under the
            // same guard.
            if (node.get("allOf") is JsonNode all && all.isArray())
                for (var i = 0; i < all.size(); i++)
                    Walk(all.get(i), path, guard, rules, resolver, visiting);

            WalkBranches(node.get("oneOf"), node, path, guard, rules, resolver, visiting);
            WalkBranches(node.get("anyOf"), node, path, guard, rules, resolver, visiting);
            WalkConditional(node, path, guard, rules, resolver, visiting);
            WalkDependencies(node, path, guard, rules, resolver, visiting);
            WalkNegation(node, path, guard, rules, resolver);
        }

        /// <summary>
        /// Walks the two keywords that key a constraint on a property merely being there.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>dependentRequired</c> says which children come together; <c>dependentSchemas</c> says
        /// what else holds once one of them appears. Both are conditionals whose condition is a
        /// presence, which the guard already expresses, so neither needs anything new.
        /// </para>
        /// <para>
        /// Neither carries the parent's presence the way <c>required</c> has to. The trigger is a
        /// child of this object, and a child cannot be there unless the object is — so the guard
        /// already says everything the vacuity would have needed.
        /// </para>
        /// </remarks>
        static void WalkDependencies(
            JsonNode node,
            CosmosDocumentPath path,
            IReadOnlyList<CosmosFact> guard,
            List<CosmosFactRule> rules,
            CosmosSchemaResolver resolver,
            HashSet<string> visiting)
        {
            if (node.get("dependentRequired") is JsonNode dependent && dependent.isObject())
            {
                var entries = dependent.fields();
                while (entries.hasNext())
                {
                    var entry = (java.util.Map.Entry)entries.next();
                    if (entry.getKey()?.ToString() is not string trigger || (JsonNode?)entry.getValue() is not JsonNode names || names.isArray() == false)
                        continue;

                    var when = Extend(guard, new CosmosFact(path.Property(trigger), new CosmosClaim.Present()));

                    for (var i = 0; i < names.size(); i++)
                        if (names.get(i)?.isTextual() == true)
                            rules.Add(new CosmosFactRule(when, new CosmosFact(path.Property(names.get(i).asText()), new CosmosClaim.Present())));
                }
            }

            if (node.get("dependentSchemas") is JsonNode schemas && schemas.isObject())
            {
                var entries = schemas.fields();
                while (entries.hasNext())
                {
                    var entry = (java.util.Map.Entry)entries.next();
                    if (entry.getKey()?.ToString() is not string trigger)
                        continue;

                    Walk((JsonNode?)entry.getValue(), path, Extend(guard, new CosmosFact(path.Property(trigger), new CosmosClaim.Present())), rules, resolver, visiting);
                }
            }
        }

        /// <summary>
        /// Reads a <c>not</c>, in the one shape whose negation is a conjunction.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The negation has to be taken of the schema, not of the atoms read from it.</b>
        /// <c>{properties: {k: {const: "A"}}}</c> is satisfied by a document with no <c>k</c> at all,
        /// so failing it means <c>k</c> is there <em>and</em> is not <c>"A"</c> — two positive claims.
        /// Negating the atom instead would give "absent or not A", a disjunction with no conjunctive
        /// body and a weaker statement than the truth.
        /// </para>
        /// <para>
        /// Which is also why almost nothing else qualifies. Failing a <c>type</c> needs a claim that a
        /// value is not of a type, failing a <c>required</c> needs one that a path is absent, and
        /// failing a schema with two constraints is a disjunction over which of them failed. One
        /// property constrained by one <c>const</c> or one <c>enum</c> is the whole of what this
        /// reads; anything else yields nothing.
        /// </para>
        /// </remarks>
        static void WalkNegation(
            JsonNode node,
            CosmosDocumentPath path,
            IReadOnlyList<CosmosFact> guard,
            List<CosmosFactRule> rules,
            CosmosSchemaResolver resolver)
        {
            if (resolver.Follow(node.get("not")) is not JsonNode negated || negated.isObject() == false)
                return;

            if (negated.size() != 1 || negated.get("properties") is not JsonNode properties || properties.isObject() == false || properties.size() != 1)
                return;

            var field = (java.util.Map.Entry)properties.fields().next();
            if (field.getKey()?.ToString() is not string name || resolver.Follow((JsonNode?)field.getValue()) is not JsonNode subschema)
                return;

            if (subschema.size() != 1)
                return;

            var child = path.Property(name);
            var excluded = new List<object?>();

            if (subschema.get("const") is JsonNode constant && TryLiteral(constant, out var value))
                excluded.Add(value);
            else if (ReadEnum(subschema) is IReadOnlyList<object?> domain)
                excluded.AddRange(domain);
            else
                return;

            // Failing the inner schema means the property is there and is none of what it named.
            rules.Add(new CosmosFactRule(guard, new CosmosFact(child, new CosmosClaim.Present())));

            foreach (var member in excluded)
                rules.Add(new CosmosFactRule(guard, new CosmosFact(child, new CosmosClaim.NotEqualTo(member))));
        }

        /// <summary>
        /// Returns a guard with one more fact in it.
        /// </summary>
        static IReadOnlyList<CosmosFact> Extend(IReadOnlyList<CosmosFact> guard, CosmosFact fact)
        {
            var extended = new List<CosmosFact>(guard.Count + 1);
            extended.AddRange(guard);
            extended.Add(fact);

            return extended;
        }

        /// <summary>
        /// Walks a <c>oneOf</c> or <c>anyOf</c>, which is only useful where a discriminator says which
        /// branch a document is in.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Discriminated.</b> Where every branch pins the same property to a different <c>const</c>,
        /// proving that property's value proves which branch applies — under <c>oneOf</c> because
        /// exactly one matches and the others are refuted, under <c>anyOf</c> because at least one does
        /// and only one can. So the branch is walked with that equality added to the guard.
        /// </para>
        /// <para>
        /// <b>Undiscriminated.</b> Nothing selects a branch, so the only facts that hold are the ones
        /// every branch states — the meet. Taking a branch's facts on their own would be claiming what
        /// only one alternative says, and stating the alternatives as a disjunction would take the
        /// theory out of Horn and make asking it intractable. Only the facts each branch states
        /// directly are intersected; a conditional nested inside an undiscriminated branch is dropped,
        /// which loses facts and stays sound.
        /// </para>
        /// <para>
        /// <b>Nullable.</b> Where every branch but one admits only <c>null</c>, the union is that one
        /// branch made nullable — the commonest way 2020-12 spells an optional value, and the same
        /// statement <c>"type": [X, "null"]</c> makes beside the branch's keywords. The meet would
        /// read nothing, <c>{ "type": "null" }</c> sharing no fact with anything; reading the branch
        /// instead is an equivalence rather than a weakening, provided each fact it states is widened
        /// to admit the null — see <see cref="AdmitNull"/>.
        /// </para>
        /// </remarks>
        static void WalkBranches(
            JsonNode? branches,
            JsonNode parent,
            CosmosDocumentPath path,
            IReadOnlyList<CosmosFact> guard,
            List<CosmosFactRule> rules,
            CosmosSchemaResolver resolver,
            HashSet<string> visiting)
        {
            if (branches is null || branches.isArray() == false || branches.size() == 0)
                return;

            if (FindDiscriminator(branches, parent, resolver) is string discriminator)
            {
                var walked = new List<Branch>();

                for (var i = 0; i < branches.size(); i++)
                {
                    // The branch as written, not as resolved: a mapping names its target by the
                    // reference, which following it would have thrown away.
                    if (branches.get(i) is not JsonNode branch || DiscriminatorValue(branch, parent, discriminator, resolver) is not object value)
                        continue;

                    var selected = Extend(guard, new CosmosFact(path.Property(discriminator), new CosmosClaim.EqualTo(value)));
                    var stated = new List<CosmosFactRule>();
                    Walk(branches.get(i), path, selected, stated, resolver, visiting);

                    rules.AddRange(stated);
                    walked.Add(new Branch(selected.Count, stated));
                }

                // And what every branch states holds of every document, whichever branch it is in:
                // the per-branch facts pay only where a query proves the discriminator, and a query
                // over every kind proves none (#175). Only where every branch was read -- a branch
                // left out is one the meet would be claiming for without having asked.
                if (walked.Count == branches.size())
                    rules.AddRange(Meet(walked, guard));

                return;
            }

            if (NullableBranch(branches, resolver) is JsonNode nullable)
            {
                var stated = new List<CosmosFactRule>();
                Walk(nullable, path, guard, stated, resolver, visiting);

                foreach (var rule in stated)
                    if (AdmitNull(rule, path) is CosmosFactRule widened)
                        rules.Add(widened);

                return;
            }

            var each = new List<Branch>();

            for (var i = 0; i < branches.size(); i++)
            {
                var stated = new List<CosmosFactRule>();
                Walk(branches.get(i), path, guard, stated, resolver, visiting);
                each.Add(new Branch(guard.Count, stated));
            }

            rules.AddRange(Meet(each, guard));
        }

        /// <summary>
        /// One branch of a union as walked: how long the guard it was walked under is, and what it
        /// stated.
        /// </summary>
        /// <remarks>
        /// A branch's guard is its parent's extended — by the discriminator's value where there is one —
        /// and every rule the walk wrote begins with it, <see cref="Extend"/> only ever appending. What
        /// follows it is what the branch made the fact conditional on.
        /// </remarks>
        readonly record struct Branch(int Guard, List<CosmosFactRule> Stated);

        /// <summary>
        /// Returns the facts every branch of a union entails, under the parent's guard.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why it holds unguarded.</b> Under <c>oneOf</c> or <c>anyOf</c> some branch applies to every
        /// valid document, so a claim every branch entails holds whichever applies. A discriminator does
        /// not weaken that; it only adds a way to know which.
        /// </para>
        /// <para>
        /// <b>By entailment, not equality (#175).</b> <c>"a string"</c> in one branch and <c>null</c> in
        /// another share no claim, and both entail <c>"a string or null"</c>, which is one claim and not a
        /// disjunction. So the candidates are each branch's claims, each type widened to admit a null, and
        /// at each path the domain joining every constant and domain the branches name; a candidate is
        /// kept where every branch has a claim entailing it. Every candidate kept is kept, not only the
        /// strongest: a consumer asking for a type by shape rather than through entailment should find
        /// it beside the stored form that entails it.
        /// </para>
        /// <para>
        /// <b>A conditional counts only against the same condition.</b> A branch's rule is compared by
        /// what it adds to the branch's own guard, so a <c>required</c> inside every branch — conditional
        /// on its parent object being there — meets the same <c>required</c> in the others, and nothing
        /// meets a condition only one branch states.
        /// </para>
        /// </remarks>
        static List<CosmosFactRule> Meet(List<Branch> branches, IReadOnlyList<CosmosFact> guard)
        {
            var meet = new List<CosmosFactRule>();

            if (branches.Count == 0)
                return meet;

            static IReadOnlyList<CosmosFact> Relative(CosmosFactRule rule, int guard)
            {
                var relative = new List<CosmosFact>();
                for (var i = guard; i < rule.Body.Count; i++)
                    relative.Add(rule.Body[i]);

                return relative;
            }

            static bool Same(IReadOnlyList<CosmosFact> left, IReadOnlyList<CosmosFact> right)
            {
                if (left.Count != right.Count)
                    return false;

                foreach (var fact in left)
                    if (Contains(right, fact) == false)
                        return false;

                return true;
            }

            static bool Contains(IReadOnlyList<CosmosFact> facts, CosmosFact fact)
            {
                foreach (var candidate in facts)
                    if (candidate.Equals(fact))
                        return true;

                return false;
            }

            var stated = new List<List<(IReadOnlyList<CosmosFact> Condition, CosmosFact Head)>>();
            foreach (var branch in branches)
            {
                var list = new List<(IReadOnlyList<CosmosFact>, CosmosFact)>();
                foreach (var rule in branch.Stated)
                    list.Add((Relative(rule, branch.Guard), rule.Head));

                stated.Add(list);
            }

            // The candidates, in the order the branches state them so the result is stable.
            var candidates = new List<(IReadOnlyList<CosmosFact> Condition, CosmosFact Head)>();

            void Offer(IReadOnlyList<CosmosFact> condition, CosmosFact head)
            {
                foreach (var (c, h) in candidates)
                    if (h.Equals(head) && Same(c, condition))
                        return;

                candidates.Add((condition, head));
            }

            foreach (var list in stated)
            {
                foreach (var (condition, head) in list)
                {
                    Offer(condition, head);

                    if (head.Claim is CosmosClaim.OfType { OrNull: false } typed)
                        Offer(condition, new CosmosFact(head.Path, typed with { OrNull = true }));

                    // The domain joining every value the branches pin this path to, under this
                    // condition: a branch saying "park" and one saying "map" both entail it.
                    var values = new List<object?>();
                    foreach (var other in stated)
                    {
                        foreach (var (c, h) in other)
                        {
                            if (h.Path.Equals(head.Path) == false || Same(c, condition) == false)
                                continue;

                            if (h.Claim is CosmosClaim.EqualTo equal && CosmosClaim.OneOf.Contains(values, equal.Value) == false)
                                values.Add(equal.Value);
                            else if (h.Claim is CosmosClaim.OneOf domain)
                                foreach (var value in domain.Values)
                                    if (CosmosClaim.OneOf.Contains(values, value) == false)
                                        values.Add(value);
                        }
                    }

                    if (values.Count > 0)
                        Offer(condition, new CosmosFact(head.Path, new CosmosClaim.OneOf(values)));
                }
            }

            foreach (var (condition, head) in candidates)
            {
                var everywhere = true;

                foreach (var list in stated)
                {
                    var entailed = false;
                    foreach (var (c, h) in list)
                    {
                        if (Same(c, condition) && h.Entails(head))
                        {
                            entailed = true;
                            break;
                        }
                    }

                    if (entailed == false)
                    {
                        everywhere = false;
                        break;
                    }
                }

                if (everywhere)
                {
                    var body = new List<CosmosFact>(guard);
                    body.AddRange(condition);
                    meet.Add(new CosmosFactRule(body, head));
                }
            }

            return meet;
        }

        /// <summary>
        /// Returns the one branch of a union that admits more than <c>null</c>, where every other
        /// branch admits only <c>null</c>; otherwise <c>null</c>.
        /// </summary>
        /// <param name="branches">The branches.</param>
        /// <param name="resolver">Resolves a branch written as a reference.</param>
        /// <returns>The branch as written, so a reference in it is still followed by the walk.</returns>
        static JsonNode? NullableBranch(JsonNode branches, CosmosSchemaResolver resolver)
        {
            JsonNode? remaining = null;
            var nulls = 0;

            for (var i = 0; i < branches.size(); i++)
            {
                if (branches.get(i) is not JsonNode branch)
                    return null;

                if (AdmitsOnlyNull(resolver.Follow(branch)))
                {
                    nulls++;
                    continue;
                }

                if (remaining is not null)
                    return null;

                remaining = branch;
            }

            return nulls > 0 ? remaining : null;
        }

        /// <summary>
        /// Determines whether a subschema admits <c>null</c> and nothing else.
        /// </summary>
        /// <remarks>
        /// Recognised whole, the way a condition is: <c>{ "type": "null" }</c>, <c>{ "const": null }</c>
        /// or an <c>enum</c> of nothing but <c>null</c>, with only annotations beside. A branch carrying
        /// anything more is not taken for one, which leaves the union to the meet and loses facts rather
        /// than inventing them.
        /// </remarks>
        /// <param name="node">The subschema, already followed.</param>
        /// <returns><c>true</c> where the only value it admits is a JSON null.</returns>
        static bool AdmitsOnlyNull(JsonNode? node)
        {
            if (node is null || node.isObject() == false)
                return false;

            var constrained = false;
            var fields = node.fields();

            while (fields.hasNext())
            {
                var field = (java.util.Map.Entry)fields.next();
                var value = (JsonNode?)field.getValue();

                switch (field.getKey()?.ToString())
                {
                    case "type" when ReadType(node) is (CosmosJsonType.Null, _):
                    case "type" when value is not null && value.isArray() && value.size() == 1 && value.get(0)?.asText() == "null":
                    case "const" when value is not null && value.isNull():
                    case "enum" when ReadEnum(node) is IReadOnlyList<object?> domain && OnlyNulls(domain):
                        constrained = true;
                        break;

                    case "$comment":
                    case "title":
                    case "description":
                        break;

                    default:
                        return false;
                }
            }

            return constrained;

            static bool OnlyNulls(IReadOnlyList<object?> domain)
            {
                foreach (var member in domain)
                    if (member is not null)
                        return false;

                return true;
            }
        }

        /// <summary>
        /// Widens a rule read from the non-null branch of a nullable union so that it holds of the
        /// null too, or returns <c>null</c> where it cannot.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A claim about the union's own value</b> has to admit the null: a type becomes its
        /// <c>OrNull</c>, a constant and a domain gain <c>null</c> as a member, and a disequality
        /// already holds of a null unless it excluded one. A stored form already admits a null —
        /// it says how the strings are written and nothing about whether one is there.
        /// </para>
        /// <para>
        /// <b>A claim that something is there</b> does not survive a null at all. A <c>required</c>
        /// child, a <c>not</c>'s property, a geography: each is true of the object the branch
        /// describes and false of a null standing in its place, where nothing below is present. So
        /// those rules are kept under one more condition, that the union's value is not null.
        /// Everything else below is a claim about a value <em>if there is one</em>, which a null
        /// makes vacuous rather than false.
        /// </para>
        /// </remarks>
        static CosmosFactRule? AdmitNull(CosmosFactRule rule, CosmosDocumentPath path)
        {
            if (rule.Head.Claim is CosmosClaim.Present or CosmosClaim.Geography)
                return new CosmosFactRule(Extend(rule.Body, new CosmosFact(path, new CosmosClaim.NotEqualTo(null))), rule.Head);

            if (rule.Head.Path.Equals(path) == false)
                return rule;

            CosmosClaim? widened = rule.Head.Claim switch
            {
                CosmosClaim.OfType typed => typed with { OrNull = true },
                CosmosClaim.EqualTo { Value: null } equal => equal,
                CosmosClaim.EqualTo equal => new CosmosClaim.OneOf(new[] { equal.Value, null }),
                CosmosClaim.OneOf domain when CosmosClaim.OneOf.Contains(domain.Values, null) => domain,
                CosmosClaim.OneOf domain => new CosmosClaim.OneOf(new List<object?>(domain.Values) { null }),
                CosmosClaim.NotEqualTo { Value: null } => null,
                CosmosClaim.NotEqualTo unequal => unequal,
                CosmosClaim.Represents represents => represents,
                _ => null,
            };

            return widened is null ? null : new CosmosFactRule(rule.Body, new CosmosFact(path, widened));
        }

        /// <summary>
        /// Walks <c>if</c>/<c>then</c>/<c>else</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The <c>if</c> has to be understood whole.</b> Its constraints are a conjunction, so
        /// reading a subset of them yields a <em>weaker</em> condition — and a weaker guard would apply
        /// the <c>then</c> branch's facts to documents the schema never promised them for. That is the
        /// one place in this file where being incomplete would be unsound rather than merely lossy, so
        /// a condition carrying anything not understood contributes nothing at all.
        /// </para>
        /// <para>
        /// <b>The <c>else</c> needs a single condition.</b> Its guard is the negation of the
        /// <c>if</c>, and the negation of a conjunction is a disjunction, which has no conjunctive
        /// body. Where the condition is one equality the negation is one disequality and the branch is
        /// usable; otherwise it is dropped.
        /// </para>
        /// </remarks>
        static void WalkConditional(
            JsonNode node,
            CosmosDocumentPath path,
            IReadOnlyList<CosmosFact> guard,
            List<CosmosFactRule> rules,
            CosmosSchemaResolver resolver,
            HashSet<string> visiting)
        {
            if (node.get("if") is not JsonNode condition)
                return;

            if (TryConditionAtoms(condition, path, resolver, out var atoms) == false)
                return;

            if (node.get("then") is JsonNode then)
            {
                var extended = new List<CosmosFact>(guard);
                extended.AddRange(atoms);
                Walk(then, path, extended, rules, resolver, visiting);
            }

            if (node.get("else") is JsonNode otherwise && atoms.Count == 1 && atoms[0].Claim is CosmosClaim.EqualTo equality)
            {
                var extended = new List<CosmosFact>(guard) { new(atoms[0].Path, new CosmosClaim.NotEqualTo(equality.Value)) };
                Walk(otherwise, path, extended, rules, resolver, visiting);
            }
        }

        /// <summary>
        /// Reads the whole of an <c>if</c> condition as facts a query could establish, or fails.
        /// </summary>
        /// <remarks>
        /// Understood: <c>properties</c> whose subschemas carry only <c>const</c>, <c>enum</c> or
        /// <c>type</c>, and <c>required</c>. Anything else — a <c>pattern</c>, a numeric bound, a
        /// nested conditional — fails the whole condition, for the reason
        /// <see cref="WalkConditional"/> gives.
        /// </remarks>
        static bool TryConditionAtoms(JsonNode condition, CosmosDocumentPath path, CosmosSchemaResolver resolver, out IReadOnlyList<CosmosFact> atoms)
        {
            var found = new List<CosmosFact>();
            atoms = found;

            condition = resolver.Follow(condition) ?? condition;

            if (condition.isObject() == false)
                return false;

            var fields = condition.fields();
            while (fields.hasNext())
            {
                var field = (java.util.Map.Entry)fields.next();
                var keyword = field.getKey()?.ToString();
                var value = (JsonNode?)field.getValue();

                switch (keyword)
                {
                    case "properties" when value is not null && value.isObject():
                        var properties = value.fields();
                        while (properties.hasNext())
                        {
                            var property = (java.util.Map.Entry)properties.next();
                            if (property.getKey()?.ToString() is not string name || (JsonNode?)property.getValue() is not JsonNode subschema)
                                return false;

                            if (TryPropertyCondition(subschema, path.Property(name), resolver, found) == false)
                                return false;
                        }

                        break;

                    case "required" when value is not null && value.isArray():
                        for (var i = 0; i < value.size(); i++)
                            if (value.get(i)?.isTextual() == true)
                                found.Add(new CosmosFact(path.Property(value.get(i).asText()), new CosmosClaim.Present()));
                            else
                                return false;

                        break;

                    // Carried by every schema and constraining nothing.
                    case "$comment":
                    case "title":
                    case "description":
                        break;

                    default:
                        return false;
                }
            }

            return found.Count > 0;
        }

        /// <summary>
        /// Reads the whole of one property subschema inside a condition, or fails.
        /// </summary>
        /// <param name="subschema">The subschema constraining the property.</param>
        /// <param name="path">The path the property is at.</param>
        /// <param name="resolver">Resolves a reference standing in the way.</param>
        /// <param name="found">Collects the atoms read.</param>
        /// <returns><c>true</c> where every keyword was understood and at least one atom came of it.</returns>
        static bool TryPropertyCondition(JsonNode subschema, CosmosDocumentPath path, CosmosSchemaResolver resolver, List<CosmosFact> found)
        {
            subschema = resolver.Follow(subschema) ?? subschema;

            if (subschema.isObject() == false)
                return false;

            var before = found.Count;
            var fields = subschema.fields();

            while (fields.hasNext())
            {
                var field = (java.util.Map.Entry)fields.next();
                var keyword = field.getKey()?.ToString();
                var value = (JsonNode?)field.getValue();

                switch (keyword)
                {
                    case "const" when value is not null && TryLiteral(value, out var constant):
                        found.Add(new CosmosFact(path, new CosmosClaim.EqualTo(constant)));
                        break;

                    case "enum" when value is not null && ReadEnum(subschema) is IReadOnlyList<object?> domain:
                        found.Add(new CosmosFact(path, new CosmosClaim.OneOf(domain)));
                        break;

                    case "type" when ReadType(subschema) is var (type, orNull):
                        found.Add(new CosmosFact(path, new CosmosClaim.OfType(type, orNull)));
                        break;

                    case "$comment":
                    case "title":
                    case "description":
                        break;

                    default:
                        return false;
                }
            }

            return found.Count > before;
        }

        /// <summary>
        /// Returns the property that says which branch a document is in, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two ways a schema says it, and real documents use both. A branch may pin the property with
        /// a <c>const</c> of its own, which is plain JSON Schema and needs no OpenAPI. Or an OpenAPI
        /// <c>discriminator</c> names the property and a <c>mapping</c> says which value selects which
        /// branch — which is how a generated document writes it, the branches being bare <c>$ref</c>s
        /// that repeat nothing.
        /// </para>
        /// <para>
        /// <b>The second leans on the discriminator being a declaration.</b> Where a branch carries a
        /// <c>const</c>, proving the property refutes every other branch and <c>oneOf</c> does the rest.
        /// A mapping proves nothing that way — validation alone could not tell the branches apart — so
        /// what licenses it is that the author said so, which is the same footing the whole schema is
        /// trusted on.
        /// </para>
        /// <para>
        /// Either way no two branches may be selected by the same value: branches a discriminator
        /// cannot tell apart are not discriminated.
        /// </para>
        /// </remarks>
        static string? FindDiscriminator(JsonNode branches, JsonNode parent, CosmosSchemaResolver resolver)
        {
            if (parent.get("discriminator")?.get("propertyName") is JsonNode named && named.isTextual())
                return Pins(branches, parent, named.asText(), resolver) ? named.asText() : null;

            var first = resolver.Follow(branches.get(0));
            if (first?.get("properties") is not JsonNode properties || properties.isObject() == false)
                return null;

            var candidates = properties.fieldNames();
            while (candidates.hasNext())
                if (candidates.next()?.ToString() is string name && Pins(branches, parent, name, resolver))
                    return name;

            return null;
        }

        /// <summary>
        /// Determines whether one property selects a different branch for each of them.
        /// </summary>
        /// <param name="branches">The branches.</param>
        /// <param name="parent">The node carrying them, and any discriminator.</param>
        /// <param name="name">The candidate property.</param>
        /// <param name="resolver">Resolves a branch written as a reference.</param>
        /// <returns><c>true</c> where every branch is selected, and no two by the same value.</returns>
        static bool Pins(JsonNode branches, JsonNode parent, string name, CosmosSchemaResolver resolver)
        {
            var seen = new List<object?>();

            for (var i = 0; i < branches.size(); i++)
            {
                if (branches.get(i) is not JsonNode branch)
                    return false;

                if (DiscriminatorValue(branch, parent, name, resolver) is not object value || CosmosClaim.OneOf.Contains(seen, value))
                    return false;

                seen.Add(value);
            }

            return true;
        }

        /// <summary>
        /// Returns the value of <paramref name="name"/> that selects this branch, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// The branch's own <c>const</c> first, and an OpenAPI mapping second. A mapping entry names
        /// its target either as a reference or as the bare schema name, and both spellings are in use;
        /// where no entry names this branch, OpenAPI's implicit rule applies and the value is the
        /// schema's own name — the last segment of the reference.
        /// </remarks>
        static object? DiscriminatorValue(JsonNode branch, JsonNode parent, string name, CosmosSchemaResolver resolver)
        {
            if (resolver.Follow(branch)?.get("properties")?.get(name) is JsonNode declared &&
                resolver.Follow(declared)?.get("const") is JsonNode constant &&
                TryLiteral(constant, out var value) && value is not null)
                return value;

            if (parent.get("discriminator") is not JsonNode discriminator ||
                discriminator.get("propertyName")?.asText() != name ||
                Text(branch, "$ref") is not string reference)
                return null;

            if (discriminator.get("mapping") is JsonNode mapping && mapping.isObject())
            {
                var entries = mapping.fields();
                while (entries.hasNext())
                {
                    var entry = (java.util.Map.Entry)entries.next();
                    var target = ((JsonNode?)entry.getValue())?.asText();

                    if (target == reference || target == LastSegment(reference))
                        return entry.getKey()?.ToString();
                }
            }

            return LastSegment(reference);
        }

        /// <summary>
        /// The schema name a reference ends in, which OpenAPI uses when no mapping names a branch.
        /// </summary>
        /// <param name="reference">The reference.</param>
        /// <returns>The last segment, or the whole of it where there is no separator.</returns>
        static string LastSegment(string reference)
        {
            var slash = reference.LastIndexOf('/');
            return slash < 0 ? reference : reference.Substring(slash + 1);
        }

        /// <summary>
        /// Reads a <c>type</c>, and whether a null is admitted beside it.
        /// </summary>
        /// <param name="node">The schema node.</param>
        /// <returns>The type and whether a null is admitted, or <c>null</c> where none is stated or two types are.</returns>
        static (CosmosJsonType Type, bool OrNull)? ReadType(JsonNode node)
        {
            // OpenAPI 3.0 writes nullability beside the type; 2020-12 writes it inside, as a union with
            // "null". Both are the same statement and neither is an absence of type -- a nullable
            // string is still a string wherever it is not null, which is what a stored form is about
            // and what every comparison here decides on.
            var orNull = node.get("nullable") is JsonNode nullable && nullable.isBoolean() && nullable.asBoolean();
            var type = node.get("type");

            if (type is not null && type.isArray())
            {
                string? single = null;

                for (var i = 0; i < type.size(); i++)
                {
                    var name = type.get(i)?.asText();

                    if (name == "null")
                    {
                        orNull = true;
                        continue;
                    }

                    // Two types beside each other state only what both agree on, which is nothing.
                    if (single is not null)
                        return null;

                    single = name;
                }

                return single is null ? null : Parse(single) is CosmosJsonType parsed ? (parsed, orNull) : null;
            }

            if (type is null || type.isTextual() == false)
                return null;

            return Parse(type.asText()) is CosmosJsonType only ? (only, orNull) : null;
        }

        static CosmosJsonType? Parse(string name) => name switch
        {
            "string" => CosmosJsonType.String,
            "integer" => CosmosJsonType.Integer,
            "number" => CosmosJsonType.Number,
            "boolean" => CosmosJsonType.Boolean,
            "object" => CosmosJsonType.Object,
            "array" => CosmosJsonType.Array,
            "null" => CosmosJsonType.Null,
            _ => null,
        };

        /// <summary>
        /// Reads an <c>enum</c> as a domain of scalars.
        /// </summary>
        /// <remarks>
        /// All or nothing: a domain carrying an object or an array is one nothing here can compare
        /// against, and half of it would be a claim the schema did not make.
        /// </remarks>
        /// <param name="node">The schema node.</param>
        /// <returns>The domain, or <c>null</c>.</returns>
        static IReadOnlyList<object?>? ReadEnum(JsonNode node)
        {
            if (node.get("enum") is not JsonNode domain || domain.isArray() == false || domain.size() == 0)
                return null;

            var values = new List<object?>(domain.size());

            for (var i = 0; i < domain.size(); i++)
            {
                if (TryLiteral(domain.get(i), out var value) == false)
                    return null;

                values.Add(value);
            }

            return values;
        }

        /// <summary>
        /// Reads a keyword whose value is a string.
        /// </summary>
        /// <param name="node">The schema node.</param>
        /// <param name="keyword">The keyword.</param>
        /// <returns>The text, or <c>null</c> where the keyword is absent or is not a string.</returns>
        static string? Text(JsonNode node, string keyword) =>
            node.get(keyword) is JsonNode value && value.isTextual() ? value.asText() : null;

        /// <summary>
        /// Reads a JSON literal as the CLR value a predicate would compare against.
        /// </summary>
        static bool TryLiteral(JsonNode? node, out object? value)
        {
            value = null;

            if (node is null)
                return false;

            if (node.isNull())
                return true;
            if (node.isTextual())
                return Assign(node.asText(), out value);
            if (node.isBoolean())
                return Assign(node.asBoolean(), out value);
            if (node.isIntegralNumber())
                return Assign(node.asLong(), out value);
            if (node.isNumber())
                return Assign(node.asDouble(), out value);

            return false;

            static bool Assign(object assigned, out object? value)
            {
                value = assigned;
                return true;
            }
        }

    }

}
