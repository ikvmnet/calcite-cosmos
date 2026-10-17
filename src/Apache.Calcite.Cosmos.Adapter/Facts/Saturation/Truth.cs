using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>What every document a context admits says about a predicate.</summary>
    internal enum Truth
    {

        /// <summary>Some documents satisfy it and some may not, or the context cannot tell.</summary>
        Unknown,

        /// <summary>Every document the context admits satisfies it.</summary>
        Always,

        /// <summary>No document the context admits satisfies it.</summary>
        Never,

    }

}
