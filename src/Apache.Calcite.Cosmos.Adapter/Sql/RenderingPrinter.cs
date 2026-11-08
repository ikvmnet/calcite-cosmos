using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// Writes a form S chose as the Cosmos SQL text sent (spec g13): writing code, which decides nothing about a document,
    /// held by the round trip with the reader, the parameter types against the translator's and M12–M16.
    /// </summary>
    /// <remarks>
    /// In the translator's spelling, so a form S chose as the translator wrote it prints the translator's text: a
    /// <c>NOT</c> over a test bare, as its guards write <c>NOT IS_NULL(p)</c>, and over anything else in parentheses, as
    /// <c>WriteNot</c> does; every literal bound through <see cref="CosmosParameterList.Add"/> as the translator binds
    /// Calcite's exact literals (<see cref="CosmosRexLiterals.TryGetExactNumberValue"/>). What the form cannot say, the
    /// type Calcite gave a literal: <c>DESIGN.md</c>, <i>The printer</i>.
    /// </remarks>
    internal static class RenderingPrinter
    {

        /// <summary>Prints a form S chose, binding its literals into the list in the order the text names them.</summary>
        /// <param name="form">The form; never an opaque one, which only the check reader and the sentinels make.</param>
        /// <param name="paths">Each path the form reads, by its spelling, as the conjunct's IR reads it.</param>
        /// <param name="rootAlias">The alias the statement reads the document under.</param>
        /// <param name="parameters">The list the literals are bound into.</param>
        /// <param name="stored">Whether S derived that the statement's set is sent the spelling its target stores
        /// (<see cref="Metadata.CosmosRendering.StoresSpelled(RenderingSet)"/>, <c>RenderingTheory.Sends</c>): then each
        /// literal, every one a compared value, is bound in that spelling, the writer's (<see cref="EmulatorStoredText.Of"/>),
        /// which is what S's leaf compares there (<c>ServiceLeaf.Compare</c>). A member's name is written as it is: whether a
        /// place may be written is S's, which proposes no form over one naming a band the set respells
        /// (<c>RenderingTheory.Writable</c>).</param>
        /// <param name="computed">Each computed place the form reads (h10), its call by its spelling.</param>
        /// <param name="spellings">
        /// The spelling S chose of each such call (<see cref="RenderingBundle.SpellingOf"/>), which it is printed in; a call S
        /// chose none of is printed in the one every call of its function takes.
        /// </param>
        /// <exception cref="ArgumentException">The form is opaque, or reads a path <paramref name="paths"/> does not name.</exception>
        /// <exception cref="CosmosTranslationException">A literal no value the service can be sent compares as, or holding a lone surrogate.</exception>
        public static string Print(ServiceForm form, IReadOnlyDictionary<string, JsonDocumentPath> paths, string rootAlias, CosmosParameterList parameters, bool stored = false,
            IReadOnlyDictionary<string, SqlValue.Call>? computed = null, Func<SqlValue.Call, long?>? spellings = null)
        {
            object? Bound(string literal) => stored && ValueOf(literal) is string text ? EmulatorStoredText.Of(text) : ValueOf(literal);

            var builder = new StringBuilder();

            void Path(string spelled)
            {
                // A computed place (h10) is its service call over its operands, each literal bound by the slot rule.
                if (computed is not null && computed.TryGetValue(spelled, out var call))
                {
                    builder.Append(Place(call) ?? throw new ArgumentException($"The form reads {spelled}, which has no service spelling.", nameof(computed)));
                    return;
                }

                if (paths.TryGetValue(spelled, out var path) == false)
                    throw new ArgumentException($"The form reads {spelled}, which the statement does not name.", nameof(paths));

                builder.Append(rootAlias);
                foreach (var segment in path.Segments)
                    if (segment is not string name)
                        builder.Append('[').Append(((long)segment).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(']');
                    else
                        CosmosSql.WritePropertyAccess(builder, RenderingPrinterFaults.Has(RenderingPrinterFault.M16) ? Misspelled(name) : name);
            }

            string? Place(SqlValue.Call call) => CosmosFunctionSpellings.Spell(call, operand =>
            {
                switch (operand)
                {
                    case SqlValue.Call { Spelled: { } inner } nested when computed!.ContainsKey(inner):
                        return Place(nested);
                    case SqlValue.Literal literal:
                        return parameters.Add(Bound(literal.Value));
                    case var _ when operand.PathRead is JsonDocumentPath read && paths.ContainsKey(SqlValue.Spell(read)):
                        var at = builder.Length;
                        Path(SqlValue.Spell(read));
                        var text = builder.ToString(at, builder.Length - at);
                        builder.Length = at;
                        return text;
                    default:
                        return null;
                }
            }, spellings?.Invoke(call));

            void Compare(ServiceForm compare, bool parenthesised)
            {
                builder.Append(parenthesised ? "(" : "");
                Path(compare.Path!);
                builder.Append(' ').Append(ServiceForm.OperatorOf(compare.Test)).Append(' ').Append(parameters.Add(Bound(compare.Literal!)));
                builder.Append(parenthesised ? ")" : "");
            }

            void Write(ServiceForm f, bool bare = false)
            {
                switch (f.Kind)
                {
                    case ServiceFormKind.Test:
                        builder.Append(ServiceForm.NameOf(f.Test)).Append('(');
                        Path(f.Path!);
                        builder.Append(')');
                        break;
                    case ServiceFormKind.Compare:
                        Compare(f, parenthesised: true);
                        break;
                    case ServiceFormKind.ComparePaths:
                        // Two readings (h11): the right side a path as the left is, no literal bound.
                        builder.Append('(');
                        Path(f.Path!);
                        builder.Append(' ').Append(ServiceForm.OperatorOf(f.Test)).Append(' ');
                        Path(f.Right!);
                        builder.Append(')');
                        break;
                    case ServiceFormKind.In:
                        builder.Append('(');
                        Path(f.Path!);
                        builder.Append(" IN (").Append(string.Join(", ", Rotated([.. f.Literals]).Select(l => parameters.Add(Bound(l))))).Append("))");
                        break;
                    case ServiceFormKind.Affix when f.Test == CosmosServiceTest.Like:
                        // LIKE as the translator writes it, the pattern a parameter.
                        builder.Append('(');
                        Path(f.Path!);
                        builder.Append(" LIKE ").Append(parameters.Add(Bound(f.Literal!))).Append(')');
                        break;
                    case ServiceFormKind.Affix:
                        // An affix, case sensitive, with no third operand, as the translator writes one (h07).
                        builder.Append(ServiceForm.NameOf(f.Test)).Append('(');
                        Path(f.Path!);
                        builder.Append(", ").Append(parameters.Add(Bound(f.Literal!))).Append(')');
                        break;
                    case ServiceFormKind.Bare:
                        Path(f.Path!);
                        break;
                    case ServiceFormKind.Constant:
                        builder.Append(f.Literal == JsonValues.Of(true) ? "true" : "false");
                        break;
                    case ServiceFormKind.Not when f.Members[0].Kind == ServiceFormKind.Test:
                        builder.Append("NOT ");
                        Write(f.Members[0]);
                        break;
                    case ServiceFormKind.Not:
                        builder.Append("(NOT ");
                        if (f.Members[0].Kind == ServiceFormKind.Compare && RenderingPrinterFaults.Has(RenderingPrinterFault.M14))
                            Compare(f.Members[0], parenthesised: false);
                        else
                            Write(f.Members[0]);
                        builder.Append(')');
                        break;
                    case ServiceFormKind.Choose:
                        builder.Append('(');
                        Write(f.Members[0]);
                        builder.Append(" ? ");
                        Write(f.Members[1]);
                        builder.Append(" : ");
                        Write(f.Members[2]);
                        builder.Append(')');
                        break;
                    case ServiceFormKind.And or ServiceFormKind.Or:
                        var members = MembersOf(f);
                        builder.Append(bare ? "" : "(");
                        for (var i = 0; i < members.Count; i++)
                        {
                            builder.Append(i == 0 ? "" : f.Kind == ServiceFormKind.And ? " AND " : " OR ");
                            Write(members[i], f.Kind == ServiceFormKind.And && members[i].Kind == ServiceFormKind.Or && RenderingPrinterFaults.Has(RenderingPrinterFault.M12));
                        }
                        builder.Append(bare ? "" : ")");
                        break;
                    default:
                        throw new ArgumentException("An opaque form is the check reader's, or a sentinel no rule lifts: never one S sends.", nameof(form));
                }
            }

            Write(form);
            return builder.ToString();
        }

        /// <summary>
        /// The value a literal is bound as: text, a boolean, null; one held approximately as its double; and an exact number
        /// as the translator binds Calcite's exact literal of it, by its own rule (<see cref="CosmosRexLiterals.TryGetExactNumberValue"/>).
        /// </summary>
        static object? ValueOf(string literal)
        {
            switch (JsonValues.TypeOf(literal))
            {
                case JsonValues.StringType:
                    return JsonValues.StringOf(literal);
                case JsonValues.BooleanType:
                    return literal == JsonValues.Of(true);
                case JsonValues.NullType:
                    return null;
            }

            if (JsonValues.ApproximateOf(literal) is { } approximate)
                return approximate;

            return JsonValues.TryNumberOf(literal, out var number) && CosmosRexLiterals.TryGetExactNumberValue(new java.math.BigDecimal(number.ToString(CultureInfo.InvariantCulture))) is { } exact
                ? exact : throw new CosmosTranslationException($"No value the service can be sent compares there as the number {literal}.");
        }

        /// <summary>A junction's members as written: its last left out under M13, and its comparisons' literals rotated under M15.</summary>
        static IReadOnlyList<ServiceForm> MembersOf(ServiceForm junction)
        {
            if (RenderingPrinterFaults.Armed == false)
                return junction.Members;

            var members = junction.Members.ToList();
            if (RenderingPrinterFaults.Has(RenderingPrinterFault.M13))
                members.RemoveAt(members.Count - 1);

            var compared = Enumerable.Range(0, members.Count).Where(i => members[i].Kind == ServiceFormKind.Compare).ToList();
            var literals = Rotated([.. compared.Select(i => members[i].Literal!)]);
            for (var k = 0; k < compared.Count; k++)
                members[compared[k]] = ServiceForm.CompareOf(members[compared[k]].Test, members[compared[k]].Path!, literals[k]);

            return members;
        }

        /// <summary>Literals in written order, or under M15 each its neighbour's.</summary>
        static IReadOnlyList<string> Rotated(IReadOnlyList<string> literals) =>
            RenderingPrinterFaults.Has(RenderingPrinterFault.M15) && literals.Count > 1 ? [.. literals.Skip(1), literals[0]] : literals;

        /// <summary>M16's name: its first letter's case flipped.</summary>
        static string Misspelled(string name) => name.Length == 0 || char.IsLetter(name[0]) == false ? name
            : (char.IsUpper(name[0]) ? char.ToLowerInvariant(name[0]) : char.ToUpperInvariant(name[0])) + name[1..];

    }

}
