using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// A value a filter reads or names: the typed IR's predicate subset, the half of a predicate that is not a test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A reading is a constructor of its own, not a SQL type used as a tag.</b> <see cref="Text"/> is the accessor's
    /// rendering of a path, <see cref="Raw"/> the value the service holds there, <see cref="Typed"/> a <c>RETURNING</c>,
    /// and a <see cref="Cast"/> converts one of them. The loader decides which, once, where Calcite types the same
    /// path <c>VARCHAR</c>, <c>ANY</c>, <c>VARIANT</c> or a <c>RETURNING</c> type depending on how it was written.
    /// What each means on every kind of stored value is <c>PredicateAtom</c>'s, in one place.
    /// </para>
    /// <para>
    /// <b>A literal is a JSON scalar</b> in the theory's spelling (<c>JsonValues</c>): <c>string|30</c>,
    /// <c>integer|30</c>, <c>boolean|true</c>. A parameter has no value, only a type and an ordinal.
    /// </para>
    /// </remarks>
    internal abstract record SqlValue
    {

        /// <summary><c>JSON_VALUE(DOC, path)</c> read as text: a scalar's rendering, and SQL null for anything else.</summary>
        public sealed record Text(JsonDocumentPath Path) : SqlValue
        {
            public override string ToString() => "text(" + Spell(Path) + ")";
        }

        /// <summary>The value the service holds at a path: a promoted column, an <c>ANY</c>-typed accessor, <c>id</c>.</summary>
        public sealed record Raw(JsonDocumentPath Path) : SqlValue
        {
            public override string ToString() => "raw(" + Spell(Path) + ")";
        }

        /// <summary><c>JSON_VALUE(DOC, path RETURNING type)</c>: the stored scalar, asserted to be of the type.</summary>
        public sealed record Typed(JsonDocumentPath Path, string Type) : SqlValue
        {
            public override string ToString() => "typed(" + Spell(Path) + " " + Type + ")";
        }

        /// <summary>A cast of a value to a SQL type: a number, or text over a raw value.</summary>
        public sealed record Cast(string Target, SqlValue Value) : SqlValue
        {
            public override string ToString() => "cast(" + Target + ", " + Value + ")";
        }

        /// <summary>A literal: a JSON scalar in the theory's spelling.</summary>
        public sealed record Literal(string Value) : SqlValue
        {

            /// <summary>Gets the literal's JSON type: <c>string</c>, <c>integer</c>, <c>fraction</c>, <c>boolean</c>, <c>null</c>.</summary>
            public string Kind => Saturation.JsonValues.TypeOf(Value);

            /// <summary>Gets whether the literal is a number.</summary>
            public bool IsNumber => Saturation.JsonValues.IsNumber(Value);

            public override string ToString() => "lit(" + Quote(Value) + ")";

        }

        /// <summary>A parameter, of whose value nothing is known: not even that it is not null.</summary>
        public sealed record Parameter(long Ordinal, string Type) : SqlValue
        {
            public override string ToString() => "?" + Ordinal + ":" + Type;
        }

        /// <summary>
        /// Returns the literal of a scalar.
        /// </summary>
        public static Literal Of(JsonScalar value) => new(JsonFactEncoding.Value(value));

        /// <summary>
        /// Gets the document path the value reads, through any cast, or <c>null</c> where it reads none.
        /// </summary>
        public JsonDocumentPath? PathRead => this switch
        {
            Text t => t.Path,
            Raw r => r.Path,
            Typed t => t.Path,
            Cast c => c.Value.PathRead,
            _ => null,
        };

        /// <summary>
        /// Spells a path unambiguously: every name quoted, so a name holding a dot is not two names.
        /// </summary>
        internal static string Spell(JsonDocumentPath path)
        {
            var builder = new StringBuilder("$");
            foreach (var name in path.Names)
                builder.Append('[').Append(Quote(name)).Append(']');

            return builder.ToString();
        }

        /// <summary>
        /// Quotes text as a JSON string, so that what it spells cannot be read as anything around it.
        /// </summary>
        internal static string Quote(string text) => System.Text.Json.JsonSerializer.Serialize(text);

    }

    /// <summary>
    /// A filter predicate: the typed IR's predicate subset, which the oracle loads into the theory as terms.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its meaning is SQL's, in three values.</b> A comparison is UNKNOWN where what it reads is SQL null — an absent
    /// path, a JSON null, and for a text accessor an object or an array — and a filter keeps no row for UNKNOWN or FALSE.
    /// <see cref="Not"/> swaps TRUE and FALSE and keeps UNKNOWN, which is why a predicate that keeps no row is not one
    /// whose negation keeps every row.
    /// </para>
    /// <para>
    /// <b>One spelling per meaning the loader can see.</b> <see cref="AllOf"/> and <see cref="AnyOf"/> flatten, drop
    /// duplicates and order their arms by digest, so that <c>a AND b</c> and <c>b AND a</c> are one term without a rule
    /// of commutativity. <see cref="ToString"/> is the digest.
    /// </para>
    /// <para>
    /// <b>Anything the loader does not read is <see cref="Opaque"/></b>, numbered within its statement: a term that proves
    /// nothing and decides nothing, and whose only use is to be counted among a statement's conjuncts.
    /// </para>
    /// <para>
    /// <b>Its digest is written once.</b> What the oracle remembers a predicate by is its digest, and a planner asks of the
    /// same predicate many times — a merged view's filter stands again inside each of its <c>CASE</c> columns — so the
    /// digest is spelled the first time it is asked and kept beside the predicate, outside its equality.
    /// </para>
    /// </remarks>
    internal abstract record SqlPredicate
    {

        string? _digest;

        /// <summary>
        /// Gets the predicate's digest, which is its spelling: equal predicates have equal digests, and are one term.
        /// </summary>
        public string Digest => _digest ??= Spell();

        /// <summary>
        /// Spells the predicate, once: see <see cref="Digest"/>.
        /// </summary>
        protected abstract string Spell();

        /// <inheritdoc />
        public sealed override string ToString() => Digest;

        /// <summary>
        /// Compares the kind of predicate alone: each kind compares its own fields, and the digest kept beside them is
        /// not one of them.
        /// </summary>
        public virtual bool Equals(SqlPredicate? other) => other is not null && EqualityContract == other.EqualityContract;

        /// <inheritdoc />
        public override int GetHashCode() => EqualityContract.GetHashCode();

        /// <summary>A comparison: a value that reads a path, an operator, and a literal or a parameter.</summary>
        public sealed record Compare(string Op, SqlValue Left, SqlValue Right) : SqlPredicate
        {

            public const string Equal = Saturation.PredicateAtom.Equal;
            public const string NotEqual = Saturation.PredicateAtom.NotEqual;
            public const string Less = Saturation.PredicateAtom.Less;
            public const string LessOrEqual = Saturation.PredicateAtom.LessOrEqual;
            public const string Greater = Saturation.PredicateAtom.Greater;
            public const string GreaterOrEqual = Saturation.PredicateAtom.GreaterOrEqual;

            /// <summary>The operator that means the same with its operands the other way round.</summary>
            public static string Mirror(string op) => Saturation.PredicateAtom.Mirror(op);

            protected override string Spell() => "(" + Left + " " + Op + " " + Right + ")";

        }

        /// <summary>A membership: <c>IN</c>, expanded out of a <c>SEARCH</c> and read back as one test.</summary>
        public sealed record In(SqlValue Value, ValueSet<string> Points) : SqlPredicate
        {
            protected override string Spell() => "(" + Value + " in [" + string.Join(", ", Points.Select(SqlValue.Quote)) + "])";
        }

        /// <summary><c>IS NULL</c>, which is never UNKNOWN.</summary>
        public sealed record IsNull(SqlValue Value) : SqlPredicate
        {
            protected override string Spell() => "(" + Value + " is null)";
        }

        /// <summary>
        /// <c>IS_DEFINED</c> of the path a value reads: true where the path is there, a JSON null included, false where it
        /// is not, and never UNKNOWN. What it asks is the document's whatever the reading, so the loader names the raw value.
        /// </summary>
        public sealed record IsDefined(SqlValue Value) : SqlPredicate
        {
            protected override string Spell() => "(" + Value + " is defined)";
        }

        /// <summary>A conjunction of two or more arms, in digest order.</summary>
        public sealed record And(IReadOnlyList<SqlPredicate> Arms) : SqlPredicate
        {
            public bool Equals(And? other) => other is not null && Arms.SequenceEqual(other.Arms);

            public override int GetHashCode() => Digest.GetHashCode(StringComparison.Ordinal);

            protected override string Spell() => "and(" + string.Join(", ", Arms) + ")";
        }

        /// <summary>A disjunction of two or more arms, in digest order.</summary>
        public sealed record Or(IReadOnlyList<SqlPredicate> Arms) : SqlPredicate
        {
            public bool Equals(Or? other) => other is not null && Arms.SequenceEqual(other.Arms);

            public override int GetHashCode() => Digest.GetHashCode(StringComparison.Ordinal);

            protected override string Spell() => "or(" + string.Join(", ", Arms) + ")";
        }

        /// <summary>A negation.</summary>
        public sealed record Not(SqlPredicate Operand) : SqlPredicate
        {
            protected override string Spell() => "not(" + Operand + ")";
        }

        /// <summary>A predicate the loader does not read, numbered within its statement.</summary>
        public sealed record Opaque(long Id) : SqlPredicate
        {
            protected override string Spell() => "opaque#" + Id;
        }

        /// <summary>
        /// Returns the conjunction of the arms: flattened, without duplicates, in digest order; one arm is itself.
        /// </summary>
        public static SqlPredicate AllOf(IEnumerable<SqlPredicate> arms) =>
            Junction(arms, a => a is And and ? and.Arms : null, list => new And(list));

        /// <summary>
        /// Returns the disjunction of the arms: flattened, without duplicates, in digest order; one arm is itself.
        /// </summary>
        public static SqlPredicate AnyOf(IEnumerable<SqlPredicate> arms) =>
            Junction(arms, a => a is Or or ? or.Arms : null, list => new Or(list));

        static SqlPredicate Junction(IEnumerable<SqlPredicate> arms, Func<SqlPredicate, IReadOnlyList<SqlPredicate>?> nested, Func<IReadOnlyList<SqlPredicate>, SqlPredicate> make)
        {
            var flat = new Dictionary<string, SqlPredicate>(StringComparer.Ordinal);
            var pending = new Stack<SqlPredicate>(arms.Reverse());
            while (pending.Count > 0)
            {
                var arm = pending.Pop();
                if (nested(arm) is { } inner)
                {
                    foreach (var x in inner.Reverse())
                        pending.Push(x);
                    continue;
                }

                flat.TryAdd(arm.Digest, arm);
            }

            if (flat.Count == 0)
                throw new ArgumentException("A junction needs an arm.", nameof(arms));

            var ordered = flat.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value).ToArray();
            return ordered.Length == 1 ? ordered[0] : make(ordered);
        }

        /// <summary>
        /// Gets the predicate and every predicate within it.
        /// </summary>
        public IEnumerable<SqlPredicate> Subterms
        {
            get
            {
                yield return this;

                var inner = this switch
                {
                    And a => a.Arms,
                    Or o => o.Arms,
                    Not n => new[] { n.Operand },
                    _ => Array.Empty<SqlPredicate>(),
                };

                foreach (var arm in inner)
                    foreach (var term in arm.Subterms)
                        yield return term;
            }
        }

        /// <summary>
        /// Gets every document path the predicate reads.
        /// </summary>
        public IEnumerable<JsonDocumentPath> Paths => Subterms.SelectMany(term => term switch
        {
            Compare c => new[] { c.Left.PathRead, c.Right.PathRead },
            In i => new[] { i.Value.PathRead },
            IsNull n => new[] { n.Value.PathRead },
            IsDefined d => new[] { d.Value.PathRead },
            _ => Array.Empty<JsonDocumentPath?>(),
        }).OfType<JsonDocumentPath>().Distinct();

    }

    /// <summary>
    /// A filter's conjuncts as the oracle reads them: each in the IR, beside what it was loaded from, and the paths the
    /// statement reads or will be asked about.
    /// </summary>
    /// <remarks>
    /// The conjuncts keep the filter's order, which is what a question names one by. The digest does not: it is the
    /// conjuncts' digests in order, and the paths, so that two filters differing only in the order of their conjuncts
    /// are answered by one tree of contexts.
    /// </remarks>
    internal sealed class PredicateStatement
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="conjuncts">The conjuncts, in the filter's order.</param>
        /// <param name="sources">What each conjunct was loaded from — a <c>RexNode</c> — for a renderer to render; or <c>null</c>.</param>
        /// <param name="mentions">Paths a question will be asked about beside those the conjuncts read.</param>
        public PredicateStatement(IReadOnlyList<SqlPredicate> conjuncts, IReadOnlyList<object?>? sources = null, IEnumerable<JsonDocumentPath>? mentions = null)
        {
            if (conjuncts is null)
                throw new ArgumentNullException(nameof(conjuncts));

            if (sources is not null && sources.Count != conjuncts.Count)
                throw new ArgumentException("One source per conjunct.", nameof(sources));

            Conjuncts = conjuncts.ToArray();
            Sources = sources?.ToArray() ?? new object?[conjuncts.Count];
            Paths = Conjuncts.SelectMany(c => c.Paths).Concat(mentions ?? Array.Empty<JsonDocumentPath>()).Distinct().ToArray();
            Digest = string.Join(" & ", Conjuncts.Select(c => c.Digest).OrderBy(d => d, StringComparer.Ordinal))
                + " @ " + string.Join(" ", Paths.Select(SqlValue.Spell).OrderBy(p => p, StringComparer.Ordinal));
        }

        /// <summary>
        /// Gets the conjuncts, in the filter's order.
        /// </summary>
        public IReadOnlyList<SqlPredicate> Conjuncts { get; }

        /// <summary>
        /// Gets what each conjunct was loaded from, parallel to <see cref="Conjuncts"/>.
        /// </summary>
        public IReadOnlyList<object?> Sources { get; }

        /// <summary>
        /// Gets every path the statement reads or mentions.
        /// </summary>
        public IReadOnlyList<JsonDocumentPath> Paths { get; }

        /// <summary>
        /// Gets the canonical digest: the conjuncts and the paths, each in order.
        /// </summary>
        public string Digest { get; }

        /// <inheritdoc />
        public override string ToString() => Digest;

    }

}
