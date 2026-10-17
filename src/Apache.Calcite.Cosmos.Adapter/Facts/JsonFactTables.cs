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

    }

    /// <summary>
    /// The tables of a context of the theory.
    /// </summary>
    /// <remarks>
    /// <b>Thread-safe.</b> A frozen context is read freely; a query's context is read one call at a time, and released
    /// once nothing holds these tables.
    /// </remarks>
    internal sealed class JsonScopeTables : JsonFactTables
    {

        readonly JsonRules _rules;
        readonly Scope _scope;
        readonly Lease? _lease;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="rules">The tables of the graph the context belongs to.</param>
        /// <param name="scope">The context.</param>
        /// <param name="owned">Whether these tables own the context, which they release once they are collected: a query's.</param>
        public JsonScopeTables(JsonRules rules, Scope scope, bool owned)
        {
            _rules = rules;
            _scope = scope;
            _lease = owned ? new Lease(scope) : null;
        }

        /// <inheritdoc />
        public override bool IsContradictory => Read(false, (scope, _) => scope.IsContradictory);

        /// <inheritdoc />
        public override bool Present(JsonDocumentPath path) => Read(false, (scope, r) => scope.Check(r.Present.Of(Term(path, r))));

        /// <inheritdoc />
        public override bool TryTypes(JsonDocumentPath path, out ValueSet<string> types) => TryGet(path, r => r.Types, out types);

        /// <inheritdoc />
        public override bool TryDom(JsonDocumentPath path, out ValueSet<string> dom) => TryGet(path, r => r.Dom, out dom);

        /// <inheritdoc />
        public override bool Ne(JsonDocumentPath path, string value) => Read(false, (scope, r) => scope.Check(r.Ne.Of(Term(path, r), value)));

        /// <inheritdoc />
        public override bool TryLo(JsonDocumentPath path, out Bound bound) => TryGet(path, r => r.Lo, out bound);

        /// <inheritdoc />
        public override bool TryHi(JsonDocumentPath path, out Bound bound) => TryGet(path, r => r.Hi, out bound);

        /// <inheritdoc />
        public override bool Form(JsonDocumentPath path, string token) => Read(false, (scope, r) => scope.Check(r.Form.Of(Term(path, r), token)));

        /// <inheritdoc />
        public override bool TryChars(JsonDocumentPath path, out ValueSet<string> bands) => TryGet(path, r => r.Chars, out bands);

        /// <inheritdoc />
        public override bool Extension(JsonDocumentPath path, string key) => Read(false, (scope, r) => scope.Check(r.Extension.Of(Term(path, r), key)));

        bool TryGet<T>(JsonDocumentPath path, Func<JsonRules, Function<JPath, T>> table, out T value)
        {
            var (found, read) = Read((false, default(T)!), (scope, r) =>
                scope.TryGet(table(r).Of(Term(path, r)), out var got) ? (true, got) : (false, default(T)!));

            value = read;
            return found;
        }

        /// <summary>
        /// Reads the context, one call at a time where it is not frozen; or answers <paramref name="otherwise"/> where the
        /// engine fails, which loses a fact and invents none.
        /// </summary>
        T Read<T>(T otherwise, Func<Scope, JsonRules, T> read)
        {
            try
            {
                if (_lease is null)
                    return read(_scope, _rules);

                lock (_lease)
                    return read(_scope, _rules);
            }
            catch (Exception e) when (JsonFactTheory.IsEngineFailure(e))
            {
                return otherwise;
            }
            finally
            {
                // The lease releases the context once the tables are collected, and they may be collectable before a
                // read of their context returns.
                GC.KeepAlive(_lease);
            }
        }

        static Term<JPath> Term(JsonDocumentPath path, JsonRules rules) => JsonFactEncoding.Path(path).ToTerm(rules);

        /// <summary>
        /// Holds a query's context and releases it once nothing holds the tables, so that a context the memo let go of
        /// does not stay listed beneath its container's for the life of the generation.
        /// </summary>
        /// <remarks>
        /// A finalizer, because nothing calls into a set to say it is done with it: a set is handed to a translator, kept
        /// by a rule for the length of a match, and dropped. Releasing a context never throws and may run on any thread.
        /// </remarks>
        sealed class Lease
        {

            readonly Scope _scope;

            public Lease(Scope scope)
            {
                _scope = scope;
            }

            ~Lease()
            {
                _scope.Dispose();
            }

        }

    }

}
