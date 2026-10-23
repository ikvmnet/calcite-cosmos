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
    /// conjunction) and its arms, flattened over its right-nesting. The other callback, where one is given, hears the
    /// syntax of every leaf — a predicate that is no junction or negation, with the values a comparison or a membership is
    /// over — and of every call, with its operands.
    /// </remarks>
    internal sealed class PredicateWriter
    {

        readonly PredicateVocabulary _ir;
        readonly Func<JsonDocumentPath, Term<JPath>> _path;
        readonly Func<ConstructorTerm<Pred>, bool, IReadOnlyList<ConstructorTerm<Pred>>, IEnumerable<Fact>>? _junction;
        readonly Func<Term, IReadOnlyList<ConstructorTerm<Val>>, IEnumerable<Fact>>? _leaf;

        public PredicateWriter(PredicateVocabulary ir, Func<JsonDocumentPath, Term<JPath>> path,
            Func<ConstructorTerm<Pred>, bool, IReadOnlyList<ConstructorTerm<Pred>>, IEnumerable<Fact>>? junction = null,
            Func<Term, IReadOnlyList<ConstructorTerm<Val>>, IEnumerable<Fact>>? leaf = null)
        {
            _ir = ir;
            _path = path;
            _junction = junction;
            _leaf = leaf;
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
                    var (left, right) = (Value(c.Left, bookkeeping), Value(c.Right, bookkeeping));
                    var compare = r.Compare.Of(c.Op, left, right);
                    if (c.Right is SqlValue.Literal literal)
                        bookkeeping.Add(r.Point.Of(compare, literal.Value));

                    return Leaf(compare, [left, right], bookkeeping);
                }
                case SqlPredicate.In i:
                {
                    var value = Value(i.Value, bookkeeping);
                    var membership = r.In.Of(value, i.Points);
                    foreach (var point in i.Points)
                        bookkeeping.Add(r.Point.Of(membership, point));

                    return Leaf(membership, [value], bookkeeping);
                }
                case SqlPredicate.IsNull n:
                    return Leaf(r.IsNull.Of(Value(n.Value, bookkeeping)), [], bookkeeping);
                case SqlPredicate.IsDefined d:
                    return Leaf(r.IsDefined.Of(Value(d.Value, bookkeeping)), [], bookkeeping);
                case SqlPredicate.Not n:
                    return r.Not.Of(Term(n.Operand, bookkeeping));
                case SqlPredicate.Opaque o:
                    return Leaf(r.Opaque.Of(o.Id), [], bookkeeping);
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
        /// Builds a value's term: a reading of a path, a cast, a literal, a parameter, or a call over its operands' terms;
        /// where a list is given, stating in it what the leaf callback states of every call.
        /// </summary>
        public ConstructorTerm<Val> Value(SqlValue value, List<Fact>? bookkeeping = null)
        {
            var r = _ir;
            if (value is not SqlValue.Call c)
            {
                return value switch
                {
                    SqlValue.Text t => r.Text.Of(_path(t.Path)),
                    SqlValue.Raw w => r.Raw.Of(_path(w.Path)),
                    SqlValue.Typed t => r.Typed.Of(_path(t.Path), t.Type),
                    SqlValue.Cast cast => r.Cast.Of(cast.Target, Value(cast.Value, bookkeeping)),
                    SqlValue.Literal l => r.Literal.Of(l.Value),
                    SqlValue.Parameter p => r.Parameter.Of(p.Ordinal, p.Type),
                    _ => throw new NotSupportedException(value.GetType().Name),
                };
            }

            var operands = c.Operands.Select(operand => Value(operand, bookkeeping)).ToList();
            var call = r.Call.Of(c.Function, Operands(operands, 0));
            if (_leaf is not null && bookkeeping is not null)
                bookkeeping.AddRange(_leaf(call, operands));

            return call;
        }

        ConstructorTerm<Args> Operands(IReadOnlyList<ConstructorTerm<Val>> operands, int from) =>
            from == operands.Count ? _ir.NoOperands.Of() : _ir.Operand.Of(operands[from], Operands(operands, from + 1));

        /// <summary>Hands the leaf callback a predicate that is no junction or negation, with the values it compares or tests membership of.</summary>
        ConstructorTerm<Pred> Leaf(ConstructorTerm<Pred> leaf, IReadOnlyList<ConstructorTerm<Val>> over, List<Fact> bookkeeping)
        {
            if (_leaf is not null)
                bookkeeping.AddRange(_leaf(leaf, over));

            return leaf;
        }

    }

}
