using System;
using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// The JSON types a claim can name, which are the seven the service's own type predicates
    /// distinguish.
    /// </summary>
    public enum JsonType
    {

        /// <summary>
        /// A JSON string.
        /// </summary>
        String,

        /// <summary>
        /// A JSON number: any number, whole or not, as JSON Schema's <c>number</c> is.
        /// </summary>
        /// <remarks>
        /// <see cref="Integer"/> lies beneath it rather than beside it, and a type is compared through
        /// <see cref="JsonTypes.IsA"/> for that reason. As a value's own type it is the narrowest that
        /// fits, so a scalar is a <c>Number</c> only where it has a fractional part.
        /// </remarks>
        Number,

        /// <summary>
        /// A JSON number with no fractional part, which is a <see cref="Number"/>.
        /// </summary>
        Integer,

        /// <summary>
        /// A JSON boolean.
        /// </summary>
        Boolean,

        /// <summary>
        /// A JSON object.
        /// </summary>
        Object,

        /// <summary>
        /// A JSON array.
        /// </summary>
        Array,

        /// <summary>
        /// A JSON null, which is not the same as an absent path.
        /// </summary>
        Null,

    }

    /// <summary>
    /// How the JSON types relate.
    /// </summary>
    public static class JsonTypes
    {

        /// <summary>
        /// Determines whether every value of one type is a value of another.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The types are a lattice, not a list.</b> JSON has one number type, and JSON Schema's
        /// <c>integer</c> is the subset of <c>number</c> with no fractional part. So a whole number is a
        /// <see cref="JsonType.Number"/> as well as a <see cref="JsonType.Integer"/>, and every other
        /// pair of distinct types is disjoint.
        /// </para>
        /// <para>
        /// Compared as a list, a path declared <c>"type": "number"</c> and a query's comparison with
        /// <c>2</c> excluded each other: the schema said <c>Number</c>, the value's type was
        /// <c>Integer</c>, and the predicate was rewritten to <c>FALSE</c> (#217). Every comparison of
        /// a value's type against a claim's goes through here so that the relation is stated once.
        /// </para>
        /// </remarks>
        /// <param name="type">The narrower type.</param>
        /// <param name="of">The type it may lie beneath.</param>
        /// <returns><c>true</c> where every value of <paramref name="type"/> is of <paramref name="of"/>.</returns>
        public static bool IsA(this JsonType type, JsonType of) =>
            type == of || type == JsonType.Integer && of == JsonType.Number;

    }

}
