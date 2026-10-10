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
        /// A JSON number that is not an integer.
        /// </summary>
        Number,

        /// <summary>
        /// A JSON number with no fractional part.
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

}
