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
        /// <param name="testKinds">
        /// The JSON kinds each type test is TRUE of, by its name: the graph's own rows, an analysis assumed at its root, which
        /// the type-test rule reads (<c>Calcite.TypeTestsAreTheirBodies</c>, G7).
        /// </param>
        /// <param name="testReads">
        /// The readings a one-place test's in-process body takes apart, with the reading it reads instead: the graph's own
        /// rows, an analysis assumed at its root, which the test-read rules read (<c>Calcite.TypeTestsAreTheirBodies</c>, G8).
        /// </param>
        public PredicateVocabulary(EGraph graph, Function<string, ValueSet<string>> testKinds, Relation<string, string> testReads)
        {
            TestKinds = testKinds;
            TestReads = testReads;
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
            Atom = graph.DefineRelation<Pred, JPath, PredicateAtom>("atom", analysis);
            TestRead = graph.DefineRelation<Val, JPath, string>("testread", analysis);

            Analyse = graph.DefineRuleset("analyse");
            DefineAnalysis();
        }

        /// <summary><c>JSON_VALUE(DOC, path)</c> read as text.</summary>
        public Constructor<JPath, Val> Text { get; }

        /// <summary>The value the service holds at a path.</summary>
        public Constructor<JPath, Val> Raw { get; }

        /// <summary>
        /// The value the service holds at a path, read through the column the loader knows it is in (rawtyped): written
        /// where a loading reads places, so never in F's statements; read by S's own rule (<c>RenderingTheory</c>), and by
        /// F's rules of a value's characters, where S's computed places and the translator's bridge ask F of a call over one.
        /// </summary>
        public Constructor<JPath, string, Val> RawIn { get; }

        /// <summary><c>JSON_VALUE(DOC, path RETURNING type)</c>.</summary>
        public Constructor<JPath, string, Val> Typed { get; }

        /// <summary>A cast of a value to a SQL type.</summary>
        public Constructor<string, Val, Val> Cast { get; }

        /// <summary>
        /// A cast of a number to <c>DECIMAL(precision, scale)</c>, one a comparison's other side is made of: written where a
        /// loading reads places, so never in F's statements; read by S's own rules of what a cast of a literal denotes
        /// (<c>RenderingTheory.Denotes</c>, G8).
        /// </summary>
        public Constructor<long, long, Val, Val> DecimalCast { get; }

        /// <summary>
        /// A value cast to <c>VARIANT</c>: the SQL type of what it boxes, and that value. Written where a loading reads places,
        /// so never in F's statements; read by S's own rules of what a box equals and where it is null (<c>boxof</c>,
        /// <c>seesthrough</c>, G8). It reads no path: no rule here reads one.
        /// </summary>
        public Constructor<string, Val, Val> Box { get; }

        /// <summary>
        /// SQL's null, of a SQL type: written where a loading reads places, so never in F's statements until h15; a comparison
        /// with it is read by S's rule over the operator's row (<c>strictop</c>, G8), and by no rule here.
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

        /// <summary>A predicate over one path, as the atom that says what it means: an analysis of terms alone.</summary>
        public Relation<Pred, JPath, PredicateAtom> Atom { get; }

        /// <summary>
        /// The path a one-place test of a value asks of, and the reading it reads there: what the test's in-process body
        /// takes the value's accessor apart into, where a row says it does (<see cref="TestReads"/>, G8).
        /// </summary>
        public Relation<Val, JPath, string> TestRead { get; }

        /// <summary>Reads terms into atoms.</summary>
        public Ruleset Analyse { get; }

        void DefineAnalysis()
        {
            Analyse.DefineRule("text reads its path", r =>
            {
                var (v, p) = r.Vars<Val, JPath>("v", "p");
                r.When(v.Is(Text.Of(p)));
                r.Then(Reads.Of(v, p, PredicateAtom.TextReader));
            });
            Analyse.DefineRule("a raw value reads its path", r =>
            {
                var (v, p) = r.Vars<Val, JPath>("v", "p");
                r.When(v.Is(Raw.Of(p)));
                r.Then(Reads.Of(v, p, PredicateAtom.RawReader));
            });
            Analyse.DefineRule("a returning reads its path as its type", r =>
            {
                var (v, p, t) = r.Vars<Val, JPath, string>("v", "p", "t");
                r.When(v.Is(Typed.Of(p, t)));
                r.Then(Reads.Of(v, p, Compute.Of(t, PredicateAtom.TypedReader)));
            });
            Analyse.DefineRule("a cast reads what it converts", r =>
            {
                var (v, w, p) = r.Vars<Val, Val, JPath>("v", "w", "p");
                var (t, k) = r.Vars<string, string>("t", "k");
                r.When(v.Is(Cast.Of(t, w)), Reads.Of(w, p, k));
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
            Analyse.DefineRule("a test reads the path of a raw value in a column as its row says the body reads it", r =>
            {
                var (v, p) = r.Vars<Val, JPath>("v", "p");
                var (k, c) = r.Vars<string, string>("k", "c");
                r.When(v.Is(RawIn.Of(p, c)), TestReads.Of(PredicateAtom.RawReader, k));
                r.Then(TestRead.Of(v, p, k));
            });
            Analyse.DefineRule("a test reads a returning's path as its row says the body reads it", r =>
            {
                var (v, p) = r.Vars<Val, JPath>("v", "p");
                var (k, t) = r.Vars<string, string>("k", "t");
                r.When(v.Is(Typed.Of(p, t)), TestReads.Of(PredicateAtom.TypedReaders, k));
                r.Then(TestRead.Of(v, p, k));
            });
            Analyse.DefineRule("a definedness test is an atom of what its body reads", r =>
            {
                var (e, v, p) = r.Vars<Pred, Val, JPath>("e", "v", "p");
                var k = r.Var<string>("k");
                r.When(e.Is(IsDefined.Of(v)), TestRead.Of(v, p, k));
                r.Then(Atom.Of(e, p, Compute.Of(k, PredicateAtom.IsDefined)));
            });
            Analyse.DefineRule("a type test is an atom of what its body reads, TRUE of the kinds its row says", r =>
            {
                var (e, v, p) = r.Vars<Pred, Val, JPath>("e", "v", "p");
                var (t, k) = r.Vars<string, string>("t", "k");
                var ks = r.Var<ValueSet<string>>("ks");
                r.When(e.Is(TypeTest.Of(t, v)), TestRead.Of(v, p, k), ks.Is(TestKinds.Of(t)));
                r.Then(Atom.Of(e, p, Compute.Of(k, t, ks, PredicateAtom.TypeTest)));
            });
            Analyse.DefineRule("an affix test with a literal pattern is an atom of what it reads", r =>
            {
                var (e, v, p) = r.Vars<Pred, Val, JPath>("e", "v", "p");
                var (op, c, k) = r.Vars<string, string, string>("op", "c", "k");
                r.When(e.Is(Affix.Of(op, v, c)), Reads.Of(v, p, k));
                r.Then(Atom.Of(e, p, Compute.Of(k, op, c, PredicateAtom.Affix)));
            });
        }

    }

}
