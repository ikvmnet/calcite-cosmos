using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// Whether a value a statement carries can be sent at all: the SDK writes a statement's parameters as UTF-8, and its
    /// encoder refuses an unpaired surrogate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asked twice, of the same values.</b> A literal is bound when the plan is made, and is asked then
    /// (<see cref="Sql.CosmosParameterList.Add"/>), so the expression it stood in is computed in process; a dynamic
    /// parameter is bound when the statement runs, and is asked then (<see cref="CosmosQueries.Bind"/>), where there is no
    /// plan left to change and the refusal is named. Measured on the probe account and the emulator alike, the encoder
    /// throws <c>EncoderFallbackException</c> before anything is sent (<c>Service.LoneSurrogateParameterIsNeverSent</c>).
    /// </para>
    /// <para>
    /// <b>Not a class of the theory's.</b> The theory's classes are of the characters a stored string may hold, which a
    /// pattern, a value or a stored form states, and a string the service stores never holds a lone surrogate: an escaped
    /// one is stored as U+FFFD, and raw bytes that are not UTF-8 are refused where they are read
    /// (<see cref="CosmosJson"/>). Whether a pair is complete is a property of the value as the encoder is handed it,
    /// which no fact about the documents decides, and is read here unit by unit.
    /// </para>
    /// </remarks>
    internal static class CosmosEncodable
    {

        /// <summary>
        /// Determines whether a value can be sent: whether every string in it, at any depth, holds no unpaired surrogate.
        /// </summary>
        /// <param name="value">The value a statement would carry.</param>
        public static bool IsEncodable(object? value)
        {
            switch (value)
            {
                case string text:
                    for (var i = 0; i < text.Length; i++)
                    {
                        if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                            i++;
                        else if (char.IsSurrogate(text[i]))
                            return false;
                    }

                    return true;

                case IDictionary<string, object?> members:
                    foreach (var (name, member) in members)
                        if (IsEncodable(name) == false || IsEncodable(member) == false)
                            return false;

                    return true;

                case System.Collections.IEnumerable items:
                    foreach (var item in items)
                        if (IsEncodable(item) == false)
                            return false;

                    return true;

                default:
                    return true;
            }
        }

    }

}
