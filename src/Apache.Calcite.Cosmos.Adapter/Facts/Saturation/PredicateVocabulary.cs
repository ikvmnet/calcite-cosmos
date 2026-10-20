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

        public PredicateVocabulary(EGraph graph)
        {
            graph.DefineSort<Val>("Val");
            graph.DefineSort<Pred>("Pred");
            graph.Values.Register<PredicateAtom>("predicate atom");
            graph.Values.Register<PathValues>("path values");

            // Nothing here is ever extracted for execution: the host reads values back, never a term.
            var term = new TableOptions { Extractable = false };
            var analysis = new TableOptions { Role = TableRole.Analysis };

            Text = graph.DefineConstructor<JPath, Val>("text", options: term);
            Raw = graph.DefineConstructor<JPath, Val>("raw", options: term);
            Typed = graph.DefineConstructor<JPath, string, Val>("typed", options: term);
            Cast = graph.DefineConstructor<string, Val, Val>("cast", options: term);
            Literal = graph.DefineConstructor<string, Val>("literal", options: term);
            Parameter = graph.DefineConstructor<long, string, Val>("parameter", options: term);
            Compare = graph.DefineConstructor<string, Val, Val, Pred>("compare", options: term);
            In = graph.DefineConstructor<Val, ValueSet<string>, Pred>("in", options: term);
            IsNull = graph.DefineConstructor<Val, Pred>("is null", options: term);
            IsDefined = graph.DefineConstructor<Val, Pred>("is defined", options: term);
            And = graph.DefineConstructor<Pred, Pred, Pred>("and", options: term);
            Or = graph.DefineConstructor<Pred, Pred, Pred>("or", options: term);
            Not = graph.DefineConstructor<Pred, Pred>("not", options: term);
            Opaque = graph.DefineConstructor<long, Pred>("opaque", options: term);

            Point = graph.DefineRelation<Pred, string>("point", analysis);
            Reads = graph.DefineRelation<Val, JPath, string>("reads", analysis);
            Atom = graph.DefineRelation<Pred, JPath, PredicateAtom>("atom", analysis);

            Analyse = graph.DefineRuleset("analyse");
            DefineAnalysis();
        }

        /// <summary><c>JSON_VALUE(DOC, path)</c> read as text.</summary>
        public Constructor<JPath, Val> Text { get; }

        /// <summary>The value the service holds at a path.</summary>
        public Constructor<JPath, Val> Raw { get; }

        /// <summary><c>JSON_VALUE(DOC, path RETURNING type)</c>.</summary>
        public Constructor<JPath, string, Val> Typed { get; }

        /// <summary>A cast of a value to a SQL type.</summary>
        public Constructor<string, Val, Val> Cast { get; }

        /// <summary>A literal, in <see cref="JsonValues"/>' spelling.</summary>
        public Constructor<string, Val> Literal { get; }

        /// <summary>A parameter: its ordinal and SQL type, and no value.</summary>
        public Constructor<long, string, Val> Parameter { get; }

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

        /// <summary>
        /// A literal an atom compares its value with: a comparison's, or each of a membership's. Stated by the writer beside
        /// the term, because a rule cannot take the members of a set one at a time.
        /// </summary>
        public Relation<Pred, string> Point { get; }

        /// <summary>The path a value reads, and how: an analysis of terms alone.</summary>
        public Relation<Val, JPath, string> Reads { get; }

        /// <summary>A predicate over one path, as the atom that says what it means: an analysis of terms alone.</summary>
        public Relation<Pred, JPath, PredicateAtom> Atom { get; }

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
            Analyse.DefineRule("a definedness test is an atom of what it reads", r =>
            {
                var (e, v, p) = r.Vars<Pred, Val, JPath>("e", "v", "p");
                var k = r.Var<string>("k");
                r.When(e.Is(IsDefined.Of(v)), Reads.Of(v, p, k));
                r.Then(Atom.Of(e, p, Compute.Of(k, PredicateAtom.IsDefined)));
            });
        }

    }

}
