using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.fun;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// Writes a weakening S chose as the Rex the split rule pushes (spec g14): writing code, total over
    /// <see cref="RenderingRexGrammar"/>, which decides nothing about a document and never leaves a member out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Over the conjunct's own accessors (<see cref="Accessor"/>): a comparison with a string over the path's raw value,
    /// and with a number or a boolean over its accessor typed as one, as it stands; a type test as its
    /// <see cref="CosmosOperators"/> call over the raw value; a constant, <c>NOT</c>, <c>AND</c> and <c>OR</c> as Calcite's,
    /// in the form's order; an <c>IN</c> as its points' equalities, the membership the loader reads back.
    /// </para>
    /// <para>
    /// <b>Never a member left out.</b> Beneath an <c>OR</c> or an odd number of <c>NOT</c>s a member left out strengthens
    /// the weakening and drops rows Calcite keeps. A node outside the grammar — a path no accessor reads, a literal its
    /// accessor holds no class of, a bare path, an opaque form — makes the whole weakening <c>null</c>, and none is pushed:
    /// sound, since each residual's weakening is its own conjunct of the filter beneath. S admits into <c>implied</c> only
    /// what the grammar writes, so only a fault makes one. That the Rex means its form is held in CI over the whole grammar
    /// and every weakening the check run's pass would push (<c>DESIGN.md</c>, <i>The weakening as a Rex</i>).
    /// </para>
    /// </remarks>
    internal static class RenderingRexBuilder
    {

        /// <summary>
        /// A path as a conjunct reads it: its raw value, which a string and a type test are written over; and an accessor
        /// typed as a number or a boolean, which a literal of its type is compared through. Either is <c>null</c> where the
        /// conjunct reads none, and the typed one where it reads the path through two SQL types.
        /// </summary>
        internal sealed record Accessor(RexNode? Raw, RexNode? Typed);

        /// <summary>Builds a weakening's Rex, or <c>null</c> where a node of it is outside the grammar.</summary>
        /// <param name="weakening">S's weakening of a residual conjunct.</param>
        /// <param name="rexBuilder">Builds the nodes, over the conjunct's types.</param>
        /// <param name="accessors">Each path the conjunct reads, by its spelling, and how it reads it.</param>
        public static RexNode? Build(ServiceForm weakening, RexBuilder rexBuilder, IReadOnlyDictionary<string, Accessor> accessors)
        {
            RexNode? Write(ServiceForm f) => f.Kind switch
            {
                ServiceFormKind.Test => TestOf(f.Test) is { } test && Of(f.Path!) is { } a && (a.Raw ?? a.Typed) is { } value ? rexBuilder.makeCall(test, value) : null,
                ServiceFormKind.Compare => Comparison(f.Test, f.Path!, f.Literal!),
                ServiceFormKind.In => Junction(SqlStdOperatorTable.OR, [.. f.Literals.Select(point => Comparison(CosmosServiceTest.Equal, f.Path!, point))]),
                ServiceFormKind.Constant => rexBuilder.makeLiteral(f.Literal == JsonValues.Of(true)),
                ServiceFormKind.Not => Write(f.Members[0]) is { } operand ? rexBuilder.makeCall(SqlStdOperatorTable.NOT, operand) : null,
                ServiceFormKind.And => Junction(SqlStdOperatorTable.AND, [.. f.Members.Select(Write)]),
                ServiceFormKind.Or => Junction(SqlStdOperatorTable.OR, [.. f.Members.Select(Write)]),
                _ => null,
            };

            Accessor? Of(string path) => accessors.TryGetValue(path, out var accessor) ? accessor : null;

            // Every member written or none: a junction with one unwritten is no junction of the form's.
            RexNode? Junction(SqlOperator connective, IReadOnlyList<RexNode?> members)
            {
                if (members.Count == 0 || members.Any(m => m is null))
                    return null;

                return members.Count == 1 ? members[0] : rexBuilder.makeCall(connective, members.Select(m => m!).ToArray());
            }

            RexNode? Comparison(CosmosServiceTest op, string path, string literal)
            {
                if (ComparisonOf(op) is not { } comparison || Of(path) is not { } accessor)
                    return null;

                var typed = accessor.Typed is { } t ? RenderingRexGrammar.ClassOf(PredicateAtom.TypedReader(t.getType()?.getSqlTypeName()?.getName() ?? "")) : null;
                return JsonValues.TypeOf(literal) switch
                {
                    JsonValues.StringType when accessor.Raw is { } raw => rexBuilder.makeCall(comparison, raw, rexBuilder.makeLiteral(JsonValues.StringOf(literal))),
                    JsonValues.IntegerType or JsonValues.FractionType when typed == RenderingRexGrammar.NumberReader => rexBuilder.makeCall(comparison, accessor.Typed, Number(rexBuilder, literal)),
                    JsonValues.BooleanType when typed == RenderingRexGrammar.BooleanReader => rexBuilder.makeCall(comparison, accessor.Typed, rexBuilder.makeLiteral(literal == JsonValues.Of(true))),
                    _ => null,
                };
            }

            return Write(weakening);
        }

        /// <summary>
        /// A number as the exact literal it is, which the loader reads back as itself; one a decimal cannot hold as the
        /// double it is spelled by, which the loader keeps approximate.
        /// </summary>
        static RexNode Number(RexBuilder rexBuilder, string literal) => JsonValues.ApproximateOf(literal) is { } approximate
            ? rexBuilder.makeApproxLiteral(new java.math.BigDecimal(approximate.ToString("R", CultureInfo.InvariantCulture)))
            : rexBuilder.makeExactLiteral(new java.math.BigDecimal(JsonValues.NumberOf(literal).ToString(CultureInfo.InvariantCulture)));

        static SqlOperator? ComparisonOf(CosmosServiceTest op) => op switch
        {
            CosmosServiceTest.Equal => SqlStdOperatorTable.EQUALS,
            CosmosServiceTest.NotEqual => SqlStdOperatorTable.NOT_EQUALS,
            CosmosServiceTest.Less => SqlStdOperatorTable.LESS_THAN,
            CosmosServiceTest.LessOrEqual => SqlStdOperatorTable.LESS_THAN_OR_EQUAL,
            CosmosServiceTest.Greater => SqlStdOperatorTable.GREATER_THAN,
            CosmosServiceTest.GreaterOrEqual => SqlStdOperatorTable.GREATER_THAN_OR_EQUAL,
            _ => null,
        };

        static SqlOperator? TestOf(CosmosServiceTest test) => test switch
        {
            CosmosServiceTest.IsDefined => CosmosOperators.IsDefined,
            CosmosServiceTest.IsNull => CosmosOperators.IsNull,
            CosmosServiceTest.IsPrimitive => CosmosOperators.IsPrimitive,
            CosmosServiceTest.IsString => CosmosOperators.IsString,
            CosmosServiceTest.IsNumber => CosmosOperators.IsNumber,
            CosmosServiceTest.IsBool => CosmosOperators.IsBool,
            CosmosServiceTest.IsObject => CosmosOperators.IsObject,
            CosmosServiceTest.IsArray => CosmosOperators.IsArray,
            _ => null,
        };

        /// <summary>
        /// Names the raw value at the path a rendering reads, so that a comparison written over it is
        /// the one the service makes.
        /// </summary>
        /// <remarks>
        /// Typed <c>ANY</c>, which is what says "the raw value at this path" rather than "its
        /// rendering": written over the accessor as it stands the translator would decline the
        /// comparison, rightly, since that node means the rendering everywhere else. An accessor is
        /// re-typed in place. A field bound to one is cast instead: a reference re-typed says the same
        /// thing, but a host transposing the filter through the projection replaces the reference with
        /// the projection's expression, and the type that said it goes with it. A cast is a call of
        /// its own and survives, and the translator renders a cast to <c>ANY</c> over a rendering as the
        /// path.
        /// </remarks>
        internal static RexNode RawValue(RexBuilder rexBuilder, RexNode accessor)
        {
            var any = rexBuilder.getTypeFactory().createSqlType(org.apache.calcite.sql.type.SqlTypeName.ANY);

            return accessor is RexCall call
                ? rexBuilder.makeCall(any, call.getOperator(), call.getOperands())
                : rexBuilder.makeCast(any, accessor);
        }

    }

}
