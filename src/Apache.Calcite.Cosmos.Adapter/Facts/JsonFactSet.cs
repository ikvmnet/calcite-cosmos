using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// What is known about a container's documents, for one query.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A context of the theory, read.</b> The container's declaration is a frozen context, and what a query
    /// established is a context beneath it, saturated under the rules of JSON and of JSON Schema. Asking a question is a
    /// read of its tables — a presence, a set of types, a domain, a form — so an answer is whatever the rules proved, and
    /// not only what one fact entails of another. Where the engine could prove nothing more than a query states, the
    /// tables are read off the query instead (<see cref="JsonFactOverlay"/>), and asked the same way.
    /// </para>
    /// <para>
    /// <b>A fact about a value holds if the value is there.</b> Every claim but <see cref="JsonClaim.Present"/> and a
    /// presence-entailing extension says what a path holds <em>if it holds anything</em>, and is answered that way: a path
    /// known to be absent satisfies every such claim, as it satisfies every JSON Schema keyword.
    /// </para>
    /// <para>
    /// <b>Thread-safe.</b> A frozen context is read freely; a query's context is read one call at a time. Its context is
    /// released once nothing holds the set.
    /// </para>
    /// </remarks>
    public sealed class JsonFactSet
    {

        /// <summary>
        /// What is known when nothing is declared and nothing was established.
        /// </summary>
        public static readonly JsonFactSet Empty = new(null, null);

        readonly JsonFactTables? _tables;
        readonly JsonFactVocabulary? _vocabulary;

        /// <summary>
        /// Initializes a new instance reading a context of the theory.
        /// </summary>
        /// <param name="rules">The tables of the graph the context belongs to.</param>
        /// <param name="vocabulary">The tokens of the theory's stored forms and extensions.</param>
        /// <param name="scope">The context.</param>
        /// <param name="owned">Whether the set owns the context, which it releases once it is collected: a query's.</param>
        internal JsonFactSet(JsonRules rules, JsonFactVocabulary vocabulary, Scope scope, bool owned) :
            this(new JsonScopeTables(rules, scope, owned), vocabulary)
        {

        }

        /// <summary>
        /// Initializes a new instance reading the given tables.
        /// </summary>
        /// <param name="tables">What is known, or <c>null</c> where nothing is.</param>
        /// <param name="vocabulary">The tokens of the theory's stored forms and extensions.</param>
        internal JsonFactSet(JsonFactTables? tables, JsonFactVocabulary? vocabulary)
        {
            _tables = tables;
            _vocabulary = vocabulary;
        }

        /// <summary>
        /// Gets the tables the set reads, or <c>null</c> where nothing is known.
        /// </summary>
        internal JsonFactTables? Tables => _tables;

        /// <summary>
        /// Gets the tokens of the theory's stored forms and extensions.
        /// </summary>
        internal JsonFactVocabulary? Vocabulary => _vocabulary;

        /// <summary>
        /// Determines whether a fact holds.
        /// </summary>
        /// <remarks>
        /// Through what the context proved, so a known value answers yes to its type and to every disequality but its own,
        /// a known domain to a wider one, and a required property to its presence.
        /// </remarks>
        /// <param name="fact">The fact to test.</param>
        /// <returns><c>true</c> if it holds.</returns>
        public bool Knows(JsonFact fact) => Read(false, t =>
        {
            var p = fact.Path;

            switch (fact.Claim)
            {
                case JsonClaim.Present:
                    return t.Present(p);

                case JsonClaim.OfType typed:
                    return t.TryTypes(p, out var types) && types.IsSubsetOf(JsonFactEncoding.Types(typed.Type, typed.OrNull));

                case JsonClaim.EqualTo equal:
                    return t.TryDom(p, out var pinned) && pinned.IsSubsetOf(ValueSet.Of(JsonFactEncoding.Value(equal.Value)));

                case JsonClaim.OneOf domain:
                    return t.TryDom(p, out var within) && within.IsSubsetOf(JsonFactEncoding.Domain(domain.Values));

                case JsonClaim.NotEqualTo unequal:
                {
                    var value = JsonFactEncoding.Value(unequal.Value);
                    return t.Ne(p, value)
                        || t.TryDom(p, out var excluding) && excluding.Contains(value) == false
                        || t.TryTypes(p, out var others) && others.Contains(TypeOf(value)) == false;
                }

                case JsonClaim.LowerBound lower:
                    return NoNumber(t, p)
                        || t.TryLo(p, out var lo) && Bound.StricterLower(lo, new Bound(lower.Value, lower.Exclusive)) == lo
                        || t.TryDom(p, out var above) && above.Count > 0 && above.All(v => TryNumberOf(v, out var x) ? lower.Admits(x) : IsNumber(v) == false);

                case JsonClaim.UpperBound upper:
                    return NoNumber(t, p)
                        || t.TryHi(p, out var hi) && Bound.StricterUpper(hi, new Bound(upper.Value, upper.Exclusive)) == hi
                        || t.TryDom(p, out var below) && below.Count > 0 && below.All(v => TryNumberOf(v, out var x) ? upper.Admits(x) : IsNumber(v) == false);

                case JsonClaim.Represents represents:
                    return _vocabulary!.TryForm(represents.Form, out var form) && t.Form(p, form);

                case JsonClaim.Characters characters:
                    return t.TryChars(p, out var held) && held.IsSubsetOf(JsonFactEncoding.Members(characters.Bands));

                case JsonClaim.Extension extension:
                    return _vocabulary!.TryExtension(extension, out var key) && t.Extension(p, key);

                default:
                    return false;
            }
        });

        /// <summary>
        /// Returns the bands of UTF-16 units the strings at a path may hold beside those below U+D800:
        /// <see cref="JsonCharacterBand.Any"/> where nothing is known.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A fact of the theory, read as a type is.</b> What the context proved of the path's <c>chars</c>: a declared
        /// <c>pattern</c> and a service fact state it, a value or a domain a declaration or a filter names derives it, and
        /// so does a stored form, each by a rule of the theory and nowhere else; met over a conjunction and joined over the
        /// cases a disjunction or a discriminator splits into. So <c>WHERE s IN ('a', 'b')</c> says its characters here
        /// in the stratum it holds in, as a pattern does in every one.
        /// </para>
        /// <para>
        /// Silent about whether a string is there: a path holding none satisfies any answer, and a consumer asks it only
        /// of a path that may hold one.
        /// </para>
        /// </remarks>
        /// <param name="path">The path.</param>
        /// <returns>The bands.</returns>
        public JsonCharacterBand CharactersOf(JsonDocumentPath path) =>
            Read(JsonCharacterBand.Any, t => t.TryChars(path, out var bands) ? JsonFactEncoding.Bands(bands) : JsonCharacterBand.Any);

        /// <summary>
        /// Returns every stored form known for a path, which may be none.
        /// </summary>
        /// <remarks>
        /// All of them, in no order that means anything. Which a caller can use, and which it prefers
        /// where a path somehow carries two, depends on what the form licenses for that caller — which
        /// the form's owner knows and this does not. See <see cref="IJsonStoredForm"/>.
        /// </remarks>
        /// <param name="path">The path.</param>
        /// <returns>The forms, which may be empty.</returns>
        public IReadOnlyList<IJsonStoredForm> FormsOf(JsonDocumentPath path) => Read<IReadOnlyList<IJsonStoredForm>>(Array.Empty<IJsonStoredForm>(), t =>
        {
            if (_vocabulary!.Forms.Count == 0)
                return Array.Empty<IJsonStoredForm>();

            var forms = new List<IJsonStoredForm>();
            foreach (var (form, token) in _vocabulary.Forms)
                if (t.Form(path, token))
                    forms.Add(form);

            return forms;
        });

        /// <summary>
        /// Gets whether what is known cannot all hold of one document.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Which means the predicate keeps nothing.</b> This set is what the container declares
        /// closed under what the query's own conjuncts established, so a contradiction in it is a
        /// contradiction between the two: a document satisfying the predicate would have to hold a
        /// value the declaration says it does not. There is no such document, and the plan need not
        /// ask the service for one.
        /// </para>
        /// <para>
        /// <b>Absence is not a contradiction.</b> Two claims that no one value satisfies — a string and an object, an
        /// enum and a value outside it — prove that the path is absent, which a document satisfies by not having it. Only
        /// beside a presence do they contradict, and a comparison the query keeps a row for is such a presence: see
        /// <c>PredicateAtom.Kept</c>.
        /// </para>
        /// <para>
        /// <b>A guarded declaration reaches the same answer by the argument it always uses.</b> Over
        /// a document the guard does not cover, the conjunct that proved the guard has already
        /// excluded the row; over one it does, the declared claim holds and contradicts. Either way
        /// nothing survives.
        /// </para>
        /// </remarks>
        public bool IsContradictory => Read(false, t => t.IsContradictory);

        /// <summary>
        /// Returns what is known about a path as claims, for diagnostics.
        /// </summary>
        /// <remarks>
        /// The presence, the narrowest type claim that states the known types, the domain, the bounds, the forms, the
        /// characters and the extensions. A disequality is not listed: the set of them is not enumerable from one path.
        /// </remarks>
        /// <param name="path">The path.</param>
        /// <returns>The claims, which may be empty.</returns>
        public IReadOnlyList<JsonClaim> ClaimsFor(JsonDocumentPath path) => Read<IReadOnlyList<JsonClaim>>(Array.Empty<JsonClaim>(), t =>
        {
            var claims = new List<JsonClaim>();

            if (t.Present(path))
                claims.Add(new JsonClaim.Present());

            if (t.TryTypes(path, out var types) && TypeClaim(types) is JsonClaim.OfType typed)
                claims.Add(typed);

            if (t.TryDom(path, out var dom) && Decode(dom) is { } values)
                claims.Add(values.Count == 1 ? new JsonClaim.EqualTo(values[0]) : new JsonClaim.OneOf(values));

            if (t.TryLo(path, out var lo))
                claims.Add(new JsonClaim.LowerBound(lo.Value, lo.Open));

            if (t.TryHi(path, out var hi))
                claims.Add(new JsonClaim.UpperBound(hi.Value, hi.Open));

            foreach (var (form, token) in _vocabulary!.Forms)
                if (t.Form(path, token))
                    claims.Add(new JsonClaim.Represents(form));

            if (t.TryChars(path, out var bands) && JsonFactEncoding.Bands(bands) is var held && held != JsonCharacterBand.Any)
                claims.Add(new JsonClaim.Characters(held));

            foreach (var (extension, key) in _vocabulary.Extensions)
                if (t.Extension(path, key))
                    claims.Add(extension);

            return claims;
        });

        /// <summary>
        /// Determines whether every value a path holds is a scalar of one kind, or null.
        /// </summary>
        /// <remarks>
        /// What makes the text of a path a faithful reading of its value: two values of one kind with one spelling are one
        /// value, where a string <c>"1"</c> and a number <c>1</c> read alike. Strings, numbers or booleans, and a null
        /// beside them, which reads as no text at all. Silent about whether the path is there.
        /// </remarks>
        /// <param name="path">The path.</param>
        /// <returns><c>true</c> where the path holds scalars of one kind.</returns>
        public bool HasOneScalarType(JsonDocumentPath path) => Read(false, t =>
            t.TryTypes(path, out var types) && IsOneScalarKind(types));

        /// <summary>
        /// Returns the one value a path holds wherever it is there, if one is known.
        /// </summary>
        /// <remarks>
        /// Silent about whether the path is there: a declared constant constrains the value only where there is one. A
        /// caller that needs it there asks <see cref="Knows"/> for <see cref="JsonClaim.Present"/> too.
        /// </remarks>
        /// <param name="path">The path.</param>
        /// <param name="value">The value.</param>
        /// <returns><c>true</c> where exactly one value is possible.</returns>
        public bool TryGetValue(JsonDocumentPath path, out JsonScalar value)
        {
            // A tuple rather than a nullable scalar: a scalar converts from a null string, so a conditional between one
            // and null types as a scalar, and answers the JSON null where it meant no answer.
            var (found, only) = Read((false, JsonScalar.Null), t =>
                t.TryDom(path, out var dom) && dom.Count == 1 && JsonFactEncoding.TryScalar(dom[0], out var one)
                    ? (true, one)
                    : (false, JsonScalar.Null));

            value = only;
            return found;
        }

        /// <summary>
        /// Returns the values a path holds wherever it is there, where they are finitely many and known.
        /// </summary>
        /// <remarks>
        /// Silent about whether the path is there, as <see cref="TryGetValue"/> is: an <c>enum</c> or an <c>IN</c>
        /// constrains the value only where there is one. What an order over the path needs of it is the characters and
        /// the types of what may be there, and that a path holding one value is a key nothing orders by — see
        /// <c>CosmosServiceOrder</c>.
        /// </remarks>
        /// <param name="path">The path.</param>
        /// <param name="values">The values, in no order that means anything.</param>
        /// <returns><c>true</c> where the values are known.</returns>
        public bool TryGetDomain(JsonDocumentPath path, out IReadOnlyList<JsonScalar> values)
        {
            var known = Read<IReadOnlyList<JsonScalar>?>(null, t => t.TryDom(path, out var dom) ? Decode(dom) : null);

            values = known ?? Array.Empty<JsonScalar>();
            return known is not null;
        }

        /// <summary>
        /// Returns the types a path holds wherever it is there, in the theory's names, where they are known.
        /// </summary>
        /// <remarks>
        /// Silent about whether the path is there; an empty set says it never is.
        /// </remarks>
        /// <param name="path">The path.</param>
        /// <param name="types">The types: <see cref="JsonValues.StringType"/>, <see cref="JsonValues.IntegerType"/> and
        /// the rest.</param>
        /// <returns><c>true</c> where the types are known.</returns>
        internal bool TryGetTypes(JsonDocumentPath path, out ValueSet<string> types)
        {
            var (found, known) = Read((false, AllTypes), t => t.TryTypes(path, out var held) ? (true, held) : (false, AllTypes));

            types = known;
            return found;
        }

        /// <summary>
        /// Reads the tables, or answers <paramref name="otherwise"/> where nothing is known.
        /// </summary>
        T Read<T>(T otherwise, Func<JsonFactTables, T> read) => _tables is null ? otherwise : read(_tables);

        /// <summary>
        /// Determines whether a path holds no number, which satisfies every bound.
        /// </summary>
        static bool NoNumber(JsonFactTables tables, JsonDocumentPath path) =>
            tables.TryTypes(path, out var types) && types.Contains(IntegerType) == false && types.Contains(FractionType) == false;

        /// <summary>
        /// Determines whether a set of types, less a null, is one scalar kind: strings, numbers, or booleans.
        /// </summary>
        static bool IsOneScalarKind(ValueSet<string> types)
        {
            string? kind = null;

            foreach (var type in types)
            {
                if (type == NullType)
                    continue;

                var of = type is IntegerType or FractionType ? "number" : type;
                if (of is not (StringType or "number" or BooleanType))
                    return false;

                if (kind is not null && kind != of)
                    return false;

                kind = of;
            }

            return kind is not null;
        }

        /// <summary>
        /// Returns the narrowest type claim a set of types satisfies, or <c>null</c> where none states it.
        /// </summary>
        static JsonClaim.OfType? TypeClaim(ValueSet<string> types)
        {
            var orNull = types.Contains(NullType);
            var rest = new HashSet<string>(types);
            rest.Remove(NullType);

            if (rest.Count == 0)
                return orNull ? new JsonClaim.OfType(JsonType.Null) : null;

            JsonType? type = rest.Count switch
            {
                1 when rest.Contains(StringType) => JsonType.String,
                1 when rest.Contains(IntegerType) => JsonType.Integer,
                1 when rest.Contains(FractionType) => JsonType.Number,
                1 when rest.Contains(BooleanType) => JsonType.Boolean,
                1 when rest.Contains(ObjectType) => JsonType.Object,
                1 when rest.Contains(ArrayType) => JsonType.Array,
                2 when rest.Contains(IntegerType) && rest.Contains(FractionType) => JsonType.Number,
                _ => null,
            };

            return type is JsonType t ? new JsonClaim.OfType(t, orNull) : null;
        }

        static List<JsonScalar>? Decode(ValueSet<string> dom)
        {
            var values = new List<JsonScalar>(dom.Count);
            foreach (var member in dom)
            {
                if (JsonFactEncoding.TryScalar(member, out var value) == false)
                    return null;

                values.Add(value);
            }

            return values;
        }

    }

}
