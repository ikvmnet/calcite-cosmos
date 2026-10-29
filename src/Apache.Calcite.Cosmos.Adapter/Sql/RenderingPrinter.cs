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
    /// Calcite's exact literals (<see cref="CosmosRexTranslator.TryGetExactNumberValue"/>). What the form cannot say, the
    /// type Calcite gave a literal: <c>DESIGN.md</c>, <i>The printer</i>.
    /// </remarks>
    internal static class RenderingPrinter
    {

        /// <summary>Prints a form S chose, binding its literals into the list in the order the text names them.</summary>
        /// <param name="form">The form; never an opaque one, which only the check reader and the sentinels make.</param>
        /// <param name="paths">Each path the form reads, by its spelling, as the conjunct's IR reads it.</param>
        /// <param name="rootAlias">The alias the statement reads the document under.</param>
        /// <param name="parameters">The list the literals are bound into.</param>
        /// <exception cref="ArgumentException">The form is opaque, or reads a path <paramref name="paths"/> does not name.</exception>
        /// <exception cref="CosmosTranslationException">A literal no value the service can be sent compares as, or holding a lone surrogate.</exception>
        public static string Print(ServiceForm form, IReadOnlyDictionary<string, JsonDocumentPath> paths, string rootAlias, CosmosParameterList parameters)
        {
            var builder = new StringBuilder();

            void Path(string spelled)
            {
                if (paths.TryGetValue(spelled, out var path) == false)
                    throw new ArgumentException($"The form reads {spelled}, which the statement does not name.", nameof(paths));

                builder.Append(rootAlias);
                foreach (var name in path.Names)
                    CosmosSql.WritePropertyAccess(builder, RenderingPrinterFaults.Has(RenderingPrinterFault.M16) ? Misspelled(name) : name);
            }

            void Compare(ServiceForm compare, bool parenthesised)
            {
                builder.Append(parenthesised ? "(" : "");
                Path(compare.Path!);
                builder.Append(' ').Append(ServiceForm.OperatorOf(compare.Test)).Append(' ').Append(parameters.Add(ValueOf(compare.Literal!)));
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
                    case ServiceFormKind.In:
                        builder.Append('(');
                        Path(f.Path!);
                        builder.Append(" IN (").Append(string.Join(", ", Rotated([.. f.Literals]).Select(l => parameters.Add(ValueOf(l))))).Append("))");
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
        /// as the translator binds Calcite's exact literal of it, by its own rule (<see cref="CosmosRexTranslator.TryGetExactNumberValue"/>).
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

            return JsonValues.TryNumberOf(literal, out var number) && CosmosRexTranslator.TryGetExactNumberValue(new java.math.BigDecimal(number.ToString(CultureInfo.InvariantCulture))) is { } exact
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
