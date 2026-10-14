using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;

using Eggnog;

namespace EggnogDerive
{

    /// <summary>
    /// The JSON half of the theory: tables for each kind of claim, and the rules that relate them, which
    /// are what <see cref="JsonFact.Entails"/> and <see cref="JsonFact.Excludes"/> do as switch tables.
    /// Defined once per graph.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A path is its <c>$.a.b</c> spelling. A scalar is a tagged string whose tag is its type, so a domain
    /// is a <see cref="ValueSet{T}"/> of strings and the type of each member is read off its tag.
    /// </para>
    /// <para>
    /// Types are a set over <c>{string, integer, fraction, boolean, object, array, null}</c>, so a JSON
    /// Schema <c>number</c> is two members, <see cref="JsonTypes.IsA"/> is a subset test, and a claim
    /// that admits null adds a member.
    /// </para>
    /// </remarks>
    sealed class JsonFactTables
    {

        static readonly ValueSet<string> StringOrNull = ValueSet.Of("string", "null");

        public JsonFactTables(EGraph graph)
        {
            Graph = graph;
            Present = graph.DefineRelation<string>("present");
            Types = graph.DefineFunction<string, ValueSet<string>>("types", ValueLattice.Intersect<string>());
            Dom = graph.DefineFunction<string, ValueSet<string>>("dom", ValueLattice.Intersect<string>());
            Ne = graph.DefineRelation<string, string>("ne");
            Form = graph.DefineRelation<string, string>("form");

            Json = graph.DefineRuleset("json");
            Schema = graph.DefineRuleset("schema");
            Saturation = Schedule.Saturate(Json, Schema);

            Json.DefineRule("a domain settles the type", r =>
            {
                var (p, d) = r.Vars<string, ValueSet<string>>("p", "d");
                r.When(d.Is(Dom.Of(p)));
                r.Then(Types.Of(p).Is(Compute.Of(d, TypesOf)));
            });
            Json.DefineRule("a disequality narrows a domain", r =>
            {
                var (p, v, d) = r.Vars<string, string, ValueSet<string>>("p", "v", "d");
                r.When(Ne.Of(p, v), d.Is(Dom.Of(p)));
                r.Where(Primitives.Contains<string>().Of(d, v));
                r.Then(Dom.Of(p).Is(Primitives.Remove<string>().Of(d, v)));
            });
            Json.DefineRule("a form describes strings", r =>
            {
                var (p, f) = r.Vars<string, string>("p", "f");
                r.When(Form.Of(p, f));
                r.Then(Types.Of(p).Is(StringOrNull));
            });
            Json.DefineRule("an empty domain", r =>
            {
                var (p, d) = r.Vars<string, ValueSet<string>>("p", "d");
                r.When(d.Is(Dom.Of(p)));
                r.Where(Primitives.IsEmpty<string>().Of(d));
                r.Then(graph.Contradiction.Of());
            });

            // Two type claims alone never exclude each other: an absent path satisfies both. A value does.
            Json.DefineRule("a value of no possible type", r =>
            {
                var (p, d, t) = r.Vars<string, ValueSet<string>, ValueSet<string>>("p", "d", "t");
                r.When(d.Is(Dom.Of(p)), t.Is(Types.Of(p)));
                r.Where(Primitives.IsEmpty<string>().Of(t));
                r.Then(graph.Contradiction.Of());
            });

            // The rules of JSON itself, which Horn cannot state: it has no atom for absence, so it could
            // only ignore two type claims that conflict.
            Absent = graph.DefineRelation<string>("absent");
            Json.DefineRule("a path of no possible type is absent", r =>
            {
                var (p, t) = r.Vars<string, ValueSet<string>>("p", "t");
                r.When(t.Is(Types.Of(p)));
                r.Where(Primitives.IsEmpty<string>().Of(t));
                r.Then(Absent.Of(p));
            });
            Json.DefineRule("a path both absent and present", r =>
            {
                var p = r.Var<string>("p");
                r.When(Absent.Of(p), Present.Of(p));
                r.Then(graph.Contradiction.Of());
            });
            Json.DefineRule("a present property has a present object for a parent", r =>
            {
                var p = r.Var<string>("p");
                r.When(Present.Of(p));
                r.Where(p, x => x.LastIndexOf('.') > 1);
                var parent = Compute.Of(p, x => x.Substring(0, x.LastIndexOf('.')));
                r.Then(Present.Of(parent), Types.Of(parent).Is(ValueSet.Of("object")));
            });
        }

        public Relation<string> Absent { get; private set; } = null!;

        public EGraph Graph { get; }

        public Relation<string> Present { get; }

        public Function<string, ValueSet<string>> Types { get; }

        public Function<string, ValueSet<string>> Dom { get; }

        public Relation<string, string> Ne { get; }

        public Relation<string, string> Form { get; }

        /// <summary>The rules of JSON itself, global to the graph.</summary>
        public Ruleset Json { get; }

        /// <summary>A container's conditional rules: global in a flat graph, local to a schema scope in a layered one.</summary>
        public Ruleset Schema { get; }

        public Schedule Saturation { get; }

        public static string Path(JsonDocumentPath path) => path.ToString();

        /// <summary>A scalar as a tagged string: its type member, a bar, and a spelling unique to its value.</summary>
        public static string Encode(JsonScalar value)
        {
            var type = Member(value.Type);
            return value.ToClrValue() switch
            {
                null => "null|",
                bool b => b ? "boolean|true" : "boolean|false",
                string s => "string|" + s,
                // Two decimals of one value are one scalar (#198): strip the trailing zeros 1.0 carries.
                decimal m => type + "|" + (m / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture),
                // A double is a scalar no decimal holds, equal only to itself.
                double f => type + "|d" + f.ToString("R", CultureInfo.InvariantCulture),
                var other => throw new NotSupportedException(other.GetType().Name),
            };
        }

        public static ValueSet<string> Encode(IEnumerable<JsonScalar> values) => ValueSet.From(values.Select(Encode));

        static string Member(JsonType type) => type switch
        {
            JsonType.String => "string",
            JsonType.Integer => "integer",
            JsonType.Number => "fraction",
            JsonType.Boolean => "boolean",
            JsonType.Object => "object",
            JsonType.Array => "array",
            JsonType.Null => "null",
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

        /// <summary>The members a type claim admits.</summary>
        public static ValueSet<string> Members(JsonType type, bool orNull)
        {
            var members = new List<string>();
            if (type == JsonType.Number)
            {
                members.Add("integer");
                members.Add("fraction");
            }
            else
            {
                members.Add(Member(type));
            }

            if (orNull)
                members.Add("null");

            return ValueSet.From(members);
        }

        static ValueSet<string> TypesOf(ValueSet<string> domain) =>
            ValueSet.From(domain.Select(value => value.Substring(0, value.IndexOf('|'))));

    }

}
