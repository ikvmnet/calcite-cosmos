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
        /// Each key's answer (<see cref="FunctionKey"/>, a witness's atom its class of value) over every function spelled as
        /// Calcite and as the service write it, the one lower-case <c>s</c> in each standing for the value.
        /// </summary>
        public static async Task<IReadOnlyDictionary<FunctionKey, CosmosFunctionAnswer>> Measure(Container container, string? unavailable, string row,
            IReadOnlyList<CosmosFunctionSpelling> functions, IReadOnlyList<(CosmosValueClass Kind, JsonCharacterBand Band, string?[] Witnesses)> keys)
        {
            var measured = Judge(row, functions, keys, await CosmosWitnessProbe.Project(container, unavailable, row, [.. keys.SelectMany(k => k.Witnesses)], Spellings(functions)));

            // Each key's answer beside the lines the probe logged, so a run on an account can be written up as rows.
            lock (Answers)
            {
                var path = System.IO.Path.GetFullPath(System.IO.Path.Combine("TestResults", "measurements", "function-answers.tsv"));
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                System.IO.File.AppendAllLines(path, measured.Select(m => string.Join('\t', CosmosWitnessProbe.Release, row, m.Key, m.Value)));
            }

            return measured;
        }

        static readonly object Answers = new();

        /// <summary>The service's spellings of the functions, <c>c.v</c> for the value, each once: what the probe projects.</summary>
        public static string[] Spellings(IReadOnlyList<CosmosFunctionSpelling> functions) =>
            [.. functions.Select(Spelled).Distinct()];

        /// <summary>The service's spelling of one, <c>c.v</c> for the value, as a line of a record names it.</summary>
        public static string Spelled(CosmosFunctionSpelling function) => function.Service.Replace("s", "c.v", StringComparison.Ordinal);

        /// <summary>
        /// Each key's answer from what the service projected, by spelling (<see cref="Spellings"/>) and witness, against
        /// Calcite in process under each spelling's libraries: a probe's, or a run record's lines read again. A witness keys
        /// its operand by each atom of its kind it is in (<see cref="FunctionValueClasses"/>), and a spelling's literals are
        /// keyed as it names them (<see cref="CosmosFunctionSpelling.Fixed"/>), over the witnesses of a string of no band. A
        /// spelling the service left unanswered over any witness (<see cref="CosmosWitnessProbe.Unanswered"/>) measures
        /// nothing, as one never sent does; one it refused (<c>!</c>) or failed (<see cref="CosmosWitnessProbe.Failed"/>)
        /// over a witness fails each key the witness is of.
        /// </summary>
        public static IReadOnlyDictionary<FunctionKey, CosmosFunctionAnswer> Judge(string row, IReadOnlyList<CosmosFunctionSpelling> functions,
            IReadOnlyList<(CosmosValueClass Kind, JsonCharacterBand Band, string?[] Witnesses)> keys, string[][] asked)
        {
            string?[] witnesses = [.. keys.SelectMany(k => k.Witnesses)];
            var spellings = Spellings(functions);
            var table = MaterialisedTable.Of(witnesses.Select(w => w is null ? "{}" : $"{{\"v\":{w}}}"));
            var connections = new Dictionary<string, DbConnection>(StringComparer.Ordinal);
            var measured = new SortedDictionary<FunctionKey, CosmosFunctionAnswer>();
            try
            {
                foreach (var function in functions)
                {
                    var service = asked[Array.IndexOf(spellings, Spelled(function))];
                    if (service.Contains(CosmosWitnessProbe.Unanswered))
                        continue;

                    if (connections.TryGetValue(function.Library, out var connection) == false)
                        connections[function.Library] = connection = MaterialisedTable.Open(function.Library);

                    foreach (var reader in CosmosAxioms.Get(row).Readers!)
                    {
                        var calcite = table.Values(connection, function.Calcite.Replace("s", Read(reader), StringComparison.Ordinal));
                        var w = 0;
                        foreach (var (kind, band, of) in keys)
                        {
                            var pairs = of.Select((witness, i) => (Witness: witness, Service: service[w + i], Calcite: calcite[w + i])).ToList();
                            w += of.Length;
                            if (function.Ordinal is long ordinal)
                                foreach (var atom in FunctionValueClasses.AtomsOf(kind))
                                    if (pairs.Where(p => Holds(atom, kind, p.Witness)).Select(p => (p.Service, p.Calcite)).ToList() is { Count: > 0 } held)
                                        Note(measured, FunctionKey.Of(function.Function, ordinal, kind, band, atom), Answer(held));

                            if (kind == CosmosValueClass.String && band == JsonCharacterBand.None)
                                foreach (var key in function.Fixed ?? [])
                                    Note(measured, key, Answer(pairs.Select(p => (p.Service, p.Calcite))));
                        }
                    }
                }
            }
            finally
            {
                foreach (var connection in connections.Values)
                    connection.Dispose();
            }

            return measured;
        }

        /// <summary>
        /// Calcite's answer in process to one spelling over each witness, under each reader the row names, spelled as the
        /// probe spells the service's: what <see cref="Judge"/> holds each of the service's answers to.
        /// </summary>
        public static IReadOnlyList<(string Reader, string[] Answers)> CalciteAnswers(string row, CosmosFunctionSpelling function, IReadOnlyList<string?> witnesses)
        {
            var table = MaterialisedTable.Of(witnesses.Select(w => w is null ? "{}" : $"{{\"v\":{w}}}"));
            using var connection = MaterialisedTable.Open(function.Library);
            return [.. CosmosAxioms.Get(row).Readers!.Select(reader =>
                (reader, table.Values(connection, function.Calcite.Replace("s", Read(reader), StringComparison.Ordinal)).Select(v => Spelled(v)).ToArray()))];
        }

        static void Note(SortedDictionary<FunctionKey, CosmosFunctionAnswer> measured, FunctionKey key, CosmosFunctionAnswer answer) =>
            measured[key] = measured.TryGetValue(key, out var other) ? Worse(other, answer) : answer;

        /// <summary>Whether a witness, as JSON, is of an atom of its kind (<see cref="FunctionValueClasses.Holds"/>).</summary>
        static bool Holds(FunctionValueClass atom, CosmosValueClass kind, string? witness) => kind switch
        {
            CosmosValueClass.String => FunctionValueClasses.Holds(atom, Apache.Calcite.Cosmos.Facts.Saturation.JsonValues.Of(Newtonsoft.Json.JsonConvert.DeserializeObject<string>(witness!)!)),
            CosmosValueClass.Number => FunctionValueClasses.Holds(atom, decimal.TryParse(witness, NumberStyles.Float, CultureInfo.InvariantCulture, out var exact)
                ? Apache.Calcite.Cosmos.Facts.Saturation.JsonValues.Of(exact)
                : Apache.Calcite.Cosmos.Facts.Saturation.JsonValues.Of(double.Parse(witness!, NumberStyles.Float, CultureInfo.InvariantCulture))),
            _ => true,
        };

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
        /// The keys a row records better than measured, those measured that no row covers, and the rows no measured key falls
        /// under: a row covers each atom its class holds, as S reads it, and a release answering better than the row is within
        /// it, one answering worse fails it.
        /// </summary>
        public static IEnumerable<string> Unsound(IReadOnlyDictionary<FunctionKey, CosmosFunctionAnswer> measured, IEnumerable<CosmosFunctionOutcome> recorded, CosmosAxiomTarget on)
        {
            var rows = recorded.Where(o => (o.On & on) != 0).Select(o => (Key: FunctionKey.Of(o.Name, o.Subject, o.Operand, o.Band, o.Value), o.Answer)).ToList();
            var covered = new HashSet<FunctionKey>();
            foreach (var (key, answer) in measured)
            {
                var covering = rows.Where(r => Covers(r.Key, key)).ToList();
                if (covering.Count == 0)
                    yield return $"{key}={answer} is not recorded";

                foreach (var (row, claimed) in covering)
                {
                    covered.Add(row);
                    if (Worse(claimed, answer) != claimed)
                        yield return $"{key}={answer}, recorded {claimed} at {row}";
                }
            }

            foreach (var (row, _) in rows.Where(r => covered.Contains(r.Key) == false))
                yield return $"{row} is recorded and not measured";
        }

        /// <summary>Whether a row's key covers a measured atom's: one function, operand, kind and band, the row's class holding the atom.</summary>
        static bool Covers(FunctionKey row, FunctionKey atom) =>
            row with { Value = atom.Value } == atom && FunctionValueClasses.Holding.Contains((atom.Kind, row.Value, atom.Value));

        /// <summary>The worse of two answers.</summary>
        public static CosmosFunctionAnswer Worse(CosmosFunctionAnswer a, CosmosFunctionAnswer b) =>
            Array.IndexOf(Severity, a) < Array.IndexOf(Severity, b) ? a : b;

        static CosmosFunctionAnswer Answer(IEnumerable<(string Service, object? Calcite)> pairs)
        {
            var list = pairs.ToList();
            return list.Any(p => p.Service is "!" or CosmosWitnessProbe.Failed) ? CosmosFunctionAnswer.Fails
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
