using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

using static Apache.Calcite.Cosmos.Facts.Saturation.JsonValues;

namespace Apache.Calcite.Cosmos.Facts
{

    /// <summary>
    /// What a query established, read beside what its container states, without the engine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> A container that declares no schema still states what the service guarantees of <c>id</c>,
    /// <c>_ts</c> and <c>_etag</c>, so every filter over it used to open a query context: four a plan, where the split
    /// rule fires, at about two milliseconds each. That is what the plan's demand gate is for: where nothing the engine
    /// would derive can change an answer, the answer is read off the facts directly.
    /// </para>
    /// <para>
    /// <b>When.</b> The declaration has no conditional rule, and the query's facts touch nothing it states:
    /// <list type="bullet">
    ///   <item>every fact is a presence, a value, a domain, a disequality or a bound, and no domain is empty;</item>
    ///   <item>no path carries more than one of them beside its presence, so no two of them meet;</item>
    ///   <item>no path is the root, and none lies beneath another, so no two paths meet either;</item>
    ///   <item>no path is one the declaration states, or lies above or beneath one.</item>
    /// </list>
    /// Anything else goes to the engine.
    /// </para>
    /// <para>
    /// <b>What the rules of JSON add, in that case, and nothing else.</b> A value or a domain is of its own types, and
    /// holds its own characters. A path that is not null is of every other type. A present path has present objects for
    /// its ancestors, the root among them. A path beneath one that holds no object is absent, and has no possible type
    /// and so no possible value. Every other path is what the declaration's own context says it is. Nothing contradicts:
    /// one claim with a presence is satisfiable, and an object above a scalar is the one meeting the gate keeps out.
    /// </para>
    /// <para>
    /// <b>Checked against the engine.</b> <c>JsonFactOverlayTests</c> runs generated sets through both and compares every
    /// answer. The engine is told of the paths beneath what was established, so that it proves them absent as the
    /// overlay does. A query that names no such path leaves the engine knowing less of them than the overlay, never
    /// more.
    /// </para>
    /// <para>
    /// <b>What it reads</b> is what a predicate oracle's stratum assumes, read path by path as <c>PredicateAtom.Kept</c>
    /// reads each conjunct and met — a meet that is the theory's own lattice (<see cref="PathValues"/>), so that one path
    /// may carry several claims.
    /// </para>
    /// </remarks>
    internal sealed class JsonFactOverlay : JsonFactTables
    {

        /// <summary>
        /// What one path established.
        /// </summary>
        sealed class Entry
        {

            public bool Present;
            public ValueSet<string>? Types;
            public ValueSet<string>? Dom;
            public string? Ne;
            public Bound? Lo;
            public Bound? Hi;

        }

        static readonly ValueSet<string> Object = ValueSet.Of(ObjectType);
        static readonly ValueSet<string> Nothing = ValueSet.Of<string>();

        readonly JsonFactTables _outright;
        readonly Dictionary<JsonDocumentPath, Entry> _established;
        readonly HashSet<JsonDocumentPath> _objects;

        JsonFactOverlay(JsonFactTables outright, Dictionary<JsonDocumentPath, Entry> established, HashSet<JsonDocumentPath> objects)
        {
            _outright = outright;
            _established = established;
            _objects = objects;
        }

        /// <summary>
        /// Returns tables reading what each path may hold, as a stratum's assumed predicates say it, beside
        /// <paramref name="outright"/>; or <c>null</c> where the engine could derive something they would not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Already met, so one path may say several things: a rendering's preimage is types and a domain, an inequality
        /// over text is types and a value excluded. What the rules of JSON add to them on one path is
        /// <see cref="PathValues"/>' normal form, read here as it stands: a domain within its types and bounds and of its
        /// own types, a JSON null excluded leaving every other type, booleans or null alone a domain of their values.
        /// </para>
        /// <para>
        /// Refused where the rules would say more: two bounds, which may cross or close on a few integers; more than one
        /// value excluded beside no domain; and nothing possible, or only absence. Each is the engine's to say.
        /// </para>
        /// </remarks>
        /// <param name="established">What each path the stratum's predicates read may hold.</param>
        /// <param name="stated">Every path the declaration states a fact about.</param>
        /// <param name="outright">The tables of the declaration's own context.</param>
        /// <returns>The tables, or <c>null</c>.</returns>
        public static JsonFactOverlay? TryOpen(IReadOnlyDictionary<JsonDocumentPath, PathValues> established, IReadOnlyList<JsonDocumentPath> stated, JsonFactTables outright)
        {
            var entries = new Dictionary<JsonDocumentPath, Entry>();

            foreach (var (path, known) in established)
            {
                var values = PathValues.Normal(known);
                if (path.IsRoot || values.Excluded.Count > 1 || values.Lower is not null && values.Upper is not null)
                    return null;

                var types = values.Types;
                if (types.Count == 0 || values.Values is { Count: 0 })
                    return null;

                entries[path] = new Entry
                {
                    Present = values.IsPresent,
                    Types = types.Count < AllTypes.Count ? types : null,
                    Dom = values.Values,
                    Ne = values.Excluded.Count == 1 ? values.Excluded.First() : null,
                    Lo = values.Lower,
                    Hi = values.Upper,
                };
            }

            return Open(entries, stated, outright);
        }

        /// <summary>
        /// The gate: no path the root, beneath another or meeting one the declaration states.
        /// </summary>
        static JsonFactOverlay? Open(Dictionary<JsonDocumentPath, Entry> entries, IReadOnlyList<JsonDocumentPath> stated, JsonFactTables outright)
        {
            foreach (var path in entries.Keys)
            {
                foreach (var other in entries.Keys)
                    if (IsBeneath(other, path))
                        return null;

                foreach (var declared in stated)
                    if (path == declared || IsBeneath(path, declared) || IsBeneath(declared, path))
                        return null;
            }

            // A present path has a present object above it, at every level up to the root.
            var objects = new HashSet<JsonDocumentPath>();
            foreach (var (path, entry) in entries)
                if (entry.Present)
                    foreach (var ancestor in Ancestors(path))
                        objects.Add(ancestor);

            return new JsonFactOverlay(outright, entries, objects);
        }

