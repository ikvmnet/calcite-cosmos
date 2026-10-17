using System;
using System.Collections.Generic;
using System.Globalization;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// The adapter's claims written in the theory's terms, and the theory's answers read back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A claim says what a path holds if it holds anything</b>, and so does every fact of the theory but
    /// <c>present</c> and <c>absent</c>: the two vocabularies agree on that, which is what makes the
    /// translation one table rather than an argument. A type is a set of JSON Schema's members, <c>Number</c> being
    /// <c>{integer, fraction}</c> and <c>OrNull</c> adding <c>null</c>; a value or a domain is a set of tagged scalars;
    /// a disequality is one; a bound is a bound, binding numbers in both.
    /// </para>
    /// <para>
    /// <b>A stored form and an extension are tokens.</b> The theory reads nothing of either beyond what every form says
    /// of itself — it is a way of writing strings, in the characters it names — so each is named by a token from
    /// <see cref="JsonFactVocabulary"/>, and the object it stands for is found again by the token. A string's characters
    /// are a set of bands, as a type is a set of types.
    /// </para>
    /// </remarks>
    internal static class JsonFactEncoding
    {

        /// <summary>
        /// Returns the theory's spelling of a path.
        /// </summary>
        /// <param name="path">The path.</param>
        /// <returns>The path as the theory writes it.</returns>
        public static Saturation.JsonPathSpec Path(JsonDocumentPath path)
        {
            var spec = Saturation.JsonPathSpec.Root;
            foreach (var name in path.Names)
                spec = spec[name];

            return spec;
        }

        /// <summary>
        /// Returns the theory's spelling of a scalar: its JSON type as a tag, and its value.
        /// </summary>
        /// <param name="value">The scalar.</param>
        /// <returns>The tagged value.</returns>
        public static string Value(JsonScalar value) => value.ToClrValue() switch
        {
            null => NullValue,
            bool b => Of(b),
            string s => Of(s),
            decimal m => Of(m),
            double d => Of(d),
            var other => throw new NotSupportedException(other.GetType().Name),
        };

        /// <summary>
        /// Reads a tagged value back as the scalar it spells.
        /// </summary>
        /// <param name="value">The tagged value.</param>
        /// <param name="scalar">The scalar.</param>
        /// <returns><c>true</c> where the value is one this encoding writes.</returns>
        public static bool TryScalar(string value, out JsonScalar scalar)
        {
            scalar = JsonScalar.Null;

            switch (TypeOf(value))
            {
                case NullType:
                    return true;
                case BooleanType:
                    scalar = value == Of(true);
                    return true;
                case StringType:
                    scalar = StringOf(value);
                    return true;
                case IntegerType or FractionType when TryNumberOf(value, out var exact):
                    scalar = exact;
                    return true;
                case IntegerType or FractionType when ApproximateOf(value) is double approximate:
                    scalar = approximate;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Returns the members a JSON type stands for.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="orNull">Whether a JSON null is admitted beside it.</param>
        /// <returns>The members.</returns>
        public static ValueSet<string> Types(JsonType type, bool orNull)
        {
            var member = type switch
            {
                JsonType.String => StringType,
                JsonType.Number => "number",
                JsonType.Integer => IntegerType,
                JsonType.Boolean => BooleanType,
                JsonType.Object => ObjectType,
                JsonType.Array => ArrayType,
                JsonType.Null => NullType,
                _ => throw new NotSupportedException(type.ToString()),
            };

            return orNull ? Saturation.JsonValues.Types(member, NullType) : Saturation.JsonValues.Types(member);
        }

        /// <summary>
        /// Returns the theory's facts that state what a claim states, for a head or an established fact.
        /// </summary>
        /// <remarks>
        /// One fact a claim, and none for a claim the theory cannot state. An extension is its token and nothing beside
        /// it: what the value is in JSON's terms the reader that read the extension states as claims of their own.
        /// </remarks>
        /// <param name="fact">The fact.</param>
        /// <param name="vocabulary">The tokens of stored forms and extensions.</param>
        /// <param name="register">
        /// Whether a form or an extension with no token is given one. A theory's rules are, once, before it is asked
        /// anything; what a query established is not, and a form no rule states is one nothing could be known of.
        /// </param>
        /// <returns>The facts, which are empty for a claim the theory cannot state.</returns>
        public static IEnumerable<Saturation.JsonFact> Statement(JsonFact fact, JsonFactVocabulary vocabulary, bool register)
        {
            var path = Path(fact.Path);

            switch (fact.Claim)
            {
                case JsonClaim.Present:
                    yield return new Saturation.JsonFact.Present(path);
                    break;

                case JsonClaim.OfType typed:
                    yield return new Saturation.JsonFact.OfType(path, Types(typed.Type, typed.OrNull));
                    break;

                case JsonClaim.EqualTo equal:
                    yield return new Saturation.JsonFact.OneOf(path, ValueSet.Of(Value(equal.Value)));
                    break;

                case JsonClaim.OneOf domain:
                    yield return new Saturation.JsonFact.OneOf(path, Domain(domain.Values));
                    break;

                case JsonClaim.NotEqualTo unequal:
                    yield return new Saturation.JsonFact.NotEqual(path, Value(unequal.Value));
                    break;

                case JsonClaim.LowerBound lower:
                    yield return new Saturation.JsonFact.Lower(path, new Saturation.Bound(lower.Value, lower.Exclusive));
                    break;

                case JsonClaim.UpperBound upper:
                    yield return new Saturation.JsonFact.Upper(path, new Saturation.Bound(upper.Value, upper.Exclusive));
                    break;

                case JsonClaim.Represents represents:
                    if (register)
                        yield return new Saturation.JsonFact.Form(path, vocabulary.Form(represents.Form));
                    else if (vocabulary.TryForm(represents.Form, out var form))
                        yield return new Saturation.JsonFact.Form(path, form);

                    break;

                case JsonClaim.Characters characters:
                    yield return new Saturation.JsonFact.Characters(path, Members(characters.Bands));
                    break;

                case JsonClaim.Written written:
                    yield return new Saturation.JsonFact.Written(path, Members(written.Bands));
                    break;

                case JsonClaim.Extension extension:
                    if (register)
                        yield return new Saturation.JsonFact.Extension(path, vocabulary.Extension(extension));
                    else if (vocabulary.TryExtension(extension, out var key))
                        yield return new Saturation.JsonFact.Extension(path, key);

                    break;
            }
        }

        /// <summary>
        /// Returns the theory's fact for a rule's body atom, or <c>null</c> where a body cannot read it.
        /// </summary>
        /// <remarks>
        /// A body atom is known the way the theory knows any fact, and means what the claim means: a domain within the
        /// set, types within the set, a disequality stated or proven, a presence. A claim the theory has no body atom for
        /// — a stored form, an extension — makes the rule unreadable, and the caller drops it, which loses facts.
        /// </remarks>
        /// <param name="fact">The body atom.</param>
        /// <returns>The theory's atom, or <c>null</c>.</returns>
        public static Saturation.JsonFact? Condition(JsonFact fact)
        {
            var path = Path(fact.Path);

            return fact.Claim switch
            {
                JsonClaim.Present => new Saturation.JsonFact.Present(path),
                JsonClaim.OfType typed => new Saturation.JsonFact.OfType(path, Types(typed.Type, typed.OrNull)),
                JsonClaim.EqualTo equal => new Saturation.JsonFact.OneOf(path, ValueSet.Of(Value(equal.Value))),
                JsonClaim.OneOf domain => new Saturation.JsonFact.OneOf(path, Domain(domain.Values)),
                JsonClaim.NotEqualTo unequal => new Saturation.JsonFact.NotEqual(path, Value(unequal.Value)),
                _ => null,
            };
        }

        /// <summary>
        /// Returns a domain as the set of its tagged values.
        /// </summary>
        public static ValueSet<string> Domain(IReadOnlyList<JsonScalar> values)
        {
            var encoded = new List<string>(values.Count);
            foreach (var value in values)
                encoded.Add(Value(value));

            return ValueSet.From(encoded);
        }

        /// <summary>
        /// Returns the theory's members for a set of bands: <see cref="Saturation.JsonValues.SurrogateBand"/> and
        /// <see cref="Saturation.JsonValues.HighBand"/>.
        /// </summary>
        public static ValueSet<string> Members(JsonCharacterBand bands) => bands switch
        {
            JsonCharacterBand.None => NoBand,
            JsonCharacterBand.Surrogates => ValueSet.Of(SurrogateBand),
            JsonCharacterBand.HighBmp => ValueSet.Of(HighBand),
            _ => AllBands,
        };

        /// <summary>
        /// Reads the theory's members back as a set of bands.
        /// </summary>
        public static JsonCharacterBand Bands(ValueSet<string> members) =>
            (members.Contains(SurrogateBand) ? JsonCharacterBand.Surrogates : JsonCharacterBand.None)
            | (members.Contains(HighBand) ? JsonCharacterBand.HighBmp : JsonCharacterBand.None);

        /// <summary>
        /// Returns the bands a value holds units of, as the theory reads a value's characters when it reasons with one: a
        /// string's own, and none for any other value.
        /// </summary>
        /// <remarks>
        /// The one reader of a value's characters, for a literal or a parameter a consumer holds, a row as it arrives and a
        /// value a declaration or a filter names, which the theory reads through <see cref="Saturation.JsonValues.CharactersOf(string)"/>
        /// itself: so what a consumer asks of a value is what the theory knows of it.
        /// </remarks>
        public static JsonCharacterBand Characters(JsonScalar value) => Bands(CharactersOf(Value(value)));

        /// <summary>
        /// Returns the bands a text's units are in.
        /// </summary>
        public static JsonCharacterBand Characters(string text) => Bands(BandsOfText(text));

        /// <summary>
        /// Returns the band one UTF-16 unit is in: <see cref="JsonCharacterBand.None"/> below U+D800.
        /// </summary>
        public static JsonCharacterBand Characters(char unit) => BandOf(unit) switch
        {
            null => JsonCharacterBand.None,
            SurrogateBand => JsonCharacterBand.Surrogates,
            _ => JsonCharacterBand.HighBmp,
        };

        /// <summary>
        /// Returns the bands of every character from one to another, each a UTF-16 code unit or a code point, a code point
        /// past U+FFFF being written in surrogates: what a pattern's reader reads a range as.
        /// </summary>
        public static JsonCharacterBand Characters(int lo, int hi) => Bands(BandsOf(lo, hi));

    }

}
