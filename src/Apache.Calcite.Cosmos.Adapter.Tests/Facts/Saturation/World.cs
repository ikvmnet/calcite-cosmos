using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Saturation;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Saturation
{

    /// <summary>
    /// How the behaviours nobody has measured fall in one trial: a coin per question, tossed once, so that one predicate
    /// asked twice of one document answers the same, and every answer Calcite might give is a world some trial lives in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A coin only lands where the oracle's own table admits.</b> Where a reading's behaviour has not been measured —
    /// two <c>VARIANT</c>s compared, the cast to text over a raw value — <c>PredicateAtom</c> answers every truth value it
    /// cannot rule out, and the coin picks among those. So the worlds test the reasoning over the table, not the table,
    /// which only a measurement can; where the table is measured the evaluator is written from the measurement and the
    /// coin is never tossed.
    /// </para>
    /// <para>
    /// <b>A parameter holds one value per world</b>, a SQL null among them: nothing is known of a parameter, so the
    /// oracle may decide nothing that one value of it would contradict.
    /// </para>
    /// </remarks>
    sealed class World
    {

        static readonly Truths[] Order = { Truths.True, Truths.False, Truths.Unknown, Truths.Raises };

        static readonly string[] Strings = { "30", "x", "true", "", "30.0" };

        static readonly decimal[] Numbers = { 30m, 7m, -1m, 2.5m };

        readonly ulong _seed;

        public World(int seed)
        {
            _seed = unchecked((ulong)seed * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL);
        }

        /// <summary>How many coins this world has tossed: questions it chose one of several answers to.</summary>
        public int Tossed { get; private set; }

        /// <summary>
        /// Picks one of the truth values, the same each time this world is asked the same question.
        /// </summary>
        public Truths Choose(Truths among, string question)
        {
            var members = Order.Where(t => (among & t) != 0).ToArray();
            if (members.Length == 0)
                throw new ArgumentOutOfRangeException(nameof(among), among, "A coin needs a side.");

            if (members.Length > 1)
                Tossed++;

            return members[(int)(Hash(question) % (ulong)members.Length)];
        }

        /// <summary>
        /// Returns the value a parameter holds in this world: a SQL null a fifth of the time, and otherwise a value of its
        /// type.
        /// </summary>
        public SqlDatum Parameter(SqlValue.Parameter parameter)
        {
            var roll = Hash("?" + parameter.Ordinal + ":" + parameter.Type);
            if (roll % 5 == 0)
                return SqlDatum.Null;

            var pick = (int)(roll / 5 % 1000);
            return parameter.Type switch
            {
                "VARCHAR" or "CHAR" => new SqlDatum.Text(Strings[pick % Strings.Length]),
                "BOOLEAN" => new SqlDatum.Boolean(pick % 2 == 0),
                "DOUBLE" or "FLOAT" or "REAL" => new SqlDatum.Double((double)Numbers[pick % Numbers.Length]),
                _ => new SqlDatum.Exact(Numbers[pick % Numbers.Length]),
            };
        }

        ulong Hash(string question)
        {
            // FNV-1a over the seed and the question, so that a world is the same in every process.
            var hash = 0xcbf29ce484222325UL ^ _seed;
            foreach (var c in question)
            {
                hash ^= c;
                hash *= 0x100000001b3UL;
            }

            hash ^= hash >> 29;
            hash *= 0xbf58476d1ce4e5b9UL;
            return hash ^ (hash >> 32);
        }

    }

}
