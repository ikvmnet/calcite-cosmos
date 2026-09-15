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
    /// See <c>DESIGN-93.md</c> §3 for the keyword table and the reasoning behind each entry.
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

            // required names the children that are always there. The claim is about the child, not
            // about this node.
            if (node.get("required") is JsonNode required && required.isArray())
                for (var i = 0; i < required.size(); i++)
                    if (required.get(i)?.isTextual() == true)
                        rules.Add(new CosmosFactRule(guard, new CosmosFact(path.Property(required.get(i).asText()), new CosmosClaim.Present())));

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
                    var branch = resolver.Follow(branches.get(i));
                    if (branch is null || DiscriminatorValue(branch, discriminator, resolver) is not object value)
                        continue;

                    var extended = new List<CosmosFact>(guard) { new(path.Property(discriminator), new CosmosClaim.EqualTo(value)) };
                    Walk(branches.get(i), path, extended, rules, resolver, visiting);
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
        /// Returns the property every branch pins to a different constant, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// OpenAPI's <c>discriminator.propertyName</c> is honoured where it is given, and otherwise the
        /// property is inferred: every branch must pin the same single property, and no two branches
        /// may pin it to the same value — two branches a discriminator cannot tell apart is not a
        /// discriminator.
        /// </remarks>
        static string? FindDiscriminator(JsonNode branches, JsonNode parent, CosmosSchemaResolver resolver)
        {
            if (parent.get("discriminator")?.get("propertyName") is JsonNode named && named.isTextual())
                return Pins(branches, named.asText(), resolver) ? named.asText() : null;

            var first = resolver.Follow(branches.get(0));
            if (first?.get("properties") is not JsonNode properties || properties.isObject() == false)
                return null;

            var candidates = properties.fieldNames();
            while (candidates.hasNext())
                if (candidates.next()?.ToString() is string name && Pins(branches, name, resolver))
                    return name;

            return null;
        }

        static bool Pins(JsonNode branches, string name, CosmosSchemaResolver resolver)
        {
            var seen = new List<object?>();

            for (var i = 0; i < branches.size(); i++)
            {
                if (resolver.Follow(branches.get(i)) is not JsonNode branch)
                    return false;

                if (DiscriminatorValue(branch, name, resolver) is not object value || CosmosClaim.OneOf.Contains(seen, value))
                    return false;

                seen.Add(value);
            }

            return true;
        }

        static object? DiscriminatorValue(JsonNode branch, string name, CosmosSchemaResolver resolver)
        {
            var subschema = resolver.Follow(branch.get("properties")?.get(name));

            if (subschema?.get("const") is JsonNode constant && TryLiteral(constant, out var value) && value is not null)
                return value;

            return null;
        }

        static CosmosJsonType? ReadType(JsonNode node)
        {
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
