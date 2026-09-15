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
            // recorded, and going round again would not add one.
            if (Text(node, "$ref") is string reference)
            {
                if (visiting.Add(reference) == false)
                    return;

                Walk(resolver.Resolve(reference), path, guard, rules, resolver, visiting);
                visiting.Remove(reference);
                return;
            }

            void State(CosmosClaim claim) => rules.Add(new CosmosFactRule(guard, new CosmosFact(path, claim)));

            if (ReadType(node) is CosmosJsonType type)
                State(new CosmosClaim.OfType(type));

            if (node.get("const") is JsonNode constant && TryLiteral(constant, out var constantValue))
                State(new CosmosClaim.EqualTo(constantValue));

            if (ReadEnum(node) is IReadOnlyList<object?> domain)
                State(new CosmosClaim.OneOf(domain));

            if (CosmosStoredForms.Recognise(Text(node, "pattern")) is CosmosRepresentation representation)
                State(new CosmosClaim.Represents(representation));

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
                for (var i = 0; i < branches.size(); i++)
                {
                    // The branch as written, not as resolved: a mapping names its target by the
                    // reference, which following it would have thrown away.
                    if (branches.get(i) is not JsonNode branch || DiscriminatorValue(branch, parent, discriminator, resolver) is not object value)
                        continue;

                    Walk(branches.get(i), path, Extend(guard, new CosmosFact(path.Property(discriminator), new CosmosClaim.EqualTo(value))), rules, resolver, visiting);
                }

                return;
            }

            List<CosmosFactRule>? meet = null;

            for (var i = 0; i < branches.size(); i++)
            {
                var stated = new List<CosmosFactRule>();
                Walk(branches.get(i), path, guard, stated, resolver, visiting);

                if (meet is null)
                {
                    meet = stated;
                    continue;
                }

                meet.RemoveAll(rule => stated.Exists(other => other.Head.Equals(rule.Head)) == false);
            }

            if (meet is not null)
                rules.AddRange(meet);
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

                    case "type" when ReadType(subschema) is CosmosJsonType type:
                        found.Add(new CosmosFact(path, new CosmosClaim.OfType(type)));
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

        static string LastSegment(string reference)
        {
            var slash = reference.LastIndexOf('/');
            return slash < 0 ? reference : reference.Substring(slash + 1);
        }

        static CosmosJsonType? ReadType(JsonNode node)
        {
            // OpenAPI 3.0 says a value may be null with a keyword beside the type rather than inside
            // it, so `{"type": "string", "nullable": true}` describes a path a stored null conforms
            // at. Claiming the type there would be claiming what the schema declined to.
            if (node.get("nullable") is JsonNode nullable && nullable.isBoolean() && nullable.asBoolean())
                return null;

            var type = node.get("type");

            // A union type states only what its members agree on, which is nothing. The idiomatic
            // ["t", "null"] is the one that looks like an exception and is not: a document storing a
            // JSON null conforms to it and is not of type t, so claiming t would be a fact the schema
            // never stated. Nullability wants a claim of its own before this can say anything.
            if (type is not null && type.isArray())
                return null;

            return type is not null && type.isTextual() ? Parse(type.asText()) : null;
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
