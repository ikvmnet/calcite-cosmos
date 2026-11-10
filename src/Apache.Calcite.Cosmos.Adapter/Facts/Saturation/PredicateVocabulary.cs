using Apache.Calcite.Cosmos.Facts.Rendering;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// The loader's IR as terms, and the analysis that reads them into atoms: one vocabulary for each graph that reasons
    /// about a conjunct, the theory's (<see cref="PredicateRules"/>) and the rendering graph, written by
    /// <see cref="PredicateWriter"/>. Nothing here says what a predicate establishes or decides.
    /// </summary>
    internal sealed class PredicateVocabulary
    {

        /// <param name="graph">The graph.</param>
        /// <param name="paths">The graph's path terms, which say whether a path holds an element.</param>
        /// <param name="readings">
        /// What Calcite makes of each reading, the graph's own rows, analyses assumed at its root: the JSON kinds each type
        /// test is TRUE of and the readings a one-place test's body takes apart (<c>Calcite.TypeTestsAreTheirBodies</c>, G7,
        /// G8), the boxes a value cast to <c>VARIANT</c> is held in, which readings take what a cast number denotes, and which
        /// operators answer null over a null operand, which the rules of G8 read.
        /// </param>
        public PredicateVocabulary(EGraph graph, PathVocabulary paths, ReadingVocabulary readings)
        {
            Paths = paths;
            Readings = readings;
            TestKinds = readings.TestKinds;
            TestReads = readings.TestReads;
            graph.DefineSort<Val>("Val");
            graph.DefineSort<Pred>("Pred");
            graph.DefineSort<Args>("Args");
            graph.Values.Register<PredicateAtom>("predicate atom");
            graph.Values.Register<PathValues>("path values");

            // Nothing here is ever extracted for execution: the host reads values back, never a term.
            var term = new TableOptions { Extractable = false };
            var analysis = new TableOptions { Role = TableRole.Analysis };

            Text = graph.DefineConstructor<JPath, Val>("text", options: term);
            Raw = graph.DefineConstructor<JPath, Val>("raw", options: term);
            RawIn = graph.DefineConstructor<JPath, string, Val>("raw in", options: term);
            Typed = graph.DefineConstructor<JPath, string, Val>("typed", options: term);
            Cast = graph.DefineConstructor<string, Val, Val>("cast", options: term);
            DecimalCast = graph.DefineConstructor<long, long, Val, Val>("decimal cast", options: term);
            Box = graph.DefineConstructor<string, Val, Val>("box", options: term);
            Query = graph.DefineConstructor<JPath, Val>("query", options: term);
            Apart = graph.DefineConstructor<string, Val, Val>("apart", options: term);
            Null = graph.DefineConstructor<string, Val>("sql null", options: term);
            Literal = graph.DefineConstructor<string, Val>("literal", options: term);
            Parameter = graph.DefineConstructor<long, string, Val>("parameter", options: term);
            Call = graph.DefineConstructor<string, Args, Val>("call", options: term);
            NoOperands = graph.DefineConstructor<Args>("no operands", options: term);
            Operand = graph.DefineConstructor<Val, Args, Args>("operand", options: term);
            Compare = graph.DefineConstructor<string, Val, Val, Pred>("compare", options: term);
            In = graph.DefineConstructor<Val, ValueSet<string>, Pred>("in", options: term);
            IsNull = graph.DefineConstructor<Val, Pred>("is null", options: term);
            IsDefined = graph.DefineConstructor<Val, Pred>("is defined", options: term);
            And = graph.DefineConstructor<Pred, Pred, Pred>("and", options: term);
            Or = graph.DefineConstructor<Pred, Pred, Pred>("or", options: term);
            Not = graph.DefineConstructor<Pred, Pred>("not", options: term);
            Opaque = graph.DefineConstructor<long, Pred>("opaque", options: term);
            TypeTest = graph.DefineConstructor<string, Val, Pred>("type test", options: term);
            Case = graph.DefineConstructor<Pred, Pred, Pred, Pred>("case", options: term);
            Affix = graph.DefineConstructor<string, Val, string, Pred>("affix", options: term);
            Truth = graph.DefineConstructor<string, Pred>("truth", options: term);

            Point = graph.DefineRelation<Pred, string>("point", analysis);
            Reads = graph.DefineRelation<Val, JPath, string>("reads", analysis);
            Accesses = graph.DefineRelation<Val, JPath, string, string>("accesses", analysis);
            Atom = graph.DefineRelation<Pred, JPath, PredicateAtom>("atom", analysis);
            TestRead = graph.DefineRelation<Val, JPath, string>("testread", analysis);
            Denotes = graph.DefineRelation<Val, string, bool>("denotes", analysis);
            NoElement = graph.DefineRelation<JPath>("noelement", analysis);
            HasElement = graph.DefineRelation<JPath>("haselement", analysis);

            Analyse = graph.DefineRuleset("analyse");
            DefineAnalysis();
        }

        /// <summary><c>JSON_VALUE(DOC, path)</c> read as text.</summary>
        public Constructor<JPath, Val> Text { get; }

        /// <summary>The value the service holds at a path.</summary>
        public Constructor<JPath, Val> Raw { get; }

        /// <summary>
        /// The value the service holds at a path, read through the column the loader knows it is in (rawtyped): a declared
        /// field, read through its column's reading (<c>raw:VARIANT</c>), and by F's rules of a value's characters.
        /// </summary>
        public Constructor<JPath, string, Val> RawIn { get; }

        /// <summary><c>JSON_VALUE(DOC, path RETURNING type)</c>.</summary>
        public Constructor<JPath, string, Val> Typed { get; }

        /// <summary>A cast of a value to a SQL type.</summary>
        public Constructor<string, Val, Val> Cast { get; }

        /// <summary>
        /// A cast of a number to <c>DECIMAL(precision, scale)</c>, one a comparison's other side is made of: read by the rules
        /// of what a cast of a literal denotes (<see cref="Denotes"/>, G8).
        /// </summary>
        public Constructor<long, long, Val, Val> DecimalCast { get; }

        /// <summary>
        /// A value cast to <c>VARIANT</c>: the SQL type of what it boxes, and that value, read by the rules of what a box
        /// equals and where it is null (<c>boxof</c>, <c>seesthrough</c>, <c>boxnull</c>, G8). It reads no path of its own.
        /// </summary>
        public Constructor<string, Val, Val> Box { get; }

        /// <summary>
        /// <c>JSON_QUERY(DOC, path)</c> with no clause: it reads its path as its own reading, whose answers are its row's
        /// (<c>Calcite.JsonQueryAnswersStructureOnly</c>, G8), and a test of it as its row says the body reads it
        /// (<see cref="TestReads"/>).
        /// </summary>
        public Constructor<JPath, Val> Query { get; }

        /// <summary>
        /// A reading written as a form apart from its accessor (<see cref="SqlValue.Form"/>, h15b): it reads what its reading
        /// reads, and a test of it as its form's row says the body reads it (<see cref="TestReads"/>).
        /// </summary>
        public Constructor<string, Val, Val> Apart { get; }

        /// <summary>
        /// SQL's null, of a SQL type: a comparison with it is read by the rule over the operator's row (<c>strictop</c>, G8).
        /// </summary>
        public Constructor<string, Val> Null { get; }

        /// <summary>A literal, in <see cref="JsonValues"/>' spelling.</summary>
        public Constructor<string, Val> Literal { get; }

        /// <summary>A parameter: its ordinal and SQL type, and no value.</summary>
        public Constructor<long, string, Val> Parameter { get; }

        /// <summary>A function of values: its name, as the loader spells it, and its operands.</summary>
        public Constructor<string, Args, Val> Call { get; }

        /// <summary>The end of a call's operands.</summary>
        public Constructor<Args> NoOperands { get; }

        /// <summary>An operand of a call, and those after it.</summary>
        public Constructor<Val, Args, Args> Operand { get; }

        /// <summary>A comparison: an operator, the value read, and a literal or a parameter.</summary>
        public Constructor<string, Val, Val, Pred> Compare { get; }

        /// <summary>A membership in a set of literals.</summary>
        public Constructor<Val, ValueSet<string>, Pred> In { get; }

        /// <summary><c>IS NULL</c>.</summary>
        public Constructor<Val, Pred> IsNull { get; }

        /// <summary><c>IS_DEFINED</c> of the path the value reads.</summary>
        public Constructor<Val, Pred> IsDefined { get; }

        /// <summary>A conjunction, right-nested in digest order: there is no rule of commutativity.</summary>
        public Constructor<Pred, Pred, Pred> And { get; }

        /// <summary>A disjunction, right-nested in digest order.</summary>
        public Constructor<Pred, Pred, Pred> Or { get; }

        /// <summary>A negation.</summary>
        public Constructor<Pred, Pred> Not { get; }

        /// <summary>A predicate the loader does not read: it proves nothing and decides nothing.</summary>
        public Constructor<long, Pred> Opaque { get; }

        /// <summary>An <c>IS_*</c> test but <c>IS_DEFINED</c>: its name, and the value it tests.</summary>
        public Constructor<string, Val, Pred> TypeTest { get; }

        /// <summary>The JSON kinds each type test is TRUE of, by its name: the graph's rows, which the type-test rule reads.</summary>
        public Function<string, ValueSet<string>> TestKinds { get; }

        /// <summary>
        /// The readings a one-place test's in-process body takes apart, by the constructor the IR writes each as, with the
        /// reading it reads instead: the graph's rows, which the test-read rules read.
        /// </summary>
        public Relation<string, string> TestReads { get; }

        /// <summary>
        /// A <c>CASE</c> over predicates: its condition, then-arm and else-arm. No rule of the facts graph reads one, so it
        /// learns of one what it learns of an opaque term.
        /// </summary>
        public Constructor<Pred, Pred, Pred, Pred> Case { get; }

        /// <summary>A match of a value's text against a literal pattern: the operator, the value, the pattern.</summary>
        public Constructor<string, Val, string, Pred> Affix { get; }

        /// <summary>A truth value as a predicate: <c>true</c>, <c>false</c> or <c>unknown</c>.</summary>
        public Constructor<string, Pred> Truth { get; }

        /// <summary>
        /// A literal an atom compares its value with: a comparison's, or each of a membership's. Stated by the writer beside
        /// the term, because a rule cannot take the members of a set one at a time.
        /// </summary>
        public Relation<Pred, string> Point { get; }

        /// <summary>The path a value reads, and how: an analysis of terms alone.</summary>
        public Relation<Val, JPath, string> Reads { get; }

        /// <summary>
        /// The path an accessor reads, the reading it is, and the name the readings of an index read as of a path key it by
        /// (<c>asofapath</c>): the reading itself, and every <c>RETURNING</c>'s <see cref="PredicateAtom.TypedReaders"/>. What
        /// it reads is <see cref="Reads"/>: the reading, or of a path holding an element the element reading where that set
        /// does not name it.
        /// </summary>
        public Relation<Val, JPath, string, string> Accesses { get; }

        /// <summary>A predicate over one path, as the atom that says what it means: an analysis of terms alone.</summary>
        public Relation<Pred, JPath, PredicateAtom> Atom { get; }

        /// <summary>
        /// The path a one-place test of a value asks of, and the reading it reads there: what the test's in-process body
        /// takes the value's accessor apart into, or <see cref="PredicateAtom.RaisesReader"/> where it raises over the value,
        /// as a row says (<see cref="TestReads"/>, G8).
        /// </summary>
        public Relation<Val, JPath, string> TestRead { get; }

        /// <summary>What Calcite makes of each reading: the rows the rules of G8 read beside the terms.</summary>
        public ReadingVocabulary Readings { get; }

        /// <summary>The graph's path terms.</summary>
        public PathVocabulary Paths { get; }

        /// <summary>A path holding no element, read off its term.</summary>
        public Relation<JPath> NoElement { get; }

        /// <summary>A path holding an element, read off its term: one of its steps is an index.</summary>
        public Relation<JPath> HasElement { get; }

        /// <summary>
        /// What a cast of a literal denotes (G8): the number the literal spells, and whether a cast that drops scale moved
        /// Calcite's value from it; nothing where a cast's whole digits do not fit, which raises. Derived from the terms alone.
        /// </summary>
        public Relation<Val, string, bool> Denotes { get; }

        /// <summary>Reads terms into atoms.</summary>
        public Ruleset Analyse { get; }

        void DefineAnalysis()
        {
            DefineElements();

            // What each accessor reads of its path, and the name the readings of an index key it by (asofapath): the reading
            // itself, but a RETURNING's, which every RETURNING is measured under.
            Analyse.DefineRule("text accesses its path as the text", r =>
            {
                var (v, p) = r.Vars<Val, JPath>("v", "p");
                r.When(v.Is(Text.Of(p)));
                r.Then(Accesses.Of(v, p, PredicateAtom.TextReader, PredicateAtom.TextReader));
            });
            Analyse.DefineRule("a raw value accesses its path as the raw value", r =>
            {
                var (v, p) = r.Vars<Val, JPath>("v", "p");
                r.When(v.Is(Raw.Of(p)));
                r.Then(Accesses.Of(v, p, PredicateAtom.RawReader, PredicateAtom.RawReader));
            });
            // A declared field, read through its column (rawtyped).
            Analyse.DefineRule("a raw value in a column accesses its path through the column", r =>
            {
                var (v, p, c, k) = r.Vars<Val, JPath, string, string>("v", "p", "c", "k");
                r.When(v.Is(RawIn.Of(p, c)), k.Is(Compute.Of(c, PredicateAtom.RawIn)));
                r.Then(Accesses.Of(v, p, k, k));
            });
            Analyse.DefineRule("a returning accesses its path as its type", r =>
            {
                var (v, p, t) = r.Vars<Val, JPath, string>("v", "p", "t");
                r.When(v.Is(Typed.Of(p, t)));
                r.Then(Accesses.Of(v, p, Compute.Of(t, PredicateAtom.TypedReader), PredicateAtom.TypedReaders));
            });
            Analyse.DefineRule("a JSON_QUERY accesses its path as its own reading", r =>
            {
                var (v, p) = r.Vars<Val, JPath>("v", "p");
                r.When(v.Is(Query.Of(p)));
                r.Then(Accesses.Of(v, p, PredicateAtom.QueryReader, PredicateAtom.QueryReader));
            });
            // An accessor reads its path as it accesses it where the path holds no element, and where it holds one and the
            // readings an index reads as of a path name it (asofapath, Calcite.ReadingsOfAnIndexAreAsOfAPath). One they do not
            // name reads the element reading, which reads its rows alone, a row no run measured as anything and a raise (h15b):
            // a column's reading of an element, which no run measured, among them.
            Analyse.DefineRule("an accessor of a path holding no element reads its path", r =>
            {
                var (v, p) = r.Vars<Val, JPath>("v", "p");
                var (k, n) = r.Vars<string, string>("k", "n");
                r.When(Accesses.Of(v, p, k, n), NoElement.Of(p));
                r.Then(Reads.Of(v, p, k));
            });
            Analyse.DefineRule("an accessor of a path holding an element reads its path where an index reads it as of a path", r =>
            {
                var (v, p) = r.Vars<Val, JPath>("v", "p");
                var (k, n) = r.Vars<string, string>("k", "n");
                var s = r.Var<ValueSet<string>>("s");
                r.When(Accesses.Of(v, p, k, n), HasElement.Of(p), s.Is(Readings.AsOfAPath.Of()));
                r.Where(Primitives.Contains<string>().Of(s, n));
                r.Then(Reads.Of(v, p, k));
            });
            Analyse.DefineRule("an accessor of a path holding an element reads the element reading where an index does not read it as of a path", r =>
            {
                var (v, p) = r.Vars<Val, JPath>("v", "p");
                var (k, n) = r.Vars<string, string>("k", "n");
                var (s, named) = r.Vars<ValueSet<string>, bool>("s", "named");
                r.When(Accesses.Of(v, p, k, n), HasElement.Of(p), s.Is(Readings.AsOfAPath.Of()), named.Is(Primitives.Contains<string>().Of(s, n)), named.Is(false));
                r.Then(Reads.Of(v, p, PredicateAtom.ElementReader));
            });
            // A reading written apart, a field's, an item's, a cast's to ANY, reads what its reading reads (h15b); of a path
            // holding an element, the element reading, whatever its reading reads: no run measured a form of an element, and an
            // item of one is no path's (Calcite.ItemIsNullWhereNoElementIs).
            Analyse.DefineRule("a reading written apart reads what its reading reads where its path holds no element", r =>
            {
                var (v, w, p) = r.Vars<Val, Val, JPath>("v", "w", "p");
                var (f, k) = r.Vars<string, string>("f", "k");
                r.When(v.Is(Apart.Of(f, w)), Reads.Of(w, p, k), NoElement.Of(p));
                r.Then(Reads.Of(v, p, k));
            });
            Analyse.DefineRule("a reading written apart of a path holding an element reads the element reading", r =>
            {
                var (v, w, p) = r.Vars<Val, Val, JPath>("v", "w", "p");
                var (f, k) = r.Vars<string, string>("f", "k");
                r.When(v.Is(Apart.Of(f, w)), Reads.Of(w, p, k), HasElement.Of(p));
                r.Then(Reads.Of(v, p, PredicateAtom.ElementReader));
            });
            // A cast is a reading where a row measured it, keyed by the reading it converts, its column apart (h15b): what a
            // column's cast reads is the column's own rows. One no row names reads nothing, so no atom rests on it: over an ANY
            // Calcite wraps a long beyond an int, so CAST(4294967297 AS INTEGER) is 1, which no window of the number holds.
            Analyse.DefineRule("a cast a row measured reads what it converts", r =>
            {
                var (v, w, p) = r.Vars<Val, Val, JPath>("v", "w", "p");
                var (t, k, kc, row) = r.Vars<string, string, string, string>("t", "k", "kc", "row");
                r.When(v.Is(Cast.Of(t, w)), Reads.Of(w, p, k), kc.Is(Compute.Of(k, PredicateAtom.Columnless)), Readings.MeasuredCast.Of(t, kc, row));
                r.Then(Reads.Of(v, p, Compute.Of(t, k, PredicateAtom.CastReader)));
            });

            Analyse.DefineRule("a comparison with a literal is an atom of what it reads", r =>
            {
                var (e, v, w) = r.Vars<Pred, Val, Val>("e", "v", "w");
                var (op, c, k) = r.Vars<string, string, string>("op", "c", "k");
                var p = r.Var<JPath>("p");
                r.When(e.Is(Compare.Of(op, v, w)), w.Is(Literal.Of(c)), Reads.Of(v, p, k));
                r.Then(Atom.Of(e, p, Compute.Of(k, op, c, PredicateAtom.Compare)));
            });
            Analyse.DefineRule("a comparison with a parameter is an atom of what it reads", r =>
            {
                var (e, v, w) = r.Vars<Pred, Val, Val>("e", "v", "w");
                var (op, t, k) = r.Vars<string, string, string>("op", "t", "k");
                var (n, p) = r.Vars<long, JPath>("n", "p");
                r.When(e.Is(Compare.Of(op, v, w)), w.Is(Parameter.Of(n, t)), Reads.Of(v, p, k));
                r.Then(Atom.Of(e, p, Compute.Of(k, op, PredicateAtom.CompareParameter)));
            });
            Analyse.DefineRule("a membership is an atom of what it reads", r =>
            {
                var (e, v, p) = r.Vars<Pred, Val, JPath>("e", "v", "p");
                var (s, k) = r.Vars<ValueSet<string>, string>("s", "k");
                r.When(e.Is(In.Of(v, s)), Reads.Of(v, p, k));
                r.Then(Atom.Of(e, p, Compute.Of(k, s, PredicateAtom.In)));
            });
            Analyse.DefineRule("a null test is an atom of what it reads", r =>
            {
                var (e, v, p) = r.Vars<Pred, Val, JPath>("e", "v", "p");
                var k = r.Var<string>("k");
                r.When(e.Is(IsNull.Of(v)), Reads.Of(v, p, k));
                r.Then(Atom.Of(e, p, Compute.Of(k, PredicateAtom.IsNull)));
            });
            // A one-place test's body is handed the document and the path the accessor names, not the value it reads
            // (Calcite.TypeTestsAreTheirBodies, G8): each accessor its row names is read as the reading the row says the body
            // reads instead, one rule a constructor; a cast, which no row names, is no accessor, and its test is no atom.
            Analyse.DefineRule("a test reads the text's path as its row says the body reads it", r =>
            {
                var (v, p, k) = r.Vars<Val, JPath, string>("v", "p", "k");
                r.When(v.Is(Text.Of(p)), TestReads.Of(PredicateAtom.TextReader, k));
                r.Then(TestRead.Of(v, p, k));
            });
            Analyse.DefineRule("a test reads a raw value's path as its row says the body reads it", r =>
            {
                var (v, p, k) = r.Vars<Val, JPath, string>("v", "p", "k");
                r.When(v.Is(Raw.Of(p)), TestReads.Of(PredicateAtom.RawReader, k));
                r.Then(TestRead.Of(v, p, k));
            });
            // A field is keyed by its column's own reading, over which the body raises (measured in process, rows2).
            Analyse.DefineRule("a test reads the path of a raw value in a column as its column's row says the body reads it", r =>
            {
                var (v, p) = r.Vars<Val, JPath>("v", "p");
                var (k, c, rc) = r.Vars<string, string, string>("k", "c", "rc");
                r.When(v.Is(RawIn.Of(p, c)), rc.Is(Compute.Of(c, PredicateAtom.RawIn)), TestReads.Of(rc, k));
                r.Then(TestRead.Of(v, p, k));
            });
            Analyse.DefineRule("a test reads a returning's path as its row says the body reads it", r =>
            {
                var (v, p) = r.Vars<Val, JPath>("v", "p");
                var (k, t) = r.Vars<string, string>("k", "t");
                r.When(v.Is(Typed.Of(p, t)), TestReads.Of(PredicateAtom.TypedReaders, k));
                r.Then(TestRead.Of(v, p, k));
            });
            Analyse.DefineRule("a test reads a JSON_QUERY's path as its row says the body reads it", r =>
            {
                var (v, p, k) = r.Vars<Val, JPath, string>("v", "p", "k");
                r.When(v.Is(Query.Of(p)), TestReads.Of(PredicateAtom.QueryReader, k));
                r.Then(TestRead.Of(v, p, k));
            });
            // A field, an item chain, a cast to ANY: what the body reads over each is its form's own row (h15b), not the row of
            // the reading it reads, which stands for the accessor the body takes apart.
            Analyse.DefineRule("a test reads the path of a reading written apart as its form's row says the body reads it", r =>
            {
                var (v, w, p) = r.Vars<Val, Val, JPath>("v", "w", "p");
                var (f, k, rd) = r.Vars<string, string, string>("f", "k", "rd");
                r.When(v.Is(Apart.Of(f, w)), Reads.Of(w, p, rd), TestReads.Of(f, k));
                r.Then(TestRead.Of(v, p, k));
            });
            // A computed place's call, read where a graph reads one (S's computed places): every call's row says the body
            // raises over it (measured in process); the bridge's presence question about a place is such a test.
            Analyse.DefineRule("a test reads a call's place as every call's row says the body reads it", r =>
            {
                var (v, q) = r.Vars<Val, JPath>("v", "q");
                var (f, k, rd) = r.Vars<string, string, string>("f", "k", "rd");
                var a = r.Var<Args>("a");
                r.When(v.Is(Call.Of(f, a)), Reads.Of(v, q, rd), TestReads.Of(PredicateAtom.CallReaders, k));
                r.Then(TestRead.Of(v, q, k));
            });
            // The atom is of the reading the test is asked over, which cuts its cells and whose raise it carries, read as its
            // body reads it: a test raises wherever its reading or its body does (Calcite.TypeTestsAreTheirBodies, rows2).
            Analyse.DefineRule("a definedness test is an atom of the reading it is asked over, read as its body reads it", r =>
            {
                var (e, v, p) = r.Vars<Pred, Val, JPath>("e", "v", "p");
                var (k, rd) = r.Vars<string, string>("k", "rd");
                r.When(e.Is(IsDefined.Of(v)), TestRead.Of(v, p, k), Reads.Of(v, p, rd));
                r.Then(Atom.Of(e, p, Compute.Of(rd, k, PredicateAtom.IsDefined)));
            });
            Analyse.DefineRule("a type test is an atom of the reading it is asked over, read as its body reads it, TRUE of the kinds its row says", r =>
            {
                var (e, v, p) = r.Vars<Pred, Val, JPath>("e", "v", "p");
                var (t, k, rd) = r.Vars<string, string, string>("t", "k", "rd");
                var ks = r.Var<ValueSet<string>>("ks");
                r.When(e.Is(TypeTest.Of(t, v)), TestRead.Of(v, p, k), Reads.Of(v, p, rd), ks.Is(TestKinds.Of(t)));
                r.Then(Atom.Of(e, p, Compute.Of(rd, t, ks, k, PredicateAtom.TypeTest)));
            });
            Analyse.DefineRule("an affix test with a literal pattern is an atom of what it reads", r =>
            {
                var (e, v, p) = r.Vars<Pred, Val, JPath>("e", "v", "p");
                var (op, c, k) = r.Vars<string, string, string>("op", "c", "k");
                r.When(e.Is(Affix.Of(op, v, c)), Reads.Of(v, p, k));
                r.Then(Atom.Of(e, p, Compute.Of(k, op, c, PredicateAtom.Affix)));
            });

            DefineComparands();
            DefineNullComparison();
            DefineBoxes();
        }

        // Whether a path holds an element, read off its term, which a raw value's reading of it reads (h15b): the document and a
        // property of a path holding none hold none, and an index, every element and a property of a path holding one, one.
        void DefineElements()
        {
            Analyse.DefineRule("the document holds no element", r =>
            {
                var p = r.Var<JPath>("p");
                r.When(p.Is(Paths.Root.Of()));
                r.Then(NoElement.Of(p));
            });
            Analyse.DefineRule("a property of a path holding no element holds none", r =>
            {
                var (p, q) = r.Vars<JPath, JPath>("p", "q");
                var n = r.Var<string>("n");
                r.When(p.Is(Paths.Prop.Of(q, n)), NoElement.Of(q));
                r.Then(NoElement.Of(p));
            });
            Analyse.DefineRule("an index holds an element", r =>
            {
                var (p, q) = r.Vars<JPath, JPath>("p", "q");
                var i = r.Var<long>("i");
                r.When(p.Is(Paths.Index.Of(q, i)));
                r.Then(HasElement.Of(p));
            });
            Analyse.DefineRule("every element holds one", r =>
            {
                var (p, q) = r.Vars<JPath, JPath>("p", "q");
                r.When(p.Is(Paths.Each.Of(q)));
                r.Then(HasElement.Of(p));
            });
            Analyse.DefineRule("a property of a path holding an element holds one", r =>
            {
                var (p, q) = r.Vars<JPath, JPath>("p", "q");
                var n = r.Var<string>("n");
                r.When(p.Is(Paths.Prop.Of(q, n)), HasElement.Of(q));
                r.Then(HasElement.Of(p));
            });
        }

        // What a number Calcite wrote cast denotes, which the loader reads as the casts' terms, and the atom of a comparison
        // with it where a row says the reading takes it (G8). A cast to a DECIMAL its whole digits do not fit raises in
        // Calcite and denotes nothing; one whose scale is below the number's truncates it toward zero, by less than one,
        // which only a reading that rescales its literal takes.
        void DefineComparands()
        {
            Analyse.DefineRule("a literal a cast to DOUBLE converts denotes the number it spells", r =>
            {
                var (v, w) = r.Vars<Val, Val>("v", "w");
                var c = r.Var<string>("c");
                r.When(v.Is(Cast.Of("DOUBLE", w)), w.Is(Literal.Of(c)));
                r.Then(Denotes.Of(w, c, false));
            });
            Analyse.DefineRule("a literal a cast to a DECIMAL converts denotes the number it spells", r =>
            {
                var (v, w) = r.Vars<Val, Val>("v", "w");
                var (pr, sc, c) = r.Vars<long, long, string>("pr", "sc", "c");
                r.When(v.Is(DecimalCast.Of(pr, sc, w)), w.Is(Literal.Of(c)));
                r.Then(Denotes.Of(w, c, false));
            });
            Analyse.DefineRule("a cast to DOUBLE denotes what it converts denotes", r =>
            {
                var (v, w) = r.Vars<Val, Val>("v", "w");
                var (c, m) = r.Vars<string, bool>("c", "m");
                r.When(v.Is(Cast.Of("DOUBLE", w)), Denotes.Of(w, c, m));
                r.Then(Denotes.Of(v, c, m));
            });
            Analyse.DefineRule("a cast to a DECIMAL that holds the number's digits denotes what it converts denotes", r =>
            {
                var (v, w) = r.Vars<Val, Val>("v", "w");
                var (pr, sc) = r.Vars<long, long>("pr", "sc");
                var (c, m) = r.Vars<string, bool>("c", "m");
                r.When(v.Is(DecimalCast.Of(pr, sc, w)), Denotes.Of(w, c, m));
                r.Where(RenderingPrimitives.WholeDigitsFit.Of(c, pr, sc));
                r.Where(RenderingPrimitives.PlacesWithin.Of(c, sc));
                r.Then(Denotes.Of(v, c, m));
            });
            Analyse.DefineRule("a cast to a DECIMAL that drops the number's places denotes it moved", r =>
            {
                var (v, w) = r.Vars<Val, Val>("v", "w");
                var (pr, sc) = r.Vars<long, long>("pr", "sc");
                var (c, m) = r.Vars<string, bool>("c", "m");
                r.When(v.Is(DecimalCast.Of(pr, sc, w)), Denotes.Of(w, c, m));
                r.Where(RenderingPrimitives.WholeDigitsFit.Of(c, pr, sc));
                r.Where(RenderingPrimitives.PlacesBeyond.Of(c, sc));
                r.Then(Denotes.Of(v, c, true));
            });
            Analyse.DefineRule("a comparison with what a cast denotes is an atom of what it reads where its reading takes it", r =>
            {
                var (e, v, w) = r.Vars<Pred, Val, Val>("e", "v", "w");
                var (op, c, k, o) = r.Vars<string, string, string, string>("op", "c", "k", "o");
                var (m, p, row) = r.Vars<bool, JPath, string>("m", "p", "row");
                r.When(e.Is(Compare.Of(op, v, w)), Denotes.Of(w, c, m), Reads.Of(v, p, k), o.Is(Compute.Of(k, PredicateAtom.Outermost)), Readings.TakesConstant.Of(o, m, row));
                r.Then(Atom.Of(e, p, Compute.Of(k, op, c, PredicateAtom.Compare)));
            });
        }

        // A comparison with SQL's null by an operator Calcite answers null for over a null operand is UNKNOWN wherever its
        // reading is read, by the operator's row (strictop), not by a refusal of the null literal (G8).
        void DefineNullComparison()
        {
            Analyse.DefineRule("a comparison with SQL's null, by an operator null over a null operand, is an atom of what it reads", r =>
            {
                var (e, v, w) = r.Vars<Pred, Val, Val>("e", "v", "w");
                var (op, t, k) = r.Vars<string, string, string>("op", "t", "k");
                var p = r.Var<JPath>("p");
                r.When(e.Is(Compare.Of(op, v, w)), w.Is(Null.Of(t)), Readings.StrictOp.Of(op).Is(true), Reads.Of(v, p, k));
                r.Then(Atom.Of(e, p, Compute.Of(k, op, PredicateAtom.CompareNull)));
            });
        }

        // A value cast to VARIANT (G8). A VARIANT equals a value only in its own box (Calcite.VariantEqualsOnlyItsOwnBox):
        // within one, the comparisons its row says see through it read the values inside, and its null test is what it
        // holds; boxes apart no rule reads, and the comparison is no atom. A text's box is the row's of its reading, a
        // literal's of its SQL type, and a raw value's of the kind it holds, so a raw value compared with a literal boxed as
        // values of the literal's own kind are reads as the raw value against the literal, every other kind in another box.
        void DefineBoxes()
        {
            var (boxOf, seesThrough, boxNull) = (Readings.BoxOf, Readings.SeesThrough, Readings.BoxNull);

            Analyse.DefineRule("a comparison that sees through a box, of a reading and a literal boxed alike, is an atom of the reading", r =>
            {
                var (e, v, w) = r.Vars<Pred, Val, Val>("e", "v", "w");
                var (u, l) = r.Vars<Val, Val>("u", "l");
                var (op, t, lt, c) = r.Vars<string, string, string, string>("op", "t", "lt", "c");
                var (k, b, p) = r.Vars<string, string, JPath>("k", "b", "p");
                r.When(e.Is(Compare.Of(op, v, w)), seesThrough.Of(op), v.Is(Box.Of(t, u)), Reads.Of(u, p, k), boxOf.Of(k, b),
                    w.Is(Box.Of(lt, l)), l.Is(Literal.Of(c)), boxOf.Of(lt, b));
                r.Then(Atom.Of(e, p, Compute.Of(k, op, c, PredicateAtom.Compare)));
            });
            Analyse.DefineRule("a comparison that sees through a box, of a raw value and a literal boxed as a value of its kind is, is an atom of the raw value", r =>
            {
                var (e, v, w, l) = r.Vars<Pred, Val, Val, Val>("e", "v", "w", "l");
                var (op, lt, c, kc) = r.Vars<string, string, string, string>("op", "lt", "c", "kc");
                var (k, b, p) = r.Vars<string, string, JPath>("k", "b", "p");
                r.When(e.Is(Compare.Of(op, v, w)), seesThrough.Of(op), v.Is(Raw.Of(p)), Reads.Of(v, p, k), w.Is(Box.Of(lt, l)), l.Is(Literal.Of(c)),
                    kc.Is(Compute.Of(c, JsonValues.TypeOf)), boxOf.Of(kc, b), boxOf.Of(lt, b));
                r.Then(Atom.Of(e, p, Compute.Of(k, op, c, PredicateAtom.Compare)));
            });
            Analyse.DefineRule("a comparison that sees through a box, of a raw value in a column and a literal boxed as a value of its kind is, is an atom of the raw value", r =>
            {
                var (e, v, w, l) = r.Vars<Pred, Val, Val, Val>("e", "v", "w", "l");
                var (op, lt, c, kc) = r.Vars<string, string, string, string>("op", "lt", "c", "kc");
                var (k, b, col) = r.Vars<string, string, string>("k", "b", "col");
                var p = r.Var<JPath>("p");
                r.When(e.Is(Compare.Of(op, v, w)), seesThrough.Of(op), v.Is(RawIn.Of(p, col)), Reads.Of(v, p, k), w.Is(Box.Of(lt, l)), l.Is(Literal.Of(c)),
                    kc.Is(Compute.Of(c, JsonValues.TypeOf)), boxOf.Of(kc, b), boxOf.Of(lt, b));
                r.Then(Atom.Of(e, p, Compute.Of(k, op, c, PredicateAtom.Compare)));
            });

            // A box's null test is what it holds where its type's row says (boxnull), each type measured apart.
            Analyse.DefineRule("a null test of a box of a type null where what it holds is is an atom of the reading it holds", r =>
            {
                var (e, v, u) = r.Vars<Pred, Val, Val>("e", "v", "u");
                var (t, k) = r.Vars<string, string>("t", "k");
                var p = r.Var<JPath>("p");
                r.When(e.Is(IsNull.Of(v)), v.Is(Box.Of(t, u)), boxNull.Of(t), Reads.Of(u, p, k));
                r.Then(Atom.Of(e, p, Compute.Of(k, PredicateAtom.IsNull)));
            });
        }

    }

}
