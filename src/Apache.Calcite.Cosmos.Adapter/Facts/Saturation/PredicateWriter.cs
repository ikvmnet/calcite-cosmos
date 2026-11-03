using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// Writes the loader's IR as <see cref="PredicateVocabulary"/>'s terms, so that every graph holding the vocabulary
    /// reads one conjunct as the same term.
    /// </summary>
    /// <remarks>
    /// Beside a comparison or a membership with literals it always states each literal as a <c>point</c>. What a junction's
    /// arms mean is the caller's — the facts graph numbers them for its split, the rendering graph records each — so each
    /// junction goes to the callback with the truth value its arms decide it at (TRUE for a disjunction, FALSE for a
    /// conjunction) and its arms, flattened over its right-nesting. The other callback, where one is given, hears every
    /// leaf — a predicate that is no junction or negation — with the value it is: a comparison, a membership or a test as
    /// the call of its operator over its operands (<see cref="In"/>, <see cref="IsNull"/>, <see cref="IsDefined"/>, a type
    /// test's name, <c>LIKE</c> over its value and its pattern), a <c>CASE</c> as its three predicates and a <c>CASE</c> of
    /// values as the call of <c>CASE</c> over its arms, and
    /// an opaque predicate as the value the loader read it as, where one is given; and every call, with its operands. A
    /// truth value is a leaf with no value. Which of those calls is null where an operand is, is rows', never the
    /// writer's. The facts graph passes neither, and the writer then builds what it always built.
    /// <para>
    /// The rendering graph alone passes two more: how a leaf tagged with an arm's stratum is written, the callback then
    /// hearing the tagged term in its leaf's place, and how a <c>CASE</c> is written, at the stratum it stands beneath
    /// (zero for none). The facts graph writes neither shape: a tag reaches it never, and a <c>CASE</c> it writes as the
    /// shared <c>case</c>.
    /// </para>
    /// </remarks>
    internal sealed class PredicateWriter
    {

        readonly PredicateVocabulary _ir;
        readonly Func<JsonDocumentPath, Term<JPath>> _path;
        readonly Func<ConstructorTerm<Pred>, bool, IReadOnlyList<ConstructorTerm<Pred>>, IEnumerable<Fact>>? _junction;
        readonly Func<Term, IReadOnlyList<ConstructorTerm<Val>>, IEnumerable<Fact>>? _leaf;
        readonly IReadOnlyDictionary<long, SqlValue>? _opaques;
        readonly Func<long, ConstructorTerm<Pred>, ConstructorTerm<Pred>>? _beneath;
        readonly Func<long, SqlPredicate.Case, List<Fact>, ConstructorTerm<Pred>>? _case;
        readonly Func<SqlValue.Call, ConstructorTerm<Val>, IReadOnlyList<ConstructorTerm<Val>>, IEnumerable<Fact>>? _computed;

        /// <summary>The names a membership, a null test and a presence test are read by as calls: Calcite's operators'.</summary>
        public const string In = "IN", IsNull = "IS NULL", IsDefined = "IS_DEFINED";

        public PredicateWriter(PredicateVocabulary ir, Func<JsonDocumentPath, Term<JPath>> path,
            Func<ConstructorTerm<Pred>, bool, IReadOnlyList<ConstructorTerm<Pred>>, IEnumerable<Fact>>? junction = null,
            Func<Term, IReadOnlyList<ConstructorTerm<Val>>, IEnumerable<Fact>>? leaf = null, IReadOnlyDictionary<long, SqlValue>? opaques = null,
            Func<long, ConstructorTerm<Pred>, ConstructorTerm<Pred>>? beneath = null, Func<long, SqlPredicate.Case, List<Fact>, ConstructorTerm<Pred>>? @case = null,
            Func<SqlValue.Call, ConstructorTerm<Val>, IReadOnlyList<ConstructorTerm<Val>>, IEnumerable<Fact>>? computed = null)
        {
            _ir = ir;
            _path = path;
            _junction = junction;
            _leaf = leaf;
            _computed = computed;
            _opaques = opaques;
            _beneath = beneath;
            _case = @case;
        }

        /// <summary>
        /// Builds a predicate's term, and states beside it the points of every atom in it and what the callbacks state of
        /// every junction, leaf and call.
        /// </summary>
        public ConstructorTerm<Pred> Term(SqlPredicate predicate, List<Fact> bookkeeping) => Term(predicate, bookkeeping, null);

        /// <summary>A predicate's term, a leaf of it tagged where <paramref name="tag"/> names a stratum.</summary>
        ConstructorTerm<Pred> Term(SqlPredicate predicate, List<Fact> bookkeeping, long? tag)
        {
            var r = _ir;
            switch (predicate)
            {
                case SqlPredicate.Beneath { Leaf: SqlPredicate.Case c } b when _case is not null && tag is null:
                    return _case(b.Stratum, c, bookkeeping);
                case SqlPredicate.Beneath b when _beneath is not null && tag is null && b.Leaf is not (SqlPredicate.And or SqlPredicate.Or or SqlPredicate.Not or SqlPredicate.Beneath):
                    return Term(b.Leaf, bookkeeping, b.Stratum);
                case SqlPredicate.Beneath b:
                    throw new NotSupportedException($"{b}: only the rendering graph tags a leaf, once.");
                case SqlPredicate.Case c when _case is not null && tag is null:
                    return _case(0, c, bookkeeping);
                case SqlPredicate.Compare c:
                {
                    var compare = r.Compare.Of(c.Op, Value(c.Left), Value(c.Right));
                    if (c.Right is SqlValue.Literal literal)
                        bookkeeping.Add(r.Point.Of(compare, literal.Value));

                    return _leaf is null ? Tagged(compare, tag) : Leaf(compare, new SqlValue.Call(c.Op, [c.Left, c.Right]), bookkeeping, tag);
                }
                case SqlPredicate.In i:
                {
                    var membership = r.In.Of(Value(i.Value), i.Points);
                    foreach (var point in i.Points)
                        bookkeeping.Add(r.Point.Of(membership, point));

                    return _leaf is null ? Tagged(membership, tag) : Leaf(membership, new SqlValue.Call(In, [i.Value]), bookkeeping, tag);
                }
                case SqlPredicate.IsNull n:
                    return _leaf is null ? Tagged(r.IsNull.Of(Value(n.Value)), tag) : Leaf(r.IsNull.Of(Value(n.Value)), new SqlValue.Call(IsNull, [n.Value]), bookkeeping, tag);
                case SqlPredicate.IsDefined d:
                    return _leaf is null ? Tagged(r.IsDefined.Of(Value(d.Value)), tag) : Leaf(r.IsDefined.Of(Value(d.Value)), new SqlValue.Call(IsDefined, [d.Value]), bookkeeping, tag);
                case SqlPredicate.TypeTest t:
                    return _leaf is null ? Tagged(r.TypeTest.Of(t.Test, Value(t.Value)), tag) : Leaf(r.TypeTest.Of(t.Test, Value(t.Value)), new SqlValue.Call(t.Test, [t.Value]), bookkeeping, tag);
                case SqlPredicate.Affix a:
                {
                    var affix = r.Affix.Of(a.Op, Value(a.Value), a.Pattern);
                    return _leaf is null ? Tagged(affix, tag) : Leaf(affix, new SqlValue.Call(a.Op, [a.Value, new SqlValue.Literal(JsonValues.Of(a.Pattern))]), bookkeeping, tag);
                }
                case SqlPredicate.Case c:
                    return Tagged(r.Case.Of(Term(c.Condition, bookkeeping), Term(c.Then, bookkeeping), Term(c.Else, bookkeeping)), tag);
                case SqlPredicate.Constant k:
                    return _leaf is null ? Tagged(r.Truth.Of(k.Name), tag) : Leaf(r.Truth.Of(k.Name), null, bookkeeping, tag);
                case SqlPredicate.Not n:
                    return r.Not.Of(Term(n.Operand, bookkeeping));
                case SqlPredicate.Opaque o:
                    return _leaf is null ? Tagged(r.Opaque.Of(o.Id), tag) : Leaf(r.Opaque.Of(o.Id), _opaques?.GetValueOrDefault(o.Id), bookkeeping, tag);
                case SqlPredicate.And a:
                    return Junction(Flatten(a.Arms, x => (x as SqlPredicate.And)?.Arms), r.And, truth: false, bookkeeping);
                case SqlPredicate.Or o:
                    return Junction(Flatten(o.Arms, x => (x as SqlPredicate.Or)?.Arms), r.Or, truth: true, bookkeeping);
                default:
                    throw new NotSupportedException(predicate.GetType().Name);
            }
        }

        /// <summary>
        /// Builds a junction right-nested, and hands the callback it and each junction it nests, each with its own arms.
        /// </summary>
        ConstructorTerm<Pred> Junction(IReadOnlyList<SqlPredicate> arms, Constructor<Pred, Pred, Pred> make, bool truth, List<Fact> bookkeeping)
        {
            var terms = arms.Select(arm => Term(arm, bookkeeping)).ToArray();
            var junction = terms[^1];

            for (var k = terms.Length - 2; k >= 0; k--)
            {
                junction = make.Of(terms[k], junction);
                if (_junction is not null)
                    bookkeeping.AddRange(_junction(junction, truth, terms[k..]));
            }

            return junction;
        }

        static IReadOnlyList<SqlPredicate> Flatten(IReadOnlyList<SqlPredicate> arms, Func<SqlPredicate, IReadOnlyList<SqlPredicate>?> nested)
        {
            var flat = new List<SqlPredicate>();
            foreach (var arm in arms)
            {
                if (nested(arm) is { } inner)
                    flat.AddRange(Flatten(inner, nested));
                else
                    flat.Add(arm);
            }

            return flat;
        }

        /// <summary>
        /// Builds a value's term: a reading of a path, a cast, a literal, a parameter, or a call over its operands' terms.
        /// </summary>
        public ConstructorTerm<Val> Value(SqlValue value)
        {
            var r = _ir;
            return value switch
            {
                SqlValue.Text t => r.Text.Of(_path(t.Path)),
                SqlValue.Raw w => r.Raw.Of(_path(w.Path)),
                SqlValue.Typed t => r.Typed.Of(_path(t.Path), t.Type),
                SqlValue.Cast c => r.Cast.Of(c.Target, Value(c.Value)),
                SqlValue.Literal l => r.Literal.Of(l.Value),
                SqlValue.Parameter p => r.Parameter.Of(p.Ordinal, p.Type),
                SqlValue.Call c => r.Call.Of(c.Function, Operands(c.Operands, 0)),
                SqlValue.Case c => r.Call.Of("CASE", Operands(c.Arms, 0)),
                _ => throw new NotSupportedException(value.GetType().Name),
            };
        }

        ConstructorTerm<Args> Operands(IReadOnlyList<SqlValue> operands, int from) =>
            from == operands.Count ? _ir.NoOperands.Of() : _ir.Operand.Of(Value(operands[from]), Operands(operands, from + 1));

        /// <summary>
        /// Hands the leaf callback a predicate that is no junction or negation, tagged where a stratum is named, with the
        /// value it is, where there is one.
        /// </summary>
        ConstructorTerm<Pred> Leaf(ConstructorTerm<Pred> leaf, SqlValue? value, List<Fact> bookkeeping, long? tag)
        {
            var written = Tagged(leaf, tag);
            bookkeeping.AddRange(_leaf!(written, value is null ? [] : [Read(value, bookkeeping)]));
            return written;
        }

        ConstructorTerm<Pred> Tagged(ConstructorTerm<Pred> leaf, long? tag) => tag is { } stratum ? _beneath!(stratum, leaf) : leaf;

        /// <summary>A value's term as <see cref="Value"/> builds it, every call in it handed to the leaf callback with its operands.</summary>
        ConstructorTerm<Val> Read(SqlValue value, List<Fact> bookkeeping)
        {
            switch (value)
            {
                case SqlValue.Call or SqlValue.Case:
                    var (function, arms) = value is SqlValue.Call called ? (called.Function, called.Operands) : ("CASE", ((SqlValue.Case)value).Arms);
                    var operands = arms.Select(operand => Read(operand, bookkeeping)).ToList();
                    var call = _ir.Call.Of(function, Operands(operands, 0));
                    bookkeeping.AddRange(_leaf!(call, operands));
                    if (value is SqlValue.Call { Type: not null } typed && _computed is not null)
                        bookkeeping.AddRange(_computed(typed, call, operands));
                    return call;
                case SqlValue.Cast c:
                    return _ir.Cast.Of(c.Target, Read(c.Value, bookkeeping));
                default:
                    return Value(value);
            }
        }

        ConstructorTerm<Args> Operands(IReadOnlyList<ConstructorTerm<Val>> operands, int from) =>
            from == operands.Count ? _ir.NoOperands.Of() : _ir.Operand.Of(operands[from], Operands(operands, from + 1));

    }

}
