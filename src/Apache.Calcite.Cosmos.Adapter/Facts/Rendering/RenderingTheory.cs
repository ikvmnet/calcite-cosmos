using System;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// The rendering graph S: what the service answers, settled per set of targets, the agreement and the connectives each
    /// set composes through, loaded into a frozen theory; and the rules that, in a bundle beneath it, evaluate a written
    /// form beside the predicate it was written from on every cell of an atom's path, decide whether it agrees or is
    /// refuted, and carry both through the predicate's AND, OR and NOT; and, in generate mode, propose an atom's forms from
    /// templates, keep the cheapest exact one of every node and build each node's weakening.
    /// </summary>
    /// <remarks>
    /// Every fact that is derived or combined is a rule here, and every universal reads complete inputs (<c>DESIGN.md</c>,
    /// <i>The rendering graph in check mode</i> and <i>in generate mode</i>).
    /// </remarks>
    internal sealed class RenderingTheory
    {

        RenderingTheory(RenderingDefinition definition)
        {
            Graph = new EGraph();
            Paths = new PathVocabulary(Graph);
            Ir = new PredicateVocabulary(Graph);
            Register(Graph.Values);

            Outcome = Graph.DefineFunction<MeasuredKey, RenderingTarget, CosmosAnswer>("outcome");
            Cites = Graph.DefineFunction<MeasuredKey, RenderingTarget, ValueSet<string>>("cites", ValueLattice.Union<string>());
            Serves = Graph.DefineFunction<RenderingSet, ValueSet<RenderingTarget>>("serves");
            Means = Graph.DefineFunction<AnswerMeanings>("means");
            Answers = Graph.DefineFunction<MeasuredKey, RenderingSet, ValueSet<CosmosAnswer>>("answers", ValueLattice.Union<CosmosAnswer>());
            Measured = Graph.DefineFunction<MeasuredKey, RenderingSet, ValueSet<RenderingTarget>>("measured", ValueLattice.Union<RenderingTarget>());
            Evidence = Graph.DefineFunction<MeasuredKey, RenderingSet, ValueSet<string>>("evidence", ValueLattice.Union<string>());
            Final = Graph.DefineFunction<MeasuredKey, RenderingSet, ValueSet<CosmosAnswer>>("final");
            Table = Graph.DefineFunction<RenderingSet, ValueSet<MeasuredEntry>>("table", ValueLattice.Union<MeasuredEntry>());
            Placed = Graph.DefineFunction<ValueSet<CosmosValueClass>>("placed", ValueLattice.Union<CosmosValueClass>());
            Class = Graph.DefineRelation<CellClass>("class");
            RaiseOnly = Graph.DefineFunction<ValueSet<CellClass>>("raiseonly", ValueLattice.Union<CellClass>());
            Allowing = Graph.DefineRelation<RenderPosition, Verdict, Box>("allowing");
            Refuting = Graph.DefineRelation<RenderPosition, Verdict, Box>("refuting");
            Tolerated = Graph.DefineFunction<Truths>("tolerated");
            Allowed = Graph.DefineFunction<RenderPosition, Verdict, ValueSet<CellClass>>("allowed", ValueLattice.Union<CellClass>());
            AllowedCites = Graph.DefineFunction<RenderPosition, Verdict, ValueSet<string>>("allowedcites", ValueLattice.Union<string>());
            Refutes = Graph.DefineFunction<RenderPosition, Verdict, ValueSet<CellClass>>("refutes", ValueLattice.Union<CellClass>());
            Edges = Graph.DefineFunction<string, ReaderEdges>("edges");
            Parts = Graph.DefineRelation<CosmosAnswer, string, string>("parts");
            Moves = Graph.DefineRelation<CosmosAnswer, string>("moves");
            StoredAs = Graph.DefineRelation<RenderingTarget, string, string>("storedas");
            Truncates = Graph.DefineRelation<RenderingTarget, CosmosServiceTest, string, string>("truncates");
            PairReads = Graph.DefineRelation<CosmosServiceTest, bool, CosmosServiceTest>("pairreads");
            BoxOf = Graph.DefineRelation<string, string>("boxof");
            SeesThrough = Graph.DefineRelation<string>("seesthrough");
            StructureOnly = Graph.DefineRelation<string>("structureonly");
            CalciteReads = Graph.DefineRelation<string, CosmosValueClass, string>("calcitereads");
            CalciteBands = Graph.DefineFunction<string, CosmosValueClass, ValueSet<string>>("calcitebands", ValueLattice.Union<string>());
            MeasuredCast = Graph.DefineRelation<string, string, string>("measuredcast");
            // What a reader compares with a number Calcite wrote cast (G8): rows at the frozen root, read beside the terms by
            // the rule that makes the comparison an atom, which may read only terms and analyses, so held as an analysis.
            TakesConstant = Graph.DefineRelation<string, bool, string>("takesconstant", new TableOptions { Role = TableRole.Analysis });
            ReaderOrder = Graph.DefineFunction<string, ValueSet<string>>("readerorder", ValueLattice.Union<string>());
            TestKinds = Graph.DefineFunction<CosmosServiceTest, ValueSet<CosmosValueClass>>("testkinds", ValueLattice.Union<CosmosValueClass>());
            Kleene = Graph.DefineFunction<MeasuredKey, RenderingTarget, CosmosAnswer>("kleene");
            Expected = Graph.DefineFunction<CosmosServiceTest, RenderingSet, ValueSet<MeasuredKey>>("expected", ValueLattice.Union<MeasuredKey>());
            Matched = Graph.DefineFunction<CosmosServiceTest, RenderingSet, ValueSet<MeasuredKey>>("matched", ValueLattice.Union<MeasuredKey>());
            Licensed = Graph.DefineFunction<RenderingSet, ValueSet<CosmosServiceTest>>("licensed", ValueLattice.Union<CosmosServiceTest>());
            Identity = Graph.DefineFunction<CosmosServiceTest, ValueSet<CellClass>>("identity");
            Opposite = Graph.DefineRelation<RenderPosition, RenderPosition>("opposite");

            Spelling = Graph.DefineFunction<JPath, string>("spelling");
            Conjunct = Graph.DefineRelation<ServiceForm, ServiceForm>("conjunct");
            Under = Graph.DefineRelation<RenderingSet>("under");
            Imported = Graph.DefineFunction<JPath, PathValues>("imported");
            Assumed = Graph.DefineFunction<Pred, PathValues>("assumed");
            ImportedChars = Graph.DefineFunction<JPath, ValueSet<string>>("importedchars");
            PartedLiterals = Graph.DefineFunction<JPath, ValueSet<string>>("partedliterals", ValueLattice.Union<string>());
            Candidate = Graph.DefineRelation<Pred, RenderPosition, ServiceForm>("candidate");
            Member = Graph.DefineRelation<ServiceForm, ServiceForm>("member");
            Partition = Graph.DefineFunction<Pred, ServiceForm, CellPartition>("partition");
            Need = Graph.DefineRelation<CellPartition, ServiceForm>("need");
            Svc = Graph.DefineFunction<CellPartition, ServiceForm, OutcomeVector>("svc");
            Outcomes = Graph.DefineFunction<Pred, ServiceForm, CellOutcomes>("outcomes");
            Agrees = Graph.DefineFunction<Pred, RenderPosition, ServiceForm, Verdict, Proof>("agrees", ValueLattice.Min<Proof>());
            Vacuous = Graph.DefineFunction<Pred, RenderPosition, ServiceForm, Verdict, bool>("vacuous", ValueLattice.Max<bool>());
            Disagrees = Graph.DefineFunction<Pred, RenderPosition, ServiceForm, Verdict, Refutation>("disagrees", ValueLattice.Min<Refutation>());
            RefutedAt = Graph.DefineRelation<ValueSet<string>, JPath>("refutedat");
            Conflict = Graph.DefineRelation<Pred, RenderPosition, ServiceForm, Verdict>("conflict");
            var analysis = new TableOptions { Role = TableRole.Analysis };
            Junction = Graph.DefineRelation<Pred, Pred, Pred, CosmosServiceTest>("junction", analysis);
            First = Graph.DefineRelation<Pred, Pred>("first", analysis);
            Within = Graph.DefineRelation<Pred, RenderPosition, ServiceForm>("within");
            Needs = Graph.DefineFunction<ServiceForm, ValueSet<CosmosServiceTest>>("needs", ValueLattice.Union<CosmosServiceTest>());
            Neutral = Graph.DefineFunction<Pred, ServiceForm, CosmosServiceTest, Witness>("neutral", ValueLattice.Min<Witness>());
            Disjoint = Graph.DefineRelation<JPath, JPath>("disjoint");

            At = Graph.DefineRelation<Pred, RenderPosition>("at");
            Template = Graph.DefineRelation<TemplateId, RenderPosition>("template");
            Fits = Graph.DefineRelation<TemplateId, string>("fits");
            PartingBands = Graph.DefineFunction<ValueSet<string>>("partingbands");
            RexGrammar = Graph.DefineFunction<RenderingRexGrammar.Value>("rexgrammar");
            StrictOp = Graph.DefineFunction<string, bool>("strictop", ValueLattice.Min<bool>());
            StrictReader = Graph.DefineRelation<string>("strictreader");
            BestExact = Graph.DefineFunction<Pred, RenderPosition, Priced>("bestexact", ValueLattice.Min<Priced>());
            Implied = Graph.DefineFunction<Pred, RenderPosition, ValueSet<ServiceForm>>("implied", ValueLattice.Union<ServiceForm>());
            Weakening = Graph.DefineFunction<Pred, RenderPosition, ServiceForm>("weakening");
            Leaf = Graph.DefineRelation<Pred>("leaf");
            Over = Graph.DefineRelation<Pred, Val>("over");
            CallArg = Graph.DefineRelation<Val, Val>("callarg");
            Strict = Graph.DefineRelation<Val, JPath>("strict");
            NullWhereAbsent = Graph.DefineRelation<JPath>("nullwhereabsent");

            // S's alone: a leaf tagged with the arm stratum it is read beneath, a case with its else-arm rooted twice, and
            // the constant else split by what it does at a position (spec h08). F writes neither term.
            var term = new TableOptions { Extractable = false };
            Beneath = Graph.DefineConstructor<long, Pred, Pred>("beneath", options: term);
            Chosen = Graph.DefineConstructor<Pred, Pred, Pred, Pred, Pred>("chosen", options: term);
            TwoValued = Graph.DefineRelation<Pred>("twovalued");
            AbsorbingElse = Graph.DefineRelation<RenderPosition, string>("absorbingelse");
            OppositeElse = Graph.DefineRelation<RenderPosition, string>("oppositeelse");

            // A computed value's place (h10), S's alone; the tables the writer and the bundle fill for it, its reading among
            // them (the loader types a call the rows name, and its type is the reader); and the function rows, raw at the
            // frozen root.
            Valued = Graph.DefineConstructor<Val, JPath>("valued", options: term);
            Paired = Graph.DefineConstructor<JPath, JPath, JPath>("paired", options: term);
            SqlType = Graph.DefineFunction<Val, string>("sqltype");
            Arity = Graph.DefineFunction<Val, long>("arity");
            CallArgAt = Graph.DefineRelation<Val, long, Val>("callargat");
            ArmImport = Graph.DefineFunction<long, JPath, PathValues>("armimport");
            StratumId = Graph.DefineFunction<Pred, long>("stratumid");
            Gathered = Graph.DefineFunction<long, Val, ValueSet<PathImport>>("gathered", ValueLattice.Union<PathImport>());
            FunctionAnswer = Graph.DefineFunction<FunctionKey, RenderingTarget, CosmosFunctionAnswer>("functionanswer");
            MeasuredKeys = Graph.DefineFunction<string, ValueSet<FunctionKey>>("measuredkeys", ValueLattice.Union<FunctionKey>());
            FunctionSubjects = Graph.DefineFunction<string, ValueSet<long>>("functionsubjects", ValueLattice.Union<long>());
            MeasuredReaders = Graph.DefineFunction<string, ValueSet<string>>("measuredreaders", ValueLattice.Intersect<string>());
            ValueClassHolds = Graph.DefineRelation<CosmosValueClass, FunctionValueClass, FunctionValueClass>("valueclassholds");
            DisagreeingKeys = Graph.DefineFunction<RenderingSet, string, ValueSet<FunctionKey>>("disagreeingkeys", ValueLattice.Union<FunctionKey>());

            // The raw columns, one row a column a key, raw at the frozen root, and their union, which Eggnog's join takes (h12).
            RawColumn = Graph.DefineFunction<RawKey, string, Truths>("rawcolumn");
            RawUnion = Graph.DefineFunction<RawTruths>("rawunion", RawTruths.Union);

            var measure = Graph.DefineRuleset("measure");
            var allowed = Graph.DefineRuleset("allowed");
            var refutes = Graph.DefineRuleset("refutes");
            var tabulate = Graph.DefineRuleset("tabulate");
            var expected = Graph.DefineRuleset("expected");
            var matched = Graph.DefineRuleset("matched");
            var licence = Graph.DefineRuleset("licence");
            var disagree = Graph.DefineRuleset("disagree");
            var raw = Graph.DefineRuleset("raw");
            DefineTheory(measure, allowed, refutes, tabulate);
            DefineLicences(expected, matched, licence);
            raw.DefineRule("a raw reading's truths on a key are the union of its columns', and a column's reading's its own", r =>
            {
                var (k, c, t) = r.Vars<RawKey, string, Truths>("k", "c", "t");
                r.When(t.Is(RawColumn.Of(k, c)));
                r.Then(RawUnion.Of().Is(Compute.Of(k, c, t, RawTruths.Of)));
            });

            // S's alone, in the vocabulary's ruleset: a declared field read through its column (rawtyped), which F never writes.
            Ir.Analyse.DefineRule("a raw value in a column reads its path through the column", r =>
            {
                var (v, p, c) = r.Vars<Val, JPath, string>("v", "p", "c");
                r.When(v.Is(Ir.RawIn.Of(p, c)));
                r.Then(Ir.Reads.Of(v, p, Compute.Of(c, PredicateAtom.RawIn)));
            });

            Denotes = Graph.DefineRelation<Val, string, bool>("denotes", analysis);
            DefineComparands();

            var tag = Graph.DefineRuleset("tag");
            DefineTag(tag);

            var structure = Graph.DefineRuleset("structure");
            var descend = Graph.DefineRuleset("descend");
            var disjoint = Graph.DefineRuleset("disjoint");
            DefineDescent(structure, descend, disjoint);

            var partition = Graph.DefineRuleset("partition");
            var need = Graph.DefineRuleset("need");
            var parted = Graph.DefineRuleset("parted");
            var evaluate = Graph.DefineRuleset("evaluate");
            var classes = Graph.DefineRuleset("classes");
            var gate = Graph.DefineRuleset("gate");
            var compose = Graph.DefineRuleset("compose");
            var conflict = Graph.DefineRuleset("conflict");
            DefineCells(definition, partition, need, parted);
            DefineEvaluation(definition, evaluate);
            DefineGate(definition, classes, gate, conflict);
            DefineComposition(compose);

            var at = Graph.DefineRuleset("at");
            var spell = Graph.DefineRuleset("spell");
            var lift = Graph.DefineRuleset("lift");
            var strict = Graph.DefineRuleset("strict");
            var best = Graph.DefineRuleset("best");
            var weaken = Graph.DefineRuleset("weaken");
            var twovalued = Graph.DefineRuleset("twovalued");
            DefineProposal(at, spell);
            DefineLifts(lift, strict);
            DefineChoice(best, weaken);
            DefineCase(at, twovalued, best, weaken);

            var place = Graph.DefineRuleset("place");
            var image = Graph.DefineRuleset("image");
            var assume = Graph.DefineRuleset("assume");
            DefineValued(disagree, place, image, assume);
            var pair = Graph.DefineRuleset("pair");
            DefinePair(pair, place);
            // A nested call is gathered once its inner place is written: each nesting level is one outer pass.
            var places = Schedule.Saturate(Schedule.Sequence(Schedule.Saturate(place), Schedule.Saturate(image), Schedule.Saturate(assume)));

            Settle = Schedule.Sequence(Schedule.Saturate(measure, allowed, refutes, expected, disagree, raw), Schedule.Saturate(tabulate), Schedule.Saturate(matched),
                Schedule.Saturate(licence));
            // Two-valuedness reads the gate's classes complete, beside the lift; the case's best reads it complete.
            Check = Schedule.Sequence(Schedule.Saturate(Ir.Analyse, pair, tag, structure, descend, disjoint), places, Schedule.Saturate(partition, need, parted),
                Schedule.Saturate(evaluate), Schedule.Saturate(classes, gate), Schedule.Saturate(compose, conflict));
            Generate = Schedule.Sequence(Schedule.Saturate(Ir.Analyse, pair, tag, structure, at, spell), places, Schedule.Saturate(partition, need, parted),
                Schedule.Saturate(evaluate), Schedule.Saturate(classes, gate), Schedule.Saturate(lift, strict, twovalued), Schedule.Saturate(best, conflict),
                Schedule.Saturate(weaken));

            Graph.Root.Freeze();
            Theory = Graph.Root.CreateChild();
            Load(definition);
        }

        public EGraph Graph { get; }

        /// <summary>The frozen scope that holds the rows, settled; every bundle is a child of it.</summary>
        public Scope Theory { get; }

        public PathVocabulary Paths { get; }

        public PredicateVocabulary Ir { get; }

        /// <summary>The theory's schedule: the rows settled per set, the classes the boxes allow and refute, and the licences.</summary>
        public Schedule Settle { get; }

        /// <summary>
        /// A bundle's schedule, in strata: terms into atoms, and the candidates down the tree; cells; the service on them;
        /// the gate on atoms; verdicts up the tree.
        /// </summary>
        public Schedule Check { get; }

        /// <summary>
        /// Generate mode's schedule: positions down the tree and the templates' forms; the cells, the service and the gate
        /// on them as in check mode; what the gate agreed lifted, and presence through calls; the bests composed up the
        /// tree; one weakening a node, from complete inputs.
        /// </summary>
        public Schedule Generate { get; }

        // The theory's tables: the rows as measured and as settled per set, and the agreement. `final` and `table` hold a
        // key once every target its set serves has measured it.
        public Function<MeasuredKey, RenderingTarget, CosmosAnswer> Outcome { get; }

        /// <summary>A computed value's place: S reads a call the function rows name as a path of its own (h10).</summary>
        public Constructor<Val, JPath> Valued { get; }

        /// <summary>Two places one comparison reads, left and right: a place of their own whose import is the product (h11).</summary>
        public Constructor<JPath, JPath, JPath> Paired { get; }

        /// <summary>A call's SQL type, written by the bundle for a call the loader typed.</summary>
        public Function<Val, string> SqlType { get; }

        /// <summary>A call's operand count, written by the bundle.</summary>
        public Function<Val, long> Arity { get; }

        /// <summary>
        /// A call's operand at an ordinal, one row an ordinal, so <c>x * x</c> has two: a relation, Eggnog's functions
        /// outputting values alone.
        /// </summary>
        public Relation<Val, long, Val> CallArgAt { get; }

        /// <summary>A place's import beneath a stratum, 0 the root's: the host's for a path, the image rule's for a computed place.</summary>
        public Function<long, JPath, PathValues> ArmImport { get; }

        /// <summary>The stratum an atom over a computed place is read beneath, 0 at the root; written by the bundle.</summary>
        public Function<Pred, long> StratumId { get; }

        /// <summary>A call's operands' imports beneath a stratum, one an ordinal once complete.</summary>
        public Function<long, Val, ValueSet<PathImport>> Gathered { get; }

        /// <summary>A function key's answer on a target, total over the targets (<see cref="CosmosFunctionAnswer.Unmeasured"/>).</summary>
        public Function<FunctionKey, RenderingTarget, CosmosFunctionAnswer> FunctionAnswer { get; }

        /// <summary>
        /// The keys some target measured of a function, by its rows' name, each read as the atoms its class holds
        /// (<see cref="ValueClassHolds"/>): what a call's keys must fall within.
        /// </summary>
        public Function<string, ValueSet<FunctionKey>> MeasuredKeys { get; }

        /// <summary>
        /// The operands a function's rows name, by the loader's ordinals, <see cref="FunctionKey.Every"/> among them where a
        /// row reads each alike: a constant raw fact, one row's operand at a time, which the union gathers (F2).
        /// </summary>
        public Function<string, ValueSet<long>> FunctionSubjects { get; }

        /// <summary>
        /// The readers a function's rows were measured through: a constant raw fact, one row's at a time, which the
        /// intersection meets, so a function two rows measure holds only through what both were measured through.
        /// </summary>
        public Function<string, ValueSet<string>> MeasuredReaders { get; }

        /// <summary>The atoms a class of value holds of a kind of operand (<see cref="FunctionValueClasses.Holding"/>): constant data.</summary>
        public Relation<CosmosValueClass, FunctionValueClass, FunctionValueClass> ValueClassHolds { get; }

        /// <summary>The keys of a function some target a set serves answers otherwise than Calcite, derived at the build.</summary>
        public Function<RenderingSet, string, ValueSet<FunctionKey>> DisagreeingKeys { get; }

        /// <summary>What one raw column answers on a key (<c>CosmosAxiomRows.ReadColumns</c>), each row raw.</summary>
        public Function<RawKey, string, Truths> RawColumn { get; }

        /// <summary>The raw columns' truths, joined key by key from every column's: what a raw reading's cells read.</summary>
        public Function<RawTruths> RawUnion { get; }
        public Function<MeasuredKey, RenderingTarget, ValueSet<string>> Cites { get; }
        public Function<RenderingSet, ValueSet<RenderingTarget>> Serves { get; }
        public Function<AnswerMeanings> Means { get; }
        public Function<MeasuredKey, RenderingSet, ValueSet<CosmosAnswer>> Answers { get; }
        public Function<MeasuredKey, RenderingSet, ValueSet<RenderingTarget>> Measured { get; }
        public Function<MeasuredKey, RenderingSet, ValueSet<string>> Evidence { get; }
        public Function<MeasuredKey, RenderingSet, ValueSet<CosmosAnswer>> Final { get; }
        public Function<RenderingSet, ValueSet<MeasuredEntry>> Table { get; }

        /// <summary>
        /// The classes some row measures an order of, whose comparisons the leaf places by the order entry's comparators
        /// (F4): derived from the rows, of every set alike, so that a set whose table holds no order of a class still asks
        /// for that order and is named missing it.
        /// </summary>
        public Function<ValueSet<CosmosValueClass>> Placed { get; }

        public Relation<CellClass> Class { get; }
        public Function<ValueSet<CellClass>> RaiseOnly { get; }
        public Relation<RenderPosition, Verdict, Box> Allowing { get; }
        public Relation<RenderPosition, Verdict, Box> Refuting { get; }
        public Function<Truths> Tolerated { get; }
        public Function<RenderPosition, Verdict, ValueSet<CellClass>> Allowed { get; }
        public Function<RenderPosition, Verdict, ValueSet<string>> AllowedCites { get; }
        public Function<RenderPosition, Verdict, ValueSet<CellClass>> Refutes { get; }
        public Function<string, ReaderEdges> Edges { get; }

        /// <summary>
        /// Which bands each comparator ranks apart from Calcite's UTF-16 order, a string order row's (F3): what the parted
        /// rules read through the comparators the set's string order names.
        /// </summary>
        public Relation<CosmosAnswer, string, string> Parts { get; }

        // The rows the foundation reads in for the groups that follow (F3, plan §3): each a row's structured cells, assumed
        // as written; no rule reads them until its group's does (TODO.md).

        /// <summary>Which band a comparator moves in order (the emulator's of what it stores, U+0000), G1's.</summary>
        public Relation<CosmosAnswer, string> Moves { get; }

        /// <summary>What a string written in a class is stored holding, by the target that stores it so, G1's.</summary>
        public Relation<RenderingTarget, string, string> StoredAs { get; }

        /// <summary>Which tests read a string only to a band's first character, for an argument of a shape, by target, G1's.</summary>
        public Relation<RenderingTarget, CosmosServiceTest, string, string> Truncates { get; }

        /// <summary>How a comparison of two paths reads against a literal, the sides swapped or not: the mirror law, G3's.</summary>
        public Relation<CosmosServiceTest, bool, CosmosServiceTest> PairReads { get; }

        /// <summary>The box each reading or kind of value is held in, cast to <c>VARIANT</c>, G8's.</summary>
        public Relation<string, string> BoxOf { get; }

        /// <summary>The comparisons that read the values inside one box, G8's.</summary>
        public Relation<string> SeesThrough { get; }

        /// <summary>The functions that answer a structure alone, G8's.</summary>
        public Relation<string> StructureOnly { get; }

        /// <summary>What a reading answers over each kind of stored value in process, G7's.</summary>
        public Relation<string, CosmosValueClass, string> CalciteReads { get; }

        /// <summary>The character classes a reading's witnesses of a kind held, beyond which it is unmeasured, G7's.</summary>
        public Function<string, CosmosValueClass, ValueSet<string>> CalciteBands { get; }

        /// <summary>The casts a <c>Calcite</c> row measured, by target and the reading cast, citing the row, G8's.</summary>
        public Relation<string, string, string> MeasuredCast { get; }

        /// <summary>
        /// The readers that compare with what a number Calcite wrote cast denotes (G8), citing the row: with the number itself
        /// (<c>false</c>), and with one a cast that drops scale moved by less than one (<c>true</c>), which only a reader whose
        /// reading rescales the literal reads (<c>PredicateAtom</c>'s decimal parse). A reader no row names takes neither.
        /// </summary>
        public Relation<string, bool, string> TakesConstant { get; }

        /// <summary>
        /// What a cast of a literal denotes, as the loader reads one where it reads places (G8): the number the literal spells,
        /// and whether a cast that drops scale moved Calcite's value from it; nothing where a cast's whole digits do not fit,
        /// which raises. Derived from the terms alone.
        /// </summary>
        public Relation<Val, string, bool> Denotes { get; }

        /// <summary>What a reading's order holds in process, every reading a row names, G3's.</summary>
        public Function<string, ValueSet<string>> ReaderOrder { get; }

        /// <summary>The classes each one-place test is true of in process, G7's.</summary>
        public Function<CosmosServiceTest, ValueSet<CosmosValueClass>> TestKinds { get; }

        // Composition's: Kleene's answers, the keys each set is expected and matched on, the connectives it composes
        // through, an identity's class and the position beneath a negation.
        public Function<MeasuredKey, RenderingTarget, CosmosAnswer> Kleene { get; }
        public Function<CosmosServiceTest, RenderingSet, ValueSet<MeasuredKey>> Expected { get; }
        public Function<CosmosServiceTest, RenderingSet, ValueSet<MeasuredKey>> Matched { get; }
        public Function<RenderingSet, ValueSet<CosmosServiceTest>> Licensed { get; }
        public Function<CosmosServiceTest, ValueSet<CellClass>> Identity { get; }
        public Relation<RenderPosition, RenderPosition> Opposite { get; }

        // A bundle's tables. The host writes a path's spelling beside its term (a merge of two paths would give one class
        // two spellings, and throw), the set, each import once and lattice-free, beside each atom what the facts its
        // conjunct was rendered from say its path may hold (`assumed`, anything where none is read), and the root's
        // candidate with every member of its junctions as written and each conjunction's members, nested ones flattened;
        // the rules write the rest: the forms each junction lies within, the candidates beneath the root, and the members
        // of a member of one connective.
        public Function<JPath, string> Spelling { get; }
        public Relation<ServiceForm, ServiceForm> Conjunct { get; }
        public Relation<RenderingSet> Under { get; }
        public Function<JPath, PathValues> Imported { get; }
        public Function<Pred, PathValues> Assumed { get; }
        public Function<JPath, ValueSet<string>> ImportedChars { get; }
        public Function<JPath, ValueSet<string>> PartedLiterals { get; }
        public Relation<Pred, RenderPosition, ServiceForm> Candidate { get; }
        public Relation<ServiceForm, ServiceForm> Member { get; }
        public Function<Pred, ServiceForm, CellPartition> Partition { get; }
        public Relation<CellPartition, ServiceForm> Need { get; }
        public Function<CellPartition, ServiceForm, OutcomeVector> Svc { get; }
        public Function<Pred, ServiceForm, CellOutcomes> Outcomes { get; }
        public Function<Pred, RenderPosition, ServiceForm, Verdict, Proof> Agrees { get; }

        /// <summary>Whether an agreement holds of nothing: by any of its proofs, Calcite raises on every row (a disjunction, by its lattice).</summary>
        public Function<Pred, RenderPosition, ServiceForm, Verdict, bool> Vacuous { get; }
        public Function<Pred, RenderPosition, ServiceForm, Verdict, Refutation> Disagrees { get; }

        /// <summary>The one location a refutation on an atom's cells sets, by its cells; a junction's sets two, and has none.</summary>
        public Relation<ValueSet<string>, JPath> RefutedAt { get; }
        public Relation<Pred, RenderPosition, ServiceForm, Verdict> Conflict { get; }

        /// <summary>A junction of the IR, its connective, and each arm beside the other; <see cref="First"/> names its first.</summary>
        public Relation<Pred, Pred, Pred, CosmosServiceTest> Junction { get; }
        public Relation<Pred, Pred> First { get; }

        /// <summary>
        /// A junction, a position and a form of its own connective it lies within: one it is a candidate for, or one its
        /// parent of that connective lies within. Its arms take the form's parts, and its own forms are built up from theirs.
        /// </summary>
        public Relation<Pred, RenderPosition, ServiceForm> Within { get; }
        public Function<ServiceForm, ValueSet<CosmosServiceTest>> Needs { get; }
        public Function<Pred, ServiceForm, CosmosServiceTest, Witness> Neutral { get; }
        public Relation<JPath, JPath> Disjoint { get; }

        // Generate mode's. The theory holds the templates asked at each position and the shapes each fits, the parting bands
        // as one set, the Rex builder's grammar, whether each name the IR reads a call by is null over a null operand (one
        // row an operator, which the lattice meets, so a name is strict only where every operator it stands for is), and the
        // readers Calcite answers null for over an absent path; the host writes a root where it stands (`at`), and beside
        // the terms each leaf over the value it is and each call's operands. The rules write the rest: each node's position, its cheapest exact form (`None`
        // until one is found), the forms it implies, and one weakening, lattice-free, which a second write faults.
        public Relation<Pred, RenderPosition> At { get; }
        public Relation<TemplateId, RenderPosition> Template { get; }
        public Relation<TemplateId, string> Fits { get; }
        public Function<ValueSet<string>> PartingBands { get; }
        public Function<RenderingRexGrammar.Value> RexGrammar { get; }
        public Function<string, bool> StrictOp { get; }
        public Relation<string> StrictReader { get; }
        public Function<Pred, RenderPosition, Priced> BestExact { get; }
        public Function<Pred, RenderPosition, ValueSet<ServiceForm>> Implied { get; }
        public Function<Pred, RenderPosition, ServiceForm> Weakening { get; }
        public Relation<Pred> Leaf { get; }
        public Relation<Pred, Val> Over { get; }
        public Relation<Val, Val> CallArg { get; }

        /// <summary>A value Calcite reads as null where a path is absent.</summary>
        public Relation<Val, JPath> Strict { get; }

        /// <summary>
        /// A place Calcite reads as null wherever the service holds it absent: every path the writer meets, and a computed
        /// place only beside its image at the root, the one place every key agrees <c>Same</c>.
        /// </summary>
        public Relation<JPath> NullWhereAbsent { get; }

        /// <summary>A leaf read beneath an arm's stratum, by the stratum's id: its own term, so its cells are that stratum's.</summary>
        public Constructor<long, Pred, Pred> Beneath { get; }

        /// <summary>
        /// A case as S writes it: its condition, its then-arm beneath the condition, and its else-arm twice, narrow beneath
        /// the condition's negation and wide where the case stands.
        /// </summary>
        public Constructor<Pred, Pred, Pred, Pred, Pred> Chosen { get; }

        /// <summary>An atom Calcite answers TRUE or FALSE on every cell, so that where it is not TRUE it is FALSE.</summary>
        public Relation<Pred> TwoValued { get; }

        /// <summary>A position and a truth value an else-arm leaves the case's rows as its condition's own failing leaves them.</summary>
        public Relation<RenderPosition, string> AbsorbingElse { get; }

        /// <summary>A position and a truth value an else-arm makes the case's rows the opposite of what its failing condition would.</summary>
        public Relation<RenderPosition, string> OppositeElse { get; }

        /// <summary>
        /// Builds S from a definition and loads its theory: one batch per axiom, then the host rows, settled and frozen.
        /// </summary>
        /// <exception cref="FactConflictException">
        /// A row contradicts another for one key and target. Its <c>Data["axiom"]</c> names the axiom whose batch was
        /// refused, and nothing of it was written; no S is built.
        /// </exception>
        public static RenderingTheory Define(RenderingDefinition definition) => new(definition);

        /// <summary>Opens a bundle under a set: a child of the theory, holding one statement round's terms, import and candidates.</summary>
        public RenderingBundle Open(RenderingSet set) => new(this, set);

        /// <summary>Whether a bundle found the graph out of ids or epochs, which retires its generation at once.</summary>
        internal volatile bool RanOut;

        static void Register(ValueStore values)
        {
            values.RegisterEnum<CosmosServiceTest>();
            values.RegisterEnum<CosmosValueClass>();
            values.RegisterEnum<CosmosAnswer>();
            values.RegisterEnum<RenderingTarget>();
            values.RegisterEnum<RenderingSet>();
            values.RegisterEnum<RenderPosition>();
            values.RegisterEnum<Verdict>();
            values.RegisterEnum<Truths>();
            values.RegisterEnum<ServiceOutcomes>();
            values.RegisterEnum<TemplateId>();
            values.RegisterEnum<CosmosFunctionAnswer>();
            values.RegisterEnum<FunctionValueClass>();
            values.Register<ServiceForm>("service form");
            values.Register<Priced>("priced");
            values.Register<RenderingRexGrammar.Value>("rex grammar");
            values.Register<CellClass>("cell class");
            values.Register<Box>("box");
            values.Register<Proof>("proof");
            values.Register<Refutation>("refutation");
            values.Register<MeasuredKey>("measured key");
            values.Register<FunctionKey>("function key");
            values.Register<RawKey>("raw key");
            values.Register<RawTruths>("raw truths");
            values.Register<(PredicateAtom, RawTruths)>("atom and raw truths");
            values.Register<PathImport>("path import");
            values.Register<MeasuredEntry>("measured entry");
            values.Register<AnswerMeanings>("answer meanings");
            values.Register<ServiceTable>("service table");
            values.Register<ReaderEdges>("reader edges");
            values.Register<CutPoint>("cut point");
            values.Register<CellPartition>("cell partition");
            values.Register<OutcomeVector>("outcome vector");
            values.Register<CellOutcomes>("cell outcomes");
            values.Register<Witness>("witness");
            values.RegisterSet<CosmosAnswer>();
            values.RegisterSet<RenderingTarget>();
            values.RegisterSet<CellClass>();
            values.RegisterSet<MeasuredEntry>();
            values.RegisterSet<CutPoint>();
            values.RegisterSet<MeasuredKey>();
            values.RegisterSet<FunctionKey>();
            values.RegisterSet<PathImport>();
            values.RegisterSet<CosmosServiceTest>();
            values.RegisterSet<CosmosValueClass>();
            values.RegisterSet<ServiceForm>();
        }

        void DefineTheory(Ruleset measure, Ruleset allowed, Ruleset refutes, Ruleset tabulate)
        {
            measure.DefineRule("a measured answer is one a set's service may give", r =>
            {
                var (k, x, s, o) = r.Vars<MeasuredKey, RenderingTarget, RenderingSet, CosmosAnswer>("k", "x", "s", "o");
                var (d, i) = r.Vars<ValueSet<RenderingTarget>, ValueSet<string>>("d", "i");
                r.When(o.Is(Outcome.Of(k, x)), d.Is(Serves.Of(s)), i.Is(Cites.Of(k, x)));
                r.Where(Primitives.Contains<RenderingTarget>().Of(d, x));
                r.Then(Answers.Of(k, s).Is(Primitives.Singleton<CosmosAnswer>().Of(o)), Measured.Of(k, s).Is(Primitives.Singleton<RenderingTarget>().Of(x)),
                    Evidence.Of(k, s).Is(i));
            });

            allowed.DefineRule("a class within an allowing box is allowed", r =>
            {
                var (k, b, tol) = r.Vars<CellClass, Box, Truths>("k", "b", "tol");
                var (pos, v) = r.Vars<RenderPosition, Verdict>("pos", "v");
                var (kc, bc, ks, bs) = r.Vars<Truths, Truths, ServiceOutcomes, ServiceOutcomes>("kc", "bc", "ks", "bs");
                r.When(Class.Of(k), Allowing.Of(pos, v, b), tol.Is(Tolerated.Of()), kc.Is(Compute.Of(k, tol, (x, y) => x.Calcite & ~y)),
                    bc.Is(Compute.Of(b, x => x.Calcite)), ks.Is(Compute.Of(k, x => x.Service)), bs.Is(Compute.Of(b, x => x.Service)));
                r.Where(RenderingPrimitives.TruthsWithin.Of(kc, bc));
                r.Where(RenderingPrimitives.OutcomesWithin.Of(ks, bs));
                r.Then(Allowed.Of(pos, v).Is(Primitives.Singleton<CellClass>().Of(k)), AllowedCites.Of(pos, v).Is(Compute.Of(b, x => x.Cites)));
            });

            allowed.DefineRule("a class whose every truth of Calcite's the policy sets aside is one Calcite raises on alone", r =>
            {
                var (k, tol, kc) = r.Vars<CellClass, Truths, Truths>("k", "tol", "kc");
                r.When(Class.Of(k), tol.Is(Tolerated.Of()), kc.Is(Compute.Of(k, x => x.Calcite)));
                r.Where(RenderingPrimitives.TruthsWithin.Of(kc, tol));
                r.Then(RaiseOnly.Of().Is(Primitives.Singleton<CellClass>().Of(k)));
            });

            // Calcite's truths as they are: a class that may raise is no definite counterexample, whatever the box says.
            refutes.DefineRule("a class within a refuting box is a definite counterexample", r =>
            {
                var (k, b) = r.Vars<CellClass, Box>("k", "b");
                var (pos, v) = r.Vars<RenderPosition, Verdict>("pos", "v");
                var (kc, bc, ks, bs) = r.Vars<Truths, Truths, ServiceOutcomes, ServiceOutcomes>("kc", "bc", "ks", "bs");
                r.When(Class.Of(k), Refuting.Of(pos, v, b), kc.Is(Compute.Of(k, x => x.Calcite)), bc.Is(Compute.Of(b, x => x.Calcite)),
                    ks.Is(Compute.Of(k, x => x.Service)), bs.Is(Compute.Of(b, x => x.Service)));
                r.Where(RenderingPrimitives.TruthsWithin.Of(kc, Truths.Any));
                r.Where(RenderingPrimitives.TruthsWithin.Of(kc, bc));
                r.Where(RenderingPrimitives.OutcomesWithin.Of(ks, bs));
                r.Then(Refutes.Of(pos, v).Is(Primitives.Singleton<CellClass>().Of(k)));
            });

            // Outcome is complete here too: a class an order row is of is placed by comparators, whatever set asks.
            tabulate.DefineRule("a class some row orders is placed by its comparators", r =>
            {
                var (k, tg) = r.Vars<MeasuredKey, RenderingTarget>("k", "tg");
                var (a, t, c) = r.Vars<CosmosAnswer, CosmosServiceTest, CosmosValueClass>("a", "t", "c");
                r.When(a.Is(Outcome.Of(k, tg)), t.Is(Compute.Of(k, x => x.Test)), t.Is(CosmosServiceTest.Order), c.Is(Compute.Of(k, x => x.Operand)));
                r.Then(Placed.Of().Is(Primitives.Singleton<CosmosValueClass>().Of(c)));
            });

            // Measured is complete when this stratum runs, so a key is settled once and its answers are all of them. What each
            // answer is to the leaf is the meanings' roles, met here (F4): the comparators that place a cell, those an affix
            // matches by, and the comparators beside which a match says nothing.
            tabulate.DefineRule("a settled key is an entry of its set's table", r =>
            {
                var (k, s) = r.Vars<MeasuredKey, RenderingSet>("k", "s");
                var (m, d) = r.Vars<ValueSet<RenderingTarget>, ValueSet<RenderingTarget>>("m", "d");
                var (v, i, mn) = r.Vars<ValueSet<CosmosAnswer>, ValueSet<string>, AnswerMeanings>("v", "i", "mn");
                r.When(m.Is(Measured.Of(k, s)), d.Is(Serves.Of(s)), v.Is(Answers.Of(k, s)), i.Is(Evidence.Of(k, s)), mn.Is(Means.Of()));
                r.Where(Primitives.IsSubsetOf<RenderingTarget>().Of(d, m));
                var placing = Primitives.Intersect<CosmosAnswer>().Of(v, Compute.Of(mn, x => x.Comparators));
                var matching = Primitives.Intersect<CosmosAnswer>().Of(v, Compute.Of(mn, x => x.Matchers));
                var unmatched = Primitives.Except<CosmosAnswer>().Of(placing, Compute.Of(mn, x => x.Matchers));
                r.Then(Table.Of(s).Is(Primitives.Singleton<MeasuredEntry>().Of(Compute.Of(Compute.Of(k, v, i, mn, MeasuredEntry.Of), placing, matching, unmatched, MeasuredEntry.Placed))),
                    Final.Of(k, s).Is(v));
            });
        }

        // A connective composes in a set where every key Kleene's logic expects of the set's targets is settled there as
        // Kleene's; Calcite composes only where its own rows hold too, which every composition reads beside the set's.
        void DefineLicences(Ruleset expected, Ruleset matched, Ruleset licence)
        {
            expected.DefineRule("every Kleene cell of a connective is expected of a set that serves a target it is Kleene's for", r =>
            {
                var (k, y, s, x) = r.Vars<MeasuredKey, RenderingTarget, RenderingSet, CosmosAnswer>("k", "y", "s", "x");
                var d = r.Var<ValueSet<RenderingTarget>>("d");
                r.When(x.Is(Kleene.Of(k, y)), d.Is(Serves.Of(s)));
                r.Where(Primitives.Contains<RenderingTarget>().Of(d, y));
                r.Then(Expected.Of(Compute.Of(k, w => w.Test), s).Is(Primitives.Singleton<MeasuredKey>().Of(k)));
            });

            // A settled answer is never empty, so one within Kleene's single answer is that answer.
            matched.DefineRule("a settled answer that is Kleene's matches", r =>
            {
                var (k, y, s, x) = r.Vars<MeasuredKey, RenderingTarget, RenderingSet, CosmosAnswer>("k", "y", "s", "x");
                var (d, v) = r.Vars<ValueSet<RenderingTarget>, ValueSet<CosmosAnswer>>("d", "v");
                r.When(v.Is(Final.Of(k, s)), x.Is(Kleene.Of(k, y)), d.Is(Serves.Of(s)));
                r.Where(Primitives.Contains<RenderingTarget>().Of(d, y));
                r.Where(Primitives.IsSubsetOf<CosmosAnswer>().Of(v, Primitives.Singleton<CosmosAnswer>().Of(x)));
                r.Then(Matched.Of(Compute.Of(k, w => w.Test), s).Is(Primitives.Singleton<MeasuredKey>().Of(k)));
            });

            licence.DefineRule("a connective every expected cell of which matched composes in the set", r =>
            {
                var (t, s) = r.Vars<CosmosServiceTest, RenderingSet>("t", "s");
                var (m, n) = r.Vars<ValueSet<MeasuredKey>, ValueSet<MeasuredKey>>("m", "n");
                r.When(m.Is(Matched.Of(t, s)), n.Is(Expected.Of(t, s)));
                r.Where(Primitives.IsSubsetOf<MeasuredKey>().Of(n, m));
                r.Then(Licensed.Of(s).Is(Primitives.Singleton<CosmosServiceTest>().Of(t)));
            });
        }

        // Positions and pairings are derived, never carried: every arm of a junction lying within a form is proposed each
        // part of it, so the gate decides which pairing holds, and the junction's own forms are composed up from what its
        // arms agreed with (design 2, DESIGN.md); a negation's operand takes the form stripped of a NOT at the opposite
        // position.
        void DefineDescent(Ruleset structure, Ruleset descend, Ruleset disjoint)
        {
            structure.DefineRule("a conjunction's arms are its junction's sides", r =>
            {
                var (e, a, b) = r.Vars<Pred, Pred, Pred>("e", "a", "b");
                r.When(e.Is(Ir.And.Of(a, b)));
                r.Then(Junction.Of(e, a, b, CosmosServiceTest.And), Junction.Of(e, b, a, CosmosServiceTest.And), First.Of(e, a));
            });

            structure.DefineRule("a disjunction's arms are its junction's sides", r =>
            {
                var (e, a, b) = r.Vars<Pred, Pred, Pred>("e", "a", "b");
                r.When(e.Is(Ir.Or.Of(a, b)));
                r.Then(Junction.Of(e, a, b, CosmosServiceTest.Or), Junction.Of(e, b, a, CosmosServiceTest.Or), First.Of(e, a));
            });

            // A form of a junction's own connective is what the junction and every junction of that connective beneath it lie
            // within; each arm of them is offered each part of it, and the compose rules build the junctions' forms up from
            // the parts their arms agree with or are refuted on, never a subset of it no arm has taken.
            descend.DefineRule("a junction lies within each form of its own connective it is a candidate for", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (pos, j, f) = r.Vars<RenderPosition, CosmosServiceTest, ServiceForm>("pos", "j", "f");
                r.When(Candidate.Of(e, pos, f), Junction.Of(e, x, y, j));
                r.Where(RenderingPrimitives.FormHeads.Of(f, j));
                r.Then(Within.Of(e, pos, f), Needs.Of(f).Is(ValueSet<CosmosServiceTest>.Empty));
            });

            descend.DefineRule("an arm of a junction that is a junction of its connective lies within what the junction lies within", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (u, w) = r.Vars<Pred, Pred>("u", "w");
                var (pos, j, f) = r.Vars<RenderPosition, CosmosServiceTest, ServiceForm>("pos", "j", "f");
                r.When(Within.Of(e, pos, f), Junction.Of(e, x, y, j), Junction.Of(y, u, w, j));
                r.Then(Within.Of(y, pos, f));
            });

            descend.DefineRule("each arm of a junction takes each part of a form it lies within", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (pos, j) = r.Vars<RenderPosition, CosmosServiceTest>("pos", "j");
                var (f, g) = r.Vars<ServiceForm, ServiceForm>("f", "g");
                r.When(Within.Of(e, pos, f), Junction.Of(e, x, y, j), Member.Of(f, g));
                r.Then(Candidate.Of(x, pos, g));
            });

            // A conjunct written twice, or with a member beside it that reads its path alone, is one atom's: offered the
            // junction of every part reading that path, the atom evaluates them together on its cells.
            descend.DefineRule("an atom of a junction takes the part of a form it lies within that reads its path alone", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (pos, j, f) = r.Vars<RenderPosition, CosmosServiceTest, ServiceForm>("pos", "j", "f");
                var (p, a, n) = r.Vars<JPath, PredicateAtom, string>("p", "a", "n");
                r.When(Within.Of(e, pos, f), Junction.Of(e, x, y, j), Ir.Atom.Of(x, p, a), n.Is(Spelling.Of(p)));
                r.Then(Candidate.Of(x, pos, Compute.Of(f, n, ServiceForm.Reading)));
            });

            descend.DefineRule("a negation's operand is a candidate for the form's operand at the opposite position", r =>
            {
                var (e, a) = r.Vars<Pred, Pred>("e", "a");
                var (pos, q, g) = r.Vars<RenderPosition, RenderPosition, ServiceForm>("pos", "q", "g");
                r.When(Candidate.Of(e, pos, g), e.Is(Ir.Not.Of(a)), Opposite.Of(pos, q));
                r.Then(Candidate.Of(a, q, Compute.Of(g, ServiceForm.Strip)));
            });

            // A member of a member of the junction's own connective is a part an arm may take, the licensed connective being
            // associative: the reader writes members as written, and this is the regrouping.
            descend.DefineRule("a member of a member of a junction's own connective is a member of the junction", r =>
            {
                var (f, g, h) = r.Vars<ServiceForm, ServiceForm, ServiceForm>("f", "g", "h");
                r.When(Member.Of(f, g), Member.Of(g, h));
                r.Where(RenderingPrimitives.FormJoins.Of(f, g));
                r.Then(Member.Of(f, h));
            });

            // Never an element against an index, nor a path against one beneath it: those may be one place.
            disjoint.DefineRule("two properties of one parent with different names are disjoint locations", r =>
            {
                var (p, q, u) = r.Vars<JPath, JPath, JPath>("p", "q", "u");
                var (n, m) = r.Vars<string, string>("n", "m");
                r.When(p.Is(Paths.Prop.Of(u, n)), q.Is(Paths.Prop.Of(u, m)), n.IsNot(m));
                r.Then(Disjoint.Of(p, q));
            });
        }

        void DefineCells(RenderingDefinition definition, Ruleset partition, Ruleset need, Ruleset parted)
        {
            partition.DefineRule("an atom's import is cut into cells, at its literals, the form's and the edges its reader names, where the form reads that path alone", r =>
            {
                var (e, p, a, f) = r.Vars<Pred, JPath, PredicateAtom, ServiceForm>("e", "p", "a", "f");
                var (pos, x, k, y) = r.Vars<RenderPosition, PathValues, ValueSet<string>, PathValues>("pos", "x", "k", "y");
                var (n, rd, g) = r.Vars<string, string, ReaderEdges>("n", "rd", "g");
                r.When(Candidate.Of(e, pos, f), Ir.Atom.Of(e, p, a), x.Is(Imported.Of(p)), k.Is(ImportedChars.Of(p)), y.Is(Assumed.Of(e)), n.Is(Spelling.Of(p)),
                    rd.Is(Compute.Of(a, w => w.Reader)), g.Is(Edges.Of(rd)));
                r.Where(Primitives.IsSubsetOf<string>().Of(Compute.Of(f, w => w.Paths), Compute.Of(n, PlaceSpellings.Set)));
                var cuts = Compute.Of(a, f, g, definition.Cut);
                r.Then(Partition.Of(e, f).Is(Compute.Of(Compute.Of(n, x, k, cuts, definition.Split), y, k, cuts, definition.SetAside)));
            });

            need.DefineRule("a candidate of an atom is evaluated on the atom's cells", r =>
            {
                var (e, p, a, f) = r.Vars<Pred, JPath, PredicateAtom, ServiceForm>("e", "p", "a", "f");
                var (pos, z) = r.Vars<RenderPosition, CellPartition>("pos", "z");
                r.When(Candidate.Of(e, pos, f), Ir.Atom.Of(e, p, a), z.Is(Partition.Of(e, f)));
                r.Then(Need.Of(z, f), Needs.Of(f).Is(ValueSet<CosmosServiceTest>.Empty));
            });

            // Every value a body computes is total: body calls are unordered, so a filter beside one guards nothing. A
            // leaf is its own part, and none of these derives anything from it.
            need.DefineRule("a compound that is evaluated is evaluated through its parts", r =>
            {
                var z = r.Var<CellPartition>("z");
                var (g, h1, h2) = r.Vars<ServiceForm, ServiceForm, ServiceForm>("g", "h1", "h2");
                r.When(Need.Of(z, g));
                r.Where(RenderingPrimitives.FormIsCompound.Of(g));
                r.When(h1.Is(Compute.Of(g, ServiceForm.First)), h2.Is(Compute.Of(g, ServiceForm.Rest)));
                r.Then(Need.Of(z, h1), Need.Of(z, h2), Needs.Of(h1).Is(ValueSet<CosmosServiceTest>.Empty), Needs.Of(h2).Is(ValueSet<CosmosServiceTest>.Empty));
            });

            // Which bands part is the set's string comparators' (Service.StringOrderIsCodePoint's parts, F3).
            parted.DefineRule("a string literal parts the orders over a path whose strings may hold the other band", r =>
            {
                var (z, e, p, a) = r.Vars<CellPartition, Pred, JPath, PredicateAtom>("z", "e", "p", "a");
                var (f, g) = r.Vars<ServiceForm, ServiceForm>("f", "g");
                var (k, b) = r.Vars<ValueSet<string>, ValueSet<string>>("k", "b");
                var (u, w, l) = r.Vars<string, string, string>("u", "w", "l");
                var (s, o, c) = r.Vars<RenderingSet, ValueSet<CosmosAnswer>, CosmosAnswer>("s", "o", "c");
                r.When(Need.Of(z, g), z.Is(Partition.Of(e, f)), Ir.Atom.Of(e, p, a), k.Is(ImportedChars.Of(p)), Under.Of(s), o.Is(Final.Of(StringOrder, s)), Parts.Of(c, u, w));
                r.Where(Primitives.Contains<CosmosAnswer>().Of(o, c));
                r.Where(RenderingPrimitives.StringComparison.Of(g));
                r.When(l.Is(Compute.Of(g, x => x.Literal ?? "")), b.Is(Compute.Of(g, CharactersOfLiteral)));
                r.Where(Primitives.Contains<string>().Of(k, u));
                r.Where(Primitives.Contains<string>().Of(b, w));
                r.Then(PartedLiterals.Of(p).Is(Primitives.Singleton<string>().Of(l)));
            });

            // The service may answer two bounds of one path as a range, not each bound, where they part (N8). What it is
            // asked is the conjunction as written, so its own orderings are read, not the IR's.
            parted.DefineRule("two string orderings of one path in a written conjunction, one of whose literals parts the orders there, need Range", r =>
            {
                var (f, h1, h2, p) = r.Vars<ServiceForm, ServiceForm, ServiceForm, JPath>("f", "h1", "h2", "p");
                var (k, b) = r.Vars<ValueSet<string>, ValueSet<string>>("k", "b");
                var (n, u, w) = r.Vars<string, string, string>("n", "u", "w");
                var (s, o, c) = r.Vars<RenderingSet, ValueSet<CosmosAnswer>, CosmosAnswer>("s", "o", "c");
                r.When(Conjunct.Of(f, h1), Conjunct.Of(f, h2), h1.IsNot(h2), n.Is(Compute.Of(h1, x => x.Path ?? "")), n.Is(Compute.Of(h2, x => x.Path ?? "")),
                    n.Is(Spelling.Of(p)), k.Is(ImportedChars.Of(p)), Under.Of(s), o.Is(Final.Of(StringOrder, s)), Parts.Of(c, u, w), b.Is(Compute.Of(h1, CharactersOfLiteral)));
                r.Where(Primitives.Contains<CosmosAnswer>().Of(o, c));
                r.Where(RenderingPrimitives.StringOrdering.Of(h1));
                r.Where(RenderingPrimitives.StringOrdering.Of(h2));
                r.Where(Primitives.Contains<string>().Of(k, u));
                r.Where(Primitives.Contains<string>().Of(b, w));
                r.Then(Needs.Of(f).Is(Primitives.Singleton<CosmosServiceTest>().Of(CosmosServiceTest.Range)));
            });
        }

        void DefineEvaluation(RenderingDefinition definition, Ruleset evaluate)
        {
            // Which literals part the orders is complete here, an earlier stratum's: a literal not among them is read as not.
            evaluate.DefineRule("a test is evaluated on every cell by the rows", r =>
            {
                var (z, e, p, a) = r.Vars<CellPartition, Pred, JPath, PredicateAtom>("z", "e", "p", "a");
                var (f, g, s) = r.Vars<ServiceForm, ServiceForm, RenderingSet>("f", "g", "s");
                var (q, t, pl) = r.Vars<ValueSet<string>, ValueSet<MeasuredEntry>, ValueSet<CosmosValueClass>>("q", "t", "pl");
                r.When(Need.Of(z, g), z.Is(Partition.Of(e, f)), Ir.Atom.Of(e, p, a), q.Is(PartedLiterals.Of(p)), Under.Of(s), t.Is(Table.Of(s)), pl.Is(Placed.Of()));
                r.Where(RenderingPrimitives.FormIsLeaf.Of(g));
                r.Then(Svc.Of(z, g).Is(Compute.Of(g, z, Compute.Of(t, pl, ServiceTable.Of), q, definition.Leaf)));
            });

            // A junction of three members or more is evaluated as its first and a regrouped rest rid of copies, which only
            // the set's licence makes the junction's own value; two members read their row as written.
            evaluate.DefineRule("a compound is evaluated from its parts by its connective's rows", r =>
            {
                var z = r.Var<CellPartition>("z");
                var (g, h1, h2, s) = r.Vars<ServiceForm, ServiceForm, ServiceForm, RenderingSet>("g", "h1", "h2", "s");
                var (v, w, t) = r.Vars<OutcomeVector, OutcomeVector, ValueSet<MeasuredEntry>>("v", "w", "t");
                var (lic, nd, pl) = r.Vars<ValueSet<CosmosServiceTest>, ValueSet<CosmosServiceTest>, ValueSet<CosmosValueClass>>("lic", "nd", "pl");
                r.When(Need.Of(z, g));
                r.Where(RenderingPrimitives.FormIsCompound.Of(g));
                r.When(h1.Is(Compute.Of(g, ServiceForm.First)), h2.Is(Compute.Of(g, ServiceForm.Rest)), v.Is(Svc.Of(z, h1)), w.Is(Svc.Of(z, h2)),
                    Under.Of(s), t.Is(Table.Of(s)), lic.Is(Licensed.Of(s)), nd.Is(Needs.Of(g)), pl.Is(Placed.Of()));
                r.Where(Primitives.IsSubsetOf<CosmosServiceTest>().Of(RenderingPrimitives.FormRegroups.Of(g), lic));
                r.Where(Primitives.IsSubsetOf<CosmosServiceTest>().Of(nd, lic));
                r.Then(Svc.Of(z, g).Is(Compute.Of(g, v, w, Compute.Of(t, pl, ServiceTable.Of), definition.Compound)));
            });
        }

        void DefineGate(RenderingDefinition definition, Ruleset classes, Ruleset gate, Ruleset conflict)
        {
            classes.DefineRule("Calcite's atom and the form are paired on every cell", r =>
            {
                var (e, p, a, f) = r.Vars<Pred, JPath, PredicateAtom, ServiceForm>("e", "p", "a", "f");
                var (pos, z, v) = r.Vars<RenderPosition, CellPartition, OutcomeVector>("pos", "z", "v");
                var (mn, tol, u) = r.Vars<AnswerMeanings, Truths, RawTruths>("mn", "tol", "u");
                r.When(Candidate.Of(e, pos, f), Ir.Atom.Of(e, p, a), z.Is(Partition.Of(e, f)), v.Is(Svc.Of(z, f)), mn.Is(Means.Of()), tol.Is(Tolerated.Of()),
                    u.Is(RawUnion.Of()));
                // Compute takes four operands: the atom goes beside the columns its raw cells read.
                r.Then(Outcomes.Of(e, f).Is(Compute.Of(Compute.Of(a, u, (x, y) => (x, y)), v, mn, tol, (au, w, m, t) => definition.Pair(au.Item1, au.Item2, w, m, t))));
            });

            // The universal over cells: the cells' classes are complete (lattice-free), and the allowed ones the theory's.
            gate.DefineRule("a candidate agrees where every class of its cells is allowed at its position", r =>
            {
                var (e, pos, f, v) = r.Vars<Pred, RenderPosition, ServiceForm, Verdict>("e", "pos", "f", "v");
                var (o, w) = r.Vars<CellOutcomes, ValueSet<string>>("o", "w");
                var (kk, y, ro) = r.Vars<ValueSet<CellClass>, ValueSet<CellClass>, ValueSet<CellClass>>("kk", "y", "ro");
                r.When(Candidate.Of(e, pos, f), o.Is(Outcomes.Of(e, f)), kk.Is(Compute.Of(o, x => x.Classes)), y.Is(Allowed.Of(pos, v)),
                    w.Is(AllowedCites.Of(pos, v)), ro.Is(RaiseOnly.Of()));
                r.Where(Primitives.IsSubsetOf<CellClass>().Of(kk, y));
                r.Then(Agrees.Of(e, pos, f, v).Is(Compute.Of(o, w, Proof.Of)), Vacuous.Of(e, pos, f, v).Is(Primitives.IsSubsetOf<CellClass>().Of(kk, ro)));
            });

            gate.DefineRule("a candidate is refuted where a class of its cells is a definite counterexample", r =>
            {
                var (e, pos, f, v) = r.Vars<Pred, RenderPosition, ServiceForm, Verdict>("e", "pos", "f", "v");
                var (o, p, a) = r.Vars<CellOutcomes, JPath, PredicateAtom>("o", "p", "a");
                var (kk, y, m) = r.Vars<ValueSet<CellClass>, ValueSet<CellClass>, ValueSet<CellClass>>("kk", "y", "m");
                r.When(Candidate.Of(e, pos, f), o.Is(Outcomes.Of(e, f)), Ir.Atom.Of(e, p, a), kk.Is(Compute.Of(o, x => x.Classes)), y.Is(Refutes.Of(pos, v)),
                    m.Is(Primitives.Intersect<CellClass>().Of(kk, y)));
                r.Where(RenderingPrimitives.SetNonempty.Of(m));
                r.Then(Disagrees.Of(e, pos, f, v).Is(Compute.Of(o, m, Refutation.Of)), RefutedAt.Of(Compute.Of(Compute.Of(o, m, Refutation.Of), x => x.Cells), p));
            });

            gate.DefineRule("an atom is a connective's identity on some cell", r =>
            {
                var (e, f, j) = r.Vars<Pred, ServiceForm, CosmosServiceTest>("e", "f", "j");
                var o = r.Var<CellOutcomes>("o");
                var (kk, y, m) = r.Vars<ValueSet<CellClass>, ValueSet<CellClass>, ValueSet<CellClass>>("kk", "y", "m");
                r.When(o.Is(Outcomes.Of(e, f)), kk.Is(Compute.Of(o, x => x.Classes)), y.Is(Identity.Of(j)), m.Is(Primitives.Intersect<CellClass>().Of(kk, y)));
                r.Where(RenderingPrimitives.SetNonempty.Of(m));
                r.Then(Neutral.Of(e, f, j).Is(Compute.Of(o, y, Witness.Of)));
            });

            conflict.DefineRule("an agreement and a refutation of one candidate are a contradiction", r =>
            {
                var (e, pos, f, v) = r.Vars<Pred, RenderPosition, ServiceForm, Verdict>("e", "pos", "f", "v");
                r.When(Fact.Exists(Agrees.Of(e, pos, f, v)), Fact.Exists(Disagrees.Of(e, pos, f, v)));
                r.Then(Conflict.Of(e, pos, f, v), Graph.Contradiction.Of());
            });
        }

        // Each step holds by the identities the licence makes Kleene's on both sides. A node may hold candidates from more
        // than one parent, so the connective is checked where the step is taken, not only where the sides were offered.
        void DefineComposition(Ruleset compose)
        {
            // The junction's form is the part of the form it lies within that its sides make, so it is built from what agreed,
            // and held to exactly their join; the need checked is the written form's, the one the service is asked.
            compose.DefineRule("a junction agrees where both sides agree on one verdict, as the part of a form it lies within that they make", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (pos, j, v) = r.Vars<RenderPosition, CosmosServiceTest, Verdict>("pos", "j", "v");
                var (f, l, m, h) = r.Vars<ServiceForm, ServiceForm, ServiceForm, ServiceForm>("f", "l", "m", "h");
                var (pl, pr) = r.Vars<Proof, Proof>("pl", "pr");
                var (vl, vr) = r.Vars<bool, bool>("vl", "vr");
                r.When(Within.Of(e, pos, f), First.Of(e, x), Junction.Of(e, x, y, j), pl.Is(Agrees.Of(x, pos, l, v)), pr.Is(Agrees.Of(y, pos, m, v)),
                    vl.Is(Vacuous.Of(x, pos, l, v)), vr.Is(Vacuous.Of(y, pos, m, v)), h.Is(Compute.Of(f, l, m, ServiceForm.Spanned)));
                r.Where(RenderingPrimitives.FormDecomposes.Of(h, l, m));
                Composes(r, j, f);
                r.Then(Agrees.Of(e, pos, h, v).Is(Compute.Of(pl, pr, Proof.Join)), Vacuous.Of(e, pos, h, v).Is(vl), Vacuous.Of(e, pos, h, v).Is(vr));
            });

            compose.DefineRule("a negation agrees where its operand agrees at the opposite position", r =>
            {
                var (e, a) = r.Vars<Pred, Pred>("e", "a");
                var (pos, q, v) = r.Vars<RenderPosition, RenderPosition, Verdict>("pos", "q", "v");
                var (g, h, p) = r.Vars<ServiceForm, ServiceForm, Proof>("g", "h", "p");
                var vp = r.Var<bool>("vp");
                r.When(Candidate.Of(e, pos, g), e.Is(Ir.Not.Of(a)), Opposite.Of(pos, q), h.Is(Compute.Of(g, ServiceForm.Strip)), p.Is(Agrees.Of(a, q, h, v)), vp.Is(Vacuous.Of(a, q, h, v)));
                Composes(r, CosmosServiceTest.Not, null);
                r.Then(Agrees.Of(e, pos, g, v).Is(p), Vacuous.Of(e, pos, g, v).Is(vp));
            });

            compose.DefineRule("a junction is refuted where one side is refuted and the other is its identity on another location, as the part of a form it lies within that they make", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (pos, j, v) = r.Vars<RenderPosition, CosmosServiceTest, Verdict>("pos", "j", "v");
                var (f, l, m, h) = r.Vars<ServiceForm, ServiceForm, ServiceForm, ServiceForm>("f", "l", "m", "h");
                var (z, w, c) = r.Vars<Refutation, Witness, ValueSet<string>>("z", "w", "c");
                var (p1, p2, a2) = r.Vars<JPath, JPath, PredicateAtom>("p1", "p2", "a2");
                r.When(Within.Of(e, pos, f), Junction.Of(e, x, y, j), z.Is(Disagrees.Of(x, pos, l, v)), w.Is(Neutral.Of(y, m, j)),
                    c.Is(Compute.Of(z, t => t.Cells)), RefutedAt.Of(c, p1), Ir.Atom.Of(y, p2, a2), Disjoint.Of(p1, p2), h.Is(Compute.Of(f, l, m, ServiceForm.Spanned)));
                r.Where(RenderingPrimitives.FormDecomposes.Of(h, l, m));
                Composes(r, j, f);
                r.Then(Disagrees.Of(e, pos, h, v).Is(Compute.Of(z, w, Refutation.With)));
            });

            compose.DefineRule("a negation is refuted where its operand is refuted at the opposite position", r =>
            {
                var (e, a) = r.Vars<Pred, Pred>("e", "a");
                var (pos, q, v) = r.Vars<RenderPosition, RenderPosition, Verdict>("pos", "q", "v");
                var (g, h, z) = r.Vars<ServiceForm, ServiceForm, Refutation>("g", "h", "z");
                r.When(Candidate.Of(e, pos, g), e.Is(Ir.Not.Of(a)), Opposite.Of(pos, q), h.Is(Compute.Of(g, ServiceForm.Strip)), z.Is(Disagrees.Of(a, q, h, v)));
                Composes(r, CosmosServiceTest.Not, null);
                r.Then(Disagrees.Of(e, pos, g, v).Is(Compute.Of(z, Refutation.Negated)));
            });
        }

        // S's alone, in the vocabulary's ruleset (G8): what a number Calcite wrote cast denotes, which the loader reads as the
        // casts' terms where it reads places, and the atom of a comparison with it where a row says the reading takes it. A
        // cast to a DECIMAL its whole digits do not fit raises in Calcite and denotes nothing; one whose scale is below the
        // number's truncates it toward zero, by less than one, which only a reading that rescales its literal takes.
        void DefineComparands()
        {
            Ir.Analyse.DefineRule("a literal a cast to DOUBLE converts denotes the number it spells", r =>
            {
                var (v, w) = r.Vars<Val, Val>("v", "w");
                var c = r.Var<string>("c");
                r.When(v.Is(Ir.Cast.Of("DOUBLE", w)), w.Is(Ir.Literal.Of(c)));
                r.Then(Denotes.Of(w, c, false));
            });
            Ir.Analyse.DefineRule("a literal a cast to a DECIMAL converts denotes the number it spells", r =>
            {
                var (v, w) = r.Vars<Val, Val>("v", "w");
                var (pr, sc, c) = r.Vars<long, long, string>("pr", "sc", "c");
                r.When(v.Is(Ir.DecimalCast.Of(pr, sc, w)), w.Is(Ir.Literal.Of(c)));
                r.Then(Denotes.Of(w, c, false));
            });
            Ir.Analyse.DefineRule("a cast to DOUBLE denotes what it converts denotes", r =>
            {
                var (v, w) = r.Vars<Val, Val>("v", "w");
                var (c, m) = r.Vars<string, bool>("c", "m");
                r.When(v.Is(Ir.Cast.Of("DOUBLE", w)), Denotes.Of(w, c, m));
                r.Then(Denotes.Of(v, c, m));
            });
            Ir.Analyse.DefineRule("a cast to a DECIMAL that holds the number's digits denotes what it converts denotes", r =>
            {
                var (v, w) = r.Vars<Val, Val>("v", "w");
                var (pr, sc) = r.Vars<long, long>("pr", "sc");
                var (c, m) = r.Vars<string, bool>("c", "m");
                r.When(v.Is(Ir.DecimalCast.Of(pr, sc, w)), Denotes.Of(w, c, m));
                r.Where(RenderingPrimitives.WholeDigitsFit.Of(c, pr, sc));
                r.Where(RenderingPrimitives.PlacesWithin.Of(c, sc));
                r.Then(Denotes.Of(v, c, m));
            });
            Ir.Analyse.DefineRule("a cast to a DECIMAL that drops the number's places denotes it moved", r =>
            {
                var (v, w) = r.Vars<Val, Val>("v", "w");
                var (pr, sc) = r.Vars<long, long>("pr", "sc");
                var (c, m) = r.Vars<string, bool>("c", "m");
                r.When(v.Is(Ir.DecimalCast.Of(pr, sc, w)), Denotes.Of(w, c, m));
                r.Where(RenderingPrimitives.WholeDigitsFit.Of(c, pr, sc));
                r.Where(RenderingPrimitives.PlacesBeyond.Of(c, sc));
                r.Then(Denotes.Of(v, c, true));
            });
            Ir.Analyse.DefineRule("a comparison with what a cast denotes is an atom of what it reads where its reading takes it", r =>
            {
                var (e, v, w) = r.Vars<Pred, Val, Val>("e", "v", "w");
                var (op, c, k, o) = r.Vars<string, string, string, string>("op", "c", "k", "o");
                var (m, p, row) = r.Vars<bool, JPath, string>("m", "p", "row");
                r.When(e.Is(Ir.Compare.Of(op, v, w)), Denotes.Of(w, c, m), Ir.Reads.Of(v, p, k), o.Is(Compute.Of(k, PredicateAtom.Outermost)), TakesConstant.Of(o, m, row));
                r.Then(Ir.Atom.Of(e, p, Compute.Of(k, op, c, PredicateAtom.Compare)));
            });
        }

        // Positions are derived down the IR, never carried; each node they reach starts with no exact form and nothing
        // implied, complete values the later strata only lower or grow. A template only proposes: the gate disposes.
        void DefineProposal(Ruleset at, Ruleset spell)
        {
            at.DefineRule("a junction's arms stand where it stands", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (pos, j) = r.Vars<RenderPosition, CosmosServiceTest>("pos", "j");
                r.When(At.Of(e, pos), Junction.Of(e, x, y, j));
                r.Then(At.Of(x, pos), BestExact.Of(x, pos).Is(Priced.None), Implied.Of(x, pos).Is(ValueSet<ServiceForm>.Empty));
            });

            at.DefineRule("a negation's operand stands at the opposite position", r =>
            {
                var (e, a) = r.Vars<Pred, Pred>("e", "a");
                var (pos, q) = r.Vars<RenderPosition, RenderPosition>("pos", "q");
                r.When(At.Of(e, pos), e.Is(Ir.Not.Of(a)), Opposite.Of(pos, q));
                r.Then(At.Of(a, q), BestExact.Of(a, q).Is(Priced.None), Implied.Of(a, q).Is(ValueSet<ServiceForm>.Empty));
            });

            spell.DefineRule("a template that fits an atom proposes its form at the atom's position", r =>
            {
                var (e, p, a, pos) = r.Vars<Pred, JPath, PredicateAtom, RenderPosition>("e", "p", "a", "pos");
                var (t, n, sh) = r.Vars<TemplateId, string, string>("t", "n", "sh");
                r.When(At.Of(e, pos), Ir.Atom.Of(e, p, a), n.Is(Spelling.Of(p)), Template.Of(t, pos), sh.Is(Compute.Of(a, RenderingTemplates.ShapeOf)), Fits.Of(t, sh));
                r.Then(Candidate.Of(e, pos, Compute.Of(t, pos, a, n, RenderingTemplates.Spell)));
            });
        }

        // The gate's agreements, complete here, lifted where they rest on something and may be sent: an exact one offered at
        // its price, an implied one the Rex builder writes as a member. Presence through calls is three rules over rows.
        void DefineLifts(Ruleset lift, Ruleset strict)
        {
            lift.DefineRule("an exact agreement that rests on something and may be sent is a best candidate", r =>
            {
                var (e, pos, f) = r.Vars<Pred, RenderPosition, ServiceForm>("e", "pos", "f");
                var b = r.Var<ValueSet<string>>("b");
                r.When(At.Of(e, pos), Fact.Exists(Agrees.Of(e, pos, f, Verdict.Equal)), Vacuous.Of(e, pos, f, Verdict.Equal).Is(false), b.Is(PartingBands.Of()));
                r.Where(RenderingPrimitives.Sendable.Of(f, b));
                r.Then(BestExact.Of(e, pos).Is(Compute.Of(f, Priced.Of)));
            });

            lift.DefineRule("an implied agreement that rests on something, may be sent and can be written as a Rex is implied", r =>
            {
                var (e, pos, f) = r.Vars<Pred, RenderPosition, ServiceForm>("e", "pos", "f");
                var (p, a) = r.Vars<JPath, PredicateAtom>("p", "a");
                var (b, g) = r.Vars<ValueSet<string>, RenderingRexGrammar.Value>("b", "g");
                r.When(At.Of(e, pos), Fact.Exists(Agrees.Of(e, pos, f, Verdict.Implies)), Vacuous.Of(e, pos, f, Verdict.Implies).Is(false), Ir.Atom.Of(e, p, a),
                    b.Is(PartingBands.Of()), g.Is(RexGrammar.Of()));
                r.Where(RenderingPrimitives.Sendable.Of(f, b));
                r.Where(RenderingPrimitives.RexWritable.Of(f, a, g));
                r.Then(Implied.Of(e, pos).Is(Primitives.Singleton<ServiceForm>().Of(f)));
            });

            // A computed place is absent where the service is undefined, which is Calcite's null only where its image holds:
            // over a key answered Undefined Calcite reads a value (LENGTH(true) against CHAR_LENGTH('true')), and no image.
            strict.DefineRule("a reading Calcite answers null over an absent path is strict in its path", r =>
            {
                var (v, p, k) = r.Vars<Val, JPath, string>("v", "p", "k");
                r.When(Ir.Reads.Of(v, p, k), StrictReader.Of(k), NullWhereAbsent.Of(p));
                r.Then(Strict.Of(v, p));
            });

            strict.DefineRule("a call whose operator answers null for a null operand is strict in what an operand is strict in", r =>
            {
                var (c, v, p) = r.Vars<Val, Val, JPath>("c", "v", "p");
                var (name, args) = r.Vars<string, Args>("name", "args");
                r.When(c.Is(Ir.Call.Of(name, args)), StrictOp.Of(name).Is(true), CallArg.Of(c, v), Strict.Of(v, p));
                r.Then(Strict.Of(c, p));
            });

            // A leaf is over the value it is, a comparison the call of its operator: null where the path is absent, so neither
            // TRUE nor FALSE there, it holds the path present wherever it is kept, at either position.
            strict.DefineRule("a predicate over a strict value implies its path present", r =>
            {
                var (e, pos, v, p) = r.Vars<Pred, RenderPosition, Val, JPath>("e", "pos", "v", "p");
                var n = r.Var<string>("n");
                r.When(At.Of(e, pos), Over.Of(e, v), Strict.Of(v, p), n.Is(Spelling.Of(p)));
                r.Then(Implied.Of(e, pos).Is(Primitives.Singleton<ServiceForm>().Of(Compute.Of(n, pos, ServiceForm.Presence))));
            });
        }

        // An exact form composes up from the arms' bests under the licence, held to exactly their join; a Min value only
        // lowers and priced-is-real only turns true, so whatever it composed from is exact. The weakening reads every best
        // and implied set complete: a leaf takes its best where that is a member, or what its members make together.
        void DefineChoice(Ruleset best, Ruleset weaken)
        {
            best.DefineRule("a junction's best is the join of its arms' bests", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (pos, j) = r.Vars<RenderPosition, CosmosServiceTest>("pos", "j");
                var (bx, by) = r.Vars<Priced, Priced>("bx", "by");
                var (l, m, h) = r.Vars<ServiceForm, ServiceForm, ServiceForm>("l", "m", "h");
                var (px, py) = r.Vars<Proof, Proof>("px", "py");
                r.When(At.Of(e, pos), First.Of(e, x), Junction.Of(e, x, y, j), bx.Is(BestExact.Of(x, pos)), by.Is(BestExact.Of(y, pos)));
                r.Where(RenderingPrimitives.PricedIsReal.Of(bx));
                r.Where(RenderingPrimitives.PricedIsReal.Of(by));
                r.When(l.Is(Compute.Of(bx, Priced.FormOf)), m.Is(Compute.Of(by, Priced.FormOf)), px.Is(Agrees.Of(x, pos, l, Verdict.Equal)), py.Is(Agrees.Of(y, pos, m, Verdict.Equal)),
                    h.Is(Compute.Of(j, l, m, ServiceForm.Joined)));
                r.Where(RenderingPrimitives.FormIsJoinOf.Of(h, l, m));
                Composes(r, j, null);
                r.Then(Agrees.Of(e, pos, h, Verdict.Equal).Is(Compute.Of(px, py, Proof.Join)), BestExact.Of(e, pos).Is(Compute.Of(h, Priced.Of)));
            });

            best.DefineRule("a negation's best is its operand's best at the opposite position, negated", r =>
            {
                var (e, a) = r.Vars<Pred, Pred>("e", "a");
                var (pos, q, bq) = r.Vars<RenderPosition, RenderPosition, Priced>("pos", "q", "bq");
                var (g, h, pa) = r.Vars<ServiceForm, ServiceForm, Proof>("g", "h", "pa");
                r.When(At.Of(e, pos), e.Is(Ir.Not.Of(a)), Opposite.Of(pos, q), bq.Is(BestExact.Of(a, q)));
                r.Where(RenderingPrimitives.PricedIsReal.Of(bq));
                r.When(g.Is(Compute.Of(bq, Priced.FormOf)), pa.Is(Agrees.Of(a, q, g, Verdict.Equal)), h.Is(Compute.Of(g, ServiceForm.Strip)));
                Composes(r, CosmosServiceTest.Not, null);
                r.Then(Agrees.Of(e, pos, h, Verdict.Equal).Is(pa), BestExact.Of(e, pos).Is(Compute.Of(h, Priced.Of)));
            });

            weaken.DefineRule("a leaf whose exact form can be written as a Rex weakens to it", r =>
            {
                var (e, pos) = r.Vars<Pred, RenderPosition>("e", "pos");
                var (b, s) = r.Vars<Priced, ValueSet<ServiceForm>>("b", "s");
                r.When(At.Of(e, pos), Leaf.Of(e), b.Is(BestExact.Of(e, pos)), s.Is(Implied.Of(e, pos)));
                r.Where(RenderingPrimitives.BestIsImplied.Of(b, s));
                r.Then(Weakening.Of(e, pos).Is(Compute.Of(b, Priced.FormOf)));
            });

            weaken.DefineRule("any other leaf weakens to what its implied forms make together", r =>
            {
                var (e, pos) = r.Vars<Pred, RenderPosition>("e", "pos");
                var (b, s) = r.Vars<Priced, ValueSet<ServiceForm>>("b", "s");
                r.When(At.Of(e, pos), Leaf.Of(e), b.Is(BestExact.Of(e, pos)), s.Is(Implied.Of(e, pos)));
                r.Where(RenderingPrimitives.BestIsNotImplied.Of(b, s));
                ComposesAll(r, ValueSet.Of(CosmosServiceTest.And, CosmosServiceTest.Or));
                r.Then(Weakening.Of(e, pos).Is(Compute.Of(s, pos, ServiceForm.Folded)));
            });

            weaken.DefineRule("a junction's weakening joins its arms' weakenings", r =>
            {
                var (e, x, y) = r.Vars<Pred, Pred, Pred>("e", "x", "y");
                var (pos, j) = r.Vars<RenderPosition, CosmosServiceTest>("pos", "j");
                var (wx, wy) = r.Vars<ServiceForm, ServiceForm>("wx", "wy");
                r.When(At.Of(e, pos), First.Of(e, x), Junction.Of(e, x, y, j), wx.Is(Weakening.Of(x, pos)), wy.Is(Weakening.Of(y, pos)));
                Composes(r, j, null);
                r.Then(Weakening.Of(e, pos).Is(Compute.Of(j, wx, wy, ServiceForm.Joined)));
            });

            weaken.DefineRule("a negation's weakening is its operand's at the opposite position, negated", r =>
            {
                var (e, a) = r.Vars<Pred, Pred>("e", "a");
                var (pos, q, w) = r.Vars<RenderPosition, RenderPosition, ServiceForm>("pos", "q", "w");
                r.When(At.Of(e, pos), e.Is(Ir.Not.Of(a)), Opposite.Of(pos, q), w.Is(Weakening.Of(a, q)));
                Composes(r, CosmosServiceTest.Not, null);
                r.Then(Weakening.Of(e, pos).Is(Compute.Of(w, ServiceForm.Negated)));
            });
        }

        // A tag changes which cells a leaf is read on, never what it reads: the tagged term is an atom of its leaf's path.
        // A computed value as a place (DESIGN.md, "A computed value is a place"): a call the rows name reads its own place,
        // as the bundle writes it; beneath each stratum its operands' imports are gathered, and once every one is in and every key they fall under
        // agrees on every target the set serves, the place's import there is the call's image, and an atom over it is
        // assumed as that. RenderingImage reads keys and evaluates; it decides nothing.
        void DefineValued(Ruleset disagree, Ruleset place, Ruleset image, Ruleset assume)
        {
            // A row covers, and disagrees on, each atom its class holds: a union, so a call's atom is the image rule's once
            // some row covers it and none disagrees, whether the row names a class or every value (F2).
            disagree.DefineRule("a function key some target measured covers each atom its class holds", r =>
            {
                var (key, x) = r.Vars<FunctionKey, RenderingTarget>("key", "x");
                var ans = r.Var<CosmosFunctionAnswer>("ans");
                var kd = r.Var<CosmosValueClass>("kd");
                var (c, a) = r.Vars<FunctionValueClass, FunctionValueClass>("c", "a");
                r.When(ans.Is(FunctionAnswer.Of(key, x)), kd.Is(Compute.Of(key, FunctionKey.KindOf)), c.Is(Compute.Of(key, FunctionKey.ValueOf)), ValueClassHolds.Of(kd, c, a));
                r.Then(MeasuredKeys.Of(Compute.Of(key, FunctionKey.FunctionOf)).Is(Primitives.Singleton<FunctionKey>().Of(Compute.Of(key, a, FunctionKey.WithValue))));
            });

            disagree.DefineRule("a function key a target of the set answers otherwise than Same disagrees there", r =>
            {
                var (key, x, st) = r.Vars<FunctionKey, RenderingTarget, RenderingSet>("key", "x", "st");
                var (ans, d) = r.Vars<CosmosFunctionAnswer, ValueSet<RenderingTarget>>("ans", "d");
                var kd = r.Var<CosmosValueClass>("kd");
                var (c, a) = r.Vars<FunctionValueClass, FunctionValueClass>("c", "a");
                r.When(ans.Is(FunctionAnswer.Of(key, x)), d.Is(Serves.Of(st)), kd.Is(Compute.Of(key, FunctionKey.KindOf)), c.Is(Compute.Of(key, FunctionKey.ValueOf)), ValueClassHolds.Of(kd, c, a));
                r.Where(Primitives.Contains<RenderingTarget>().Of(d, x));
                r.Where(RenderingPrimitives.IsNotSame.Of(ans));
                r.Then(DisagreeingKeys.Of(st, Compute.Of(key, FunctionKey.FunctionOf)).Is(Primitives.Singleton<FunctionKey>().Of(Compute.Of(key, a, FunctionKey.WithValue))));
            });

            place.DefineRule("a call's operands' imports are gathered beneath each stratum, one an ordinal", r =>
            {
                var (v, w) = r.Vars<Val, Val>("v", "w");
                var (q, p) = r.Vars<JPath, JPath>("q", "p");
                var (kq, k) = r.Vars<string, string>("kq", "k");
                var (i, s) = r.Vars<long, long>("i", "s");
                var (x, ch) = r.Vars<PathValues, ValueSet<string>>("x", "ch");
                r.When(Ir.Reads.Of(v, q, kq), q.Is(Valued.Of(v)), CallArgAt.Of(v, i, w), Ir.Reads.Of(w, p, k), x.Is(ArmImport.Of(s, p)), ch.Is(ImportedChars.Of(p)));
                r.Then(Gathered.Of(s, v).Is(Primitives.Singleton<PathImport>().Of(Compute.Of(i, k, x, ch, PathImport.Of))));
            });

            image.DefineRule("a place's import beneath a stratum is its call's image, once every operand is in and every key agrees", r =>
            {
                var (v, args) = r.Vars<Val, Args>("v", "args");
                var q = r.Var<JPath>("q");
                var (kq, n, t, kn) = r.Vars<string, string, string, string>("kq", "n", "t", "kn");
                var (s, m) = r.Vars<long, long>("s", "m");
                var g = r.Var<ValueSet<PathImport>>("g");
                var st = r.Var<RenderingSet>("st");
                var (z, d, ks) = r.Vars<ValueSet<FunctionKey>, ValueSet<FunctionKey>, ValueSet<FunctionKey>>("z", "d", "ks");
                var img = r.Var<PathValues>("img");
                r.When(Ir.Reads.Of(v, q, kq), q.Is(Valued.Of(v)), v.Is(Ir.Call.Of(n, args)), t.Is(SqlType.Of(v)), kn.Is(Compute.Of(n, t, RenderingImage.KeyFunction)),
                    g.Is(Gathered.Of(s, v)), m.Is(Arity.Of(v)), m.Is(Primitives.Count<PathImport>().Of(g)), Under.Of(st), z.Is(MeasuredKeys.Of(kn)), d.Is(DisagreeingKeys.Of(st, kn)));
                r.When(ks.Is(Compute.Of(kn, g, FunctionSubjects.Of(kn), MeasuredReaders.Of(kn), RenderingImage.Keys)));
                r.Where(Primitives.IsSubsetOf<FunctionKey>().Of(ks, z));
                r.Where(Primitives.IsEmpty<FunctionKey>().Of(Primitives.Intersect<FunctionKey>().Of(ks, d)));
                r.When(img.Is(Compute.Of(kn, t, g, RenderingImage.Of)));
                r.Then(ArmImport.Of(s, q).Is(img));
            });

            // A path's is the host's import already, written again equal; a computed place's and a pair's are this rule's, and
            // with it the absence the service holds where Calcite reads null, which a place has only where every key agrees.
            image.DefineRule("a place's import is its import beneath the root", r =>
            {
                var q = r.Var<JPath>("q");
                var x = r.Var<PathValues>("x");
                r.When(x.Is(ArmImport.Of(0L, q)));
                r.Then(Imported.Of(q).Is(x), NullWhereAbsent.Of(q));
            });

            assume.DefineRule("an atom over a computed place is assumed as its place's image beneath the atom's stratum", r =>
            {
                var e = r.Var<Pred>("e");
                var q = r.Var<JPath>("q");
                var a = r.Var<PredicateAtom>("a");
                var s = r.Var<long>("s");
                var x = r.Var<PathValues>("x");
                r.When(Ir.Atom.Of(e, q, a), s.Is(StratumId.Of(e)), x.Is(ArmImport.Of(s, q)));
                r.Then(Assumed.Of(e).Is(x));
            });
        }

        // Two readings compared (DESIGN.md, "A comparison of two readings"): an atom of their pair, whose import beneath a
        // stratum is the product of its places' there, each with its strings' classes. The bundle writes the pair's
        // spelling and characters; PathCell parts the product's cells by order.
        void DefinePair(Ruleset pair, Ruleset place)
        {
            pair.DefineRule("a comparison of two readings is an atom of their pair", r =>
            {
                var (e, v, w) = r.Vars<Pred, Val, Val>("e", "v", "w");
                var (p, q) = r.Vars<JPath, JPath>("p", "q");
                var (op, k, l) = r.Vars<string, string, string>("op", "k", "l");
                r.When(e.Is(Ir.Compare.Of(op, v, w)), Ir.Reads.Of(v, p, k), Ir.Reads.Of(w, q, l));
                r.Then(Ir.Atom.Of(e, Paired.Of(p, q), Compute.Of(k, op, l, PredicateAtom.CompareReadings)));
            });

            place.DefineRule("a pair's import beneath a stratum is the product of its places' imports there", r =>
            {
                var (n, p, q) = r.Vars<JPath, JPath, JPath>("n", "p", "q");
                var s = r.Var<long>("s");
                var (x, y) = r.Vars<PathValues, PathValues>("x", "y");
                var (kp, kq) = r.Vars<ValueSet<string>, ValueSet<string>>("kp", "kq");
                r.When(n.Is(Paired.Of(p, q)), x.Is(ArmImport.Of(s, p)), y.Is(ArmImport.Of(s, q)), kp.Is(ImportedChars.Of(p)), kq.Is(ImportedChars.Of(q)));
                r.Then(ArmImport.Of(s, n).Is(Compute.Of(x, kp, y, kq, PathValues.Product)));
            });
        }

        void DefineTag(Ruleset tag)
        {
            tag.DefineRule("a predicate beneath a stratum is an atom as its predicate is", r =>
            {
                var (e, x, s) = r.Vars<Pred, Pred, long>("e", "x", "s");
                var (p, a) = r.Vars<JPath, PredicateAtom>("p", "a");
                r.When(e.Is(Beneath.Of(s, x)), Ir.Atom.Of(x, p, a));
                r.Then(Ir.Atom.Of(e, p, a));
            });
        }

        // A case (DESIGN.md, "The rendering graph beyond the WHERE"): its condition stands at Pos, where it takes the
        // then-arm, and at Neg, which the opposite else reads; its arms where it stands. Each best reads the condition's
        // exact form and the arms' as complete, each a proof of its own cells; the else-arm's narrow copy only over a
        // condition two-valued on its cells, the wide copy always.
        void DefineCase(Ruleset at, Ruleset twovalued, Ruleset best, Ruleset weaken)
        {
            at.DefineRule("a case's condition stands at Pos and at Neg, wherever the case stands", r =>
            {
                var (e, c, a, bn) = r.Vars<Pred, Pred, Pred, Pred>("e", "c", "a", "bn");
                var bw = r.Var<Pred>("bw");
                var pos = r.Var<RenderPosition>("pos");
                r.When(At.Of(e, pos), e.Is(Chosen.Of(c, a, bn, bw)));
                r.Then(At.Of(c, RenderPosition.Pos), BestExact.Of(c, RenderPosition.Pos).Is(Priced.None), Implied.Of(c, RenderPosition.Pos).Is(ValueSet<ServiceForm>.Empty),
                    At.Of(c, RenderPosition.Neg), BestExact.Of(c, RenderPosition.Neg).Is(Priced.None), Implied.Of(c, RenderPosition.Neg).Is(ValueSet<ServiceForm>.Empty));
            });

            at.DefineRule("a case's arms stand where the case stands", r =>
            {
                var (e, c, a, bn) = r.Vars<Pred, Pred, Pred, Pred>("e", "c", "a", "bn");
                var bw = r.Var<Pred>("bw");
                var pos = r.Var<RenderPosition>("pos");
                r.When(At.Of(e, pos), e.Is(Chosen.Of(c, a, bn, bw)));
                r.Then(At.Of(a, pos), BestExact.Of(a, pos).Is(Priced.None), Implied.Of(a, pos).Is(ValueSet<ServiceForm>.Empty),
                    At.Of(bn, pos), BestExact.Of(bn, pos).Is(Priced.None), Implied.Of(bn, pos).Is(ValueSet<ServiceForm>.Empty),
                    At.Of(bw, pos), BestExact.Of(bw, pos).Is(Priced.None), Implied.Of(bw, pos).Is(ValueSet<ServiceForm>.Empty));
            });

            // Every cell of an atom is paired by any candidate, so one candidate's classes are the atom's; complete here.
            twovalued.DefineRule("an atom none of whose classes Calcite answers unknown on is two-valued", r =>
            {
                var (e, p, a, f) = r.Vars<Pred, JPath, PredicateAtom, ServiceForm>("e", "p", "a", "f");
                var (o, z) = r.Vars<CellOutcomes, ValueSet<CellClass>>("o", "z");
                r.When(Ir.Atom.Of(e, p, a), At.Of(e, RenderPosition.Pos), Candidate.Of(e, RenderPosition.Pos, f), o.Is(Outcomes.Of(e, f)), z.Is(Compute.Of(o, x => x.Classes)));
                r.Where(RenderingPrimitives.NoCalciteUnknown.Of(z));
                r.Then(TwoValued.Of(e));
            });

            // Where the condition is not TRUE its exact Pos form is not true either, so the junction leaves the row as the
            // absorbing else does, whatever the service answers there: no two-valuedness is read.
            best.DefineRule("a case whose else absorbs at its position is its condition joined with its then-arm", r =>
            {
                var (e, c, a, k) = r.Vars<Pred, Pred, Pred, Pred>("e", "c", "a", "k");
                var kw = r.Var<Pred>("kw");
                var (pos, kv) = r.Vars<RenderPosition, string>("pos", "kv");
                var (bc, ba) = r.Vars<Priced, Priced>("bc", "ba");
                var (cf, af, h) = r.Vars<ServiceForm, ServiceForm, ServiceForm>("cf", "af", "h");
                var (pc, pa) = r.Vars<Proof, Proof>("pc", "pa");
                r.When(At.Of(e, pos), e.Is(Chosen.Of(c, a, k, kw)), k.Is(Ir.Truth.Of(kv)), AbsorbingElse.Of(pos, kv), bc.Is(BestExact.Of(c, RenderPosition.Pos)), ba.Is(BestExact.Of(a, pos)));
                r.Where(RenderingPrimitives.PricedIsReal.Of(bc));
                r.Where(RenderingPrimitives.PricedIsReal.Of(ba));
                r.When(cf.Is(Compute.Of(bc, Priced.FormOf)), af.Is(Compute.Of(ba, Priced.FormOf)), pc.Is(Agrees.Of(c, RenderPosition.Pos, cf, Verdict.Equal)),
                    pa.Is(Agrees.Of(a, pos, af, Verdict.Equal)), h.Is(Compute.Of(pos, cf, af, ServiceForm.AbsorbingElse)));
                ComposesAll(r, ValueSet.Of(CosmosServiceTest.And, CosmosServiceTest.Or, CosmosServiceTest.Not));
                r.Then(Agrees.Of(e, pos, h, Verdict.Equal).Is(Compute.Of(pc, pa, Proof.Join)), BestExact.Of(e, pos).Is(Compute.Of(h, Priced.Of)));
            });

            // Where a two-valued condition is not TRUE it is FALSE, which one form exact at both positions says alike.
            best.DefineRule("a case whose else is the opposite constant, over a two-valued condition, is its condition's form joined with its then-arm", r =>
            {
                var (e, c, a, k) = r.Vars<Pred, Pred, Pred, Pred>("e", "c", "a", "k");
                var kw = r.Var<Pred>("kw");
                var (pos, kv) = r.Vars<RenderPosition, string>("pos", "kv");
                var (bp, bn, ba) = r.Vars<Priced, Priced, Priced>("bp", "bn", "ba");
                var (cf, af, h) = r.Vars<ServiceForm, ServiceForm, ServiceForm>("cf", "af", "h");
                var (pc, pn, pa) = r.Vars<Proof, Proof, Proof>("pc", "pn", "pa");
                r.When(At.Of(e, pos), e.Is(Chosen.Of(c, a, k, kw)), k.Is(Ir.Truth.Of(kv)), OppositeElse.Of(pos, kv), TwoValued.Of(c),
                    bp.Is(BestExact.Of(c, RenderPosition.Pos)), bn.Is(BestExact.Of(c, RenderPosition.Neg)), ba.Is(BestExact.Of(a, pos)));
                r.Where(RenderingPrimitives.PricedIsReal.Of(bp));
                r.Where(RenderingPrimitives.PricedIsReal.Of(ba));
                r.Where(RenderingPrimitives.SameForm.Of(bp, bn));
                r.When(cf.Is(Compute.Of(bp, Priced.FormOf)), af.Is(Compute.Of(ba, Priced.FormOf)), pc.Is(Agrees.Of(c, RenderPosition.Pos, cf, Verdict.Equal)),
                    pn.Is(Agrees.Of(c, RenderPosition.Neg, cf, Verdict.Equal)), pa.Is(Agrees.Of(a, pos, af, Verdict.Equal)), h.Is(Compute.Of(pos, cf, af, ServiceForm.OppositeElse)));
                ComposesAll(r, ValueSet.Of(CosmosServiceTest.And, CosmosServiceTest.Or, CosmosServiceTest.Not));
                r.Then(Agrees.Of(e, pos, h, Verdict.Equal).Is(Compute.Of(Compute.Of(pc, pn, Proof.Join), pa, Proof.Join)), BestExact.Of(e, pos).Is(Compute.Of(h, Priced.Of)));
            });

            // One template, written twice: the narrow else joined with the condition's two-valuedness, the wide else alone,
            // which is sound whatever came before; BestExact's lattice keeps the cheaper (spec h08).
            Ternary(best, narrow: true);
            Ternary(best, narrow: false);

            // A truth value is no atom, and no cell is read for it: on both service targets a constant is its own truth
            // wherever the set measured it so (Service.AConstantIsItsOwnTruth, the Constant licence).
            best.DefineRule("a truth value is exact as the constant its position reads it as", r =>
            {
                var (e, pos, kv, h) = r.Vars<Pred, RenderPosition, string, ServiceForm>("e", "pos", "kv", "h");
                var (sk, lk) = r.Vars<RenderingSet, ValueSet<CosmosServiceTest>>("sk", "lk");
                r.When(At.Of(e, pos), e.Is(Ir.Truth.Of(kv)), Under.Of(sk), lk.Is(Licensed.Of(sk)), h.Is(Compute.Of(pos, kv, ServiceForm.TruthAt)));
                r.Where(Primitives.Contains<CosmosServiceTest>().Of(lk, CosmosServiceTest.Constant));
                r.Where(Primitives.Contains<string>().Of(MeasuredTruths, kv));
                r.Then(Agrees.Of(e, pos, h, Verdict.Equal).Is(ConstantProof), BestExact.Of(e, pos).Is(Compute.Of(h, Priced.Of)),
                    Implied.Of(e, pos).Is(Primitives.Singleton<ServiceForm>().Of(h)));
            });

            // The wide else alone, which is sound beneath any condition.
            weaken.DefineRule("a case weakens to its arms' weakenings under its condition's", r =>
            {
                var (e, c, a, bn) = r.Vars<Pred, Pred, Pred, Pred>("e", "c", "a", "bn");
                var bw = r.Var<Pred>("bw");
                var pos = r.Var<RenderPosition>("pos");
                var (wc, wa, wb) = r.Vars<ServiceForm, ServiceForm, ServiceForm>("wc", "wa", "wb");
                r.When(At.Of(e, pos), e.Is(Chosen.Of(c, a, bn, bw)), wc.Is(Weakening.Of(c, RenderPosition.Pos)), wa.Is(Weakening.Of(a, pos)), wb.Is(Weakening.Of(bw, pos)));
                ComposesAll(r, ValueSet.Of(CosmosServiceTest.And, CosmosServiceTest.Or));
                r.Then(Weakening.Of(e, pos).Is(Compute.Of(pos, wc, wa, wb, RenderingWeakening.OfCase)));
            });
        }

        /// <summary>
        /// The service ternary of the condition's exact Pos form and the arms' exact forms, where the set and Calcite both
        /// license it: the then-arm is read beneath the condition, and the else-arm narrow over a two-valued condition or wide.
        /// </summary>
        void Ternary(Ruleset best, bool narrow)
        {
            best.DefineRule(narrow ? "a case's best is the service ternary of its condition's Pos best and its arms' bests, the narrow else over a two-valued condition"
                : "a case's best is the service ternary of its condition's Pos best and its arms' bests, the wide else", r =>
            {
                var (e, c, a, bn) = r.Vars<Pred, Pred, Pred, Pred>("e", "c", "a", "bn");
                var bw = r.Var<Pred>("bw");
                var pos = r.Var<RenderPosition>("pos");
                var (bc, ba, bb) = r.Vars<Priced, Priced, Priced>("bc", "ba", "bb");
                var (cf, af, bf, h) = r.Vars<ServiceForm, ServiceForm, ServiceForm, ServiceForm>("cf", "af", "bf", "h");
                var (pc, px, py) = r.Vars<Proof, Proof, Proof>("pc", "px", "py");
                var b = narrow ? bn : bw;
                r.When(At.Of(e, pos), e.Is(Chosen.Of(c, a, bn, bw)), bc.Is(BestExact.Of(c, RenderPosition.Pos)), ba.Is(BestExact.Of(a, pos)), bb.Is(BestExact.Of(b, pos)));
                if (narrow)
                    r.When(TwoValued.Of(c));

                r.Where(RenderingPrimitives.PricedIsReal.Of(bc));
                r.Where(RenderingPrimitives.PricedIsReal.Of(ba));
                r.Where(RenderingPrimitives.PricedIsReal.Of(bb));
                r.When(cf.Is(Compute.Of(bc, Priced.FormOf)), af.Is(Compute.Of(ba, Priced.FormOf)), bf.Is(Compute.Of(bb, Priced.FormOf)),
                    pc.Is(Agrees.Of(c, RenderPosition.Pos, cf, Verdict.Equal)), px.Is(Agrees.Of(a, pos, af, Verdict.Equal)), py.Is(Agrees.Of(b, pos, bf, Verdict.Equal)),
                    h.Is(Compute.Of(cf, af, bf, ServiceForm.ChosenOf)));
                Composes(r, CosmosServiceTest.Choose, null);
                r.Then(Agrees.Of(e, pos, h, Verdict.Equal).Is(Compute.Of(Compute.Of(pc, px, Proof.Join), py, Proof.Join)), BestExact.Of(e, pos).Is(Compute.Of(h, Priced.Of)));
            });
        }

        /// <summary>The key of the order strings are compared by, whose comparators the parted rules read the parts of.</summary>
        static readonly MeasuredKey StringOrder = MeasuredKey.Of(CosmosServiceTest.Order, CosmosValueClass.String, CosmosValueClass.None);

        /// <summary>What a truth value's constant rests on: the row that measured each constant its own truth, on no cell.</summary>
        static readonly Proof ConstantProof = new(ValueSet.Of("Service.AConstantIsItsOwnTruth"), 0);

        /// <summary>
        /// The truth values <c>Service.AConstantIsItsOwnTruth</c> measured: UNKNOWN is none, so a missing else is read by
        /// the absorbing rule alone, whose table holds it at both positions.
        /// </summary>
        static readonly ValueSet<string> MeasuredTruths = ValueSet.Of("true", "false");

        /// <summary>
        /// Holds a step to a connective the set under which the bundle runs licenses and Calcite does, and a junction's
        /// form to a set that licenses whatever it needs.
        /// </summary>
        void Composes(RuleBuilder r, Term<CosmosServiceTest> connective, Var<ServiceForm>? junction)
        {
            var s = r.Var<RenderingSet>("s");
            var (lic, cal) = r.Vars<ValueSet<CosmosServiceTest>, ValueSet<CosmosServiceTest>>("lic", "cal");
            r.When(Under.Of(s), lic.Is(Licensed.Of(s)), cal.Is(Licensed.Of(RenderingSet.Calcite)));
            r.Where(Primitives.Contains<CosmosServiceTest>().Of(lic, connective));
            r.Where(Primitives.Contains<CosmosServiceTest>().Of(cal, connective));
            if (junction is null)
                return;

            var nd = r.Var<ValueSet<CosmosServiceTest>>("nd");
            r.When(nd.Is(Needs.Of(junction)));
            r.Where(Primitives.IsSubsetOf<CosmosServiceTest>().Of(nd, lic));
        }

        /// <summary>
        /// The bands a form's literal holds characters of, none where it holds no literal: total, as a body's calls must be,
        /// since Eggnog 1.1.1 may run it before the filter beside it that admits only string comparisons.
        /// </summary>
        static ValueSet<string> CharactersOfLiteral(ServiceForm form) => form.Literal is { } literal ? JsonValues.CharactersOf(literal) : JsonValues.NoBand;

        /// <summary>
        /// Holds a step to every one of the connectives, which the set and Calcite both license: <see cref="Composes"/>'s
        /// reads under names of its own, so the two may stand in one rule.
        /// </summary>
        void ComposesAll(RuleBuilder r, ValueSet<CosmosServiceTest> connectives)
        {
            var sa = r.Var<RenderingSet>("sa");
            var (lica, cala) = r.Vars<ValueSet<CosmosServiceTest>, ValueSet<CosmosServiceTest>>("lica", "cala");
            r.When(Under.Of(sa), lica.Is(Licensed.Of(sa)), cala.Is(Licensed.Of(RenderingSet.Calcite)));
            r.Where(Primitives.IsSubsetOf<CosmosServiceTest>().Of(connectives, lica));
            r.Where(Primitives.IsSubsetOf<CosmosServiceTest>().Of(connectives, cala));
        }

        /// <summary>
        /// Writes each axiom's rows as one batch, so a contradiction refuses that axiom whole and names it, then the host
        /// rows; settles them, and freezes the theory.
        /// </summary>
        void Load(RenderingDefinition definition)
        {
            foreach (var axiom in definition.Rows.GroupBy(row => row.AxiomId))
            {
                try
                {
                    Theory.Write(w =>
                    {
                        foreach (var row in axiom)
                            w.Assume(Outcome.Of(row.Key, row.Target).Is(row.Answer), Cites.Of(row.Key, row.Target).Is(ValueSet.Of(axiom.Key)));
                    });
                }
                catch (FactConflictException e)
                {
                    e.Data["axiom"] = axiom.Key;
                    e.Data["faulted"] = Theory.IsFaulted;
                    throw;
                }
            }

            Theory.Write(w =>
            {
                // Every set's licence starts empty, so a rule may read a set that licenses nothing.
                foreach (var (set, targets) in definition.Serves)
                    w.Assume(Serves.Of(set).Is(targets), Licensed.Of(set).Is(ValueSet<CosmosServiceTest>.Empty));

                foreach (var k in AgreementRows.Classes)
                    w.Assume(Class.Of(k));

                foreach (var (pos, v, b) in definition.Allowing)
                    w.Assume(Allowing.Of(pos, v, b));

                foreach (var (pos, v, b) in definition.Refuting)
                    w.Assume(Refuting.Of(pos, v, b));

                foreach (var (reader, edges) in definition.Edges)
                    w.Assume(Edges.Of(reader).Is(edges));

                foreach (var (c, u, v) in definition.Parts)
                    w.Assume(Parts.Of(c, u, v));

                // The foundation's rows (F3), each as its row states it.
                foreach (var (c, band) in definition.Moves)
                    w.Assume(Moves.Of(c, band));

                foreach (var (target, written, stored) in definition.StoredAs)
                    w.Assume(StoredAs.Of(target, written, stored));

                foreach (var (target, test, shape, band) in definition.Truncations)
                    w.Assume(Truncates.Of(target, test, shape, band));

                foreach (var (test, swapped, reads) in definition.PairReadings)
                    w.Assume(PairReads.Of(test, swapped, reads));

                foreach (var (kind, box) in definition.Boxes)
                    w.Assume(BoxOf.Of(kind, box));

                foreach (var op in definition.SeesThrough)
                    w.Assume(SeesThrough.Of(op));

                foreach (var function in definition.StructureOnly)
                    w.Assume(StructureOnly.Of(function));

                foreach (var (reader, kind, answer) in definition.CalciteReadings)
                    w.Assume(CalciteReads.Of(reader, kind, answer));

                foreach (var (reader, kind, band) in definition.CalciteBands)
                    w.Assume(CalciteBands.Of(reader, kind).Is(ValueSet.Of(band)));

                foreach (var (target, reader, row) in definition.Casts)
                    w.Assume(MeasuredCast.Of(target, reader, row));

                foreach (var (reader, moved, row) in definition.Comparands)
                    w.Assume(TakesConstant.Of(reader, moved, row));

                foreach (var (reader, claim) in definition.ReaderOrders)
                    w.Assume(ReaderOrder.Of(reader).Is(ValueSet.Of(claim)));

                foreach (var (test, kind) in definition.TestKinds)
                    w.Assume(TestKinds.Of(test).Is(ValueSet.Of(kind)));

                foreach (var (target, key, answer) in definition.Kleene)
                    w.Assume(Kleene.Of(key, target).Is(answer));

                foreach (var (connective, classes) in definition.Identity)
                    w.Assume(Identity.Of(connective).Is(classes));

                foreach (var (pos, q) in KleeneTables.Opposite)
                    w.Assume(Opposite.Of(pos, q));

                foreach (var (pos, truth) in KleeneTables.AbsorbingElse)
                    w.Assume(AbsorbingElse.Of(pos, truth));

                foreach (var (pos, truth) in KleeneTables.OppositeElse)
                    w.Assume(OppositeElse.Of(pos, truth));

                foreach (var (id, pos) in definition.Templates)
                    w.Assume(Template.Of(id, pos));

                foreach (var (id, shape) in definition.Fits)
                    w.Assume(Fits.Of(id, shape));

                foreach (var (name, strict) in definition.StrictOperators)
                    w.Assume(StrictOp.Of(name).Is(strict));

                foreach (var reader in definition.StrictReaders)
                    w.Assume(StrictReader.Of(reader));

                // The function rows, total over the targets: a key one target measured is unmeasured on the rest, and read as
                // anything there; which atoms each covers is the disagree stratum's. Each set's disagreeing keys start empty,
                // so the image rule reads a function none disagrees on.
                var keys = definition.Functions.Select(f => f.Key).Distinct().ToList();
                var answered = definition.Functions.ToDictionary(f => (f.Key, f.Target), f => f.Answer);
                foreach (var key in keys)
                    foreach (var target in Enum.GetValues<RenderingTarget>())
                        w.Assume(FunctionAnswer.Of(key, target).Is(answered.TryGetValue((key, target), out var answer) ? answer : CosmosFunctionAnswer.Unmeasured));

                foreach (var (kind, @class, atom) in FunctionValueClasses.Holding)
                    w.Assume(ValueClassHolds.Of(kind, @class, atom));

                foreach (var (function, subject) in definition.FunctionSubjects)
                    w.Assume(FunctionSubjects.Of(function).Is(ValueSet.Of(subject)));

                foreach (var (function, readers) in definition.FunctionReads)
                    w.Assume(MeasuredReaders.Of(function).Is(ValueSet.From(readers)));

                foreach (var set in definition.Serves.Keys)
                    foreach (var function in keys.Select(k => k.Function).Distinct())
                        w.Assume(DisagreeingKeys.Of(set, function).Is(ValueSet<FunctionKey>.Empty));

                // The union starts empty, so an atom is paired on its cells where no column is named.
                foreach (var (key, column, truths) in definition.Columns)
                    w.Assume(RawColumn.Of(key, column).Is(truths));

                w.Assume(RawUnion.Of().Is(RawTruths.Empty));

                w.Assume(Means.Of().Is(definition.Means), Placed.Of().Is(ValueSet<CosmosValueClass>.Empty), RaiseOnly.Of().Is(ValueSet<CellClass>.Empty), Tolerated.Of().Is(definition.Tolerated),
                    PartingBands.Of().Is(definition.PartingBands), RexGrammar.Of().Is(RenderingRexGrammar.Written));
            });

            var report = Theory.Run(Settle);
            if (report.StopReason != RunStopReason.Completed)
                throw new InvalidOperationException($"The rendering theory stopped {report.StopReason} after {report.Iterations} iterations.");

            Theory.Freeze();
        }

    }

}
