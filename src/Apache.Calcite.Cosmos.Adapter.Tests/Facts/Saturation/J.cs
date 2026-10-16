using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Eggnog;

using Apache.Calcite.Cosmos.Facts.Saturation;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>A JSON value, small enough to enumerate and generate.</summary>
    abstract record J
    {

        public sealed record Null : J;

        public sealed record Bool(bool Value) : J;

        public sealed record Num(decimal Value) : J;

        /// <summary>A number a decimal cannot hold, which the theory carries approximately.</summary>
        public sealed record Huge(double Value) : J;

        public sealed record Str(string Value) : J;

        public sealed record Obj(SortedDictionary<string, J> Properties) : J;

        public sealed record Arr(List<J> Elements) : J;

        public string Type => this switch
        {
            Null => JsonValues.NullType,
            Bool => JsonValues.BooleanType,
            Num n => n.Value == decimal.Truncate(n.Value) ? IntegerType : FractionType,
            Huge h => Math.Floor(h.Value) == h.Value ? IntegerType : FractionType,
            Str => JsonValues.StringType,
            Obj => JsonValues.ObjectType,
            _ => JsonValues.ArrayType,
        };

        public bool IsScalar => this is not (Obj or Arr);

        /// <summary>The scalar's tagged spelling, as the theory writes it.</summary>
        public string Encoded => this switch
        {
            Null => NullValue,
            Bool b => Of(b.Value),
            Num n => Of(n.Value),
            Huge h => Of(h.Value),
            Str s => Of(s.Value),
            _ => throw new InvalidOperationException("not a scalar"),
        };

        public sealed override string ToString() => this switch
        {
            Null => "null",
            Bool b => b.Value ? "true" : "false",
            Num n => n.Value.ToString(CultureInfo.InvariantCulture),
            Huge h => h.Value.ToString("R", CultureInfo.InvariantCulture),
            Str s => "\"" + s.Value + "\"",
            Obj o => "{" + string.Join(", ", o.Properties.Select(kv => $"\"{kv.Key}\": {kv.Value}")) + "}",
            Arr a => "[" + string.Join(", ", a.Elements) + "]",
            _ => "?",
        };

    }

}
