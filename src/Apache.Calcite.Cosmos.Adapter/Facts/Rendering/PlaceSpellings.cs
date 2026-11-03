using System.Text.Encodings.Web;
using System.Text.Json;

using Eggnog;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// A place's spelling and the paths a form over it may read: a path's or a computed place's is itself, and a pair's
    /// (h11) the two it compares, written as a JSON array, which no path's or call's spelling begins with. Writing and
    /// reading text; it decides nothing.
    /// </summary>
    internal static class PlaceSpellings
    {

        static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        /// <summary>A pair's spelling, of its left place's and its right's.</summary>
        public static string Of(string left, string right) => JsonSerializer.Serialize<string[]>([left, right], Relaxed);

        /// <summary>A pair's two places, or <c>null</c> for any other spelling.</summary>
        public static (string Left, string Right)? Sides(string spelling) =>
            spelling.StartsWith('[') && JsonSerializer.Deserialize<string[]>(spelling) is [var left, var right] ? (left, right) : null;

        /// <summary>The spellings a form over the place may read: a pair's two, or the place's own.</summary>
        public static ValueSet<string> Set(string spelling) => Sides(spelling) is (var left, var right) ? ValueSet.Of(left, right) : ValueSet.Of(spelling);

    }

}
