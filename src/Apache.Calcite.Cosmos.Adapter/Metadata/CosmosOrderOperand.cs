using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts;

using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What an order the service computes is an order of: a sort key, the argument of a <c>MIN</c> or a <c>MAX</c>, or
    /// a side of a comparison, as its consumer bound it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Structure only.</b> An operand says what sits at a position — a path and how its column reads it, a literal,
    /// a parameter's type — and nothing about what the documents hold there. That is a question about the facts, which
    /// <see cref="CosmosServiceOrder"/> asks of the ones its consumer was decided under, so that a rule and the node it
    /// builds, asking of the same operand and the same facts, cannot answer differently.
    /// </para>
    /// </remarks>
    public sealed record CosmosOrderOperand
    {

        CosmosOrderOperand(CosmosOrderOperandKind kind)
        {
            Kind = kind;
        }

        /// <summary>
        /// Returns a document path, read back as a column of <paramref name="type"/> under <paramref name="reading"/>.
        /// </summary>
        /// <remarks>
        /// A statement's path no fact can be about — an array's element, which <see cref="CosmosDocumentPaths.From"/>
        /// answers no document path for, or a path rooted at a traversal's alias — is still a path: the service compares
        /// the value it holds there, and the column reads it as it reads any other. It is passed as <c>null</c>, and read
        /// as a path that may hold anything.
        /// </remarks>
        /// <param name="path">The path, or <c>null</c> for a path no fact can be about.</param>
        /// <param name="type">The type the column is declared, which is what Calcite orders and compares.</param>
        /// <param name="reading">How the column is read back: the stored value, or its rendering.</param>
        /// <returns>The operand.</returns>
        public static CosmosOrderOperand ForPath(JsonDocumentPath? path, SqlTypeName type, CosmosReading reading) =>
            new(CosmosOrderOperandKind.Path) { Path = path, Type = type ?? throw new ArgumentNullException(nameof(type)), Reading = reading };

        /// <summary>
        /// Returns a conversion of a path's text, by a cast or by a parse, into a type stored as a string.
        /// </summary>
        /// <param name="path">The path converted.</param>
        /// <param name="format">The format a parse reads it with, or <c>null</c> for a cast.</param>
        /// <param name="held">The halves of an instant the converted value holds.</param>
        /// <returns>The operand.</returns>
        public static CosmosOrderOperand ForConversion(JsonDocumentPath path, string? format, CosmosTemporalParts held) =>
            new(CosmosOrderOperandKind.Conversion) { Path = path, Format = format, Held = held };

        /// <summary>
        /// Returns a geodesic distance over the given stored geographies and any number of geographies the statement
        /// carries.
        /// </summary>
        /// <param name="operands">The paths it measures between, or <c>null</c> where an operand is one the binding
        /// cannot account for.</param>
        /// <returns>The operand.</returns>
        public static CosmosOrderOperand ForDistance(IReadOnlyList<JsonDocumentPath>? operands) =>
            new(CosmosOrderOperandKind.Distance) { Operands = operands ?? Array.Empty<JsonDocumentPath>(), AccountedFor = operands is not null };

        /// <summary>
        /// Returns a relevance score.
        /// </summary>
        /// <param name="bestFirstDescending">Whether the score is one whose best match is its greatest value — a full
        /// text score and a fusion of ranks — so that ranking best first is ordering it descending.</param>
        /// <returns>The operand.</returns>
        public static CosmosOrderOperand ForScore(bool bestFirstDescending) =>
            new(CosmosOrderOperandKind.Score) { BestFirstDescending = bestFirstDescending };

        /// <summary>
        /// Returns a literal.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <returns>The operand.</returns>
        public static CosmosOrderOperand ForLiteral(JsonScalar value) =>
            new(CosmosOrderOperandKind.Literal) { Value = value };

        /// <summary>
        /// Returns a parameter of a type.
        /// </summary>
        /// <param name="type">The parameter's type.</param>
        /// <returns>The operand.</returns>
        public static CosmosOrderOperand ForParameter(SqlTypeName type) =>
            new(CosmosOrderOperandKind.Parameter) { Type = type ?? throw new ArgumentNullException(nameof(type)) };

        /// <summary>
        /// Returns a value the service computes: an operator, a function or a <c>CASE</c> over the document, known by
        /// its type alone.
        /// </summary>
        /// <param name="type">The value's type.</param>
        /// <param name="nullable">Whether the type admits a null, so that Calcite may read the value as one.</param>
        /// <returns>The operand.</returns>
        public static CosmosOrderOperand ForExpression(SqlTypeName type, bool nullable) =>
            new(CosmosOrderOperandKind.Expression) { Type = type ?? throw new ArgumentNullException(nameof(type)), Nullable = nullable };

        /// <summary>
        /// Gets what the operand is.
        /// </summary>
        public CosmosOrderOperandKind Kind { get; }

        /// <summary>
        /// Gets the document path, for a path and a conversion; for a path, <c>null</c> where no fact can be about it.
        /// </summary>
        public JsonDocumentPath? Path { get; private init; }

        /// <summary>
        /// Gets the declared type, for a path, a parameter and an expression.
        /// </summary>
        public SqlTypeName? Type { get; private init; }

        /// <summary>
        /// Gets how a path's column is read back.
        /// </summary>
        public CosmosReading Reading { get; private init; }

        /// <summary>
        /// Gets the format a parse reads a conversion's path with, or <c>null</c> for a cast.
        /// </summary>
        public string? Format { get; private init; }

        /// <summary>
        /// Gets the halves of an instant a conversion's value holds.
        /// </summary>
        public CosmosTemporalParts Held { get; private init; }

        /// <summary>
        /// Gets the stored geographies a distance measures between.
        /// </summary>
        public IReadOnlyList<JsonDocumentPath> Operands { get; private init; } = Array.Empty<JsonDocumentPath>();

        /// <summary>
        /// Gets whether every operand of a distance is a stored geography in <see cref="Operands"/> or one the statement
        /// carries.
        /// </summary>
        public bool AccountedFor { get; private init; }

        /// <summary>
        /// Gets whether a score's best match is its greatest value.
        /// </summary>
        public bool BestFirstDescending { get; private init; }

        /// <summary>
        /// Gets whether an expression's type admits a null.
        /// </summary>
        public bool Nullable { get; private init; }

        /// <summary>
        /// Gets a literal's value.
        /// </summary>
        public JsonScalar Value { get; private init; }

        /// <inheritdoc />
        public override string ToString() => Kind switch
        {
            CosmosOrderOperandKind.Path => $"{(Path is null ? "a path no fact is about" : Path.ToString())} as {Type?.getName()} ({Reading})",
            CosmosOrderOperandKind.Conversion => $"{Path} converted{(Format is null ? "" : " by " + Format)}",
            CosmosOrderOperandKind.Distance => $"a distance over {string.Join(", ", Operands)}",
            CosmosOrderOperandKind.Score => "a score",
            CosmosOrderOperandKind.Literal => $"literal {Value}",
            CosmosOrderOperandKind.Parameter => $"a {Type?.getName()} parameter",
            _ => $"a {Type?.getName()}{(Nullable ? "" : " NOT NULL")} expression",
        };

    }

}
