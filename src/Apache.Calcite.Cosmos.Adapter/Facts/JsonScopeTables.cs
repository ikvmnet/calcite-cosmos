using System;
using System.Collections.Concurrent;
using System.Threading;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// The tables of a context of the theory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A path the context was never told of is told of the first time it is asked about.</b> The rules reason over
    /// paths that exist (<see cref="JsonContexts.Mention"/>), so a context knows nothing of one no fact and no question
    /// mentioned when it was opened — and a clause after a filter reads paths no conjunct does: a sort key, a projection,
    /// a grouping beneath a scalar the filter pins, which the rule that a path beneath one holding no object is absent says
    /// is never there. Where the tables are given a way to, such a path is read in a child that mentions it, saturated
    /// beneath the context and kept (<see cref="JsonContexts.Mentioning"/>), and it answers what the rules derive of it;
    /// the context itself is frozen, and says the same of every path it was told of. A child that cannot be opened leaves
    /// the path read in the context, knowing nothing, which loses a fact and invents none.
    /// </para>
    /// <para>
    /// <b>Thread-safe.</b> A frozen context is read freely, and a path is told of once, whichever thread asks first; a
    /// query's context is read one call at a time, and released once nothing holds these tables.
    /// </para>
    /// </remarks>
    internal sealed class JsonScopeTables : JsonFactTables
    {

        readonly JsonRules _rules;
        readonly Scope _scope;
        readonly Lease? _lease;
        readonly Func<JsonDocumentPath, Scope?>? _mention;
        readonly object? _keep;
        readonly ConcurrentDictionary<JsonDocumentPath, Lazy<Scope>>? _told;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="rules">The tables of the graph the context belongs to.</param>
        /// <param name="scope">The context.</param>
        /// <param name="owned">Whether these tables own the context, which they release once they are collected: a query's.</param>
        /// <param name="mention">Opens a child of the context, frozen, that mentions a path the context has no term for, or
        /// returns <c>null</c> where it cannot; <c>null</c> to read every path in the context itself. Only a frozen context
        /// is told of a path.</param>
        /// <param name="keep">What the context lives as long as, kept alive for as long as anything reads these tables: the
        /// lease of the statement it belongs to; or <c>null</c>.</param>
        public JsonScopeTables(JsonRules rules, Scope scope, bool owned, Func<JsonDocumentPath, Scope?>? mention = null, object? keep = null)
        {
            _rules = rules;
            _scope = scope;
            _lease = owned ? new Lease(scope) : null;
            _mention = owned ? null : mention;
            _keep = keep;
            _told = _mention is null ? null : new ConcurrentDictionary<JsonDocumentPath, Lazy<Scope>>();
        }

        /// <inheritdoc />
        public override bool IsContradictory => Read(false, (scope, _) => scope.IsContradictory);

        /// <inheritdoc />
        public override bool Present(JsonDocumentPath path) => Read(path, false, (scope, r, p) => scope.Check(r.Present.Of(p)));

        /// <inheritdoc />
        public override bool TryTypes(JsonDocumentPath path, out ValueSet<string> types) => TryGet(path, r => r.Types, out types);

        /// <inheritdoc />
        public override bool TryDom(JsonDocumentPath path, out ValueSet<string> dom) => TryGet(path, r => r.Dom, out dom);

        /// <inheritdoc />
        public override bool Ne(JsonDocumentPath path, string value) => Read(path, false, (scope, r, p) => scope.Check(r.Ne.Of(p, value)));

        /// <inheritdoc />
        public override bool TryLo(JsonDocumentPath path, out Bound bound) => TryGet(path, r => r.Lo, out bound);

        /// <inheritdoc />
        public override bool TryHi(JsonDocumentPath path, out Bound bound) => TryGet(path, r => r.Hi, out bound);

        /// <inheritdoc />
        public override bool TryMinLength(JsonDocumentPath path, out long length) => TryGet(path, r => r.MinLength, out length);

        /// <inheritdoc />
        public override bool TryMaxLength(JsonDocumentPath path, out long length) => TryGet(path, r => r.MaxLength, out length);

        /// <inheritdoc />
        public override bool Form(JsonDocumentPath path, string token) => Read(path, false, (scope, r, p) => scope.Check(r.Form.Of(p, token)));

        /// <inheritdoc />
        public override bool TryChars(JsonDocumentPath path, out ValueSet<string> bands) => TryGet(path, r => r.Chars, out bands);

        /// <inheritdoc />
        public override bool Extension(JsonDocumentPath path, string key) => Read(path, false, (scope, r, p) => scope.Check(r.Extension.Of(p, key)));

        /// <inheritdoc />
        public override bool Constant(JsonDocumentPath path, string value) => Read(path, false, (scope, r, p) => scope.Check(r.Constant.Of(p, value)));

        bool TryGet<T>(JsonDocumentPath path, Func<JsonRules, Function<JPath, T>> table, out T value)
        {
            var (found, read) = Read(path, (false, default(T)!), (scope, r, p) =>
                scope.TryGet(table(r).Of(p), out var got) ? (true, got) : (false, default(T)!));

            value = read;
            return found;
        }

        /// <summary>
        /// Reads what is known of a path, in the context or in the child told of it.
        /// </summary>
        T Read<T>(JsonDocumentPath path, T otherwise, Func<Scope, JsonRules, Term<JPath>, T> read) =>
            Read(otherwise, (scope, r) =>
            {
                var term = Term(path, r);
                return read(_told is null ? scope : Told(path, term), r, term);
            });

        /// <summary>
        /// Returns the context a path is read in: this one where it has a term for the path, and otherwise the child told
        /// of it, opened the first time it is asked for.
        /// </summary>
        Scope Told(JsonDocumentPath path, Term<JPath> term)
        {
            if (_told!.TryGetValue(path, out var told) == false)
                told = _told.GetOrAdd(path, new Lazy<Scope>(() => Tell(path, term), LazyThreadSafetyMode.ExecutionAndPublication));

            return told.Value;
        }

        Scope Tell(JsonDocumentPath path, Term<JPath> term)
        {
            if (_scope.TryGet(term, out JPath _))
                return _scope;

            try
            {
                return _mention!(path) ?? _scope;
            }
            catch (Exception e) when (JsonFactTheory.IsEngineFailure(e))
            {
                return _scope;
            }
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
                // What releases the context once the tables are collected, and they may be collectable before a read of
                // their context returns.
                GC.KeepAlive(_lease);
                GC.KeepAlive(_keep);
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
