using System.Runtime.CompilerServices;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// Opens Calcite's model class filter to the classes this adapter's models name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Calcite 1.43 checks every class a model names — schema factories, user-defined functions,
    /// table factories, JDBC drivers — against a denylist and then an allowlist, and the allowlist is
    /// fail-closed: <c>ClassNameFilter.check</c> requires a positive match, so an <em>empty</em>
    /// allowlist rejects everything rather than nothing. The default is empty.
    /// </para>
    /// <para>
    /// That is not only about this adapter's own factory. Calcite reaches the same path for its own
    /// built-ins — <c>SqlSpatialTypeOperatorTable</c> declares
    /// <c>org.apache.calcite.runtime.SpatialTypeFunctions</c> through <c>ModelHandler</c> — so with
    /// nothing allowed, asking Calcite for its spatial library throws.
    /// </para>
    /// <para>
    /// The value is read once, when <c>CalciteSystemProperty</c> initializes, and the filter it
    /// builds is held in a static. So this has to run before anything touches Calcite, which is what
    /// a module initializer is: it runs when this assembly loads, and this assembly is what a model
    /// naming <see cref="CosmosSchemaFactory"/> loads first.
    /// </para>
    /// <para>
    /// It appends rather than assigns. A host that has already stated its own allowlist keeps it, and
    /// a second adapter doing the same thing does not erase the first.
    /// </para>
    /// </remarks>
    static class CosmosModelClasses
    {

        /// <summary>
        /// The property Calcite reads its model class allowlist from.
        /// </summary>
        const string AllowedProperty = "calcite.model.classes.allowed";

        /// <summary>
        /// Calcite's own classes, which its built-in operator tables declare through the model path,
        /// and this adapter's, which a model names to reach the schema factory.
        /// </summary>
        /// <remarks>
        /// A pattern ending in <c>.</c> matches the package or namespace and everything under it;
        /// one that does not is matched exactly. The adapter's pattern covers the assembly-qualified
        /// spelling a model uses, the class name being the prefix of it.
        /// </remarks>
        static readonly string[] Patterns =
        [
            "org.apache.calcite.",
            "Apache.Calcite.Cosmos.Adapter.",
        ];

        /// <summary>
        /// Adds <see cref="Patterns"/> to the allowlist, keeping whatever is already there.
        /// </summary>
        [ModuleInitializer]
        internal static void Initialize()
        {
            var current = java.lang.System.getProperty(AllowedProperty) ?? "";

            foreach (var pattern in Patterns)
                if (Absent(current, pattern))
                    current = current.Length == 0 ? pattern : current + "," + pattern;

            java.lang.System.setProperty(AllowedProperty, current);
        }

        /// <summary>
        /// Determines whether a comma separated list does not already carry a pattern.
        /// </summary>
        /// <param name="list">The list.</param>
        /// <param name="pattern">The pattern.</param>
        /// <returns><c>true</c> where the pattern is not in the list.</returns>
        static bool Absent(string list, string pattern)
        {
            foreach (var part in list.Split(','))
                if (part.Trim() == pattern)
                    return false;

            return true;
        }

    }

}
