using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>A statement about a path, to be assumed into a context.</summary>
    internal abstract record JsonFact(JsonPathSpec Path)
    {

        public sealed record Present(JsonPathSpec Path) : JsonFact(Path);

        public sealed record Absent(JsonPathSpec Path) : JsonFact(Path);

        public sealed record OfType(JsonPathSpec Path, ValueSet<string> Types) : JsonFact(Path);

        public sealed record OneOf(JsonPathSpec Path, ValueSet<string> Values) : JsonFact(Path);

        public sealed record NotEqual(JsonPathSpec Path, string Value) : JsonFact(Path);

        public sealed record Lower(JsonPathSpec Path, Bound Bound) : JsonFact(Path);

        public sealed record Upper(JsonPathSpec Path, Bound Bound) : JsonFact(Path);

        public sealed record MinLength(JsonPathSpec Path, long Length) : JsonFact(Path);

        public sealed record MaxLength(JsonPathSpec Path, long Length) : JsonFact(Path);

        public sealed record MinItems(JsonPathSpec Path, long Count) : JsonFact(Path);

        public sealed record MaxItems(JsonPathSpec Path, long Count) : JsonFact(Path);

        public sealed record ClosedTo(JsonPathSpec Path, ValueSet<string> Properties) : JsonFact(Path);

        public sealed record Requires(JsonPathSpec Path, string Property) : JsonFact(Path);

        public sealed record Holds(JsonPathSpec Path, string Value) : JsonFact(Path);

        public sealed record MultipleOf(JsonPathSpec Path, decimal Factor) : JsonFact(Path);

        public sealed record UniqueItems(JsonPathSpec Path) : JsonFact(Path);

        public sealed record MinProperties(JsonPathSpec Path, long Count) : JsonFact(Path);

        public sealed record MaxProperties(JsonPathSpec Path, long Count) : JsonFact(Path);

        /// <summary>The two paths hold one value, or are both absent: what <c>c.a = c.b</c> proves, and what a
        /// <c>CHECK (a = b)</c> over paths that are both there states.</summary>
        public sealed record Same(JsonPathSpec Path, JsonPathSpec Other) : JsonFact(Path);

        public sealed record ContainsAtLeast(JsonPathSpec Path, string Value, long Count) : JsonFact(Path);

        public sealed record ContainsAtMost(JsonPathSpec Path, string Value, long Count) : JsonFact(Path);

        public sealed record NameMinLength(JsonPathSpec Path, long Length) : JsonFact(Path);

        public sealed record NameMaxLength(JsonPathSpec Path, long Length) : JsonFact(Path);

        /// <summary>If the path holds a string, it is spelled in the named stored form.</summary>
        public sealed record Form(JsonPathSpec Path, string Name) : JsonFact(Path);

        /// <summary>
        /// If the path holds a string, its characters are of these classes alone (<see cref="JsonValues.CharacterClasses"/>);
        /// none for ASCII other than U+0000 that the service trims as Calcite does.
        /// </summary>
        public sealed record Characters(JsonPathSpec Path, ValueSet<string> Bands) : JsonFact(Path);

        /// <summary>
        /// If the path holds a string, it was written with characters of these classes alone: what a validator saw, from
        /// which the theory derives what is stored.
        /// </summary>
        public sealed record Written(JsonPathSpec Path, ValueSet<string> Bands) : JsonFact(Path);

        /// <summary>
        /// A stored form's strings hold characters of these classes alone: a statement about how the form is
        /// written, and so about no path, which rides on the root. Says nothing about any document.
        /// </summary>
        public sealed record FormCharacters(string Name, ValueSet<string> Bands) : JsonFact(JsonPathSpec.Root);

        /// <summary>The path holds what a host's claim, named by its key, says; the theory reads nothing of it.</summary>
        public sealed record Extension(JsonPathSpec Path, string Key) : JsonFact(Path);

        /// <summary>A split may open one case per value the path can hold. Says nothing about any document.</summary>
        public sealed record Discriminates(JsonPathSpec Path) : JsonFact(Path);

        public Fact ToFact(JsonRules r)
        {
            var p = Path.ToTerm(r);
            return this switch
            {
                Present => r.Present.Of(p),
                Absent => r.Absent.Of(p),
                OfType f => r.Types.Of(p).Is(f.Types),
                OneOf f => r.Dom.Of(p).Is(f.Values),
                NotEqual f => r.Ne.Of(p, f.Value),
                Lower f => r.Lo.Of(p).Is(f.Bound),
                Upper f => r.Hi.Of(p).Is(f.Bound),
                MinLength f => r.MinLength.Of(p).Is(f.Length),
                MaxLength f => r.MaxLength.Of(p).Is(f.Length),
                MinItems f => r.MinItems.Of(p).Is(f.Count),
                MaxItems f => r.MaxItems.Of(p).Is(f.Count),
                ClosedTo f => r.Closed.Of(p).Is(f.Properties),
                Requires f => r.Required.Of(p, f.Property),
                Holds f => r.Contains.Of(p, f.Value),
                MultipleOf f => r.MultipleOf.Of(p).Is(f.Factor),
                UniqueItems => r.Unique.Of(p),
                MinProperties f => r.MinProperties.Of(p).Is(f.Count),
                MaxProperties f => r.MaxProperties.Of(p).Is(f.Count),
                Same f => p.Is(f.Other.ToTerm(r)),
                ContainsAtLeast f => r.ContainsAtLeast.Of(p, f.Value).Is(f.Count),
                ContainsAtMost f => r.ContainsAtMost.Of(p, f.Value).Is(f.Count),
                NameMinLength f => r.NameMinLength.Of(p).Is(f.Length),
                NameMaxLength f => r.NameMaxLength.Of(p).Is(f.Length),
                Form f => r.Form.Of(p, f.Name),
                Characters f => r.Chars.Of(p).Is(f.Bands),
                Written f => r.Written.Of(p).Is(f.Bands),
                FormCharacters f => r.FormChars.Of(f.Name).Is(f.Bands),
                Extension f => r.Extension.Of(p, f.Key),
                Discriminates => r.Discriminator.Of(p),
                _ => throw new NotSupportedException(GetType().Name),
            };
        }

    }

}
