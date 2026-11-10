using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;

using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

using Xunit;

using static Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering.RenderingRexBuilderTests;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    public sealed partial class RenderingGenerateMutationTests
    {

        static readonly JsonDocumentPath Tags = JsonDocumentPath.Root.Property("tags");

        /// <summary>
        /// M21's plants (h13, line 16): the loader's element read one on, one back, or as the key its index spells, which is
        /// the object's property where Calcite reads the array's element.
        /// </summary>
        static readonly Dictionary<string, Func<JsonDocumentPath, JsonDocumentPath>> ItemPlants = new()
        {
            ["one on"] = _ => Tags.Element(2),
            ["one back"] = _ => Tags.Element(0),
            ["a key as the index"] = _ => Tags.Property("1"),
        };

        /// <summary>Documents an element read meets: arrays long and short, an object keyed by the index, a string, nothing.</summary>
        static readonly string[] ItemDocuments =
        [
            """{"tags":["a","b"]}""", """{"tags":["b","a"]}""", """{"tags":["a","c","b"]}""", """{"tags":["b","b","x"]}""", """{"tags":["a"]}""",
            """{"tags":{"1":"b"}}""", """{"tags":"ab"}""", """{"tags":[]}""", """{"tags":null}""", """{}""",
        ];

        /// <summary>
        /// M21: <c>JSON_VALUE(DOC, '$.tags[1]') = 'b'</c> read by the loader as the element's comparison, whose meaning IrEval
        /// gives as Calcite's own executor does on every document; each plant's read, its element moved, parts from Calcite on
        /// some document, which is what source A, reading IrEval as Calcite, then judges against.
        /// </summary>
        [Fact]
        public void M21AnElementReadOffItsIndexPartsFromCalcite()
        {
            var rex = Rex.makeCall(SqlStdOperatorTable.EQUALS, JsonValue("tags[1]", SqlTypeName.VARCHAR), Rex.makeLiteral("b"));
            var read = new CosmosPredicateLoader.Loading(Fields, Readings, CosmosImplementor.DefaultRootAlias).Read(rex);
            read.Places.Should().Equal([Tags.Element(1)], "the loader reads the index as an element");
            var calcite = CalciteInProcess.Compile(rex, Fields, Readings) ?? throw new InvalidOperationException("Calcite cannot compile the comparison alone.");
            var documents = ItemDocuments.Select(j => (J.Obj)CosmosEvalServiceTests.Fixture.Read(System.Text.Json.JsonDocument.Parse(j).RootElement)!).ToList();

            // Where IrEval's truths over every typed mode are not Calcite's: a document that parts the read from Calcite.
            List<string> Parted(SqlPredicate ir) => [.. documents
                .Select(d => (d, Want: calcite(d), Got: RenderingSoundnessTests.Tally.Calcite(ir, d, new World(13), raises: true)))
                .Where(x => x.Want is { } want && x.Got is { } got && got.Any(t => t != want))
                .Select(x => $"{Documents.Json(x.d)}: Calcite {x.Want}, read {string.Join("|", x.Got!)}")];

            Parted(read).Should().BeEmpty("unplanted, the read means what Calcite computes");
            foreach (var (name, move) in ItemPlants)
            {
                var parted = Parted(Moved(read, move));
                TestContext.Current.TestOutputHelper?.WriteLine($"M21 {name}: {parted.Count} documents part; {string.Join("; ", parted.Take(3))}");
                parted.Should().NotBeEmpty($"M21 {name} is caught");
            }
        }

        /// <summary>A predicate with each path it reads moved as given.</summary>
        static SqlPredicate Moved(SqlPredicate p, Func<JsonDocumentPath, JsonDocumentPath> move) => p switch
        {
            SqlPredicate.Compare c => c with { Left = Moved(c.Left, move), Right = Moved(c.Right, move) },
            SqlPredicate.Not n => n with { Operand = Moved(n.Operand, move) },
            _ => throw new NotSupportedException(p.ToString()),
        };

        static SqlValue Moved(SqlValue v, Func<JsonDocumentPath, JsonDocumentPath> move) => v switch
        {
            SqlValue.Text t => new SqlValue.Text(move(t.Path)),
            SqlValue.Raw r => new SqlValue.Raw(move(r.Path)),
            SqlValue.Typed t => t with { Path = move(t.Path) },
            SqlValue.Cast c => c with { Value = Moved(c.Value, move) },
            _ => v,
        };

    }

}
