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
    /// A predicate a query can contain, in JSON's terms. Each is true of a document only where its path is there and
    /// holds what it asks, which is how Cosmos DB evaluates the comparison it stands for: a comparison over an absent
    /// path, or across types, is undefined, and a filter keeps no row for it.
    /// </summary>
    internal abstract record JsonAtom(JsonPathSpec Path)
    {

        /// <summary><c>IS_DEFINED</c>.</summary>
        public sealed record Defined(JsonPathSpec Path) : JsonAtom(Path);

        /// <summary><c>IS_STRING</c>, <c>IS_NUMBER</c> and the rest; <c>number</c> covers both kinds of number.</summary>
        public sealed record HasType(JsonPathSpec Path, string Type) : JsonAtom(Path);

        /// <summary>An equality with a scalar.</summary>
        public sealed record EqualTo(JsonPathSpec Path, string Value) : JsonAtom(Path);

        /// <summary>A range over a number, either side open or absent.</summary>
        public sealed record NumberIn(JsonPathSpec Path, Bound? Lower, Bound? Upper) : JsonAtom(Path);

        /// <summary>A range over <c>ARRAY_LENGTH</c>.</summary>
        public sealed record LengthIn(JsonPathSpec Path, long? Min, long? Max) : JsonAtom(Path);

        /// <summary><c>ARRAY_CONTAINS</c> of a scalar.</summary>
        public sealed record ArrayHolds(JsonPathSpec Path, string Value) : JsonAtom(Path);

        /// <summary>An equality between two paths, true where both are there and hold one value.</summary>
        public sealed record SameAs(JsonPathSpec Path, JsonPathSpec Other) : JsonAtom(Path);

    }

}
