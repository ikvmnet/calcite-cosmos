using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The printer (spec g13; line 14): every form S chooses over the pull request's inputs prints a text the reader reads
    /// back as that form, written as the text was; where S chose the translator's form, the translator's text, byte for
    /// byte, with its parameters bound to values of the translator's types; and each kind and literal as the translator
    /// writes and binds it.
    /// </summary>
    public sealed class RenderingPrinterTests
    {

        static readonly string Alias = CosmosImplementor.DefaultRootAlias;

        /// <summary>Every form S chose over the run's inputs, under each set, once a text: each root's best, with the paths its bundle's roots read.</summary>
        internal static IReadOnlyList<(ServiceForm Form, IReadOnlyDictionary<string, JsonDocumentPath> Paths)> Chosen() =>
            [.. RenderingCheckRun.PerPullRequest.Value.Generated!.Bundles.SelectMany(b => b.Answers.Values.Select(a => a.Best).OfType<ServiceForm>().Select(f => (f, PathsOf(b.Roots))))
                .DistinctBy(c => c.Item1.Text)];

        /// <summary>Each path the predicates read, by its spelling.</summary>
        internal static IReadOnlyDictionary<string, JsonDocumentPath> PathsOf(IEnumerable<SqlPredicate> predicates) =>
            predicates.SelectMany(p => p.Paths).Distinct().ToDictionary(SqlValue.Spell, StringComparer.Ordinal);

        /// <summary>
        /// What a round trip finds wrong with a form, or <c>null</c>: its print read back as another form or written otherwise,
        /// printed again from what was read as another text or other values, or not printed at all.
        /// </summary>
        internal static string? RoundTrip(ServiceForm form, IReadOnlyDictionary<string, JsonDocumentPath> paths)
        {
            try
            {
                var parameters = new CosmosParameterList();
                var text = RenderingPrinter.Print(form, paths, Alias, parameters);
                var read = CosmosSqlFormReader.Read(text, parameters.Parameters);
                if (read.Form.Equals(form) == false || read.Written != text)
                    return $"{form} printed {text}, read back as {read.Form} written {read.Written}";

                var again = new CosmosParameterList();
                var reprinted = RenderingPrinter.Print(read.Form, paths, Alias, again);
                return reprinted == text && again.Parameters.SequenceEqual(parameters.Parameters) ? null : $"{form} printed {text}, and again {reprinted}";
            }
            catch (Exception e) when (e is ArgumentException or CosmosTranslationException)
            {
                return $"{form} printed nothing: {e.Message}";
            }
        }

        /// <summary>Each name and the value it is bound to, its type and its spelling as a plan's digest spells it, in order.</summary>
        static string Bound(IEnumerable<CosmosParameter> parameters) =>
            string.Join(", ", parameters.Select(p => p.Name + " " + (p.Value?.GetType().Name ?? "null") + " " + System.Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture)));

        /// <summary>
        /// Every best of every bundle production opened in the run, under each set, prints and reads back as itself and as
        /// the text it printed; printed again from what was read, the same text and values.
        /// </summary>
        [Fact]
        public void EveryChosenFormReadsBackAsItself()
        {
            var chosen = Chosen();
            var kinds = chosen.SelectMany(c => c.Form.Subforms).GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Count());
            TestContext.Current.TestOutputHelper?.WriteLine($"{chosen.Count} forms chosen; their subforms by kind: {string.Join(", ", kinds.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"))}");

            chosen.Should().HaveCountGreaterThan(200);
            chosen.Select(c => RoundTrip(c.Form, c.Paths)).OfType<string>().Should().BeEmpty();
        }

        /// <summary>
        /// Where S sends the form the translator writes of a conjunct from the same stratum, read back, the printer writes the
        /// translator's text, its names in the translator's order, each bound to the translator's value, of its type and
        /// scale; byte for byte, but where the translator brackets a <c>NOT</c> over a test inside a junction, which the
        /// printer writes bare and the reader reads as one form (a weakening's members, which S's inner filters send since
        /// the switch); reported. And but for what the form cannot say: a number compared through a reading Calcite types
        /// approximate, whose literal it types so too, which the translator binds as a double and the printer as the decimal
        /// the form holds, the same number to the service; reported.
        /// </summary>
        [Fact]
        public void EverySameFormPrintsTheTranslatorsTextAndTypes()
        {
            var same = new List<(RenderingGenerateRun.Sent Sent, SqlPredicate Ir, string Text, IReadOnlyList<CosmosParameter> Bound)>();
            foreach (var c in RenderingCheckRun.PerPullRequest.Value.Generated!.Sends.Where(c => c.Form is not null))
            {
                var theirs = new CosmosParameterList();
                if (c.Source.TranslatorFor(c.Conjunct, theirs).TryTranslateConjunct(c.Source.Conjuncts[c.Conjunct], out var written)
                    && CosmosSqlFormReader.Read(written!, theirs.Parameters) is var read && read.Written == written && read.Form.Equals(c.Form))
                    same.Add((c, c.Source.Statement.Conjuncts[c.Conjunct], written!, theirs.Parameters));
            }

            same = [.. same.DistinctBy(c => (c.Ir.Digest, c.Text))];
            var (parted, retyped, bracketed) = (new List<string>(), new List<string>(), new List<string>());
            foreach (var (c, ir, written, bound) in same)
            {
                var parameters = new CosmosParameterList();
                var text = RenderingPrinter.Print(c.Form!, PathsOf([ir]), Alias, parameters);
                var (printed, theirs) = (Bound(parameters.Parameters), Bound(bound));
                var shown = $"{c.Input} {c.Name}/{c.Statement}/{c.Conjunct} {ir}: the translator's {written} [{theirs}], printed {text} [{printed}]";
                if (text != written || CosmosSqlFormReader.Inline(text, parameters.Parameters) != CosmosSqlFormReader.Inline(written, bound))
                    (Unbracketed(text) == Unbracketed(written) && printed == theirs ? bracketed : parted).Add(shown);
                else if (printed != theirs)
                    (parameters.Parameters.Zip(bound).All(p => Bound([p.First]) == Bound([p.Second]) || p.First.Value is decimal && p.Second.Value is double)
                        && ComparesApproximately(ir) ? retyped : parted).Add(shown);
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"{same.Count} conjuncts S sends as the translator writes them, {same.Sum(c => c.Bound.Count)} parameters; {parted.Count} parted; "
                + $"{bracketed.Count} bracketed otherwise alone: {string.Join("; ", bracketed)}; "
                + $"{retyped.Count} compared through an approximate reading, bound a double by the translator: {string.Join("; ", retyped)}");
            same.Should().HaveCountGreaterThan(100);
            parted.Should().BeEmpty();
        }

        /// <summary>A text with its brackets taken out: two texts alike so differ in their brackets alone.</summary>
        static string Unbracketed(string text) => text.Replace("(", "", StringComparison.Ordinal).Replace(")", "", StringComparison.Ordinal);

        /// <summary>Whether a conjunct compares a number through a reading Calcite types approximate, as it types the literal.</summary>
        static bool ComparesApproximately(SqlPredicate conjunct) =>
            conjunct.Subterms.Any(t => t is SqlPredicate.Compare { Left: SqlValue.Typed { Type: "DOUBLE" or "REAL" or "FLOAT" }, Right: SqlValue.Literal { IsNumber: true } });

        static readonly string Category = SqlValue.Spell(JsonDocumentPath.Root.Property("category"));

        static readonly string FirstName = SqlValue.Spell(JsonDocumentPath.Root.Property("customer").Property("first name"));

        static readonly IReadOnlyDictionary<string, JsonDocumentPath> Paths = new Dictionary<string, JsonDocumentPath>
        {
            [Category] = JsonDocumentPath.Root.Property("category"),
            [FirstName] = JsonDocumentPath.Root.Property("customer").Property("first name"),
        };

        static (string Text, IReadOnlyList<CosmosParameter> Parameters) Print(ServiceForm form)
        {
            var parameters = new CosmosParameterList();
            return (RenderingPrinter.Print(form, Paths, Alias, parameters), parameters.Parameters);
        }

        /// <summary>
        /// Each kind in the translator's spelling: a test bare, a comparison and a junction parenthesised, a <c>NOT</c> bare
        /// over a test and parenthesised over anything else, an <c>IN</c> its points in order, a path through the property
        /// spelling, brackets where a name needs them; an opaque form, and a path the statement does not name, refused.
        /// </summary>
        [Fact]
        public void EachKindPrintsInTheTranslatorsSpelling()
        {
            var (defined, isNull) = (ServiceForm.TestOf(CosmosServiceTest.IsDefined, Category), ServiceForm.TestOf(CosmosServiceTest.IsNull, Category));
            var (bikes, tools) = (ServiceForm.CompareOf(CosmosServiceTest.NotEqual, Category, JsonValues.Of("bikes")), ServiceForm.CompareOf(CosmosServiceTest.Equal, Category, JsonValues.Of("tools")));

            Print(defined).Text.Should().Be("IS_DEFINED(c.category)");
            Print(ServiceForm.JunctionOf(CosmosServiceTest.And, [defined, ServiceForm.NotOf(isNull), bikes])).Text
                .Should().Be("(IS_DEFINED(c.category) AND NOT IS_NULL(c.category) AND (c.category != @p0))");
            Print(ServiceForm.NotOf(tools)).Text.Should().Be("(NOT (c.category = @p0))");
            Print(ServiceForm.NotOf(ServiceForm.JunctionOf(CosmosServiceTest.Or, [tools, isNull]))).Text.Should().Be("(NOT ((c.category = @p0) OR IS_NULL(c.category)))");
            Print(ServiceForm.JunctionOf(CosmosServiceTest.And, [ServiceForm.JunctionOf(CosmosServiceTest.Or, [tools, bikes]), defined])).Text
                .Should().Be("(((c.category = @p0) OR (c.category != @p1)) AND IS_DEFINED(c.category))");
            Print(ServiceForm.InOf(FirstName, ValueSet.Of(JsonValues.Of("ann"), JsonValues.Of("bo")))).Should().Match<(string Text, IReadOnlyList<CosmosParameter> Parameters)>(p =>
                p.Text == "(c.customer[\"first name\"] IN (@p0, @p1))" && (string)p.Parameters[0].Value! == "ann" && (string)p.Parameters[1].Value! == "bo");
            Print(ServiceForm.BareOf(Category)).Text.Should().Be("c.category");
            Print(ServiceForm.ConstantOf(false)).Text.Should().Be("false");

            FluentActions.Invoking(() => Print(ServiceForm.OpaqueOf("x", ValueSet<string>.Empty))).Should().Throw<ArgumentException>();
            FluentActions.Invoking(() => Print(ServiceForm.TestOf(CosmosServiceTest.IsDefined, SqlValue.Spell(JsonDocumentPath.Root.Property("price"))))).Should().Throw<ArgumentException>();
        }

        /// <summary>
        /// Each literal bound as the translator binds Calcite's exact literal of the same number
        /// (<see cref="CosmosRexTranslator.TryGetExactNumberValue"/>), and refused where the translator declines it; text,
        /// a boolean and null as themselves; a number held approximately as its double; and a lone surrogate refused by the
        /// list, as every value the translator binds is.
        /// </summary>
        [Fact]
        public void EachLiteralIsBoundAsTheTranslatorBindsIt()
        {
            object? Bound(string literal) => Print(ServiceForm.CompareOf(CosmosServiceTest.Equal, Category, literal)).Parameters.Single().Value;

            foreach (var number in new[] { "30", "30.0", "-7", "1.5", "0.1", "9007199254740992", "9007199254740993", "1152921504606846977", "-1152921504606846977",
                "2.9999999999999999", "100000000000000000000000000", "79228162514264337593543950335" })
            {
                var theirs = CosmosRexTranslator.TryGetExactNumberValue(new java.math.BigDecimal(number));
                var literal = JsonValues.Of(decimal.Parse(number, System.Globalization.CultureInfo.InvariantCulture));
                if (theirs is null)
                    FluentActions.Invoking(() => Bound(literal)).Should().Throw<CosmosTranslationException>(number);
                else
                    Bound(literal).Should().Match(v => v != null && v.GetType() == theirs.GetType() && v.Equals(theirs), number);
            }

            Bound(JsonValues.Of(1e30)).Should().Be(1e30);
            Bound(JsonValues.Of("30")).Should().Be("30");
            Bound(JsonValues.Of(true)).Should().Be(true);
            Bound(JsonValues.NullValue).Should().BeNull();
            FluentActions.Invoking(() => Bound(JsonValues.Of("a\uD800"))).Should().Throw<CosmosTranslationException>();
        }

    }

}
