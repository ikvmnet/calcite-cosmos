using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Saturation;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// A SQL value as Calcite holds one to compare it: a null, a string, an exact number, a double, a boolean, or the
    /// failure of whatever was to produce it.
    /// </summary>
    abstract record SqlDatum
    {

        public static readonly SqlDatum Null = new NullDatum();

        public static readonly SqlDatum Raise = new Raised();

        public sealed record NullDatum : SqlDatum;

        public sealed record Raised : SqlDatum;

        public sealed record Text(string Value) : SqlDatum;

        public sealed record Exact(decimal Value) : SqlDatum;

        public sealed record Double(double Value) : SqlDatum;

        /// <summary>A number <c>BigDecimal</c> parsed from text no decimal holds, compared exactly by its digits.</summary>
        public sealed record Decimal(string Spelled) : SqlDatum;

        public sealed record Boolean(bool Value) : SqlDatum;

    }

}
