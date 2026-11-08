using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// A Cosmos SQL condition as a value, never a class, so no rule can merge two: a test, a comparison or a membership of
    /// one path, a path written bare, a constant, or a connective over others, its members as written. Its canonical text
    /// is its equality, hash and order, with the reader each leaf was judged over where one carries one; the structural
    /// helpers take it apart and decide nothing (<c>DESIGN.md</c>).
    /// </summary>
    internal sealed class ServiceForm : IEquatable<ServiceForm>, IComparable<ServiceForm>
    {

        static readonly ServiceForm[] Leaf = [];

        ServiceForm(ServiceFormKind kind, CosmosServiceTest test, string? path, string? literal, ValueSet<string> literals, ValueSet<string> paths, ServiceForm[] members, string text,
            string? reader = null, string? right = null)
        {
            Kind = kind;
            Reader = reader;
            Right = right;
            var readers = members.Length == 0 ? reader ?? "" : string.Join(",", members.Select(m => m._readers));
            (_readers, _key) = (readers, readers.Any(c => c != ',') ? text + " @" + readers : text);
            Test = test;
            Path = path;
            Literal = literal;
            Literals = literals;
            Paths = paths;
            Members = members;
            Text = text;
            Nodes = kind switch
            {
                ServiceFormKind.Not => 1 + members[0].Nodes,
                ServiceFormKind.And or ServiceFormKind.Or => members.Sum(m => m.Nodes) + members.Length - 1,
                ServiceFormKind.Choose => members.Sum(m => m.Nodes) + 2,
                _ => 1,
            };
            Groups = members.Sum(m => m.Groups) + (kind is ServiceFormKind.And or ServiceFormKind.Or ? 1 : 0);
        }

        public ServiceFormKind Kind { get; }

        /// <summary>
        /// The reader S judged a comparison or a membership over, where its literals hold no string (<see cref="Read"/>): what
        /// the Rex builder writes it through. Never printed; <c>null</c> for every other form.
        /// </summary>
        public string? Reader { get; }

        readonly string _readers, _key;

        /// <summary>What the service is asked: the test, the operator or the connective; an opaque form reads as bare.</summary>
        public CosmosServiceTest Test { get; }

        /// <summary>The path a leaf reads; <c>null</c> for a constant, an opaque form and a compound.</summary>
        public string? Path { get; }

        /// <summary>The path a comparison of two paths compares <see cref="Path"/> with; <c>null</c> for every other form.</summary>
        public string? Right { get; }

        /// <summary>What a comparison compares with, or a constant's value.</summary>
        public string? Literal { get; }

        /// <summary>Every literal in the form.</summary>
        public ValueSet<string> Literals { get; }

        /// <summary>Every path the form reads.</summary>
        public ValueSet<string> Paths { get; }

        /// <summary>A negation's operand, or a junction's members as written; none for a leaf.</summary>
        public IReadOnlyList<ServiceForm> Members { get; }

        /// <summary>A junction's members, each of its own connective's in place of its members: what its value is a join of.</summary>
        public IEnumerable<ServiceForm> Flat => Members.SelectMany(m => Joins(m) ? m.Flat : [m]);

        /// <summary>What a junction's arm may take of it: a member, or one within a member of its own connective, however deep.</summary>
        public IEnumerable<ServiceForm> Parts => Members.SelectMany(m => Joins(m) ? m.Parts.Prepend(m) : [m]);

        bool Joins(ServiceForm member) => member.Kind == Kind && Kind is ServiceFormKind.And or ServiceFormKind.Or;

        public string Text { get; }

        /// <summary>Its size, the price of an exact form: a leaf 1, a <c>NOT</c> one more than its operand, a junction its members and the connectives between them.</summary>
        public int Nodes { get; }

        /// <summary>
        /// Its junctions, each a group the printer writes in parentheses: of two forms of one size the one with fewer prints
        /// the shorter SQL, so the price keeps a junction its connective's laws flatten below the junction it flattens.
        /// </summary>
        public int Groups { get; }

        /// <summary>The form and every form within it, through any connective: unlike <see cref="Parts"/>, which keeps to its own.</summary>
        public IEnumerable<ServiceForm> Subforms => Members.SelectMany(m => m.Subforms).Prepend(this);

        public static ServiceForm TestOf(CosmosServiceTest test, string path) =>
            new(ServiceFormKind.Test, test, path, null, ValueSet<string>.Empty, ValueSet.Of(path), Leaf, NameOf(test) + "(" + path + ")");

        public static ServiceForm CompareOf(CosmosServiceTest op, string path, string literal) =>
            new(ServiceFormKind.Compare, op, path, literal, ValueSet.Of(literal), ValueSet.Of(path), Leaf, "(" + path + " " + OperatorOf(op) + " " + SqlValue.Quote(literal) + ")");

        /// <summary>A path compared with another, read as the service reads each (h11).</summary>
        public static ServiceForm ComparePathsOf(CosmosServiceTest op, string left, string right) =>
            new(ServiceFormKind.ComparePaths, op, left, null, ValueSet<string>.Empty, ValueSet.Of(left, right), Leaf, "(" + left + " " + OperatorOf(op) + " " + right + ")", right: right);

        /// <summary>
        /// A path matched against a string (h07): <c>STARTSWITH</c>, <c>ENDSWITH</c> or <c>CONTAINS</c> of the argument, case
        /// sensitive, or <c>LIKE</c> of a pattern, as asked; whether it says what an atom asks is the gate's.
        /// </summary>
        public static ServiceForm AffixOf(CosmosServiceTest test, string path, string argument) => Affixes.Contains(test)
            ? new(ServiceFormKind.Affix, test, path, JsonValues.Of(argument), ValueSet.Of(JsonValues.Of(argument)), ValueSet.Of(path), Leaf,
                test == CosmosServiceTest.Like ? "(" + path + " LIKE " + SqlValue.Quote(JsonValues.Of(argument)) + ")" : NameOf(test) + "(" + path + ", " + SqlValue.Quote(JsonValues.Of(argument)) + ")")
            : throw new ArgumentOutOfRangeException(nameof(test), test, null);

        /// <summary>The tests an affix form asks: <c>STARTSWITH</c>, <c>ENDSWITH</c>, <c>CONTAINS</c> and <c>LIKE</c>.</summary>
        public static ValueSet<CosmosServiceTest> Affixes { get; } = ValueSet.Of(CosmosServiceTest.StartsWith, CosmosServiceTest.EndsWith, CosmosServiceTest.Contains, CosmosServiceTest.Like);

        /// <summary>
        /// An affix's pattern as a side's rows read it (<see cref="AffixPattern.Of"/>, G7); <c>null</c> for any other form, and
        /// for a <c>LIKE</c> holding a unit the rows leave unread.
        /// </summary>
        public LikePattern? PatternOf(ValueSet<LikeUnit> units) => Kind == ServiceFormKind.Affix ? AffixPattern.Of(Test, JsonValues.StringOf(Literal!), units) : null;

        /// <summary>A test's name in the service's SQL, which a form's text and the printer both spell it by.</summary>
        public static string NameOf(CosmosServiceTest test) => SpellingOf(test) ?? throw new ArgumentOutOfRangeException(nameof(test), test, null);

        /// <summary>The test the service spells by a name (<see cref="SpellingOf"/>); <c>null</c> for a name it spells none by.</summary>
        public static CosmosServiceTest? TestNamed(string name) =>
            Enum.GetValues<CosmosServiceTest>().Where(test => SpellingOf(test) == name).Select(test => (CosmosServiceTest?)test).FirstOrDefault();

        /// <summary>A test's name in the service's SQL; <c>null</c> for one the service spells by no name: a comparison or a connective.</summary>
        public static string? SpellingOf(CosmosServiceTest test) => test switch
        {
            CosmosServiceTest.IsDefined => "IS_DEFINED",
            CosmosServiceTest.IsNull => "IS_NULL",
            CosmosServiceTest.IsPrimitive => "IS_PRIMITIVE",
            CosmosServiceTest.IsString => "IS_STRING",
            CosmosServiceTest.IsNumber => "IS_NUMBER",
            CosmosServiceTest.IsBool => "IS_BOOL",
            CosmosServiceTest.IsObject => "IS_OBJECT",
            CosmosServiceTest.IsArray => "IS_ARRAY",
            CosmosServiceTest.StartsWith => "STARTSWITH",
            CosmosServiceTest.EndsWith => "ENDSWITH",
            CosmosServiceTest.Contains => "CONTAINS",
            _ => null,
        };

        /// <summary>A comparison's operator in the service's SQL, which a form's text and the printer both spell it by.</summary>
        public static string OperatorOf(CosmosServiceTest op) => op switch
        {
            CosmosServiceTest.Equal => "=",
            CosmosServiceTest.NotEqual => "!=",
            CosmosServiceTest.Less => "<",
            CosmosServiceTest.LessOrEqual => "<=",
            CosmosServiceTest.Greater => ">",
            CosmosServiceTest.GreaterOrEqual => ">=",
            _ => throw new ArgumentOutOfRangeException(nameof(op), op, null),
        };

        public static ServiceForm InOf(string path, ValueSet<string> literals) =>
            new(ServiceFormKind.In, CosmosServiceTest.In, path, null, literals, ValueSet.Of(path), Leaf, "(" + path + " IN (" + string.Join(", ", literals.Select(SqlValue.Quote)) + "))");

        public static ServiceForm BareOf(string path) => new(ServiceFormKind.Bare, CosmosServiceTest.Bare, path, null, ValueSet<string>.Empty, ValueSet.Of(path), Leaf, path);

        public static ServiceForm ConstantOf(bool value) =>
            new(ServiceFormKind.Constant, CosmosServiceTest.Constant, null, JsonValues.Of(value), ValueSet.Of(JsonValues.Of(value)), ValueSet<string>.Empty, Leaf, value ? "true" : "false");

        public static ServiceForm NotOf(ServiceForm operand) => Compound(ServiceFormKind.Not, CosmosServiceTest.Not, [operand], "(NOT " + operand.Text + ")");

        /// <summary>An <c>AND</c> or an <c>OR</c> of two or more members, as written.</summary>
        public static ServiceForm JunctionOf(CosmosServiceTest connective, IEnumerable<ServiceForm> members)
        {
            var written = members.ToArray();
            if (connective is not (CosmosServiceTest.And or CosmosServiceTest.Or) || written.Length < 2)
                throw new ArgumentException("A junction is an AND or an OR of two members or more.", nameof(members));

            var kind = connective == CosmosServiceTest.And ? ServiceFormKind.And : ServiceFormKind.Or;
            return Compound(kind, connective, written, "(" + string.Join(" " + kind.ToString().ToUpperInvariant() + " ", written.Select(m => m.Text)) + ")");
        }

        /// <summary>The ternary <c>c ? a : b</c>: <paramref name="then"/> where the condition is true, <paramref name="otherwise"/> elsewhere.</summary>
        public static ServiceForm ChosenOf(ServiceForm condition, ServiceForm then, ServiceForm otherwise) =>
            Compound(ServiceFormKind.Choose, CosmosServiceTest.Choose, [condition, then, otherwise], "(" + condition.Text + " ? " + then.Text + " : " + otherwise.Text + ")");

        /// <summary>A form negated where a row says so (<see cref="Negated"/>), and as it is otherwise: the else rows' sign of a condition.</summary>
        public static ServiceForm Signed(bool negated, ServiceForm form) => negated ? Negated(form) : form;

        /// <summary>Text the reader could not read, over the paths it names: the service's answer on it is not known.</summary>
        public static ServiceForm OpaqueOf(string text, ValueSet<string> paths) =>
            new(ServiceFormKind.Opaque, CosmosServiceTest.Bare, null, null, ValueSet<string>.Empty, paths, Leaf, "OPAQUE(" + SqlValue.Quote(text) + ")");

        /// <summary>
        /// A form whose every comparison and membership holding no string literal carries the reader it was judged over,
        /// the one a template proposed it for: a value change, which no rule reads but the Rex grammar, so the builder writes
        /// each member through the accessor it was proven over (spec §4.2 h09). A string is written over the raw value
        /// whatever the reader, and a raw value's number has no accessor of its own, so neither carries one.
        /// </summary>
        public static ServiceForm Read(ServiceForm form, string reader) => form.Kind switch
        {
            _ when PredicateAtom.IsRaw(reader) => form,
            ServiceFormKind.Compare or ServiceFormKind.In when form.Literals.Count > 0 && form.Literals.Any(JsonValues.IsString) == false =>
                new(form.Kind, form.Test, form.Path, form.Literal, form.Literals, form.Paths, Leaf, form.Text, reader),
            ServiceFormKind.Not or ServiceFormKind.And or ServiceFormKind.Or or ServiceFormKind.Choose =>
                Compound(form.Kind, form.Test, [.. form.Members.Select(m => Read(m, reader))], form.Text),
            _ => form,
        };

        static ServiceForm Compound(ServiceFormKind kind, CosmosServiceTest test, ServiceForm[] members, string text) =>
            new(kind, test, null, null, members.Aggregate(ValueSet<string>.Empty, (s, m) => s.Union(m.Literals)), members.Aggregate(ValueSet<string>.Empty, (s, m) => s.Union(m.Paths)), members, text);

        /// <summary>A negation's operand, or a junction's first member; a leaf is its own.</summary>
        public static ServiceForm First(ServiceForm form) => form.Members.Count > 0 ? form.Members[0] : form;

        /// <summary>A negation's operand, or a junction without its first member as written; a leaf is its own.</summary>
        public static ServiceForm Rest(ServiceForm form) => form.Kind is ServiceFormKind.And or ServiceFormKind.Or ? Without(form, form.Members[0], within: false) : First(form);

        /// <summary>
        /// A junction without every part whose text is the member's, those within a member of its own connective among them,
        /// its grouping kept: the one member left where one is, and the member itself where none is, a junction of copies of
        /// one form being that form.
        /// </summary>
        public static ServiceForm Without(ServiceForm junction, ServiceForm member) => Without(junction, member, within: true);

        static ServiceForm Without(ServiceForm junction, ServiceForm member, bool within)
        {
            if (junction.Kind is not (ServiceFormKind.And or ServiceFormKind.Or))
                throw new ArgumentException("Only a junction has members to take one from.", nameof(junction));

            var rest = junction.Members.Select(m => within && junction.Joins(m) && m.Parts.Contains(member) ? Without(m, member, within) : m).Where(m => m.Equals(member) == false).ToArray();
            return rest.SequenceEqual(junction.Members) ? junction : rest.Length switch
            {
                0 => member,
                1 => rest[0],
                _ => JunctionOf(junction.Test, rest),
            };
        }

        /// <summary>
        /// A junction kept to what two forms make of it, its grouping kept: every member it is a join of that neither form is
        /// nor holds as one of the junction's own connective is taken out, as <see cref="Without(ServiceForm, ServiceForm)"/>
        /// takes one. Whether the two are the result's parts is the caller's to check. Total, as a rule body's calls must
        /// be: anything but a junction has no parts to keep, and an opaque sentinel says so.
        /// </summary>
        public static ServiceForm Spanned(ServiceForm junction, ServiceForm left, ServiceForm right)
        {
            if (junction.Kind is not (ServiceFormKind.And or ServiceFormKind.Or))
                return Unspanned;

            // What the sides hold of it: a side that is one of its parts whole, and of one built of them, each part it holds.
            var (parts, present) = (junction.Parts.ToHashSet(), new HashSet<ServiceForm>());
            void Hold(ServiceForm side)
            {
                if (parts.Contains(side) || junction.Joins(side) == false)
                    present.Add(side);
                else
                    foreach (var member in side.Members)
                        Hold(member);
            }

            Hold(left);
            Hold(right);
            return Kept(junction, present);
        }

        /// <summary>
        /// A junction kept to the parts that read one path alone, its grouping kept; the junction itself where none does,
        /// which reads more than the path. Total: anything but a junction is the sentinel <see cref="Spanned"/> gives.
        /// </summary>
        public static ServiceForm Reading(ServiceForm junction, string path)
        {
            if (junction.Kind is not (ServiceFormKind.And or ServiceFormKind.Or))
                return Unspanned;

            bool Alone(ServiceForm part) => part.Paths.Count > 0 && part.Paths.All(p => p == path);
            var present = junction.Parts.Where(Alone).ToHashSet();
            return present.Count == 0 ? junction : Kept(junction, present);
        }

        static readonly ServiceForm Unspanned = OpaqueOf("unspanned", ValueSet<string>.Empty);

        /// <summary>A junction with every greatest part that neither is nor holds one present taken out, while it stays one.</summary>
        static ServiceForm Kept(ServiceForm junction, ISet<ServiceForm> present) =>
            Unheld(junction, present).Aggregate(junction, (kept, part) => kept.Kind == junction.Kind ? Without(kept, part) : kept);

        /// <summary>A junction's greatest parts that neither are nor hold one present: a member whole, or within one of its own connective that holds one.</summary>
        static IEnumerable<ServiceForm> Unheld(ServiceForm junction, ISet<ServiceForm> present) => junction.Members.SelectMany(m =>
            present.Contains(m) ? [] : junction.Joins(m) && m.Parts.Any(present.Contains) ? Unheld(m, present) : new[] { m });

        /// <summary>Whether a junction is one of its parts joined with the rest of it.</summary>
        public static bool Decomposes(ServiceForm junction, ServiceForm left, ServiceForm right) =>
            junction.Kind is ServiceFormKind.And or ServiceFormKind.Or && junction.Parts.Contains(left) && Without(junction, left).Equals(right);

        /// <summary>A negation's operand, or the negation of anything else.</summary>
        public static ServiceForm Strip(ServiceForm form) => form.Kind == ServiceFormKind.Not ? form.Members[0] : NotOf(form);

        /// <summary>
        /// Two forms joined by a connective: a junction of its own flattened into its members, one of each part kept in the
        /// arms' order, and Kleene's constants folded whatever the position, the identity left out and the absorbing one
        /// the whole. The weakening's assembly alone: a weakening has no lattice, one form a node, so it cannot leave the
        /// paired form and its regrouping both to a rule (<c>TODO.md</c>, the weakening's assembly); a best is joined by
        /// <see cref="Paired"/>, and S's rules regroup and fold it. Total, as a rule body's calls must be: no junction joins
        /// by anything else, and an opaque sentinel says so.
        /// </summary>
        public static ServiceForm Joined(CosmosServiceTest connective, ServiceForm left, ServiceForm right)
        {
            if (connective is not (CosmosServiceTest.And or CosmosServiceTest.Or))
                return Unjoined;

            var kind = connective == CosmosServiceTest.And ? ServiceFormKind.And : ServiceFormKind.Or;
            var (identity, absorbing) = (ConstantOf(kind == ServiceFormKind.And), ConstantOf(kind == ServiceFormKind.Or));
            var parts = new List<ServiceForm>();
            foreach (var part in new[] { left, right }.SelectMany(arm => arm.Kind == kind ? arm.Flat : [arm]))
            {
                if (part.Equals(absorbing))
                    return absorbing;

                if (part.Equals(identity) == false && parts.Contains(part) == false)
                    parts.Add(part);
            }

            return parts.Count switch { 0 => identity, 1 => parts[0], _ => JunctionOf(connective, parts) };
        }

        static readonly ServiceForm Unjoined = OpaqueOf("unjoined", ValueSet<string>.Empty);

        /// <summary>
        /// Two forms joined by a connective as they stand, a junction of the two: nothing flattened, rid of copies or folded. A
        /// junction of its own connective, a part twice and a constant stay as they are, which S's rules regroup and fold where
        /// the set's licence and Kleene's rows say how (<see cref="Regrouped"/>, <see cref="Unit"/>, G4). Only puts the forms
        /// together; whether the join holds is the rule's. Total, as a rule body's calls must be: no junction joins by
        /// anything else, and an opaque sentinel says so.
        /// </summary>
        public static ServiceForm Paired(CosmosServiceTest connective, ServiceForm left, ServiceForm right) =>
            connective is CosmosServiceTest.And or CosmosServiceTest.Or ? JunctionOf(connective, [left, right]) : Unjoined;

        /// <summary>
        /// A junction as its connective's associativity and idempotence regroup it: each member of its own connective
        /// flattened into its members, one of each part kept in order, the one part where one is left. Only takes the form
        /// apart and puts it together; whether the laws hold is the set's licence and the rule's
        /// (<see cref="RenderingPrimitives.FormIsGrouped"/>, G4). Total: anything but a junction is the sentinel.
        /// </summary>
        public static ServiceForm Regrouped(ServiceForm junction)
        {
            if (junction.Kind is not (ServiceFormKind.And or ServiceFormKind.Or))
                return Unjoined;

            var parts = junction.Flat.Distinct().ToArray();
            return parts.Length == 1 ? parts[0] : JunctionOf(junction.Test, parts);
        }

        /// <summary>
        /// A junction holding a constant as a row of Kleene's says it folds (<see cref="KleeneTables.Units"/>, G4): the
        /// constant where it absorbs the junction; otherwise the junction without it, the constant where nothing is left and the
        /// one member where one is. Only takes the form apart and puts it together; which constant does what is the row's, and
        /// whether the junction holds it the rule's. Total: anything but a junction is the sentinel.
        /// </summary>
        public static ServiceForm Unit(ServiceForm junction, bool constant, bool absorbs)
        {
            if (junction.Kind is not (ServiceFormKind.And or ServiceFormKind.Or))
                return Unjoined;

            var unit = ConstantOf(constant);
            var rest = junction.Members.Where(m => m.Equals(unit) == false).ToArray();
            return absorbs ? unit : rest.Length switch { 0 => unit, 1 => rest[0], _ => JunctionOf(junction.Test, rest) };
        }

        /// <summary>
        /// Forms folded into one by the connective a position keeps rows by, <c>AND</c> at <c>Pos</c>, where each keeps
        /// every row the leaf keeps, and <c>OR</c> at <c>Neg</c>, where each drops every row it drops; in the set's order,
        /// the identity for none. A leaf's weakening, which prunes nothing and chooses nothing.
        /// </summary>
        public static ServiceForm Folded(ValueSet<ServiceForm> forms, RenderPosition pos) =>
            forms.Aggregate(ConstantOf(pos == RenderPosition.Pos), (joined, form) => Joined(pos == RenderPosition.Pos ? CosmosServiceTest.And : CosmosServiceTest.Or, joined, form));

        /// <summary>A path present, at <c>Pos</c>; beneath a <c>NOT</c>, at <c>Neg</c>, what is false exactly where it is present.</summary>
        public static ServiceForm Presence(string path, RenderPosition pos) =>
            pos == RenderPosition.Pos ? TestOf(CosmosServiceTest.IsDefined, path) : NotOf(TestOf(CosmosServiceTest.IsDefined, path));

        /// <summary>A form negated: a constant's other value, a negation's operand, or the negation of anything else.</summary>
        public static ServiceForm Negated(ServiceForm form) => form.Kind == ServiceFormKind.Constant ? ConstantOf(form.Literal == JsonValues.Of(false)) : Strip(form);

        /// <inheritdoc />
        public bool Equals(ServiceForm? other) => other is not null && string.Equals(_key, other._key, StringComparison.Ordinal);

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as ServiceForm);

        /// <inheritdoc />
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

        /// <inheritdoc />
        public int CompareTo(ServiceForm? other) => other is null ? 1 : string.CompareOrdinal(Text, other.Text) is var order and not 0 ? order : string.CompareOrdinal(_key, other._key);

        /// <inheritdoc />
        public override string ToString() => Text;

    }

}
