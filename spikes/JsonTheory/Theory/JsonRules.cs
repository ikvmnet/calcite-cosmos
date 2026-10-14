using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog;

using static JsonTheory.JsonValues;

namespace JsonTheory
{

    /// <summary>
    /// The rules of JSON, defined once per graph and true of every document: what a root context holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Facts are about a path, and nearly all of them are conditional on the path being there.</b> A type, a domain, a
    /// disequality or a bound says what the value is <em>if</em> there is one; JSON Schema's own keywords mean exactly
    /// that, and an absent path satisfies every one of them. Only <see cref="Present"/>, <see cref="Absent"/> and
    /// <see cref="Contains"/> say whether something is there. Keeping the two apart is what lets two claims that cannot
    /// both describe one value — a string and an object — prove something: not a contradiction, but that the path is
    /// absent. It becomes a contradiction only beside presence.
    /// </para>
    /// <para>
    /// <b>A pattern path says something of every element.</b> <c>Each(p)</c> stands for every element of the array at
    /// <c>p</c>, and <c>Prop(Each(p), n)</c> for property <c>n</c> of every element. A pattern carries conditional facts and
    /// absence, never presence, and every concrete path it covers inherits them through <see cref="Instance"/>.
    /// </para>
    /// <para>
    /// <b>No rule makes a path.</b> Rules match paths that already exist, so saturation is over the paths a schema and
    /// a query mention, and it terminates: every lattice here only narrows.
    /// </para>
    /// </remarks>
    public sealed class JsonRules
    {

        public JsonRules(EGraph graph)
        {
            Graph = graph;
            graph.DefineSort<JPath>("Path");
            graph.Values.Register<Bound>("bound");

            Root = graph.DefineConstructor<JPath>("Root");
            Prop = graph.DefineConstructor<JPath, string, JPath>("Prop");
            Index = graph.DefineConstructor<JPath, long, JPath>("Index");
            Each = graph.DefineConstructor<JPath, JPath>("Each");

            Present = graph.DefineRelation<JPath>("present");
            Absent = graph.DefineRelation<JPath>("absent");
            Types = graph.DefineFunction<JPath, ValueSet<string>>("types", ValueLattice.Intersect<string>());
            Dom = graph.DefineFunction<JPath, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            Ne = graph.DefineRelation<JPath, string>("ne");
            Lo = graph.DefineFunction<JPath, Bound>("lo", new ValueLattice<Bound>("stricter lower", Bound.StricterLower));
            Hi = graph.DefineFunction<JPath, Bound>("hi", new ValueLattice<Bound>("stricter upper", Bound.StricterUpper));
            MinLength = graph.DefineFunction<JPath, long>("minLength", ValueLattice.Max<long>());
            MaxLength = graph.DefineFunction<JPath, long>("maxLength", ValueLattice.Min<long>());
            MinItems = graph.DefineFunction<JPath, long>("minItems", ValueLattice.Max<long>());
            MaxItems = graph.DefineFunction<JPath, long>("maxItems", ValueLattice.Min<long>());
            Closed = graph.DefineFunction<JPath, ValueSet<string>>("closed", ValueLattice.Intersect<string>());
            Required = graph.DefineRelation<JPath, string>("required");
            Contains = graph.DefineRelation<JPath, string>("contains");
            Instance = graph.DefineRelation<JPath, JPath>("instance");
            graph.Values.Register<decimal>("decimal");
            MultipleOf = graph.DefineFunction<JPath, decimal>("multipleOf", new ValueLattice<decimal>("least common multiple", Multiples.Lcm));
            Unique = graph.DefineRelation<JPath>("unique");
            MinProperties = graph.DefineFunction<JPath, long>("minProperties", ValueLattice.Max<long>());
            MaxProperties = graph.DefineFunction<JPath, long>("maxProperties", ValueLattice.Min<long>());
            PresentNames = graph.DefineFunction<JPath, ValueSet<string>>("presentNames", ValueLattice.Union<string>());
            RequiredNames = graph.DefineFunction<JPath, ValueSet<string>>("requiredNames", ValueLattice.Union<string>());

            Json = graph.DefineRuleset("json");

            DefineValues();
            DefineNumbers();
            DefineStrings();
            DefineObjects();
            DefineArrays();
            DefinePatterns();
            DefineMore();
        }

