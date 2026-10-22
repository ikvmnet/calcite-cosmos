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

        /// <summary>Whether a junction is one of its parts joined with the rest of it.</summary>
        public static bool Decomposes(ServiceForm junction, ServiceForm left, ServiceForm right) =>
            junction.Kind is ServiceFormKind.And or ServiceFormKind.Or && junction.Parts.Contains(left) && Without(junction, left).Equals(right);

        /// <summary>A negation's operand, or the negation of anything else.</summary>
        public static ServiceForm Strip(ServiceForm form) => form.Kind == ServiceFormKind.Not ? form.Members[0] : NotOf(form);

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
