using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Adapter.Tests.Infrastructure;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;
using Apache.Calcite.Data;
using Apache.Calcite.Extensions.Runtime;

using FluentAssertions;
using FluentAssertions.Execution;

using Microsoft.Azure.Cosmos;

using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

using Xunit;

using JavaTypeFactoryImpl = org.apache.calcite.jdbc.JavaTypeFactoryImpl;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// What the process keeps: every static field of the adapter, each classed with its reason, and none of them
    /// something a schema learns — that hangs off the schema (<see cref="CosmosReasoning"/>, docs chapter 6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The line is learned against fixed.</b> A static may hold what is fixed in the assembly, or a pure function
    /// of it over a bounded domain: rules, the measured registry and the definition read from it, code tables,
    /// sentinels. It may not hold what grows with what is asked or depends on a declaration: graphs, interned
    /// values, compiled declarations, oracles and their memos. The instance tables those live in are the schema's,
    /// and <see cref="CosmosReasoningTests.ASchemaLetGoTakesItsGraphsWithIt"/> finds them collected with it, S
    /// asked on the test's own thread among them, so no thread static of the engine's keeps them either.
    /// </para>
    /// <para>
    /// <b>Three checks, each for what the others cannot see.</b> A static's declared type is followed through every field
    /// of the adapter's types it can hold, which catches a table or bound metadata however deep; its value is walked after
    /// a schema has worked and gone, which catches what a declared <c>object</c> or a Calcite object holds; and a weak
    /// table, whose entries are a running test's while their keys live, names the test that finds them released. What a
    /// weak table's named test shows is read by whoever reviews the line, not by this test.
    /// </para>
    /// <para>
    /// <b>Compiler-generated types are read, not listed.</b> Their statics are lambda caches, each class's
    /// singleton of itself and array data, which the test checks; an auto-property's backing field is listed by its
    /// property's name.
    /// </para>
    /// <para>
    /// <b>The engine's process-wide statics</b> (Eggnog 1.1.1) are its own: pools cleared on return
    /// (<c>PoolSet.Shared</c>, <c>SharedPool.Instance</c>), weak tables on a graph's own objects
    /// (<c>DatabaseView</c>, <c>PreparedBlock</c>), primitive caches and option defaults. Its thread statics are
    /// listed here too, since one of them holds a scope.
    /// </para>
    /// </remarks>
    public sealed class CosmosStaticsTests
    {

        /// <summary>The classes a static may fall in.</summary>
        enum Kind
        {

            /// <summary>Fixed in the assembly, or a pure function of it over a bounded domain.</summary>
            StaticFact,

            /// <summary>Telemetry, reflection handles, settings, stateless helpers.</summary>
            Infra,

            /// <summary>What a test arms for its own flow, which production never does.</summary>
            TestArm,

            /// <summary>A weak table keyed on what dies with a plan, a graph or a connection, and released with it.</summary>
            PlanScoped,

        }

        const Kind StaticFact = Kind.StaticFact;
        const Kind Infra = Kind.Infra;
        const Kind TestArm = Kind.TestArm;
        const Kind PlanScoped = Kind.PlanScoped;

        const string Reflection = "a reflection handle the generated code calls";
        const string Builder = "a builder over a type factory of its own: types are Calcite's interned values, and no weak table is keyed on what it makes";

        /// <summary>
        /// Every static field of the adapter, by type relative to <c>Apache.Calcite.Cosmos</c>, classed, with why.
        /// </summary>
        static readonly (string Type, string[] Fields, Kind Kind, string Reason)[] Classed =
        [
            ("Adapter.Client.CosmosDocument", ["ServiceProperties"], StaticFact, "the properties the service writes on every item"),
            ("Adapter.Client.CosmosInstrumentation", ["ActivitySource", "Meter", "RequestCharge", "Responses"], Infra, "telemetry, which no plan reads"),
            ("Adapter.Client.CosmosJson", ["UnixEpochDay"], StaticFact, "a date"),
            ("Adapter.CosmosModelClasses", ["Patterns"], StaticFact, "the classes a model may name"),
            ("Adapter.CosmosSchemaFactory", ["Mapper"], Infra, "Jackson's mapper for model operands, its caches keyed by Java types"),

            ("Adapter.Metadata.CosmosConstraintSet", ["Empty"], StaticFact, "the empty set"),
            ("Adapter.Metadata.CosmosContainerMetadata", ["DefaultIncludedPatterns", "DefaultStatisticsTimeToLive", "ServiceExcludedPatterns"], StaticFact, "the service's indexing defaults, and a default lifetime"),
            ("Adapter.Metadata.CosmosContainerMetadata", ["ServiceFacts"], StaticFact, "the service's guarantees, unbound: rules and their encoding, which compile nothing until a schema binds a copy"),
            ("Adapter.Metadata.CosmosFactRewriter", ["Rewrites"], PlanScoped, "keyed weakly on a statement, which Read keeps only while its container lives"),
            ("Adapter.Metadata.CosmosFilterStatement", ["Read"], PlanScoped, "keyed weakly on the condition, then on the container, so it keeps neither whatever condition a caller passes"),
            ("Adapter.Metadata.CosmosNumericForms", ["Digits", "IntegerUnpadded", "Unpadded"], StaticFact, "numeric spellings"),
            ("Adapter.Metadata.CosmosPartitionKeyExtractor", ["IdPolicy"], StaticFact, "the id's path"),
            ("Adapter.Metadata.CosmosPartitionKeyExtractor.RexBuilderHolder", ["Value"], Infra, Builder),
            ("Adapter.Metadata.CosmosPatternReader.Parser", ["Digit", "Word"], StaticFact, "character classes"),
            ("Adapter.Metadata.CosmosPredicateLoader.RexBuilderHolder", ["Value"], Infra, Builder),
            ("Adapter.Metadata.CosmosRendering", ["Defined"], StaticFact, "S's definition, read once from the measured registry and CosmosStrictness; every schema's S is defined from it"),
            ("Adapter.Metadata.CosmosRenderingCapture", ["Armed", "Current"], TestArm, "the observer a test arms for its flow"),
            ("Adapter.Metadata.CosmosRowFacts", ["Decided"], PlanScoped, "keyed weakly on a plan's node, which reaches the table whose container it remembers; internal, asked by the planner alone"),
            ("Adapter.Metadata.CosmosSchemaRecognition", ["Cosmos"], StaticFact, "the recognisers of Cosmos DB's schema keywords"),
            ("Adapter.Metadata.CosmosServiceFacts", ["Properties", "Stated"], StaticFact, "what the service guarantees of its own properties, as rules"),
            ("Adapter.Metadata.CosmosServiceOrder", ["Int64Least", "Int64Most", "TwoTo53"], StaticFact, "numeric bounds"),
            ("Adapter.Metadata.CosmosStrictness", ["Operators", "Readers"], StaticFact, "read once from Calcite's static operator tables and the registry"),
            ("Adapter.Metadata.CosmosTemporalForms", ["EngineTargets", "Iso8601Date", "Iso8601UtcMicroseconds", "Iso8601UtcMilliseconds", "Iso8601UtcSeconds", "Iso8601UtcTicks", "ParseDigits", "Temporal"], StaticFact, "temporal spellings and their parses, written by the type initializer alone"),
            ("Adapter.Metadata.CosmosUuidForms", ["CanonicalLower", "CanonicalLowerSortable", "CanonicalUpper", "CanonicalUpperSortable", "Hyphens", "Uuids"], StaticFact, "UUID spellings"),
            ("Adapter.Metadata.CosmosUuidForms", ["UnsignedUuidComparison"], Infra, "Calcite's system property, read once: the process's configuration"),

            ("Adapter.Rel.Convert.CosmosConverters", ["Announced"], PlanScoped, "keyed weakly on one implementation's implementor, which dies with the implementation"),
            ("Adapter.Rel.Convert.CosmosConverters", ["GetExecutorMethod", "GetJsonPropertyMethod", "GetJsonTextPropertyMethod", "GetLookupCacheMethod", "GetPartitionCounterMethod", "GetPathMethod", "GetPropertyMethod", "GetTextPropertyMethod", "GetWriterMethod"], Infra, Reflection),
            ("Adapter.Rel.Convert.CosmosSelfJoinRule", ["Instances"], Infra, "the rule's instances, whose fields are their configuration"),
            ("Adapter.Rel.Convert.CosmosSortProjectTransposeRule", ["Instance"], Infra, "a rule with no fields"),
            ("Adapter.Rel.Convert.CosmosToClrCursorConverter", ["OpenAsyncMethod", "OpenMethod"], Infra, Reflection),
            ("Adapter.Rel.CosmosConstraintCompiler", ["Cache"], PlanScoped, "keyed weakly on the type factory, then on the table, so it keeps neither whatever factory a caller passes"),
            ("Adapter.Rel.CosmosFilter.RexBuilderHolder", ["Value"], Infra, Builder),
            ("Adapter.Rel.CosmosLookupJoin", ["JoinAsyncMethod", "JoinMethod"], Infra, Reflection),
            ("Adapter.Rel.CosmosTableModify", ["NullableMethod", "WriteAsyncMethod", "WriteMethod"], Infra, Reflection),

            ("Adapter.Sql.CosmosAxiomRows", ["Owed", "Targets"], StaticFact, "the registry's rows owed a target, and the targets"),
            ("Adapter.Sql.CosmosAxioms", ["All", "Family", "NoTruth", "Orders", "Stored", "Strings", "Today"], StaticFact, "the measured registry and the dates it was measured on"),
            ("Adapter.Sql.CosmosOperators", ["FullTextContains", "FullTextContainsAll", "FullTextContainsAny", "FullTextScore", "Instance", "IsArray", "IsBool", "IsDefined", "IsNull", "IsNumber", "IsObject", "IsPrimitive", "IsString", "ObjectToArray", "RegexMatch", "Rrf", "ServiceOnly", "StringToArray", "StringToBoolean", "StringToNumber", "StringToObject", "ToStringFunction", "VectorDistance"], StaticFact, "the adapter's operators and their table"),
            ("Adapter.Sql.CosmosRexTranslator", ["ArrayReading", "DirectFunctions", "FragmentReading", "GeographyFunctions", "ScalarReading"], StaticFact, "the functions the translator writes as they are"),
            ("Adapter.Sql.CosmosSchemaFunctions", ["Instance"], StaticFact, "the functions a schema offers"),
            ("Adapter.Sql.CosmosSql", ["ReservedWords"], StaticFact, "Cosmos SQL's reserved words"),
            ("Adapter.Sql.CosmosStringFunctions", ["Counted", "Mapped", "Searched", "Trimmed", "Verdicts"], StaticFact, "what the service's string functions were measured to refuse"),
            ("Adapter.Sql.CosmosTranslatorFaults", ["Armed", "Current"], TestArm, "the translator's fault a test plants for its flow"),
            ("Adapter.Sql.CosmosTypeTestImplementor", ["Bodies"], StaticFact, "the in-process bodies of the type tests"),

            ("Facts.AdmissionRound.Answer", ["None"], StaticFact, "the answer that admits nothing, immutable"),
            ("Facts.JsonDocumentPath", ["Root"], StaticFact, "the root path"),
            ("Facts.JsonFactSet", ["Empty"], StaticFact, "the empty set"),
            ("Facts.JsonFactTheory", ["Empty"], StaticFact, "the theory of no rules, unbound, which compiles nothing"),
            ("Facts.JsonFactTheory", ["Told"], Infra, "the engine's limits for telling a declaration's facts"),
            ("Facts.JsonScalar", ["Null"], StaticFact, "JSON's null"),
            ("Facts.JsonSchemaRecognisers", ["None"], StaticFact, "no recognisers"),
            ("Facts.JsonSchemaResolver", ["Data", "DefaultBase"], StaticFact, "JSON Schema's own keywords and base"),
            ("Facts.JsonScopeTables", ["Asking"], Infra, "the engine's limits for asking a context"),
            ("Facts.OracleLicence", ["Declaration", "GroupBy", "Kept", "Key", "OrderBy", "Select"], StaticFact, "licences, immutable"),
            ("Facts.OracleVerdict", ["Always", "Contradicted", "Unknown"], StaticFact, "verdicts, immutable"),
            ("Facts.StatementTerms", ["Default"], Infra, "the engine's limits for a statement's runs"),
            ("Facts.StatementTerms.Contradiction", ["Tables"], StaticFact, "a singleton with no fields"),
            ("Facts.StatementTerms.Node", ["Nothing"], StaticFact, "the empty set"),

            ("Facts.Rendering.AgreementRows", ["Allowing", "Classes", "Refuting"], StaticFact, "S's agreement boxes"),
            ("Facts.Rendering.AnswerMeanings", ["Measured"], StaticFact, "what each answer was measured to mean"),
            ("Facts.Rendering.CellOutcomes", ["WholeCondition"], StaticFact, "a sentinel"),
            ("Facts.Rendering.KleeneTables", ["Booleans", "Identity", "Operands", "Opposite", "Rows"], StaticFact, "Kleene logic as rows"),
            ("Facts.Rendering.Priced", ["None", "Unpriced"], StaticFact, "sentinels"),
            ("Facts.Rendering.ReaderEdgesTable", ["Rows"], StaticFact, "each reader's edges"),
            ("Facts.Rendering.ReadingPrecisionTable", ["Rows"], StaticFact, "each reading's precision"),
            ("Facts.Rendering.RenderingAnswerMemo", ["Digests"], PlanScoped, "keyed weakly on a generation: its rows' hash, a function of the definition"),
            ("Facts.Rendering.RenderingCost", ["None"], StaticFact, "a sentinel"),
            ("Facts.Rendering.RenderingGraph", ["DefaultRetry"], Infra, "a setting"),
            ("Facts.Rendering.RenderingOracle", ["DefaultWait"], Infra, "a setting"),
            ("Facts.Rendering.RenderingPrimitives", ["BestIsImplied", "BestIsNotImplied", "FormDecomposes", "FormHeads", "FormHolds", "FormIsCompound", "FormIsJoinOf", "FormIsLeaf", "FormJoins", "FormRegroups", "OutcomesWithin", "PricedIsReal", "RexWritable", "Sendable", "SetNonempty", "StringComparison", "StringOrdering", "TruthsWithin"], StaticFact, "pure functions S's rules call"),
            ("Facts.Rendering.RenderingRexGrammar", ["TypeTests", "Written"], StaticFact, "what a Rex writes"),
            ("Facts.Rendering.RenderingTemplateRows", ["Fits", "Templates"], StaticFact, "the templates and the shapes each fits"),
            ("Facts.Rendering.RenderingTemplates", ["Unspelled"], StaticFact, "a sentinel"),
            ("Facts.Rendering.ServiceForm", ["Leaf", "Unjoined", "Unspanned"], StaticFact, "sentinels"),
            ("Facts.Rendering.ServiceTable", ["Views"], PlanScoped, "keyed weakly on a generation's interned table: an index of it, a function of the definition"),

            ("Facts.Saturation.JsonContexts", ["Armed", "Reporting"], TestArm, "the reports a measurement asks for its flow"),
            ("Facts.Saturation.JsonPathSpec", ["Every", "Root"], StaticFact, "the root and the every-element step"),
            ("Facts.Saturation.JsonValues", ["AllBands", "AllTypes", "CharacterClasses", "ClassSets", "NoBand", "Numbers", "Scalars", "Trimmed"], StaticFact, "JSON's types and character bands"),
            ("Facts.Saturation.PathCell", ["Units"], StaticFact, "the units each class set allows: at most 32 keys over five fixed bands, each value a function of its key"),
            ("Facts.Saturation.PathValues", ["AllKinds", "Anything", "AnythingThere", "Lattice", "Rendered"], StaticFact, "the lattice and its tops"),
            ("Facts.Saturation.PredicateAtom", ["Parsed"], StaticFact, "each reader spelling's parse: keys from a finite grammar, values stateless"),
            ("Facts.Saturation.PredicateAtom.Reading.NumberCast", ["Converts", "DecimalRows", "TextRows"], StaticFact, "a reader's rows"),
            ("Facts.Saturation.PredicateAtom.Reading.Raw", ["Rows"], StaticFact, "a reader's rows"),
            ("Facts.Saturation.PredicateAtom.Reading.RawText", ["Rows"], StaticFact, "a reader's rows"),
            ("Facts.Saturation.PredicateAtom.Reading.Text", ["Rows"], StaticFact, "a reader's rows"),
            ("Facts.Saturation.PredicateAtom.Reading.Typed", ["Rows"], StaticFact, "a reader's rows"),
            ("Facts.Saturation.PredicateAtom.State", ["AbsentState", "NullState"], StaticFact, "singletons with no fields"),
            ("Facts.Saturation.PredicateRules", ["Defined"], PlanScoped, "keyed weakly on a graph's rules (its process-wide lock is TODO.md's)"),
            ("Facts.Saturation.RawColumnsTable", ["Rows"], StaticFact, "the raw columns"),
            ("Facts.Saturation.Renderings", ["JavaTrims"], StaticFact, "what Java's trim removes"),
            ("Facts.Saturation.StringCalls", ["Every", "First", "Functions"], StaticFact, "how each string function draws its operands' characters"),
        ];

        /// <summary>
        /// Learned and process-wide, and leaving in a pull request of its own: listed so that the test holds the rest,
        /// and failing once the field has gone, so that the line goes with it. None is left: the last,
        /// <c>CosmosFunctionBodies.Paths</c>, went in #243.
        /// </summary>
        static readonly (string Type, string Field, string Reason)[] Leaving = [];

        /// <summary>
        /// Facts whose type could hold what a schema learns and whose value holds none of it: a theory a static holds is
        /// rules alone, bound to no generations and with no oracle, and the empty set reads no scope. Their values are
        /// <see cref="NoStaticReachesWhatASchemaLearned"/>'s to check, and one whose type no longer could leaves the list.
        /// </summary>
        static readonly (string Key, string Reason)[] Unbound =
        [
            ("Adapter.Metadata.CosmosContainerMetadata.ServiceFacts", "the service's guarantees, bound to no generations until a schema binds a copy"),
            ("Facts.JsonFactSet.Empty", "the empty set, over no scope"),
            ("Facts.JsonFactTheory.Empty", "the theory of no rules, bound to no generations"),
        ];

        /// <summary>
        /// Each weak table, and the test that finds its entries gone once what they remember is let go: being weak on a key
        /// says nothing of how long the key lives, so each names the test that shows its key cannot outlive the table, the
        /// graph or the reasoning its entries reach.
        /// </summary>
        static readonly (string Key, string Test)[] Released =
        [
            ("Adapter.Metadata.CosmosFactRewriter.Rewrites", nameof(AStatementRememberedUnderALongLivedConditionKeepsNoTable)),
            ("Adapter.Metadata.CosmosFilterStatement.Read", nameof(AStatementRememberedUnderALongLivedConditionKeepsNoTable)),
            ("Adapter.Metadata.CosmosRowFacts.Decided", nameof(NoStaticReachesWhatASchemaLearned)),
            ("Adapter.Rel.Convert.CosmosConverters.Announced", nameof(NoStaticReachesWhatASchemaLearned)),
            ("Adapter.Rel.CosmosConstraintCompiler.Cache", nameof(AConstraintRememberedUnderALongLivedFactoryKeepsNoTable)),
            ("Facts.Rendering.RenderingAnswerMemo.Digests", nameof(CosmosReasoningTests.ASchemaLetGoTakesItsGraphsWithIt)),
            ("Facts.Rendering.ServiceTable.Views", nameof(CosmosReasoningTests.ASchemaLetGoTakesItsGraphsWithIt)),
            ("Facts.Saturation.PredicateRules.Defined", nameof(CosmosReasoningTests.ASchemaLetGoTakesItsGraphsWithIt)),
        ];

        /// <summary>
        /// The engine's thread statics at Eggnog 1.1.1, none of which keeps a graph past the call that set it.
        /// </summary>
        static readonly (string Type, string[] Fields, string Reason)[] EngineThreadStatics =
        [
            ("Eggnog.ExtractionCatalog", ["currentScope"], "the scope an extraction runs in, set around it and put back in a finally (Extractor.cs:88-108); the adapter extracts nothing"),
            ("Eggnog.EngineCallMarker", ["depth"], "a count"),
            ("Eggnog.Relations.ActionScratch", ["cells", "slots"], "scratch arrays of integers"),
            ("Eggnog.Relations.ConcurrentChildren", ["leasesHeld"], "a count"),
            ("Eggnog.Relations.ContextTables", ["keyScratch"], "a scratch array of integers"),
            ("Eggnog.Relations.OffsetBufferPool", ["cache"], "integer buffers a thread keeps for reuse"),
            ("Eggnog.Relations.SortedWritesTable", ["keyScratch"], "a scratch array of integers"),
            ("Eggnog.Relations.TableView", ["scratch"], "a scratch array of integers"),
            ("Eggnog.Relations.ThreadOperationCounters", ["current"], "counters"),
        ];

        /// <summary>
        /// What a static may hold only as a weak table's key: a schema's reasoning and what it is made of.
        /// </summary>
        static readonly Type[] Learned =
        [
            typeof(CosmosReasoning), typeof(JsonFactGenerations), typeof(JsonFactGenerations.Generation), typeof(CosmosOracle),
            typeof(StatementTerms), typeof(JsonContexts), typeof(RenderingOracle), typeof(RenderingGraph), typeof(RenderingTheory),
            typeof(RenderingAnswerMemo), typeof(Eggnog.EGraph), typeof(Eggnog.Scope),
        ];

        /// <summary>
        /// Every static field of the adapter is classed, and none can hold what a schema learns: a fact is read-only, a weak
        /// table is a weak table and names the test that finds its entries released, only a test's arm flows with a thread
        /// or an async flow, and no other static's type can reach a graph, an oracle, a memo or a reasoning, through any
        /// field of any type of the adapter's it can hold, but as a weak table's key. A static not classed fails, so a new
        /// one has to be classed here, with its reason; what the values reach is <see cref="NoStaticReachesWhatASchemaLearned"/>'s.
        /// </summary>
        [Fact]
        public void StaticsHoldNoLearnedState()
        {
            using var all = new AssertionScope();
            var assembly = typeof(CosmosReasoning).Assembly;
            var found = Statics(assembly).ToList();
            var keys = found.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

            var classed = Classed.SelectMany(c => c.Fields.Select(f => (Key: c.Type + "." + f, c.Kind))).ToList();
            var leaving = Leaving.Select(l => l.Type + "." + l.Field).ToList();
            classed.Select(c => c.Key).Concat(leaving).GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key)
                .Should().BeEmpty("a field is classed once");

            var kinds = classed.ToDictionary(c => c.Key, c => c.Kind, StringComparer.Ordinal);
            found.Where(f => !kinds.ContainsKey(f.Key) && !leaving.Contains(f.Key)).Select(f => $"{f.Key} ({f.Field.FieldType})")
                .Should().BeEmpty("every static field of the adapter is classed, with its reason");
            kinds.Keys.Concat(leaving).Where(k => !keys.Contains(k))
                .Should().BeEmpty("a field gone takes its line with it");

            Kind? KindOf(string key) => kinds.TryGetValue(key, out var kind) ? kind : null;
            found.Where(f => KindOf(f.Key) is StaticFact or PlanScoped && !f.Field.IsInitOnly).Select(f => f.Key)
                .Should().BeEmpty("a fact and a weak table are written by the type initializer alone");
            found.Where(f => KindOf(f.Key) == PlanScoped && !IsGeneric(f.Field.FieldType, typeof(ConditionalWeakTable<,>))).Select(f => f.Key)
                .Should().BeEmpty("what dies with a plan, a graph or a connection is kept only in a table weak on it");
            found.Where(f => Flows(f.Field) && KindOf(f.Key) != TestArm).Select(f => f.Key)
                .Should().BeEmpty("only a test's arm flows with a thread or an async flow");
            assembly.GetTypes().SelectMany(t => t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)).Where(Flows)
                .Select(f => $"{f.DeclaringType}.{f.Name}").Should().BeEmpty("nothing flows with a thread or an async flow but a test's arm, which is static");

            var released = Released.ToDictionary(r => r.Key, r => r.Test, StringComparer.Ordinal);
            found.Where(f => KindOf(f.Key) == PlanScoped && !released.ContainsKey(f.Key)).Select(f => f.Key)
                .Should().BeEmpty("a weak table names the test that finds its entries gone with what they remember");
            released.Keys.Where(k => KindOf(k) != PlanScoped).Should().BeEmpty("only a weak table names a test that releases it");
            var tests = typeof(CosmosStaticsTests).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                .Where(m => m.IsDefined(typeof(FactAttribute), true)).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
            released.Values.Where(t => !tests.Contains(t)).Should().BeEmpty("a weak table's test is a test");

            var unbound = Unbound.Select(u => u.Key).ToHashSet(StringComparer.Ordinal);
            found.Where(f => KindOf(f.Key) != PlanScoped && !unbound.Contains(f.Key) && Holds(f.Field.FieldType, out _)).Select(f => Holds(f.Field.FieldType, out var path) ? $"{f.Key}: {path}" : f.Key)
                .Should().BeEmpty("a schema's reasoning and what it is made of hang off the schema");
            found.Where(f => unbound.Contains(f.Key) && (KindOf(f.Key) != StaticFact || !Holds(f.Field.FieldType, out _))).Select(f => f.Key)
                .Should().BeEmpty("only a fact whose type could hold what a schema learns is listed as holding none of it");

            // The compiler's own statics: a lambda's delegate, a closure class's singleton of itself, and array data.
            assembly.GetTypes().Where(Generated).SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .Where(f => !f.IsLiteral && !typeof(Delegate).IsAssignableFrom(f.FieldType) && f.FieldType != f.DeclaringType
                    && !(f.DeclaringType!.Name == "<PrivateImplementationDetails>" && f.FieldType.IsValueType))
                .Select(f => $"{f.DeclaringType}.{f.Name} ({f.FieldType})").Should().BeEmpty("the compiler keeps only lambdas, singletons and array data");
        }

        /// <summary>
        /// The engine's thread statics are each listed, with why none keeps a graph past the call that set it: an engine
        /// that adds one adds a line here.
        /// </summary>
        [Fact]
        public void TheEnginesThreadStaticsAreListed()
        {
            var found = typeof(Eggnog.EGraph).Assembly.GetTypes()
                .SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .Where(f => f.IsDefined(typeof(ThreadStaticAttribute), false))
                .Select(f => f.DeclaringType!.FullName + "." + f.Name);

            found.Should().BeEquivalentTo(EngineThreadStatics.SelectMany(e => e.Fields.Select(f => e.Type + "." + f)));
        }

        /// <summary>
        /// A constraint remembered under a type factory that lives for the process keeps no table: the compiler's table is
        /// weak on the table as well as the factory, so a table let go takes its schema's reasoning with it, whatever
        /// factory compiled its constraints. Keyed strongly on the table beneath the factory, as it was, it kept both.
        /// </summary>
        [Fact]
        public void AConstraintRememberedUnderALongLivedFactoryKeepsNoTable()
        {
            var held = CompileAndLetGo();
            Collect();

            held.Where(h => h.Reference.IsAlive).Select(h => h.Name).Should().BeEmpty("the factory outlives the table, and keeps none of it");
        }

        /// <summary>A builder whose type factory lives for the process, as a static's does.</summary>
        static readonly RexBuilder LongLived = new(new JavaTypeFactoryImpl());

        [MethodImpl(MethodImplOptions.NoInlining)]
        static (string Name, WeakReference Reference)[] CompileAndLetGo()
        {
            var unique = (CosmosConstraint.Unique)CosmosConstraint.Parse("UNIQUE (JSON_VALUE(DOC, '$.data.guid'))", CosmosConstraintSource.Declared);
            var table = new CosmosTable(new CosmosContainerMetadata("links", ["/linkId"]).WithConstraints([unique]));
            var compiled = CosmosConstraintCompiler.Compile(unique, table, LongLived);
            compiled.Keys.Should().ContainSingle();
            CosmosConstraintCompiler.Compile(unique, table, LongLived).Should().BeSameAs(compiled, "a constraint is compiled once a factory and table");

            return [("table", new WeakReference(table)), ("its reasoning", new WeakReference(table.Container.Reasoning))];
        }

        /// <summary>
        /// A statement remembered under a condition that lives for the process keeps no table: the statements read off a
        /// condition are weak on the container as well, so a long-lived builder's cached <c>TRUE</c>, handed to the
        /// rewriter, the partition key extractor or a side's declaration, keeps neither the table nor its schema's
        /// reasoning, nor the rewrite remembered under the statement. Keyed on the condition alone, as it was, it kept both.
        /// </summary>
        [Fact]
        public void AStatementRememberedUnderALongLivedConditionKeepsNoTable()
        {
            var held = ReadAndLetGo();
            Collect();

            held.Where(h => h.Reference.IsAlive).Select(h => h.Name).Should().BeEmpty("the condition outlives the table, and keeps none of it");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static (string Name, WeakReference Reference)[] ReadAndLetGo()
        {
            const string Uuid = """{ "properties": { "ref": { "type": "string", "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$" } } }""";
            var table = new CosmosTable(new CosmosContainerMetadata("items", ["/ref"]).WithFacts(CosmosSchemaRecognition.ReadFrom(JsonNode.Parse(Uuid)!)));
            var container = table.Container;
            var reasoning = container.Reasoning!;
            var condition = LongLived.makeLiteral(true);
            List<CosmosPath?> fields = [CosmosPath.Root("c").Property("ref"), CosmosPath.Root("c")];
            var reference = LongLived.makeInputRef(LongLived.getTypeFactory().createSqlType(SqlTypeName.VARCHAR), 0);

            CosmosFactRewriter.Rewrite(condition, fields, container, "c", LongLived);
            CosmosPartitionKeyExtractor.TryExtract(condition, fields, null, container, "c", out _);
            CosmosFilterStatement.Declares(LongLived.makeCall(SqlStdOperatorTable.IS_NOT_NULL, reference), fields, null, container, LongLived);
            reasoning.Facts.Started.Should().BePositive("the statements were reasoned about");

            return
            [
                ("table", new WeakReference(table)), ("its metadata", new WeakReference(container)),
                ("its reasoning", new WeakReference(reasoning)), ("its F graph", new WeakReference(reasoning.Facts.Current.Contexts.Graph)),
            ];
        }

        /// <summary>
        /// After a schema of its own has planned, implemented and executed statements through a data source, and been let
        /// go, the schema and everything it learned are collected, and no static but a weak table reaches a graph, an
        /// oracle, a memo or a reasoning, whatever its declared type: each static's value is walked, field by field,
        /// through collections and Calcite's objects alike. A weak table is its own test's (<see cref="Released"/>), since
        /// what its entries reach while their keys live is a test's running beside this one.
        /// </summary>
        [Fact]
        public void NoStaticReachesWhatASchemaLearned()
        {
            var held = ExecuteAndLetGo();
            Collect();

            held.Where(h => h.Reference.IsAlive).Select(h => h.Name).Should().BeEmpty("nothing the schema learned outlives it");

            var kinds = Classed.SelectMany(c => c.Fields.Select(f => (Key: c.Type + "." + f, c.Kind))).ToDictionary(c => c.Key, c => c.Kind, StringComparer.Ordinal);
            var roots = Statics(typeof(CosmosReasoning).Assembly)
                .Where(f => f.Field.DeclaringType!.ContainsGenericParameters == false && (kinds.TryGetValue(f.Key, out var kind) == false || kind != PlanScoped))
                .Select(f => (f.Key, Value: f.Field.GetValue(null)));

            var reaching = Reaching(roots, out var walked);
            TestContext.Current.TestOutputHelper?.WriteLine($"walked {walked:N0} objects from the adapter's statics");
            reaching.Should().BeEmpty("no static but a weak table reaches what a schema learned");
        }

        /// <summary>A container's executor that finds no documents.</summary>
        sealed class Nothing : ICosmosQueryExecutor
        {

            public async ValueTask<IClrCursor<JsonElement>> OpenAsync(CosmosQuery query, PartitionKey? partitionKey = null, CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                return new ListCursor<JsonElement>(Array.Empty<JsonElement>());
            }

        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static (string Name, WeakReference Reference)[] ExecuteAndLetGo()
        {
            const string Uuid = """{ "properties": { "ref": { "type": "string", "pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$" } } }""";
            var schema = new CosmosSchema([new CosmosContainerMetadata("c", ["/pk"]).WithFacts(CosmosSchemaRecognition.ReadFrom(JsonNode.Parse(Uuid)!))], _ => new Nothing());
            var table = (CosmosTable)schema.tables().get("c")!;
            var reasoning = table.Container.Reasoning!;

            using (var dataSource = new CalciteDataSourceBuilder(new CalciteConnectionStringBuilder { CaseSensitive = true }.ConnectionString).AddSchema("COSMOS", schema).Build())
            using (var connection = dataSource.OpenConnection())
                foreach (var sql in new[]
                {
                    """SELECT c."id" FROM "COSMOS"."c" AS c WHERE c."id" = 'x' AND JSON_VALUE(c."DOC", '$.pk') = 'p'""",
                    """SELECT c."id" FROM "COSMOS"."c" AS c WHERE JSON_VALUE(c."DOC", '$.ref') = 'a' AND JSON_VALUE(c."DOC", '$.pk') = 'p'""",
                    """SELECT COUNT(*) FROM "COSMOS"."c" AS c WHERE JSON_VALUE(c."DOC", '$.ref') > 'a'""",
                    """SELECT c."id" FROM "COSMOS"."c" AS c ORDER BY JSON_VALUE(c."DOC", '$.ref') FETCH NEXT 5 ROWS ONLY""",
                    """SELECT a."id" FROM "COSMOS"."c" AS a JOIN "COSMOS"."c" AS b ON a."id" = b."id" WHERE JSON_VALUE(a."DOC", '$.pk') = 'p'""",
                })
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = sql;
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                        continue;
                }

            reasoning.Facts.Started.Should().BePositive("the statements were reasoned about");

            return
            [
                ("schema", new WeakReference(schema)), ("table", new WeakReference(table)), ("its reasoning", new WeakReference(reasoning)),
                ("its F graph", new WeakReference(reasoning.Facts.Current.Contexts.Graph)),
            ];
        }

        /// <summary>
        /// Walks each root's value, every instance field of every object it reaches, and the elements of arrays and the
        /// values of weak tables, and returns the path to each learned object found. Reflection, threads, class loaders and
        /// telemetry are not opened: none holds a value of the adapter's.
        /// </summary>
        static List<string> Reaching(IEnumerable<(string Key, object? Value)> roots, out long walked)
        {
            var from = new Dictionary<object, (object? From, string Edge)>(ReferenceEqualityComparer.Instance);
            var pending = new Queue<object>();
            var found = new List<string>();

            foreach (var (key, value) in roots)
                if (value is not null && Opened(value) && from.TryAdd(value, (null, key)))
                    pending.Enqueue(value);

            walked = 0;
            while (pending.TryDequeue(out var o))
            {
                walked++;
                if (Learned.Any(l => l.IsInstanceOfType(o)))
                {
                    var steps = new List<string> { $"({o.GetType().Name})" };
                    for (var at = o; ;)
                    {
                        var (before, edge) = from[at];
                        steps.Add(before is null ? edge : $"{edge} [{before.GetType().Name}]");
                        if (before is null)
                            break;

                        at = before;
                    }

                    steps.Reverse();
                    found.Add(string.Join(" -> ", steps));
                    continue;
                }

                foreach (var (child, edge) in Children(o))
                    if (child is not null && Opened(child) && from.TryAdd(child, (o, edge)))
                        pending.Enqueue(child);
            }

            return found;
        }

        static readonly string[] Unopened = ["java.lang.Class", "java.lang.ClassLoader", "java.lang.Thread", "IKVM.", "System.Reflection.", "System.RuntimeType", "System.Threading.Thread", "System.Diagnostics.", "System.Runtime.Loader."];

        static bool Opened(object o)
        {
            var type = o.GetType();
            if (type.IsPrimitive || type.IsEnum || o is string or Type or MemberInfo or Assembly or Module or Pointer)
                return false;

            var name = type.FullName ?? "";
            return Unopened.Any(u => name.StartsWith(u, StringComparison.Ordinal)) == false;
        }

        static IEnumerable<(object? Child, string Edge)> Children(object o)
        {
            var type = o.GetType();
            if (o is Array array)
            {
                if (type.GetElementType()!.IsPrimitive == false)
                    for (var i = 0; i < array.Length; i++)
                        yield return (array.GetValue(i), $"[{i}]");

                yield break;
            }

            if (IsGeneric(type, typeof(ConditionalWeakTable<,>)))
            {
                var i = 0;
                foreach (var entry in (System.Collections.IEnumerable)o)
                    yield return (entry.GetType().GetProperty("Value")!.GetValue(entry), $"<value {i++}>");

                yield break;
            }

            for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
                foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (field.FieldType.IsPointer == false && field.FieldType.IsByRefLike == false && field.FieldType.IsPrimitive == false && field.FieldType.IsEnum == false)
                        yield return (field.GetValue(o), field.Name);
        }

        static void Collect()
        {
            for (var i = 0; i < 4; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            GC.Collect();
        }

        /// <summary>The adapter's static fields, constants and the compiler's own types aside, each by its key.</summary>
        static IEnumerable<(string Key, FieldInfo Field)> Statics(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
                if (Generated(type) == false)
                    foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (field.IsLiteral == false)
                            yield return (Key(type, field), field);
        }

        /// <summary>Whether a type is the compiler's: a closure class, a state machine, array data, or one nested in one.</summary>
        static bool Generated(Type type)
        {
            for (var t = type; t is not null; t = t.DeclaringType)
                if (t.IsDefined(typeof(CompilerGeneratedAttribute), false) || t.Name.StartsWith('<'))
                    return true;

            return false;
        }

        /// <summary>A field's key: its type's name past <c>Apache.Calcite.Cosmos.</c>, and an auto-property's name for its backing field.</summary>
        static string Key(Type type, FieldInfo field)
        {
            const string Prefix = "Apache.Calcite.Cosmos.";
            var name = Regex.Match(field.Name, "^<(.+)>k__BackingField$") is { Success: true } m ? m.Groups[1].Value : field.Name;
            var full = type.FullName!.Replace('+', '.');
            return (full.StartsWith(Prefix, StringComparison.Ordinal) ? full[Prefix.Length..] : full) + "." + name;
        }

        /// <summary>Whether a field flows with a thread or an async flow.</summary>
        static bool Flows(FieldInfo field) =>
            field.IsDefined(typeof(ThreadStaticAttribute), false) || IsGeneric(field.FieldType, typeof(AsyncLocal<>)) || IsGeneric(field.FieldType, typeof(ThreadLocal<>));

        static bool IsGeneric(Type type, Type definition) => type.IsGenericType && type.GetGenericTypeDefinition() == definition;

        /// <summary>
        /// Whether a static's declared type can reach a learned type anywhere but as a weak table's key: through arrays and
        /// generic arguments, and through every instance field of the adapter's own types, and of every type of the
        /// adapter's that derives from or implements them, as far as they reach. The engine's and Calcite's types are not
        /// opened; what their values reach is the walk's.
        /// </summary>
        /// <param name="declared">The static's declared type.</param>
        /// <param name="path">The fields through which it reaches one, for the message.</param>
        static bool Holds(Type declared, out string path)
        {
            var adapter = typeof(CosmosReasoning).Assembly;
            var from = new Dictionary<Type, (Type? Type, string Edge)> { [declared] = (null, "") };
            var pending = new Queue<Type>([declared]);

            while (pending.TryDequeue(out var type))
            {
                if (Learned.Any(l => l.IsAssignableFrom(type)))
                {
                    var steps = new List<string> { type.Name };
                    for (var at = type; from[at].Type is { } before; at = before)
                        steps.Add($"{from[at].Edge} [{before.Name}]");

                    steps.Reverse();
                    path = string.Join(" -> ", steps);
                    return true;
                }

                void Reach(Type next, string edge)
                {
                    if (next.IsGenericParameter == false && from.TryAdd(next, (type, edge)))
                        pending.Enqueue(next);
                }

                if (type.IsArray)
                    Reach(type.GetElementType()!, "[]");
                if (type.IsGenericType)
                {
                    var arguments = type.GetGenericArguments();
                    foreach (var argument in IsGeneric(type, typeof(ConditionalWeakTable<,>)) ? arguments.Skip(1) : arguments)
                        Reach(argument, "<>");
                }

                if (type.Assembly != adapter)
                    continue;

                foreach (var derived in AdapterTypes.Where(t => t != type && t.ContainsGenericParameters == false && type.IsAssignableFrom(t)))
                    Reach(derived, ":");
                for (var t = type; t is not null && t.Assembly == adapter; t = t.BaseType)
                    foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        Reach(field.FieldType, field.Name);
            }

            path = "";
            return false;
        }

        static readonly Type[] AdapterTypes = typeof(CosmosReasoning).Assembly.GetTypes();

    }

}
