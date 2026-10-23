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
    /// is its equality, hash and order; the structural helpers take it apart and decide nothing (<c>DESIGN.md</c>).
    /// </summary>
    internal sealed class ServiceForm : IEquatable<ServiceForm>, IComparable<ServiceForm>
    {

        static readonly ServiceForm[] Leaf = [];

        ServiceForm(ServiceFormKind kind, CosmosServiceTest test, string? path, string? literal, ValueSet<string> literals, ValueSet<string> paths, ServiceForm[] members, string text)
        {
            Kind = kind;
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
                _ => 1,
            };
        }

        public ServiceFormKind Kind { get; }

        /// <summary>What the service is asked: the test, the operator or the connective; an opaque form reads as bare.</summary>
        public CosmosServiceTest Test { get; }

        /// <summary>The path a leaf reads; <c>null</c> for a constant, an opaque form and a compound.</summary>
        public string? Path { get; }

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

        public static ServiceForm TestOf(CosmosServiceTest test, string path) => new(ServiceFormKind.Test, test, path, null, ValueSet<string>.Empty, ValueSet.Of(path), Leaf, test switch
        {
            CosmosServiceTest.IsDefined => "IS_DEFINED",
            CosmosServiceTest.IsNull => "IS_NULL",
            CosmosServiceTest.IsPrimitive => "IS_PRIMITIVE",
            CosmosServiceTest.IsString => "IS_STRING",
            CosmosServiceTest.IsNumber => "IS_NUMBER",
            CosmosServiceTest.IsBool => "IS_BOOL",
            CosmosServiceTest.IsObject => "IS_OBJECT",
            CosmosServiceTest.IsArray => "IS_ARRAY",
            _ => throw new ArgumentOutOfRangeException(nameof(test), test, null),
        } + "(" + path + ")");

        public static ServiceForm CompareOf(CosmosServiceTest op, string path, string literal) => new(ServiceFormKind.Compare, op, path, literal, ValueSet.Of(literal), ValueSet.Of(path), Leaf, "(" + path + " " + op switch
        {
            CosmosServiceTest.Equal => "=",
            CosmosServiceTest.NotEqual => "!=",
            CosmosServiceTest.Less => "<",
            CosmosServiceTest.LessOrEqual => "<=",
            CosmosServiceTest.Greater => ">",
            CosmosServiceTest.GreaterOrEqual => ">=",
            _ => throw new ArgumentOutOfRangeException(nameof(op), op, null),
        } + " " + SqlValue.Quote(literal) + ")");

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

        /// <summary>Text the reader could not read, over the paths it names: the service's answer on it is not known.</summary>
        public static ServiceForm OpaqueOf(string text, ValueSet<string> paths) =>
            new(ServiceFormKind.Opaque, CosmosServiceTest.Bare, null, null, ValueSet<string>.Empty, paths, Leaf, "OPAQUE(" + SqlValue.Quote(text) + ")");

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
        /// takes one. Whether the two are the result's parts is the caller's to check.
        /// </summary>
        public static ServiceForm Spanned(ServiceForm junction, ServiceForm left, ServiceForm right)
        {
            if (junction.Kind is not (ServiceFormKind.And or ServiceFormKind.Or))
                throw new ArgumentException("Only a junction has parts to keep.", nameof(junction));

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
            var spanned = junction;
            foreach (var part in Unheld(junction, present))
                if (spanned.Kind == junction.Kind)
                    spanned = Without(spanned, part);

            return spanned;
        }

        /// <summary>
        /// A junction kept to the parts that read one path alone, its grouping kept; the junction itself where none does,
        /// which reads more than the path.
        /// </summary>
        public static ServiceForm Reading(ServiceForm junction, string path)
        {
            if (junction.Kind is not (ServiceFormKind.And or ServiceFormKind.Or))
                throw new ArgumentException("Only a junction has parts to keep.", nameof(junction));

            bool Alone(ServiceForm part) => part.Paths.Count > 0 && part.Paths.All(p => p == path);
            var present = junction.Parts.Where(Alone).ToHashSet();
            if (present.Count == 0)
                return junction;

            var reading = junction;
            foreach (var part in Unheld(junction, present))
                if (reading.Kind == junction.Kind)
                    reading = Without(reading, part);

            return reading;
        }

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
        /// the whole. Only takes forms apart and puts them together; whether the join holds is the licence's. Total, as a
        /// rule body's calls must be: no junction joins by anything else, and an opaque sentinel says so.
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

        /// <summary>A path present, at <c>Pos</c>; beneath a <c>NOT</c>, at <c>Neg</c>, what is false exactly where it is present.</summary>
        public static ServiceForm Presence(string path, RenderPosition pos) =>
            pos == RenderPosition.Pos ? TestOf(CosmosServiceTest.IsDefined, path) : NotOf(TestOf(CosmosServiceTest.IsDefined, path));

        /// <summary>A form negated: a constant's other value, a negation's operand, or the negation of anything else.</summary>
        public static ServiceForm Negated(ServiceForm form) => form.Kind == ServiceFormKind.Constant ? ConstantOf(form.Literal == JsonValues.Of(false)) : Strip(form);

        /// <inheritdoc />
        public bool Equals(ServiceForm? other) => other is not null && string.Equals(Text, other.Text, StringComparison.Ordinal);

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as ServiceForm);

        /// <inheritdoc />
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

        /// <inheritdoc />
        public int CompareTo(ServiceForm? other) => other is null ? 1 : string.CompareOrdinal(Text, other.Text);

        /// <inheritdoc />
        public override string ToString() => Text;

    }

}