        public EGraph Graph { get; }

        public Ruleset Json { get; }

        public Constructor<JPath> Root { get; }

        public Constructor<JPath, string, JPath> Prop { get; }

        public Constructor<JPath, long, JPath> Index { get; }

        public Constructor<JPath, JPath> Each { get; }

        /// <summary>The path is there.</summary>
        public Relation<JPath> Present { get; }

        /// <summary>The path is not there.</summary>
        public Relation<JPath> Absent { get; }

        /// <summary>If the path is there, its type is one of these.</summary>
        public Function<JPath, ValueSet<string>> Types { get; }

        /// <summary>If the path is there, its value is one of these scalars.</summary>
        public Function<JPath, ValueSet<string>> Dom { get; }

        /// <summary>If the path is there, its value is not this.</summary>
        public Relation<JPath, string> Ne { get; }

        /// <summary>If the path holds a number, it is at least (or above) this.</summary>
        public Function<JPath, Bound> Lo { get; }

        /// <summary>If the path holds a number, it is at most (or below) this.</summary>
        public Function<JPath, Bound> Hi { get; }

        /// <summary>If the path holds a string, it is at least this long.</summary>
        public Function<JPath, long> MinLength { get; }

        /// <summary>If the path holds a string, it is at most this long.</summary>
        public Function<JPath, long> MaxLength { get; }

        /// <summary>If the path holds an array, it has at least this many elements.</summary>
        public Function<JPath, long> MinItems { get; }

        /// <summary>If the path holds an array, it has at most this many elements.</summary>
        public Function<JPath, long> MaxItems { get; }

        /// <summary>If the path holds an object, its properties are among these.</summary>
        public Function<JPath, ValueSet<string>> Closed { get; }

        /// <summary>If the path holds an object, it has this property.</summary>
        public Relation<JPath, string> Required { get; }

        /// <summary>The path holds an array with an element equal to this scalar.</summary>
        public Relation<JPath, string> Contains { get; }

        /// <summary>The first path is one of those the second, a pattern, stands for.</summary>
        public Relation<JPath, JPath> Instance { get; }

        /// <summary>If the path holds a number, it is a multiple of this.</summary>
        public Function<JPath, decimal> MultipleOf { get; }

        /// <summary>If the path holds an array, no two of its elements are equal.</summary>
        public Relation<JPath> Unique { get; }

        /// <summary>If the path holds an object, it has at least this many properties.</summary>
        public Function<JPath, long> MinProperties { get; }

        /// <summary>If the path holds an object, it has at most this many properties.</summary>
        public Function<JPath, long> MaxProperties { get; }

        /// <summary>Properties the path is known to have, all of them present.</summary>
        public Function<JPath, ValueSet<string>> PresentNames { get; }

        /// <summary>Properties the path has if it holds an object.</summary>
        public Function<JPath, ValueSet<string>> RequiredNames { get; }

        void Rule(string name, Action<RuleBuilder> define) => Json.DefineRule(name, define);

        void DefineValues()
        {
            Rule("a value is of its own type", r =>
            {
                var (p, d) = r.Vars<JPath, ValueSet<string>>("p", "d");
                r.When(d.Is(Dom.Of(p)));
                r.Then(Types.Of(p).Is(Compute.Of(d, TypesOf)));
            });
            Rule("a boolean is true or false", r =>
            {
                var (p, t) = r.Vars<JPath, ValueSet<string>>("p", "t");
                r.When(t.Is(Types.Of(p)));
                r.Where(t, x => x.IsSubsetOf(ValueSet.Of(BooleanType)));
                r.Then(Dom.Of(p).Is(ValueSet.Of(Of(true), Of(false))));
            });
            Rule("null is one value", r =>
            {
                var (p, t) = r.Vars<JPath, ValueSet<string>>("p", "t");
                r.When(t.Is(Types.Of(p)));
                r.Where(t, x => x.IsSubsetOf(ValueSet.Of(NullType)));
                r.Then(Dom.Of(p).Is(ValueSet.Of(NullValue)));
            });
            Rule("a disequality narrows a domain", r =>
            {
                var (p, v, d) = r.Vars<JPath, string, ValueSet<string>>("p", "v", "d");
                r.When(Ne.Of(p, v), d.Is(Dom.Of(p)));
                r.Where(Primitives.Contains<string>().Of(d, v));
                r.Then(Dom.Of(p).Is(Primitives.Remove<string>().Of(d, v)));
            });
            Rule("a value that is not null is of another type", r =>
            {
                var p = r.Var<JPath>("p");
                r.When(Ne.Of(p, NullValue));
                r.Then(Types.Of(p).Is(AllTypesBut(NullType)));
            });
            Rule("a path of no possible type is absent", r =>
            {
                var (p, t) = r.Vars<JPath, ValueSet<string>>("p", "t");
                r.When(t.Is(Types.Of(p)));
                r.Where(Primitives.IsEmpty<string>().Of(t));
                r.Then(Absent.Of(p));
            });
            Rule("a path is not both absent and present", r =>
            {
                var p = r.Var<JPath>("p");
                r.When(Absent.Of(p), Present.Of(p));
                r.Then(Graph.Contradiction.Of());
            });
        }

