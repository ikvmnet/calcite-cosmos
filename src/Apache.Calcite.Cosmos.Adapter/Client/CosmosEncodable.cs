using System.Collections.Generic;

using Apache.Calcite.Cosmos.Facts.Saturation;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// Whether a value a statement carries can be sent as itself: the SDK writes a statement's parameters as UTF-8 JSON,
    /// its encoder refuses an unpaired surrogate, and no JSON number holds a NaN or an infinity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asked twice, of the same values.</b> A literal is bound when the plan is made, and is asked then
    /// (<see cref="Sql.CosmosParameterList.Add"/>), so the expression it stood in is computed in process; a dynamic
    /// parameter is bound when the statement runs, and is asked then (<see cref="CosmosQueries.Bind"/>), where there is no
    /// plan left to change and the refusal is named. Measured on the probe account and the emulator alike, the encoder
    /// throws <c>EncoderFallbackException</c> before anything is sent (<c>Service.LoneSurrogateParameterIsNeverSent</c>).
    /// A NaN or an infinity is written by the SDK's default serializer as the string <c>"NaN"</c>, <c>"Infinity"</c> or
    /// <c>"-Infinity"</c> (<c>CosmosQueriesTests.ANonFiniteDoubleIsNoJsonNumber</c>), which the service reads as a string
    /// wherever the statement reads the value, where Calcite reads the number.
    /// </para>
    /// <para>
    /// <b>Not a class of the theory's.</b> The theory's classes are of the characters a stored string may hold, which a
    /// pattern, a value or a stored form states, and a string the service stores never holds a lone surrogate: an escaped
    /// one is stored as U+FFFD, and raw bytes that are not UTF-8 are refused where they are read
    /// (<see cref="CosmosJson"/>). Whether a pair is complete is a property of the value as the encoder is handed it,
    /// which no fact about the documents decides, and is read unit by unit by <see cref="JsonValues.IsEncodable"/>, the
    /// one test, which the rendering graph's <c>sendable</c> reads too. Whether a double is finite is likewise the
    /// value's own.
    /// </para>
    /// </remarks>
    internal static class CosmosEncodable
    {

        /// <summary>
        /// Determines whether a value can be sent: whether nothing in it, at any depth, is what <see cref="Unencodable"/>
        /// finds.
        /// </summary>
        /// <param name="value">The value a statement would carry.</param>
        public static bool IsEncodable(object? value) => Unencodable(value) is null;

        /// <summary>
        /// Finds what in a value cannot be sent as itself: the first string, at any depth, that holds an unpaired
        /// surrogate, or the first double that is NaN or an infinity.
        /// </summary>
        /// <param name="value">The value a statement would carry.</param>
        /// <returns>The string or the double, or <c>null</c> where the value can be sent.</returns>
        public static object? Unencodable(object? value)
        {
            switch (value)
            {
                case string text:
                    return JsonValues.IsEncodable(text) ? null : text;

                case double number:
                    return double.IsFinite(number) ? null : number;

                case IDictionary<string, object?> members:
                    foreach (var (name, member) in members)
                        if ((Unencodable(name) ?? Unencodable(member)) is { } found)
                            return found;

                    return null;

                case System.Collections.IEnumerable items:
                    foreach (var item in items)
                        if (Unencodable(item) is { } found)
                            return found;

                    return null;

                default:
                    return null;
            }
        }

    }

}
