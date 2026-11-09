using System.Collections.Generic;
using System.Linq;

using Apache.Calcite.Cosmos.Facts.Rendering;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Planning.Stage3
{

    /// <summary>
    /// Branch-only, deleted at h16: what the owner decided of h14's triage on 2026-11-03, and of post1's partitions 1792 and
    /// of what run 2 would find on 2026-11-04, each push by its set and text
    /// (spec §7.2 step 4; line 6). Q3's declines are accepted by name, as correctness fixes: Calcite computes an
    /// <c>INTEGER</c>'s arithmetic in the column's type and wraps, the service never does, and no declared range bounds the
    /// column; the out-of-domain one is generated. A value <c>CASE</c> compared (3D, h31's <c>Yield</c>) and an unnest
    /// element (3D or 3E) are overridden by name for 3A, their translator code kept until then; a column compared with a
    /// text reading and <c>MOD</c>, whose pushes are the emulator's alone, are overridden under the emulator rule, until a
    /// <c>Production</c> push of either appears. #246's stored-order refusal is the emulator rule's own. Where run 2 found
    /// the service answering apart from Calcite, the case mapping beyond ASCII and <c>LIKE</c>'s <c>_</c> over a line
    /// terminator, the owner accepted the declines by name before it ran (2026-11-04, <see cref="WhereTriage.RunTwo"/>);
    /// under <c>Emulator</c> they are the emulator rule's own refusals. A decision holds only for the reason it was taken
    /// for: a push read again for another reason, or one no name lists, is open.
    /// </summary>
    internal static class OwnerDecisions
    {

        internal const string Accepted = "accepted by name", Overridden = "overridden by name", EmulatorRule = "refused under the emulator rule";

        const string Production = nameof(RenderingSet.Production), Emulator = nameof(RenderingSet.Emulator);

        /// <summary>One decision: its kind, the set and the text of the push it names, and the triage's reason it was taken for.</summary>
        internal sealed record Decision(string Kind, string Set, string Reason, string Text);

        internal static readonly IReadOnlyList<Decision> Named =
        [
            new(Accepted, Emulator, "Q3: integer overflow", @"(((c.price + integer|1) < integer|80) OR (c.price < integer|340))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(((c.price + integer|3) = integer|0) OR ((c.price + integer|3) = integer|80))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"((IS_DEFINED(c.category) AND NOT IS_NULL(c.category) AND (c.category != string|Road Runner)) OR ((c.price * integer|3) > integer|5))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"((IS_DEFINED(c.k) AND NOT IS_NULL(c.k) AND IS_NUMBER(c.k) AND ((c.k - integer|1) != integer|3)) ? (c.k = integer|3) : ((c.k * integer|3) > integer|10))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"((NOT IS_DEFINED(c.tags[0]) OR IS_NULL(c.tags[0]) OR NOT IS_PRIMITIVE(c.tags[0])) ? ((c.price - integer|2) = integer|80) : (STARTSWITH(c.category, string|S) OR (c.category = string|BIKES) OR (c.category = string|none)))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"((NOT IS_DEFINED(c.tags[0]) OR IS_NULL(c.tags[0]) OR NOT IS_PRIMITIVE(c.tags[0])) ? (NOT (((c.price - integer|2) = integer|80) OR IS_NULL(c.price) OR NOT IS_DEFINED(c.price) OR NOT IS_NUMBER(c.price))) : ((NOT STARTSWITH(c.category, string|S)) AND (IS_DEFINED(c.category) AND NOT IS_NULL(c.category) AND (c.category != string|BIKES)) AND (IS_DEFINED(c.category) AND NOT IS_NULL(c.category) AND (c.category != string|none))))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"((c.k + integer|1) = integer|3)"),
            new(Accepted, Emulator, "Q3: integer overflow", @"((c.k + integer|2) < integer|3)"),
            new(Accepted, Emulator, "Q3: integer overflow", @"((c.name = string|SPRINT) OR (c.name = string|B-2) OR (NOT IS_DEFINED((c.price + integer|1)) OR IS_NULL((c.price + integer|1))))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"((c.price * integer|1) < integer|50)"),
            new(Accepted, Emulator, "Q3: integer overflow", @"((c.price * integer|1) = integer|100)"),
            new(Accepted, Emulator, "Q3: integer overflow", @"((c.price * integer|1) > integer|0)"),
            new(Accepted, Emulator, "Q3: integer overflow", @"((c.price * integer|2) = integer|100)"),
            new(Accepted, Emulator, "Q3: integer overflow", @"((c.price * integer|2) > integer|100)"),
            new(Accepted, Emulator, "Q3: integer overflow", @"((c.price + integer|1) = integer|340)"),
            new(Accepted, Emulator, "Q3: integer overflow", @"((c.price + integer|2) = integer|0)"),
            new(Accepted, Emulator, "Q3: integer overflow", @"((c.price + integer|3) > integer|5)"),
            new(Accepted, Emulator, "Q3: integer overflow", @"((c.price - integer|1) = integer|80)"),
            new(Accepted, Emulator, "Q3: integer overflow", @"((c.price - integer|2) = integer|340)"),
            new(Accepted, Emulator, "Q3: integer overflow", @"((c.price - integer|3) = integer|5)"),
            new(Accepted, Emulator, "Q3: integer overflow", @"((c.price < integer|100) ? (IS_DEFINED(c.category) AND NOT IS_NULL(c.category) AND (c.category != string|Road Runner)) : ((c.price - integer|3) < integer|100))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(IS_DEFINED(c.k) AND NOT IS_NULL(c.k) AND IS_NUMBER(c.k) AND ((c.k - integer|3) != integer|0))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(IS_DEFINED(c.k) AND NOT IS_NULL(c.k) AND IS_NUMBER(c.k) AND ((c.k - integer|3) != integer|10))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(IS_DEFINED(c.price) AND NOT IS_NULL(c.price) AND IS_NUMBER(c.price) AND ((c.price + integer|1) != integer|2))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(IS_DEFINED(c.price) AND NOT IS_NULL(c.price) AND IS_NUMBER(c.price) AND ((c.price + integer|3) != integer|0))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(IS_DEFINED(c.price) AND NOT IS_NULL(c.price) AND IS_NUMBER(c.price) AND ((c.price + integer|3) != integer|80))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(IS_DEFINED(c.price) AND NOT IS_NULL(c.price) AND IS_NUMBER(c.price) AND ((c.price - integer|3) != integer|2))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT ((((c.price + integer|1) < integer|80) OR IS_NULL(c.price) OR NOT IS_DEFINED(c.price) OR NOT IS_NUMBER(c.price)) OR ((c.price < integer|340) OR IS_NULL(c.price) OR NOT IS_DEFINED(c.price) OR NOT IS_NUMBER(c.price))))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT (((c.price * integer|1) < integer|50) OR IS_NULL(c.price) OR NOT IS_DEFINED(c.price) OR NOT IS_NUMBER(c.price)))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT (((c.price * integer|1) = integer|100) OR IS_NULL(c.price) OR NOT IS_DEFINED(c.price) OR NOT IS_NUMBER(c.price)))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT (((c.price * integer|1) > integer|0) OR IS_NULL(c.price) OR NOT IS_DEFINED(c.price) OR NOT IS_NUMBER(c.price)))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT (((c.price * integer|2) = integer|100) OR IS_NULL(c.price) OR NOT IS_DEFINED(c.price) OR NOT IS_NUMBER(c.price)))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT (((c.price + integer|3) > integer|100) OR IS_NULL(c.price) OR NOT IS_DEFINED(c.price) OR NOT IS_NUMBER(c.price)))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT (((c.price - integer|1) = integer|80) OR IS_NULL(c.price) OR NOT IS_DEFINED(c.price) OR NOT IS_NUMBER(c.price)))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT (((c.price - integer|2) = integer|340) OR IS_NULL(c.price) OR NOT IS_DEFINED(c.price) OR NOT IS_NUMBER(c.price)))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT ((c.price + integer|1) != integer|2))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT ((c.price + integer|3) != integer|2))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT ((c.price - integer|3) != integer|2))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT (NOT ((c.price + integer|3) > integer|100)))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT (NOT (IS_DEFINED(c.price) AND NOT IS_NULL(c.price) AND IS_NUMBER(c.price) AND ((c.price + integer|3) != integer|2))))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT (NOT (NOT IS_DEFINED((c.price - integer|2)) OR IS_NULL((c.price - integer|2)))))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT (NOT IS_DEFINED((c.price * integer|2)) OR IS_NULL((c.price * integer|2))))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT (NOT IS_DEFINED((c.price + integer|1)) OR IS_NULL((c.price + integer|1))))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT (NOT IS_DEFINED((c.price - integer|2)) OR IS_NULL((c.price - integer|2))))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT (NOT IS_DEFINED((c.price - integer|3)) OR IS_NULL((c.price - integer|3))))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT IS_DEFINED((c.price * integer|1)) OR IS_NULL((c.price * integer|1)))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT IS_DEFINED((c.price * integer|2)) OR IS_NULL((c.price * integer|2)))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT IS_DEFINED((c.price + integer|1)) OR IS_NULL((c.price + integer|1)))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT IS_DEFINED((c.price - integer|1)) OR IS_NULL((c.price - integer|1)))"),
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT IS_DEFINED((c.price - integer|3)) OR IS_NULL((c.price - integer|3)))"),
            new(Accepted, Production, "Q3: integer overflow", @"(((c.price + integer|1) < integer|80) OR (c.price < integer|340))"),
            new(Accepted, Production, "Q3: integer overflow", @"(((c.price + integer|3) = integer|0) OR ((c.price + integer|3) = integer|80))"),
            new(Accepted, Production, "Q3: integer overflow", @"((IS_DEFINED(c.category) AND NOT IS_NULL(c.category) AND (c.category != string|Road Runner)) OR ((c.price * integer|3) > integer|5))"),
            new(Accepted, Production, "Q3: integer overflow", @"((NOT IS_DEFINED(c.tags[0]) OR IS_NULL(c.tags[0]) OR NOT IS_PRIMITIVE(c.tags[0])) ? ((c.price - integer|2) = integer|80) : (STARTSWITH(c.category, string|S) OR (c.category = string|BIKES) OR (c.category = string|none)))"),
            new(Accepted, Production, "Q3: integer overflow", @"((NOT IS_DEFINED(c.tags[0]) OR IS_NULL(c.tags[0]) OR NOT IS_PRIMITIVE(c.tags[0])) ? (NOT (((c.price - integer|2) = integer|80) OR IS_NULL(c.price) OR NOT IS_DEFINED(c.price) OR NOT IS_NUMBER(c.price))) : ((NOT STARTSWITH(c.category, string|S)) AND (IS_DEFINED(c.category) AND NOT IS_NULL(c.category) AND (c.category != string|BIKES)) AND (IS_DEFINED(c.category) AND NOT IS_NULL(c.category) AND (c.category != string|none))))"),
            new(Accepted, Production, "Q3: integer overflow", @"((c.id < string|6) ? (IS_DEFINED(c.price) AND NOT IS_NULL(c.price) AND IS_NUMBER(c.price) AND ((c.price * integer|2) != integer|80)) : (IS_DEFINED(c.category) AND NOT IS_NULL(c.category) AND (c.category != string|6)))"),
            new(Accepted, Production, "Q3: integer overflow", @"((c.name = string|SPRINT) OR (c.name = string|B-2) OR (NOT IS_DEFINED((c.price + integer|1)) OR IS_NULL((c.price + integer|1))))"),
            new(Accepted, Production, "Q3: integer overflow", @"((c.price * integer|1) < integer|50)"),
            new(Accepted, Production, "Q3: integer overflow", @"((c.price * integer|1) = integer|100)"),
            new(Accepted, Production, "Q3: integer overflow", @"((c.price * integer|1) > integer|0)"),
            new(Accepted, Production, "Q3: integer overflow", @"((c.price * integer|2) = integer|100)"),
            new(Accepted, Production, "Q3: integer overflow", @"((c.price * integer|2) > integer|100)"),
            new(Accepted, Production, "Q3: integer overflow", @"((c.price + integer|1) = integer|340)"),
            new(Accepted, Production, "Q3: integer overflow", @"((c.price + integer|2) = integer|0)"),
            new(Accepted, Production, "Q3: integer overflow", @"((c.price + integer|3) > integer|5)"),
            new(Accepted, Production, "Q3: integer overflow", @"((c.price - integer|1) = integer|80)"),
            new(Accepted, Production, "Q3: integer overflow", @"((c.price - integer|3) = integer|5)"),
            new(Accepted, Production, "Q3: integer overflow", @"(IS_DEFINED(c.price) AND NOT IS_NULL(c.price) AND IS_NUMBER(c.price) AND ((c.price + integer|1) != integer|2))"),
            new(Accepted, Production, "Q3: integer overflow", @"(IS_DEFINED(c.price) AND NOT IS_NULL(c.price) AND IS_NUMBER(c.price) AND ((c.price + integer|3) != integer|0))"),
            new(Accepted, Production, "Q3: integer overflow", @"(IS_DEFINED(c.price) AND NOT IS_NULL(c.price) AND IS_NUMBER(c.price) AND ((c.price + integer|3) != integer|80))"),
            new(Accepted, Production, "Q3: integer overflow", @"(IS_DEFINED(c.price) AND NOT IS_NULL(c.price) AND IS_NUMBER(c.price) AND ((c.price - integer|3) != integer|2))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT ((((c.price + integer|1) < integer|80) OR IS_NULL(c.price) OR NOT IS_DEFINED(c.price) OR NOT IS_NUMBER(c.price)) OR ((c.price < integer|340) OR IS_NULL(c.price) OR NOT IS_DEFINED(c.price) OR NOT IS_NUMBER(c.price))))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT (((c.price * integer|1) < integer|50) OR IS_NULL(c.price) OR NOT IS_DEFINED(c.price) OR NOT IS_NUMBER(c.price)))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT (((c.price * integer|1) = integer|100) OR IS_NULL(c.price) OR NOT IS_DEFINED(c.price) OR NOT IS_NUMBER(c.price)))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT (((c.price * integer|1) > integer|0) OR IS_NULL(c.price) OR NOT IS_DEFINED(c.price) OR NOT IS_NUMBER(c.price)))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT (((c.price * integer|2) = integer|100) OR IS_NULL(c.price) OR NOT IS_DEFINED(c.price) OR NOT IS_NUMBER(c.price)))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT (((c.price + integer|3) > integer|100) OR IS_NULL(c.price) OR NOT IS_DEFINED(c.price) OR NOT IS_NUMBER(c.price)))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT (((c.price - integer|1) = integer|80) OR IS_NULL(c.price) OR NOT IS_DEFINED(c.price) OR NOT IS_NUMBER(c.price)))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT ((c.price + integer|1) != integer|2))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT ((c.price + integer|3) != integer|2))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT ((c.price - integer|3) != integer|2))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT (NOT ((c.price + integer|3) > integer|100)))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT (NOT (IS_DEFINED(c.price) AND NOT IS_NULL(c.price) AND IS_NUMBER(c.price) AND ((c.price + integer|3) != integer|2))))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT (NOT (NOT IS_DEFINED((c.price - integer|2)) OR IS_NULL((c.price - integer|2)))))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT (NOT IS_DEFINED((c.price * integer|2)) OR IS_NULL((c.price * integer|2))))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT (NOT IS_DEFINED((c.price + integer|1)) OR IS_NULL((c.price + integer|1))))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT (NOT IS_DEFINED((c.price - integer|2)) OR IS_NULL((c.price - integer|2))))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT (NOT IS_DEFINED((c.price - integer|3)) OR IS_NULL((c.price - integer|3))))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT IS_DEFINED((c.price * integer|1)) OR IS_NULL((c.price * integer|1)))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT IS_DEFINED((c.price * integer|2)) OR IS_NULL((c.price * integer|2)))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT IS_DEFINED((c.price + integer|1)) OR IS_NULL((c.price + integer|1)))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT IS_DEFINED((c.price - integer|1)) OR IS_NULL((c.price - integer|1)))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT IS_DEFINED((c.price - integer|3)) OR IS_NULL((c.price - integer|3)))"),
            // 2026-11-04, after run 1: partitions 1792, both sets, accepted by name with Q3's declines.
            new(Accepted, Emulator, "Q3: integer overflow", @"(NOT (((c.category != string|Road Runner) OR (((c.price * integer|3) > integer|5) OR IS_NULL(c.price) OR NOT IS_DEFINED(c.price) OR NOT IS_NUMBER(c.price))) AND STARTSWITH(c.id, string|B)))"),
            new(Accepted, Production, "Q3: integer overflow", @"(NOT (((c.category != string|Road Runner) OR (((c.price * integer|3) > integer|5) OR IS_NULL(c.price) OR NOT IS_DEFINED(c.price) OR NOT IS_NUMBER(c.price))) AND STARTSWITH(c.id, string|B)))"),
            new(Accepted, Emulator, "Q3: out-of-domain math", @"(LOG(c.price) > integer|50)"),
            new(Accepted, Production, "Q3: out-of-domain math", @"(LOG(c.price) > integer|50)"),
            new(Overridden, Emulator, "a column compared with a text reading: two readings of different kinds, no pair", @"(c.e <= c.id)"),
            new(Overridden, Emulator, "a column compared with a text reading: two readings of different kinds, no pair", @"(c.id <= c.e)"),
            new(Overridden, Emulator, "a column compared with a text reading: two readings of different kinds, no pair", @"(c.id > c.e)"),
            new(Overridden, Emulator, "a function no row names (MOD, ABS, CEIL, FLOOR, SIGN): new function rows", @"(IS_DEFINED((c.k % integer|3)) AND NOT IS_NULL((c.k % integer|3)))"),
            new(Overridden, Emulator, "a value CASE compared: 3D's Yield (no 3A piece)", @"(((c.category = string|bikes) ? integer|1 : integer|0) = integer|0)"),
            new(Overridden, Emulator, "a value CASE compared: 3D's Yield (no 3A piece)", @"(((c.type = string|Link) AND (c.data.type = string|spot) AND (IS_DEFINED(c.data.guid) AND NOT IS_NULL(c.data.guid) AND IS_PRIMITIVE(c.data.guid))) ? (ST_DISTANCE(c.data.data.location, ?CosmosDynamicValue { Ordinal = 0, Form = , Rounding = None, Bound = None, Presence = False, Length = , Padded = False, VariableName = ?0 }) < integer|50000) : boolean|false)"),
            new(Overridden, Production, "a value CASE compared: 3D's Yield (no 3A piece)", @"(((c.category = string|bikes) ? integer|1 : integer|0) = integer|0)"),
            new(Overridden, Emulator, "an unnest element: S's places are rooted at the container (no piece)", @"(t0 = string|steel)"),
            new(Overridden, Emulator, "an unnest element: S's places are rooted at the container (no piece)", @"(t1 = string|L)"),
            new(Overridden, Production, "an unnest element: S's places are rooted at the container (no piece)", @"(t0 = string|bikes)"),
            new(Overridden, Production, "an unnest element: S's places are rooted at the container (no piece)", @"(t0 = string|steel)"),
            new(Overridden, Production, "an unnest element: S's places are rooted at the container (no piece)", @"(t1 = string|L)"),
            new(EmulatorRule, Emulator, "the emulator's stored string order over U+0000 or the high band: refused under Emulator", @"(NOT (((c.label > string|Exact) OR IS_NULL(c.label) OR NOT IS_DEFINED(c.label)) OR (((c.label > string|Exact) OR IS_NULL(c.label) OR NOT IS_DEFINED(c.label)) AND (NOT IS_BOOL(c.label))) OR (((c.label > string|Exact) OR IS_NULL(c.label) OR NOT IS_DEFINED(c.label)) AND (NOT IS_NUMBER(c.label))) OR IS_NULL(c.label) OR (NOT IS_DEFINED(c.label)) OR (NOT IS_PRIMITIVE(c.label)) OR (IS_STRING(c.label) AND ((c.label > string|Exact) OR IS_NULL(c.label) OR NOT IS_DEFINED(c.label)))))"),
            // 2026-11-04, accepted before run 2 ran, where it found the service answering apart from Calcite: the case mapping
            // outside ASCII and LIKE's _ over a line terminator. Each case mapping here is asked at the statement's root, where its
            // path may hold any letter; beneath the conjunct that narrows it S proves it (DESIGN.md, What line 6 asks after run 2).
            new(Accepted, Production, WhereTriage.RunTwo, @"((NOT (c.id LIKE string|_prin_)) OR ((IS_DEFINED(c.category) AND NOT IS_NULL(c.category) AND (c.category != string|Sprint!)) AND (IS_DEFINED(c.category) AND NOT IS_NULL(c.category) AND (c.category != string|shoes))))"),
            new(Accepted, Production, WhereTriage.RunTwo, @"(NOT (NOT (c.category LIKE string|_ike_)))"),
            new(Accepted, Production, WhereTriage.RunTwo, @"(NOT (NOT (c.id LIKE string|_prin_)))"),
            new(Accepted, Production, WhereTriage.RunTwo, @"(NOT (c.category LIKE string|_ike_))"),
            new(Accepted, Production, WhereTriage.RunTwo, @"(NOT (c.id LIKE string|_prin_))"),
            new(Accepted, Production, WhereTriage.RunTwo, @"(c.s LIKE string|a_b)"),
            new(Accepted, Production, WhereTriage.RunTwo, @"(NOT IS_DEFINED(UPPER(c.category)) OR IS_NULL(UPPER(c.category)))"),
            new(Accepted, Production, WhereTriage.RunTwo, @"(UPPER(c.category) > string|bikes)"),
            new(Accepted, Production, WhereTriage.RunTwo, @"(UPPER(c.s) = string|AB)"),
            new(Accepted, Production, WhereTriage.RunTwo, @"(UPPER(c.s) = string|ABC)"),
            new(EmulatorRule, Emulator, WhereTriage.RunTwo, @"(c.t LIKE string|_)"),
            new(EmulatorRule, Emulator, WhereTriage.RunTwo, @"(c.t LIKE string|_0)"),
            new(EmulatorRule, Emulator, WhereTriage.RunTwo, @"(c.t LIKE string|a_b)"),
            new(EmulatorRule, Emulator, WhereTriage.RunTwo, @"(IS_DEFINED(c.category) AND NOT IS_NULL(c.category) AND IS_PRIMITIVE(c.category) AND (UPPER(c.category) != string|SPRINT))"),
            new(EmulatorRule, Emulator, WhereTriage.RunTwo, @"(NOT IS_DEFINED(UPPER(c.category)) OR IS_NULL(UPPER(c.category)))"),
            new(EmulatorRule, Emulator, WhereTriage.RunTwo, @"(UPPER(c.s) = string|ABC)"),
        ];

        static readonly Dictionary<(string Set, string Text), Decision> ByKey = Named.ToDictionary(d => (d.Set, d.Text));

        /// <summary>The owner's decision of a push the triage left Unproven for a reason, or none.</summary>
        internal static Decision? Of(TranslatorCensus.Push push, string reason) =>
            ByKey.GetValueOrDefault((push.Set, push.Text)) is { } decision && decision.Reason == reason ? decision : null;

    }

}
