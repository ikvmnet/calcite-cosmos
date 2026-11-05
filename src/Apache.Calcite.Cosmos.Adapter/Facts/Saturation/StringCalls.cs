using System;
using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Rendering;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// What a string function's value draws its characters from, for each function the theory reads a call of: which
    /// operands, and what the function makes of the classes each one's characters are in. Rows, in one place, as
    /// <see cref="PredicateAtom"/> is for what a comparison means: the root of the facts graph holds them
    /// (<c>JsonRules.CallDraws</c>, <c>JsonRules.CallMaps</c>), and the rules of a value's characters
    /// (<see cref="PredicateRules"/>) join them by the call's name and state nothing of a function they do not name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Calcite's functions, as its runtime computes them.</b> A concatenation, a repetition, a reversal, a replacement's
    /// subject and replacement, a conditional's arms and a cast from text to text are made of their operands' characters
    /// and nothing else. A window and a trim keep some of them: a window splitting a surrogate pair keeps a lone one,
    /// which is still a surrogate, and unpaired now (F1); a reversal keeps a pair whole, and a replacement matches its
    /// search by code point, never inside a pair. A case mapping maps each character, and Java's full mapping, which Calcite's
    /// <c>UPPER</c> and <c>LOWER</c> are, keeps every class but one where it was: over every code point, read by the
    /// theory's own reader of a unit, only <c>UPPER</c> takes a character from U+E000 to one below U+D800 — U+FB13 to
    /// U+0544 U+0546 — and no mapping makes a surrogate, a character the service trims or U+0000 of anything else
    /// (<c>StringCallsTests</c> holds this to Calcite's runtime over every code point).
    /// </para>
    /// <para>
    /// <b>What a function does not name, it does not compute here.</b> A function this table does not name may make
    /// characters of its own — a conversion such as <c>ToString</c>, a JSON function — and a call of one is given no
    /// characters, so a string function over it is computed in process. So is one over a parameter, which may hold
    /// anything until it is bound.
    /// </para>
    /// </remarks>
    internal static class StringCalls
    {

        /// <summary>A pair's units cut apart: a surrogate may be left unpaired.</summary>
        static readonly JsonClassMap Cut = new(SurrogateBand, UnpairedClass);

        /// <summary>
        /// Java's full upper-case mapping: a character from U+E000 mapped to letters below U+D800, as the ligatures U+FB13
        /// to U+FB17 are to Armenian.
        /// </summary>
        static readonly JsonClassMap Upper = new(HighBand, NonAsciiBand);

        /// <summary>
        /// The functions, by the name the loader spells a call of each with: the operands its value draws characters from,
        /// by the loader's ordinals, <see cref="FunctionKey.Every"/> where it draws from each alike, as a function row names
        /// every operand; and the rows of what it makes of each one's beyond keeping every class (<see cref="JsonClassMap"/>).
        /// The root's data, assumed once a function, every function the theory reads among them.
        /// </summary>
        public static IReadOnlyList<(string Function, ValueSet<long> Draws, ValueSet<JsonClassMap> Maps)> Rows { get; } =
        [
            Row("||", [FunctionKey.Every]),
            Row("CONCAT", [FunctionKey.Every]),
            Row("CASE", [FunctionKey.Every]),
            Row("UPPER", [0], Upper),
            Row("LOWER", [0]),
            Row("SUBSTRING", [0], Cut),
            Row("LEFT", [0], Cut),
            Row("RIGHT", [0], Cut),
            Row("REVERSE", [0]),
            Row("REPEAT", [0]),
            Row("CAST", [0]),
            Row("REPLACE", [0, 2]),
            Row("TRIM BOTH", [1]),
            Row("TRIM LEADING", [1]),
            Row("TRIM TRAILING", [1]),
        ];

        static (string, ValueSet<long>, ValueSet<JsonClassMap>) Row(string function, long[] draws, params JsonClassMap[] maps) =>
            (function, ValueSet.From(draws), ValueSet.From(maps));

        /// <summary>
        /// Returns the classes a function's value may hold characters of, drawn from an operand holding characters of
        /// these under its rows, beside those it draws from the operands after it.
        /// </summary>
        /// <param name="operand">The classes of the operand.</param>
        /// <param name="maps">The function's rows (<see cref="Rows"/>).</param>
        /// <param name="rest">The classes the function draws from the operands after it.</param>
        public static ValueSet<string> Drawn(ValueSet<string> operand, ValueSet<JsonClassMap> maps, ValueSet<string> rest) =>
            ClassSet(ClassMask(Mapped(operand, maps)) | ClassMask(rest));

        /// <summary>
        /// Returns the classes a function's value may hold characters of where its operand holds characters of these,
        /// under its rows: for a check to hold the rows to what Calcite computes.
        /// </summary>
        internal static JsonCharacterBand Image(string function, JsonCharacterBand operand) =>
            ClassMask(Mapped(ClassSet(operand), Rows.Single(r => r.Function == function).Maps));

    }

}
