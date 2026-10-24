using System;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;

using Eggnog;

using FluentAssertions;

using Xunit;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// A form's structure: its text is all there is to it, and the helpers that take a junction apart agree with each
    /// other and with how it was written.
    /// </summary>
    public sealed class ServiceFormTests
    {

        static readonly string P = SqlValue.Spell(JsonDocumentPath.Root.Property("label"));

        static readonly string Q = SqlValue.Spell(JsonDocumentPath.Root.Property("kind"));

        static readonly ServiceForm[] Pool =
        [
            ServiceForm.TestOf(CosmosServiceTest.IsPrimitive, P),
            ServiceForm.NotOf(ServiceForm.TestOf(CosmosServiceTest.IsNull, P)),
            ServiceForm.CompareOf(CosmosServiceTest.NotEqual, P, Of("bikes")),
            ServiceForm.CompareOf(CosmosServiceTest.Equal, Q, Of("a")),
            ServiceForm.BareOf(Q),
        ];

        /// <summary>
        /// A junction without a member keeps every other member in order, and decomposes into that member and the rest
        /// exactly where the member is one of its own.
        /// </summary>
        [Fact]
        public void WithoutAndDecomposesAgree()
        {
            var random = new Random(20_261_020);
            for (var trial = 0; trial < 300; trial++)
            {
                var junction = ServiceForm.JunctionOf(random.Next(2) == 0 ? CosmosServiceTest.And : CosmosServiceTest.Or,
                    Enumerable.Range(0, random.Next(2, 5)).Select(_ => Pool[random.Next(Pool.Length)]));
                ServiceForm.Decomposes(junction, ServiceForm.First(junction), ServiceForm.Rest(junction)).Should().BeTrue(junction.Text);

                foreach (var member in Pool)
                {
                    var others = junction.Members.Where(m => m.Equals(member) == false).ToList();
                    var rest = ServiceForm.Without(junction, member);
                    var expected = others.Count == junction.Members.Count ? junction
                        : others.Count == 0 ? member
                        : others.Count == 1 ? others[0]
                        : ServiceForm.JunctionOf(junction.Test, others);

                    rest.Should().Be(expected, $"{junction} without {member}");
                    ServiceForm.Decomposes(junction, member, rest).Should().Be(others.Count < junction.Members.Count, $"{junction} into {member} and {rest}");
                    Pool.Where(other => ServiceForm.Decomposes(junction, member, other)).Should().OnlyContain(other => other.Equals(rest));
                }
            }

            ServiceForm.Decomposes(Pool[2], Pool[2], Pool[2]).Should().BeFalse("a leaf has no members");
        }

        /// <summary>
        /// A junction kept to what two forms make of it keeps the parts they are or hold, as written, and only where they are
        /// its written parts is it their join; kept to one path's parts it is those parts, a leaf where one is, and itself
        /// where none.
        /// </summary>
        [Fact]
        public void SpannedAndReadingKeepTheirPartsAsWritten()
        {
            var (a, b, c, d, e) = (Pool[0], Pool[1], Pool[2], Pool[3], Pool[4]);
            ServiceForm And(params ServiceForm[] members) => ServiceForm.JunctionOf(CosmosServiceTest.And, members);
            var nested = And(And(a, b), d, And(c, e));

            // A side regrouped or reordered against the claim holds its parts, but is no part, so nothing is its join.
            foreach (var (l, m, spanned, joins) in new[] { (a, d, And(a, d), true), (d, a, And(a, d), true), (And(a, b), c, And(And(a, b), c), true),
                (b, And(e, c), And(b, And(c, e)), false), (And(a, b), And(d, And(c, e)), nested, true) })
            {
                ServiceForm.Spanned(nested, l, m).Should().Be(spanned, $"{l} and {m}");
                (ServiceForm.Decomposes(spanned, l, m) || ServiceForm.Decomposes(spanned, m, l)).Should().Be(joins, $"{spanned} into {l} and {m}");
            }

            ServiceForm.Spanned(nested, a, ServiceForm.CompareOf(CosmosServiceTest.Equal, P, Of("z"))).Should().Be(a, "a side that is no part holds nothing of it");
            (ServiceForm.Reading(nested, P), ServiceForm.Reading(nested, Q)).Should().Be((And(And(a, b), c), And(d, e)));
            ServiceForm.Reading(And(c, d), P).Should().Be(c);
            ServiceForm.Reading(nested, SqlValue.Spell(JsonDocumentPath.Root.Property("other"))).Should().Be(nested, "no part reads it alone");
            new[] { ServiceForm.Spanned(a, a, b), ServiceForm.Reading(a, P) }.Should().OnlyContain(f => f.Kind == ServiceFormKind.Opaque, "a leaf has no parts, and a rule body's call is total");
        }

        /// <summary>Stripping takes one negation off, or puts one on, so twice is nothing on a form with at most one.</summary>
        [Fact]
        public void StripIsAnInvolutionOnNot()
        {
            var forms = Pool.Where(f => f.Kind != ServiceFormKind.Not)
                .Append(ServiceForm.JunctionOf(CosmosServiceTest.And, Pool)).Append(ServiceForm.JunctionOf(CosmosServiceTest.Or, Pool.Take(2)));

            foreach (var g in forms)
            {
                ServiceForm.Strip(g).Should().Be(ServiceForm.NotOf(g));
                ServiceForm.Strip(ServiceForm.NotOf(g)).Should().Be(g);
                ServiceForm.Strip(ServiceForm.Strip(g)).Should().Be(g);
                ServiceForm.Strip(ServiceForm.Strip(ServiceForm.NotOf(g))).Should().Be(ServiceForm.NotOf(g));
            }
        }

        /// <summary>
        /// Two forms built alike are one form, and forms that differ in any part — order, grouping, connective, a literal's
        /// type, a path's names — differ in text, a literal holding SQL included.
        /// </summary>
        [Fact]
        public void TextIsCanonical()
        {
            ServiceForm Build(int i)
            {
                var (a, b, c) = (Pool[0], Pool[2], Pool[3]);
                return i switch
                {
                    0 => ServiceForm.JunctionOf(CosmosServiceTest.And, [a, b]),
                    1 => ServiceForm.JunctionOf(CosmosServiceTest.And, [b, a]),
                    2 => ServiceForm.JunctionOf(CosmosServiceTest.Or, [a, b]),
                    3 => ServiceForm.JunctionOf(CosmosServiceTest.And, [ServiceForm.JunctionOf(CosmosServiceTest.Or, [a, b]), c]),
                    4 => ServiceForm.JunctionOf(CosmosServiceTest.Or, [a, ServiceForm.JunctionOf(CosmosServiceTest.And, [b, c])]),
                    5 => ServiceForm.JunctionOf(CosmosServiceTest.And, [a, b, c]),
                    6 => ServiceForm.JunctionOf(CosmosServiceTest.And, [ServiceForm.JunctionOf(CosmosServiceTest.And, [a, b]), c]),
                    7 => ServiceForm.JunctionOf(CosmosServiceTest.And, [a, a]),
                    8 => ServiceForm.CompareOf(CosmosServiceTest.Equal, P, Of("a\") OR (\"b")),
                    9 => ServiceForm.JunctionOf(CosmosServiceTest.Or, [ServiceForm.CompareOf(CosmosServiceTest.Equal, P, Of("a")), ServiceForm.CompareOf(CosmosServiceTest.Equal, P, Of("b"))]),
                    10 => ServiceForm.CompareOf(CosmosServiceTest.Equal, P, Of("30")),
                    11 => ServiceForm.CompareOf(CosmosServiceTest.Equal, P, Of(30m)),
                    12 => ServiceForm.BareOf(SqlValue.Spell(JsonDocumentPath.Root.Property("a.b"))),
                    13 => ServiceForm.BareOf(SqlValue.Spell(JsonDocumentPath.Root.Property("a").Property("b"))),
                    14 => ServiceForm.TestOf(CosmosServiceTest.IsDefined, P),
                    15 => ServiceForm.InOf(P, ValueSet.Of(Of("a"), Of("b"))),
                    16 => ServiceForm.ConstantOf(true),
                    _ => ServiceForm.OpaqueOf("true", ValueSet<string>.Empty),
                };
            }

            var forms = Enumerable.Range(0, 18).Select(Build).ToList();
            forms.Select(f => f.Text).Distinct().Should().HaveCount(forms.Count, string.Join("\n", forms));
            for (var i = 0; i < forms.Count; i++)
            {
                var again = Build(i);
                (again.Equals(forms[i]), again.GetHashCode() == forms[i].GetHashCode(), again.CompareTo(forms[i])).Should().Be((true, true, 0), again.Text);
            }

            ServiceForm.JunctionOf(CosmosServiceTest.And, Pool.Take(3)).Text
                .Should().Be("(IS_PRIMITIVE($[\"label\"]) AND (NOT IS_NULL($[\"label\"])) AND ($[\"label\"] != \"string|bikes\"))");
        }

    }

}
