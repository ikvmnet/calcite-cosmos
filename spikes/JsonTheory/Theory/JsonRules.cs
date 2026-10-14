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
            Lo = graph.DefineFunction<JPath, Bound>("lo", new ValueLattice<Bound>("stricter lower", Bound.StricterLower, Bound.WeakerLower));
            Hi = graph.DefineFunction<JPath, Bound>("hi", new ValueLattice<Bound>("stricter upper", Bound.StricterUpper, Bound.WeakerUpper));
            MinLength = graph.DefineFunction<JPath, long>("minLength", ValueLattice.Max<long>());
            MaxLength = graph.DefineFunction<JPath, long>("maxLength", ValueLattice.Min<long>());
            MinItems = graph.DefineFunction<JPath, long>("minItems", ValueLattice.Max<long>());
            MaxItems = graph.DefineFunction<JPath, long>("maxItems", ValueLattice.Min<long>());
            Closed = graph.DefineFunction<JPath, ValueSet<string>>("closed", ValueLattice.Intersect<string>());
            Required = graph.DefineRelation<JPath, string>("required");
            Contains = graph.DefineRelation<JPath, string>("contains");
            Instance = graph.DefineRelation<JPath, JPath>("instance");
            graph.Values.Register<decimal>("decimal");
            MultipleOf = graph.DefineFunction<JPath, decimal>("multipleOf", new ValueLattice<decimal>("least common multiple", Multiples.Lcm, Multiples.Gcd));
            Unique = graph.DefineRelation<JPath>("unique");
            MinProperties = graph.DefineFunction<JPath, long>("minProperties", ValueLattice.Max<long>());
            MaxProperties = graph.DefineFunction<JPath, long>("maxProperties", ValueLattice.Min<long>());
            PresentNames = graph.DefineFunction<JPath, ValueSet<string>>("presentNames", ValueLattice.Union<string>());
            RequiredNames = graph.DefineFunction<JPath, ValueSet<string>>("requiredNames", ValueLattice.Union<string>());
            ContainsAtLeast = graph.DefineFunction<JPath, string, long>("containsAtLeast", ValueLattice.Max<long>());
            ContainsAtMost = graph.DefineFunction<JPath, string, long>("containsAtMost", ValueLattice.Min<long>());
            NameMinLength = graph.DefineFunction<JPath, long>("nameMinLength", ValueLattice.Max<long>());
            NameMaxLength = graph.DefineFunction<JPath, long>("nameMaxLength", ValueLattice.Min<long>());

            Json = graph.DefineRuleset("json");

            DefineValues();
            DefineNumbers();
            DefineStrings();
            DefineObjects();
            DefineArrays();
            DefinePatterns();
            DefineMore();
            DefineContainsAndNames();
            DefineEquality();
            DefineGuarded();
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

        /// <summary>If the path holds an array, at least this many of its elements equal the scalar (<c>contains</c>, <c>minContains</c>).</summary>
        public Function<JPath, string, long> ContainsAtLeast { get; }

        /// <summary>If the path holds an array, at most this many of its elements equal the scalar (<c>maxContains</c>).</summary>
        public Function<JPath, string, long> ContainsAtMost { get; }

        /// <summary>If the path holds an object, every property name is at least this long (<c>propertyNames</c>).</summary>
        public Function<JPath, long> NameMinLength { get; }

        /// <summary>If the path holds an object, every property name is at most this long (<c>propertyNames</c>).</summary>
        public Function<JPath, long> NameMaxLength { get; }

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
            Rule("an absent path has no possible type", r =>
            {
                var p = r.Var<JPath>("p");
                r.When(Absent.Of(p));
                r.Then(Types.Of(p).Is(ValueSet.Of<string>()));
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
            Inherit("nameMinLength", NameMinLength);
            Inherit("nameMaxLength", NameMaxLength);
            Rule("an instance inherits a least count of a value", r =>
            {
                var (c, e, v) = r.Vars<JPath, JPath, string>("c", "e", "v");
                var n = r.Var<long>("n");
                r.When(Instance.Of(c, e), n.Is(ContainsAtLeast.Of(e, v)));
                r.Then(ContainsAtLeast.Of(c, v).Is(n));
            });
            Rule("an instance inherits a most count of a value", r =>
            {
                var (c, e, v) = r.Vars<JPath, JPath, string>("c", "e", "v");
                var n = r.Var<long>("n");
                r.When(Instance.Of(c, e), n.Is(ContainsAtMost.Of(e, v)));
                r.Then(ContainsAtMost.Of(c, v).Is(n));
            });
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

        void DefineContainsAndNames()
        {
            // contains, minContains and maxContains, for a scalar the elements are compared with.
            Rule("an array holding a value holds it at least once", r =>
            {
                var (p, v) = r.Vars<JPath, string>("p", "v");
                r.When(Contains.Of(p, v));
                r.Then(ContainsAtLeast.Of(p, v).Is(1L));
            });
            Rule("an array holding a value n times is at least n long", r =>
            {
                var (p, v) = r.Vars<JPath, string>("p", "v");
                var n = r.Var<long>("n");
                r.When(n.Is(ContainsAtLeast.Of(p, v)));
                r.Then(MinItems.Of(p).Is(n));
            });
            Rule("an array cannot hold a value its elements cannot be", r =>
            {
                var (p, e, v) = r.Vars<JPath, JPath, string>("p", "e", "v");
                var (n, d) = r.Vars<long, ValueSet<string>>("n", "d");
                r.When(n.Is(ContainsAtLeast.Of(p, v)), e.Is(Each.Of(p)), d.Is(Dom.Of(e)));
                r.Where(Primitives.GreaterOrEqual.Of(n, 1L));
                r.Where(d, v, (domain, value) => domain.Contains(value) == false);
                r.Then(Types.Of(p).Is(AllTypesBut(ArrayType)));
            });
            Rule("an array cannot hold a value of a type its elements cannot have", r =>
            {
                var (p, e, v) = r.Vars<JPath, JPath, string>("p", "e", "v");
                var (n, t) = r.Vars<long, ValueSet<string>>("n", "t");
                r.When(n.Is(ContainsAtLeast.Of(p, v)), e.Is(Each.Of(p)), t.Is(Types.Of(e)));
                r.Where(Primitives.GreaterOrEqual.Of(n, 1L));
                r.Where(t, v, (types, value) => types.Contains(TypeOf(value)) == false);
                r.Then(Types.Of(p).Is(AllTypesBut(ArrayType)));
            });
            Rule("an array cannot hold a value its elements are all not", r =>
            {
                var (p, e, v) = r.Vars<JPath, JPath, string>("p", "e", "v");
                var n = r.Var<long>("n");
                r.When(n.Is(ContainsAtLeast.Of(p, v)), e.Is(Each.Of(p)), Ne.Of(e, v));
                r.Where(Primitives.GreaterOrEqual.Of(n, 1L));
                r.Then(Types.Of(p).Is(AllTypesBut(ArrayType)));
            });
            Rule("a unique array holds a value at most once", r =>
            {
                var (p, v) = r.Vars<JPath, string>("p", "v");
                var n = r.Var<long>("n");
                r.When(Unique.Of(p), n.Is(ContainsAtLeast.Of(p, v)));
                r.Then(ContainsAtMost.Of(p, v).Is(1L));
            });
            Rule("no array holds a value more often than at most", r =>
            {
                var (p, v) = r.Vars<JPath, string>("p", "v");
                var (l, h) = r.Vars<long, long>("l", "h");
                r.When(l.Is(ContainsAtLeast.Of(p, v)), h.Is(ContainsAtMost.Of(p, v)));
                r.Where(Primitives.Greater.Of(l, h));
                r.Then(Types.Of(p).Is(AllTypesBut(ArrayType)));
            });
            Rule("an array holding a value no times has no element equal to it", r =>
            {
                var (p, e, v) = r.Vars<JPath, JPath, string>("p", "e", "v");
                var h = r.Var<long>("h");
                r.When(h.Is(ContainsAtMost.Of(p, v)), e.Is(Each.Of(p)));
                r.Where(h, x => x == 0);
                r.Then(Ne.Of(e, v));
            });

            Rule("an array holding a value no times has no element equal to it, element by element", r =>
            {
                var (p, c, v) = r.Vars<JPath, JPath, string>("p", "c", "v");
                var (i, h) = r.Vars<long, long>("i", "h");
                r.When(h.Is(ContainsAtMost.Of(p, v)), c.Is(Index.Of(p, i)));
                r.Where(h, x => x == 0);
                r.Then(Ne.Of(c, v));
            });

            // propertyNames, for the length of a name.
            Rule("a property whose name is too short is absent", r =>
            {
                var (c, p, n) = r.Vars<JPath, JPath, string>("c", "p", "n");
                var l = r.Var<long>("l");
                r.When(c.Is(Prop.Of(p, n)), l.Is(NameMinLength.Of(p)));
                r.Where(n, l, (name, least) => name.Length < least);
                r.Then(Absent.Of(c));
            });
            Rule("a property whose name is too long is absent", r =>
            {
                var (c, p, n) = r.Vars<JPath, JPath, string>("c", "p", "n");
                var h = r.Var<long>("h");
                r.When(c.Is(Prop.Of(p, n)), h.Is(NameMaxLength.Of(p)));
                r.Where(n, h, (name, most) => name.Length > most);
                r.Then(Absent.Of(c));
            });
        }

        void DefineEquality()
        {
            // Two paths holding one value are one class of path, so congruence makes their properties and elements one
            // too, and every fact about either is a fact about both. No value contains itself, so a path equal to one
            // of its own descendants holds nothing.
            Rule("a value is not one of its own properties", r =>
            {
                var (p, n) = r.Vars<JPath, string>("p", "n");
                r.When(p.Is(Prop.Of(p, n)));
                r.Then(Absent.Of(p));
            });
            Rule("a value is not one of its own elements", r =>
            {
                var (p, i) = r.Vars<JPath, long>("p", "i");
                r.When(p.Is(Index.Of(p, i)));
                r.Then(Absent.Of(p));
            });
        }

        /// <summary>A hidden discriminator: no place in any document, standing for which branch of a union applies.</summary>
        public Constructor<string, JPath> Ghost { get; private set; } = null!;

        /// <summary>A path a split may open one case per value of.</summary>
        public Relation<JPath> Discriminator { get; private set; } = null!;

        /// <summary>A path a query mentions.</summary>
        public Relation<JPath> Mentioned { get; private set; } = null!;

        /// <summary>
        /// A path whose facts bear on what a query mentions: the paths it mentions, the paths around them, and the
        /// paths a conditional rule's body reads where its head speaks of a relevant path. A query splits only on
        /// relevant discriminators.
        /// </summary>
        public Relation<JPath> Relevant { get; private set; } = null!;

        internal Relation<long, JPath> HeadPath { get; private set; } = null!;
        internal Relation<long, JPath> AtomPath { get; private set; } = null!;

        /// <summary>The split a schema's context opens on every discriminator, to lift what every branch implies.</summary>
        public SplitRule SchemaSplit { get; private set; } = null!;

        /// <summary>The split a query's context opens on discriminators it mentions.</summary>
        public SplitRule QuerySplit { get; private set; } = null!;

        /// <summary>The rules of JSON to saturation, without splitting.</summary>
        public Schedule Settle { get; private set; } = null!;

        public Schedule SchemaSaturation { get; private set; } = null!;

        public Schedule QuerySaturation { get; private set; } = null!;

        internal Relation<long, long> Step { get; private set; } = null!;
        internal Relation<long, long> Length { get; private set; } = null!;
        internal Relation<long> Done { get; private set; } = null!;
        internal Relation<JPath, string> Pinned { get; private set; } = null!;
        internal Relation<long, long, JPath> AtomPresent { get; private set; } = null!;
        internal Relation<long, long, JPath> AtomAbsent { get; private set; } = null!;
        internal Relation<long, long, JPath, string> AtomEqual { get; private set; } = null!;
        internal Relation<long, long, JPath, ValueSet<string>> AtomIn { get; private set; } = null!;
        internal Relation<long, long, JPath, ValueSet<string>> AtomType { get; private set; } = null!;
        internal Relation<long, long, JPath, string> AtomNe { get; private set; } = null!;
        internal Relation<long, JPath> HeadPresent { get; private set; } = null!;
        internal Relation<long, JPath> HeadAbsent { get; private set; } = null!;
        internal Relation<long, JPath, ValueSet<string>> HeadType { get; private set; } = null!;
        internal Relation<long, JPath, ValueSet<string>> HeadDom { get; private set; } = null!;
        internal Relation<long, JPath, string> HeadNe { get; private set; } = null!;
        internal Relation<long, JPath, Bound> HeadLo { get; private set; } = null!;
        internal Relation<long, JPath, Bound> HeadHi { get; private set; } = null!;
        internal Relation<long, JPath, long> HeadMinItems { get; private set; } = null!;
        internal Relation<long, JPath, long> HeadMaxItems { get; private set; } = null!;
        internal Relation<long, JPath, string> HeadRequired { get; private set; } = null!;
        internal Relation<long, JPath, ValueSet<string>> HeadClosed { get; private set; } = null!;

        /// <summary>
        /// A schema's conditional statements — a <c>oneOf</c> branch under its discriminator, an <c>if</c>/<c>then</c> —
        /// held as rows of the schema's context and read by these fixed rules, so that a query costs the rules it
        /// fires rather than the rules there are. Rule <c>r</c>'s body is a chain of atoms: <c>step(r, k)</c> says its
        /// first <c>k</c> hold, an atom keyed <c>(r, k)</c> extends it, and once it is complete the head rows of
        /// <c>r</c> become facts.
        /// </summary>
        void DefineGuarded()
        {
            var g = Graph;
            var local = new TableOptions { Lift = false };
            Ghost = g.DefineConstructor<string, JPath>("Ghost");
            Discriminator = g.DefineRelation<JPath>("discriminator");
            Mentioned = g.DefineRelation<JPath>("mentioned", local);
            Relevant = g.DefineRelation<JPath>("relevant", local);
            HeadPath = g.DefineRelation<long, JPath>("head_path", local);
            AtomPath = g.DefineRelation<long, JPath>("atom_path", local);
            Step = g.DefineRelation<long, long>("step", local);
            Length = g.DefineRelation<long, long>("length", local);
            Done = g.DefineRelation<long>("done", local);
            Pinned = g.DefineRelation<JPath, string>("pinned", local);
            AtomPresent = g.DefineRelation<long, long, JPath>("atom_present", local);
            AtomAbsent = g.DefineRelation<long, long, JPath>("atom_absent", local);
            AtomEqual = g.DefineRelation<long, long, JPath, string>("atom_equal", local);
            AtomIn = g.DefineRelation<long, long, JPath, ValueSet<string>>("atom_in", local);
            AtomType = g.DefineRelation<long, long, JPath, ValueSet<string>>("atom_type", local);
            AtomNe = g.DefineRelation<long, long, JPath, string>("atom_ne", local);
            HeadPresent = g.DefineRelation<long, JPath>("head_present", local);
            HeadAbsent = g.DefineRelation<long, JPath>("head_absent", local);
            HeadType = g.DefineRelation<long, JPath, ValueSet<string>>("head_types", local);
            HeadDom = g.DefineRelation<long, JPath, ValueSet<string>>("head_dom", local);
            HeadNe = g.DefineRelation<long, JPath, string>("head_ne", local);
            HeadLo = g.DefineRelation<long, JPath, Bound>("head_lo", local);
            HeadHi = g.DefineRelation<long, JPath, Bound>("head_hi", local);
            HeadMinItems = g.DefineRelation<long, JPath, long>("head_minItems", local);
            HeadMaxItems = g.DefineRelation<long, JPath, long>("head_maxItems", local);
            HeadRequired = g.DefineRelation<long, JPath, string>("head_required", local);
            HeadClosed = g.DefineRelation<long, JPath, ValueSet<string>>("head_closed", local);

            Rule("a singleton domain pins its value", r =>
            {
                var (p, d) = r.Vars<JPath, ValueSet<string>>("p", "d");
                r.When(d.Is(Dom.Of(p)));
                r.Where(d, x => x.Count == 1);
                r.Then(Pinned.Of(p, Compute.Of(d, x => x[0])));
            });

            void Extends(string name, Func<RuleBuilder, Var<long>, Var<long>, Fact[]> atom, Action<RuleBuilder>? where = null) =>
                Rule("a body is extended by " + name, r =>
                {
                    var (id, k) = r.Vars<long, long>("r", "k");
                    r.When(new[] { Step.Of(id, k) }.Concat(atom(r, id, k)).ToArray());
                    where?.Invoke(r);
                    r.Then(Step.Of(id, Primitives.Add.Of(k, 1L)));
                });

            Var<JPath> P(RuleBuilder r) => r.Var<JPath>("p");
            Extends("a present path", (r, id, k) => { var p = P(r); return new[] { AtomPresent.Of(id, k, p), Present.Of(p) }; });
            Extends("an absent path", (r, id, k) => { var p = P(r); return new[] { AtomAbsent.Of(id, k, p), Absent.Of(p) }; });
            Extends("a pinned value", (r, id, k) => { var p = P(r); var v = r.Var<string>("v"); return new[] { AtomEqual.Of(id, k, p, v), Pinned.Of(p, v) }; });
            Rule("a body is extended by a domain within a set", r =>
            {
                var (id, k, p) = r.Vars<long, long, JPath>("r", "k", "p");
                var (s, d) = r.Vars<ValueSet<string>, ValueSet<string>>("s", "d");
                r.When(Step.Of(id, k), AtomIn.Of(id, k, p, s), d.Is(Dom.Of(p)));
                r.Where(Primitives.IsSubsetOf<string>().Of(d, s));
                r.Then(Step.Of(id, Primitives.Add.Of(k, 1L)));
            });
            Rule("a body is extended by types within a set", r =>
            {
                var (id, k, p) = r.Vars<long, long, JPath>("r", "k", "p");
                var (s, t) = r.Vars<ValueSet<string>, ValueSet<string>>("s", "t");
                r.When(Step.Of(id, k), AtomType.Of(id, k, p, s), t.Is(Types.Of(p)));
                r.Where(Primitives.IsSubsetOf<string>().Of(t, s));
                r.Then(Step.Of(id, Primitives.Add.Of(k, 1L)));
            });
            Rule("a body is extended by a stated disequality", r =>
            {
                var (id, k, p) = r.Vars<long, long, JPath>("r", "k", "p");
                var v = r.Var<string>("v");
                r.When(Step.Of(id, k), AtomNe.Of(id, k, p, v), Ne.Of(p, v));
                r.Then(Step.Of(id, Primitives.Add.Of(k, 1L)));
            });
            Rule("a body is extended by a disequality a domain proves", r =>
            {
                var (id, k, p) = r.Vars<long, long, JPath>("r", "k", "p");
                var (v, d) = r.Vars<string, ValueSet<string>>("v", "d");
                r.When(Step.Of(id, k), AtomNe.Of(id, k, p, v), d.Is(Dom.Of(p)));
                r.Where(d, v, (domain, value) => domain.Contains(value) == false);
                r.Then(Step.Of(id, Primitives.Add.Of(k, 1L)));
            });
            Rule("a complete body", r =>
            {
                var (id, n) = r.Vars<long, long>("r", "n");
                r.When(Step.Of(id, n), Length.Of(id, n));
                r.Then(Done.Of(id));
            });

            void Head<T>(string name, Relation<long, JPath, T> table, Func<Term<JPath>, Term<T>, Fact> fact) =>
                Rule("a complete body states " + name, r =>
                {
                    var (id, p) = r.Vars<long, JPath>("r", "p");
                    var v = r.Var<T>("v");
                    r.When(Done.Of(id), table.Of(id, p, v));
                    r.Then(fact(p, v));
                });

            Rule("a complete body states presence", r =>
            {
                var (id, p) = r.Vars<long, JPath>("r", "p");
                r.When(Done.Of(id), HeadPresent.Of(id, p));
                r.Then(Present.Of(p));
            });
            Rule("a complete body states absence", r =>
            {
                var (id, p) = r.Vars<long, JPath>("r", "p");
                r.When(Done.Of(id), HeadAbsent.Of(id, p));
                r.Then(Absent.Of(p));
            });
            Head("types", HeadType, (p, v) => Types.Of(p).Is(v));
            Head("a domain", HeadDom, (p, v) => Dom.Of(p).Is(v));
            Head("a disequality", HeadNe, (p, v) => Ne.Of(p, v));
            Head("a lower bound", HeadLo, (p, v) => Lo.Of(p).Is(v));
            Head("an upper bound", HeadHi, (p, v) => Hi.Of(p).Is(v));
            Head("a least length", HeadMinItems, (p, v) => MinItems.Of(p).Is(v));
            Head("a most length", HeadMaxItems, (p, v) => MaxItems.Of(p).Is(v));
            Head("a required property", HeadRequired, (p, v) => Required.Of(p, v));
            Head("a closed object", HeadClosed, (p, v) => Closed.Of(p).Is(v));

            // What a query mentions is relevant, and so is what bears on it: a path's parent and array, the patterns it
            // is an instance of, and what a conditional rule reads where its head speaks of a relevant path. Not a
            // path's properties: every path is a property of the root, so that would make everything relevant. The
            // gate decides only where to split, so a split it skips proves less and never anything false.
            Rule("a mentioned path is relevant", r =>
            {
                var p = r.Var<JPath>("p");
                r.When(Mentioned.Of(p));
                r.Then(Relevant.Of(p));
            });
            Rule("the parent of a relevant path is relevant", r =>
            {
                var (c, p, n) = r.Vars<JPath, JPath, string>("c", "p", "n");
                r.When(Relevant.Of(c), c.Is(Prop.Of(p, n)));
                r.Then(Relevant.Of(p));
            });
            Rule("the array of a relevant element is relevant", r =>
            {
                var (c, p, i) = r.Vars<JPath, JPath, long>("c", "p", "i");
                r.When(Relevant.Of(c), c.Is(Index.Of(p, i)));
                r.Then(Relevant.Of(p));
            });
            Rule("the pattern of a relevant instance is relevant", r =>
            {
                var (c, e) = r.Vars<JPath, JPath>("c", "e");
                r.When(Relevant.Of(c), Instance.Of(c, e));
                r.Then(Relevant.Of(e));
            });
            Rule("what a rule reads is relevant where what it states is", r =>
            {
                var (id, h, p) = r.Vars<long, JPath, JPath>("r", "h", "p");
                r.When(Relevant.Of(h), HeadPath.Of(id, h), AtomPath.Of(id, p));
                r.Then(Relevant.Of(p));
            });

            // One case per value a present discriminator can still hold: the cases cover the context, because the path
            // is there and its value is one of the domain's. What every case proves is lifted, and a case may split
            // again on a discriminator its own branch makes present.
            SchemaSplit = g.DefineDomainSplit<JPath, string>("discriminator", Dom, new[] { Discriminator, Present });
            QuerySplit = g.DefineDomainSplit<JPath, string>("relevant discriminator", Dom, new[] { Discriminator, Present, Relevant });
            Settle = Schedule.Saturate(Json);
            SchemaSaturation = Schedule.Saturate(Settle, Schedule.Split(SchemaSplit, Schedule.Saturate(Settle, Schedule.Split(SchemaSplit, Settle))));
            QuerySaturation = Schedule.Saturate(Settle, Schedule.Split(QuerySplit, Schedule.Saturate(Settle, Schedule.Split(QuerySplit, Settle))));
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