        /// <inheritdoc />
        public override bool IsContradictory => _outright.IsContradictory;

        /// <inheritdoc />
        public override bool Present(JsonDocumentPath path)
        {
            if (_established.TryGetValue(path, out var entry))
                return entry.Present;

            if (_objects.Contains(path))
                return true;

            return IsAbsent(path) == false && _outright.Present(path);
        }

        /// <inheritdoc />
        public override bool TryTypes(JsonDocumentPath path, out ValueSet<string> types)
        {
            if (_established.TryGetValue(path, out var entry))
            {
                types = entry.Types!;
                return entry.Types is not null;
            }

            if (IsAbsent(path))
            {
                types = Nothing;
                return true;
            }

            if (_objects.Contains(path))
            {
                types = _outright.TryTypes(path, out var known) ? ValueSet.From(Intersect(known, Object)) : Object;
                return true;
            }

            return _outright.TryTypes(path, out types);
        }

        /// <inheritdoc />
        public override bool TryDom(JsonDocumentPath path, out ValueSet<string> dom)
        {
            if (_established.TryGetValue(path, out var entry))
            {
                dom = entry.Dom!;
                return entry.Dom is not null;
            }

            // A path of no possible type is null if anything, and boolean if anything, which no value is.
            if (IsAbsent(path))
            {
                dom = Nothing;
                return true;
            }

            return _outright.TryDom(path, out dom);
        }

        /// <inheritdoc />
        public override bool Ne(JsonDocumentPath path, string value)
        {
            if (_established.TryGetValue(path, out var entry))
                return entry.Ne == value;

            return IsAbsent(path) == false && _outright.Ne(path, value);
        }

        /// <inheritdoc />
        public override bool TryLo(JsonDocumentPath path, out Bound bound)
        {
            if (_established.TryGetValue(path, out var entry))
            {
                bound = entry.Lo.GetValueOrDefault();
                return entry.Lo is not null;
            }

            if (IsAbsent(path))
            {
                bound = default;
                return false;
            }

            return _outright.TryLo(path, out bound);
        }

        /// <inheritdoc />
        public override bool TryHi(JsonDocumentPath path, out Bound bound)
        {
            if (_established.TryGetValue(path, out var entry))
            {
                bound = entry.Hi.GetValueOrDefault();
                return entry.Hi is not null;
            }

            if (IsAbsent(path))
            {
                bound = default;
                return false;
            }

            return _outright.TryHi(path, out bound);
        }

        /// <inheritdoc />
        /// <remarks>
        /// What the theory's rule says of a domain, read off the overlay's own: a value's characters are its own, and a
        /// domain says which bands the path's strings draw from (<see cref="JsonValues.CharactersOf(ValueSet{string})"/>,
        /// which the rule computes). The engine states it of every domain a run passes through, and a domain only narrows,
        /// so what it holds at the end is what the last domain says, which is the one the overlay reads. An established
        /// path holds no other fact the engine could narrow them by: the gate keeps out every path the declaration states.
        /// </remarks>
        public override bool TryChars(JsonDocumentPath path, out ValueSet<string> bands)
        {
            if (_established.ContainsKey(path) || IsAbsent(path))
            {
                var known = TryDom(path, out var dom);
                bands = known ? CharactersOf(dom) : AllBands;
                return known;
            }

            return _outright.TryChars(path, out bands);
        }

        /// <inheritdoc />
        public override bool Form(JsonDocumentPath path, string token) =>
            _established.ContainsKey(path) == false && IsAbsent(path) == false && _outright.Form(path, token);

        /// <inheritdoc />
        public override bool Extension(JsonDocumentPath path, string key) =>
            _established.ContainsKey(path) == false && IsAbsent(path) == false && _outright.Extension(path, key);

        /// <summary>
        /// Determines whether a path lies beneath an established one that holds no object, which makes it absent.
        /// </summary>
        bool IsAbsent(JsonDocumentPath path)
        {
            foreach (var ancestor in Ancestors(path))
                if (_established.TryGetValue(ancestor, out var entry) && entry.Types is { } types && types.Contains(ObjectType) == false)
                    return true;

            return false;
        }

        static IEnumerable<string> Intersect(ValueSet<string> a, ValueSet<string> b)
        {
            foreach (var member in a)
                if (b.Contains(member))
                    yield return member;
        }

        /// <summary>
        /// Returns every path strictly above a path, the root first; none above the root.
        /// </summary>
        static IEnumerable<JsonDocumentPath> Ancestors(JsonDocumentPath path)
        {
            if (path.IsRoot)
                yield break;

            var names = path.Names;
            var ancestor = JsonDocumentPath.Root;
            yield return ancestor;

            for (var i = 0; i < names.Count - 1; i++)
                yield return ancestor = ancestor.Property(names[i]);
        }

        /// <summary>
        /// Determines whether <paramref name="path"/> lies strictly beneath <paramref name="ancestor"/>.
        /// </summary>
        static bool IsBeneath(JsonDocumentPath path, JsonDocumentPath ancestor)
        {
            if (ancestor.Depth >= path.Depth)
                return false;

            var names = path.Names;
            var prefix = ancestor.Names;
            for (var i = 0; i < prefix.Count; i++)
                if (names[i] != prefix[i])
                    return false;

            return true;
        }

    }

}
