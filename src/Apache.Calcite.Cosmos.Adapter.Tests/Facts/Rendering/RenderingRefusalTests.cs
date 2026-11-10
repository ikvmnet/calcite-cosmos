using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.EndToEnd;
using Apache.Calcite.Cosmos.Facts.Rendering;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// The translator's refusals a <c>WHERE</c> without parameters reaches, each held by S or the loader since the switch
    /// (h15): one statement a refusal (spec Appendix E, by the refusal's line there), planned for each target, every
    /// conjunct of every statement its filter reads declined or S's proven form, never one the translator pushed. A
    /// deletion commit runs it against the refusals its members held (h16, h22, h27, h37, h42); h16's held none. Not
    /// here: a parameter's (h19), a column's (h31), a rank's, a geography's and a vector's (h40), a literal's
    /// representation and a field with no path, which no <c>WHERE</c> over these containers spells.
    /// </summary>
    public sealed class RenderingRefusalTests
    {

        const string Where = "SELECT c.\"id\" FROM {0} AS c WHERE ";

        /// <summary>The refusal, as Appendix E lines it, and a statement that reaches it over the differential's containers.</summary>
        static readonly (string Refusal, string Sql)[] Refusals =
        [
            (":883 text rendering, equality", W("typed", "JSON_VALUE(c.\"DOC\", '$.price') = '30'")),
            (":4026 text rendering, ordering", W("typed", "JSON_VALUE(c.\"DOC\", '$.label') < 'c'")),
            (":4989 text rendering, LIKE", W("typed", "JSON_VALUE(c.\"DOC\", '$.price') LIKE '3%'")),
            (":931 string function, unmeasured characters", W("strings", "UPPER(JSON_VALUE(c.\"DOC\", '$.s')) = 'ASSB'")),
            (":1081 unsupported node", W("products", "JSON_VALUE(c.\"DOC\", '$.name') SIMILAR TO 'K%'")),
            (":1197 past 2^53", W("declared", "JSON_VALUE(c.\"DOC\", '$.v' RETURNING BIGINT) = 9007199254740993")),
            (":1347 JSON_QUERY in an expression", W("products", "CHAR_LENGTH(JSON_QUERY(c.\"DOC\", '$.metadata')) > 2")),
            (":1828 RETURNING DECIMAL", W("declared", "JSON_VALUE(c.\"DOC\", '$.body' RETURNING DECIMAL) = 2")),
            (":2034 integer division", W("products", "JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) / 2 = 2")),
            (":2069 decimal arithmetic", W("products", "JSON_VALUE(c.\"DOC\", '$.price' RETURNING DECIMAL(10, 2)) * 1.1 > 100")),
            (":3953 cast to text", W("products", "CAST(JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) AS VARCHAR) = '120'")),
            (":3964 cast truncating", W("products", "CAST(JSON_VALUE(c.\"DOC\", '$.price' RETURNING DOUBLE) AS INTEGER) = 120")),
            (":4035 equality answered otherwise", W("typed", "JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) = 30")),
            (":4073 ordering answered otherwise", W("typed", "JSON_VALUE(c.\"DOC\", '$.price' RETURNING INTEGER) > 8")),
            (":4816 truth test, undeclared", W("typed", "JSON_VALUE(c.\"DOC\", '$.label' RETURNING BOOLEAN) IS TRUE")),
            (":4926 IS NULL over a predicate", W("products", "(JSON_VALUE(c.\"DOC\", '$.category') <> 'bikes') IS NULL")),
            (":4967 LIKE ESCAPE", W("products", "JSON_VALUE(c.\"DOC\", '$.name') LIKE 'K!%%' ESCAPE '!'")),
            (":4992 LIKE computed pattern", W("products", "JSON_VALUE(c.\"DOC\", '$.name') LIKE JSON_VALUE(c.\"DOC\", '$.category')")),
            (":4995 LIKE bracket", W("products", "JSON_VALUE(c.\"DOC\", '$.name') LIKE '[K]%'")),
            (":5114 ITEM with a computed subscript", W("products", "JSON_QUERY(c.\"DOC\", '$.tags' RETURNING VARCHAR ARRAY)[CHAR_LENGTH(c.\"id\")] = 'outdoor'")),
            (":5937 CARDINALITY over a map", W("products", "CARDINALITY(MAP['k', JSON_VALUE(c.\"DOC\", '$.name')]) = 1")),
            (":5483 unsupported function", W("products", "SOUNDEX(JSON_VALUE(c.\"DOC\", '$.name')) = 'K523'")),
            (":5511 REPEAT past the limit", W("products", "REPEAT(JSON_VALUE(c.\"DOC\", '$.name'), 20000) = 'x'")),
            (":5511 REPEAT negative", W("products", "REPEAT(JSON_VALUE(c.\"DOC\", '$.name'), -1) = ''")),
            (":5526 REPLACE empty search", W("products", "REPLACE(JSON_VALUE(c.\"DOC\", '$.name'), '', 'x') = 'Kickstand'")),
            (":5543 concatenation over a null", W("products", "JSON_VALUE(c.\"DOC\", '$.name') || JSON_VALUE(c.\"DOC\", '$.missing') = 'x'")),
            (":5814 SUBSTRING without a length", W("strings", "SUBSTRING(JSON_VALUE(c.\"DOC\", '$.a') FROM 2) = 'Bc'")),
            (":5912 POSITION with a start", W("strings", "POSITION('b' IN JSON_VALUE(c.\"DOC\", '$.b') FROM 2) = 3")),
            (":6270 TRIM of another character", W("strings", "TRIM(LEADING 'a' FROM JSON_VALUE(c.\"DOC\", '$.a')) = 'Bc'")),
        ];

        static string W(string container, string where) => string.Format(Where, container) + where;

        /// <summary>
        /// Every conjunct a refusal's statement admits, under either target, is one S owns and admitted in a form it proved:
        /// the rest are declined, the translator's own refusals among them (an opaque term's, until h42).
        /// </summary>
        [Fact]
        public void EveryRefusalTheWhereReachesIsDeclinedOrProven()
        {
            var pushed = new List<string>();
            foreach (var target in new[] { CosmosServiceTarget.Azure, CosmosServiceTarget.Emulator })
            {
                var containers = CosmosDifferentialTests.Containers.Select(c => c.WithReasoning(new CosmosReasoning()).WithServiceTarget(target)).ToArray();
                foreach (var (refusal, sql) in Refusals)
                {
                    var recorder = new RenderingRecorder();
                    using (recorder.Arm())
                        RenderingInputs.Differential("refusals", [sql], containers).Single().Plan();

                    recorder.Read.Should().NotBeEmpty("{0} reaches a filter under {1}", refusal, target);
                    foreach (var statement in recorder.Read)
                        for (var i = 0; i < statement.Conjuncts.Count; i++)
                            if (statement.IsAdmitted(i) && (RenderingFamily.Owns(statement.Statement.Conjuncts[i]) == false || statement.Form(i) is null))
                                pushed.Add($"{refusal} under {target}: {statement.Statement.Conjuncts[i].Digest}");
                }
            }

            pushed.Should().BeEmpty("no refusal's conjunct is pushed but in a form S proved");
        }

    }

}
