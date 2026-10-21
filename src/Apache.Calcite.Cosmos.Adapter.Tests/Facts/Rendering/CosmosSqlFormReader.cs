using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Apache.Calcite.Cosmos.Adapter.Sql;
using Apache.Calcite.Cosmos.Facts;
using Apache.Calcite.Cosmos.Facts.Rendering;
using Apache.Calcite.Cosmos.Facts.Saturation;

using Eggnog;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Facts.Rendering
{

    /// <summary>
    /// Reads the Cosmos SQL text the translator sent into a <see cref="ServiceForm"/>, sharing nothing with the writer: the
    /// one reader of that input (spec §5.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The grammar is the family's: <c>NOT</c> binds tighter than <c>AND</c>, and <c>AND</c> than <c>OR</c>, which the
    /// guards rely on (<c>IS_DEFINED(p) AND NOT IS_NULL(p) AND (p != @p0)</c>); a primary is a parenthesised condition,
    /// an <c>IS_*</c> test of a path, or a path compared with a parameter. Grouping is kept as written: no junction is
    /// flattened, regrouped or deduplicated, so that a form is the one sent.
    /// </para>
    /// <para>
    /// Anything else is read as <see cref="ServiceFormKind.Opaque"/>, up to the next <c>AND</c>, <c>OR</c> or closing
    /// parenthesis outside its own parentheses and ternaries, and the service's answer on it is not known. A parameter is
    /// its value in <see cref="JsonValues"/>' spelling; one bound only when the statement runs makes its comparison opaque.
    /// <see cref="ReadForm.Written"/> prints the parse back with the source's own parentheses and names, so a lost token
    /// or a misread path shows as text that differs from the text sent.
    /// </para>
    /// </remarks>
    internal static class CosmosSqlFormReader
    {

        static readonly Regex Name = new(@"@[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant);

        static readonly Dictionary<string, CosmosServiceTest> Tests = new(StringComparer.Ordinal)
        {
            ["IS_DEFINED"] = CosmosServiceTest.IsDefined, ["IS_NULL"] = CosmosServiceTest.IsNull, ["IS_PRIMITIVE"] = CosmosServiceTest.IsPrimitive,
            ["IS_STRING"] = CosmosServiceTest.IsString, ["IS_NUMBER"] = CosmosServiceTest.IsNumber, ["IS_BOOL"] = CosmosServiceTest.IsBool,
            ["IS_OBJECT"] = CosmosServiceTest.IsObject, ["IS_ARRAY"] = CosmosServiceTest.IsArray,
        };

        static readonly Dictionary<string, CosmosServiceTest> Operators = new(StringComparer.Ordinal)
        {
            ["="] = CosmosServiceTest.Equal, ["!="] = CosmosServiceTest.NotEqual, ["<>"] = CosmosServiceTest.NotEqual, ["<"] = CosmosServiceTest.Less,
            ["<="] = CosmosServiceTest.LessOrEqual, [">"] = CosmosServiceTest.Greater, [">="] = CosmosServiceTest.GreaterOrEqual,
        };

        /// <summary>Reads a text the translator wrote, its parameters bound to the values given.</summary>
        /// <param name="text">The text.</param>
        /// <param name="parameters">The parameters its names are bound to.</param>
        /// <param name="notOverAnd">The plant "a reader that binds <c>NOT</c> looser than <c>AND</c>", for the precedence test alone.</param>
        public static ReadForm Read(string text, IReadOnlyList<CosmosParameter> parameters, bool notOverAnd = false)
        {
            var parser = new Parser(text, parameters, notOverAnd);
            var (form, written) = parser.Whole();
            return new ReadForm(form, written);
        }

        /// <summary>The parameter names a text holds.</summary>
        public static IReadOnlySet<string> Names(string text) => Name.Matches(text).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

        /// <summary>A text with each parameter's value in place of its name: two texts that bind one value under two names are one.</summary>
        public static string Inline(string text, IReadOnlyList<CosmosParameter> parameters) =>
            Name.Replace(text, m => parameters.FirstOrDefault(p => p.Name == m.Value) is { Name: not null } p ? Literal(p.Value) ?? "?" + p.Value : m.Value);

        /// <summary>A value as a literal is spelled, or <c>null</c> for one bound only when the statement runs, or that is no scalar.</summary>
        public static string? Literal(object? value) => value switch
        {
            null => JsonValues.NullValue,
            string s => JsonValues.Of(s),
            bool b => JsonValues.Of(b),
            long l => JsonValues.Of((decimal)l),
            decimal d => JsonValues.Of(d),
            double d => JsonValues.Of(d),
            _ => null,
        };

        /// <summary>
        /// A recursive descent over the text's tokens, each production answering its form and its text as written; a primary
        /// outside the grammar falls back to an opaque reading of the source it spans.
        /// </summary>
        sealed class Parser
        {

            readonly string _text;
            readonly IReadOnlyList<CosmosParameter> _parameters;
            readonly bool _notOverAnd;
            readonly List<(string Text, int Start, int End)> _tokens = [];
            int _at;

            public Parser(string text, IReadOnlyList<CosmosParameter> parameters, bool notOverAnd)
            {
                _text = text;
                _parameters = parameters;
                _notOverAnd = notOverAnd;
                Tokenize();
            }

            string? Peek => _at < _tokens.Count ? _tokens[_at].Text : null;

            public (ServiceForm, string) Whole()
            {
                var whole = Or();
                return _at == _tokens.Count ? whole : (Opaque(_text), _text);
            }

            (ServiceForm, string) Or() => Junction("OR", CosmosServiceTest.Or, And);

            (ServiceForm, string) And() => Junction("AND", CosmosServiceTest.And, Not);

            (ServiceForm, string) Junction(string word, CosmosServiceTest connective, Func<(ServiceForm, string)> member)
            {
                var members = new List<(ServiceForm Form, string Written)> { member() };
                while (Accept(word))
                    members.Add(member());

                return members.Count == 1 ? members[0]
                    : (ServiceForm.JunctionOf(connective, members.Select(m => m.Form)), string.Join(" " + word + " ", members.Select(m => m.Written)));
            }

            (ServiceForm, string) Not()
            {
                if (Accept("NOT") == false)
                    return Primary();

                var (operand, written) = _notOverAnd ? And() : Not();
                return (ServiceForm.NotOf(operand), "NOT " + written);
            }

            (ServiceForm, string) Primary()
            {
                var start = _at;
                if (TryPrimary() is { } read && Peek is null or ")" or "AND" or "OR")
                    return read;

                _at = start;
                return Opaque(start);
            }

            (ServiceForm, string)? TryPrimary()
            {
                if (Accept("("))
                {
                    var (inner, written) = Or();
                    return Accept(")") ? (inner, "(" + written + ")") : null;
                }

                if (Peek is { } word && Tests.TryGetValue(word, out var test))
                {
                    _at++;
                    return Accept("(") && TryPath() is { } tested && Accept(")")
                        ? (tested.Spelled is { } spelled ? ServiceForm.TestOf(test, spelled) : Opaque(word + "(" + tested.Written + ")"), word + "(" + tested.Written + ")")
                        : null;
                }

                if (TryPath() is not { } path || Peek is not { } op || Operators.TryGetValue(op, out var compare) == false)
                    return null;

                _at++;
                if (TryParameter() is not { } right)
                    return null;

                var comparison = path.Written + " " + op + " " + right.Name;
                return (path.Spelled is { } compared && Literal(right.Value) is { } literal ? ServiceForm.CompareOf(compare, compared, literal) : Opaque(comparison), comparison);
            }

            /// <summary>A path from the root alias, spelled as the IR spells it where every step is a name.</summary>
            (string? Spelled, string Written)? TryPath()
            {
                if (Peek is not { } alias || IsWord(alias) == false || Tests.ContainsKey(alias) || alias is "NOT" or "AND" or "OR")
                    return null;

                _at++;
                var written = new StringBuilder(alias);
                var path = alias == CosmosImplementor.DefaultRootAlias ? JsonDocumentPath.Root : (JsonDocumentPath?)null;
                while (true)
                {
                    if (Peek == "." && _at + 1 < _tokens.Count && IsWord(_tokens[_at + 1].Text))
                    {
                        var name = _tokens[_at + 1].Text;
                        _at += 2;
                        CosmosSql.WritePropertyAccess(written, name);
                        path = path?.Property(name);
                    }
                    else if (Peek == "[" && _at + 2 < _tokens.Count && _tokens[_at + 2].Text == "]" && _tokens[_at + 1].Text is ['"', ..] quoted)
                    {
                        var name = JsonSerializer.Deserialize<string>(quoted)!;
                        _at += 3;
                        CosmosSql.WritePropertyAccess(written, name);
                        path = path?.Property(name);
                    }
                    else if (Peek == "[" && _at + 2 < _tokens.Count && _tokens[_at + 2].Text == "]" && int.TryParse(_tokens[_at + 1].Text, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    {
                        _at += 3;
                        CosmosSql.WriteIndexAccess(written, index);
                        path = null;
                    }
                    else
                        break;
                }

                return (path is { } read && read != JsonDocumentPath.Root ? SqlValue.Spell(read) : null, written.ToString());
            }

            CosmosParameter? TryParameter()
            {
                if (Peek is not ['@', ..] name)
                    return null;

                _at++;
                return _parameters.FirstOrDefault(p => p.Name == name) is { Name: not null } bound ? bound : new CosmosParameter(name, new CosmosDynamicValue(-1));
            }

            /// <summary>
            /// The source from a token up to the next <c>AND</c>, <c>OR</c> or closing parenthesis outside the parentheses
            /// and ternaries it opens, read as opaque.
            /// </summary>
            (ServiceForm, string) Opaque(int start)
            {
                _at = start;
                var (depth, ternary) = (0, 0);
                for (; _at < _tokens.Count; _at++)
                {
                    var token = _tokens[_at].Text;
                    if (depth == 0 && (token == ")" || (ternary == 0 && token is "AND" or "OR")))
                        break;

                    (depth, ternary) = token switch
                    {
                        "(" or "[" => (depth + 1, ternary),
                        ")" or "]" => (depth - 1, ternary),
                        "?" when depth == 0 => (depth, ternary + 1),
                        ":" when depth == 0 => (depth, ternary - 1),
                        _ => (depth, ternary),
                    };
                }

                // A primary that spans nothing is no primary: the token is taken alone, so the reading moves on.
                if (_at == start && _at < _tokens.Count)
                    _at++;

                var source = start < _tokens.Count ? _text[_tokens[start].Start.._tokens[Math.Max(start, _at - 1)].End] : "";
                return (Opaque(source), source);
            }

            ServiceForm Opaque(string source)
            {
                var paths = new List<string>();
                var reader = new Parser(source, _parameters, _notOverAnd);
                for (; reader._at < reader._tokens.Count; reader._at++)
                    if (reader.Peek == CosmosImplementor.DefaultRootAlias && (reader._at == 0 || reader._tokens[reader._at - 1].Text != ".") && reader.TryPath() is { Spelled: { } spelled })
                        paths.Add(spelled);

                return ServiceForm.OpaqueOf(source, ValueSet.From(paths));
            }

            bool Accept(string token)
            {
                if (Peek != token)
                    return false;

                _at++;
                return true;
            }

            static bool IsWord(string token) => token is [(>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_', ..];

            void Tokenize()
            {
                for (var i = 0; i < _text.Length;)
                {
                    var c = _text[i];
                    var start = i;
                    if (char.IsWhiteSpace(c))
                    {
                        i++;
                        continue;
                    }

                    if (char.IsAsciiLetter(c) || c is '_' or '@')
                        for (i++; i < _text.Length && (char.IsAsciiLetterOrDigit(_text[i]) || _text[i] == '_'); i++) ;
                    else if (char.IsAsciiDigit(c))
                        for (i++; i < _text.Length && (char.IsAsciiLetterOrDigit(_text[i]) || _text[i] == '.'); i++) ;
                    else if (c is '"' or '\'')
                        for (i++; i < _text.Length && _text[i] != c; i++)
                            i += _text[i] == '\\' ? 1 : 0;
                    else if (i + 1 < _text.Length && _text.Substring(i, 2) is "!=" or "<>" or "<=" or ">=" or "||" or "??")
                        i++;

                    i = Math.Min(i + (char.IsAsciiLetterOrDigit(c) || c is '_' or '@' ? 0 : 1), _text.Length);
                    _tokens.Add((_text[start..i], start, i));
                }
            }

        }

    }

}
