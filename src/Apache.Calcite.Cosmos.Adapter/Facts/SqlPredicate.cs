using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts
{

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

        /// <summary>
        /// One of the service's <c>IS_*</c> tests but <c>IS_DEFINED</c>, of the path a value reads: what it asks is the
        /// document's kind, true or false and never UNKNOWN (<c>Calcite.TypeTestsAreTheirBodies</c>), so the loader names
        /// the raw value.
        /// </summary>
        public sealed record TypeTest(string Test, SqlValue Value) : SqlPredicate
        {
            protected override string Spell() => "(" + Test + " " + Value + ")";
        }

        /// <summary>
        /// <c>CASE WHEN Condition THEN Then ELSE Else END</c> over predicates: TRUE, FALSE or UNKNOWN as the arm the first
        /// TRUE condition takes, the else-arm where the condition is FALSE or UNKNOWN (<c>Calcite.CaseTakesTheFirstTrueArm</c>).
        /// A <c>CASE</c> of more arms is one nested in its else-arm, and the truth tests and <c>COALESCE</c> are cases.
        /// </summary>
        public sealed record Case(SqlPredicate Condition, SqlPredicate Then, SqlPredicate Else) : SqlPredicate
        {
            protected override string Spell() => "case(" + Condition + ", " + Then + ", " + Else + ")";
        }

        /// <summary>
        /// A match of the text a value reads against a literal pattern: <c>LIKE</c>, with no <c>ESCAPE</c> and no bracket.
        /// </summary>
        public sealed record Affix(string Op, SqlValue Value, string Pattern) : SqlPredicate
        {
            protected override string Spell() => "(" + Value + " " + Op + " " + SqlValue.Quote(Pattern) + ")";
        }

        /// <summary>A truth value: TRUE, FALSE, or UNKNOWN for <c>null</c>.</summary>
        public sealed record Constant(bool? Truth) : SqlPredicate
        {

            /// <summary>The truth value's name, as the shared vocabulary's <c>truth</c> spells it.</summary>
            public string Name => Truth switch { true => "true", false => "false", null => "unknown" };

            protected override string Spell() => Name;

        }

        /// <summary>
        /// A leaf of an arm of a <c>CASE</c> S writes, tagged with the stratum its bundle opened for the arm: written by the
        /// bundle alone, never by the loader nor to the facts graph, so that one leaf spelled alike in a sibling and in an
        /// arm, or in two arms, is two terms and two atoms, each read beneath its own stratum.
        /// </summary>
        public sealed record Beneath(long Stratum, SqlPredicate Leaf) : SqlPredicate
        {
            protected override string Spell() => "beneath#" + Stratum + "(" + Leaf + ")";
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
                    Case c => new[] { c.Condition, c.Then, c.Else },
                    Beneath b => new[] { b.Leaf },
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
            TypeTest t => new[] { t.Value.PathRead },
            Affix a => new[] { a.Value.PathRead },
            _ => Array.Empty<JsonDocumentPath?>(),
        }).OfType<JsonDocumentPath>().Distinct();

    }

}
