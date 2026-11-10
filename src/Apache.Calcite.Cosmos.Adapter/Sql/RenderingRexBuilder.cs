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
    /// Over the conjunct's own accessors (<see cref="Accessor"/>): a comparison with a string over the path's raw value, an
    /// ordering beside a <c>VARIANT</c> field over the raw value of the path's text, or where the conjunct reads none,
    /// through its own text of the field, a cast to a character type (<c>CAST($4 AS VARCHAR)</c>), or the field itself, and
    /// with a number or a boolean over the accessor of the reader it was judged over (<see cref="ServiceForm.Reader"/>),
    /// as it stands; a number through a cast of the text, <c>RETURNING DECIMAL</c> or the text as its guarded pair,
    /// <c>CASE WHEN IS_NUMBER(raw) THEN acc op n ELSE FALSE END</c> in an <c>AND</c> beside <c>IS_NUMBER(p)</c> and
    /// <c>… ELSE TRUE END</c> in an <c>OR</c> beside <c>NOT IS_NUMBER(p)</c>, the guard then left to the <c>CASE</c>, so
    /// that Calcite parses no string and the Rex means the form on an absent path too; a type test as its
    /// <see cref="CosmosOperators"/> call over the path's accessor re-typed <c>ANY</c> (<see cref="Accessor.Tested"/>), the
    /// document column's where the conjunct reads the path through a field, an item or a cast (h15b), and where the input
    /// holds no document column, or the place is a call's, over the field or the call; a constant, <c>NOT</c>, <c>AND</c> and <c>OR</c> as Calcite's,
    /// in the form's order, and the ternary as a <c>CASE</c>; an <c>IN</c> as its points' equalities, the membership the
    /// loader reads back. No carrier operator: what reaches the service is the form S proves of the filter beneath, which
    /// drops what it declines (<c>CosmosFilterSplitRule.Admitted</c>), a test over a node the body raises over among it, so
    /// nothing here declines a member for what Calcite would answer of it.
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
        /// A path as a conjunct reads it: its raw value, which a string and a type test are written over, <c>null</c> where
        /// the conjunct reads none; and each node it reads the path through, by its reader (<c>typed:INTEGER</c>,
        /// <c>cast:DOUBLE:text</c>, <c>text</c>), which a member judged over that reader is written through.
        /// </summary>
        internal sealed record Accessor(RexNode? Raw, IReadOnlyDictionary<string, RexNode> Readers)
        {

            /// <summary>
            /// The path's accessor re-typed <c>ANY</c> over the input's document column, <c>JSON_VALUE(DOC, path)</c>, which the
            /// in-process body takes apart into the document and the path; <c>null</c> where the input holds no document
            /// column, and for a call's place, which is no path (h15b).
            /// </summary>
            public RexNode? Retyped { get; init; }

            /// <summary>
            /// The node a type test, and a guarded pair's <c>IS_NUMBER</c>, is written over: the path's accessor re-typed
            /// <c>ANY</c> as the conjunct reads it, or the document column's (<see cref="Retyped"/>), or one typed as a number or
            /// a boolean, each a <c>JSON_VALUE</c> or <c>JSON_QUERY</c> the in-process body takes apart into the document and
            /// the path, which the filter beneath reads back as a test of the path (h15b); and where there is none, the raw
            /// value as it stands, a field or a call, which it reads back as a test of that, its body's raise a row
            /// (<c>Calcite.TypeTestsAreTheirBodies</c>) S declines there and the split rule drops; <c>null</c> only where the
            /// conjunct reads no node of the path.
            /// </summary>
            public RexNode? Tested => TakenApart(Raw) ? Raw : TakenApart(Retyped) ? Retyped
                : Readers.Where(r => RenderingRexGrammar.TypeTests.ContainsKey(r.Key)).OrderBy(r => r.Key, System.StringComparer.Ordinal).Select(r => r.Value).FirstOrDefault(TakenApart)
                    ?? Raw ?? Readers.OrderBy(r => r.Key, System.StringComparer.Ordinal).Select(r => r.Value).FirstOrDefault();

            /// <summary>Whether the in-process body takes a node apart into the document and a path: an accessor, which the loader reads back as the path's.</summary>
            static bool TakenApart(RexNode? node) => node is RexCall call && call.getOperator().getName() is "JSON_VALUE" or "JSON_QUERY";

        }

        /// <summary>Builds a weakening's Rex, or <c>null</c> where a node of it is outside the grammar.</summary>
        /// <param name="weakening">S's weakening of a residual conjunct.</param>
        /// <param name="rexBuilder">Builds the nodes, over the conjunct's types.</param>
        /// <param name="accessors">Each path the conjunct reads, by its spelling, and how it reads it.</param>
        public static RexNode? Build(ServiceForm weakening, RexBuilder rexBuilder, IReadOnlyDictionary<string, Accessor> accessors)
        {
            RexNode? Write(ServiceForm f) => f.Kind switch
            {
                ServiceFormKind.Test => TestOf(f.Test) is { } test && Of(f.Path!)?.Tested is { } value ? rexBuilder.makeCall(test, value) : null,
                ServiceFormKind.Compare => Comparison(f.Test, f.Path!, f.Literal!, f.Reader),
                ServiceFormKind.In => Junction(SqlStdOperatorTable.OR, [.. f.Literals.Select(point => Comparison(CosmosServiceTest.Equal, f.Path!, point, f.Reader))]),
                ServiceFormKind.Constant => rexBuilder.makeLiteral(f.Literal == JsonValues.Of(true)),
                ServiceFormKind.Not => Write(f.Members[0]) is { } operand ? rexBuilder.makeCall(SqlStdOperatorTable.NOT, operand) : null,
                ServiceFormKind.And or ServiceFormKind.Or => Guarded(f),
                ServiceFormKind.Choose => Junction(SqlStdOperatorTable.CASE, [.. f.Members.Select(Write)]),
                _ => null,
            };

            Accessor? Of(string path) => accessors.TryGetValue(path, out var accessor) ? accessor : null;

            // A junction's members, each comparison its guard decides written as the guarded CASE and the guard left out.
            RexNode? Guarded(ServiceForm f)
            {
                var (guards, otherwise, cased) = (RenderingRexGrammar.GuardsOf(f), f.Kind == ServiceFormKind.Or, new HashSet<string>(System.StringComparer.Ordinal));
                var written = new List<RexNode?>();
                foreach (var m in f.Members)
                {
                    var guarded = m.Kind == ServiceFormKind.Compare && guards.Contains(m.Path!) && Comparison(m.Test, m.Path!, m.Literal!, m.Reader) is null;
                    if (guarded)
                        cased.Add(m.Path!);
                    written.Add(guarded ? Case(m, otherwise) : Write(m));
                }

                return Junction(otherwise ? SqlStdOperatorTable.OR : SqlStdOperatorTable.AND, [.. written.Where((_, i) => IsGuard(f.Members[i], otherwise, cased) == false)]);
            }

            static bool IsGuard(ServiceForm member, bool otherwise, ISet<string> cased) => (otherwise ? member.Kind == ServiceFormKind.Not ? member.Members[0] : null : member) is
                { Kind: ServiceFormKind.Test, Test: CosmosServiceTest.IsNumber, Path: var p } && cased.Contains(p!);

            // CASE WHEN IS_NUMBER(raw) THEN acc op n ELSE FALSE|TRUE END, over the reader the member was judged over: the guard
            // over the raw value the conjunct holds, or the document column's (h15b), never a typed accessor's, which raises.
            RexNode? Case(ServiceForm member, bool otherwise)
            {
                if (ComparisonOf(member.Test) is not { } comparison || Of(member.Path!) is not { Tested: { } raw } accessor
                    || (ReferenceEquals(raw, accessor.Raw) || ReferenceEquals(raw, accessor.Retyped)) == false || member.Reader is not { } reader
                    || RenderingRexGrammar.Written.IsGuarded(reader, member.Literal!) == false || accessor.Readers.GetValueOrDefault(reader) is not { } node)
                    return null;

                var number = reader == PredicateAtom.TextReader
                    ? rexBuilder.makeCast(rexBuilder.getTypeFactory().createTypeWithNullability(rexBuilder.getTypeFactory().createSqlType(org.apache.calcite.sql.type.SqlTypeName.DOUBLE), true), node)
                    : node;
                return rexBuilder.makeCall(SqlStdOperatorTable.CASE, rexBuilder.makeCall(CosmosOperators.IsNumber, raw),
                    rexBuilder.makeCall(comparison, number, Number(rexBuilder, member.Literal!)), rexBuilder.makeLiteral(otherwise));
            }

            // A string as Calcite writes one beside the raw value: boxed beside a VARIANT field, which Calcite compares with a
            // bare string on no row it reads (raw VARIANT = lit), so that the filter the weakening is pushed in, reading it
            // back, reads the comparison its own conjunct holds; an ordering over the raw value of the path's text, as any
            // other path's, or where the conjunct reads no text of it, through its own cast of the field to a character type
            // (h15), or where neither, over the field, which the filter beneath reads back as the raw column's ordering.
            RexNode? StringComparison(SqlOperator comparison, RexNode raw, Accessor accessor, string literal)
            {
                var text = rexBuilder.makeLiteral(JsonValues.StringOf(literal));
                if (raw.getType()?.getSqlTypeName() != org.apache.calcite.sql.type.SqlTypeName.VARIANT)
                    return rexBuilder.makeCall(comparison, raw, text);

                if (ReferenceEquals(comparison, SqlStdOperatorTable.EQUALS) || ReferenceEquals(comparison, SqlStdOperatorTable.NOT_EQUALS))
                    return rexBuilder.makeCall(comparison, raw, rexBuilder.makeCast(rexBuilder.getTypeFactory().createSqlType(org.apache.calcite.sql.type.SqlTypeName.VARIANT), text));

                if (accessor.Readers.GetValueOrDefault(PredicateAtom.TextReader) is { } read)
                    return rexBuilder.makeCall(comparison, RawValue(rexBuilder, read), text);

                return rexBuilder.makeCall(comparison, accessor.Readers.OrderBy(r => r.Key, System.StringComparer.Ordinal).Select(r => r.Value)
                    .FirstOrDefault(n => n.getType() is { } type && org.apache.calcite.sql.type.SqlTypeUtil.inCharFamily(type)) ?? raw, text);
            }

            // Every member written or none: a junction with one unwritten is no junction of the form's.
            RexNode? Junction(SqlOperator connective, IReadOnlyList<RexNode?> members)
            {
                if (members.Count == 0 || members.Any(m => m is null))
                    return null;

                return members.Count == 1 ? members[0] : rexBuilder.makeCall(connective, members.Select(m => m!).ToArray());
            }

            // A string over the raw value; a number or a boolean through the accessor of the reader judged, typed as one.
            RexNode? Comparison(CosmosServiceTest op, string path, string literal, string? reader)
            {
                if (ComparisonOf(op) is not { } comparison || Of(path) is not { } accessor)
                    return null;

                var typed = reader is not null && accessor.Readers.GetValueOrDefault(reader) is { } node ? (RenderingRexGrammar.ClassOf(reader), node) : (null, null);
                return JsonValues.TypeOf(literal) switch
                {
                    JsonValues.StringType when accessor.Raw is { } raw => StringComparison(comparison, raw, accessor, literal),
                    JsonValues.IntegerType or JsonValues.FractionType when typed.Item1 == RenderingRexGrammar.NumberReader => rexBuilder.makeCall(comparison, typed.node, Number(rexBuilder, literal)),
                    JsonValues.BooleanType when typed.Item1 == RenderingRexGrammar.BooleanReader => rexBuilder.makeCall(comparison, typed.node, rexBuilder.makeLiteral(literal == JsonValues.Of(true))),
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