        void DefineNumbers()
        {
            Rule("a lower bound narrows a domain", r =>
            {
                var (p, d, l) = r.Vars<JPath, ValueSet<string>, Bound>("p", "d", "l");
                r.When(d.Is(Dom.Of(p)), l.Is(Lo.Of(p)));
                r.Then(Dom.Of(p).Is(Compute.Of(d, l, AtLeast)));
            });
            Rule("an upper bound narrows a domain", r =>
            {
                var (p, d, h) = r.Vars<JPath, ValueSet<string>, Bound>("p", "d", "h");
                r.When(d.Is(Dom.Of(p)), h.Is(Hi.Of(p)));
                r.Then(Dom.Of(p).Is(Compute.Of(d, h, AtMost)));
            });
            Rule("no number lies between crossed bounds", r =>
            {
                var (p, l, h) = r.Vars<JPath, Bound, Bound>("p", "l", "h");
                r.When(l.Is(Lo.Of(p)), h.Is(Hi.Of(p)));
                r.Where(l, h, Bound.NoNumberBetween);
                r.Then(Types.Of(p).Is(AllTypesBut(IntegerType, FractionType)));
            });
            Rule("no integer lies between bounds closer than one", r =>
            {
                var (p, l, h) = r.Vars<JPath, Bound, Bound>("p", "l", "h");
                r.When(l.Is(Lo.Of(p)), h.Is(Hi.Of(p)));
                r.Where(l, h, Bound.NoIntegerBetween);
                r.Then(Types.Of(p).Is(AllTypesBut(IntegerType)));
            });
        }

        void DefineStrings()
        {
            Rule("a minimum length narrows a domain", r =>
            {
                var (p, d, n) = r.Vars<JPath, ValueSet<string>, long>("p", "d", "n");
                r.When(d.Is(Dom.Of(p)), n.Is(MinLength.Of(p)));
                r.Then(Dom.Of(p).Is(Compute.Of(d, n, LengthAtLeast)));
            });
            Rule("a maximum length narrows a domain", r =>
            {
                var (p, d, n) = r.Vars<JPath, ValueSet<string>, long>("p", "d", "n");
                r.When(d.Is(Dom.Of(p)), n.Is(MaxLength.Of(p)));
                r.Then(Dom.Of(p).Is(Compute.Of(d, n, LengthAtMost)));
            });
            Rule("no string has crossed lengths", r =>
            {
                var (p, l, h) = r.Vars<JPath, long, long>("p", "l", "h");
                r.When(l.Is(MinLength.Of(p)), h.Is(MaxLength.Of(p)));
                r.Where(Primitives.Greater.Of(l, h));
                r.Then(Types.Of(p).Is(AllTypesBut(StringType)));
            });
        }

