using System;
using System.Collections.Generic;
using System.Linq;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts.Saturation
{

    /// <summary>
    /// What a string function's value draws its characters from, for each function the theory reads a call of: which
    /// operands, and what the function makes of the classes each one's characters are in. In one place, as
    /// <see cref="PredicateAtom"/> is for what a comparison means, and read by the rules of a value's characters
    /// (<see cref="PredicateRules"/>), which state nothing of a function this does not name.
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

        /// <summary>
        /// One function: which operands its value draws characters from, by position, and the rows of what it makes of each
        /// one's beyond keeping every class (<see cref="JsonClassMap"/>), which the rules read as data (<c>callMaps</c>).
        /// </summary>
        sealed record Draw(Func<long, bool> Reads, JsonClassMap[] Maps);

        /// <summary>A pair's units cut apart: a surrogate may be left unpaired.</summary>
        static readonly JsonClassMap Cut = new(SurrogateBand, UnpairedClass);

        /// <summary>
        /// Java's full upper-case mapping: a character from U+E000 mapped to letters below U+D800, as the ligatures U+FB13
        /// to U+FB17 are to Armenian.
        /// </summary>
        static readonly JsonClassMap Upper = new(HighBand, NonAsciiBand);

        static readonly Draw Every = new(_ => true, []);

        static readonly Draw First = new(operand => operand == 0, []);

        static readonly Draw Window = new(operand => operand == 0, [Cut]);

        /// <summary>The functions, by the name the loader spells a call of each with.</summary>
        static readonly Dictionary<string, Draw> Functions = new(StringComparer.Ordinal)
        {
            ["||"] = Every,
            ["CONCAT"] = Every,
            ["CASE"] = Every,
            ["UPPER"] = new(operand => operand == 0, [Upper]),
            ["LOWER"] = First,
            ["SUBSTRING"] = Window,
            ["LEFT"] = Window,
            ["RIGHT"] = Window,
            ["REVERSE"] = First,
            ["REPEAT"] = First,
            ["CAST"] = First,
            ["REPLACE"] = new(operand => operand is 0 or 2, []),
            ["TRIM BOTH"] = new(operand => operand == 1, []),
            ["TRIM LEADING"] = new(operand => operand == 1, []),
            ["TRIM TRAILING"] = new(operand => operand == 1, []),
        };

        /// <summary>
        /// Determines whether the theory reads a call of a function.
        /// </summary>
        public static bool Knows(string function) => Functions.ContainsKey(function);

        /// <summary>
        /// Determines whether a function's value draws characters from an operand.
        /// </summary>
        /// <param name="function">The function, one the theory reads.</param>
        /// <param name="operand">The operand's position, from 0.</param>
        public static bool Reads(string function, long operand) => Functions[function].Reads(operand);

        /// <summary>
        /// The rows of what each function makes of an operand's classes: the root's data, assumed once a function, every
        /// function the theory reads among them (<c>JsonRules.CallMaps</c>).
        /// </summary>
        public static IEnumerable<(string Function, ValueSet<JsonClassMap> Maps)> Maps =>
            Functions.Select(f => (f.Key, ValueSet.From(f.Value.Maps)));

        /// <summary>
        /// Returns the classes a function's value may hold characters of, drawn from an operand holding characters of
        /// these under its rows, beside those it draws from the operands after it.
        /// </summary>
        /// <param name="operand">The classes of the operand.</param>
        /// <param name="maps">The function's rows (<see cref="Maps"/>).</param>
        /// <param name="rest">The classes the function draws from the operands after it.</param>
        public static ValueSet<string> Drawn(ValueSet<string> operand, ValueSet<JsonClassMap> maps, ValueSet<string> rest) =>
            ClassSet(ClassMask(Mapped(operand, maps)) | ClassMask(rest));

        /// <summary>
        /// Returns the classes a function's value may hold characters of where its operand holds characters of these,
        /// under its rows: for a check to hold the rows to what Calcite computes.
        /// </summary>
        internal static JsonCharacterBand Image(string function, JsonCharacterBand operand) =>
            ClassMask(Mapped(ClassSet(operand), ValueSet.From(Functions[function].Maps)));

    }

}
