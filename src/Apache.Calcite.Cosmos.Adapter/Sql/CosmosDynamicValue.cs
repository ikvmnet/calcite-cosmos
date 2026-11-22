using System.Globalization;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// Stands in a bound parameter for a value the plan does not have: the ordinal of one of the
    /// statement's own dynamic parameters, whose value arrives with the execution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A prepared statement is compiled once and run many times, so a value written as <c>?</c> is not
    /// a value at plan time — it is an ordinal. The statement text is finished without it, the
    /// parameter is bound under a name like every other, and only the slot behind the name is left
    /// open. <see cref="Client.CosmosQueries.Bind"/> closes it, reading the value out of the data
    /// context the execution supplies.
    /// </para>
    /// <para>
    /// The ordinal is what is carried, because the ordinal is what the plan knows of the value. What
    /// the value will be is the caller's; what type it is the plan already decided, and the slot carries
    /// that type (<see cref="TypeName"/>) and the length a sized text or binary type holds a value to
    /// (<see cref="Length"/>), because both change what Calcite reads. The other additions are what the
    /// plan chose from the comparison the value sits in: a spelling taken from the path it is compared
    /// with, which <see cref="Form"/> says more about, and a bound (<see cref="Bound"/>). A <c>FETCH</c> or
    /// <c>OFFSET</c> count's slot carries instead its clause and the rounding policy Calcite's limit reads
    /// it with (<see cref="RowCount"/>, <see cref="RowCountRounding"/>).
    /// </para>
    /// </remarks>
    /// <param name="Ordinal">The dynamic parameter's index, as <c>RexDynamicParam</c> numbers them.</param>
    public readonly record struct CosmosDynamicValue(int Ordinal)
    {

        /// <summary>
        /// Gets the stored form an instant or a UUID is to be written in when it arrives, or <c>null</c>
        /// where the value is bound as it stands.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The one case where the value is not bound as it stands, and why it is this one.</b> A
        /// comparison of a path read as an instant against a literal is lowered while the plan is
        /// made, the literal written in the path's declared spelling — see
        /// <see cref="Metadata.CosmosFactRewriter"/>. Against a parameter there is nothing to write
        /// yet, and the instant itself is no value the service could compare a string with. So the
        /// slot carries the spelling too, and <see cref="Client.CosmosQueries.Bind"/> writes the value
        /// into it when it arrives, as the plan would have written the literal.
        /// </para>
        /// <para>
        /// Still a decision about types and not about the value: the plan knows the path's form and
        /// which comparison the parameter sits in, and both are settled before any value exists.
        /// </para>
        /// </remarks>
        public Metadata.CosmosRepresentation? Form { get; init; }

        /// <summary>
        /// Gets which way an instant between two of <see cref="Form"/>'s spellings is rounded, which
        /// the comparison it is bound into decides.
        /// </summary>
        public Metadata.CosmosTemporalRounding Rounding { get; init; }

        /// <summary>
        /// Gets the bound a string is written as in place of itself, for the comparison the reader sends where the value
        /// is no anchor; <see cref="CosmosAnchorBound.None"/> for the value as it stands.
        /// </summary>
        /// <remarks>
        /// The same kind of decision <see cref="Form"/> is: the plan knows which comparison the slot sits in and what the
        /// service compares there, and writes the bound into the statement's fallback as it would the value — see
        /// <see cref="CosmosOrderComparison"/>.
        /// </remarks>
        public CosmosAnchorBound Bound { get; init; }

        /// <summary>
        /// Gets whether the slot binds only whether the value is there: <c>true</c> for a value, <c>null</c> for none.
        /// </summary>
        /// <remarks>
        /// A comparison's guard tests its parameter for null and nothing else, so it is sent nothing the service would
        /// have to read: not the value itself, which may be one it has no type for, such as Calcite's boxed <c>UUID</c>.
        /// </remarks>
        public bool Presence { get; init; }

        /// <summary>
        /// Gets the length the parameter's type holds a value to — the UTF-16 code units of its <c>CHAR(n)</c> or
        /// <c>VARCHAR(n)</c>, or the bytes of its <c>BINARY(n)</c> or <c>VARBINARY(n)</c> — or <c>null</c> for a type that
        /// holds any.
        /// </summary>
        /// <remarks>
        /// What the value is, in Calcite, and so what the service has to be sent: Calcite's generated code truncates a
        /// parameter to its type's length (<c>RexToLixTranslator.visitDynamicParam</c>), and the statement compares what
        /// Calcite would.
        /// </remarks>
        public int? Length { get; init; }

        /// <summary>
        /// Gets whether a value shorter than <see cref="Length"/> is padded to it, as Calcite pads a <c>CHAR(n)</c>
        /// parameter with spaces; it pads no binary one.
        /// </summary>
        public bool Padded { get; init; }

        /// <summary>
        /// Gets the parameter's SQL type, as Calcite types the <c>RexDynamicParam</c>, or <c>null</c> for a slot made
        /// without one, whose value is bound as it arrives.
        /// </summary>
        /// <remarks>
        /// Calcite compares a value of this type, whatever the caller bound. Its own JDBC driver hands the caller's class
        /// to the data context — a long for a <c>DOUBLE</c> parameter, a string for an integer one — and its generated code
        /// converts the value to the type when a row reads it (<c>Calcite.AParameterIsConvertedToItsTypeWhenRead</c>). So
        /// the slot carries the type, and <see cref="Client.CosmosQueries.Bind"/> converts the value as that code does
        /// before it is sent.
        /// </remarks>
        public org.apache.calcite.sql.type.SqlTypeName? TypeName { get; init; }

        /// <summary>
        /// Gets the clause the slot holds the row count of, <c>FETCH</c> or <c>OFFSET</c>, or <c>null</c> for a slot that
        /// holds a value.
        /// </summary>
        /// <remarks>
        /// Calcite's limit reads a count otherwise than a row reads a value: as a decimal, whose ceiling is the rows it
        /// takes or skips (<see cref="CosmosRowLimit.Rows"/>). So the slot says it holds a count, and
        /// <see cref="Client.CosmosQueries.Bind"/> reads the value so.
        /// </remarks>
        public string? RowCount { get; init; }

        /// <summary>
        /// Gets the rounding policy the planner's context held for a row count, which Calcite's limit applies to it, or
        /// <c>null</c> for none.
        /// </summary>
        public org.apache.calcite.adapter.enumerable.FetchOffsetRoundingPolicy? RowCountRounding { get; init; }

        /// <summary>
        /// Gets the name Calcite binds the value under in the data context.
        /// </summary>
        /// <remarks>
        /// <c>?0</c>, <c>?1</c> and so on — the spelling Calcite's own generated code uses for the
        /// same lookup, so a plan reading it here and a plan reading it there see one value.
        /// </remarks>
        public string VariableName => "?" + Ordinal.ToString(CultureInfo.InvariantCulture);

    }

}
