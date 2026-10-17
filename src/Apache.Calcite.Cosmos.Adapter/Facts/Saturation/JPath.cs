using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// A path into a document: a class of terms built from <c>Root</c>, <c>Prop</c>, <c>Index</c> and <c>Each</c>.
    /// </summary>
    internal readonly record struct JPath(ClassId Id) : ISort<JPath>
    {

        public static JPath FromId(ClassId id) => new(id);

    }

}