        void DefineObjects()
        {
            Rule("a present property has a present object for a parent", r =>
            {
                var (c, p, n) = r.Vars<JPath, JPath, string>("c", "p", "n");
                r.When(Present.Of(c), c.Is(Prop.Of(p, n)));
                r.Then(Present.Of(p), Types.Of(p).Is(Types(ObjectType)));
            });
            Rule("an absent path has no properties", r =>
            {
                var (c, p, n) = r.Vars<JPath, JPath, string>("c", "p", "n");
                r.When(c.Is(Prop.Of(p, n)), Absent.Of(p));
                r.Then(Absent.Of(c));
            });
            Rule("only an object has properties", r =>
            {
                var (c, p, n) = r.Vars<JPath, JPath, string>("c", "p", "n");
                var t = r.Var<ValueSet<string>>("t");
                r.When(c.Is(Prop.Of(p, n)), t.Is(Types.Of(p)));
                r.Where(t, x => x.Contains(ObjectType) == false);
                r.Then(Absent.Of(c));
            });
            Rule("a closed object has no other properties", r =>
            {
                var (c, p, n) = r.Vars<JPath, JPath, string>("c", "p", "n");
                var s = r.Var<ValueSet<string>>("s");
                r.When(c.Is(Prop.Of(p, n)), s.Is(Closed.Of(p)));
                r.Where(s, n, (allowed, name) => allowed.Contains(name) == false);
                r.Then(Absent.Of(c));
            });
            Rule("a present object has its required properties", r =>
            {
                var (c, p, n) = r.Vars<JPath, JPath, string>("c", "p", "n");
                var t = r.Var<ValueSet<string>>("t");
                r.When(c.Is(Prop.Of(p, n)), Required.Of(p, n), Present.Of(p), t.Is(Types.Of(p)));
                r.Where(t, x => x.IsSubsetOf(Types(ObjectType)));
                r.Then(Present.Of(c));
            });
        }

        void DefineArrays()
        {
            Rule("a present element has a present array long enough to hold it", r =>
            {
                var (c, p, i) = r.Vars<JPath, JPath, long>("c", "p", "i");
                r.When(Present.Of(c), c.Is(Index.Of(p, i)));
                r.Then(Present.Of(p), Types.Of(p).Is(Types(ArrayType)), MinItems.Of(p).Is(Primitives.Add.Of(i, 1L)));
            });
            Rule("an absent path has no elements", r =>
            {
                var (c, p, i) = r.Vars<JPath, JPath, long>("c", "p", "i");
                r.When(c.Is(Index.Of(p, i)), Absent.Of(p));
                r.Then(Absent.Of(c));
            });
            Rule("only an array has elements", r =>
            {
                var (c, p, i) = r.Vars<JPath, JPath, long>("c", "p", "i");
                var t = r.Var<ValueSet<string>>("t");
                r.When(c.Is(Index.Of(p, i)), t.Is(Types.Of(p)));
                r.Where(t, x => x.Contains(ArrayType) == false);
                r.Then(Absent.Of(c));
            });
            Rule("an element beyond the most an array holds is absent", r =>
            {
                var (c, p, i) = r.Vars<JPath, JPath, long>("c", "p", "i");
                var h = r.Var<long>("h");
                r.When(c.Is(Index.Of(p, i)), h.Is(MaxItems.Of(p)));
                r.Where(Primitives.GreaterOrEqual.Of(i, h));
                r.Then(Absent.Of(c));
            });
            Rule("an element within the least a present array holds is present", r =>
            {
                var (c, p, i) = r.Vars<JPath, JPath, long>("c", "p", "i");
                var (l, t) = r.Vars<long, ValueSet<string>>("l", "t");
                r.When(c.Is(Index.Of(p, i)), Present.Of(p), t.Is(Types.Of(p)), l.Is(MinItems.Of(p)));
                r.Where(t, x => x.IsSubsetOf(Types(ArrayType)));
                r.Where(Primitives.Less.Of(i, l));
                r.Then(Present.Of(c));
            });
            Rule("no array has crossed lengths", r =>
            {
                var (p, l, h) = r.Vars<JPath, long, long>("p", "l", "h");
                r.When(l.Is(MinItems.Of(p)), h.Is(MaxItems.Of(p)));
                r.Where(Primitives.Greater.Of(l, h));
                r.Then(Types.Of(p).Is(AllTypesBut(ArrayType)));
            });
            Rule("an array holding a value is a present, non-empty array", r =>
            {
                var (p, v) = r.Vars<JPath, string>("p", "v");
                r.When(Contains.Of(p, v));
                r.Then(Present.Of(p), Types.Of(p).Is(Types(ArrayType)), MinItems.Of(p).Is(1L));
            });
            Rule("an array holds no value its elements cannot be", r =>
            {
                var (p, e, v) = r.Vars<JPath, JPath, string>("p", "e", "v");
                var d = r.Var<ValueSet<string>>("d");
                r.When(Contains.Of(p, v), e.Is(Each.Of(p)), d.Is(Dom.Of(e)));
                r.Where(d, v, (domain, value) => domain.Contains(value) == false);
                r.Then(Graph.Contradiction.Of());
            });
            Rule("an array holds no value of a type its elements cannot have", r =>
            {
                var (p, e, v) = r.Vars<JPath, JPath, string>("p", "e", "v");
                var t = r.Var<ValueSet<string>>("t");
                r.When(Contains.Of(p, v), e.Is(Each.Of(p)), t.Is(Types.Of(e)));
                r.Where(t, v, (types, value) => types.Contains(TypeOf(value)) == false);
                r.Then(Graph.Contradiction.Of());
            });
            Rule("an array holds nothing where no element can be there", r =>
            {
                var (p, e, v) = r.Vars<JPath, JPath, string>("p", "e", "v");
                r.When(Contains.Of(p, v), e.Is(Each.Of(p)), Absent.Of(e));
                r.Then(Graph.Contradiction.Of());
            });
        }

