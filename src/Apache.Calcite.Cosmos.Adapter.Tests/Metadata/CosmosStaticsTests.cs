using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using FluentAssertions;
using FluentAssertions.Execution;

using org.apache.calcite.rex;

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

        /// <summary>The classes of the reassessment's table.</summary>
        enum Kind
        {

            /// <summary>STATIC_FACT: fixed in the assembly, or a pure function of it over a bounded domain.</summary>
            StaticFact,

            /// <summary>INFRA: telemetry, reflection handles, settings, stateless helpers.</summary>
            Infra,

            /// <summary>TEST_ARM: what a test arms for its own flow, which production never does.</summary>
            TestArm,

            /// <summary>PLAN_SCOPED: a weak table keyed on what dies with a plan, a graph or a connection.</summary>
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
            ("Adapter.Metadata.CosmosFactRewriter", ["Rewrites"], PlanScoped, "keyed weakly on a plan's statement"),
            ("Adapter.Metadata.CosmosFilterStatement", ["Read"], PlanScoped, "keyed weakly on a plan's condition"),
            ("Adapter.Metadata.CosmosNumericForms", ["Digits", "IntegerUnpadded", "Unpadded"], StaticFact, "numeric spellings"),
            ("Adapter.Metadata.CosmosPartitionKeyExtractor", ["IdPolicy"], StaticFact, "the id's path"),
            ("Adapter.Metadata.CosmosPartitionKeyExtractor.RexBuilderHolder", ["Value"], Infra, Builder),
            ("Adapter.Metadata.CosmosPatternReader.Parser", ["Digit", "Word"], StaticFact, "character classes"),
            ("Adapter.Metadata.CosmosPredicateLoader.RexBuilderHolder", ["Value"], Infra, Builder),
            ("Adapter.Metadata.CosmosRendering", ["Defined"], StaticFact, "S's definition, read once from the measured registry and CosmosStrictness; every schema's S is defined from it"),
            ("Adapter.Metadata.CosmosRenderingCapture", ["Armed", "Current"], TestArm, "the observer a test arms for its flow"),
            ("Adapter.Metadata.CosmosRowFacts", ["Decided"], PlanScoped, "keyed weakly on a plan's node"),
            ("Adapter.Metadata.CosmosSchemaRecognition", ["Cosmos"], StaticFact, "the recognisers of Cosmos DB's schema keywords"),
            ("Adapter.Metadata.CosmosServiceFacts", ["Properties", "Stated"], StaticFact, "what the service guarantees of its own properties, as rules"),
            ("Adapter.Metadata.CosmosServiceOrder", ["Int64Least", "Int64Most", "TwoTo53"], StaticFact, "numeric bounds"),
            ("Adapter.Metadata.CosmosStrictness", ["Operators", "Readers"], StaticFact, "read once from Calcite's static operator tables and the registry"),
            ("Adapter.Metadata.CosmosTemporalForms", ["EngineTargets", "Iso8601Date", "Iso8601UtcMicroseconds", "Iso8601UtcMilliseconds", "Iso8601UtcSeconds", "Iso8601UtcTicks", "ParseDigits", "Temporal"], StaticFact, "temporal spellings and their parses, written by the type initializer alone"),
            ("Adapter.Metadata.CosmosUuidForms", ["CanonicalLower", "CanonicalLowerSortable", "CanonicalUpper", "CanonicalUpperSortable", "Hyphens", "Uuids"], StaticFact, "UUID spellings"),
            ("Adapter.Metadata.CosmosUuidForms", ["UnsignedUuidComparison"], Infra, "Calcite's system property, read once: the process's configuration"),

            ("Adapter.Rel.Convert.CosmosConverters", ["Announced"], PlanScoped, "keyed weakly on one implementation's implementor"),
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
        /// and failing once the field has gone, so that the line goes with it.
        /// </summary>
        static readonly (string Type, string Field, string Reason)[] Leaving =
        [
            ("Adapter.Sql.CosmosFunctionBodies", "Paths", "every path an in-process type test was asked, unbounded: parsed per call instead (TODO.md)"),
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
        /// Every static field of the adapter is classed, and none holds what a schema learns: a fact is read-only, a weak
        /// table is a weak table, only a test's arm flows with a thread or an async flow, no static's type is a graph, an
        /// oracle, a memo or a reasoning but as a weak table's key, and a theory held by one is bound to nothing. A static
        /// not classed fails, so a new one has to be classed here, with its reason.
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

            found.Where(f => Holds(f.Field.FieldType)).Select(f => $"{f.Key} ({f.Field.FieldType})")
                .Should().BeEmpty("a schema's reasoning and what it is made of hang off the schema");
            var values = found.Select(f => (f.Key, Value: Value(f.Field))).ToList();
            values.Where(v => v.Value is not null && Learned.Any(t => t.IsInstanceOfType(v.Value))).Select(v => v.Key)
                .Should().BeEmpty("no static holds a graph, an oracle, a memo or a reasoning");
            values.Where(v => v.Value is JsonFactTheory { Generations: not null }).Select(v => v.Key)
                .Should().BeEmpty("a theory a static holds is rules alone, bound to no schema's generations");

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

        /// <summary>Whether a static's declared type holds a learned type anywhere but as a weak table's key.</summary>
        static bool Holds(Type type)
        {
            if (type.IsArray)
                return Holds(type.GetElementType()!);
            if (Learned.Any(l => l.IsAssignableFrom(type)))
                return true;
            if (type.IsGenericType == false)
                return false;

            var arguments = type.GetGenericArguments();
            return (IsGeneric(type, typeof(ConditionalWeakTable<,>)) ? arguments.Skip(1) : arguments).Any(Holds);
        }

        /// <summary>A static's value on this thread, or <c>null</c> for an open generic's.</summary>
        static object? Value(FieldInfo field) => field.DeclaringType!.ContainsGenericParameters ? null : field.GetValue(null);

    }

}
