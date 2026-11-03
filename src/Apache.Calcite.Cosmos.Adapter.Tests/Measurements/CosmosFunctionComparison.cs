using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

using Microsoft.Azure.Cosmos;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Measurements
{

    /// <summary>
    /// Measures function rows: each function asked of the service over each key's witnesses at <c>c.v</c>
    /// (<see cref="CosmosWitnessProbe.Project"/>) and of Calcite in process over the same documents, then each key's
    /// answer (<see cref="CosmosFunctionAnswer"/>), the worst over a function's spellings and the readers its row names
    /// (<see cref="CosmosAxiom.Readers"/>), each a reading of <c>v</c> Calcite is asked through.
    /// </summary>
    /// <remarks>
    /// Two values are the same where their JSON is; a service number written as an integer where it is Calcite's number
    /// exactly, and one written with a point or an exponent where its double is, a <c>DECIMAL</c> compared as written;
    /// null and undefined are one. A raise in Calcite is a value no service answer equals.
    /// </remarks>
    internal static class CosmosFunctionComparison
    {

        /// <summary>The answers from worst to best: a key's is the worst any operand or spelling gives.</summary>
        static readonly CosmosFunctionAnswer[] Severity = [CosmosFunctionAnswer.Fails, CosmosFunctionAnswer.Differs, CosmosFunctionAnswer.Undefined, CosmosFunctionAnswer.Same];

        /// <summary>
        /// Each key's answer, <c>function/kind/band</c>, over every function spelled as Calcite and as the service write
        /// it, the one lower-case <c>s</c> in each standing for the value.
        /// </summary>
        public static async Task<IReadOnlyDictionary<string, CosmosFunctionAnswer>> Measure(Container container, string? unavailable, string row,
            IReadOnlyList<(string Function, string Calcite, string Service)> functions, IReadOnlyList<(CosmosValueClass Kind, JsonCharacterBand Band, string?[] Witnesses)> keys)
        {
            string?[] witnesses = [.. keys.SelectMany(k => k.Witnesses)];
            string[] spellings = [.. functions.Select(f => f.Service.Replace("s", "c.v", StringComparison.Ordinal)).Distinct()];
            var asked = await CosmosWitnessProbe.Project(container, unavailable, row, witnesses, spellings);
            var service = functions.Select(f => asked[Array.IndexOf(spellings, f.Service.Replace("s", "c.v", StringComparison.Ordinal))]).ToArray();

            using DbConnection connection = MaterialisedTable.Open("all");
            var table = MaterialisedTable.Of(witnesses.Select(w => w is null ? "{}" : $"{{\"v\":{w}}}"));
            var measured = new SortedDictionary<string, CosmosFunctionAnswer>(StringComparer.Ordinal);
            for (var f = 0; f < functions.Count; f++)
                foreach (var reader in CosmosAxioms.Get(row).Readers!)
                {
                    var calcite = table.Values(connection, functions[f].Calcite.Replace("s", Read(reader), StringComparison.Ordinal));
                    var w = 0;
                    foreach (var (kind, band, of) in keys)
                    {
                        var answer = Answer(of.Select((_, i) => (service[f][w + i], calcite[w + i])));
                        w += of.Length;
                        var key = $"{functions[f].Function}/{kind}/{band}";
                        measured[key] = measured.TryGetValue(key, out var other) ? Worse(other, answer) : answer;
                    }
                }

            return measured;
        }

        /// <summary>
        /// Calcite's reading of <c>v</c> through a reader the row names: the text accessor, a typed one, or a cast of either.
        /// </summary>
        static string Read(string reader) => reader.Split(':', 3) switch
        {
            [Apache.Calcite.Cosmos.Facts.Saturation.PredicateAtom.TextReader] => "JSON_VALUE(\"d\", '$.v')",
            ["typed", var type] => $"JSON_VALUE(\"d\", '$.v' RETURNING {type})",
            ["cast", var type, var inner] => $"CAST({Read(inner)} AS {type})",
            _ => throw new ArgumentException($"No reading of {reader}.", nameof(reader)),
        };

        /// <summary>
        /// The keys a row records worse than measured, and those it records better: a release answering better than the
        /// row is within it, one answering worse fails it.
        /// </summary>
        public static IEnumerable<string> Unsound(IReadOnlyDictionary<string, CosmosFunctionAnswer> measured, IEnumerable<CosmosFunctionOutcome> recorded, CosmosAxiomTarget on)
        {
            var row = recorded.Where(o => (o.On & on) != 0).ToDictionary(o => $"{o.Function}/{o.Operand}/{o.Band}", o => o.Answer, StringComparer.Ordinal);
            foreach (var (key, answer) in measured)
                if (row.TryGetValue(key, out var claimed) == false)
                    yield return $"{key}={answer} is not recorded";
                else if (Worse(claimed, answer) != claimed)
                    yield return $"{key}={answer}, recorded {claimed}";

            foreach (var key in row.Keys.Where(k => measured.ContainsKey(k) == false))
                yield return $"{key} is recorded and not measured";
        }

        /// <summary>The worse of two answers.</summary>
        public static CosmosFunctionAnswer Worse(CosmosFunctionAnswer a, CosmosFunctionAnswer b) =>
            Array.IndexOf(Severity, a) < Array.IndexOf(Severity, b) ? a : b;

        static CosmosFunctionAnswer Answer(IEnumerable<(string Service, object? Calcite)> pairs)
        {
            var list = pairs.ToList();
            return list.Any(p => p.Service == "!") ? CosmosFunctionAnswer.Fails
                : list.All(p => Alike(p.Service, p.Calcite)) ? CosmosFunctionAnswer.Same
                : list.All(p => Alike(p.Service, p.Calcite) || p.Service == "undefined") ? CosmosFunctionAnswer.Undefined
                : CosmosFunctionAnswer.Differs;
        }

        /// <summary>
        /// Whether the service's JSON is Calcite's value: a service number written as an integer is that integer exactly,
        /// one written with a point or an exponent the double it rounds to, as a reader reads either.
        /// </summary>
        static bool Alike(string service, object? calcite)
        {
            if (service == Spelled(calcite))
                return true;

            var integral = BigInteger.TryParse(service, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole);
            var real = 0d;
            if (integral == false && double.TryParse(service, NumberStyles.Float, CultureInfo.InvariantCulture, out real) == false)
                return false;

            return calcite switch
            {
                double or float => Convert.ToDouble(calcite, CultureInfo.InvariantCulture) is var d && (integral ? double.IsInteger(d) && new BigInteger(d) == whole : real.Equals(d)),
                int or long or short or byte or BigInteger => BigInteger.Parse(Spelled(calcite), CultureInfo.InvariantCulture) is var n && (integral ? n == whole : double.IsInteger(real) && new BigInteger(real) == n),
                null or string or Exception => false,
                _ => decimal.TryParse(Spelled(calcite), NumberStyles.Float, CultureInfo.InvariantCulture, out var exact) && decimal.TryParse(service, NumberStyles.Float, CultureInfo.InvariantCulture, out var sent) && exact == sent,
            };
        }

        /// <summary>Calcite's value as the probe spells the service's: JSON, <c>undefined</c> for null, <c>!</c> and the exception's type for a raise.</summary>
        static string Spelled(object? value) => value switch
        {
            null => "undefined",
            string text => Newtonsoft.Json.JsonConvert.ToString(text),
            Exception e => "!" + e.GetType().Name,
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()!,
        };

    }

}