        void DefinePatterns()
        {
            Rule("an element is an instance of every element", r =>
            {
                var (c, e, p) = r.Vars<JPath, JPath, JPath>("c", "e", "p");
                var i = r.Var<long>("i");
                r.When(c.Is(Index.Of(p, i)), e.Is(Each.Of(p)));
                r.Then(Instance.Of(c, e));
            });
            Rule("a property of an instance is an instance of the property", r =>
            {
                var (c, e, x, y) = r.Vars<JPath, JPath, JPath, JPath>("c", "e", "x", "y");
                var n = r.Var<string>("n");
                r.When(Instance.Of(c, e), x.Is(Prop.Of(c, n)), y.Is(Prop.Of(e, n)));
                r.Then(Instance.Of(x, y));
            });
            Rule("an element of an instance is an instance of the element", r =>
            {
                var (c, e, x, y) = r.Vars<JPath, JPath, JPath, JPath>("c", "e", "x", "y");
                var i = r.Var<long>("i");
                r.When(Instance.Of(c, e), x.Is(Index.Of(c, i)), y.Is(Index.Of(e, i)));
                r.Then(Instance.Of(x, y));
            });
            Rule("an element of an instance is an instance of every element of the pattern", r =>
            {
                var (c, e, x, y) = r.Vars<JPath, JPath, JPath, JPath>("c", "e", "x", "y");
                var i = r.Var<long>("i");
                r.When(Instance.Of(c, e), x.Is(Index.Of(c, i)), y.Is(Each.Of(e)));
                r.Then(Instance.Of(x, y));
            });
            Rule("every element of an instance is an instance of every element", r =>
            {
                var (c, e, x, y) = r.Vars<JPath, JPath, JPath, JPath>("c", "e", "x", "y");
                r.When(Instance.Of(c, e), x.Is(Each.Of(c)), y.Is(Each.Of(e)));
                r.Then(Instance.Of(x, y));
            });

            // What a pattern says of every path it stands for. Presence is not among them: a pattern says what each
            // element is if it is there, and that some are is said by the array's length.
            Inherit("types", Types);
            Inherit("dom", Dom);
            Inherit("lo", Lo);
            Inherit("hi", Hi);
            Inherit("minLength", MinLength);
            Inherit("maxLength", MaxLength);
            Inherit("minItems", MinItems);
            Inherit("maxItems", MaxItems);
            Inherit("closed", Closed);
            Inherit("multipleOf", MultipleOf);
            Inherit("minProperties", MinProperties);
            Inherit("maxProperties", MaxProperties);
            Rule("an instance inherits uniqueness", r =>
            {
                var (c, e) = r.Vars<JPath, JPath>("c", "e");
                r.When(Instance.Of(c, e), Unique.Of(e));
                r.Then(Unique.Of(c));
            });
            Rule("an instance inherits a disequality", r =>
            {
                var (c, e, v) = r.Vars<JPath, JPath, string>("c", "e", "v");
                r.When(Instance.Of(c, e), Ne.Of(e, v));
                r.Then(Ne.Of(c, v));
            });
            Rule("an instance inherits a required property", r =>
            {
                var (c, e, n) = r.Vars<JPath, JPath, string>("c", "e", "n");
                r.When(Instance.Of(c, e), Required.Of(e, n));
                r.Then(Required.Of(c, n));
            });
            Rule("an instance inherits absence", r =>
            {
                var (c, e) = r.Vars<JPath, JPath>("c", "e");
                r.When(Instance.Of(c, e), Absent.Of(e));
                r.Then(Absent.Of(c));
            });
        }

