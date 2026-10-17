using System;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// The tables a <see cref="JsonFactSet"/> reads its answers from: what is known of each path, in the theory's terms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two kinds, read alike.</b> A context of the theory, saturated by the engine; and the facts a query states read
    /// beside what its container states, where the engine could add nothing that changes an answer (see
    /// <see cref="JsonFactOverlay"/>). A set asks both the same questions, so the two can differ only in what they hold.
    /// </para>
    /// <para>
    /// <b>Each read loses a fact rather than inventing one.</b> Where the engine fails, a read answers that nothing is
    /// known.
    /// </para>
    /// </remarks>
    internal abstract class JsonFactTables
    {

        /// <summary>
        /// Gets whether what is known cannot all hold of one document.
        /// </summary>
        public abstract bool IsContradictory { get; }

        /// <summary>
        /// Determines whether a path is known to be there.
        /// </summary>
        public abstract bool Present(JsonDocumentPath path);

        /// <summary>
        /// Returns the types a path can hold if it is there, where they are known.
        /// </summary>
        public abstract bool TryTypes(JsonDocumentPath path, out ValueSet<string> types);

        /// <summary>
        /// Returns the values a path can hold if it is there, where they are known.
        /// </summary>
        public abstract bool TryDom(JsonDocumentPath path, out ValueSet<string> dom);

        /// <summary>
        /// Determines whether a path is known not to hold a value if it is there.
        /// </summary>
        public abstract bool Ne(JsonDocumentPath path, string value);

        /// <summary>
        /// Returns the least a path holds if it holds a number, where it is known.
        /// </summary>
        public abstract bool TryLo(JsonDocumentPath path, out Bound bound);

        /// <summary>
        /// Returns the most a path holds if it holds a number, where it is known.
        /// </summary>
        public abstract bool TryHi(JsonDocumentPath path, out Bound bound);

        /// <summary>
        /// Determines whether a path is known to be spelled in a stored form, by the form's token.
        /// </summary>
        public abstract bool Form(JsonDocumentPath path, string token);

        /// <summary>
        /// Returns the bands of UTF-16 units a path's strings may hold beside those below U+D800, where they are known.
        /// </summary>
        public abstract bool TryChars(JsonDocumentPath path, out ValueSet<string> bands);

        /// <summary>
        /// Determines whether a path is known to hold what an extension claim says, by the claim's key.
        /// </summary>
        public abstract bool Extension(JsonDocumentPath path, string key);

        /// <summary>
        /// Determines whether a path is known to be there and to hold this value in every document.
        /// </summary>
        public abstract bool Constant(JsonDocumentPath path, string value);

    }

}
