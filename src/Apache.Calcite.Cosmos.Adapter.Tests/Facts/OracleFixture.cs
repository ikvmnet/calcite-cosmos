using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts
{

    /// <summary>
    /// What the oracle's tests build their declarations and predicates from: paths, claims, readings and tests, spelled
    /// the way a reader of the test would say them.
    /// </summary>
    internal static class OracleFixture
    {

        public static JsonDocumentPath P(params string[] names) => names.Aggregate(JsonDocumentPath.Root, (path, name) => path.Property(name));

        public static JsonFactRule Outright(JsonDocumentPath path, JsonClaim claim) => JsonFactRule.Unconditional(new JsonFact(path, claim));

        public static JsonFactRule When(JsonDocumentPath guard, JsonScalar value, JsonDocumentPath path, JsonClaim claim) =>
            new(new[] { new JsonFact(guard, new JsonClaim.Present()), new JsonFact(guard, new JsonClaim.EqualTo(value)) }, new JsonFact(path, claim));

        public static JsonClaim Present => new JsonClaim.Present();

        public static JsonClaim Type(JsonType type, bool orNull = false) => new JsonClaim.OfType(type, orNull);

        public static JsonClaim Value(JsonScalar value) => new JsonClaim.EqualTo(value);

        public static JsonClaim Enum(params JsonScalar[] values) => new JsonClaim.OneOf(values);

        public static JsonFactTheory Theory(params JsonFactRule[] rules) => new(rules);

        public static SqlValue Text(JsonDocumentPath path) => new SqlValue.Text(path);

        public static SqlValue Raw(JsonDocumentPath path) => new SqlValue.Raw(path);

        public static SqlValue Typed(JsonDocumentPath path, string type) => new SqlValue.Typed(path, type);

        public static SqlValue Cast(string target, SqlValue value) => new SqlValue.Cast(target, value);

        public static SqlValue Lit(JsonScalar value) => SqlValue.Of(value);

        public static SqlValue Param(long ordinal, string type = "VARCHAR") => new SqlValue.Parameter(ordinal, type);

        public static SqlPredicate Cmp(SqlValue value, string op, SqlValue operand) => new SqlPredicate.Compare(op, value, operand);

        public static SqlPredicate Eq(SqlValue value, JsonScalar literal) => Cmp(value, "=", Lit(literal));

        public static SqlPredicate Ne(SqlValue value, JsonScalar literal) => Cmp(value, "<>", Lit(literal));

        public static SqlPredicate In(SqlValue value, params JsonScalar[] points) =>
            new SqlPredicate.In(value, Eggnog.ValueSet.From(points.Select(p => SqlValue.Of(p).Value)));

        public static SqlPredicate IsNull(SqlValue value) => new SqlPredicate.IsNull(value);

        public static SqlPredicate IsNotNull(SqlValue value) => Not(IsNull(value));

        public static SqlPredicate Not(SqlPredicate operand) => new SqlPredicate.Not(operand);

        public static SqlPredicate And(params SqlPredicate[] arms) => SqlPredicate.AllOf(arms);

        public static SqlPredicate Or(params SqlPredicate[] arms) => SqlPredicate.AnyOf(arms);

        public static SqlPredicate Opaque(long id) => new SqlPredicate.Opaque(id);

        /// <summary>
        /// The graphs the fixture's oracles reason in: the oracle tests' own, apart from those the planner's fixtures share
        /// (<see cref="TestReasoning"/>).
        /// </summary>
        static readonly JsonFactGenerations Graphs = new();

        /// <summary>A fresh oracle over a declaration, so that no test reads another's memo.</summary>
        public static CosmosOracle Oracle(JsonFactTheory theory) => new(theory, Graphs);

        /// <summary>The statement with every conjunct admitted in the first round: its last stratum holds them all.</summary>
        public static CosmosStatementScope Admitted(JsonFactTheory theory, params SqlPredicate[] conjuncts) =>
            Oracle(theory).Admit(new PredicateStatement(conjuncts), (_, _) => true);

        /// <summary>What every conjunct, admitted, establishes beside the declaration.</summary>
        public static JsonFactSet Established(JsonFactTheory theory, params SqlPredicate[] conjuncts) =>
            Admitted(theory, conjuncts).Facts(OracleLicence.Select);

        /// <summary>The statement with nothing admitted: questions answered from the declaration and what is given.</summary>
        public static CosmosStatementScope Declared(JsonFactTheory theory, params SqlPredicate[] conjuncts) =>
            Oracle(theory).Admit(new PredicateStatement(conjuncts));

    }

}
