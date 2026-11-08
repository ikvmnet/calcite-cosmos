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
            // The classes each type test is true of (G7), the vocabulary's to read: held as an analysis, rows at the root.
            TestKinds = Graph.DefineFunction<string, ValueSet<string>>("testkinds", ValueLattice.Union<string>(), options: new TableOptions { Role = TableRole.Analysis });
            Ir = new PredicateVocabulary(Graph, TestKinds);
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
            Rewrites = Graph.DefineRelation<CosmosAnswer, string>("rewrites");
            Unsent = Graph.DefineRelation<RenderingTarget, string>("unsent");
            Rewriting = Graph.DefineFunction<ValueSet<CosmosAnswer>>("rewriting", ValueLattice.Union<CosmosAnswer>());
            RewrittenBy = Graph.DefineFunction<CosmosAnswer, ValueSet<string>>("rewrittenby", ValueLattice.Union<string>());
            Sends = Graph.DefineFunction<RenderingSet, SentSpelling>("sends");
            Respelled = Graph.DefineFunction<RenderingSet, ValueSet<string>>("respelled", ValueLattice.Union<string>());
            Unsendable = Graph.DefineFunction<RenderingSet, ValueSet<string>>("unsendable", ValueLattice.Union<string>());
            PairReads = Graph.DefineRelation<CosmosServiceTest, bool, CosmosServiceTest>("pairreads");
            BoxOf = Graph.DefineRelation<string, string>("boxof");
            SeesThrough = Graph.DefineRelation<string>("seesthrough");
            StructureOnly = Graph.DefineRelation<string>("structureonly");
            // What Calcite makes of each reading, the facts graph's vocabulary too (G7).
            Calcite = new ReadingVocabulary(Graph, TestKinds);
            CalciteReads = Calcite.Reads;
            CalciteBands = Calcite.Bands;
            MeasuredCast = Graph.DefineRelation<string, string, string>("measuredcast");
            // What a reader compares with a number Calcite wrote cast (G8): rows at the frozen root, read beside the terms by
            // the rule that makes the comparison an atom, which may read only terms and analyses, so held as an analysis.
            TakesConstant = Graph.DefineRelation<string, bool, string>("takesconstant", new TableOptions { Role = TableRole.Analysis });
            // What each reading's order holds and the kind it compares as (G3): rows at the frozen root, read beside the terms
            // by the rule that makes two readings compared an atom, so held as analyses, as takesconstant is.
            ReaderOrder = Graph.DefineFunction<string, ValueSet<string>>("readerorder", ValueLattice.Union<string>(), options: new TableOptions { Role = TableRole.Analysis });
            ReaderKind = Graph.DefineRelation<string, string>("readerkind", new TableOptions { Role = TableRole.Analysis });
            Kleene = Graph.DefineFunction<MeasuredKey, RenderingTarget, CosmosAnswer>("kleene");
            Expected = Graph.DefineFunction<CosmosServiceTest, RenderingSet, ValueSet<MeasuredKey>>("expected", ValueLattice.Union<MeasuredKey>());
            Matched = Graph.DefineFunction<CosmosServiceTest, RenderingSet, ValueSet<MeasuredKey>>("matched", ValueLattice.Union<MeasuredKey>());
            Licensed = Graph.DefineFunction<RenderingSet, ValueSet<CosmosServiceTest>>("licensed", ValueLattice.Union<CosmosServiceTest>());
            Identity = Graph.DefineFunction<CosmosServiceTest, ValueSet<CellClass>>("identity");
            Opposite = Graph.DefineRelation<RenderPosition, RenderPosition>("opposite");
            ReadBy = Graph.DefineFunction<CosmosServiceTest, CosmosValueClass, ValueSet<CosmosAnswer>>("readby", ValueLattice.Union<CosmosAnswer>());
            Evaluates = Graph.DefineFunction<CosmosServiceTest, CosmosValueClass, ValueSet<CosmosAnswer>>("evaluates", ValueLattice.Union<CosmosAnswer>());
            WholeReads = Graph.DefineRelation<CosmosAnswer, string>("wholereads");
            Readers = Graph.DefineFunction<MeasuredKey, RenderingSet, ValueSet<CosmosAnswer>>("readers", ValueLattice.Union<CosmosAnswer>());
            Readings = Graph.DefineFunction<MeasuredKey, RenderingSet, ValueSet<(CosmosAnswer, string)>>("readings", ValueLattice.Union<(CosmosAnswer, string)>());
            StandsFor = Graph.DefineRelation<MeasuredKey, MeasuredKey>("standsfor");
            Stands = Graph.DefineFunction<MeasuredKey, ValueSet<MeasuredKey>>("stands", ValueLattice.Union<MeasuredKey>());
            FailingAnswers = Graph.DefineFunction<MeasuredKey, RenderingSet, ValueSet<CosmosAnswer>>("failinganswers", ValueLattice.Union<CosmosAnswer>());
            FailingCovers = Graph.DefineFunction<MeasuredKey, RenderingSet, ValueSet<MeasuredKey>>("failingcovers", ValueLattice.Union<MeasuredKey>());
            FailingEvidence = Graph.DefineFunction<MeasuredKey, RenderingSet, ValueSet<string>>("failingevidence", ValueLattice.Union<string>());
            Kind = Graph.DefineRelation<CosmosValueClass>("kind");
            PairAnswers = Graph.DefineFunction<MeasuredKey, RenderingSet, ValueSet<CosmosAnswer>>("pairanswers", ValueLattice.Union<CosmosAnswer>());
            PairEvidence = Graph.DefineFunction<MeasuredKey, RenderingSet, ValueSet<string>>("pairevidence", ValueLattice.Union<string>());
            PairTable = Graph.DefineFunction<RenderingSet, ValueSet<MeasuredEntry>>("pairtable", ValueLattice.Union<MeasuredEntry>());
            MeasuredCites = Graph.DefineFunction<CosmosServiceTest, CosmosValueClass, RenderingSet, ValueSet<string>>("measuredcites", ValueLattice.Union<string>());
            Units = Graph.DefineRelation<CosmosServiceTest, bool, bool>("units");
            Constants = Graph.DefineRelation<CosmosValueClass, bool>("constants");
            View = Graph.DefineFunction<RenderingSet, ServiceTable>("view");
            ReadAs = Graph.DefineFunction<CellClass, CellClass>("readas");
            IdentityClasses = Graph.DefineFunction<CosmosServiceTest, ValueSet<CellClass>>("identityclasses", ValueLattice.Union<CellClass>());
            UnknownClasses = Graph.DefineFunction<ValueSet<CellClass>>("unknownclasses", ValueLattice.Union<CellClass>());

            Spelling = Graph.DefineFunction<JPath, string>("spelling");
            Conjunct = Graph.DefineRelation<ServiceForm, ServiceForm>("conjunct");
            Under = Graph.DefineRelation<RenderingSet>("under");
            Imported = Graph.DefineFunction<JPath, PathValues>("imported", new ValueLattice<PathValues>("joined imports", PathValues.Join));
            Assumed = Graph.DefineFunction<Pred, PathValues>("assumed");
            ImportedChars = Graph.DefineFunction<JPath, ValueSet<string>>("importedchars");
            PartedLiterals = Graph.DefineFunction<JPath, ValueSet<string>>("partedliterals", ValueLattice.Union<string>());
            UnplacedLiterals = Graph.DefineFunction<ServiceForm, ValueSet<string>>("unplacedliterals", ValueLattice.Union<string>());
            Unkept = Graph.DefineFunction<ServiceForm, ValueSet<string>>("unkept", ValueLattice.Union<string>());
            Unread = Graph.DefineFunction<ServiceForm, ValueSet<string>>("unread", ValueLattice.Union<string>());
            Uncut = Graph.DefineFunction<ServiceForm, ValueSet<string>>("uncut", ValueLattice.Union<string>());
            Blind = Graph.DefineFunction<ServiceForm, ValueSet<string>>("blind", ValueLattice.Union<string>());
            Departs = Graph.DefineFunction<ServiceForm, ValueSet<(string, string)>>("departs", ValueLattice.Union<(string, string)>());
            Moved = Graph.DefineFunction<ServiceForm, ValueSet<string>>("moved", ValueLattice.Union<string>());
            LicenceRows = Graph.DefineFunction<ServiceForm, ValueSet<string>>("licencerows", ValueLattice.Union<string>());
            NameChars = Graph.DefineFunction<JPath, ValueSet<string>>("namechars");
            Writable = Graph.DefineRelation<JPath>("writable");
            Candidate = Graph.DefineRelation<Pred, RenderPosition, ServiceForm>("candidate");
            Member = Graph.DefineRelation<ServiceForm, ServiceForm>("member");
            Partition = Graph.DefineFunction<Pred, ServiceForm, CellPartition>("partition");
            Need = Graph.DefineRelation<CellPartition, ServiceForm>("need");
            Svc = Graph.DefineFunction<CellPartition, ServiceForm, OutcomeVector>("svc");
            Outcomes = Graph.DefineFunction<Pred, ServiceForm, CellOutcomes>("outcomes");
            Rests = Graph.DefineFunction<Pred, ServiceForm, ValueSet<string>>("rests", ValueLattice.Union<string>());
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
            PartingBands = Graph.DefineFunction<RenderingSet, ValueSet<string>>("partingbands", ValueLattice.Union<string>());
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
            AbsorbingElse = Graph.DefineRelation<RenderPosition, string, CosmosServiceTest, bool>("absorbingelse");
            OppositeElse = Graph.DefineRelation<RenderPosition, string, CosmosServiceTest, bool>("oppositeelse");

            // A computed value's place (h10), S's alone; the tables the writer and the bundle fill for it, its reading among
            // them (the loader types a call the rows name, and its type is the reader); and the function rows, raw at the
            // frozen root.
            Valued = Graph.DefineConstructor<Val, JPath>("valued", options: term);
            Paired = Graph.DefineConstructor<JPath, JPath, JPath>("paired", options: term);
            SqlType = Graph.DefineFunction<Val, string>("sqltype");
            Arity = Graph.DefineFunction<Val, long>("arity");
            CallArgAt = Graph.DefineRelation<Val, long, Val>("callargat");
            ArmImport = Graph.DefineFunction<long, JPath, PathValues>("armimport");
            ArmChars = Graph.DefineFunction<long, JPath, ValueSet<string>>("armchars");
            StratumId = Graph.DefineFunction<Pred, long>("stratumid");
            Gathered = Graph.DefineFunction<long, Val, ValueSet<PathImport>>("gathered", ValueLattice.Union<PathImport>());
            FunctionAnswer = Graph.DefineFunction<FunctionKey, RenderingTarget, CosmosFunctionAnswer>("functionanswer");
            MeasuredKeys = Graph.DefineFunction<string, ValueSet<FunctionKey>>("measuredkeys", ValueLattice.Union<FunctionKey>());
            FunctionSubjects = Graph.DefineFunction<string, ValueSet<long>>("functionsubjects", ValueLattice.Union<long>());
            MeasuredReaders = Graph.DefineFunction<string, ValueSet<string>>("measuredreaders", ValueLattice.Intersect<string>());
            ValueClassHolds = Graph.DefineRelation<CosmosValueClass, FunctionValueClass, FunctionValueClass>("valueclassholds");
            DisagreeingKeys = Graph.DefineFunction<RenderingSet, string, ValueSet<FunctionKey>>("disagreeingkeys", ValueLattice.Union<FunctionKey>());
            RowName = Graph.DefineFunction<string, string, string>("rowname");
            RowNamedAlone = Graph.DefineRelation<string>("rownamedalone");
            CallKey = Graph.DefineFunction<Val, string>("callkey");
            FunctionYield = Graph.DefineFunction<FunctionKey, CalciteYield>("functionyield");
            YieldKeys = Graph.DefineFunction<string, CalciteYield, ValueSet<FunctionKey>>("yieldkeys", ValueLattice.Union<FunctionKey>());
            DefinedKeys = Graph.DefineFunction<string, ValueSet<FunctionKey>>("definedkeys", ValueLattice.Union<FunctionKey>());
            TypeKinds = Graph.DefineFunction<string, ValueSet<string>>("typekinds", ValueLattice.Union<string>());
            LiteralReaders = Graph.DefineFunction<ValueSet<string>>("literalreaders", ValueLattice.Union<string>());
            HeldAbsent = Graph.DefineFunction<Val, ValueSet<string>>("heldabsent", ValueLattice.Union<string>());
            SpelledAlways = Graph.DefineRelation<string, long>("spelledalways");
            SpelledWhere = Graph.DefineRelation<string, long, long, FunctionValueClass>("spelledwhere");
            CallSpelling = Graph.DefineFunction<Val, long>("callspelling", ValueLattice.Min<long>());
            FoldsAs = Graph.DefineRelation<RenderingTarget, CosmosServiceTest, string, string>("foldsas");
            FoldTargets = Graph.DefineFunction<RenderingSet, CosmosServiceTest, string, ValueSet<RenderingTarget>>("foldtargets", ValueLattice.Union<RenderingTarget>());
            FoldParts = Graph.DefineFunction<RenderingSet, CosmosServiceTest, string, ValueSet<string>>("foldparts", ValueLattice.Union<string>());
            Folds = Graph.DefineFunction<RenderingSet, CosmosServiceTest, string, ValueSet<string>>("folds");
            FoldedAs = Graph.DefineRelation<Pred, CosmosServiceTest, string, long>("foldedas");
            // What each target's LIKE reads each unit of its pattern as, and a set's, settled where every target it serves reads it so (G7).
            LikeReads = Graph.DefineRelation<RenderingTarget, LikeUnit, string>("likereads");
            LikeTargets = Graph.DefineFunction<RenderingSet, LikeUnit, string, ValueSet<RenderingTarget>>("liketargets", ValueLattice.Union<RenderingTarget>());
            LikesOf = Graph.DefineFunction<RenderingSet, ValueSet<(LikeUnit, string)>>("likesof", ValueLattice.Union<(LikeUnit, string)>());

            // The raw columns as their rows state them, one fact a field, raw at the frozen root; what a test answers of a value
            // read as null or raised on, and each class's JSON type; each column's answer on a key, the rules', and their union,
            // which Eggnog's join takes (h12, G5).
            RawReads = Graph.DefineFunction<string, string, CosmosValueClass, string>("rawreads");
            RawBeside = Graph.DefineRelation<string, string, CosmosValueClass>("rawbeside");
            RawCompiles = Graph.DefineFunction<string, string, CosmosServiceTest, bool>("rawcompiles");
            RawAsType = Graph.DefineFunction<string, string, bool>("rawastype");
            RawAcross = Graph.DefineFunction<string, string, CosmosServiceTest, Truths>("rawacross");
            ReadTruths = Calcite.ReadTruths;
            ClassType = Graph.DefineFunction<CosmosValueClass, string>("classtype");
            RawColumn = Graph.DefineFunction<RawKey, string, RawAnswer>("rawcolumn");
            RawUnion = Graph.DefineFunction<RawTruths>("rawunion", RawTruths.Union);
            // Which raw column refines the text over every class the text reads, and so reads as it over a function's rows (G5).
            ReadsClasses = Graph.DefineFunction<string, ValueSet<CosmosValueClass>>("readsclasses", ValueLattice.Union<CosmosValueClass>());
            RefinesText = Graph.DefineFunction<string, ValueSet<CosmosValueClass>>("refinestext", ValueLattice.Union<CosmosValueClass>());
            FunctionReaders = Graph.DefineFunction<string, ValueSet<string>>("functionreaders", ValueLattice.Union<string>());
            // Every reading's rows as one value, which Calcite's side of a cell reads (G7).
            ReaderRows = Calcite.ReaderRows;

            var measure = Graph.DefineRuleset("measure");
            var allowed = Graph.DefineRuleset("allowed");
            var refutes = Graph.DefineRuleset("refutes");
            var tabulate = Graph.DefineRuleset("tabulate");
            var expected = Graph.DefineRuleset("expected");
            var matched = Graph.DefineRuleset("matched");
            var licence = Graph.DefineRuleset("licence");
            var disagree = Graph.DefineRuleset("disagree");
            var raw = Graph.DefineRuleset("raw");
            var readas = Graph.DefineRuleset("readas");
            var reads = Graph.DefineRuleset("reads");
            var roles = Graph.DefineRuleset("roles");
            var failing = Graph.DefineRuleset("failing");
            var pairs = Graph.DefineRuleset("pairs");
            var entries = Graph.DefineRuleset("entries");
            var view = Graph.DefineRuleset("view");
            DefineTheory(measure, allowed, refutes, tabulate);
            DefineRoles(roles, entries);
            DefineFailing(failing, entries);
            DefinePairs(pairs, entries);
            DefineView(view);
            DefineLicences(expected, matched, licence);
            var sets = Graph.DefineRuleset("sets");
            DefineSets(tabulate, sets);
            DefineRaw(raw, readas);
            DefineReadings(reads);

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

            // The IR's readings, run with its analysis: a comparison of two readings as its pair's atom, and a call as the
            // name its rows are filed under (G6), each complete before any place is gathered. One ruleset, as a ruleset's
            // every round in a saturation counts against a run's iterations.
            var pair = Graph.DefineRuleset("pair");
            var place = Graph.DefineRuleset("place");
            var image = Graph.DefineRuleset("image");
            var assume = Graph.DefineRuleset("assume");
            DefineValued(disagree, pair, place, image, assume);
            DefinePair(pair, place);
            var fold = Graph.DefineRuleset("fold");
            DefineFolds(measure, tabulate, fold);
            DefineLikes(measure, tabulate);
            // A nested call is gathered once its inner place is written, each nesting level one pass of the three strata, so S
            // images a call PlaceDepth deep and no deeper (G6): one nested past it gets no image, and only its own conjunct is
            // unproven, the run completing within its budget (RenderingBundle.PlaceIterations), where the loader cut it before.
            var places = Schedule.Repeat(PlaceDepth, Schedule.Sequence(Schedule.Saturate(place), Schedule.Saturate(image), Schedule.Saturate(assume)));

            // What reads a key, a failing side and a pair read Final complete; an entry reads all three complete (G2-G4).
            Settle = Schedule.Sequence(Schedule.Saturate(measure, allowed, refutes, expected, disagree, raw, reads), Schedule.Saturate(tabulate, readas), Schedule.Saturate(roles, failing, pairs),
                Schedule.Saturate(entries), Schedule.Saturate(view), Schedule.Saturate(matched), Schedule.Saturate(licence), Schedule.Saturate(sets));
            // The gate reads the rows a candidate's classes rest on complete (G2). Two-valuedness reads the gate's classes
            // complete, beside the lift; the case's best reads it complete.
            Check = Schedule.Sequence(Schedule.Saturate(Ir.Analyse, pair, tag, structure, descend, disjoint), places, Schedule.Saturate(partition, need, parted),
                Schedule.Saturate(evaluate), Schedule.Saturate(classes), Schedule.Saturate(gate), Schedule.Saturate(compose, conflict));
            // A LIKE over a fold reads its place's image complete (G6), and only the translator's bridge asks it: in a stage
            // of check mode the ruleset cost every check a round a pass, three of a type test's, past the count it was given.
            Bridge = Schedule.Sequence(Check, Schedule.Saturate(fold));
            Generate = Schedule.Sequence(Schedule.Saturate(Ir.Analyse, pair, tag, structure, at, spell), places, Schedule.Saturate(partition, need, parted),
                Schedule.Saturate(evaluate), Schedule.Saturate(classes), Schedule.Saturate(gate), Schedule.Saturate(lift, strict, twovalued), Schedule.Saturate(best, conflict),
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

        /// <summary>The theory's schedule: the rows settled per set, each set's table and its view, the classes the boxes allow and refute, and the licences.</summary>
        public Schedule Settle { get; }

        /// <summary>
        /// A bundle's schedule, in strata: terms into atoms, and the candidates down the tree; cells; the service on them;
        /// the gate on atoms; verdicts up the tree.
        /// </summary>
        public Schedule Check { get; }

        /// <summary>
        /// The translator's bridge's schedule (G6): check mode's, then the case-insensitive affix a <c>LIKE</c> over a fold
        /// is written as, which no check reads.
        /// </summary>
        public Schedule Bridge { get; }

        /// <summary>
        /// Generate mode's schedule: positions down the tree and the templates' forms; the cells, the service and the gate
        /// on them as in check mode; what the gate agreed lifted, and presence through calls; the bests composed up the
        /// tree; one weakening a node, from complete inputs.
        /// </summary>
        public Schedule Generate { get; }

        /// <summary>
        /// How deep S images a nested call: the passes its place, image and assume strata take (G6's <c>Repeat(4, …)</c>), the
        /// depth the loader cut a place at before; a bound on the work, which no row says, as the run's budget is.
        /// </summary>
        internal const int PlaceDepth = 4;

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

        /// <summary>
        /// The classes a place's characters may hold beneath a stratum, written by the bundle: the node's a later round's
        /// computed atom was rendered beneath read, the root's elsewhere, a superset (G6).
        /// </summary>
        public Function<long, JPath, ValueSet<string>> ArmChars { get; }

        /// <summary>
        /// The stratum an atom over a computed place is read beneath, 0 at the root, an arm's, or a later round's own where its
        /// conjunct was rendered beneath that round's node (G6); written by the bundle.
        /// </summary>
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

        /// <summary>
        /// The keys of a function some target a set serves answers otherwise than Calcite, derived at the build; a key the
        /// service defines (<see cref="CosmosFunctionAnswer.Defined"/>) disagrees nowhere.
        /// </summary>
        public Function<RenderingSet, string, ValueSet<FunctionKey>> DisagreeingKeys { get; }

        /// <summary>The name an operator's rows are filed under at a SQL type, <c>+:INTEGER</c> (<c>rowname</c>, G6): constant data.</summary>
        public Function<string, string, string> RowName { get; }

        /// <summary>An operator whose rows are filed under its own name, whatever its type (<c>rownamedalone</c>, G6): constant data.</summary>
        public Relation<string> RowNamedAlone { get; }

        /// <summary>The name a call's rows are filed under, S's rule over <see cref="RowName"/> and <see cref="RowNamedAlone"/> (G6).</summary>
        public Function<Val, string> CallKey { get; }

        /// <summary>A spelling, by its rank, every call of a function may take (<c>spelledalways</c>, G6): constant data.</summary>
        public Relation<string, long> SpelledAlways { get; }

        /// <summary>
        /// A spelling, by its rank, a call of a function may take where its literal at an ordinal is a whole number of a class
        /// (<c>spelledwhere</c>, G6): constant data.
        /// </summary>
        public Relation<string, long, long, FunctionValueClass> SpelledWhere { get; }

        /// <summary>
        /// The spelling a call takes, the least ranked of those its function's rows let it (<c>callspelling</c>, G6): what the
        /// printer and the translator write of a call whose function has more than one.
        /// </summary>
        public Function<Val, long> CallSpelling { get; }

        /// <summary>What Calcite answers over a function key, a value or null, as its row states (G6): constant data.</summary>
        public Function<FunctionKey, CalciteYield> FunctionYield { get; }

        /// <summary>The keys of a function Calcite answers a value, or null, over, each read as the atoms its class holds (G6).</summary>
        public Function<string, CalciteYield, ValueSet<FunctionKey>> YieldKeys { get; }

        /// <summary>The keys of a function the service defines alone (the D class), each read as the atoms its class holds (G6).</summary>
        public Function<string, ValueSet<FunctionKey>> DefinedKeys { get; }

        /// <summary>The kinds of value a SQL type holds, a constant of Calcite's type system (G6): constant data.</summary>
        public Function<string, ValueSet<string>> TypeKinds { get; }

        /// <summary>The readers of a literal operand, read exactly and keyed apart where the rows key a literal (G6): constant data.</summary>
        public Function<ValueSet<string>> LiteralReaders { get; }

        /// <summary>
        /// Absent, where a call has an operand at an ordinal its function's rows name no key of (G6): no row says what
        /// Calcite answers over it, so the call's image holds absent beside its type's kinds. Empty otherwise.
        /// </summary>
        public Function<Val, ValueSet<string>> HeldAbsent { get; }

        /// <summary>What a raw column's reading reads a stored value of each class as (<c>CosmosAxiomRows.ReadColumns</c>, G5), raw.</summary>
        public Function<string, string, CosmosValueClass, string> RawReads { get; }

        /// <summary>The literal classes a raw column's reading is compared beside, raw.</summary>
        public Relation<string, string, CosmosValueClass> RawBeside { get; }

        /// <summary>Whether each comparison compiles over a raw column's reading, raw.</summary>
        public Function<string, string, CosmosServiceTest, bool> RawCompiles { get; }

        /// <summary>Whether a raw column's reading compares a value as its type, or as the text it renders, raw.</summary>
        public Function<string, string, bool> RawAsType { get; }

        /// <summary>What a comparison a raw column compiles answers of a value it reads beside a literal of another type, raw.</summary>
        public Function<string, string, CosmosServiceTest, Truths> RawAcross { get; }

        /// <summary>What a test answers of a value a reading reads as null, raises on, or reads as a value, where that decides it (G5).</summary>
        public Function<CosmosServiceTest, string, Truths> ReadTruths { get; }

        /// <summary>The JSON type of each class of value, constant data (G5).</summary>
        public Function<CosmosValueClass, string> ClassType { get; }

        /// <summary>What one raw column answers on a key, as S's raw rules derive it from the column's rows; no lattice, so two disagreeing conflict.</summary>
        public Function<RawKey, string, RawAnswer> RawColumn { get; }

        /// <summary>The raw columns' answers, joined key by key from every column's: what a raw reading's cells read.</summary>
        public Function<RawTruths> RawUnion { get; }

        /// <summary>The classes a reading's rows say it reads: what refining it is over (G5).</summary>
        public Function<string, ValueSet<CosmosValueClass>> ReadsClasses { get; }

        /// <summary>The classes over which a raw column's raw reading refines the text: it reads them as the text does, or raises (G5).</summary>
        public Function<string, ValueSet<CosmosValueClass>> RefinesText { get; }

        /// <summary>
        /// The readers a function's operand may be read through for its rows to hold of it: those its rows were measured
        /// through, and a raw column refining the text over every class where they were measured through the text (G5).
        /// What the image rule reads.
        /// </summary>
        public Function<string, ValueSet<string>> FunctionReaders { get; }

        /// <summary>What Calcite makes of each reading, as rules over its rows (G7): the vocabulary the facts graph defines too.</summary>
        public ReadingVocabulary Calcite { get; }

        /// <summary>
        /// Every reading's rows as one value, joined reading by reading (G7): what Calcite's side of a cell evaluates an atom's
        /// reading by, as it reads its raw keys in <see cref="RawUnion"/>.
        /// </summary>
        public Function<ReadingTable> ReaderRows { get; }
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

        /// <summary>
        /// Which tests' case-insensitive forms match as the test over Calcite's fold by a function, by target, with each band
        /// the row says nothing of (G6, <c>Service.AffixesMatchByCodePoint</c>'s).
        /// </summary>
        public Relation<RenderingTarget, CosmosServiceTest, string, string> FoldsAs { get; }

        /// <summary>The targets a set serves that a test's case-insensitive form matches as the test over a fold on (G6).</summary>
        public Function<RenderingSet, CosmosServiceTest, string, ValueSet<RenderingTarget>> FoldTargets { get; }

        /// <summary>The bands a set's targets' rows of a test's case-insensitive form over a fold say nothing of (G6).</summary>
        public Function<RenderingSet, CosmosServiceTest, string, ValueSet<string>> FoldParts { get; }

        /// <summary>
        /// A test's case-insensitive form matches as the test over a fold on every target a set serves, outside the bands
        /// held (G6): settled from <see cref="FoldTargets"/> and <see cref="FoldParts"/> complete.
        /// </summary>
        public Function<RenderingSet, CosmosServiceTest, string, ValueSet<string>> Folds { get; }

        /// <summary>What a target's <c>LIKE</c> reads a unit of its pattern as, citing its row (G7, <c>Service.LikeMatchesByCodePoint</c>'s).</summary>
        public Relation<RenderingTarget, LikeUnit, string> LikeReads { get; }

        /// <summary>The targets a set serves whose rows read a unit of a pattern so, by the row (G7).</summary>
        public Function<RenderingSet, LikeUnit, string, ValueSet<RenderingTarget>> LikeTargets { get; }

        /// <summary>
        /// What a set's <c>LIKE</c> reads each unit of its pattern as, each with its row: a unit every target the set serves
        /// reads so, settled from <see cref="LikeTargets"/> complete, which the set's view hands the leaf (G7).
        /// </summary>
        public Function<RenderingSet, ValueSet<(LikeUnit, string)>> LikesOf { get; }

        /// <summary>
        /// A <c>LIKE</c> over a fold answers as a test's case-insensitive form of an argument over the fold's operand at an
        /// ordinal (G6): what the translator writes it as.
        /// </summary>
        public Relation<Pred, CosmosServiceTest, string, long> FoldedAs { get; }

        /// <summary>How a comparison of two paths reads against a literal, the sides swapped or not: the mirror law, G3's.</summary>
        public Relation<CosmosServiceTest, bool, CosmosServiceTest> PairReads { get; }

        /// <summary>Which band a comparator's spelling rewrites (the emulator's of what it stores, U+0000 and U+FDD0), a spelling row's (G1).</summary>
        public Relation<CosmosAnswer, string> Rewrites { get; }

        /// <summary>The classes a parameter holding a character of is never sent, by target (G1).</summary>
        public Relation<RenderingTarget, string> Unsent { get; }

        /// <summary>The comparators whose spelling rewrites some band, a union over <see cref="Rewrites"/>.</summary>
        public Function<ValueSet<CosmosAnswer>> Rewriting { get; }

        /// <summary>
        /// The bands each answer's spelling rewrites, a union over <see cref="Rewrites"/> starting empty for every answer:
        /// complete in <c>tabulate</c>, so an answer's absence from a row reads as a spelling that keeps the band.
        /// </summary>
        public Function<CosmosAnswer, ValueSet<string>> RewrittenBy { get; }

        /// <summary>
        /// The spelling a set's statements send a string in, derived from its string order and <see cref="Rewrites"/> (G1): what
        /// the writer applies (<see cref="SpellingOf"/>). Absent for a set whose rows name no string order.
        /// </summary>
        public Function<RenderingSet, SentSpelling> Sends { get; }

        /// <summary>The bands a set's string comparators' spelling rewrites, empty for a set sent as written: what a written name may not hold.</summary>
        public Function<RenderingSet, ValueSet<string>> Respelled { get; }

        /// <summary>The classes no literal of a form sent under a set may hold, a parameter holding one never being sent to a target it serves.</summary>
        public Function<RenderingSet, ValueSet<string>> Unsendable { get; }

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

        /// <summary>
        /// What a reading's order holds in process, every reading a row names, and none for every other reading of a kind:
        /// what the pair rule hands a comparison of two readings (G3).
        /// </summary>
        public Function<string, ValueSet<string>> ReaderOrder { get; }

        /// <summary>The kind each reading compares as, the family of Calcite's type it is of; none for a raw value (G3).</summary>
        public Relation<string, string> ReaderKind { get; }

        /// <summary>
        /// The JSON kinds each type test is TRUE of in process, by its name (<c>Calcite.TypeTestsAreTheirBodies</c>, G7), which
        /// the vocabulary's type-test rule reads, as the facts graph's does.
        /// </summary>
        public Function<string, ValueSet<string>> TestKinds { get; }

        // Composition's: Kleene's answers, the keys each set is expected and matched on, the connectives it composes
        // through, an identity's class and the position beneath a negation.
        public Function<MeasuredKey, RenderingTarget, CosmosAnswer> Kleene { get; }
        public Function<CosmosServiceTest, RenderingSet, ValueSet<MeasuredKey>> Expected { get; }
        public Function<CosmosServiceTest, RenderingSet, ValueSet<MeasuredKey>> Matched { get; }
        public Function<RenderingSet, ValueSet<CosmosServiceTest>> Licensed { get; }
        public Function<CosmosServiceTest, ValueSet<CellClass>> Identity { get; }
        public Relation<RenderPosition, RenderPosition> Opposite { get; }

        /// <summary>
        /// Which answers read each test's entry over each operand class (<see cref="AnswerMeanings.ReadBy"/>, G2): a string
        /// order's comparators the order of strings', a number order's the order of numbers', the matchers an affix's or a
        /// <c>LIKE</c>'s over a string. Host data the roles rule reads.
        /// </summary>
        public Function<CosmosServiceTest, CosmosValueClass, ValueSet<CosmosAnswer>> ReadBy { get; }

        /// <summary>
        /// Which answers the leaf evaluates each test's cells by over each operand class (<see cref="RenderingDefinition.Evaluates"/>,
        /// <see cref="ServiceLeaf.Evaluates"/>): what the roles rule hands a key a reader only among (G2).
        /// </summary>
        public Function<CosmosServiceTest, CosmosValueClass, ValueSet<CosmosAnswer>> Evaluates { get; }

        /// <summary>How each comparator reads a whole number within int64, <c>value</c> or <c>double</c>, a row's (G2).</summary>
        public Relation<CosmosAnswer, string> WholeReads { get; }

        /// <summary>
        /// The answers that read a settled key, each placing or matching a cell: those of its test's role over its operand,
        /// where it names one and no comparator of another beside them; none otherwise, from the start (G2).
        /// </summary>
        public Function<MeasuredKey, RenderingSet, ValueSet<CosmosAnswer>> Readers { get; }

        /// <summary>How each reader of a settled key reads a whole number, as <see cref="WholeReads"/> says; none from the start (G2).</summary>
        public Function<MeasuredKey, RenderingSet, ValueSet<(CosmosAnswer, string)>> Readings { get; }

        /// <summary>A key of a connective's side that may fail, beside each key it stands for (<see cref="KleeneTables.Failing"/>, G4).</summary>
        public Relation<MeasuredKey, MeasuredKey> StandsFor { get; }

        /// <summary>Every key a failing side's key stands for: what a set must have settled for its entry to be one.</summary>
        public Function<MeasuredKey, ValueSet<MeasuredKey>> Stands { get; }

        // What a set settled of the keys a failing side's key stands for (G4): their answers, the keys, and their rows.
        public Function<MeasuredKey, RenderingSet, ValueSet<CosmosAnswer>> FailingAnswers { get; }
        public Function<MeasuredKey, RenderingSet, ValueSet<MeasuredKey>> FailingCovers { get; }
        public Function<MeasuredKey, RenderingSet, ValueSet<string>> FailingEvidence { get; }

        /// <summary>The kinds of value a side of a pair may hold (<see cref="CosmosValueClasses.Kinds"/>), over which the pair table is read (G3).</summary>
        public Relation<CosmosValueClass> Kind { get; }

        // What a set's rows read each pair key as (G3), every reading's answers and rows; and the pair table they make.
        public Function<MeasuredKey, RenderingSet, ValueSet<CosmosAnswer>> PairAnswers { get; }
        public Function<MeasuredKey, RenderingSet, ValueSet<string>> PairEvidence { get; }

        /// <summary>Every pair key a set's rows read, one entry a key, empty from the start: the table a comparison of two paths reads (G3).</summary>
        public Function<RenderingSet, ValueSet<MeasuredEntry>> PairTable { get; }

        /// <summary>
        /// The rows a set measured a test's Kleene cells by, of an operand: present where some cell of them matched, the union of
        /// their rows. What a truth value's constant and a pair licence's leaf rest on (G3, G4).
        /// </summary>
        public Function<CosmosServiceTest, CosmosValueClass, RenderingSet, ValueSet<string>> MeasuredCites { get; }

        /// <summary>Kleene's constants of each junction: whether one absorbs it (<see cref="KleeneTables.Units"/>, G4).</summary>
        public Relation<CosmosServiceTest, bool, bool> Units { get; }

        /// <summary>
        /// The constant the service is sent for each class a truth value may be of (<see cref="CosmosValueClasses.Constants"/>,
        /// G4): TRUE's <c>true</c> and FALSE's <c>false</c>, and none for UNKNOWN, which no constant is.
        /// </summary>
        public Relation<CosmosValueClass, bool> Constants { get; }

        /// <summary>
        /// Each set's table as the evaluation primitives read it: its entries, the classes S placed and its pair table, viewed
        /// once they are settled (<see cref="ServiceTable"/>), so that every leaf and compound under the set reads one view.
        /// </summary>
        public Function<RenderingSet, ServiceTable> View { get; }

        /// <summary>
        /// What each class is to the gate (G2): a class of a value that is no boolean as a whole condition over one reads
        /// (<see cref="AnswerMeanings.Read"/>), and any other as it is.
        /// </summary>
        public Function<CellClass, CellClass> ReadAs { get; }

        /// <summary>The classes inside on which an atom is each junction's identity, as the gate reads them (G2).</summary>
        public Function<CosmosServiceTest, ValueSet<CellClass>> IdentityClasses { get; }

        /// <summary>The classes inside on which Calcite may answer UNKNOWN, which keep an atom from being two-valued (G2).</summary>
        public Function<ValueSet<CellClass>> UnknownClasses { get; }

        // A bundle's tables. The host writes a path's spelling beside its term (a merge of two paths would give one class
        // two spellings, and throw), the set, each import once and lattice-free, beside each atom what the facts its
        // conjunct was rendered from say its path may hold (`assumed`, anything where none is read), and the root's
        // candidate with every member of its junctions as written and each conjunction's members, nested ones flattened;
        // the rules write the rest: the forms each junction lies within, the candidates beneath the root, and the members
        // of a member of one connective.
        public Function<JPath, string> Spelling { get; }
        public Relation<ServiceForm, ServiceForm> Conjunct { get; }
        public Relation<RenderingSet> Under { get; }
        /// <summary>
        /// What a place may hold wherever an atom reads it: a path's, the host's import, written once; a computed place's,
        /// the join of its images beneath the root and each stratum an atom over it is read at (G6), so a later round's atom
        /// is cut from its place's image there where the root has none. Each atom sets aside the cells its stratum does not
        /// admit (<see cref="Assumed"/>).
        /// </summary>
        public Function<JPath, PathValues> Imported { get; }
        public Function<Pred, PathValues> Assumed { get; }

        public Function<JPath, ValueSet<string>> ImportedChars { get; }
        public Function<JPath, ValueSet<string>> PartedLiterals { get; }

        // What S's rules hand each leaf beside its table (LeafReads, G1), keyed by the leaf, each empty until a rule says
        // otherwise: the literals unplaced, the classes a point is unkept over, the bands unread, uncut and blinding, a pair's
        // bands departing and moved.
        public Function<ServiceForm, ValueSet<string>> UnplacedLiterals { get; }
        public Function<ServiceForm, ValueSet<string>> Unkept { get; }
        public Function<ServiceForm, ValueSet<string>> Unread { get; }
        public Function<ServiceForm, ValueSet<string>> Uncut { get; }
        public Function<ServiceForm, ValueSet<string>> Blind { get; }
        public Function<ServiceForm, ValueSet<(string, string)>> Departs { get; }
        public Function<ServiceForm, ValueSet<string>> Moved { get; }

        /// <summary>The rows that license each test a leaf leans on, empty until a rule says otherwise (<see cref="LeafReads.Licences"/>, G3).</summary>
        public Function<ServiceForm, ValueSet<string>> LicenceRows { get; }

        /// <summary>The classes the member names a place reads hold, the bundle's reading of its paths (G1).</summary>
        public Function<JPath, ValueSet<string>> NameChars { get; }

        /// <summary>A place the writer can write under the bundle's set, which the spelling rule proposes a form over (G1).</summary>
        public Relation<JPath> Writable { get; }
        public Relation<Pred, RenderPosition, ServiceForm> Candidate { get; }
        public Relation<ServiceForm, ServiceForm> Member { get; }
        public Function<Pred, ServiceForm, CellPartition> Partition { get; }
        public Relation<CellPartition, ServiceForm> Need { get; }
        public Function<CellPartition, ServiceForm, OutcomeVector> Svc { get; }
        public Function<Pred, ServiceForm, CellOutcomes> Outcomes { get; }

        /// <summary>The rows a candidate's classes rest on beside its cells', empty until a rule says otherwise (G2).</summary>
        public Function<Pred, ServiceForm, ValueSet<string>> Rests { get; }
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
        /// <summary>The bands a set's string comparators rank apart, which no string ordering's literal S sends under it holds.</summary>
        public Function<RenderingSet, ValueSet<string>> PartingBands { get; }
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

        /// <summary>
        /// A position and a truth value an else-arm leaves the case's rows as its condition's own failing leaves them, with the
        /// junction the case then is of its condition, negated or not, and its then-arm (<see cref="KleeneTables.AbsorbingElse"/>).
        /// </summary>
        public Relation<RenderPosition, string, CosmosServiceTest, bool> AbsorbingElse { get; }

        /// <summary>
        /// A position and a truth value an else-arm makes the case's rows the opposite of what its failing condition would, with
        /// the junction the case then is over a two-valued condition (<see cref="KleeneTables.OppositeElse"/>).
        /// </summary>
        public Relation<RenderPosition, string, CosmosServiceTest, bool> OppositeElse { get; }

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
            values.RegisterEnum<CalciteYield>();
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
            values.Register<RawAnswer>("raw answer");
            values.Register<RawTruths>("raw truths");
            values.Register<ClassRead>("class read");
            values.Register<ReadingTable>("reading table");
            values.Register<LikeUnit>("like unit");
            values.Register<(LikeUnit, string)>("like unit and row");
            values.Register<(PredicateAtom, ReadingTable)>("atom and readings");
            values.Register<(PredicateAtom, RawTruths)>("atom and raw truths");
            values.Register<PathImport>("path import");
            values.Register<MeasuredEntry>("measured entry");
            values.Register<AnswerMeanings>("answer meanings");
            values.Register<ServiceTable>("service table");
            values.Register<LeafReads>("leaf reads");
            values.Register<(string, string)>("band pair");
            values.Register<(CosmosAnswer, string)>("answer reading");
            values.RegisterEnum<SentSpelling>();
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
            values.RegisterSet<(string, string)>();
            values.RegisterSet<(CosmosAnswer, string)>();
            values.RegisterSet<ClassRead>();
            values.RegisterSet<(LikeUnit, string)>();
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

            // What a class is to the gate (G2): a value that is no boolean as a whole condition over one reads it, which keeps
            // only some outcomes as they are; any other as it is.
            allowed.DefineRule("a class of a value that is no boolean reads as a whole condition over one reads", r =>
            {
                var (k, w, mn) = r.Vars<CellClass, bool, AnswerMeanings>("k", "w", "mn");
                r.When(Class.Of(k), w.Is(Compute.Of(k, x => x.NonBoolean)), w.Is(true), mn.Is(Means.Of()));
                r.Then(ReadAs.Of(k).Is(Compute.Of(mn, k, (m, x) => m.Read(x))));
            });

            allowed.DefineRule("a class of a boolean reads as it is", r =>
            {
                var (k, w) = r.Vars<CellClass, bool>("k", "w");
                r.When(Class.Of(k), w.Is(Compute.Of(k, x => x.NonBoolean)), w.Is(false));
                r.Then(ReadAs.Of(k).Is(k));
            });

            allowed.DefineRule("a class within an allowing box is allowed", r =>
            {
                var (k, b, tol) = r.Vars<CellClass, Box, Truths>("k", "b", "tol");
                var (pos, v, o, rk) = r.Vars<RenderPosition, Verdict, bool, CellClass>("pos", "v", "o", "rk");
                var (kc, bc, ks, bs) = r.Vars<Truths, Truths, ServiceOutcomes, ServiceOutcomes>("kc", "bc", "ks", "bs");
                r.When(Class.Of(k), o.Is(Compute.Of(k, x => x.Outside)), o.Is(false), rk.Is(ReadAs.Of(k)), Allowing.Of(pos, v, b), tol.Is(Tolerated.Of()),
                    kc.Is(Compute.Of(k, tol, (x, y) => x.Calcite & ~y)), bc.Is(Compute.Of(b, x => x.Calcite)), ks.Is(Compute.Of(rk, x => x.Service)), bs.Is(Compute.Of(b, x => x.Service)));
                r.Where(RenderingPrimitives.TruthsWithin.Of(kc, bc));
                r.Where(RenderingPrimitives.OutcomesWithin.Of(ks, bs));
                r.Then(Allowed.Of(pos, v).Is(Primitives.Singleton<CellClass>().Of(k)), AllowedCites.Of(pos, v).Is(Compute.Of(b, x => x.Cites)));
            });

            // A cell only a document an earlier round drops holds is read as one Calcite raises on, where the policy sets a raise
            // aside: anything but a failure refines it, and it refutes nothing (G2). A policy that sets none aside sets no cell
            // aside either.
            allowed.DefineRule("a class set aside is allowed within a box admitting its outcomes", r =>
            {
                var (k, b, o, rk) = r.Vars<CellClass, Box, bool, CellClass>("k", "b", "o", "rk");
                var (pos, v, tol) = r.Vars<RenderPosition, Verdict, Truths>("pos", "v", "tol");
                var (ks, bs) = r.Vars<ServiceOutcomes, ServiceOutcomes>("ks", "bs");
                r.When(Class.Of(k), o.Is(Compute.Of(k, x => x.Outside)), o.Is(true), tol.Is(Tolerated.Of()), rk.Is(ReadAs.Of(k)), Allowing.Of(pos, v, b),
                    ks.Is(Compute.Of(rk, x => x.Service)), bs.Is(Compute.Of(b, x => x.Service)));
                r.Where(RenderingPrimitives.TruthsWithin.Of(Truths.Raises, tol));
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

            allowed.DefineRule("a class set aside is one Calcite raises on alone", r =>
            {
                var (k, o, tol) = r.Vars<CellClass, bool, Truths>("k", "o", "tol");
                r.When(Class.Of(k), o.Is(Compute.Of(k, x => x.Outside)), o.Is(true), tol.Is(Tolerated.Of()));
                r.Where(RenderingPrimitives.TruthsWithin.Of(Truths.Raises, tol));
                r.Then(RaiseOnly.Of().Is(Primitives.Singleton<CellClass>().Of(k)));
            });

            // A junction's identity and Calcite's UNKNOWN are of a class inside, as the gate reads it (G2).
            allowed.DefineRule("a class inside that reads as a connective's identity is one", r =>
            {
                var (k, rk, o) = r.Vars<CellClass, CellClass, bool>("k", "rk", "o");
                var (j, y) = r.Vars<CosmosServiceTest, ValueSet<CellClass>>("j", "y");
                r.When(Class.Of(k), o.Is(Compute.Of(k, x => x.Outside)), o.Is(false), rk.Is(ReadAs.Of(k)), y.Is(Identity.Of(j)));
                r.Where(Primitives.Contains<CellClass>().Of(y, rk));
                r.Then(IdentityClasses.Of(j).Is(Primitives.Singleton<CellClass>().Of(k)));
            });

            allowed.DefineRule("a class inside whose truths of Calcite's hold UNKNOWN is one Calcite answers unknown on", r =>
            {
                var (k, o, kc) = r.Vars<CellClass, bool, Truths>("k", "o", "kc");
                r.When(Class.Of(k), o.Is(Compute.Of(k, x => x.Outside)), o.Is(false), kc.Is(Compute.Of(k, x => x.Calcite)));
                r.Where(RenderingPrimitives.TruthsWithin.Of(Truths.Unknown, kc));
                r.Then(UnknownClasses.Of().Is(Primitives.Singleton<CellClass>().Of(k)));
            });

            // Calcite's truths as they are: a class that may raise is no definite counterexample, whatever the box says, and
            // nor is a class set aside.
            refutes.DefineRule("a class within a refuting box is a definite counterexample", r =>
            {
                var (k, b, o, rk) = r.Vars<CellClass, Box, bool, CellClass>("k", "b", "o", "rk");
                var (pos, v) = r.Vars<RenderPosition, Verdict>("pos", "v");
                var (kc, bc, ks, bs) = r.Vars<Truths, Truths, ServiceOutcomes, ServiceOutcomes>("kc", "bc", "ks", "bs");
                r.When(Class.Of(k), o.Is(Compute.Of(k, x => x.Outside)), o.Is(false), rk.Is(ReadAs.Of(k)), Refuting.Of(pos, v, b), kc.Is(Compute.Of(k, x => x.Calcite)),
                    bc.Is(Compute.Of(b, x => x.Calcite)), ks.Is(Compute.Of(rk, x => x.Service)), bs.Is(Compute.Of(b, x => x.Service)));
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

            // Measured is complete when this stratum runs, so a key is settled once and its answers are all of them; what reads
            // it is the roles stratum's, which starts from nothing (G2).
            tabulate.DefineRule("a key every target its set serves measured is settled", r =>
            {
                var (k, s) = r.Vars<MeasuredKey, RenderingSet>("k", "s");
                var (m, d, v) = r.Vars<ValueSet<RenderingTarget>, ValueSet<RenderingTarget>, ValueSet<CosmosAnswer>>("m", "d", "v");
                r.When(m.Is(Measured.Of(k, s)), d.Is(Serves.Of(s)), v.Is(Answers.Of(k, s)));
                r.Where(Primitives.IsSubsetOf<RenderingTarget>().Of(d, m));
                r.Then(Final.Of(k, s).Is(v), Readers.Of(k, s).Is(ValueSet<CosmosAnswer>.Empty), Readings.Of(k, s).Is(ValueSet<(CosmosAnswer, string)>.Empty));
            });
        }

        // What reads each settled key (G2): those of its answers whose role is the key's test's over its operand and that the
        // leaf evaluates, where it names one and no comparator of another role, or of none the leaf evaluates, beside them;
        // and how each reads a whole number; the key, with them, an entry of its set's table once both are complete. A
        // comparator a row names for another kind's order is of another role, and one the meanings give a role the leaf has
        // no evaluation of is of none, so the key has no reader and its leaf answers anything.
        void DefineRoles(Ruleset roles, Ruleset entries)
        {
            roles.DefineRule("the answers of a key's role the leaf evaluates read it, where it names one and no comparator of another", r =>
            {
                var (k, s, t) = r.Vars<MeasuredKey, RenderingSet, CosmosServiceTest>("k", "s", "t");
                var (v, w, mn) = r.Vars<ValueSet<CosmosAnswer>, ValueSet<CosmosAnswer>, AnswerMeanings>("v", "w", "mn");
                var (c, ev) = r.Vars<CosmosValueClass, ValueSet<CosmosAnswer>>("c", "ev");
                r.When(v.Is(Final.Of(k, s)), t.Is(Compute.Of(k, x => x.Test)), c.Is(Compute.Of(k, x => x.Operand)), w.Is(ReadBy.Of(t, c)), ev.Is(Evaluates.Of(t, c)), mn.Is(Means.Of()));
                var role = Primitives.Intersect<CosmosAnswer>().Of(w, ev);
                var read = Primitives.Intersect<CosmosAnswer>().Of(v, role);
                r.Where(Primitives.Greater.Of(Primitives.Count<CosmosAnswer>().Of(read), 0L));
                r.Where(Primitives.IsSubsetOf<CosmosAnswer>().Of(Primitives.Intersect<CosmosAnswer>().Of(v, Compute.Of(mn, x => x.Comparators)), role));
                r.Then(Readers.Of(k, s).Is(read));
            });

            roles.DefineRule("a reader of a key reads a whole number as its rows say", r =>
            {
                var (k, s, d) = r.Vars<MeasuredKey, RenderingSet, ValueSet<CosmosAnswer>>("k", "s", "d");
                var (c, g) = r.Vars<CosmosAnswer, string>("c", "g");
                r.When(d.Is(Readers.Of(k, s)), WholeReads.Of(c, g));
                r.Where(Primitives.Contains<CosmosAnswer>().Of(d, c));
                r.Then(Readings.Of(k, s).Is(Primitives.Singleton<(CosmosAnswer, string)>().Of(Compute.Of(c, g, (x, y) => (x, y)))));
            });

            entries.DefineRule("a settled key is an entry of its set's table", r =>
            {
                var (k, s) = r.Vars<MeasuredKey, RenderingSet>("k", "s");
                var (v, i, mn) = r.Vars<ValueSet<CosmosAnswer>, ValueSet<string>, AnswerMeanings>("v", "i", "mn");
                var (d, g) = r.Vars<ValueSet<CosmosAnswer>, ValueSet<(CosmosAnswer, string)>>("d", "g");
                r.When(v.Is(Final.Of(k, s)), i.Is(Evidence.Of(k, s)), mn.Is(Means.Of()), d.Is(Readers.Of(k, s)), g.Is(Readings.Of(k, s)));
                r.Then(Table.Of(s).Is(Primitives.Singleton<MeasuredEntry>().Of(Compute.Of(Compute.Of(k, v, i, mn, MeasuredEntry.Of), d, g, MeasuredEntry.Read))));
            });
        }

        // A side that may fail stands for every truth it could have given, and fails the connective beside them (decision 2,
        // G4): its key answers what every key it stands for answers, once the set settled them all, and a failure.
        void DefineFailing(Ruleset failing, Ruleset entries)
        {
            failing.DefineRule("a failing side's key answers what each key it stands for answers", r =>
            {
                var (f, k, s) = r.Vars<MeasuredKey, MeasuredKey, RenderingSet>("f", "k", "s");
                var (v, i) = r.Vars<ValueSet<CosmosAnswer>, ValueSet<string>>("v", "i");
                r.When(StandsFor.Of(f, k), v.Is(Final.Of(k, s)), i.Is(Evidence.Of(k, s)));
                r.Then(FailingAnswers.Of(f, s).Is(v), FailingCovers.Of(f, s).Is(Primitives.Singleton<MeasuredKey>().Of(k)), FailingEvidence.Of(f, s).Is(i));
            });

            entries.DefineRule("a failing side's key whose every key the set settled is an entry of its table, failing beside them", r =>
            {
                var (f, s, mn) = r.Vars<MeasuredKey, RenderingSet, AnswerMeanings>("f", "s", "mn");
                var (a, i) = r.Vars<ValueSet<CosmosAnswer>, ValueSet<string>>("a", "i");
                var (c, w) = r.Vars<ValueSet<MeasuredKey>, ValueSet<MeasuredKey>>("c", "w");
                r.When(a.Is(FailingAnswers.Of(f, s)), c.Is(FailingCovers.Of(f, s)), w.Is(Stands.Of(f)), i.Is(FailingEvidence.Of(f, s)), mn.Is(Means.Of()));
                r.Where(Primitives.IsSubsetOf<MeasuredKey>().Of(w, c));
                r.Then(Table.Of(s).Is(Primitives.Singleton<MeasuredEntry>().Of(Compute.Of(f, Primitives.Insert<CosmosAnswer>().Of(a, CosmosAnswer.Fails), i, mn, MeasuredEntry.Of))));
            });
        }

        // Each set's table as the leaves and compounds read it, once its entries, the classes placed and its pair table are
        // complete: one view a set, which the evaluate rules hand the primitives, held by the theory. A set that settled no
        // key has no table and no view, so nothing is evaluated under it.
        void DefineView(Ruleset view)
        {
            view.DefineRule("a set's settled table is viewed as its leaves read it", r =>
            {
                var s = r.Var<RenderingSet>("s");
                var (t, pt, pl) = r.Vars<ValueSet<MeasuredEntry>, ValueSet<MeasuredEntry>, ValueSet<CosmosValueClass>>("t", "pt", "pl");
                var lu = r.Var<ValueSet<(LikeUnit, string)>>("lu");
                r.When(t.Is(Table.Of(s)), pt.Is(PairTable.Of(s)), pl.Is(Placed.Of()), lu.Is(LikesOf.Of(s)));
                r.Then(View.Of(s).Is(Compute.Of(t, pl, pt, lu, ServiceTable.Of)));
            });
        }

        // A comparison of two paths read off the rows (G3, Service.TwoPathsCompareAsAPathAndALiteral): as the left against a
        // literal of the right's value, as the right against the left's with the comparator mirrored (pairreads), and as a
        // side absent; each pair of kinds' key the union of what every reading the set settled answers, and an entry of its
        // set's pair table once complete.
        void DefinePairs(Ruleset pairs, Ruleset entries)
        {
            pairs.DefineRule("a pair reads as its left against a literal of its right's value", r =>
            {
                var (l, rt, s) = r.Vars<CosmosValueClass, CosmosValueClass, RenderingSet>("l", "rt", "s");
                var (t, u, dk) = r.Vars<CosmosServiceTest, CosmosServiceTest, MeasuredKey>("t", "u", "dk");
                var (v, i) = r.Vars<ValueSet<CosmosAnswer>, ValueSet<string>>("v", "i");
                r.When(Kind.Of(l), Kind.Of(rt), PairReads.Of(t, false, u), dk.Is(Compute.Of(u, rt, l, KeyOf)), v.Is(Final.Of(dk, s)), i.Is(Evidence.Of(dk, s)));
                r.Then(PairAnswers.Of(Compute.Of(t, rt, l, KeyOf), s).Is(v), PairEvidence.Of(Compute.Of(t, rt, l, KeyOf), s).Is(i));
            });

            pairs.DefineRule("a pair reads as its right against a literal of its left's value, the comparator mirrored", r =>
            {
                var (l, rt, s) = r.Vars<CosmosValueClass, CosmosValueClass, RenderingSet>("l", "rt", "s");
                var (t, u, dk) = r.Vars<CosmosServiceTest, CosmosServiceTest, MeasuredKey>("t", "u", "dk");
                var (v, i) = r.Vars<ValueSet<CosmosAnswer>, ValueSet<string>>("v", "i");
                r.When(Kind.Of(l), Kind.Of(rt), PairReads.Of(t, true, u), dk.Is(Compute.Of(u, l, rt, KeyOf)), v.Is(Final.Of(dk, s)), i.Is(Evidence.Of(dk, s)));
                r.Then(PairAnswers.Of(Compute.Of(t, rt, l, KeyOf), s).Is(v), PairEvidence.Of(Compute.Of(t, rt, l, KeyOf), s).Is(i));
            });

            pairs.DefineRule("a pair with a side absent reads as the row of one", r =>
            {
                var (o, ab, s) = r.Vars<CosmosValueClass, CosmosValueClass, RenderingSet>("o", "ab", "s");
                var (t, u) = r.Vars<CosmosServiceTest, CosmosServiceTest>("t", "u");
                var (v, i) = r.Vars<ValueSet<CosmosAnswer>, ValueSet<string>>("v", "i");
                r.When(Kind.Of(o), Kind.Of(ab), ab.Is(CosmosValueClass.Absent), PairReads.Of(t, false, u), v.Is(Final.Of(PairAbsent, s)), i.Is(Evidence.Of(PairAbsent, s)));
                r.Then(PairAnswers.Of(Compute.Of(t, ab, o, KeyOf), s).Is(v), PairEvidence.Of(Compute.Of(t, ab, o, KeyOf), s).Is(i),
                    PairAnswers.Of(Compute.Of(t, o, ab, KeyOf), s).Is(v), PairEvidence.Of(Compute.Of(t, o, ab, KeyOf), s).Is(i));
            });

            entries.DefineRule("a pair key the rows read is an entry of its set's pair table", r =>
            {
                var (k, s, mn) = r.Vars<MeasuredKey, RenderingSet, AnswerMeanings>("k", "s", "mn");
                var (a, i) = r.Vars<ValueSet<CosmosAnswer>, ValueSet<string>>("a", "i");
                r.When(a.Is(PairAnswers.Of(k, s)), i.Is(PairEvidence.Of(k, s)), mn.Is(Means.Of()));
                r.Then(PairTable.Of(s).Is(Primitives.Singleton<MeasuredEntry>().Of(Compute.Of(k, a, i, mn, MeasuredEntry.Of))));
            });
        }

        // What each set's rows make of its statements (G1): the spelling they send, the bands its string comparators rewrite
        // and rank apart, and the classes no parameter may hold, read off the comparators the set's string order names or the
        // targets it serves; the writer, the lifts and the bundles read them at the frozen root.
        void DefineSets(Ruleset tabulate, Ruleset sets)
        {
            tabulate.DefineRule("a comparator whose spelling rewrites a band rewrites", r =>
            {
                var (c, u) = r.Vars<CosmosAnswer, string>("c", "u");
                r.When(Rewrites.Of(c, u));
                r.Then(Rewriting.Of().Is(Primitives.Singleton<CosmosAnswer>().Of(c)), RewrittenBy.Of(c).Is(Primitives.Singleton<string>().Of(u)));
            });

            sets.DefineRule("a set whose string order compares a spelling that rewrites a band is sent that spelling, the band respelled", r =>
            {
                var (s, o) = r.Vars<RenderingSet, ValueSet<CosmosAnswer>>("s", "o");
                var (c, u) = r.Vars<CosmosAnswer, string>("c", "u");
                r.When(o.Is(Final.Of(StringOrder, s)), Rewrites.Of(c, u));
                r.Where(Primitives.Contains<CosmosAnswer>().Of(o, c));
                r.Then(Sends.Of(s).Is(SentSpelling.Stored), Respelled.Of(s).Is(Primitives.Singleton<string>().Of(u)));
            });

            // Rewriting is complete here, the tabulate stratum's.
            sets.DefineRule("a set whose string order compares no spelling that rewrites is sent as written", r =>
            {
                var (s, o, w) = r.Vars<RenderingSet, ValueSet<CosmosAnswer>, ValueSet<CosmosAnswer>>("s", "o", "w");
                r.When(o.Is(Final.Of(StringOrder, s)), w.Is(Rewriting.Of()));
                r.Where(Primitives.IsEmpty<CosmosAnswer>().Of(Primitives.Intersect<CosmosAnswer>().Of(o, w)));
                r.Then(Sends.Of(s).Is(SentSpelling.Written));
            });

            sets.DefineRule("two bands a set's string comparator ranks apart are its parting bands", r =>
            {
                var (s, o) = r.Vars<RenderingSet, ValueSet<CosmosAnswer>>("s", "o");
                var (c, u, w) = r.Vars<CosmosAnswer, string, string>("c", "u", "w");
                r.When(o.Is(Final.Of(StringOrder, s)), Parts.Of(c, u, w));
                r.Where(Primitives.Contains<CosmosAnswer>().Of(o, c));
                r.Then(PartingBands.Of(s).Is(Primitives.Singleton<string>().Of(u)), PartingBands.Of(s).Is(Primitives.Singleton<string>().Of(w)));
            });

            sets.DefineRule("a class no parameter is sent holding on a target a set serves is unsendable under it", r =>
            {
                var (s, d) = r.Vars<RenderingSet, ValueSet<RenderingTarget>>("s", "d");
                var (t, x) = r.Vars<RenderingTarget, string>("t", "x");
                r.When(d.Is(Serves.Of(s)), Unsent.Of(t, x));
                r.Where(Primitives.Contains<RenderingTarget>().Of(d, t));
                r.Then(Unsendable.Of(s).Is(Primitives.Singleton<string>().Of(x)));
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

            // A settled answer is never empty, so one within Kleene's single answer is that answer. What measured it is gathered
            // by test and operand, which a constant's truth and a pair's licence rest on (G3, G4).
            matched.DefineRule("a settled answer that is Kleene's matches", r =>
            {
                var (k, y, s, x) = r.Vars<MeasuredKey, RenderingTarget, RenderingSet, CosmosAnswer>("k", "y", "s", "x");
                var (d, v, i) = r.Vars<ValueSet<RenderingTarget>, ValueSet<CosmosAnswer>, ValueSet<string>>("d", "v", "i");
                r.When(v.Is(Final.Of(k, s)), x.Is(Kleene.Of(k, y)), d.Is(Serves.Of(s)), i.Is(Evidence.Of(k, s)));
                r.Where(Primitives.Contains<RenderingTarget>().Of(d, y));
                r.Where(Primitives.IsSubsetOf<CosmosAnswer>().Of(v, Primitives.Singleton<CosmosAnswer>().Of(x)));
                r.Then(Matched.Of(Compute.Of(k, w => w.Test), s).Is(Primitives.Singleton<MeasuredKey>().Of(k)),
                    MeasuredCites.Of(Compute.Of(k, w => w.Test), Compute.Of(k, w => w.Operand), s).Is(i));
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
                var rr = r.Var<ReadingTable>("rr");
                r.When(Candidate.Of(e, pos, f), Ir.Atom.Of(e, p, a), x.Is(Imported.Of(p)), k.Is(ImportedChars.Of(p)), y.Is(Assumed.Of(e)), n.Is(Spelling.Of(p)),
                    rd.Is(Compute.Of(a, w => w.Reader)), g.Is(Edges.Of(rd)), rr.Is(ReaderRows.Of()));
                r.Where(Primitives.IsSubsetOf<string>().Of(Compute.Of(f, w => w.Paths), Compute.Of(n, PlaceSpellings.Set)));
                // A LIKE's pattern cuts as Calcite's rows read it (G7).
                var cuts = Compute.Of(a, f, g, rr, definition.Cut);
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

            // What a leaf reads beside its table is empty until a parted rule says otherwise (LeafReads, G1).
            need.DefineRule("a leaf a partition needs reads nothing beside its table until a row says so", r =>
            {
                var (z, g) = r.Vars<CellPartition, ServiceForm>("z", "g");
                r.When(Need.Of(z, g));
                r.Where(RenderingPrimitives.FormIsLeaf.Of(g));
                r.Then(UnplacedLiterals.Of(g).Is(ValueSet<string>.Empty), Unkept.Of(g).Is(ValueSet<string>.Empty), Unread.Of(g).Is(ValueSet<string>.Empty),
                    Uncut.Of(g).Is(ValueSet<string>.Empty), Blind.Of(g).Is(ValueSet<string>.Empty), Departs.Of(g).Is(ValueSet<(string, string)>.Empty),
                    Moved.Of(g).Is(ValueSet<string>.Empty), LicenceRows.Of(g).Is(ValueSet<string>.Empty));
            });

            // What licenses a leaf's evaluation is cited beside its table: the rows that measured, as Kleene's, the cells of
            // each test it leans on (G3).
            parted.DefineRule("a leaf rests on the rows that license each test it leans on", r =>
            {
                var (z, g, s, t) = r.Vars<CellPartition, ServiceForm, RenderingSet, CosmosServiceTest>("z", "g", "s", "t");
                var ev = r.Var<ValueSet<string>>("ev");
                r.When(Need.Of(z, g), Under.Of(s), ev.Is(MeasuredCites.Of(t, CosmosValueClass.None, s)));
                r.Where(Primitives.Contains<CosmosServiceTest>().Of(RenderingPrimitives.FormLeansOn.Of(g), t));
                r.Then(LicenceRows.Of(g).Is(ev));
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

            DefineReads(parted);
        }

        // The rest of what a leaf reads beside its table (LeafReads, G1), each from rows through the comparators the set's
        // string order or the test's entry names, or the targets the set serves: the leaf tests what they hand it on a cell.
        void DefineReads(Ruleset parted)
        {
            // The emulator's order of what it stores moves U+0000 (Service.StringOrderIsCodePoint's moves).
            parted.DefineRule("a band the set's string comparator moves parts every string literal over a path whose strings may hold it", r =>
            {
                var (z, e, p, a) = r.Vars<CellPartition, Pred, JPath, PredicateAtom>("z", "e", "p", "a");
                var (f, g) = r.Vars<ServiceForm, ServiceForm>("f", "g");
                var (k, u, l) = r.Vars<ValueSet<string>, string, string>("k", "u", "l");
                var (s, o, c) = r.Vars<RenderingSet, ValueSet<CosmosAnswer>, CosmosAnswer>("s", "o", "c");
                r.When(Need.Of(z, g), z.Is(Partition.Of(e, f)), Ir.Atom.Of(e, p, a), k.Is(ImportedChars.Of(p)), Under.Of(s), o.Is(Final.Of(StringOrder, s)), Moves.Of(c, u));
                r.Where(Primitives.Contains<CosmosAnswer>().Of(o, c));
                r.Where(RenderingPrimitives.StringComparison.Of(g));
                r.When(l.Is(Compute.Of(g, x => x.Literal ?? "")));
                r.Where(Primitives.Contains<string>().Of(k, u));
                r.Then(PartedLiterals.Of(p).Is(Primitives.Singleton<string>().Of(l)));
            });

            // So does a literal holding it, against a range whose strings hold none: no written bound says where the stored
            // order puts it. A point is the comparator's, which compares the spelling it is sent (TheEmulatorEscapesUFDD0Too).
            parted.DefineRule("a string literal holding a band the set's string comparator moves is parted over the path it is compared with", r =>
            {
                var (z, e, p, a) = r.Vars<CellPartition, Pred, JPath, PredicateAtom>("z", "e", "p", "a");
                var (f, g) = r.Vars<ServiceForm, ServiceForm>("f", "g");
                var (b, u, l) = r.Vars<ValueSet<string>, string, string>("b", "u", "l");
                var (s, o, c) = r.Vars<RenderingSet, ValueSet<CosmosAnswer>, CosmosAnswer>("s", "o", "c");
                r.When(Need.Of(z, g), z.Is(Partition.Of(e, f)), Ir.Atom.Of(e, p, a), Under.Of(s), o.Is(Final.Of(StringOrder, s)), Moves.Of(c, u));
                r.Where(Primitives.Contains<CosmosAnswer>().Of(o, c));
                r.Where(RenderingPrimitives.StringComparison.Of(g));
                r.When(l.Is(Compute.Of(g, x => x.Literal ?? "")), b.Is(Compute.Of(g, CharactersOfLiteral)));
                r.Where(Primitives.Contains<string>().Of(b, u));
                r.Then(PartedLiterals.Of(p).Is(Primitives.Singleton<string>().Of(l)));
            });

            // A set sent its stored spelling sends every literal so, which a comparator of its order that reads another spelling
            // compares as it arrives, respelled (a set serving Azure beside the emulator).
            parted.DefineRule("a string literal holding a band its set respells lies anywhere under a comparator of its string order whose spelling keeps the band", r =>
            {
                var (z, g, s) = r.Vars<CellPartition, ServiceForm, RenderingSet>("z", "g", "s");
                var (o, rs, rw, b) = r.Vars<ValueSet<CosmosAnswer>, ValueSet<string>, ValueSet<string>, ValueSet<string>>("o", "rs", "rw", "b");
                var (c, d, mn) = r.Vars<CosmosAnswer, CosmosAnswer, AnswerMeanings>("c", "d", "mn");
                var (u, l) = r.Vars<string, string>("u", "l");
                r.When(Need.Of(z, g), Under.Of(s), o.Is(Final.Of(StringOrder, s)), rs.Is(Respelled.Of(s)), rw.Is(RewrittenBy.Of(c)), mn.Is(Means.Of()), Rewrites.Of(d, u));
                r.Where(Primitives.Contains<CosmosAnswer>().Of(o, c));
                r.Where(Primitives.Contains<CosmosAnswer>().Of(Compute.Of(mn, x => x.Comparators), c));
                r.Where(Primitives.Contains<string>().Of(rs, u));
                r.Where(Primitives.IsEmpty<string>().Of(Primitives.Intersect<string>().Of(rw, Primitives.Singleton<string>().Of(u))));
                r.Where(RenderingPrimitives.StringComparison.Of(g));
                r.When(l.Is(Compute.Of(g, x => x.Literal ?? "")), b.Is(Compute.Of(g, CharactersOfLiteral)));
                r.Where(Primitives.Contains<string>().Of(b, u));
                r.Then(UnplacedLiterals.Of(g).Is(Primitives.Singleton<string>().Of(l)));
            });

            // Azure stores a lone surrogate it is sent escaped as U+FFFD (Service.LoneSurrogateIsStoredAsReplacement's storedas):
            // a point is a value as written, so one holding the class says nothing of where the stored string lies.
            parted.DefineRule("a string comparison's points are unkept over a class a target the set serves stores as another, where its path's strings may hold it", r =>
            {
                var (z, e, p, a) = r.Vars<CellPartition, Pred, JPath, PredicateAtom>("z", "e", "p", "a");
                var (f, g, s) = r.Vars<ServiceForm, ServiceForm, RenderingSet>("f", "g", "s");
                var (d, t, k) = r.Vars<ValueSet<RenderingTarget>, RenderingTarget, ValueSet<string>>("d", "t", "k");
                var (u, w) = r.Vars<string, string>("u", "w");
                r.When(Need.Of(z, g), z.Is(Partition.Of(e, f)), Ir.Atom.Of(e, p, a), k.Is(ImportedChars.Of(p)), Under.Of(s), d.Is(Serves.Of(s)), StoredAs.Of(t, u, w));
                r.Where(Primitives.Contains<RenderingTarget>().Of(d, t));
                r.Where(RenderingPrimitives.StringComparison.Of(g));
                r.Where(Primitives.Contains<string>().Of(k, u));
                r.Then(Unkept.Of(g).Is(Primitives.Singleton<string>().Of(u)));
            });

            // A pair's places each hold their own characters, which the parts rows meet one band a side (h11, F3).
            parted.DefineRule("a pair departs where its places may hold two bands the set's string comparator ranks apart", r =>
            {
                var (z, e, p, a) = r.Vars<CellPartition, Pred, JPath, PredicateAtom>("z", "e", "p", "a");
                var (f, g, x, y) = r.Vars<ServiceForm, ServiceForm, JPath, JPath>("f", "g", "x", "y");
                var (kx, ky, u, w) = r.Vars<ValueSet<string>, ValueSet<string>, string, string>("kx", "ky", "u", "w");
                var (s, o, c) = r.Vars<RenderingSet, ValueSet<CosmosAnswer>, CosmosAnswer>("s", "o", "c");
                r.When(Need.Of(z, g), z.Is(Partition.Of(e, f)), Ir.Atom.Of(e, p, a), p.Is(Paired.Of(x, y)), kx.Is(ImportedChars.Of(x)), ky.Is(ImportedChars.Of(y)),
                    Under.Of(s), o.Is(Final.Of(StringOrder, s)), Parts.Of(c, u, w));
                r.Where(Primitives.Contains<CosmosAnswer>().Of(o, c));
                r.Where(Primitives.Contains<string>().Of(kx, u));
                r.Where(Primitives.Contains<string>().Of(ky, w));
                r.Then(Departs.Of(g).Is(Primitives.Singleton<(string, string)>().Of(Compute.Of(u, w, (m, n) => (m, n)))));
            });

            parted.DefineRule("a pair whose places may hold a band the set's string comparator moves lies anywhere where a side holds it", r =>
            {
                var (z, e, p, a) = r.Vars<CellPartition, Pred, JPath, PredicateAtom>("z", "e", "p", "a");
                var (f, g, x, y) = r.Vars<ServiceForm, ServiceForm, JPath, JPath>("f", "g", "x", "y");
                var (k, u) = r.Vars<ValueSet<string>, string>("k", "u");
                var (s, o, c) = r.Vars<RenderingSet, ValueSet<CosmosAnswer>, CosmosAnswer>("s", "o", "c");
                r.When(Need.Of(z, g), z.Is(Partition.Of(e, f)), Ir.Atom.Of(e, p, a), p.Is(Paired.Of(x, y)), k.Is(ImportedChars.Of(p)), Under.Of(s), o.Is(Final.Of(StringOrder, s)),
                    Moves.Of(c, u));
                r.Where(Primitives.Contains<CosmosAnswer>().Of(o, c));
                r.Where(Primitives.Contains<string>().Of(k, u));
                r.Then(Moved.Of(g).Is(Primitives.Singleton<string>().Of(u)));
            });

            parted.DefineRule("a pair's points are unkept over a class a target the set serves stores as another, where its places may hold it", r =>
            {
                var (z, e, p, a) = r.Vars<CellPartition, Pred, JPath, PredicateAtom>("z", "e", "p", "a");
                var (f, g, x, y) = r.Vars<ServiceForm, ServiceForm, JPath, JPath>("f", "g", "x", "y");
                var (s, d, t, k) = r.Vars<RenderingSet, ValueSet<RenderingTarget>, RenderingTarget, ValueSet<string>>("s", "d", "t", "k");
                var (u, w) = r.Vars<string, string>("u", "w");
                r.When(Need.Of(z, g), z.Is(Partition.Of(e, f)), Ir.Atom.Of(e, p, a), p.Is(Paired.Of(x, y)), k.Is(ImportedChars.Of(p)), Under.Of(s), d.Is(Serves.Of(s)),
                    StoredAs.Of(t, u, w));
                r.Where(Primitives.Contains<RenderingTarget>().Of(d, t));
                r.Where(Primitives.Contains<string>().Of(k, u));
                r.Then(Unkept.Of(g).Is(Primitives.Singleton<string>().Of(u)));
            });

            // Azure's CONTAINS, and the LIKE of one run it answers as one, read a string only to its first U+0000
            // (Service.NulEndsWhatReplaceAndContainsRead's truncates).
            parted.DefineRule("an affix is unread over a band its test reads a string only up to on a target the set serves, its pattern of the row's shape", r =>
            {
                var (z, g, s) = r.Vars<CellPartition, ServiceForm, RenderingSet>("z", "g", "s");
                var (d, t) = r.Vars<ValueSet<RenderingTarget>, RenderingTarget>("d", "t");
                var (x, sh, u) = r.Vars<CosmosServiceTest, string, string>("x", "sh", "u");
                var lu = r.Var<ValueSet<(LikeUnit, string)>>("lu");
                r.When(Need.Of(z, g), Under.Of(s), d.Is(Serves.Of(s)), Truncates.Of(t, x, sh, u), x.Is(Compute.Of(g, f => f.Test)), lu.Is(LikesOf.Of(s)),
                    sh.Is(Compute.Of(g, lu, AffixShapeOf)));
                r.Where(Primitives.Contains<RenderingTarget>().Of(d, t));
                r.Then(Unread.Of(g).Is(Primitives.Singleton<string>().Of(u)));
            });

            // A search holding the band matches every string.
            parted.DefineRule("an affix whose pattern holds a band its test reads a string only up to reads no string", r =>
            {
                var (z, g, s) = r.Vars<CellPartition, ServiceForm, RenderingSet>("z", "g", "s");
                var (d, t, b) = r.Vars<ValueSet<RenderingTarget>, RenderingTarget, ValueSet<string>>("d", "t", "b");
                var (x, sh, u) = r.Vars<CosmosServiceTest, string, string>("x", "sh", "u");
                var lu = r.Var<ValueSet<(LikeUnit, string)>>("lu");
                r.When(Need.Of(z, g), Under.Of(s), d.Is(Serves.Of(s)), Truncates.Of(t, x, sh, u), x.Is(Compute.Of(g, f => f.Test)), lu.Is(LikesOf.Of(s)),
                    sh.Is(Compute.Of(g, lu, AffixShapeOf)), b.Is(Compute.Of(g, CharactersOfPattern)));
                r.Where(Primitives.Contains<RenderingTarget>().Of(d, t));
                r.Where(Primitives.Contains<string>().Of(b, u));
                r.Then(Blind.Of(g).Is(Primitives.Singleton<string>().Of(u)));
            });

            // Azure stores a lone surrogate as U+FFFD (Service.LoneSurrogateIsStoredAsReplacement's storedas).
            parted.DefineRule("an affix whose pattern holds what a target the set serves stores a band as is unread over that band", r =>
            {
                var (z, g, s) = r.Vars<CellPartition, ServiceForm, RenderingSet>("z", "g", "s");
                var (d, t, b) = r.Vars<ValueSet<RenderingTarget>, RenderingTarget, ValueSet<string>>("d", "t", "b");
                var (u, w) = r.Vars<string, string>("u", "w");
                r.When(Need.Of(z, g), Under.Of(s), d.Is(Serves.Of(s)), StoredAs.Of(t, u, w), b.Is(Compute.Of(g, CharactersOfPattern)));
                r.Where(Primitives.Contains<RenderingTarget>().Of(d, t));
                r.Where(Primitives.Contains<string>().Of(b, w));
                r.Then(Unread.Of(g).Is(Primitives.Singleton<string>().Of(u)));
            });

            // The emulator matches what it stores against what it is sent (Service.EmulatorComparesTheSpellingItIsSent's rewrites).
            parted.DefineRule("an affix whose pattern its matcher's spelling splits is uncut over a band that spelling rewrites", r =>
            {
                var (z, g, s) = r.Vars<CellPartition, ServiceForm, RenderingSet>("z", "g", "s");
                var (k, o) = r.Vars<MeasuredKey, ValueSet<CosmosAnswer>>("k", "o");
                var (c, u) = r.Vars<CosmosAnswer, string>("c", "u");
                var lu = r.Var<ValueSet<(LikeUnit, string)>>("lu");
                r.When(Need.Of(z, g), Under.Of(s), k.Is(Compute.Of(g, MatchKeyOf)), o.Is(Final.Of(k, s)), Rewrites.Of(c, u), lu.Is(LikesOf.Of(s)));
                r.Where(Primitives.Contains<CosmosAnswer>().Of(o, c));
                r.Where(RenderingPrimitives.SpellingSplits.Of(g, c, lu));
                r.Then(Uncut.Of(g).Is(Primitives.Singleton<string>().Of(u)));
            });

            // The spelling is the set's and the matcher the test's: a pattern holding a band the two read apart is matched as
            // neither evaluates it, sent respelled to a matcher whose spelling keeps the band, or as written to one that
            // rewrites it.
            parted.DefineRule("an affix whose pattern holds a band its set respells reads no string under a matcher of its test whose spelling keeps the band", r =>
            {
                var (z, g, s) = r.Vars<CellPartition, ServiceForm, RenderingSet>("z", "g", "s");
                var (k, o, mn) = r.Vars<MeasuredKey, ValueSet<CosmosAnswer>, AnswerMeanings>("k", "o", "mn");
                var (rs, rw, b) = r.Vars<ValueSet<string>, ValueSet<string>, ValueSet<string>>("rs", "rw", "b");
                var (c, d, u) = r.Vars<CosmosAnswer, CosmosAnswer, string>("c", "d", "u");
                r.When(Need.Of(z, g), Under.Of(s), k.Is(Compute.Of(g, MatchKeyOf)), o.Is(Final.Of(k, s)), rs.Is(Respelled.Of(s)), rw.Is(RewrittenBy.Of(c)), mn.Is(Means.Of()),
                    Rewrites.Of(d, u), b.Is(Compute.Of(g, CharactersOfPattern)));
                r.Where(Primitives.Contains<CosmosAnswer>().Of(o, c));
                r.Where(Primitives.Contains<CosmosAnswer>().Of(Compute.Of(mn, x => x.Matchers), c));
                r.Where(Primitives.Contains<string>().Of(rs, u));
                r.Where(Primitives.IsEmpty<string>().Of(Primitives.Intersect<string>().Of(rw, Primitives.Singleton<string>().Of(u))));
                r.Where(Primitives.Contains<string>().Of(b, u));
                r.Then(Blind.Of(g).Is(Primitives.Singleton<string>().Of(u)));
            });

            parted.DefineRule("an affix whose pattern holds a band its matcher's spelling rewrites reads no string under a set that does not respell it", r =>
            {
                var (z, g, s) = r.Vars<CellPartition, ServiceForm, RenderingSet>("z", "g", "s");
                var (k, o, mn) = r.Vars<MeasuredKey, ValueSet<CosmosAnswer>, AnswerMeanings>("k", "o", "mn");
                var (rs, b) = r.Vars<ValueSet<string>, ValueSet<string>>("rs", "b");
                var (c, u) = r.Vars<CosmosAnswer, string>("c", "u");
                r.When(Need.Of(z, g), Under.Of(s), k.Is(Compute.Of(g, MatchKeyOf)), o.Is(Final.Of(k, s)), rs.Is(Respelled.Of(s)), mn.Is(Means.Of()), Rewrites.Of(c, u),
                    b.Is(Compute.Of(g, CharactersOfPattern)));
                r.Where(Primitives.Contains<CosmosAnswer>().Of(o, c));
                r.Where(Primitives.Contains<CosmosAnswer>().Of(Compute.Of(mn, x => x.Matchers), c));
                r.Where(Primitives.IsEmpty<string>().Of(Primitives.Intersect<string>().Of(rs, Primitives.Singleton<string>().Of(u))));
                r.Where(Primitives.Contains<string>().Of(b, u));
                r.Then(Blind.Of(g).Is(Primitives.Singleton<string>().Of(u)));
            });
        }

        void DefineEvaluation(RenderingDefinition definition, Ruleset evaluate)
        {
            // What a leaf reads beside its table is complete here, an earlier stratum's: a literal or band not among them is read
            // as not (LeafReads, G1).
            evaluate.DefineRule("a test is evaluated on every cell by the rows", r =>
            {
                var (z, e, p, a) = r.Vars<CellPartition, Pred, JPath, PredicateAtom>("z", "e", "p", "a");
                var (f, g, s) = r.Vars<ServiceForm, ServiceForm, RenderingSet>("f", "g", "s");
                var (q, tv) = r.Vars<ValueSet<string>, ServiceTable>("q", "tv");
                var (un, uk, ur, uc) = r.Vars<ValueSet<string>, ValueSet<string>, ValueSet<string>, ValueSet<string>>("un", "uk", "ur", "uc");
                var (bl, de, mv) = r.Vars<ValueSet<string>, ValueSet<(string, string)>, ValueSet<string>>("bl", "de", "mv");
                var (lic, lr) = r.Vars<ValueSet<CosmosServiceTest>, ValueSet<string>>("lic", "lr");
                r.When(Need.Of(z, g), z.Is(Partition.Of(e, f)), Ir.Atom.Of(e, p, a), q.Is(PartedLiterals.Of(p)), Under.Of(s), tv.Is(View.Of(s)),
                    un.Is(UnplacedLiterals.Of(g)), uk.Is(Unkept.Of(g)), ur.Is(Unread.Of(g)), uc.Is(Uncut.Of(g)), bl.Is(Blind.Of(g)), de.Is(Departs.Of(g)), mv.Is(Moved.Of(g)),
                    lic.Is(Licensed.Of(s)), lr.Is(LicenceRows.Of(g)));
                r.Where(RenderingPrimitives.FormIsLeaf.Of(g));
                // A leaf whose reading leans on a test's licence is read only under a set whose rows license it (G3).
                r.Where(Primitives.IsSubsetOf<CosmosServiceTest>().Of(RenderingPrimitives.FormLeansOn.Of(g), lic));
                var reads = Compute.Of(Compute.Of(q, un, uk, LeafReads.Places), Compute.Of(ur, uc, bl, LeafReads.Matches), Compute.Of(de, mv, lr, LeafReads.Pairs), LeafReads.Of);
                r.Then(Svc.Of(z, g).Is(Compute.Of(g, z, tv, reads, definition.Leaf)));
            });

            // A junction of three members or more is evaluated as its first and a regrouped rest rid of copies, which only
            // the set's licence makes the junction's own value; two members read their row as written.
            evaluate.DefineRule("a compound is evaluated from its parts by its connective's rows", r =>
            {
                var z = r.Var<CellPartition>("z");
                var (g, h1, h2, s) = r.Vars<ServiceForm, ServiceForm, ServiceForm, RenderingSet>("g", "h1", "h2", "s");
                var (v, w, tv) = r.Vars<OutcomeVector, OutcomeVector, ServiceTable>("v", "w", "tv");
                var (lic, nd) = r.Vars<ValueSet<CosmosServiceTest>, ValueSet<CosmosServiceTest>>("lic", "nd");
                r.When(Need.Of(z, g));
                r.Where(RenderingPrimitives.FormIsCompound.Of(g));
                r.When(h1.Is(Compute.Of(g, ServiceForm.First)), h2.Is(Compute.Of(g, ServiceForm.Rest)), v.Is(Svc.Of(z, h1)), w.Is(Svc.Of(z, h2)),
                    Under.Of(s), tv.Is(View.Of(s)), lic.Is(Licensed.Of(s)), nd.Is(Needs.Of(g)));
                r.Where(Primitives.IsSubsetOf<CosmosServiceTest>().Of(RenderingPrimitives.FormRegroups.Of(g), lic));
                r.Where(Primitives.IsSubsetOf<CosmosServiceTest>().Of(nd, lic));
                r.Then(Svc.Of(z, g).Is(Compute.Of(g, v, w, tv, definition.Compound)));
            });
        }

        void DefineGate(RenderingDefinition definition, Ruleset classes, Ruleset gate, Ruleset conflict)
        {
            // The cells' classes as both sides give them; what each is to the gate, and the rows it then rests on, are S's (G2).
            classes.DefineRule("Calcite's atom and the form are paired on every cell", r =>
            {
                var (e, p, a, f) = r.Vars<Pred, JPath, PredicateAtom, ServiceForm>("e", "p", "a", "f");
                var (pos, z, v) = r.Vars<RenderPosition, CellPartition, OutcomeVector>("pos", "z", "v");
                var (u, w) = r.Vars<RawTruths, ReadingTable>("u", "w");
                r.When(Candidate.Of(e, pos, f), Ir.Atom.Of(e, p, a), z.Is(Partition.Of(e, f)), v.Is(Svc.Of(z, f)), u.Is(RawUnion.Of()), w.Is(ReaderRows.Of()));
                r.Then(Outcomes.Of(e, f).Is(Compute.Of(a, u, v, w, definition.Pair)), Rests.Of(e, f).Is(ValueSet<string>.Empty));
            });

            classes.DefineRule("a candidate a cell of which holds no boolean rests on the rows that read a whole condition over one", r =>
            {
                var (e, f, o, mn) = r.Vars<Pred, ServiceForm, CellOutcomes, AnswerMeanings>("e", "f", "o", "mn");
                r.When(o.Is(Outcomes.Of(e, f)), mn.Is(Means.Of()));
                r.Where(RenderingPrimitives.HoldsNoBoolean.Of(o));
                r.Then(Rests.Of(e, f).Is(Compute.Of(mn, x => x.NonBooleanRows)));
            });

            // The universal over cells: the cells' classes are complete (lattice-free), and the allowed ones the theory's.
            gate.DefineRule("a candidate agrees where every class of its cells is allowed at its position", r =>
            {
                var (e, pos, f, v) = r.Vars<Pred, RenderPosition, ServiceForm, Verdict>("e", "pos", "f", "v");
                var (o, w, rs) = r.Vars<CellOutcomes, ValueSet<string>, ValueSet<string>>("o", "w", "rs");
                var (kk, y, ro) = r.Vars<ValueSet<CellClass>, ValueSet<CellClass>, ValueSet<CellClass>>("kk", "y", "ro");
                r.When(Candidate.Of(e, pos, f), o.Is(Outcomes.Of(e, f)), rs.Is(Rests.Of(e, f)), kk.Is(Compute.Of(o, x => x.Classes)), y.Is(Allowed.Of(pos, v)),
                    w.Is(AllowedCites.Of(pos, v)), ro.Is(RaiseOnly.Of()));
                r.Where(Primitives.IsSubsetOf<CellClass>().Of(kk, y));
                r.Then(Agrees.Of(e, pos, f, v).Is(Compute.Of(Compute.Of(o, rs, CellOutcomes.Resting), w, Proof.Of)), Vacuous.Of(e, pos, f, v).Is(Primitives.IsSubsetOf<CellClass>().Of(kk, ro)));
            });

            gate.DefineRule("a candidate is refuted where a class of its cells is a definite counterexample", r =>
            {
                var (e, pos, f, v) = r.Vars<Pred, RenderPosition, ServiceForm, Verdict>("e", "pos", "f", "v");
                var (o, p, a) = r.Vars<CellOutcomes, JPath, PredicateAtom>("o", "p", "a");
                var (kk, y, m) = r.Vars<ValueSet<CellClass>, ValueSet<CellClass>, ValueSet<CellClass>>("kk", "y", "m");
                var rs = r.Var<ValueSet<string>>("rs");
                r.When(Candidate.Of(e, pos, f), o.Is(Outcomes.Of(e, f)), rs.Is(Rests.Of(e, f)), Ir.Atom.Of(e, p, a), kk.Is(Compute.Of(o, x => x.Classes)), y.Is(Refutes.Of(pos, v)),
                    m.Is(Primitives.Intersect<CellClass>().Of(kk, y)));
                r.Where(RenderingPrimitives.SetNonempty.Of(m));
                var refutation = Compute.Of(Compute.Of(o, rs, CellOutcomes.Resting), m, Refutation.Of);
                r.Then(Disagrees.Of(e, pos, f, v).Is(refutation), RefutedAt.Of(Compute.Of(refutation, x => x.Cells), p));
            });

            gate.DefineRule("an atom is a connective's identity on some cell", r =>
            {
                var (e, f, j) = r.Vars<Pred, ServiceForm, CosmosServiceTest>("e", "f", "j");
                var o = r.Var<CellOutcomes>("o");
                var (kk, y, m) = r.Vars<ValueSet<CellClass>, ValueSet<CellClass>, ValueSet<CellClass>>("kk", "y", "m");
                r.When(o.Is(Outcomes.Of(e, f)), kk.Is(Compute.Of(o, x => x.Classes)), y.Is(IdentityClasses.Of(j)), m.Is(Primitives.Intersect<CellClass>().Of(kk, y)));
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

            // A set sent as written respells nothing, so every place it reads is writable; the stored spelling writes a name
            // holding neither character it rewrites (Service.EmulatorComparesTheSpellingItIsSent's rewrites, G1).
            spell.DefineRule("a place is writable under a set whose spelling rewrites no band its names hold", r =>
            {
                var (p, s) = r.Vars<JPath, RenderingSet>("p", "s");
                var (k, w) = r.Vars<ValueSet<string>, ValueSet<string>>("k", "w");
                r.When(k.Is(NameChars.Of(p)), Under.Of(s), w.Is(Respelled.Of(s)));
                r.Where(Primitives.IsEmpty<string>().Of(Primitives.Intersect<string>().Of(k, w)));
                r.Then(Writable.Of(p));
            });

            spell.DefineRule("a template that fits an atom proposes its form at the atom's position", r =>
            {
                var (e, p, a, pos) = r.Vars<Pred, JPath, PredicateAtom, RenderPosition>("e", "p", "a", "pos");
                var (t, n, sh) = r.Vars<TemplateId, string, string>("t", "n", "sh");
                var rr = r.Var<ReadingTable>("rr");
                r.When(At.Of(e, pos), Ir.Atom.Of(e, p, a), Writable.Of(p), n.Is(Spelling.Of(p)), Template.Of(t, pos), sh.Is(Compute.Of(a, RenderingTemplates.ShapeOf)), Fits.Of(t, sh),
                    rr.Is(ReaderRows.Of()));
                r.Then(Candidate.Of(e, pos, Compute.Of(t, pos, Compute.Of(a, rr, RenderingTemplates.Read), n, RenderingTemplates.Spell)));
            });
        }

        // The gate's agreements, complete here, lifted where they rest on something and may be sent: an exact one offered at
        // its price, an implied one the Rex builder writes as a member. Presence through calls is three rules over rows.
        void DefineLifts(Ruleset lift, Ruleset strict)
        {
            // May be sent: no literal holds a class no parameter is sent holding, and no string ordering's literal a band the set's
            // comparators rank apart, so that nothing S sends is one bound of a range the two orders rank apart (G1).
            lift.DefineRule("an exact agreement that rests on something and may be sent is a best candidate", r =>
            {
                var (e, pos, f) = r.Vars<Pred, RenderPosition, ServiceForm>("e", "pos", "f");
                var (s, ub, pb) = r.Vars<RenderingSet, ValueSet<string>, ValueSet<string>>("s", "ub", "pb");
                var (lc, oc) = r.Vars<ValueSet<string>, ValueSet<string>>("lc", "oc");
                r.When(At.Of(e, pos), Fact.Exists(Agrees.Of(e, pos, f, Verdict.Equal)), Vacuous.Of(e, pos, f, Verdict.Equal).Is(false), Under.Of(s), ub.Is(Unsendable.Of(s)),
                    pb.Is(PartingBands.Of(s)), lc.Is(Compute.Of(f, LiteralClasses)), oc.Is(Compute.Of(f, OrderedLiteralClasses)));
                r.Where(Primitives.IsEmpty<string>().Of(Primitives.Intersect<string>().Of(lc, ub)));
                r.Where(Primitives.IsEmpty<string>().Of(Primitives.Intersect<string>().Of(oc, pb)));
                r.Then(BestExact.Of(e, pos).Is(Compute.Of(f, Priced.Of)));
            });

            lift.DefineRule("an implied agreement that rests on something, may be sent and can be written as a Rex is implied", r =>
            {
                var (e, pos, f) = r.Vars<Pred, RenderPosition, ServiceForm>("e", "pos", "f");
                var (p, a) = r.Vars<JPath, PredicateAtom>("p", "a");
                var (s, ub, pb, g) = r.Vars<RenderingSet, ValueSet<string>, ValueSet<string>, RenderingRexGrammar.Value>("s", "ub", "pb", "g");
                var (lc, oc) = r.Vars<ValueSet<string>, ValueSet<string>>("lc", "oc");
                r.When(At.Of(e, pos), Fact.Exists(Agrees.Of(e, pos, f, Verdict.Implies)), Vacuous.Of(e, pos, f, Verdict.Implies).Is(false), Ir.Atom.Of(e, p, a),
                    Under.Of(s), ub.Is(Unsendable.Of(s)), pb.Is(PartingBands.Of(s)), lc.Is(Compute.Of(f, LiteralClasses)), oc.Is(Compute.Of(f, OrderedLiteralClasses)), g.Is(RexGrammar.Of()));
                r.Where(Primitives.IsEmpty<string>().Of(Primitives.Intersect<string>().Of(lc, ub)));
                r.Where(Primitives.IsEmpty<string>().Of(Primitives.Intersect<string>().Of(oc, pb)));
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

        // An exact form composes up from the arms' bests under the licence, their junction as they stand; a Min value only
        // lowers and priced-is-real only turns true, so whatever it composed from is exact. A junction of its own connective,
        // a part twice or a constant within is the regroup and units rules' to rewrite, as the else rules' is (G4), and the
        // price keeps the form they rewrite to. The weakening reads every best and implied set complete: a leaf takes its best
        // where that is a member, or what its members make together.
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
                    h.Is(Compute.Of(j, l, m, ServiceForm.Paired)));
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
        void DefineValued(Ruleset disagree, Ruleset reading, Ruleset place, Ruleset image, Ruleset assume)
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
                r.When(ans.Is(FunctionAnswer.Of(key, x)), ans.IsNot(CosmosFunctionAnswer.Same), ans.IsNot(CosmosFunctionAnswer.Defined), d.Is(Serves.Of(st)),
                    kd.Is(Compute.Of(key, FunctionKey.KindOf)), c.Is(Compute.Of(key, FunctionKey.ValueOf)), ValueClassHolds.Of(kd, c, a));
                r.Where(Primitives.Contains<RenderingTarget>().Of(d, x));
                r.Then(DisagreeingKeys.Of(st, Compute.Of(key, FunctionKey.FunctionOf)).Is(Primitives.Singleton<FunctionKey>().Of(Compute.Of(key, a, FunctionKey.WithValue))));
            });

            // What Calcite answers over a key, a value or null, as its row states, and the keys the service defines alone,
            // each over the atoms its class holds as the measured keys are (G6).
            disagree.DefineRule("a function key Calcite answers a value or null over covers each atom its class holds", r =>
            {
                var key = r.Var<FunctionKey>("key");
                var y = r.Var<CalciteYield>("y");
                var kd = r.Var<CosmosValueClass>("kd");
                var (c, a) = r.Vars<FunctionValueClass, FunctionValueClass>("c", "a");
                r.When(y.Is(FunctionYield.Of(key)), kd.Is(Compute.Of(key, FunctionKey.KindOf)), c.Is(Compute.Of(key, FunctionKey.ValueOf)), ValueClassHolds.Of(kd, c, a));
                r.Then(YieldKeys.Of(Compute.Of(key, FunctionKey.FunctionOf), y).Is(Primitives.Singleton<FunctionKey>().Of(Compute.Of(key, a, FunctionKey.WithValue))));
            });

            disagree.DefineRule("a function key the service defines covers each atom its class holds", r =>
            {
                var (key, x) = r.Vars<FunctionKey, RenderingTarget>("key", "x");
                var kd = r.Var<CosmosValueClass>("kd");
                var (c, a) = r.Vars<FunctionValueClass, FunctionValueClass>("c", "a");
                var ans = r.Var<CosmosFunctionAnswer>("ans");
                r.When(ans.Is(FunctionAnswer.Of(key, x)), ans.Is(CosmosFunctionAnswer.Defined), kd.Is(Compute.Of(key, FunctionKey.KindOf)), c.Is(Compute.Of(key, FunctionKey.ValueOf)), ValueClassHolds.Of(kd, c, a));
                r.Then(DefinedKeys.Of(Compute.Of(key, FunctionKey.FunctionOf)).Is(Primitives.Singleton<FunctionKey>().Of(Compute.Of(key, a, FunctionKey.WithValue))));
            });

            // Which name a call's rows are filed under is the rows': its operator's at its type where they name one, its own
            // where they name it alone (G6), each call's key and absence complete before any place is gathered.
            reading.DefineRule("a call of an operator its rows name at a type is keyed by that name at its type", r =>
            {
                var (v, args) = r.Vars<Val, Args>("v", "args");
                var (n, t, kn) = r.Vars<string, string, string>("n", "t", "kn");
                r.When(v.Is(Ir.Call.Of(n, args)), t.Is(SqlType.Of(v)), kn.Is(RowName.Of(n, t)));
                r.Then(CallKey.Of(v).Is(kn), HeldAbsent.Of(v).Is(ValueSet<string>.Empty));
            });

            reading.DefineRule("a call of an operator its rows name alone is keyed by its name", r =>
            {
                var (v, args) = r.Vars<Val, Args>("v", "args");
                var (n, t) = r.Vars<string, string>("n", "t");
                r.When(v.Is(Ir.Call.Of(n, args)), t.Is(SqlType.Of(v)), RowNamedAlone.Of(n));
                r.Then(CallKey.Of(v).Is(n), HeldAbsent.Of(v).Is(ValueSet<string>.Empty));
            });

            // Which spelling a call takes, where its function has more than one, is the least its operands let it (G6): one
            // every call takes, and one a literal at an ordinal lets it take where it is a whole number of the class the
            // spelling asks, a window's start of one or more. No image reads it; the printer and the translator write it.
            reading.DefineRule("a call takes a spelling every call of its function takes", r =>
            {
                var (v, args) = r.Vars<Val, Args>("v", "args");
                var n = r.Var<string>("n");
                var k = r.Var<long>("k");
                r.When(v.Is(Ir.Call.Of(n, args)), SpelledAlways.Of(n, k));
                r.Then(CallSpelling.Of(v).Is(k));
            });

            reading.DefineRule("a call takes a spelling its literal at an ordinal lets it, a whole number of the class the spelling asks", r =>
            {
                var (v, w) = r.Vars<Val, Val>("v", "w");
                var args = r.Var<Args>("args");
                var (n, c) = r.Vars<string, string>("n", "c");
                var (k, i) = r.Vars<long, long>("k", "i");
                var a = r.Var<FunctionValueClass>("a");
                r.When(v.Is(Ir.Call.Of(n, args)), SpelledWhere.Of(n, k, i, a), CallArgAt.Of(v, i, w), w.Is(Ir.Literal.Of(c)));
                r.Where(RenderingPrimitives.WholeOfClass.Of(a, c));
                r.Then(CallSpelling.Of(v).Is(k));
            });

            // An operand no row of its function keys - a window's start, a trim's character, a repetition's stored count -
            // says nothing of what Calcite answers over it, so the call may be null over it: absent beside its type's kinds
            // (G6). A literal is keyed where the rows key one apart. Read off the call's operands as the host writes them,
            // complete before any place is gathered.
            reading.DefineRule("an operand read at an ordinal its function's rows name no key of lets its call be absent", r =>
            {
                var (v, w) = r.Vars<Val, Val>("v", "w");
                var p = r.Var<JPath>("p");
                var (k, kn) = r.Vars<string, string>("k", "kn");
                var i = r.Var<long>("i");
                var sub = r.Var<ValueSet<long>>("sub");
                r.When(kn.Is(CallKey.Of(v)), sub.Is(FunctionSubjects.Of(kn)), CallArgAt.Of(v, i, w), Ir.Reads.Of(w, p, k));
                r.Where(Primitives.IsEmpty<long>().Of(Primitives.Intersect<long>().Of(sub, Primitives.Insert<long>().Of(Primitives.Singleton<long>().Of(i), FunctionKey.Every))));
                r.Then(HeldAbsent.Of(v).Is(ValueSet.Of(PathValues.AbsentKind)));
            });

            reading.DefineRule("a literal operand at an ordinal its function's rows name no key of, nor any literal of, lets its call be absent", r =>
            {
                var (v, w) = r.Vars<Val, Val>("v", "w");
                var (c, kn) = r.Vars<string, string>("c", "kn");
                var i = r.Var<long>("i");
                var sub = r.Var<ValueSet<long>>("sub");
                r.When(kn.Is(CallKey.Of(v)), sub.Is(FunctionSubjects.Of(kn)), CallArgAt.Of(v, i, w), w.Is(Ir.Literal.Of(c)));
                var keying = Primitives.Insert<long>().Of(Primitives.Insert<long>().Of(Primitives.Singleton<long>().Of(i), FunctionKey.Every), FunctionKey.Literal);
                r.Where(Primitives.IsEmpty<long>().Of(Primitives.Intersect<long>().Of(sub, keying)));
                r.Then(HeldAbsent.Of(v).Is(ValueSet.Of(PathValues.AbsentKind)));
            });

            place.DefineRule("a call's operands' imports are gathered beneath each stratum, one an ordinal", r =>
            {
                var (v, w) = r.Vars<Val, Val>("v", "w");
                var (q, p) = r.Vars<JPath, JPath>("q", "p");
                var (kq, k) = r.Vars<string, string>("kq", "k");
                var (i, s) = r.Vars<long, long>("i", "s");
                var (x, ch) = r.Vars<PathValues, ValueSet<string>>("x", "ch");
                r.When(Ir.Reads.Of(v, q, kq), q.Is(Valued.Of(v)), CallArgAt.Of(v, i, w), Ir.Reads.Of(w, p, k), x.Is(ArmImport.Of(s, p)), ch.Is(ArmChars.Of(s, p)));
                r.Then(Gathered.Of(s, v).Is(Primitives.Singleton<PathImport>().Of(Compute.Of(i, k, x, ch, PathImport.Of))));
            });

            // The two images of a measured call, apart on whether Calcite answers null over some key its operands fall under:
            // a value of its type over every one, the point where C# evaluates it as Calcite does, or absent beside. Each
            // reads the gathering complete, one import an ordinal, each read through a reader its rows were measured through or
            // exactly, and every key measured and agreeing on every target the set serves, its result's among them where its
            // rows key one: a repetition past the bound Azure repeats to has no image there (G6).
            void Measured(RuleBuilder r, bool absent)
            {
                var v = r.Var<Val>("v");
                var q = r.Var<JPath>("q");
                var (kq, t, kn) = r.Vars<string, string, string>("kq", "t", "kn");
                var (s, m) = r.Vars<long, long>("s", "m");
                var g = r.Var<ValueSet<PathImport>>("g");
                var st = r.Var<RenderingSet>("st");
                var (z, d, ks) = r.Vars<ValueSet<FunctionKey>, ValueSet<FunctionKey>, ValueSet<FunctionKey>>("z", "d", "ks");
                var (vk, nk) = r.Vars<ValueSet<FunctionKey>, ValueSet<FunctionKey>>("vk", "nk");
                var (rd, ex, tk, ha) = r.Vars<ValueSet<string>, ValueSet<string>, ValueSet<string>, ValueSet<string>>("rd", "ex", "tk", "ha");
                r.When(Ir.Reads.Of(v, q, kq), q.Is(Valued.Of(v)), kn.Is(CallKey.Of(v)), t.Is(SqlType.Of(v)), g.Is(Gathered.Of(s, v)), m.Is(Arity.Of(v)),
                    m.Is(Primitives.Count<PathImport>().Of(g)), m.Is(Primitives.Count<long>().Of(Compute.Of(g, PathImport.OrdinalsOf))), Under.Of(st),
                    z.Is(MeasuredKeys.Of(kn)), d.Is(DisagreeingKeys.Of(st, kn)), rd.Is(FunctionReaders.Of(kn)), ex.Is(LiteralReaders.Of()));
                r.When(vk.Is(YieldKeys.Of(kn, CalciteYield.Value)), nk.Is(YieldKeys.Of(kn, CalciteYield.Null)), tk.Is(TypeKinds.Of(t)), ha.Is(HeldAbsent.Of(v)));
                r.Where(Primitives.IsSubsetOf<string>().Of(Compute.Of(g, PathImport.ReadersOf), Primitives.Union<string>().Of(rd, ex)));
                // Its operands' keys, and its result's where the rows key one: a repetition by its length (G6).
                r.When(ks.Is(Primitives.Union<FunctionKey>().Of(Compute.Of(kn, g, FunctionSubjects.Of(kn), ex, RenderingImage.Keys),
                    Compute.Of(kn, g, FunctionSubjects.Of(kn), RenderingImage.ResultKeys))));
                r.Where(Primitives.IsSubsetOf<FunctionKey>().Of(ks, z));
                r.Where(Primitives.IsEmpty<FunctionKey>().Of(Primitives.Intersect<FunctionKey>().Of(ks, d)));
                r.Where(Primitives.IsSubsetOf<FunctionKey>().Of(ks, Primitives.Union<FunctionKey>().Of(vk, nk)));
                var kinds = Primitives.Union<string>().Of(tk, ha);
                if (absent)
                {
                    r.Where(RenderingPrimitives.Overlaps.Of(ks, nk));
                    r.Then(ArmImport.Of(s, q).Is(Compute.Of(Primitives.Insert<string>().Of(kinds, PathValues.AbsentKind), PathValues.OfKinds)));
                }
                else
                {
                    r.Where(Primitives.IsEmpty<FunctionKey>().Of(Primitives.Intersect<FunctionKey>().Of(ks, nk)));
                    r.Then(ArmImport.Of(s, q).Is(Compute.Of(kn, g, Compute.Of(kinds, PathValues.OfKinds), RenderingImage.Of)));
                }
            }

            image.DefineRule("a place's import beneath a stratum is its call's image, a value of its type, once every operand is in and every key agrees and is one Calcite answers a value over",
                r => Measured(r, absent: false));
            image.DefineRule("a place's import beneath a stratum is its type's kinds and absent, once every operand is in and every key agrees and Calcite answers null over one",
                r => Measured(r, absent: true));

            // A call the service defines alone (the D class) is what the service answers, Calcite having no body for it:
            // every kind of its type and absent, whatever its operands are read through, once each key they fall under is
            // one it defines (F3, G6).
            // Whatever the IR reads its operands through, or reads none: a call the service defines over every key any operand
            // may fall under, the D class (F3), is the service's alone, so its image needs no gathering (G6).
            image.DefineRule("a place's import beneath a stratum is its type's kinds and absent where the service defines every key any operands may fall under", r =>
            {
                var v = r.Var<Val>("v");
                var q = r.Var<JPath>("q");
                var (kq, t, kn) = r.Vars<string, string, string>("kq", "t", "kn");
                var (s, m) = r.Vars<long, long>("s", "m");
                var ch = r.Var<ValueSet<string>>("ch");
                var (dk, ks) = r.Vars<ValueSet<FunctionKey>, ValueSet<FunctionKey>>("dk", "ks");
                var tk = r.Var<ValueSet<string>>("tk");
                r.When(Ir.Reads.Of(v, q, kq), q.Is(Valued.Of(v)), kn.Is(CallKey.Of(v)), t.Is(SqlType.Of(v)), m.Is(Arity.Of(v)), ch.Is(ArmChars.Of(s, q)),
                    dk.Is(DefinedKeys.Of(kn)), tk.Is(TypeKinds.Of(t)));
                r.When(ks.Is(Compute.Of(kn, m, FunctionSubjects.Of(kn), LiteralReaders.Of(), RenderingImage.AnyKeys)));
                r.Where(Primitives.IsSubsetOf<FunctionKey>().Of(ks, dk));
                r.Then(ArmImport.Of(s, q).Is(Compute.Of(Primitives.Insert<string>().Of(tk, PathValues.AbsentKind), PathValues.OfKinds)));
            });
            image.DefineRule("a place's import beneath a stratum is its type's kinds and absent, once every operand is in and every key is one the service defines", r =>
            {
                var v = r.Var<Val>("v");
                var q = r.Var<JPath>("q");
                var (kq, t, kn) = r.Vars<string, string, string>("kq", "t", "kn");
                var (s, m) = r.Vars<long, long>("s", "m");
                var g = r.Var<ValueSet<PathImport>>("g");
                var (dk, ks) = r.Vars<ValueSet<FunctionKey>, ValueSet<FunctionKey>>("dk", "ks");
                var tk = r.Var<ValueSet<string>>("tk");
                r.When(Ir.Reads.Of(v, q, kq), q.Is(Valued.Of(v)), kn.Is(CallKey.Of(v)), t.Is(SqlType.Of(v)), g.Is(Gathered.Of(s, v)), m.Is(Arity.Of(v)),
                    m.Is(Primitives.Count<PathImport>().Of(g)), m.Is(Primitives.Count<long>().Of(Compute.Of(g, PathImport.OrdinalsOf))), dk.Is(DefinedKeys.Of(kn)), tk.Is(TypeKinds.Of(t)));
                r.When(ks.Is(Compute.Of(kn, g, FunctionSubjects.Of(kn), LiteralReaders.Of(), RenderingImage.Keys)));
                r.Where(Primitives.IsSubsetOf<FunctionKey>().Of(ks, dk));
                r.Then(ArmImport.Of(s, q).Is(Compute.Of(Primitives.Insert<string>().Of(tk, PathValues.AbsentKind), PathValues.OfKinds)));
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

            // A later round's atom is read beneath a stratum of the round's own, where its place may have an image the root
            // has none of (G6): the place's import joins it. Its absence is Calcite's null only beneath that stratum, whose
            // keys agree there, so no strictness follows from it: NullWhereAbsent has no stratum, and every atom over the
            // place at every stratum would read it (the root rule's alone, which holds wherever the root's image does).
            image.DefineRule("a computed place's import joins its image beneath each stratum an atom over it is read at", r =>
            {
                var e = r.Var<Pred>("e");
                var (q, a) = (r.Var<JPath>("q"), r.Var<PredicateAtom>("a"));
                var v = r.Var<Val>("v");
                var s = r.Var<long>("s");
                var x = r.Var<PathValues>("x");
                r.When(Ir.Atom.Of(e, q, a), q.Is(Valued.Of(v)), s.Is(StratumId.Of(e)), x.Is(ArmImport.Of(s, q)));
                r.Then(Imported.Of(q).Is(x));
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

        // The service's case-insensitive STARTSWITH, ENDSWITH and CONTAINS match as the test over Calcite's UPPER or LOWER of
        // both sides, outside the bands their rows say nothing of (Service.AffixesMatchByCodePoint's foldsas, G6): a set's
        // fold is its every target's, settled at the frozen root, and a LIKE over a fold S images is that affix over the
        // fold's subject, case-insensitive, where the pattern is the narrowest affix of an argument the fold leaves as it is
        // and neither the subject nor the argument may hold a band the set's rows part at. Answering as the plain affix over
        // the place does: whether that says what Calcite does is the gate's (DESIGN.md, "A case fold under `LIKE` is the
        // service's case-insensitive match").
        void DefineFolds(Ruleset measure, Ruleset tabulate, Ruleset fold)
        {
            measure.DefineRule("a target a set serves whose rows fold a test's case-insensitive form is the set's, with the bands its row parts at", r =>
            {
                var (x, s, t) = r.Vars<RenderingTarget, RenderingSet, CosmosServiceTest>("x", "s", "t");
                var (f, b) = r.Vars<string, string>("f", "b");
                var d = r.Var<ValueSet<RenderingTarget>>("d");
                r.When(FoldsAs.Of(x, t, f, b), d.Is(Serves.Of(s)));
                r.Where(Primitives.Contains<RenderingTarget>().Of(d, x));
                r.Then(FoldTargets.Of(s, t, f).Is(Primitives.Singleton<RenderingTarget>().Of(x)), FoldParts.Of(s, t, f).Is(Primitives.Singleton<string>().Of(b)));
            });

            tabulate.DefineRule("a fold every target its set serves has rows of is the set's, outside the bands any parts at", r =>
            {
                var (s, t, f) = r.Vars<RenderingSet, CosmosServiceTest, string>("s", "t", "f");
                var (m, d) = r.Vars<ValueSet<RenderingTarget>, ValueSet<RenderingTarget>>("m", "d");
                var b = r.Var<ValueSet<string>>("b");
                r.When(m.Is(FoldTargets.Of(s, t, f)), d.Is(Serves.Of(s)), b.Is(FoldParts.Of(s, t, f)));
                r.Where(Primitives.IsSubsetOf<RenderingTarget>().Of(d, m));
                r.Then(Folds.Of(s, t, f).Is(b));
            });

            fold.DefineRule("a LIKE over a fold S images is the case-insensitive affix its set's rows match alike, over the fold's subject", r =>
            {
                var (e, v, w) = r.Vars<Pred, Val, Val>("e", "v", "w");
                var (q, p) = r.Vars<JPath, JPath>("q", "p");
                var (c, a, kn) = r.Vars<string, string, string>("c", "a", "kn");
                var (kq, k) = r.Vars<string, string>("kq", "k");
                var (i, s, t) = r.Vars<long, RenderingSet, CosmosServiceTest>("i", "s", "t");
                var (b, ch) = r.Vars<ValueSet<string>, ValueSet<string>>("b", "ch");
                var (sub, x) = r.Vars<ValueSet<long>, PathValues>("sub", "x");
                var rr = r.Var<ReadingTable>("rr");
                r.When(e.Is(Ir.Affix.Of(PredicateAtom.LikeTest, v, c)), Ir.Reads.Of(v, q, kq), q.Is(Valued.Of(v)), x.Is(Imported.Of(q)), kn.Is(CallKey.Of(v)),
                    sub.Is(FunctionSubjects.Of(kn)), CallArgAt.Of(v, i, w), Ir.Reads.Of(w, p, k), ch.Is(ImportedChars.Of(p)), Under.Of(s), rr.Is(ReaderRows.Of()));
                r.When(t.Is(Compute.Of(c, rr, AffixPattern.WrittenTest)), a.Is(Compute.Of(c, rr, AffixPattern.WrittenArgument)), b.Is(Folds.Of(s, t, kn)));
                r.Where(Primitives.Contains<long>().Of(sub, i));
                r.Where(RenderingPrimitives.FoldsToItself.Of(kn, a));
                r.Where(Primitives.IsEmpty<string>().Of(Primitives.Intersect<string>().Of(b, ch)));
                r.Where(Primitives.IsEmpty<string>().Of(Primitives.Intersect<string>().Of(b, Compute.Of(a, JsonValues.BandsOfText))));
                r.Then(FoldedAs.Of(e, t, a, i));
            });
        }

        // What a set's LIKE reads each unit of a pattern as (G7): a target's rows' where every target the set serves reads the
        // unit so, as a fold is settled; a unit the targets read apart is the set's by no row, so a pattern holding it is unread.
        void DefineLikes(Ruleset measure, Ruleset tabulate)
        {
            measure.DefineRule("a target a set serves whose rows read a unit of LIKE's pattern is the set's, by the row", r =>
            {
                var (x, s) = r.Vars<RenderingTarget, RenderingSet>("x", "s");
                var u = r.Var<LikeUnit>("u");
                var row = r.Var<string>("row");
                var d = r.Var<ValueSet<RenderingTarget>>("d");
                r.When(LikeReads.Of(x, u, row), d.Is(Serves.Of(s)));
                r.Where(Primitives.Contains<RenderingTarget>().Of(d, x));
                r.Then(LikeTargets.Of(s, u, row).Is(Primitives.Singleton<RenderingTarget>().Of(x)));
            });

            tabulate.DefineRule("a unit of LIKE's pattern every target its set serves reads so is the set's, by the row", r =>
            {
                var s = r.Var<RenderingSet>("s");
                var u = r.Var<LikeUnit>("u");
                var row = r.Var<string>("row");
                var (m, d) = r.Vars<ValueSet<RenderingTarget>, ValueSet<RenderingTarget>>("m", "d");
                r.When(m.Is(LikeTargets.Of(s, u, row)), d.Is(Serves.Of(s)));
                r.Where(Primitives.IsSubsetOf<RenderingTarget>().Of(d, m));
                r.Then(LikesOf.Of(s).Is(Primitives.Singleton<(LikeUnit, string)>().Of(Compute.Of(u, row, LikeRead))));
            });
        }

        /// <summary>A unit of a set's pattern and the row it is read by, as <see cref="LikesOf"/> holds it.</summary>
        static (LikeUnit, string) LikeRead(LikeUnit unit, string row) => (unit, row);

        // The raw columns (h12, G5): what each column answers on a key, from its rows alone. A null test is what the column
        // reads the class as means to it; a comparison it compiles, over a class it reads as null or raises on, what that
        // read means; over a value of the literal's type, the comparison as the type, and over its cast's text, the text
        // compared, both of which the cell decides; across types, what the column says; one it does not compile fails the
        // statement. The union joins the columns, each column's reading keeping its own (rawtyped). Beside them, which raw
        // column reads as the text over a function's rows: one that, over every class the text reads, reads it as the text
        // does or raises.
        void DefineRaw(Ruleset raw, Ruleset readas)
        {
            raw.DefineRule("a raw reading's null test over a class is what its column reads the class as means to it", r =>
            {
                var (rd, c, a) = r.Vars<string, string, string>("rd", "c", "a");
                var k = r.Var<CosmosValueClass>("k");
                var t = r.Var<Truths>("t");
                r.When(a.Is(RawReads.Of(rd, c, k)), t.Is(ReadTruths.Of(CosmosServiceTest.IsNull, a)));
                r.Then(RawColumn.Of(Compute.Of(rd, k, (x, y) => RawKey.Of(x, y, CosmosServiceTest.IsNull, CosmosValueClass.None)), c).Is(Compute.Of(t, RawAnswer.Stated)));
            });

            raw.DefineRule("a raw reading's comparison over a class its column reads as null or raises on is what that read means to it", r =>
            {
                var (rd, c, a) = r.Vars<string, string, string>("rd", "c", "a");
                var (k, o) = r.Vars<CosmosValueClass, CosmosValueClass>("k", "o");
                var (t, tr) = r.Vars<CosmosServiceTest, Truths>("t", "tr");
                r.When(a.Is(RawReads.Of(rd, c, k)), RawCompiles.Of(rd, c, t).Is(true), RawBeside.Of(rd, c, o), tr.Is(ReadTruths.Of(t, a)));
                r.Then(RawColumn.Of(Compute.Of(rd, k, t, o, RawKey.Of), c).Is(Compute.Of(tr, RawAnswer.Stated)));
            });

            raw.DefineRule("a raw reading's comparison of a value with a literal of its type is the comparison as the type, which the cell decides", r =>
            {
                var (rd, c, ty) = r.Vars<string, string, string>("rd", "c", "ty");
                var (k, o) = r.Vars<CosmosValueClass, CosmosValueClass>("k", "o");
                var t = r.Var<CosmosServiceTest>("t");
                r.When(RawReads.Of(rd, c, k).Is(CalciteRead.Itself), RawAsType.Of(rd, c).Is(true), RawCompiles.Of(rd, c, t).Is(true), RawBeside.Of(rd, c, o),
                    ty.Is(ClassType.Of(k)), ty.Is(ClassType.Of(o)));
                r.Then(RawColumn.Of(Compute.Of(rd, k, t, o, RawKey.Of), c).Is(RawAnswer.OnTheCell));
            });

            raw.DefineRule("a raw reading's comparison of a value with a literal of another type is what its column answers across types", r =>
            {
                var (rd, c) = r.Vars<string, string>("rd", "c");
                var (tk, to) = r.Vars<string, string>("tk", "to");
                var (k, o) = r.Vars<CosmosValueClass, CosmosValueClass>("k", "o");
                var (t, tr) = r.Vars<CosmosServiceTest, Truths>("t", "tr");
                r.When(RawReads.Of(rd, c, k).Is(CalciteRead.Itself), RawAsType.Of(rd, c).Is(true), RawCompiles.Of(rd, c, t).Is(true), RawBeside.Of(rd, c, o),
                    tk.Is(ClassType.Of(k)), to.Is(ClassType.Of(o)), tk.IsNot(to), tr.Is(RawAcross.Of(rd, c, t)));
                r.Then(RawColumn.Of(Compute.Of(rd, k, t, o, RawKey.Of), c).Is(Compute.Of(tr, RawAnswer.Stated)));
            });

            raw.DefineRule("a cast of a raw value compares a value it reads as itself as itself, which the cell decides", r =>
            {
                var (rd, c) = r.Vars<string, string>("rd", "c");
                var (k, o) = r.Vars<CosmosValueClass, CosmosValueClass>("k", "o");
                var t = r.Var<CosmosServiceTest>("t");
                r.When(RawReads.Of(rd, c, k).Is(CalciteRead.Itself), RawAsType.Of(rd, c).Is(false), RawCompiles.Of(rd, c, t).Is(true), RawBeside.Of(rd, c, o));
                r.Then(RawColumn.Of(Compute.Of(rd, k, t, o, RawKey.Of), c).Is(RawAnswer.OnTheCell));
            });

            raw.DefineRule("a cast of a raw value compares the text it renders a value as, which the cell decides", r =>
            {
                var (rd, c) = r.Vars<string, string>("rd", "c");
                var (k, o) = r.Vars<CosmosValueClass, CosmosValueClass>("k", "o");
                var t = r.Var<CosmosServiceTest>("t");
                r.When(RawReads.Of(rd, c, k).Is(CalciteRead.Renders), RawAsType.Of(rd, c).Is(false), RawCompiles.Of(rd, c, t).Is(true), RawBeside.Of(rd, c, o));
                r.Then(RawColumn.Of(Compute.Of(rd, k, t, o, RawKey.Of), c).Is(RawAnswer.OnTheCell));
            });

            raw.DefineRule("a raw reading's comparison its column does not compile fails the statement", r =>
            {
                var (rd, c, a) = r.Vars<string, string, string>("rd", "c", "a");
                var (k, o) = r.Vars<CosmosValueClass, CosmosValueClass>("k", "o");
                var t = r.Var<CosmosServiceTest>("t");
                r.When(a.Is(RawReads.Of(rd, c, k)), RawCompiles.Of(rd, c, t).Is(false), RawBeside.Of(rd, c, o));
                r.Then(RawColumn.Of(Compute.Of(rd, k, t, o, RawKey.Of), c).Is(RawAnswer.Fails));
            });

            raw.DefineRule("a raw reading's answer on a key is the union of its columns', and a column's reading's its own", r =>
            {
                var (k, c, t) = r.Vars<RawKey, string, RawAnswer>("k", "c", "t");
                r.When(t.Is(RawColumn.Of(k, c)));
                r.Then(RawUnion.Of().Is(Compute.Of(k, c, t, RawTruths.Of)));
            });

            raw.DefineRule("a reading reads the classes its rows name", r =>
            {
                var (rd, a) = r.Vars<string, string>("rd", "a");
                var k = r.Var<CosmosValueClass>("k");
                r.When(CalciteReads.Of(rd, k, a));
                r.Then(ReadsClasses.Of(rd).Is(Compute.Of(k, x => ValueSet.Of(x))));
            });

            raw.DefineRule("a raw column raising over a class refines the text over it", r =>
            {
                var c = r.Var<string>("c");
                var k = r.Var<CosmosValueClass>("k");
                r.When(RawReads.Of(PredicateAtom.RawReader, c, k).Is(CalciteRead.Raises));
                r.Then(RefinesText.Of(c).Is(Compute.Of(k, x => ValueSet.Of(x))));
            });

            raw.DefineRule("a raw column reading a class as the text reads it refines the text over it", r =>
            {
                var (c, a) = r.Vars<string, string>("c", "a");
                var k = r.Var<CosmosValueClass>("k");
                r.When(a.Is(RawReads.Of(PredicateAtom.RawReader, c, k)), CalciteReads.Of(PredicateAtom.TextReader, k, a));
                r.Then(RefinesText.Of(c).Is(Compute.Of(k, x => ValueSet.Of(x))));
            });

            raw.DefineRule("a function's operand is read through what its rows were measured through", r =>
            {
                var f = r.Var<string>("f");
                var rd = r.Var<ValueSet<string>>("rd");
                r.When(rd.Is(MeasuredReaders.Of(f)));
                r.Then(FunctionReaders.Of(f).Is(rd));
            });

            // Asked of the rows gathered complete, a stage after raw's (Settle): a subset of a set still growing is no answer.
            readas.DefineRule("a raw column refining the text over every class the text reads is read as it over a function measured through the text", r =>
            {
                var (f, c) = r.Vars<string, string>("f", "c");
                var rd = r.Var<ValueSet<string>>("rd");
                var (tk, rf) = r.Vars<ValueSet<CosmosValueClass>, ValueSet<CosmosValueClass>>("tk", "rf");
                r.When(rd.Is(MeasuredReaders.Of(f)), tk.Is(ReadsClasses.Of(PredicateAtom.TextReader)), rf.Is(RefinesText.Of(c)));
                r.Where(Primitives.Contains<string>().Of(rd, PredicateAtom.TextReader));
                r.Where(Primitives.IsSubsetOf<CosmosValueClass>().Of(tk, rf));
                r.Then(FunctionReaders.Of(f).Is(Compute.Of(c, x => ValueSet.Of(PredicateAtom.RawIn(x)))));
            });
        }

        // What each reading reads each class as, from its rows (G7, DESIGN.md "Calcite's readings as rows"): the readings
        // vocabulary's rules, which the facts graph runs too, and a raw column's reading through its column and through
        // every column, S's alone, which reads the raw columns' rows.
        void DefineReadings(Ruleset reads)
        {
            Calcite.Define(reads);

            reads.DefineRule("a raw column's reading reads each class as its column's rows say, and the reading through every column as each", r =>
            {
                var (rd, c, a) = r.Vars<string, string, string>("rd", "c", "a");
                var k = r.Var<CosmosValueClass>("k");
                r.When(a.Is(RawReads.Of(rd, c, k)));
                r.Then(ReaderRows.Of().Is(Compute.Of(rd, c, k, a, ReadingTable.Raw)));
            });
        }

        // Two readings compared (DESIGN.md, "A comparison of two readings"): an atom of their pair where both are of one
        // kind, which Calcite compares as written, with what each reading's order holds; whose import beneath a stratum is
        // the product of its places' there, each with its strings' classes. The bundle writes the pair's spelling and
        // characters; PathCell parts the product's cells by order. A pair of two kinds, or of a raw value, is no atom (G3).
        void DefinePair(Ruleset pair, Ruleset place)
        {
            pair.DefineRule("a comparison of two readings of one kind is an atom of their pair, with what each reading's order holds", r =>
            {
                var (e, v, w) = r.Vars<Pred, Val, Val>("e", "v", "w");
                var (p, q) = r.Vars<JPath, JPath>("p", "q");
                var (op, k, l, c) = r.Vars<string, string, string, string>("op", "k", "l", "c");
                var (ck, cl) = r.Vars<ValueSet<string>, ValueSet<string>>("ck", "cl");
                r.When(e.Is(Ir.Compare.Of(op, v, w)), Ir.Reads.Of(v, p, k), Ir.Reads.Of(w, q, l), ReaderKind.Of(k, c), ReaderKind.Of(l, c), ck.Is(ReaderOrder.Of(k)),
                    cl.Is(ReaderOrder.Of(l)));
                r.Then(Ir.Atom.Of(e, Paired.Of(p, q), Compute.Of(Compute.Of(k, op, l, PredicateAtom.CompareReadings), ck, cl, PredicateAtom.OrderedBy)));
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
                var (o, z, uk) = r.Vars<CellOutcomes, ValueSet<CellClass>, ValueSet<CellClass>>("o", "z", "uk");
                r.When(Ir.Atom.Of(e, p, a), At.Of(e, RenderPosition.Pos), Candidate.Of(e, RenderPosition.Pos, f), o.Is(Outcomes.Of(e, f)), z.Is(Compute.Of(o, x => x.Classes)),
                    uk.Is(UnknownClasses.Of()));
                r.Where(Primitives.IsEmpty<CellClass>().Of(Primitives.Intersect<CellClass>().Of(z, uk)));
                r.Then(TwoValued.Of(e));
            });

            // Where the condition is not TRUE its exact Pos form is not true either, so the junction leaves the row as the
            // absorbing else does, whatever the service answers there: no two-valuedness is read. Which junction, of the
            // condition negated or not, is the row's (G4), a constant it holds Kleene's units rule's to fold, and a junction of its
            // own connective or a part twice the regroup rule's.
            best.DefineRule("a case whose else absorbs at its position is its condition joined with its then-arm", r =>
            {
                var (e, c, a, k) = r.Vars<Pred, Pred, Pred, Pred>("e", "c", "a", "k");
                var kw = r.Var<Pred>("kw");
                var (pos, kv, j, ng) = r.Vars<RenderPosition, string, CosmosServiceTest, bool>("pos", "kv", "j", "ng");
                var (bc, ba) = r.Vars<Priced, Priced>("bc", "ba");
                var (cf, af, nc, h) = r.Vars<ServiceForm, ServiceForm, ServiceForm, ServiceForm>("cf", "af", "nc", "h");
                var (pc, pa) = r.Vars<Proof, Proof>("pc", "pa");
                r.When(At.Of(e, pos), e.Is(Chosen.Of(c, a, k, kw)), k.Is(Ir.Truth.Of(kv)), AbsorbingElse.Of(pos, kv, j, ng), bc.Is(BestExact.Of(c, RenderPosition.Pos)),
                    ba.Is(BestExact.Of(a, pos)));
                r.Where(RenderingPrimitives.PricedIsReal.Of(bc));
                r.Where(RenderingPrimitives.PricedIsReal.Of(ba));
                r.When(cf.Is(Compute.Of(bc, Priced.FormOf)), af.Is(Compute.Of(ba, Priced.FormOf)), pc.Is(Agrees.Of(c, RenderPosition.Pos, cf, Verdict.Equal)),
                    pa.Is(Agrees.Of(a, pos, af, Verdict.Equal)), nc.Is(Compute.Of(ng, cf, ServiceForm.Signed)), h.Is(Compute.Of(j, nc, af, ServiceForm.Paired)));
                ComposesAll(r, ValueSet.Of(CosmosServiceTest.And, CosmosServiceTest.Or, CosmosServiceTest.Not));
                r.Then(Agrees.Of(e, pos, h, Verdict.Equal).Is(Compute.Of(pc, pa, Proof.Join)), BestExact.Of(e, pos).Is(Compute.Of(h, Priced.Of)));
            });

            // Where a two-valued condition is not TRUE it is FALSE, which one form exact at both positions says alike.
            best.DefineRule("a case whose else is the opposite constant, over a two-valued condition, is its condition's form joined with its then-arm", r =>
            {
                var (e, c, a, k) = r.Vars<Pred, Pred, Pred, Pred>("e", "c", "a", "k");
                var kw = r.Var<Pred>("kw");
                var (pos, kv, j, ng) = r.Vars<RenderPosition, string, CosmosServiceTest, bool>("pos", "kv", "j", "ng");
                var (bp, bn, ba) = r.Vars<Priced, Priced, Priced>("bp", "bn", "ba");
                var (cf, af, nc, h) = r.Vars<ServiceForm, ServiceForm, ServiceForm, ServiceForm>("cf", "af", "nc", "h");
                var (pc, pn, pa) = r.Vars<Proof, Proof, Proof>("pc", "pn", "pa");
                r.When(At.Of(e, pos), e.Is(Chosen.Of(c, a, k, kw)), k.Is(Ir.Truth.Of(kv)), OppositeElse.Of(pos, kv, j, ng), TwoValued.Of(c),
                    bp.Is(BestExact.Of(c, RenderPosition.Pos)), bn.Is(BestExact.Of(c, RenderPosition.Neg)), ba.Is(BestExact.Of(a, pos)));
                r.Where(RenderingPrimitives.PricedIsReal.Of(bp));
                r.Where(RenderingPrimitives.PricedIsReal.Of(ba));
                r.Where(RenderingPrimitives.SameForm.Of(bp, bn));
                r.When(cf.Is(Compute.Of(bp, Priced.FormOf)), af.Is(Compute.Of(ba, Priced.FormOf)), pc.Is(Agrees.Of(c, RenderPosition.Pos, cf, Verdict.Equal)),
                    pn.Is(Agrees.Of(c, RenderPosition.Neg, cf, Verdict.Equal)), pa.Is(Agrees.Of(a, pos, af, Verdict.Equal)), nc.Is(Compute.Of(ng, cf, ServiceForm.Signed)),
                    h.Is(Compute.Of(j, nc, af, ServiceForm.Paired)));
                ComposesAll(r, ValueSet.Of(CosmosServiceTest.And, CosmosServiceTest.Or, CosmosServiceTest.Not));
                r.Then(Agrees.Of(e, pos, h, Verdict.Equal).Is(Compute.Of(Compute.Of(pc, pn, Proof.Join), pa, Proof.Join)), BestExact.Of(e, pos).Is(Compute.Of(h, Priced.Of)));
            });

            // A junction holding a constant its connective's rows make Kleene's, under a set whose rows measured the constant its
            // own truth, is what Kleene's logic folds it to: the junction without its identity, or its absorbing element (G4).
            // Both agree, and the best keeps the cheaper, the fold having fewer nodes.
            best.DefineRule("a junction holding its connective's unit agrees as Kleene's logic folds it", r =>
            {
                var (e, pos, f, g) = r.Vars<Pred, RenderPosition, ServiceForm, ServiceForm>("e", "pos", "f", "g");
                var (j, kc, u, pf) = r.Vars<CosmosServiceTest, bool, bool, Proof>("j", "kc", "u", "pf");
                var (sk, lk) = r.Vars<RenderingSet, ValueSet<CosmosServiceTest>>("sk", "lk");
                r.When(At.Of(e, pos), pf.Is(Agrees.Of(e, pos, f, Verdict.Equal)), Units.Of(j, kc, u), Under.Of(sk), lk.Is(Licensed.Of(sk)));
                r.Where(RenderingPrimitives.FormHeads.Of(f, j));
                r.Where(RenderingPrimitives.FormHolds.Of(f, Compute.Of(kc, ServiceForm.ConstantOf)));
                r.Where(Primitives.Contains<CosmosServiceTest>().Of(lk, CosmosServiceTest.Constant));
                r.When(g.Is(Compute.Of(f, kc, u, ServiceForm.Unit)));
                Composes(r, j, null);
                r.Then(Agrees.Of(e, pos, g, Verdict.Equal).Is(pf), BestExact.Of(e, pos).Is(Compute.Of(g, Priced.Of)));
            });

            // A junction holding a junction of its own connective, or a part twice, under a set whose rows license the connective,
            // is what its associativity and idempotence regroup it to: flattened, one of each part kept (G4). The junction and
            // else rules join as the arms stand, and this regroups. Both agree, and the price keeps the regrouped: fewer nodes
            // where a copy goes, and where only a junction is flattened, as many nodes in fewer groups (Priced).
            best.DefineRule("a junction holding its own connective or a part twice agrees as its connective's laws regroup it", r =>
            {
                var (e, pos, f, g) = r.Vars<Pred, RenderPosition, ServiceForm, ServiceForm>("e", "pos", "f", "g");
                var (j, pf) = r.Vars<CosmosServiceTest, Proof>("j", "pf");
                r.When(At.Of(e, pos), pf.Is(Agrees.Of(e, pos, f, Verdict.Equal)));
                r.Where(RenderingPrimitives.FormIsGrouped.Of(f));
                r.When(j.Is(Compute.Of(f, x => x.Test)), g.Is(Compute.Of(f, ServiceForm.Regrouped)));
                Composes(r, j, null);
                r.Then(Agrees.Of(e, pos, g, Verdict.Equal).Is(pf), BestExact.Of(e, pos).Is(Compute.Of(g, Priced.Of)));
            });

            // One template, written twice: the narrow else joined with the condition's two-valuedness, the wide else alone,
            // which is sound whatever came before; BestExact's lattice keeps the cheaper (spec h08).
            Ternary(best, narrow: true);
            Ternary(best, narrow: false);

            // A truth value is no atom, and no cell is read for it: on both service targets a constant is its own truth
            // wherever the set measured it so (Service.AConstantIsItsOwnTruth, the Constant licence), resting on the rows that
            // measured that truth's constant (G4), and sent as the constant its class is (constants); UNKNOWN is no constant
            // and no row measured one, so the absorbing rule alone reads a missing else.
            best.DefineRule("a truth value is exact as the constant its position reads it as", r =>
            {
                var (e, pos, kv, h) = r.Vars<Pred, RenderPosition, string, ServiceForm>("e", "pos", "kv", "h");
                var (sk, lk) = r.Vars<RenderingSet, ValueSet<CosmosServiceTest>>("sk", "lk");
                var (tc, ev, b) = r.Vars<CosmosValueClass, ValueSet<string>, bool>("tc", "ev", "b");
                r.When(At.Of(e, pos), e.Is(Ir.Truth.Of(kv)), Under.Of(sk), lk.Is(Licensed.Of(sk)), tc.Is(Compute.Of(kv, CosmosValueClasses.OfTruth)), Constants.Of(tc, b),
                    ev.Is(MeasuredCites.Of(CosmosServiceTest.Constant, tc, sk)), h.Is(Compute.Of(b, ServiceForm.ConstantOf)));
                r.Where(Primitives.Contains<CosmosServiceTest>().Of(lk, CosmosServiceTest.Constant));
                r.Then(Agrees.Of(e, pos, h, Verdict.Equal).Is(Compute.Of(ev, Proof.Rests)), BestExact.Of(e, pos).Is(Compute.Of(h, Priced.Of)),
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

        /// <summary>The key of a comparison of two paths with a side absent, which the pairs rule reads such a pair by (G3).</summary>
        static readonly MeasuredKey PairAbsent = MeasuredKey.Of(CosmosServiceTest.ComparePaths, CosmosValueClass.Absent, CosmosValueClass.None);

        /// <summary>A key of no band, as the pairs rules build one from its test and classes.</summary>
        static MeasuredKey KeyOf(CosmosServiceTest test, CosmosValueClass operand, CosmosValueClass cell) => MeasuredKey.Of(test, operand, cell);

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

        /// <summary>The classes an affix's pattern holds characters of; none for any other form.</summary>
        static ValueSet<string> CharactersOfPattern(ServiceForm form) => form.Kind == ServiceFormKind.Affix ? JsonValues.BandsOfText(JsonValues.StringOf(form.Literal!)) : JsonValues.NoBand;

        /// <summary>The shape a truncation row may name of an affix's pattern (<see cref="AffixPattern.ShapeOf"/>); none for any other form.</summary>
        static string AffixShapeOf(ServiceForm form, ValueSet<(LikeUnit, string)> likes) =>
            form.PatternOf(ServiceTable.UnitsOf(likes)) is { } pattern ? AffixPattern.ShapeOf(pattern.Spelled) : "";

        /// <summary>The key of a test's entry over a string, which names its matchers.</summary>
        static MeasuredKey MatchKeyOf(ServiceForm form) => MeasuredKey.Of(form.Test, CosmosValueClass.String, CosmosValueClass.None);

        /// <summary>The classes a form's literals hold characters of, which the lifts meet with what a set's targets are never sent.</summary>
        internal static ValueSet<string> LiteralClasses(ServiceForm form) => JsonValues.CharactersOf(ValueSet.From(form.Literals));

        /// <summary>The classes the literals of a form's string orderings hold characters of, which the lifts meet with a set's parting bands.</summary>
        internal static ValueSet<string> OrderedLiteralClasses(ServiceForm form) =>
            JsonValues.CharactersOf(ValueSet.From(form.Subforms.Where(g => RenderingPrimitives.StringOrdering.Function(g)).Select(g => g.Literal!)));

        /// <summary>
        /// The spelling S derived for a set's statements (<see cref="Sends"/>), read at the frozen root: what the writer applies,
        /// and <c>null</c> for a set whose rows name no string order.
        /// </summary>
        public SentSpelling? SpellingOf(RenderingSet set) => Theory.TryGet(Sends.Of(set), out var spelling) ? spelling : null;

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

                foreach (var (target, test, function, band) in definition.Folds)
                    w.Assume(FoldsAs.Of(target, test, function, band));

                foreach (var (c, band) in definition.Rewrites)
                    w.Assume(Rewrites.Of(c, band));

                // An answer no row says rewrites a band keeps every band (G1).
                foreach (var answer in Enum.GetValues<CosmosAnswer>())
                    w.Assume(RewrittenBy.Of(answer).Is(ValueSet<string>.Empty));

                foreach (var (target, @class) in definition.Unsent)
                    w.Assume(Unsent.Of(target, @class));

                // What each set's rows make of its statements starts empty, so a set none says anything of reads nothing (G1),
                // a LIKE's pattern among them (G7).
                foreach (var set in definition.Serves.Keys)
                    w.Assume(PartingBands.Of(set).Is(ValueSet<string>.Empty), Respelled.Of(set).Is(ValueSet<string>.Empty), Unsendable.Of(set).Is(ValueSet<string>.Empty),
                        LikesOf.Of(set).Is(ValueSet<(LikeUnit, string)>.Empty));

                foreach (var (target, unit, row) in definition.ServiceLikeUnits)
                    w.Assume(LikeReads.Of(target, unit, row));

                foreach (var (test, swapped, reads) in definition.PairReadings)
                    w.Assume(PairReads.Of(test, swapped, reads));

                foreach (var (kind, box) in definition.Boxes)
                    w.Assume(BoxOf.Of(kind, box));

                foreach (var op in definition.SeesThrough)
                    w.Assume(SeesThrough.Of(op));

                foreach (var function in definition.StructureOnly)
                    w.Assume(StructureOnly.Of(function));

                w.Assume([.. Calcite.Facts(definition.Calcite(), definition.ReadTruths, definition.ReadCompares)]);

                foreach (var (target, reader, row) in definition.Casts)
                    w.Assume(MeasuredCast.Of(target, reader, row));

                foreach (var (reader, moved, row) in definition.Comparands)
                    w.Assume(TakesConstant.Of(reader, moved, row));

                // Every reading of a kind holds what its rows say of its order, and nothing where none says (G3).
                foreach (var (reader, kind) in definition.ReaderKinds)
                    w.Assume(ReaderKind.Of(reader, kind), ReaderOrder.Of(reader).Is(ValueSet<string>.Empty));

                foreach (var (reader, claim) in definition.ReaderOrders)
                    w.Assume(ReaderOrder.Of(reader).Is(ValueSet.Of(claim)));

                foreach (var (target, key, answer) in definition.Kleene)
                    w.Assume(Kleene.Of(key, target).Is(answer));

                foreach (var (connective, classes) in definition.Identity)
                    w.Assume(Identity.Of(connective).Is(classes));

                foreach (var (pos, q) in KleeneTables.Opposite)
                    w.Assume(Opposite.Of(pos, q));

                foreach (var (pos, truth, j, negated) in KleeneTables.AbsorbingElse)
                    w.Assume(AbsorbingElse.Of(pos, truth, j, negated));

                foreach (var (pos, truth, j, negated) in KleeneTables.OppositeElse)
                    w.Assume(OppositeElse.Of(pos, truth, j, negated));

                foreach (var (j, constant, absorbs) in KleeneTables.Units)
                    w.Assume(Units.Of(j, constant, absorbs));

                // What reads each test's entry, how a comparator reads a whole number (G2); what a failing side stands for (G4);
                // the kinds a pair's side holds, and each set's pair table, empty until a row reads one (G3).
                foreach (var (test, operand, answer) in definition.Means.ReadBy.AsEnumerable())
                    w.Assume(ReadBy.Of(test, operand).Is(ValueSet.Of(answer)));

                foreach (var (test, operand, answer) in definition.Evaluates.AsEnumerable())
                    w.Assume(Evaluates.Of(test, operand).Is(ValueSet.Of(answer)));

                foreach (var (@class, constant) in CosmosValueClasses.Constants)
                    w.Assume(Constants.Of(@class, constant));

                foreach (var (comparator, reading) in definition.WholeReadings)
                    w.Assume(WholeReads.Of(comparator, reading));

                foreach (var (failing, stands) in KleeneTables.Failing)
                    w.Assume(StandsFor.Of(failing, stands), Stands.Of(failing).Is(ValueSet.Of(stands)));

                foreach (var kind in CosmosValueClasses.Kinds.AsEnumerable())
                    w.Assume(Kind.Of(kind));

                foreach (var set in definition.Serves.Keys)
                    w.Assume(PairTable.Of(set).Is(ValueSet<MeasuredEntry>.Empty));

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

                foreach (var (op, type, name) in definition.FunctionNames)
                    w.Assume(type is null ? RowNamedAlone.Of(op) : RowName.Of(op, type).Is(name));

                foreach (var (function, spelling, ordinal, @class) in definition.CallSpellings)
                    w.Assume(ordinal == FunctionKey.Every ? SpelledAlways.Of(function, spelling) : SpelledWhere.Of(function, spelling, ordinal, @class));

                foreach (var (key, yields) in definition.FunctionYields)
                    w.Assume(FunctionYield.Of(key).Is(yields));

                foreach (var function in keys.Select(k => k.Function).Distinct())
                    foreach (var yields in Enum.GetValues<CalciteYield>())
                        w.Assume(YieldKeys.Of(function, yields).Is(ValueSet<FunctionKey>.Empty));

                foreach (var (type, kind) in definition.TypeKinds)
                    w.Assume(TypeKinds.Of(type).Is(ValueSet.Of(kind)));

                w.Assume(LiteralReaders.Of().Is(ValueSet.From(definition.LiteralReaders)));

                foreach (var set in definition.Serves.Keys)
                    foreach (var function in keys.Select(k => k.Function).Distinct())
                        w.Assume(DisagreeingKeys.Of(set, function).Is(ValueSet<FunctionKey>.Empty));

                // The columns' rows, one fact a field; the union starts empty, so an atom is paired on its cells where no column is named.
                foreach (var (reading, column, kind, answer) in definition.Columns.Reads)
                    w.Assume(RawReads.Of(reading, column, kind).Is(answer));

                foreach (var (reading, column, operand) in definition.Columns.Beside)
                    w.Assume(RawBeside.Of(reading, column, operand));

                foreach (var (reading, column, test, compiles) in definition.Columns.Compiles)
                    w.Assume(RawCompiles.Of(reading, column, test).Is(compiles));

                foreach (var (reading, column, asType) in definition.Columns.AsType)
                    w.Assume(RawAsType.Of(reading, column).Is(asType));

                foreach (var (reading, column, test, truths) in definition.Columns.Across)
                    w.Assume(RawAcross.Of(reading, column, test).Is(truths));

                foreach (var (@class, type) in definition.ClassTypes)
                    w.Assume(ClassType.Of(@class).Is(type));

                w.Assume(RawUnion.Of().Is(RawTruths.Empty));

                w.Assume(Means.Of().Is(definition.Means), Placed.Of().Is(ValueSet<CosmosValueClass>.Empty), RaiseOnly.Of().Is(ValueSet<CellClass>.Empty), Tolerated.Of().Is(definition.Tolerated),
                    Rewriting.Of().Is(ValueSet<CosmosAnswer>.Empty), RexGrammar.Of().Is(RenderingRexGrammar.Written), UnknownClasses.Of().Is(ValueSet<CellClass>.Empty));
            });

            var report = Theory.Run(Settle);
            if (report.StopReason != RunStopReason.Completed)
                throw new InvalidOperationException($"The rendering theory stopped {report.StopReason} after {report.Iterations} iterations.");

            Theory.Freeze();
        }

    }

}
