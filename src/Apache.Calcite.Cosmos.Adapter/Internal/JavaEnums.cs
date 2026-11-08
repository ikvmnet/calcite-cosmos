using System;

using org.apache.calcite.rel;
using org.apache.calcite.sql;

namespace Apache.Calcite.Cosmos.Adapter.Internal
{

    /// <summary>
    /// Projects Java enumeration values onto the CLR enumerations IKVM generates for them, by name.
    /// </summary>
    /// <remarks>
    /// IKVM compiles a Java enumeration's <c>__Enum</c> values as constants taken from the ordinals of the Calcite this
    /// assembly was built against. Calcite inserts values mid-enumeration between releases, so an ordinal read at run time
    /// names a different value whenever the loaded Calcite differs; a name names the same one.
    /// </remarks>
    internal static class JavaEnums
    {

        /// <summary>
        /// Returns the value of <typeparamref name="TEnum"/> named as <paramref name="value"/> is, or
        /// <paramref name="unknown"/> where the Calcite this assembly was built against has no such value.
        /// </summary>
        public static TEnum ByName<TEnum>(java.lang.Enum value, TEnum unknown) where TEnum : struct, Enum =>
            Enum.TryParse<TEnum>(value.name(), out var result) ? result : unknown;

        /// <summary>
        /// Returns <paramref name="kind"/> as <see cref="SqlKind.__Enum"/>, or <see cref="SqlKind.__Enum.OTHER"/> for a kind
        /// newer than this assembly.
        /// </summary>
        public static SqlKind.__Enum Of(SqlKind kind) => ByName(kind, SqlKind.__Enum.OTHER);

        /// <summary>
        /// Returns <paramref name="direction"/> as <see cref="RelFieldCollation.Direction.__Enum"/>, or
        /// <see cref="RelFieldCollation.Direction.__Enum.CLUSTERED"/> for a direction newer than this assembly.
        /// </summary>
        public static RelFieldCollation.Direction.__Enum Of(RelFieldCollation.Direction direction) =>
            ByName(direction, RelFieldCollation.Direction.__Enum.CLUSTERED);

        /// <summary>
        /// Returns <paramref name="direction"/> as <see cref="RelFieldCollation.NullDirection.__Enum"/>, or
        /// <see cref="RelFieldCollation.NullDirection.__Enum.UNSPECIFIED"/> for a direction newer than this assembly.
        /// </summary>
        public static RelFieldCollation.NullDirection.__Enum Of(RelFieldCollation.NullDirection direction) =>
            ByName(direction, RelFieldCollation.NullDirection.__Enum.UNSPECIFIED);

    }

}
