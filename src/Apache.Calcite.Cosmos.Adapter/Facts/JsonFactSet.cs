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
    /// not only what one fact entails of another.
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
        public static readonly JsonFactSet Empty = new(null, null, null, null);

        readonly JsonRules? _rules;
        readonly JsonFactVocabulary? _vocabulary;
        readonly Scope? _scope;
        readonly Lease? _lease;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="rules">The tables of the graph the context belongs to.</param>
        /// <param name="vocabulary">The tokens of the theory's stored forms and extensions.</param>
        /// <param name="scope">The context.</param>
        /// <param name="owned">Whether the set owns the context, which it releases once it is collected: a query's.</param>
        internal JsonFactSet(JsonRules? rules, JsonFactVocabulary? vocabulary, Scope? scope, bool owned) :
            this(rules, vocabulary, scope, owned && scope is not null ? new Lease(scope) : null)
        {

        }

        JsonFactSet(JsonRules? rules, JsonFactVocabulary? vocabulary, Scope? scope, Lease? lease)
        {
            _rules = rules;
            _vocabulary = vocabulary;
            _scope = scope;
            _lease = lease;
        }

        /// <summary>
        /// Determines whether a fact holds.
        /// </summary>
        /// <remarks>
        /// Through what the context proved, so a known value answers yes to its type and to every disequality but its own,
        /// a known domain to a wider one, and a required property to its presence.
        /// </remarks>
        /// <param name="fact">The fact to test.</param>
        /// <returns><c>true</c> if it holds.</returns>
        public bool Knows(JsonFact fact) => Read(false, (scope, r) =>
        {
            var p = Term(fact.Path, r);

            switch (fact.Claim)
            {
                case JsonClaim.Present:
                    return scope.Check(r.Present.Of(p));

                case JsonClaim.OfType typed:
                    return scope.TryGet(r.Types.Of(p), out var types) && types.IsSubsetOf(JsonFactEncoding.Types(typed.Type, typed.OrNull));

                case JsonClaim.EqualTo equal:
                    return scope.TryGet(r.Dom.Of(p), out var pinned) && pinned.IsSubsetOf(ValueSet.Of(JsonFactEncoding.Value(equal.Value)));

                case JsonClaim.OneOf domain:
                    return scope.TryGet(r.Dom.Of(p), out var within) && within.IsSubsetOf(JsonFactEncoding.Domain(domain.Values));

                case JsonClaim.NotEqualTo unequal:
                {
                    var value = JsonFactEncoding.Value(unequal.Value);
                    return scope.Check(r.Ne.Of(p, value))
                        || scope.TryGet(r.Dom.Of(p), out var excluding) && excluding.Contains(value) == false
                        || scope.TryGet(r.Types.Of(p), out var others) && others.Contains(TypeOf(value)) == false;
                }

                case JsonClaim.LowerBound lower:
                    return NoNumber(scope, r, p)
                        || scope.TryGet(r.Lo.Of(p), out var lo) && Bound.StricterLower(lo, new Bound(lower.Value, lower.Exclusive)) == lo
                        || scope.TryGet(r.Dom.Of(p), out var above) && above.Count > 0 && above.All(v => TryNumberOf(v, out var x) ? lower.Admits(x) : IsNumber(v) == false);

                case JsonClaim.UpperBound upper:
                    return NoNumber(scope, r, p)
                        || scope.TryGet(r.Hi.Of(p), out var hi) && Bound.StricterUpper(hi, new Bound(upper.Value, upper.Exclusive)) == hi
                        || scope.TryGet(r.Dom.Of(p), out var below) && below.Count > 0 && below.All(v => TryNumberOf(v, out var x) ? upper.Admits(x) : IsNumber(v) == false);

                case JsonClaim.Represents represents:
                    return _vocabulary!.TryForm(represents.Form, out var form) && scope.Check(r.Form.Of(p, form));

                case JsonClaim.Extension extension:
                    return _vocabulary!.TryExtension(extension, out var key) && scope.Check(r.Extension.Of(p, key));

                default:
                    return false;
            }
        });

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
        public IReadOnlyList<IJsonStoredForm> FormsOf(JsonDocumentPath path) => Read<IReadOnlyList<IJsonStoredForm>>(Array.Empty<IJsonStoredForm>(), (scope, r) =>
        {
            if (_vocabulary!.Forms.Count == 0)
                return Array.Empty<IJsonStoredForm>();

            var p = Term(path, r);
            var forms = new List<IJsonStoredForm>();
            foreach (var (form, token) in _vocabulary.Forms)
                if (scope.Check(r.Form.Of(p, token)))
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
        /// <c>CosmosFactExtractor</c>.
        /// </para>
        /// <para>
        /// <b>A guarded declaration reaches the same answer by the argument it always uses.</b> Over
        /// a document the guard does not cover, the conjunct that proved the guard has already
        /// excluded the row; over one it does, the declared claim holds and contradicts. Either way
        /// nothing survives.
        /// </para>
        /// </remarks>
        public bool IsContradictory => Read(false, (scope, _) => scope.IsContradictory);

        /// <summary>
        /// Returns what is known about a path as claims, for diagnostics.
        /// </summary>
        /// <remarks>
        /// The presence, the narrowest type claim that states the known types, the domain, and the forms and extensions.
        /// A disequality is not listed: the set of them is not enumerable from one path.
        /// </remarks>
        /// <param name="path">The path.</param>
        /// <returns>The claims, which may be empty.</returns>
        public IReadOnlyList<JsonClaim> ClaimsFor(JsonDocumentPath path) => Read<IReadOnlyList<JsonClaim>>(Array.Empty<JsonClaim>(), (scope, r) =>
        {
            var p = Term(path, r);
            var claims = new List<JsonClaim>();

            if (scope.Check(r.Present.Of(p)))
                claims.Add(new JsonClaim.Present());

            if (scope.TryGet(r.Types.Of(p), out var types) && TypeClaim(types) is JsonClaim.OfType typed)
                claims.Add(typed);

            if (scope.TryGet(r.Dom.Of(p), out var dom) && Decode(dom) is { } values)
                claims.Add(values.Count == 1 ? new JsonClaim.EqualTo(values[0]) : new JsonClaim.OneOf(values));

            if (scope.TryGet(r.Lo.Of(p), out var lo))
                claims.Add(new JsonClaim.LowerBound(lo.Value, lo.Open));

            if (scope.TryGet(r.Hi.Of(p), out var hi))
                claims.Add(new JsonClaim.UpperBound(hi.Value, hi.Open));

            foreach (var (form, token) in _vocabulary!.Forms)
                if (scope.Check(r.Form.Of(p, token)))
                    claims.Add(new JsonClaim.Represents(form));

            foreach (var (extension, key) in _vocabulary.Extensions)
                if (scope.Check(r.Extension.Of(p, key)))
                    claims.Add(extension);

            return claims;
        });

        /// <summary>
        /// Determines whether a path is guaranteed to hold a scalar of a known type in every document.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Two claims, and both are needed.</b> The path has to be <em>there</em>, since an accessor
        /// answers null over an absent one; and it has to hold a scalar of a known type, since the
        /// accessor answers null for a JSON null and for an object or an array alike — neither being a
        /// scalar, which is SQL/JSON's own line. A type admitting null is not enough, for the same
        /// reason the presence is not.
        /// </para>
        /// <para>
        /// <b>One type, too.</b> A string at one document and a number at another are both scalars, and the service orders
        /// them by type before value, where the accessor's text orders them as text. So the types are those of one scalar
        /// kind — strings, numbers or booleans — as a single declared type always said.
        /// </para>
        /// <para>
        /// <b>Asked by two callers for two reasons that turn out to be one.</b> A sort wants it for the
        /// null placement — Cosmos orders nulls first ascending where Calcite's default is last, so a
        /// key that cannot be null leaves the two nothing to disagree about. A projection that renders
        /// a guarded accessor wants it to know the guard is vacuous, so that ordering by the raw path
        /// orders the rows the way ordering by the rendered column would. Both are the statement that
        /// no document makes the accessor answer something other than the value.
        /// </para>
        /// </remarks>
        /// <param name="path">The path.</param>
        /// <returns><c>true</c> where every document holds a scalar of a known type there.</returns>
        public bool IsAlwaysScalar(JsonDocumentPath path) => Read(false, (scope, r) =>
        {
            var p = Term(path, r);
            return scope.Check(r.Present.Of(p))
                && scope.TryGet(r.Types.Of(p), out var types)
                && types.Contains(NullType) == false
                && IsOneScalarKind(types);
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
        public bool HasOneScalarType(JsonDocumentPath path) => Read(false, (scope, r) =>
            scope.TryGet(r.Types.Of(Term(path, r)), out var types) && IsOneScalarKind(types));

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
            var (found, only) = Read((false, JsonScalar.Null), (scope, r) =>
                scope.TryGet(r.Dom.Of(Term(path, r)), out var dom) && dom.Count == 1 && JsonFactEncoding.TryScalar(dom[0], out var one)
                    ? (true, one)
                    : (false, JsonScalar.Null));

            value = only;
            return found;
        }

        /// <summary>
        /// Reads the context, one call at a time where it is not frozen; or answers <paramref name="otherwise"/> where
        /// there is none, or where the engine fails, which loses a fact and invents none.
        /// </summary>
        T Read<T>(T otherwise, Func<Scope, JsonRules, T> read)
        {
            if (_scope is null)
                return otherwise;

            try
            {
                if (_lease is null)
                    return read(_scope, _rules!);

                lock (_lease)
                    return read(_scope, _rules!);
            }
            catch (Exception e) when (JsonFactTheory.IsEngineFailure(e))
            {
                return otherwise;
            }
            finally
            {
                // The lease releases the context once the set is collected, and the set may be collectable before a read
                // of its context returns.
                GC.KeepAlive(_lease);
            }
        }

        static Term<JPath> Term(JsonDocumentPath path, JsonRules rules) => JsonFactEncoding.Path(path).ToTerm(rules);

        /// <summary>
        /// Determines whether a path holds no number, which satisfies every bound.
        /// </summary>
        static bool NoNumber(Scope scope, JsonRules r, Term<JPath> p) =>
            scope.TryGet(r.Types.Of(p), out var types) && types.Contains(IntegerType) == false && types.Contains(FractionType) == false;

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

        /// <summary>
        /// Holds a query's context and releases it once nothing holds the set, so that a context the memo let go of does
        /// not stay listed beneath its container's for the life of the generation.
        /// </summary>
        /// <remarks>
        /// A finalizer, because nothing calls into a set to say it is done with it: a set is handed to a translator, kept
        /// by a rule for the length of a match, and dropped. Releasing a context never throws and may run on any thread.
        /// </remarks>
        sealed class Lease
        {

            readonly Scope _scope;

            public Lease(Scope scope)
            {
                _scope = scope;
            }

            ~Lease()
            {
                _scope.Dispose();
            }

        }

    }

}
