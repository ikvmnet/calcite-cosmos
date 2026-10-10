using System;
using System.Text;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// Something true of a container's documents taken together, rather than of each one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written as SQL DDL</b>, because SQL already has the vocabulary and the expressions are the ones a
    /// model's views are written in: <c>UNIQUE (expr, …) [WHERE predicate]</c>. The grammar here is only the
    /// part around the expressions; the expressions themselves are Calcite's, parsed and validated against
    /// the container's row type by <see cref="Rel.CosmosConstraintCompiler"/>.
    /// </para>
    /// <para>
    /// <b>Why it is not a fact.</b> A <see cref="CosmosFact"/> is a claim about one document, and a theory of
    /// them is asked one document at a time. A uniqueness constraint is a claim about every <em>pair</em> of
    /// documents, which no single document can satisfy or violate.
    /// </para>
    /// <para>
    /// <b>One kind so far.</b> <c>CHECK (predicate)</c> — a claim about each document that can relate two of
    /// its paths, <c>data.id = linkId</c> — is the next, and is refused by name until it is read.
    /// </para>
    /// </remarks>
    public abstract record CosmosConstraint
    {

        /// <summary>
        /// Gets the constraint as DDL.
        /// </summary>
        public abstract string Text { get; }

        /// <summary>
        /// Gets where the constraint was learned, which is also what stands behind it.
        /// </summary>
        public abstract CosmosConstraintSource Source { get; }

        /// <inheritdoc />
        public sealed override string ToString() => Text;

        /// <summary>
        /// Reads a constraint written as DDL.
        /// </summary>
        /// <remarks>
        /// Only the shape is read here. Whether the expressions mean anything against the container is
        /// <see cref="Rel.CosmosConstraintCompiler"/>'s question, and is asked once the container is known.
        /// </remarks>
        /// <param name="text">The DDL, such as <c>UNIQUE (JSON_VALUE(DOC, '$.data.guid'))</c>.</param>
        /// <param name="source">Where it was learned.</param>
        /// <returns>The constraint.</returns>
        /// <exception cref="ArgumentException">The text is not a constraint this reads.</exception>
        public static CosmosConstraint Parse(string text, CosmosConstraintSource source)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("A constraint cannot be empty.", nameof(text));

            var trimmed = text.Trim();

            if (StartsWithKeyword(trimmed, "CHECK"))
                throw new ArgumentException($"'{trimmed}': CHECK constraints are not read yet. A constraint that is not read is not in force, so it is refused rather than ignored.", nameof(text));

            if (StartsWithKeyword(trimmed, "UNIQUE") == false)
                throw new ArgumentException($"'{trimmed}' is not a constraint this reads; write UNIQUE (expression, …) [WHERE predicate].", nameof(text));

            var rest = trimmed.Substring("UNIQUE".Length).TrimStart();
            if (rest.Length == 0 || rest[0] != '(')
                throw new ArgumentException($"'{trimmed}': UNIQUE is followed by a parenthesised list of expressions.", nameof(text));

            var close = MatchingParenthesis(rest, 0)
                ?? throw new ArgumentException($"'{trimmed}': the list of expressions is not closed.", nameof(text));

            var keys = rest.Substring(1, close - 1).Trim();
            if (keys.Length == 0)
                throw new ArgumentException($"'{trimmed}': UNIQUE needs at least one expression.", nameof(text));

            var tail = rest.Substring(close + 1).Trim();
            string? filter = null;

            if (tail.Length > 0)
            {
                if (StartsWithKeyword(tail, "WHERE") == false)
                    throw new ArgumentException($"'{trimmed}': after the expressions only WHERE predicate may follow.", nameof(text));

                filter = tail.Substring("WHERE".Length).Trim();
                if (filter.Length == 0)
                    throw new ArgumentException($"'{trimmed}': WHERE needs a predicate.", nameof(text));
            }

            return new Unique(keys, filter, source);
        }

        /// <summary>
        /// Determines whether text begins with a keyword followed by something that cannot continue a word.
        /// </summary>
        static bool StartsWithKeyword(string text, string keyword) =>
            text.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
            && (text.Length == keyword.Length || char.IsLetterOrDigit(text[keyword.Length]) == false && text[keyword.Length] != '_');

        /// <summary>
        /// Finds the parenthesis closing the one at <paramref name="open"/>, stepping over SQL string literals
        /// and quoted identifiers, in which a parenthesis is text.
        /// </summary>
        static int? MatchingParenthesis(string text, int open)
        {
            var depth = 0;

            for (var i = open; i < text.Length; i++)
            {
                switch (text[i])
                {
                    case '\'':
                    case '"':
                        var quote = text[i];
                        for (i++; i < text.Length; i++)
                        {
                            if (text[i] != quote)
                                continue;

                            // A doubled quote is the quote itself, inside the literal.
                            if (i + 1 < text.Length && text[i + 1] == quote)
                            {
                                i++;
                                continue;
                            }

                            break;
                        }
                        break;
                    case '(':
                        depth++;
                        break;
                    case ')':
                        depth--;
                        if (depth == 0)
                            return i;
                        break;
                }
            }

            return null;
        }

        /// <summary>
        /// Renders a path in policy form, such as <c>/data/guid</c>, as the accessor a constraint names it by.
        /// </summary>
        /// <param name="policyPath">The path.</param>
        /// <returns>The accessor, or <c>null</c> where the path names nothing below the root or many values.</returns>
        public static string? AccessorOf(string? policyPath)
        {
            if (string.IsNullOrWhiteSpace(policyPath))
                return null;

            var path = new StringBuilder("$");
            var any = false;

            foreach (var name in policyPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // A wildcard or an array step names many values rather than one.
                if (name is "*" or "?" or "[]")
                    return null;

                var bare = name.Length > 1 && name[0] == '"' && name[^1] == '"' ? name[1..^1] : name;

                // A quote would need escaping at two levels, the JSON path's and the SQL literal's; no
                // container this has met names a property so, and refusing is better than guessing.
                if (bare.Contains('\''))
                    return null;

                if (IsIdentifier(bare))
                    path.Append('.').Append(bare);
                else
                    path.Append("[''").Append(bare).Append("'']");

                any = true;
            }

            return any ? $"JSON_VALUE(DOC, '{path}')" : null;

            static bool IsIdentifier(string name)
            {
                if (name.Length == 0 || (char.IsLetter(name[0]) == false && name[0] != '_'))
                    return false;

                foreach (var c in name)
                    if (char.IsLetterOrDigit(c) == false && c != '_')
                        return false;

                return true;
            }
        }

        /// <summary>
        /// A <c>UNIQUE</c> constraint: among the documents the predicate admits, no two share the values of
        /// the expressions.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What a key expression stands for.</b> A plain document accessor — <c>JSON_VALUE(DOC, '$.x')</c>,
        /// or a promoted column — stands for the <em>stored</em> value at its path, which is what the service
        /// enforces and what an application usually means. Any other expression stands for its own value:
        /// <c>LOWER(JSON_VALUE(DOC, '$.email'))</c> says two emails differing in case are one.
        /// </para>
        /// <para>
        /// <b>Always across the whole container.</b> The service makes <c>id</c> unique within a logical
        /// partition, and a unique key policy does the same for its paths, so each is stated with the partition
        /// key paths added. Uniqueness within a partition is not something a planner can use alone.
        /// </para>
        /// <para>
        /// <b>A predicate scopes the claim, and a consumer has to prove it</b> of every document it compares.
        /// A document for which a key is null is outside the claim: a null equals nothing.
        /// </para>
        /// </remarks>
        /// <param name="Keys">The key expressions, as Calcite SQL over the document, separated by commas.</param>
        /// <param name="Filter">The predicate scoping the claim, as Calcite SQL, or <c>null</c> for every document.</param>
        /// <param name="SourceOf">Where the constraint was learned.</param>
        public sealed record Unique(string Keys, string? Filter, CosmosConstraintSource SourceOf) : CosmosConstraint
        {

            /// <inheritdoc />
            public override string Text => $"UNIQUE ({Keys})" + (Filter is null ? "" : $" WHERE {Filter}");

            /// <inheritdoc />
            public override CosmosConstraintSource Source => SourceOf;

        }

    }

    /// <summary>
    /// Where a constraint was learned.
    /// </summary>
    /// <remarks>
    /// The first two are enforced by the service on every write; the third is trusted the way a schema is,
    /// and is the only one that can be wrong.
    /// </remarks>
    public enum CosmosConstraintSource
    {

        /// <summary>
        /// The service's own guarantee — <c>id</c> unique within a logical partition — read against the
        /// container's partition key.
        /// </summary>
        Service,

        /// <summary>
        /// The container definition: its unique key policy, read against its partition key.
        /// </summary>
        ContainerDefinition,

        /// <summary>
        /// A model's declaration, which nothing enforces.
        /// </summary>
        Declared,

    }

}
