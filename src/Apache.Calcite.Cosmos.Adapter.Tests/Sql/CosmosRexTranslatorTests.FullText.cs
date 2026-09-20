using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.FullText.Sql;

using FluentAssertions;

using org.apache.calcite.jdbc;
using org.apache.calcite.rex;
using org.apache.calcite.sql.type;
using Xunit;


namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    public partial class CosmosRexTranslatorTests
    {

        /// <summary>
        /// Full text search, which is not SQL and so arrives through operators of this adapter's own.
        /// </summary>
        /// <remarks>
        /// The signatures are the service's, taken from the query language reference: the first argument of
        /// every function is a property path, and the <c>ALL</c> and <c>ANY</c> forms take one or more
        /// keywords after it.
        /// </remarks>
        public class FullText
        {

            readonly JavaTypeFactoryImpl _types = new();
            readonly RexBuilder _rex;
            readonly List<CosmosPath?> _fields;

            public FullText()
            {
                _rex = new RexBuilder(_types);
                _fields = new List<CosmosPath?>
                {
                    CosmosPath.Root("c").Property("text"),   // 0
                    null,                                    // 1 — a computed projection
                    CosmosPath.Root("c").Property("note"),   // 2 — a path nothing declares
                };
            }

            CosmosRexTranslator Translator() => new(_rex, _fields, new CosmosParameterList());

            /// <summary>
            /// A translator that knows what the container declares, which is what gates the full text
            /// functions.
            /// </summary>
            CosmosRexTranslator Translator(CosmosContainerMetadata container) => new(_rex, _fields, new CosmosParameterList(), null, container);

            RexNode Text() => _rex.makeInputRef(_types.createSqlType(SqlTypeName.VARCHAR), 0);

            RexNode Computed() => _rex.makeInputRef(_types.createSqlType(SqlTypeName.VARCHAR), 1);

            RexNode Other() => _rex.makeInputRef(_types.createSqlType(SqlTypeName.VARCHAR), 2);

            RexNode Keyword(string value) => _rex.makeLiteral(value, _types.createSqlType(SqlTypeName.VARCHAR, value.Length));

            string Translate(org.apache.calcite.sql.SqlOperator op, params RexNode[] operands) =>
                Translator().Translate(_rex.makeCall(op, operands));

            bool CanTranslate(org.apache.calcite.sql.SqlOperator op, params RexNode[] operands) =>
                Translator().TryTranslate(_rex.makeCall(op, operands), out _);

            [Fact]
            public void ContainsTakesAPathAndOneKeyword()
            {
                Translate(CosmosOperators.FullTextContains, Text(), Keyword("search phrase"))
                    .Should().Be("FULLTEXTCONTAINS(c.text, @p0)");
            }

            [Fact]
            public void ContainsAllTakesEveryKeyword()
            {
                Translate(CosmosOperators.FullTextContainsAll, Text(), Keyword("one"), Keyword("two"), Keyword("three"))
                    .Should().Be("FULLTEXTCONTAINSALL(c.text, @p0, @p1, @p2)");
            }

            [Fact]
            public void ContainsAnyTakesEveryKeyword()
            {
                Translate(CosmosOperators.FullTextContainsAny, Text(), Keyword("one"), Keyword("two"))
                    .Should().Be("FULLTEXTCONTAINSANY(c.text, @p0, @p1)");
            }

            // ── The shared vocabulary ───────────────────────────────────────────────────

            /// <summary>
            /// The <c>CLR_FT_*</c> predicates render as the service's own spellings.
            /// </summary>
            /// <remarks>
            /// <para>
            /// <b>A rename and nothing more,</b> which is the whole of what adopting the shared vocabulary
            /// costs here: the operand shape is identical — a searched path, then keywords — so the same
            /// writer answers both, given the name to emit.
            /// </para>
            /// <para>
            /// The service's own names stay. A query already written against <c>FULLTEXTCONTAINS</c> keeps
            /// working, and a query written against the shared surface now plans here as well as anywhere
            /// else; they are two spellings of one rendering rather than two implementations.
            /// </para>
            /// </remarks>
            [Fact]
            public void TheSharedPredicatesRenderAsTheServiceSpelling()
            {
                Translate(FullTextOperatorTable.ClrFtContains, Text(), Keyword("search phrase"))
                    .Should().Be("FULLTEXTCONTAINS(c.text, @p0)");

                Translate(FullTextOperatorTable.ClrFtContainsAll, Text(), Keyword("one"), Keyword("two"), Keyword("three"))
                    .Should().Be("FULLTEXTCONTAINSALL(c.text, @p0, @p1, @p2)");

                Translate(FullTextOperatorTable.ClrFtContainsAny, Text(), Keyword("one"), Keyword("two"))
                    .Should().Be("FULLTEXTCONTAINSANY(c.text, @p0, @p1)");
            }

            /// <summary>
            /// The shared score is held to the same clause the service's own is.
            /// </summary>
            /// <remarks>
            /// Where a call is legal is the adapter's business, not the vocabulary's — the package says so
            /// and this is that line being drawn. Cosmos permits a score in <c>ORDER BY RANK</c> and
            /// nowhere else, so the shared spelling is refused everywhere the service's own is.
            /// </remarks>
            [Fact]
            public void TheSharedScoreIsRankClauseOnly()
            {
                CanTranslate(FullTextOperatorTable.ClrFtScore, Text(), Keyword("steel"))
                    .Should().BeFalse("a score is not a predicate and not a column");

                Translator().TranslateRank(_rex.makeCall(FullTextOperatorTable.ClrFtScore, Text(), Keyword("steel")))
                    .Should().Be("FULLTEXTSCORE(c.text, @p0)");
            }

            /// <summary>
            /// A phrase is the term itself, because that is what the service reads a multi-word term as.
            /// </summary>
            /// <remarks>
            /// <b>The constructor exists because the bare spelling is not portable</b>, not because Cosmos
            /// needs one: <c>FullTextContains(c.text, "red bicycle")</c> is already a phrase here, while
            /// PostgreSQL's <c>plainto_tsquery</c> reads the same two words as <c>red &amp; bicycle</c>.
            /// So the rendering is the text, and what the constructor buys is that the query said which it
            /// meant.
            /// </remarks>
            [Fact]
            public void APhraseIsTheTermItself()
            {
                Translate(FullTextOperatorTable.ClrFtContains, Text(),
                    _rex.makeCall(FullTextOperatorTable.ClrFtPhrase, Keyword("red bicycle")))
                    .Should().Be("FULLTEXTCONTAINS(c.text, @p0)");

                var parameters = new CosmosParameterList();
                new CosmosRexTranslator(_rex, _fields, parameters).Translate(
                    _rex.makeCall(FullTextOperatorTable.ClrFtContains, Text(),
                        _rex.makeCall(FullTextOperatorTable.ClrFtPhrase, Keyword("red bicycle"))));

                parameters.Parameters.Should().ContainSingle().Which.Value.Should().Be("red bicycle");
            }

            /// <summary>
            /// A fuzzy term is the object form the service documents.
            /// </summary>
            /// <remarks>
            /// <c>{"term": …, "distance": …}</c>, which is documented by the text of <c>SC2241</c> — the
            /// refusal a keyword <em>array</em> earns — and was recorded in <c>DESIGN.md</c> as a form this
            /// adapter did not offer. It does now, because the shared vocabulary gave it a spelling.
            /// Bound as a parameter like every other keyword.
            /// </remarks>
            [Fact]
            public void AFuzzyTermIsTheObjectTheServiceDocuments()
            {
                var parameters = new CosmosParameterList();
                var translator = new CosmosRexTranslator(_rex, _fields, parameters);

                translator.Translate(_rex.makeCall(FullTextOperatorTable.ClrFtContains, Text(),
                    _rex.makeCall(FullTextOperatorTable.ClrFtFuzzy, Keyword("bycycle"),
                        _rex.makeExactLiteral(new java.math.BigDecimal(2)))))
                    .Should().Be("FULLTEXTCONTAINS(c.text, @p0)");

                var value = parameters.Parameters.Should().ContainSingle().Which.Value
                    .Should().BeAssignableTo<System.Collections.Generic.IDictionary<string, object?>>().Which;

                value["term"].Should().Be("bycycle");
                value["distance"].Should().Be(2L);
            }

            /// <summary>
            /// A prefix term is declined, the service having no form for one.
            /// </summary>
            /// <remarks>
            /// <para>
            /// <b>The one thing in the vocabulary Cosmos does not offer.</b> The service matches whole
            /// analyzed terms; a prefix is PostgreSQL's <c>to_tsquery('a:*')</c>, SQL Server's
            /// <c>'"a*"'</c>, FTS5's <c>a*</c>, and nothing here.
            /// </para>
            /// <para>
            /// Declined rather than approximated. <c>STARTSWITH</c> over the same property is not the same
            /// question — it is a substring test over the stored text, where a full text prefix is over
            /// the analyzer's terms — and answering a different question is worse than refusing this one.
            /// The call then has no body, so the query says so.
            /// </para>
            /// </remarks>
            [Fact]
            public void APrefixTermIsDeclined()
            {
                CanTranslate(FullTextOperatorTable.ClrFtContains, Text(),
                    _rex.makeCall(FullTextOperatorTable.ClrFtPrefix, Keyword("mount")))
                    .Should().BeFalse("the service matches whole analyzed terms");
            }

            /// <remarks>
            /// Keywords bind as parameters like any other literal, so the statement text is independent of
            /// what is being searched for.
            /// </remarks>
            [Fact]
            public void KeywordsAreBoundRatherThanInlined()
            {
                var parameters = new CosmosParameterList();
                var translator = new CosmosRexTranslator(_rex, _fields, parameters);

                translator.Translate(_rex.makeCall(CosmosOperators.FullTextContains, Text(), Keyword("phrase")));

                parameters.Parameters.Should().ContainSingle().Which.Value.Should().Be("phrase");
            }

            /// <remarks>
            /// The reference calls the first argument a property path, not an expression. A call over
            /// something with no path is refused rather than rendered into a statement the service rejects.
            /// </remarks>
            [Fact]
            public void APredicateOverAComputedColumnIsDeclined()
            {
                CanTranslate(CosmosOperators.FullTextContains, Computed(), Keyword("phrase")).Should().BeFalse();
            }

            [Fact]
            public void APredicateWithNoKeywordIsDeclined()
            {
                CanTranslate(CosmosOperators.FullTextContains, Text()).Should().BeFalse();
            }

            /// <remarks>
            /// The operator table exists so a query can name these at all; Calcite's standard table has
            /// nothing to resolve them to.
            /// </remarks>
            [Fact]
            public void TheOperatorTableCarriesEveryPredicate()
            {
                var names = new List<string>();
                var operators = CosmosOperators.Instance.getOperatorList();

                for (var i = 0; i < operators.size(); i++)
                    names.Add(((org.apache.calcite.sql.SqlOperator)operators.get(i)).getName());

                names.Should().BeEquivalentTo([
                    "FULLTEXTCONTAINS", "FULLTEXTCONTAINSALL", "FULLTEXTCONTAINSANY",
                    "FULLTEXTSCORE", "RRF", "VECTORDISTANCE",
                    "IS_DEFINED", "IS_ARRAY", "IS_BOOL", "IS_NULL",
                    "IS_NUMBER", "IS_OBJECT", "IS_PRIMITIVE", "IS_STRING",
                    // What has no SQL counterpart to map from: a regular expression dialect, and the
                    // JSON conversions. The array functions are mapped instead — see the translator.
                    "REGEXMATCH",
                    "ToString", "StringToNumber", "StringToObject", "StringToArray", "StringToBoolean", "ObjectToArray",
                ]);
            }

            /// <remarks>
            /// The scoring functions are nameable — a query has to be able to write
            /// <c>ORDER BY FULLTEXTSCORE(…)</c> for <c>CosmosRankRule</c> to recognise the shape — but they
            /// are legal in that clause alone. The translator is what holds them to it, so naming one
            /// anywhere else declines rather than rendering a statement the service rejects.
            /// </remarks>
            [Fact]
            public void AScoringFunctionIsRefusedOutsideARankClause()
            {
                CanTranslate(CosmosOperators.FullTextScore, Text(), Keyword("steel")).Should().BeFalse();
                CanTranslate(CosmosOperators.Rrf, Text(), Keyword("steel")).Should().BeFalse();
            }

            /// <remarks>
            /// And renders in one. This is the only entry point that permits it.
            /// </remarks>
            [Fact]
            public void AScoringFunctionRendersInARankClause()
            {
                Translator().TranslateRank(_rex.makeCall(CosmosOperators.FullTextScore, Text(), Keyword("steel")))
                    .Should().Be("FULLTEXTSCORE(c.text, @p0)");
            }

            /// <remarks>
            /// An ordinary expression is not something to rank by.
            /// </remarks>
            [Fact]
            public void ARankClauseTakesOnlyAScoringFunction()
            {
                var act = () => Translator().TranslateRank(Text());
                act.Should().Throw<CosmosTranslationException>();
            }


            // ── The declaration decides ─────────────────────────────────

            /// <remarks>
            /// The declaration does not gate the call. It did, on a measurement that a predicate over a
            /// path the container declares nothing about answers a bodyless 400; measured again against
            /// three accounts and four containers, the service answers every form over an undeclared
            /// path, so what the container declares is what the call costs and not whether it renders
            /// (#85). A container declaring the path renders as it always did.
            /// </remarks>
            [Fact]
            public void APredicateOverADeclaredPathRenders()
            {
                var container = new CosmosContainerMetadata("products", fullTextPaths: new[] { "/text" });

                Translator(container).Translate(_rex.makeCall(CosmosOperators.FullTextContains, Text(), Keyword("steel")))
                    .Should().Be("FULLTEXTCONTAINS(c.text, @p0)");
            }

            [Fact]
            public void APredicateOverAnUndeclaredPathRendersToo()
            {
                var container = new CosmosContainerMetadata("products", fullTextPaths: new[] { "/text" });

                Translator(container).Translate(_rex.makeCall(CosmosOperators.FullTextContains, Other(), Keyword("steel")))
                    .Should().Be("FULLTEXTCONTAINS(c.note, @p0)");
            }

            /// <remarks>
            /// Every form of the predicate, and the score, over a container that declares nothing at all
            /// — the dev1 <c>parks</c> row of the measurement, which answered every one of them.
            /// </remarks>
            [Fact]
            public void EveryFullTextFormRendersWithoutADeclaration()
            {
                var container = new CosmosContainerMetadata("products");
                var translator = Translator(container);

                translator.TryTranslate(_rex.makeCall(CosmosOperators.FullTextContains, Text(), Keyword("a")), out _).Should().BeTrue();
                translator.TryTranslate(_rex.makeCall(CosmosOperators.FullTextContainsAll, Text(), Keyword("a"), Keyword("b")), out _).Should().BeTrue();
                translator.TryTranslate(_rex.makeCall(CosmosOperators.FullTextContainsAny, Text(), Keyword("a"), Keyword("b")), out _).Should().BeTrue();

                Translator(container).TranslateRank(_rex.makeCall(CosmosOperators.FullTextScore, Text(), Keyword("a")))
                    .Should().Be("FULLTEXTSCORE(c.text, @p0)");
            }

            /// <remarks>
            /// A caller that has not said which container this is written against gets what it always got.
            /// Nothing but the rules and the implementor supplies one, and both of those know.
            /// </remarks>
            [Fact]
            public void WithoutAContainerNothingIsGated()
            {
                Translate(CosmosOperators.FullTextContains, Other(), Keyword("steel"))
                    .Should().Be("FULLTEXTCONTAINS(c.note, @p0)");
            }

            /// <remarks>
            /// A vector distance takes two vectors and either may be a literal — searching for the
            /// neighbours of a supplied embedding is the point — so it is enough that one of them is a
            /// declared path.
            /// </remarks>
            [Fact]
            public void AVectorDistanceNeedsOneDeclaredVector()
            {
                var container = new CosmosContainerMetadata("products", vectorPaths: new[] { "/text" });

                Translator(container).Translate(_rex.makeCall(CosmosOperators.VectorDistance, Text(), Other()))
                    .Should().Be("VECTORDISTANCE(c.text, c.note)");

                Translator(container).Translate(_rex.makeCall(CosmosOperators.VectorDistance, Other(), Text()))
                    .Should().Be("VECTORDISTANCE(c.note, c.text)");

                Translator(container).TryTranslate(_rex.makeCall(CosmosOperators.VectorDistance, Other(), Other()), out _)
                    .Should().BeFalse();
            }

            /// <remarks>
            /// The two are declared separately: a full text path is not a vector path. The other way
            /// round used to be asserted here as well and is no longer a question the translator answers
            /// — a full text function renders over any path, and what the declarations say about it is a
            /// price, which the planner tests check (#85).
            /// </remarks>
            [Fact]
            public void AFullTextDeclarationDoesNotDeclareAVectorPath()
            {
                var fullText = new CosmosContainerMetadata("products", fullTextPaths: new[] { "/text" });

                Translator(fullText).TryTranslate(_rex.makeCall(CosmosOperators.VectorDistance, Text(), Text()), out _).Should().BeFalse();
            }


            // ── Type tests ──────────────────────────────────────────────

            /// <remarks>
            /// A container has no row schema, so what a property holds is a question about the document.
            /// These are the functions that ask, and every one was accepted by the emulator.
            /// </remarks>
            [Fact]
            public void TypeTestsRenderUnderTheirOwnNames()
            {
                Translate(CosmosOperators.IsDefined, Text()).Should().Be("IS_DEFINED(c.text)");
                Translate(CosmosOperators.IsArray, Text()).Should().Be("IS_ARRAY(c.text)");
                Translate(CosmosOperators.IsBool, Text()).Should().Be("IS_BOOL(c.text)");
                Translate(CosmosOperators.IsNull, Text()).Should().Be("IS_NULL(c.text)");
                Translate(CosmosOperators.IsNumber, Text()).Should().Be("IS_NUMBER(c.text)");
                Translate(CosmosOperators.IsObject, Text()).Should().Be("IS_OBJECT(c.text)");
                Translate(CosmosOperators.IsPrimitive, Text()).Should().Be("IS_PRIMITIVE(c.text)");
                Translate(CosmosOperators.IsString, Text()).Should().Be("IS_STRING(c.text)");
            }

            /// <remarks>
            /// Unlike a full text predicate, the argument is an ordinary expression: asking the type of a
            /// computed value is meaningful, so nothing requires it to be a path.
            /// </remarks>
            [Fact]
            public void ATypeTestOverAComputedColumnIsStillDeclined()
            {
                // Not because the function objects, but because the ordinal has no path to render at all.
                CanTranslate(CosmosOperators.IsDefined, Computed()).Should().BeFalse();
            }

        }

    }

}
