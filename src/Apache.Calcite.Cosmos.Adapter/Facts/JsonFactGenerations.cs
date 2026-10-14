using System;
using System.Runtime.CompilerServices;
using System.Threading;

using Apache.Calcite.Cosmos.Facts.Saturation;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// The graphs a family of theories reasons in, one generation at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One graph for many containers.</b> A graph holds the rules of JSON in its root and the rules of JSON Schema in
    /// a child of it, both frozen; each container's declaration is a frozen context beneath those, and each query a
    /// context beneath its container's. Defining the rules costs a graph tens of milliseconds, so the containers of one
    /// schema object share one, and a container costs only its own context.
    /// </para>
    /// <para>
    /// <b>Why there are generations.</b> A graph interns every value it is ever written — a property name, a literal, a
    /// domain — and its pools never shrink, so a graph serving queries forever grows forever. Once a generation has
    /// interned more than its limit it is retired: the next theory to ask starts a new one and compiles itself into it,
    /// and the others follow as they are asked. Nothing disposes a retired generation. A statement still holding one of
    /// its answers keeps it alive, and once none does it is collected whole, with every context beneath it.
    /// </para>
    /// <para>
    /// <b>Owned by a schema object, not by the process.</b> What a schema learns hangs off the schema, so the schema
    /// factory makes one of these per schema object and binds its containers to it. A theory made without one — by a host
    /// building metadata itself, or by a test — shares <see cref="Default"/>.
    /// </para>
    /// </remarks>
    internal sealed class JsonFactGenerations
    {

        /// <summary>
        /// How many values a generation interns before it is retired: about a million, which is tens of megabytes.
        /// </summary>
        public const long DefaultInternedLimit = 1_000_000;

        /// <summary>
        /// The generations of every theory not bound to a schema object's.
        /// </summary>
        public static JsonFactGenerations Default { get; } = new();

        readonly long _internedLimit;
        readonly object _starting = new();
        readonly ConditionalWeakTable<JsonFactTheory, JsonFactTheory> _bound = new();
        Generation? _current;
        int _started;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="internedLimit">How many values a generation interns before it is retired.</param>
        public JsonFactGenerations(long internedLimit = DefaultInternedLimit)
        {
            if (internedLimit < 1)
                throw new ArgumentOutOfRangeException(nameof(internedLimit));

            _internedLimit = internedLimit;
        }

        /// <summary>
        /// Gets how many generations have been started, for diagnostics and tests.
        /// </summary>
        public int Started => Volatile.Read(ref _started);

        /// <summary>
        /// Gets the generation a theory compiles into now, starting one where none is live.
        /// </summary>
        public Generation Current
        {
            get
            {
                var current = Volatile.Read(ref _current);
                if (current is not null && current.InternedCount <= _internedLimit)
                    return current;

                lock (_starting)
                {
                    current = _current;
                    if (current is null || current.InternedCount > _internedLimit)
                    {
                        current = new Generation(this, Interlocked.Increment(ref _started));
                        Volatile.Write(ref _current, current);
                    }

                    return current;
                }
            }
        }

        /// <summary>
        /// Returns a theory stating what <paramref name="theory"/> states, reasoning in these generations; the same one
        /// each time it is asked of the same theory.
        /// </summary>
        /// <param name="theory">The theory.</param>
        /// <returns>The theory, bound here.</returns>
        public JsonFactTheory Bind(JsonFactTheory theory)
        {
            if (ReferenceEquals(theory.Generations, this))
                return theory;

            return _bound.GetValue(theory, t => new JsonFactTheory(t.Rules, this));
        }

        /// <summary>
        /// One graph: the rules of JSON and of JSON Schema, frozen, beneath which theories compile their contexts.
        /// </summary>
        internal sealed class Generation
        {

            internal Generation(JsonFactGenerations owner, int number)
            {
                Owner = owner;
                Number = number;
                Contexts = new JsonContexts();
            }

            /// <summary>
            /// Gets the generations this one belongs to.
            /// </summary>
            public JsonFactGenerations Owner { get; }

            /// <summary>
            /// Gets which generation this is, counting from one.
            /// </summary>
            public int Number { get; }

            /// <summary>
            /// Gets the graph and its frozen contexts.
            /// </summary>
            public JsonContexts Contexts { get; }

            /// <summary>
            /// Gets how many values the graph has interned, which never decreases.
            /// </summary>
            public long InternedCount => Contexts.Graph.Values.InternedCount;

            /// <inheritdoc />
            public override string ToString() => $"generation {Number}";

        }

    }

}
