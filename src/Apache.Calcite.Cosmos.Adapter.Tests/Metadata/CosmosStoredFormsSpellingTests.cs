using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;

using Xunit;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// Every spelling of a stored form the registry of spellings recognised, read as the language it is, and the
    /// spellings it recognised that are another language read as that.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What these hold the reader to.</b> The forms were once recognised by rewriting a pattern's text into a key —
    /// <c>\d</c> as <c>[0-9]</c>, a class's members sorted, a run's count merged, and, first, whitespace dropped — and
    /// looking the key up among the forms' own spellings. They are recognised now by the strings the pattern admits,
    /// read by the one reader of a pattern (<see cref="CosmosPatternReader"/>). <see cref="Registry"/> is every
    /// spelling the table was built from, each with the form the table gave it, as main <c>4b2ccb3</c> recognised
    /// them; <see cref="Spellings"/> writes each the ways the old rewrites made one key of; and every one is the same
    /// form now.
    /// </para>
    /// <para>
    /// <b>And what the old reader got wrong, which these hold the new one away from.</b> A space dropped was a
    /// pattern read as a form it is not: <c>^[0-9]{5} $</c> admits no string of five digits. So was a run repeated
    /// no times, <c>Z{0}</c>, read as one. Each is checked here against every form, beside a pattern differing from a
    /// form by one class, widened or narrowed.
    /// </para>
    /// </remarks>
    public class CosmosStoredFormsSpellingTests
    {

        /// <summary>
        /// The spellings the forms were registered under, and the form each was recognised as, written out from the
        /// generators of main <c>4b2ccb3</c> and confirmed against its <c>CosmosStoredForms.Recognise</c>: every temporal
        /// row, every fixed width a pattern could name, both unpadded integers, and the sixteen UUID forms in their
        /// canonical spelling.
        /// </summary>
        /// <remarks>
        /// Two rows are another family's: eight digits is a basic calendar date, and thirty-two a hyphenless UUID with no
        /// letter in it, because the families are asked in that order. They are what main answered, and are answered so
        /// still.
        /// </remarks>
        static readonly (string Pattern, string Form)[] Registry =
        {
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$", "iso8601-utc-seconds"),
            (@"^[0-9]{8}T[0-9]{6}Z$", "iso8601-basic-f0-z"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{1}Z$", "iso8601-instant-f1-z"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{1}Z$", "iso8601-basic-f1-z"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{2}Z$", "iso8601-instant-f2-z"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{2}Z$", "iso8601-basic-f2-z"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z$", "iso8601-utc-milliseconds"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{3}Z$", "iso8601-basic-f3-z"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{4}Z$", "iso8601-instant-f4-z"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{4}Z$", "iso8601-basic-f4-z"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{5}Z$", "iso8601-instant-f5-z"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{5}Z$", "iso8601-basic-f5-z"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{6}Z$", "iso8601-utc-microseconds"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{6}Z$", "iso8601-basic-f6-z"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}Z$", "iso8601-utc-ticks"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{7}Z$", "iso8601-basic-f7-z"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{8}Z$", "iso8601-instant-f8-z"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{8}Z$", "iso8601-basic-f8-z"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{9}Z$", "iso8601-instant-f9-z"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{9}Z$", "iso8601-basic-f9-z"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}Z$", "iso8601-instant-minutes-z"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\+00:00$", "iso8601-instant-f0-offset"),
            (@"^[0-9]{8}T[0-9]{6}\+00:00$", "iso8601-basic-f0-offset"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{1}\+00:00$", "iso8601-instant-f1-offset"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{1}\+00:00$", "iso8601-basic-f1-offset"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{2}\+00:00$", "iso8601-instant-f2-offset"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{2}\+00:00$", "iso8601-basic-f2-offset"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}\+00:00$", "iso8601-instant-f3-offset"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{3}\+00:00$", "iso8601-basic-f3-offset"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{4}\+00:00$", "iso8601-instant-f4-offset"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{4}\+00:00$", "iso8601-basic-f4-offset"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{5}\+00:00$", "iso8601-instant-f5-offset"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{5}\+00:00$", "iso8601-basic-f5-offset"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{6}\+00:00$", "iso8601-instant-f6-offset"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{6}\+00:00$", "iso8601-basic-f6-offset"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}\+00:00$", "iso8601-instant-f7-offset"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{7}\+00:00$", "iso8601-basic-f7-offset"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{8}\+00:00$", "iso8601-instant-f8-offset"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{8}\+00:00$", "iso8601-basic-f8-offset"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{9}\+00:00$", "iso8601-instant-f9-offset"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{9}\+00:00$", "iso8601-basic-f9-offset"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}\+00:00$", "iso8601-instant-minutes-offset"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}$", "iso8601-instant-f0-local"),
            (@"^[0-9]{8}T[0-9]{6}$", "iso8601-basic-f0-local"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{1}$", "iso8601-instant-f1-local"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{1}$", "iso8601-basic-f1-local"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{2}$", "iso8601-instant-f2-local"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{2}$", "iso8601-basic-f2-local"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}$", "iso8601-instant-f3-local"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{3}$", "iso8601-basic-f3-local"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{4}$", "iso8601-instant-f4-local"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{4}$", "iso8601-basic-f4-local"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{5}$", "iso8601-instant-f5-local"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{5}$", "iso8601-basic-f5-local"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{6}$", "iso8601-instant-f6-local"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{6}$", "iso8601-basic-f6-local"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}$", "iso8601-instant-f7-local"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{7}$", "iso8601-basic-f7-local"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{8}$", "iso8601-instant-f8-local"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{8}$", "iso8601-basic-f8-local"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{9}$", "iso8601-instant-f9-local"),
            (@"^[0-9]{8}T[0-9]{6}\.[0-9]{9}$", "iso8601-basic-f9-local"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}$", "iso8601-instant-minutes-local"),
            (@"^[0-9]{4}-[0-9]{2}-[0-9]{2}$", "iso8601-date"),
            (@"^[0-9]{8}$", "iso8601-date-basic"),
            (@"^[0-9]{4}-[0-9]{2}$", "iso8601-year-month"),
            (@"^[0-9]{2}:[0-9]{2}:[0-9]{2}$", "iso8601-time-f0"),
            (@"^[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{1}$", "iso8601-time-f1"),
            (@"^[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{2}$", "iso8601-time-f2"),
            (@"^[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}$", "iso8601-time-f3"),
            (@"^[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{4}$", "iso8601-time-f4"),
            (@"^[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{5}$", "iso8601-time-f5"),
            (@"^[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{6}$", "iso8601-time-f6"),
            (@"^[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}$", "iso8601-time-f7"),
            (@"^[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{8}$", "iso8601-time-f8"),
            (@"^[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{9}$", "iso8601-time-f9"),
            (@"^[0-9]{2}:[0-9]{2}$", "iso8601-time-minutes"),
            (@"^[0-9]{2}$", "integer-fixed-width-2"),
            (@"^[0-9]{3}$", "integer-fixed-width-3"),
            (@"^[0-9]{4}$", "integer-fixed-width-4"),
            (@"^[0-9]{5}$", "integer-fixed-width-5"),
            (@"^[0-9]{6}$", "integer-fixed-width-6"),
            (@"^[0-9]{7}$", "integer-fixed-width-7"),
            (@"^[0-9]{8}$", "iso8601-date-basic"),
            (@"^[0-9]{9}$", "integer-fixed-width-9"),
            (@"^[0-9]{10}$", "integer-fixed-width-10"),
            (@"^[0-9]{11}$", "integer-fixed-width-11"),
            (@"^[0-9]{12}$", "integer-fixed-width-12"),
            (@"^[0-9]{13}$", "integer-fixed-width-13"),
            (@"^[0-9]{14}$", "integer-fixed-width-14"),
            (@"^[0-9]{15}$", "integer-fixed-width-15"),
            (@"^[0-9]{16}$", "integer-fixed-width-16"),
            (@"^[0-9]{17}$", "integer-fixed-width-17"),
            (@"^[0-9]{18}$", "integer-fixed-width-18"),
            (@"^[0-9]{19}$", "integer-fixed-width-19"),
            (@"^[0-9]{20}$", "integer-fixed-width-20"),
            (@"^[0-9]{21}$", "integer-fixed-width-21"),
            (@"^[0-9]{22}$", "integer-fixed-width-22"),
            (@"^[0-9]{23}$", "integer-fixed-width-23"),
            (@"^[0-9]{24}$", "integer-fixed-width-24"),
            (@"^[0-9]{25}$", "integer-fixed-width-25"),
            (@"^[0-9]{26}$", "integer-fixed-width-26"),
            (@"^[0-9]{27}$", "integer-fixed-width-27"),
            (@"^[0-9]{28}$", "integer-fixed-width-28"),
            (@"^[0-9]{29}$", "integer-fixed-width-29"),
            (@"^[0-9]{30}$", "integer-fixed-width-30"),
            (@"^[0-9]{31}$", "integer-fixed-width-31"),
            (@"^[0-9]{32}$", "uuid-hyphenless-lower"),
            (@"^[0-9]{33}$", "integer-fixed-width-33"),
            (@"^[0-9]{34}$", "integer-fixed-width-34"),
            (@"^[0-9]{35}$", "integer-fixed-width-35"),
            (@"^[0-9]{36}$", "integer-fixed-width-36"),
            (@"^[0-9]{37}$", "integer-fixed-width-37"),
            (@"^[0-9]{38}$", "integer-fixed-width-38"),
            (@"^[0-9]{39}$", "integer-fixed-width-39"),
            (@"^[0-9]{40}$", "integer-fixed-width-40"),
            (@"^[0-9]{41}$", "integer-fixed-width-41"),
            (@"^[0-9]{42}$", "integer-fixed-width-42"),
            (@"^[0-9]{43}$", "integer-fixed-width-43"),
            (@"^[0-9]{44}$", "integer-fixed-width-44"),
            (@"^[0-9]{45}$", "integer-fixed-width-45"),
            (@"^[0-9]{46}$", "integer-fixed-width-46"),
            (@"^[0-9]{47}$", "integer-fixed-width-47"),
            (@"^[0-9]{48}$", "integer-fixed-width-48"),
            (@"^[0-9]{49}$", "integer-fixed-width-49"),
            (@"^[0-9]{50}$", "integer-fixed-width-50"),
            (@"^[0-9]{51}$", "integer-fixed-width-51"),
            (@"^[0-9]{52}$", "integer-fixed-width-52"),
            (@"^[0-9]{53}$", "integer-fixed-width-53"),
            (@"^[0-9]{54}$", "integer-fixed-width-54"),
            (@"^[0-9]{55}$", "integer-fixed-width-55"),
            (@"^[0-9]{56}$", "integer-fixed-width-56"),
            (@"^[0-9]{57}$", "integer-fixed-width-57"),
            (@"^[0-9]{58}$", "integer-fixed-width-58"),
            (@"^[0-9]{59}$", "integer-fixed-width-59"),
            (@"^[0-9]{60}$", "integer-fixed-width-60"),
            (@"^[0-9]{61}$", "integer-fixed-width-61"),
            (@"^[0-9]{62}$", "integer-fixed-width-62"),
            (@"^[0-9]{63}$", "integer-fixed-width-63"),
            (@"^[0-9]{64}$", "integer-fixed-width-64"),
            (@"^[0-9]{65}$", "integer-fixed-width-65"),
            (@"^[0-9]{66}$", "integer-fixed-width-66"),
            (@"^[0-9]{67}$", "integer-fixed-width-67"),
            (@"^[0-9]{68}$", "integer-fixed-width-68"),
            (@"^[0-9]{69}$", "integer-fixed-width-69"),
            (@"^[0-9]{70}$", "integer-fixed-width-70"),
            (@"^[0-9]{71}$", "integer-fixed-width-71"),
            (@"^[0-9]{72}$", "integer-fixed-width-72"),
            (@"^[0-9]{73}$", "integer-fixed-width-73"),
            (@"^[0-9]{74}$", "integer-fixed-width-74"),
            (@"^[0-9]{75}$", "integer-fixed-width-75"),
            (@"^[0-9]{76}$", "integer-fixed-width-76"),
            (@"^[0-9]{77}$", "integer-fixed-width-77"),
            (@"^[0-9]{78}$", "integer-fixed-width-78"),
            (@"^[0-9]{79}$", "integer-fixed-width-79"),
            (@"^[0-9]{80}$", "integer-fixed-width-80"),
            (@"^[0-9]{81}$", "integer-fixed-width-81"),
            (@"^[0-9]{82}$", "integer-fixed-width-82"),
            (@"^[0-9]{83}$", "integer-fixed-width-83"),
            (@"^[0-9]{84}$", "integer-fixed-width-84"),
            (@"^[0-9]{85}$", "integer-fixed-width-85"),
            (@"^[0-9]{86}$", "integer-fixed-width-86"),
            (@"^[0-9]{87}$", "integer-fixed-width-87"),
            (@"^[0-9]{88}$", "integer-fixed-width-88"),
            (@"^[0-9]{89}$", "integer-fixed-width-89"),
            (@"^[0-9]{90}$", "integer-fixed-width-90"),
            (@"^[0-9]{91}$", "integer-fixed-width-91"),
            (@"^[0-9]{92}$", "integer-fixed-width-92"),
            (@"^[0-9]{93}$", "integer-fixed-width-93"),
            (@"^[0-9]{94}$", "integer-fixed-width-94"),
            (@"^[0-9]{95}$", "integer-fixed-width-95"),
            (@"^[0-9]{96}$", "integer-fixed-width-96"),
            (@"^[0-9]{97}$", "integer-fixed-width-97"),
            (@"^[0-9]{98}$", "integer-fixed-width-98"),
            (@"^[0-9]{99}$", "integer-fixed-width-99"),
            (@"^(0|[1-9][0-9]*)$", "integer-unpadded"),
            (@"^[1-9][0-9]*$", "integer-unpadded"),
            (@"^[0-9a-f]{8}[0-9a-f]{4}[0-9a-f]{4}[0-9a-f]{4}[0-9a-f]{12}$", "uuid-hyphenless-lower"),
            (@"^[0-7][0-9a-f]{7}[0-9a-f]{4}[0-9a-f]{4}[89ab][0-9a-f]{3}[0-9a-f]{12}$", "uuid-hyphenless-lower-sortable"),
            (@"^[0-9A-F]{8}[0-9A-F]{4}[0-9A-F]{4}[0-9A-F]{4}[0-9A-F]{12}$", "uuid-hyphenless-upper"),
            (@"^[0-7][0-9A-F]{7}[0-9A-F]{4}[0-9A-F]{4}[89AB][0-9A-F]{3}[0-9A-F]{12}$", "uuid-hyphenless-upper-sortable"),
            (@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", "uuid-canonical-lower"),
            (@"^[0-7][0-9a-f]{7}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", "uuid-canonical-lower-sortable"),
            (@"^[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}$", "uuid-canonical-upper"),
            (@"^[0-7][0-9A-F]{7}-[0-9A-F]{4}-[0-9A-F]{4}-[89AB][0-9A-F]{3}-[0-9A-F]{12}$", "uuid-canonical-upper-sortable"),
            (@"^\{[0-9a-f]{8}[0-9a-f]{4}[0-9a-f]{4}[0-9a-f]{4}[0-9a-f]{12}\}$", "uuid-braced-hyphenless-lower"),
            (@"^\{[0-7][0-9a-f]{7}[0-9a-f]{4}[0-9a-f]{4}[89ab][0-9a-f]{3}[0-9a-f]{12}\}$", "uuid-braced-hyphenless-lower-sortable"),
            (@"^\{[0-9A-F]{8}[0-9A-F]{4}[0-9A-F]{4}[0-9A-F]{4}[0-9A-F]{12}\}$", "uuid-braced-hyphenless-upper"),
            (@"^\{[0-7][0-9A-F]{7}[0-9A-F]{4}[0-9A-F]{4}[89AB][0-9A-F]{3}[0-9A-F]{12}\}$", "uuid-braced-hyphenless-upper-sortable"),
            (@"^\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}$", "uuid-braced-lower"),
            (@"^\{[0-7][0-9a-f]{7}-[0-9a-f]{4}-[0-9a-f]{4}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\}$", "uuid-braced-lower-sortable"),
            (@"^\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\}$", "uuid-braced-upper"),
            (@"^\{[0-7][0-9A-F]{7}-[0-9A-F]{4}-[0-9A-F]{4}-[89AB][0-9A-F]{3}-[0-9A-F]{12}\}$", "uuid-braced-upper-sortable"),
        };

        /// <summary>
        /// Matches an atom of a registered spelling, or a count after one.
        /// </summary>
        static readonly Regex Atoms = new(@"(?<count>\{\d+\})|(?<atom>\[[^\]]*\]|\\.|[^\\\[\]{}()|*+?^$])", RegexOptions.CultureInvariant);

        /// <summary>
        /// Writes a spelling every way the registry's rewrites made one key of: as it is; with <c>\d</c> for
        /// <c>[0-9]</c>; with every count spelled out; with every count of two or more split into one and the rest; with
        /// every class's members in the other order; and the second and third at once.
        /// </summary>
        static IEnumerable<string> Spellings(string pattern)
        {
            var digits = pattern.Replace("[0-9]", @"\d");

            return new[] { pattern, digits, Spelled(pattern), Split(pattern), Reordered(pattern), Spelled(digits) }.Distinct();
        }

        static string Spelled(string pattern) =>
            Regex.Replace(pattern, @"(\[[^\]]*\]|\\.|[^\\\[\]{}()|*+?^$])\{(\d+)\}", m => string.Concat(Enumerable.Repeat(m.Groups[1].Value, int.Parse(m.Groups[2].Value))));

        static string Split(string pattern) =>
            Regex.Replace(pattern, @"(\[[^\]]*\]|\\.|[^\\\[\]{}()|*+?^$])\{(\d+)\}", m => int.Parse(m.Groups[2].Value) switch
            {
                < 2 => m.Value,
                2 => m.Groups[1].Value + m.Groups[1].Value,
                var n => m.Groups[1].Value + m.Groups[1].Value + "{" + (n - 1) + "}",
            });

        static string Reordered(string pattern) =>
            Regex.Replace(pattern, @"\[([^\]^\\]+)\]", m => "[" + string.Concat(Regex.Matches(m.Groups[1].Value, ".-.|.").Select(x => x.Value).Reverse()) + "]");

        /// <summary>
        /// Writes a spelling with one atom changed, at every atom in turn.
        /// </summary>
        static IEnumerable<string> EachAtom(string pattern, System.Func<string, string?> change)
        {
            foreach (Match match in Atoms.Matches(pattern))
            {
                if (match.Groups["atom"].Success == false || change(match.Value) is not string changed)
                    continue;

                yield return pattern.Substring(0, match.Index) + changed + pattern.Substring(match.Index + match.Length);
            }
        }

        static string Name(string pattern) => CosmosStoredForms.Recognise(pattern)?.Name ?? "nothing";

        /// <summary>
        /// Every spelling the registry recognised is recognised as the same form, written every way the registry's
        /// rewrites read alike.
        /// </summary>
        [Fact]
        public void EverySpellingTheRegistryRecognisedIsTheSameForm()
        {
            var read = 0;
            var wrong = new List<string>();

            foreach (var (pattern, form) in Registry)
                foreach (var spelling in Spellings(pattern))
                {
                    read++;
                    if (Name(spelling) != form)
                        wrong.Add($"{spelling}: {Name(spelling)}, not {form}");
                }

            wrong.Should().BeEmpty();
            read.Should().BeGreaterThan(Registry.Length * 4, "each spelling is written several ways");
        }

        /// <summary>
        /// A space is a character: the registry dropped it, and read a pattern admitting none of a form's strings as
        /// the form.
        /// </summary>
        /// <remarks>
        /// ECMA-262 has no mode that ignores whitespace, and JSON Schema names no flag. <c>^[0-9]{5} $</c> admits five
        /// digits and a space, which a container written to it holds, and a comparison rendering its literal as five
        /// digits would select none of them.
        /// </remarks>
        [Fact]
        public void ASpaceTheRegistryDroppedIsAnotherLanguage()
        {
            var wrong = new List<string>();

            foreach (var (pattern, _) in Registry)
            {
                var body = pattern.Substring(1, pattern.Length - 2);

                foreach (var spaced in new[] { "^ " + body + "$", "^" + body + " $", EachAtom(pattern, a => a + " ").First() })
                    if (CosmosStoredForms.Recognise(spaced) is { } form)
                        wrong.Add($"{spaced}: {form.Name}");
            }

            wrong.Should().BeEmpty();
        }

        /// <summary>
        /// A run repeated no times matches nothing: the registry read it as the atom once, and a zone written
        /// <c>Z{0}</c> as the zone.
        /// </summary>
        [Fact]
        public void ARunRepeatedNoTimesIsNone()
        {
            CosmosStoredForms.Recognise("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z{0}$")!.Value.Name
                .Should().Be("iso8601-instant-f0-local", "the strings it admits carry no zone");

            CosmosStoredForms.Recognise("^[0-9]{8}T{0}[0-9]{6}Z$").Should().BeNull("fourteen digits and a Z is no basic instant");
        }

        /// <summary>
        /// A pattern a class wider than a form's, anywhere, is another language and no form.
        /// </summary>
        /// <remarks>
        /// One character more at one atom — a digit's class, a separator, a zone, a brace, a hyphen, a UUID's nibble —
        /// admits strings the form does not, and a form is recognised only where every string the pattern admits is the
        /// form's.
        /// </remarks>
        [Fact]
        public void AClassWiderThanAFormsIsNoForm()
        {
            var read = 0;
            var wrong = new List<string>();

            foreach (var (pattern, _) in Registry)
                foreach (var widened in EachAtom(pattern, Widen))
                {
                    read++;
                    if (CosmosStoredForms.Recognise(widened) is { } form)
                        wrong.Add($"{widened}: {form.Name}");
                }

            wrong.Should().BeEmpty();
            read.Should().BeGreaterThan(Registry.Length * 3);

            static string Widen(string atom) =>
                atom[0] == '[' ? atom.Insert(atom.Length - 1, "_") : "[" + atom + "_]";
        }

        /// <summary>
        /// A temporal or numeric pattern a class narrower than a form's is another language too, and no form: the table
        /// claims its languages, not every language inside one.
        /// </summary>
        /// <remarks>
        /// A UUID is the one family that reads its narrowings, the shape being decided rather than looked up: every slot
        /// confined to some hex digits is a canonical UUID still, as a version pinned to <c>[1-5]</c> or a variant to
        /// <c>[89ab]</c> is.
        /// </remarks>
        [Fact]
        public void AClassNarrowerThanATableFormsIsNoForm()
        {
            var read = 0;
            var wrong = new List<string>();

            foreach (var (pattern, form) in Registry)
                foreach (var narrowed in EachAtom(pattern, a => a == "[0-9]" ? "[0-8]" : a == "[1-9]" ? "[2-9]" : null))
                {
                    read++;
                    var name = Name(narrowed);

                    if (form.StartsWith("uuid-") ? name.StartsWith("uuid-") == false : name != "nothing")
                        wrong.Add($"{narrowed}: {name}");
                }

            wrong.Should().BeEmpty();
            read.Should().BeGreaterThan(Registry.Length * 2);
        }

        /// <summary>
        /// The same language written in a way the registry's rewrites did not make one key of is the form now, where it
        /// was nothing: a class of a form's characters written out, a range of one count, a group, a separator written as
        /// a class or an escape, the alternatives of an unpadded integer in the other order, a width of one digit.
        /// </summary>
        [Theory]
        [InlineData(@"^[0123456789]{5}$", "integer-fixed-width-5")]
        [InlineData(@"^[0-9]$", "integer-fixed-width-1")]
        [InlineData(@"^[0-9]{4,4}-[0-9]{2}-[0-9]{2}$", "iso8601-date")]
        [InlineData(@"^(?:[0-9]{4})-[0-9]{2}-[0-9]{2}$", "iso8601-date")]
        [InlineData(@"^[0-9]{4}[-][0-9]{2}\x2D[0-9]{2}$", "iso8601-date")]
        [InlineData(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}[T][0-9]{2}:[0-9]{2}:[0-9]{2}(?:Z)$", "iso8601-utc-seconds")]
        [InlineData(@"(?:^[0-9]{4}-[0-9]{2}-[0-9]{2}$)", "iso8601-date")]
        [InlineData(@"^(?:0|[1-9]\d*)$", "integer-unpadded")]
        [InlineData(@"^([1-9][0-9]*|0)$", "integer-unpadded")]
        [InlineData(@"^0$|^[1-9][0-9]*$", "integer-unpadded")]
        [InlineData(@"^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$", "uuid-canonical-lower")]
        public void TheSameLanguageSpelledAnotherWayIsTheForm(string pattern, string form)
        {
            Name(pattern).Should().Be(form);
        }

    }

}
