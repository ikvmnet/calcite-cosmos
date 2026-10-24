using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Apache.Calcite.Cosmos.Facts.Rendering
{

    /// <summary>
    /// S's answers by content (<see cref="RenderingQuestionKey"/>), the least recently asked out first past its bound: each
    /// computed once however many threads ask, by the first, the rest waiting on it; a decline handed to whoever asked for
    /// it and then forgotten.
    /// </summary>
    /// <remarks>What it keeps, and why a decline is not kept: <c>DESIGN.md</c>, <i>The production oracle</i>.</remarks>
    internal sealed class RenderingAnswerMemo
    {

        static readonly ConditionalWeakTable<RenderingTheory, string> Digests = new();

        readonly int _remembered;
        readonly Dictionary<RenderingQuestionKey, LinkedListNode<(RenderingQuestionKey Key, Lazy<RenderingOracle.RenderingBundleAnswer> Answer)>> _entries = [];
        readonly LinkedList<(RenderingQuestionKey Key, Lazy<RenderingOracle.RenderingBundleAnswer> Answer)> _recent = new();

        /// <param name="remembered">How many answers it holds before it forgets the least recently asked.</param>
        public RenderingAnswerMemo(int remembered)
        {
            if (remembered < 1)
                throw new ArgumentOutOfRangeException(nameof(remembered));

            _remembered = remembered;
        }

        /// <summary>How many answers it holds, one being computed among them.</summary>
        public int Count
        {
            get
            {
                lock (_entries)
                    return _entries.Count;
            }
        }

        /// <summary>
        /// The frozen theory's rows, hashed once a theory: generations defined alike share answers, and a planted row shares
        /// none. A primitive is no row, so a plant of one keeps a memo of its own.
        /// </summary>
        public static string DigestOf(RenderingTheory theory) => Digests.GetValue(theory, t => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", t.Graph.Tables.SelectMany(t.Theory.Rows).Select(row => row.ToString()).Order(StringComparer.Ordinal))))));

        /// <summary>
        /// The answer filed under a key, computed the first time it is asked, and whether it was filed already or being
        /// computed; a declined one, or one whose computation threw, is forgotten once handed back.
        /// </summary>
        public RenderingOracle.RenderingBundleAnswer GetOrAdd(RenderingQuestionKey key, Func<RenderingOracle.RenderingBundleAnswer> compute, out bool hit)
        {
            Lazy<RenderingOracle.RenderingBundleAnswer> answer;
            lock (_entries)
            {
                if (hit = _entries.TryGetValue(key, out var node))
                    _recent.Remove(node!);
                else
                    _entries.Add(key, node = new((key, new(compute))));

                _recent.AddFirst(node!);
                answer = node!.Value.Answer;
                if (_entries.Count > _remembered)
                {
                    _entries.Remove(_recent.Last!.Value.Key);
                    _recent.RemoveLast();
                }
            }

            try
            {
                if (answer.Value.Declined is not null)
                    Forget(key, answer);

                return answer.Value;
            }
            catch
            {
                Forget(key, answer);
                throw;
            }
        }

        void Forget(RenderingQuestionKey key, Lazy<RenderingOracle.RenderingBundleAnswer> answer)
        {
            lock (_entries)
                if (_entries.TryGetValue(key, out var node) && ReferenceEquals(node.Value.Answer, answer))
                {
                    _entries.Remove(key);
                    _recent.Remove(node);
                }
        }

    }

}
