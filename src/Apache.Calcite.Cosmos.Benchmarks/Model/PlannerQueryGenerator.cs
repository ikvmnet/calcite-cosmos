using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Apache.Calcite.Cosmos.Benchmarks.Model
{

    /// <summary>
    /// Builds statements of a chosen size.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The corpus says what the planner costs for the statements people write. These say how that
    /// cost grows, which is the question a corpus cannot answer: a search whose time doubles with
    /// each conjunct and one whose time grows with the square of the joins fail at different sizes,
    /// and neither failure is visible in a table of fixed statements.
    /// </para>
    /// <para>
    /// Every generator varies one dimension and holds the rest fixed, so that the series is
    /// attributable. The predicates are all translatable and all over distinct paths, because a
    /// predicate the rules decline exercises the split rule instead and would put two curves in
    /// one series.
    /// </para>
    /// </remarks>
    public static class PlannerQueryGenerator
    {

        /// <summary>
        /// The containers a generated join chain walks, in order.
        /// </summary>
        /// <remarks>
        /// Distinct containers first, because a self-join is a different question — one convention
        /// rather than several, and one set of statistics. Past the sixth the list repeats under a
        /// new alias, which is the only way to ask for a longer chain than the schema has tables.
        /// </remarks>
        static readonly string[] JoinTables = { "orders", "customers", "products", "archive", "events", "shipments" };

        /// <summary>
        /// Builds a statement with the given number of translatable conjuncts.
        /// </summary>
        /// <remarks>
        /// Each conjunct names a path no other conjunct names, so none of them subsume, and the
        /// simplifier cannot collapse the predicate to something smaller than it was asked for.
        /// </remarks>
        /// <param name="count">How many conjuncts.</param>
        /// <returns>The statement.</returns>
        public static string Conjuncts(int count)
        {
            if (count < 1)
                throw new ArgumentOutOfRangeException(nameof(count));

            var sql = new StringBuilder("SELECT * FROM products AS c WHERE c.\"category\" = 'bikes'");

            for (var i = 0; i < count; i++)
                sql.Append(CultureInfo.InvariantCulture, $" AND CAST(c.\"_MAP\"['p{i}'] AS VARCHAR) = 'v{i}'");

            return sql.ToString();
        }

        /// <summary>
        /// Builds a statement whose predicate is a disjunction of the given number of conjunct pairs.
        /// </summary>
        /// <remarks>
        /// Disjunction rather than conjunction because the two are not the same shape of work: a
        /// conjunction splits into parts the service can take one at a time, and a disjunction has to
        /// be taken whole or not at all.
        /// </remarks>
        /// <param name="count">How many branches.</param>
        /// <returns>The statement.</returns>
        public static string Disjuncts(int count)
        {
            if (count < 1)
                throw new ArgumentOutOfRangeException(nameof(count));

            var sql = new StringBuilder("SELECT * FROM products AS c WHERE ");

            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                    sql.Append(" OR ");

                sql.Append(CultureInfo.InvariantCulture, $"(c.\"category\" = 'c{i}' AND CAST(c.\"_MAP\"['p{i}'] AS VARCHAR) = 'v{i}')");
            }

            return sql.ToString();
        }

        /// <summary>
        /// Builds a chain of equi-joins over the given number of containers.
        /// </summary>
        /// <remarks>
        /// A chain rather than a star, because a chain is the shape whose join orders grow fastest
        /// with no join becoming trivially better than another. Every join is an equality on
        /// <c>id</c>, so every one of them is a lookup the planner has to cost against a scan.
        /// </remarks>
        /// <param name="count">How many containers. Two is the smallest join.</param>
        /// <returns>The statement.</returns>
        public static string Joins(int count)
        {
            if (count < 2)
                throw new ArgumentOutOfRangeException(nameof(count));

            var sql = new StringBuilder("SELECT t0.\"id\" FROM ");
            sql.Append(CultureInfo.InvariantCulture, $"{JoinTables[0]} AS t0");

            for (var i = 1; i < count; i++)
                sql.Append(CultureInfo.InvariantCulture, $" JOIN {JoinTables[i % JoinTables.Length]} AS t{i} ON t{i - 1}.\"id\" = t{i}.\"id\"");

            return sql.ToString();
        }

        /// <summary>
        /// Builds the given number of nested derived tables, each adding a filter over the one below.
        /// </summary>
        /// <remarks>
        /// Nothing here is pushed down by nesting alone — the filters merge — so this measures what
        /// the merging costs rather than what it saves.
        /// </remarks>
        /// <param name="depth">How many levels. One is a plain sub-select.</param>
        /// <returns>The statement.</returns>
        public static string Nesting(int depth)
        {
            if (depth < 1)
                throw new ArgumentOutOfRangeException(nameof(depth));

            var sql = new StringBuilder("SELECT * FROM products AS c WHERE c.\"category\" = 'bikes'");

            for (var i = 0; i < depth; i++)
                sql = new StringBuilder(string.Create(
                    CultureInfo.InvariantCulture,
                    $"SELECT * FROM ({sql}) AS n{i} WHERE CAST(n{i}.\"_MAP\"['p{i}'] AS VARCHAR) = 'v{i}'"));

            return sql.ToString();
        }

        /// <summary>
        /// Builds a statement traversing the given number of arrays of one document.
        /// </summary>
        /// <param name="count">How many traversals.</param>
        /// <returns>The statement.</returns>
        public static string Unnests(int count)
        {
            if (count < 1)
                throw new ArgumentOutOfRangeException(nameof(count));

            var sql = new StringBuilder("SELECT c.\"id\" FROM products AS c");

            for (var i = 0; i < count; i++)
                sql.Append(CultureInfo.InvariantCulture, $", UNNEST(c.\"_MAP\"['a{i}']) AS u{i}");

            sql.Append(" WHERE c.\"category\" = 'bikes'");

            for (var i = 0; i < count; i++)
                sql.Append(CultureInfo.InvariantCulture, $" AND CAST(u{i} AS VARCHAR) = 'v{i}'");

            return sql.ToString();
        }

        /// <summary>
        /// Builds a statement projecting the given number of typed columns out of the document.
        /// </summary>
        /// <remarks>
        /// The width of a relational view over a container, which is where a projection rule's cost
        /// is per column rather than per statement.
        /// </remarks>
        /// <param name="count">How many columns.</param>
        /// <returns>The statement.</returns>
        public static string Projections(int count)
        {
            if (count < 1)
                throw new ArgumentOutOfRangeException(nameof(count));

            var columns = string.Join(", ", Enumerable.Range(0, count)
                .Select(i => string.Create(CultureInfo.InvariantCulture, $"CAST(c.\"_MAP\"['p{i}'] AS VARCHAR) AS \"c{i}\"")));

            return $"SELECT {columns} FROM products AS c WHERE c.\"category\" = 'bikes'";
        }

        /// <summary>
        /// Builds a point-read set over the given number of ids.
        /// </summary>
        /// <remarks>
        /// The converter rewrites <c>IN</c> into an OR ladder, so the size the planner sees grows
        /// with the list even though the statement's shape does not.
        /// </remarks>
        /// <param name="count">How many ids.</param>
        /// <returns>The statement.</returns>
        public static string PointReadSet(int count)
        {
            if (count < 1)
                throw new ArgumentOutOfRangeException(nameof(count));

            var ids = string.Join(", ", Enumerable.Range(0, count)
                .Select(i => string.Create(CultureInfo.InvariantCulture, $"'k{i}'")));

            return $"SELECT * FROM products AS c WHERE c.\"category\" = 'bikes' AND c.\"id\" IN ({ids})";
        }

        /// <summary>
        /// Builds a union of the given number of pushed branches.
        /// </summary>
        /// <param name="count">How many branches.</param>
        /// <returns>The statement.</returns>
        public static string UnionBranches(int count)
        {
            if (count < 2)
                throw new ArgumentOutOfRangeException(nameof(count));

            var branches = Enumerable.Range(0, count)
                .Select(i => string.Create(CultureInfo.InvariantCulture, $"SELECT c.\"id\" FROM products AS c WHERE c.\"category\" = 'c{i}'"));

            return string.Join(" UNION ALL ", branches);
        }

    }

}
