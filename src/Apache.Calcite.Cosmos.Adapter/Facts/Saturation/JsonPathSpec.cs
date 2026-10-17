using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// A path written the way a person writes one: <c>$</c>, <c>$.a.b</c>, <c>$.tags[10]</c>, and <c>$.items[*].id</c>
    /// for property <c>id</c> of every element of <c>items</c>.
    /// </summary>
    internal sealed record JsonPathSpec(IReadOnlyList<object> Segments)
    {

        /// <summary>The segment that stands for every element of an array.</summary>
        public static readonly object Every = new();

        public static JsonPathSpec Root { get; } = new(System.Array.Empty<object>());

        public JsonPathSpec this[string property] => new(Segments.Append(property).ToArray());

        public JsonPathSpec this[long index] => new(Segments.Append((object)index).ToArray());

        public JsonPathSpec Elements => new(Segments.Append(Every).ToArray());

        public bool IsPattern => Segments.Any(s => ReferenceEquals(s, Every));

        /// <summary>The name of a hidden discriminator this stands for, which is no place in any document.</summary>
        public string? Hidden { get; init; }

        /// <summary>A hidden discriminator: which branch of an undiscriminated union applies.</summary>
        public static JsonPathSpec HiddenNamed(string name) => new(System.Array.Empty<object>()) { Hidden = name };

        public static JsonPathSpec Parse(string text)
        {
            if (text.StartsWith('$') == false)
                throw new FormatException(text);

            var path = Root;
            var i = 1;
            while (i < text.Length)
            {
                if (text[i] == '.')
                {
                    var end = i + 1;
                    while (end < text.Length && text[end] != '.' && text[end] != '[')
                        end++;

                    path = path[text.Substring(i + 1, end - i - 1)];
                    i = end;
                }
                else if (text[i] == '[')
                {
                    var end = text.IndexOf(']', i);
                    var inside = text.Substring(i + 1, end - i - 1);
                    path = inside == "*" ? path.Elements : path[long.Parse(inside, CultureInfo.InvariantCulture)];
                    i = end + 1;
                }
                else
                {
                    throw new FormatException(text);
                }
            }

            return path;
        }

        public static implicit operator JsonPathSpec(string text) => Parse(text);

        public Term<JPath> ToTerm(JsonRules rules)
        {
            if (Hidden is not null)
                return rules.Ghost.Of(Hidden);

            Term<JPath> term = rules.Root.Of();
            foreach (var segment in Segments)
                term = segment switch
                {
                    string name => rules.Prop.Of(term, name),
                    long index => rules.Index.Of(term, index),
                    _ => rules.Each.Of(term),
                };

            return term;
        }

        public bool Equals(JsonPathSpec? other) => other is not null && Hidden == other.Hidden && Segments.SequenceEqual(other.Segments);

        public override int GetHashCode() => Segments.Aggregate(Hidden?.GetHashCode() ?? 17, (h, s) => h * 31 + (ReferenceEquals(s, Every) ? 7 : s.GetHashCode()));

        public override string ToString()
        {
            if (Hidden is not null)
                return "hidden:" + Hidden;

            var builder = new StringBuilder("$");
            foreach (var segment in Segments)
                builder.Append(segment switch
                {
                    string name => "." + name,
                    long index => "[" + index.ToString(CultureInfo.InvariantCulture) + "]",
                    _ => "[*]",
                });

            return builder.ToString();
        }

    }

}