        void DefineMore()
        {
            // Numbers: multipleOf, and a small integer range as the values it holds.
            Rule("a multiple narrows a domain", r =>
            {
                var (p, d, m) = r.Vars<JPath, ValueSet<string>, decimal>("p", "d", "m");
                r.When(d.Is(Dom.Of(p)), m.Is(MultipleOf.Of(p)));
                r.Then(Dom.Of(p).Is(Compute.Of(d, m, Multiples.Only)));
            });
            Rule("a multiple of a whole number is whole", r =>
            {
                var (p, m) = r.Vars<JPath, decimal>("p", "m");
                r.When(m.Is(MultipleOf.Of(p)));
                r.Where(m, x => x == decimal.Truncate(x));
                r.Then(Types.Of(p).Is(AllTypesBut(FractionType)));
            });
            Rule("no multiple lies between bounds", r =>
            {
                var (p, m) = r.Vars<JPath, decimal>("p", "m");
                var (l, h) = r.Vars<Bound, Bound>("l", "h");
                r.When(m.Is(MultipleOf.Of(p)), l.Is(Lo.Of(p)), h.Is(Hi.Of(p)));
                r.Where(m, l, h, Multiples.NoneBetween);
                r.Then(Types.Of(p).Is(AllTypesBut(IntegerType, FractionType)));
            });
            Rule("an integer between near bounds is one of the integers between them", r =>
            {
                var (p, t) = r.Vars<JPath, ValueSet<string>>("p", "t");
                var (l, h) = r.Vars<Bound, Bound>("l", "h");
                r.When(t.Is(Types.Of(p)), l.Is(Lo.Of(p)), h.Is(Hi.Of(p)));
                r.Where(t, x => x.IsSubsetOf(ValueSet.Of(IntegerType)));
                r.Where(l, h, (lower, upper) => Multiples.IntegersBetween(lower, upper) is { Count: <= 64 });
                r.Then(Dom.Of(p).Is(Compute.Of(l, h, (lower, upper) => ValueSet.From(Multiples.IntegersBetween(lower, upper)!.Select(Of)))));
            });

            // Arrays: uniqueItems, and what an empty or impossible element says of its array.
            Rule("a unique array is no longer than the values its elements can be", r =>
            {
                var (p, e) = r.Vars<JPath, JPath>("p", "e");
                var d = r.Var<ValueSet<string>>("d");
                r.When(Unique.Of(p), e.Is(Each.Of(p)), d.Is(Dom.Of(e)));
                r.Then(MaxItems.Of(p).Is(Primitives.Count<string>().Of(d)));
            });
            Rule("two present elements of a unique array are not one value", r =>
            {
                var (p, a, b) = r.Vars<JPath, JPath, JPath>("p", "a", "b");
                var (i, j) = r.Vars<long, long>("i", "j");
                var (da, db) = r.Vars<ValueSet<string>, ValueSet<string>>("da", "db");
                r.When(Unique.Of(p), a.Is(Index.Of(p, i)), b.Is(Index.Of(p, j)), Present.Of(a), Present.Of(b), da.Is(Dom.Of(a)), db.Is(Dom.Of(b)));
                r.Where(Primitives.Less.Of(i, j));
                r.Where(da, db, (x, y) => x.Count == 1 && x.Equals(y));
                r.Then(Graph.Contradiction.Of());
            });
            Rule("an array whose elements cannot be there is empty", r =>
            {
                var (p, e) = r.Vars<JPath, JPath>("p", "e");
                r.When(e.Is(Each.Of(p)), Absent.Of(e));
                r.Then(MaxItems.Of(p).Is(0L));
            });
            Rule("an empty array has no element", r =>
            {
                var (p, e) = r.Vars<JPath, JPath>("p", "e");
                var h = r.Var<long>("h");
                r.When(e.Is(Each.Of(p)), h.Is(MaxItems.Of(p)));
                r.Where(h, x => x == 0);
                r.Then(Absent.Of(e));
            });
            Rule("an element an array must hold and cannot means no array", r =>
            {
                var (c, p, i) = r.Vars<JPath, JPath, long>("c", "p", "i");
                var l = r.Var<long>("l");
                r.When(c.Is(Index.Of(p, i)), Absent.Of(c), l.Is(MinItems.Of(p)));
                r.Where(Primitives.Less.Of(i, l));
                r.Then(Types.Of(p).Is(AllTypesBut(ArrayType)));
            });

            // Objects: what a required property that cannot be there says, and counting properties.
            Rule("a required property that cannot be there means no object", r =>
            {
                var (c, p, n) = r.Vars<JPath, JPath, string>("c", "p", "n");
                r.When(c.Is(Prop.Of(p, n)), Required.Of(p, n), Absent.Of(c));
                r.Then(Types.Of(p).Is(AllTypesBut(ObjectType)));
            });
            Rule("a closed object cannot require what it excludes", r =>
            {
                var (p, n) = r.Vars<JPath, string>("p", "n");
                var s = r.Var<ValueSet<string>>("s");
                r.When(Required.Of(p, n), s.Is(Closed.Of(p)));
                r.Where(s, n, (allowed, name) => allowed.Contains(name) == false);
                r.Then(Types.Of(p).Is(AllTypesBut(ObjectType)));
            });
            Rule("a present property is counted", r =>
            {
                var (c, p, n) = r.Vars<JPath, JPath, string>("c", "p", "n");
                r.When(Present.Of(c), c.Is(Prop.Of(p, n)));
                r.Then(PresentNames.Of(p).Is(Primitives.Singleton<string>().Of(n)));
            });
            Rule("a required property is counted", r =>
            {
                var (p, n) = r.Vars<JPath, string>("p", "n");
                r.When(Required.Of(p, n));
                r.Then(RequiredNames.Of(p).Is(Primitives.Singleton<string>().Of(n)));
            });
            Rule("an object has no more properties than allowed", r =>
            {
                var (p, s) = r.Vars<JPath, ValueSet<string>>("p", "s");
                var m = r.Var<long>("m");
                r.When(s.Is(PresentNames.Of(p)), m.Is(MaxProperties.Of(p)));
                r.Where(s, m, (names, most) => names.Count > most);
                r.Then(Types.Of(p).Is(AllTypesBut(ObjectType)));
            });
            Rule("an object requires no more properties than allowed", r =>
            {
                var (p, s) = r.Vars<JPath, ValueSet<string>>("p", "s");
                var m = r.Var<long>("m");
                r.When(s.Is(RequiredNames.Of(p)), m.Is(MaxProperties.Of(p)));
                r.Where(s, m, (names, most) => names.Count > most);
                r.Then(Types.Of(p).Is(AllTypesBut(ObjectType)));
            });
            Rule("a closed object has no more properties than it names", r =>
            {
                var (p, s) = r.Vars<JPath, ValueSet<string>>("p", "s");
                var m = r.Var<long>("m");
                r.When(s.Is(Closed.Of(p)), m.Is(MinProperties.Of(p)));
                r.Where(s, m, (names, least) => names.Count < least);
                r.Then(Types.Of(p).Is(AllTypesBut(ObjectType)));
            });
            Rule("no object has crossed property counts", r =>
            {
                var (p, l, h) = r.Vars<JPath, long, long>("p", "l", "h");
                r.When(l.Is(MinProperties.Of(p)), h.Is(MaxProperties.Of(p)));
                r.Where(Primitives.Greater.Of(l, h));
                r.Then(Types.Of(p).Is(AllTypesBut(ObjectType)));
            });
        }

        void Inherit<T>(string name, Function<JPath, T> table) =>
            Rule($"an instance inherits {name}", r =>
            {
                var (c, e) = r.Vars<JPath, JPath>("c", "e");
                var v = r.Var<T>("v");
                r.When(Instance.Of(c, e), v.Is(table.Of(e)));
                r.Then(table.Of(c).Is(v));
            });

    }

}
