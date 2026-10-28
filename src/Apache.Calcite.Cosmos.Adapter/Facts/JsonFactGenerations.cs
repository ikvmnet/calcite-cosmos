using System;
using System.Threading;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

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
    /// domain — and its pools never shrink, so a graph serving queries forever grows forever; and its ids and epochs are
    /// finite. Once a generation has interned more than its limit, or used its share of either, it is retired: the next
    /// theory to ask starts a new one and compiles itself into it, and the others follow as they are asked. Nothing
    /// disposes a retired generation. A statement still holding one of its answers keeps it alive, and once none does it
    /// is collected whole, with every context beneath it.
    /// </para>
    /// <para>
    /// <b>Owned by a schema object, not by the process.</b> What a schema learns hangs off the schema, so each schema
    /// object's reasoning holds one of these and binds its containers to it. There is no default: a theory bound to none
    /// compiles nothing, and no static reaches one, so a schema let go takes its graphs with it.
    /// </para>
    /// </remarks>
    internal sealed class JsonFactGenerations
    {

        /// <summary>
        /// How many values a generation interns before it is retired: about a million, which is tens of megabytes.
        /// </summary>
        public const long DefaultInternedLimit = 1_000_000;

        /// <summary>
        /// The share of its graph's ids or epochs a generation uses before it is retired: half, which leaves the rest to the
        /// contexts still open on it, since past either every call that needs one throws.
        /// </summary>
        public const double DefaultUsedLimit = 0.5;

        readonly long _internedLimit;
        readonly double _usedLimit;
        readonly object _starting = new();
        Generation? _current;
        int _started;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="internedLimit">How many values a generation interns before it is retired.</param>
        /// <param name="usedLimit">The share of its graph's ids or epochs a generation uses before it is retired.</param>
        public JsonFactGenerations(long internedLimit = DefaultInternedLimit, double usedLimit = DefaultUsedLimit)
        {
            if (internedLimit < 1)
                throw new ArgumentOutOfRangeException(nameof(internedLimit));
            if (usedLimit is not (> 0 and <= 1))
                throw new ArgumentOutOfRangeException(nameof(usedLimit));

            _internedLimit = internedLimit;
            _usedLimit = usedLimit;
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
                if (current is not null && Spent(current.Usage) == false)
                    return current;

                lock (_starting)
                {
                    current = _current;
                    if (current is null || Spent(current.Usage))
                    {
                        current = new Generation(this, Interlocked.Increment(ref _started));
                        Volatile.Write(ref _current, current);
                    }

                    return current;
                }
            }
        }

        bool Spent(GraphUsage usage) => usage.InternedValues > _internedLimit || usage.FractionUsed >= _usedLimit;

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
            /// Gets how much of its graph the generation has used: values interned, ids and epochs, none of which decreases.
            /// </summary>
            public GraphUsage Usage => Contexts.Graph.Usage;

            /// <inheritdoc />
            public override string ToString() => $"generation {Number}";

        }

    }

}
