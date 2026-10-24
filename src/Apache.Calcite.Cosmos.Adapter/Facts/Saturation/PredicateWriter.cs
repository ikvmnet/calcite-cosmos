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
    /// the call of its operator over its operands (<see cref="In"/>, <see cref="IsNull"/>, <see cref="IsDefined"/>), and
    /// an opaque predicate as the value the loader read it as, where one is given; and every call, with its operands. Which
    /// of those calls is null where an operand is, is rows', never the writer's. The facts graph passes neither, and the
    /// writer then builds what it always built.
    /// </remarks>
    internal sealed class PredicateWriter
    {

        readonly PredicateVocabulary _ir;
        readonly Func<JsonDocumentPath, Term<JPath>> _path;
        readonly Func<ConstructorTerm<Pred>, bool, IReadOnlyList<ConstructorTerm<Pred>>, IEnumerable<Fact>>? _junction;
        readonly Func<Term, IReadOnlyList<ConstructorTerm<Val>>, IEnumerable<Fact>>? _leaf;
        readonly IReadOnlyDictionary<long, SqlValue>? _opaques;

        /// <summary>The names a membership, a null test and a presence test are read by as calls: Calcite's operators'.</summary>
        public const string In = "IN", IsNull = "IS NULL", IsDefined = "IS_DEFINED";

        public PredicateWriter(PredicateVocabulary ir, Func<JsonDocumentPath, Term<JPath>> path,
            Func<ConstructorTerm<Pred>, bool, IReadOnlyList<ConstructorTerm<Pred>>, IEnumerable<Fact>>? junction = null,
            Func<Term, IReadOnlyList<ConstructorTerm<Val>>, IEnumerable<Fact>>? leaf = null, IReadOnlyDictionary<long, SqlValue>? opaques = null)
        {
            _ir = ir;
            _path = path;
            _junction = junction;
            _leaf = leaf;
            _opaques = opaques;
        }

        /// <summary>
        /// Builds a predicate's term, and states beside it the points of every atom in it and what the callbacks state of
        /// every junction, leaf and call.
        /// </summary>
        public ConstructorTerm<Pred> Term(SqlPredicate predicate, List<Fact> bookkeeping)
        {
            var r = _ir;
            switch (predicate)
            {
                case SqlPredicate.Compare c:
                {
                    var compare = r.Compare.Of(c.Op, Value(c.Left), Value(c.Right));
                    if (c.Right is SqlValue.Literal literal)
                        bookkeeping.Add(r.Point.Of(compare, literal.Value));

                    return _leaf is null ? compare : Leaf(compare, new SqlValue.Call(c.Op, [c.Left, c.Right]), bookkeeping);
                }
                case SqlPredicate.In i:
                {
                    var membership = r.In.Of(Value(i.Value), i.Points);
                    foreach (var point in i.Points)
                        bookkeeping.Add(r.Point.Of(membership, point));

                    return _leaf is null ? membership : Leaf(membership, new SqlValue.Call(In, [i.Value]), bookkeeping);
                }
                case SqlPredicate.IsNull n:
                    return _leaf is null ? r.IsNull.Of(Value(n.Value)) : Leaf(r.IsNull.Of(Value(n.Value)), new SqlValue.Call(IsNull, [n.Value]), bookkeeping);
                case SqlPredicate.IsDefined d:
                    return _leaf is null ? r.IsDefined.Of(Value(d.Value)) : Leaf(r.IsDefined.Of(Value(d.Value)), new SqlValue.Call(IsDefined, [d.Value]), bookkeeping);
                case SqlPredicate.Not n:
                    return r.Not.Of(Term(n.Operand, bookkeeping));
                case SqlPredicate.Opaque o:
                    return _leaf is null ? r.Opaque.Of(o.Id) : Leaf(r.Opaque.Of(o.Id), _opaques?.GetValueOrDefault(o.Id), bookkeeping);
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
                _ => throw new NotSupportedException(value.GetType().Name),
            };
        }

        ConstructorTerm<Args> Operands(IReadOnlyList<SqlValue> operands, int from) =>
            from == operands.Count ? _ir.NoOperands.Of() : _ir.Operand.Of(Value(operands[from]), Operands(operands, from + 1));

        /// <summary>Hands the leaf callback a predicate that is no junction or negation, with the value it is, where there is one.</summary>
        ConstructorTerm<Pred> Leaf(ConstructorTerm<Pred> leaf, SqlValue? value, List<Fact> bookkeeping)
        {
            bookkeeping.AddRange(_leaf!(leaf, value is null ? [] : [Read(value, bookkeeping)]));
            return leaf;
        }

        /// <summary>A value's term as <see cref="Value"/> builds it, every call in it handed to the leaf callback with its operands.</summary>
        ConstructorTerm<Val> Read(SqlValue value, List<Fact> bookkeeping)
        {
            switch (value)
            {
                case SqlValue.Call c:
                    var operands = c.Operands.Select(operand => Read(operand, bookkeeping)).ToList();
                    var call = _ir.Call.Of(c.Function, Operands(operands, 0));
                    bookkeeping.AddRange(_leaf!(call, operands));
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
