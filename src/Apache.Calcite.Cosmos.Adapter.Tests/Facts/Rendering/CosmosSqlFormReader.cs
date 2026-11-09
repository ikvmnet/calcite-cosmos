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
    /// Reads the Cosmos SQL text the translator sent into a <see cref="ServiceForm"/>, sharing nothing with the writer but
    /// its spelling of a property and an index, through which a path is printed back: the one reader of that input (spec
    /// §5.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The grammar is the service's, as measured (<see cref="Row"/>): <c>NOT</c> binds tighter than a comparison and than
    /// <c>AND</c>, and <c>AND</c> than <c>OR</c>, which the guards rely on (<c>IS_DEFINED(p) AND NOT IS_NULL(p) AND
    /// (p != @p0)</c>); a primary is a parenthesised condition or ternary <c>(c ? t : e)</c>, a constant, an <c>IS_*</c> test
    /// of a path, an affix or <c>LIKE</c> of one, a path compared with a parameter or with a path, a path's <c>IN</c> of
    /// parameters, or a path alone, so <c>NOT p = @v</c>, which compares <c>NOT p</c>, is no form. A path is the root
    /// alias's names and indexes, or a computed place's call (h10) where the caller names the places the text may read
    /// (<see cref="PlacesOf"/>). Grouping is kept as written: no junction is flattened, regrouped or deduplicated, so that
    /// a form is the one sent.
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

        /// <summary>The registry row the grammar rests on, which every check's claim, read through it, rests on too.</summary>
        internal const string Row = "Service.NotBindsTightestThenAndThenOr";

        static readonly Regex Name = new(@"@[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant);

        static readonly Dictionary<string, CosmosServiceTest> Tests = new(StringComparer.Ordinal)
        {
            ["IS_DEFINED"] = CosmosServiceTest.IsDefined, ["IS_NULL"] = CosmosServiceTest.IsNull, ["IS_PRIMITIVE"] = CosmosServiceTest.IsPrimitive,
            ["IS_STRING"] = CosmosServiceTest.IsString, ["IS_NUMBER"] = CosmosServiceTest.IsNumber, ["IS_BOOL"] = CosmosServiceTest.IsBool,
            ["IS_OBJECT"] = CosmosServiceTest.IsObject, ["IS_ARRAY"] = CosmosServiceTest.IsArray,
        };

        /// <summary>The affixes the printer writes (h07), each of a path and a parameter, case sensitive with no third operand.</summary>
        static readonly Dictionary<string, CosmosServiceTest> Affixes = new(StringComparer.Ordinal)
        {
            ["STARTSWITH"] = CosmosServiceTest.StartsWith, ["ENDSWITH"] = CosmosServiceTest.EndsWith, ["CONTAINS"] = CosmosServiceTest.Contains,
        };

        static readonly Dictionary<string, CosmosServiceTest> Operators = new(StringComparer.Ordinal)
        {
            ["="] = CosmosServiceTest.Equal, ["!="] = CosmosServiceTest.NotEqual, ["<"] = CosmosServiceTest.Less,
            ["<="] = CosmosServiceTest.LessOrEqual, [">"] = CosmosServiceTest.Greater, [">="] = CosmosServiceTest.GreaterOrEqual,
        };

        /// <summary>Reads a text the translator or the printer wrote, its parameters bound to the values given.</summary>
        /// <param name="text">The text.</param>
        /// <param name="parameters">The parameters its names are bound to.</param>
        /// <param name="notOverAnd">The plant "a reader that binds <c>NOT</c> looser than <c>AND</c>", for the precedence test alone.</param>
        /// <param name="places">Each computed place the text may read, by its call as written with its parameters inlined (<see cref="PlacesOf"/>).</param>
        public static ReadForm Read(string text, IReadOnlyList<CosmosParameter> parameters, bool notOverAnd = false, IReadOnlyDictionary<string, string>? places = null)
        {
            var parser = new Parser(text, parameters, notOverAnd, places);
            var (form, written) = parser.Whole();
            return new ReadForm(form, written);
        }

        /// <summary>
        /// Each computed place a statement reads (h10), keyed by its call in each spelling the service may be sent
        /// (<see cref="CosmosFunctionSpellings.Spellings"/>), its literals inlined as <see cref="Inline"/> writes them, and its
        /// paths as the root alias's: what <see cref="Read"/> reads back as the place's spelling.
        /// </summary>
        public static IReadOnlyDictionary<string, string> PlacesOf(IEnumerable<SqlValue.Call> calls)
        {
            var computed = calls.Where(c => c.Spelled is not null).DistinctBy(c => c.Spelled).ToDictionary(c => c.Spelled!, StringComparer.Ordinal);
            var places = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var call in computed.Values)
                foreach (var spelling in CosmosFunctionSpellings.Spellings.Where(s => s.Function == call.Function).Select(s => (long?)s.Spelling).Prepend(null).Distinct())
                {
                    var bound = new List<CosmosParameter>();
                    string? Operand(SqlValue operand) => operand switch
                    {
                        SqlValue.Call { Spelled: { } inner } nested when computed.ContainsKey(inner) => CosmosFunctionSpellings.Spell(nested, Operand),
                        SqlValue.Literal literal => Bind(literal.Value),
                        _ when operand.PathRead is JsonDocumentPath read => Written(read),
                        _ => null,
                    };
                    string Bind(string literal)
                    {
                        var name = "@q" + bound.Count.ToString(CultureInfo.InvariantCulture);
                        bound.Add(new CosmosParameter(name, JsonValues.IsString(literal) ? JsonValues.StringOf(literal) : JsonValues.IsNumber(literal) ? JsonValues.NumberOf(literal)
                            : literal == JsonValues.Of(true) ? true : literal == JsonValues.Of(false) ? false : null));
                        return name;
                    }

                    if (CosmosFunctionSpellings.Spell(call, Operand, spelling) is { } text)
                        places.TryAdd(Inline(text, bound), call.Spelled!);
                }

            return places;
        }

        /// <summary>A path from the root alias, as the printer and the translator write it.</summary>
        static string Written(JsonDocumentPath path)
        {
            var written = new StringBuilder(CosmosImplementor.DefaultRootAlias);
            foreach (var segment in path.Segments)
                if (segment is string name)
                    CosmosSql.WritePropertyAccess(written, name);
                else
                    CosmosSql.WriteIndexAccess(written, checked((int)(long)segment));

            return written.ToString();
        }

        /// <summary>The parameter names a text holds.</summary>
        public static IReadOnlySet<string> Names(string text) => Name.Matches(text).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

        /// <summary>A text with every parameter name suffixed, so that texts bound by lists of their own join apart.</summary>
        public static string Rename(string text, string suffix) => Name.Replace(text, m => m.Value + suffix);

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
            double d => Value(d) is decimal x ? JsonValues.Of(x) : JsonValues.Of(d),
            _ => null,
        };

        /// <summary>
        /// A double the service is sent, as the number it compares: a whole one's own value, and any other's shortest spelling,
        /// whose double it is; never .NET's cast, which keeps fifteen digits and reads 2^63 as 9223372036854780000. <c>null</c>
        /// where no decimal holds it, which is spelled approximately.
        /// </summary>
        static decimal? Value(double d) =>
            double.IsFinite(d) == false ? null
            : Math.Floor(d) == d && Math.Abs(d) < 7.9e28 ? (decimal)new System.Numerics.BigInteger(d)
            : decimal.TryParse(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
                && Cosmos.Facts.Saturation.DoubleBound.Of(x) == d ? x : null;

        // ── Branch-only, the Stage 3 census's (deleted at h16): the whole expression the translator writes ──────────────

        /// <summary>
        /// Branch-only, the Stage 3 census's (deleted at h16): reads any text the translator writes, a condition or a value,
        /// into its syntax, beyond the forms <see cref="Read"/> reads: type tests over any operand, the ternary and
        /// <c>??</c>, <c>LIKE</c> and the functions it is spelled as, calls, arithmetic, <c>IN</c> and <c>[]</c>. The grammar
        /// is <see cref="Read"/>'s, <c>NOT</c> tightest (<see cref="Row"/>), with the ternary loosest and <c>??</c> beneath it,
        /// which no row measures: the translator parenthesises every binary form it writes, so no text it writes leans on
        /// them. Text outside it reads as one <see cref="OpaqueSyntax"/>. A round trip through <see cref="Squeezed"/> checks
        /// only that every token was read, not the tree: a tree read at the wrong precedence prints the same tokens.
        /// </summary>
        public static SqlSyntax ReadSyntax(string text)
        {
            var parser = new SyntaxParser(text);
            try
            {
                var read = parser.Expression();
                return parser.Done ? read : new OpaqueSyntax(text);
            }
            catch (FormatException)
            {
                return new OpaqueSyntax(text);
            }
        }

        /// <summary>A text without the white space outside its quotes: what a syntax printed back is compared by.</summary>
        public static string Squeezed(string text)
        {
            var squeezed = new StringBuilder(text.Length);
            char? quote = null;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (quote is { } q)
                {
                    squeezed.Append(c);
                    if (c == '\\' && i + 1 < text.Length)
                        squeezed.Append(text[++i]);
                    else if (c == q)
                        quote = null;
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                    squeezed.Append(c);
                }
                else if (char.IsWhiteSpace(c) == false)
                    squeezed.Append(c);
            }

            return squeezed.ToString();
        }

        /// <summary>A text the translator wrote, read as syntax; <see cref="Written"/> prints it back.</summary>
        internal abstract record SqlSyntax
        {

            public abstract string Written { get; }

            public virtual IEnumerable<SqlSyntax> Operands => [];

            /// <summary>Whether any of it was read as opaque.</summary>
            public bool Opaque => this is OpaqueSyntax || Operands.Any(o => o.Opaque);

            /// <summary>It and everything beneath it, depth first.</summary>
            public IEnumerable<SqlSyntax> Nodes => Operands.SelectMany(o => o.Nodes).Prepend(this);

            public sealed override string ToString() => Written;

        }

        /// <summary>A path from an alias: its steps as written, a name, a quoted name or an index.</summary>
        internal sealed record PathSyntax(string Alias, IReadOnlyList<string> Steps) : SqlSyntax
        {
            public override string Written => Alias + string.Concat(Steps);
            public bool Indexed => Steps.Any(s => s.Length > 1 && s[0] == '[' && s[1] != '"');
        }

        internal sealed record LiteralSyntax(string Text) : SqlSyntax
        {
            public override string Written => Text;
        }

        internal sealed record ParameterSyntax(string Name) : SqlSyntax
        {
            public override string Written => Name;
        }

        internal sealed record CallSyntax(string Name, IReadOnlyList<SqlSyntax> Arguments) : SqlSyntax
        {
            public override string Written => Name + "(" + string.Join(", ", Arguments.Select(a => a.Written)) + ")";
            public override IEnumerable<SqlSyntax> Operands => Arguments;
        }

        internal sealed record TernarySyntax(SqlSyntax Condition, SqlSyntax Then, SqlSyntax Else) : SqlSyntax
        {
            public override string Written => Condition.Written + " ? " + Then.Written + " : " + Else.Written;
            public override IEnumerable<SqlSyntax> Operands => [Condition, Then, Else];
        }

        internal sealed record CoalesceSyntax(SqlSyntax Left, SqlSyntax Right) : SqlSyntax
        {
            public override string Written => Left.Written + " ?? " + Right.Written;
            public override IEnumerable<SqlSyntax> Operands => [Left, Right];
        }

        /// <summary>A comparison, a connective, arithmetic or <c>||</c>.</summary>
        internal sealed record BinarySyntax(string Operator, SqlSyntax Left, SqlSyntax Right) : SqlSyntax
        {
            public override string Written => Left.Written + " " + Operator + " " + Right.Written;
            public override IEnumerable<SqlSyntax> Operands => [Left, Right];
        }

        internal sealed record UnarySyntax(string Operator, SqlSyntax Operand) : SqlSyntax
        {
            public override string Written => Operator == "NOT" ? "NOT " + Operand.Written : Operator + Operand.Written;
            public override IEnumerable<SqlSyntax> Operands => [Operand];
        }

        internal sealed record InSyntax(SqlSyntax Value, IReadOnlyList<SqlSyntax> Items, bool Not) : SqlSyntax
        {
            public override string Written => Value.Written + (Not ? " NOT IN (" : " IN (") + string.Join(", ", Items.Select(i => i.Written)) + ")";
            public override IEnumerable<SqlSyntax> Operands => Items.Prepend(Value);
        }

        internal sealed record LikeSyntax(SqlSyntax Value, SqlSyntax Pattern, bool Not) : SqlSyntax
        {
            public override string Written => Value.Written + (Not ? " NOT LIKE " : " LIKE ") + Pattern.Written;
            public override IEnumerable<SqlSyntax> Operands => [Value, Pattern];
        }

        /// <summary>An index of what is not a path: a call's, a parenthesised value's.</summary>
        internal sealed record IndexSyntax(SqlSyntax Of, SqlSyntax Index) : SqlSyntax
        {
            public override string Written => Of.Written + "[" + Index.Written + "]";
            public override IEnumerable<SqlSyntax> Operands => [Of, Index];
        }

        internal sealed record MemberSyntax(SqlSyntax Of, string Name) : SqlSyntax
        {
            public override string Written => Of.Written + "." + Name;
            public override IEnumerable<SqlSyntax> Operands => [Of];
        }

        internal sealed record GroupSyntax(SqlSyntax Inner) : SqlSyntax
        {
            public override string Written => "(" + Inner.Written + ")";
            public override IEnumerable<SqlSyntax> Operands => [Inner];
        }

        internal sealed record ArraySyntax(IReadOnlyList<SqlSyntax> Items) : SqlSyntax
        {
            public override string Written => "[" + string.Join(", ", Items.Select(i => i.Written)) + "]";
            public override IEnumerable<SqlSyntax> Operands => Items;
        }

        internal sealed record OpaqueSyntax(string Text) : SqlSyntax
        {
            public override string Written => Text;
        }

        /// <summary>The descent <see cref="ReadSyntax"/> runs, one production a precedence level, over <see cref="Parser"/>'s tokens.</summary>
        sealed class SyntaxParser(string text)
        {

            static readonly HashSet<string> Comparisons = new(StringComparer.Ordinal) { "=", "!=", "<>", "<", "<=", ">", ">=" };

            readonly IReadOnlyList<(string Text, int Start, int End)> _tokens = new Parser(text, [], false).Tokens;
            int _at;

            public bool Done => _at == _tokens.Count;

            string? Peek => _at < _tokens.Count ? _tokens[_at].Text : null;

            string? PeekAt(int ahead) => _at + ahead < _tokens.Count ? _tokens[_at + ahead].Text : null;

            bool Accept(string token)
            {
                if (Peek != token)
                    return false;

                _at++;
                return true;
            }

            void Expect(string token)
            {
                if (Accept(token) == false)
                    throw new FormatException(token);
            }

            public SqlSyntax Expression()
            {
                var condition = Coalesce();
                if (Accept("?") == false)
                    return condition;

                var then = Expression();
                Expect(":");
                return new TernarySyntax(condition, then, Expression());
            }

            SqlSyntax Coalesce()
            {
                var left = Or();
                return Accept("??") ? new CoalesceSyntax(left, Coalesce()) : left;
            }

            SqlSyntax Or() => Junction("OR", And);

            SqlSyntax And() => Junction("AND", Comparison);

            SqlSyntax Junction(string word, Func<SqlSyntax> member)
            {
                var left = member();
                while (Accept(word))
                    left = new BinarySyntax(word, left, member());

                return left;
            }

            SqlSyntax Comparison()
            {
                var left = Additive();
                if (Peek is { } op && Comparisons.Contains(op))
                {
                    _at++;
                    return new BinarySyntax(op, left, Additive());
                }

                var not = Peek == "NOT" && PeekAt(1) is "IN" or "LIKE";
                if (not)
                    _at++;

                if (Accept("IN"))
                {
                    Expect("(");
                    return new InSyntax(left, List(")"), not);
                }

                if (Accept("LIKE"))
                    return new LikeSyntax(left, Additive(), not);

                return not ? throw new FormatException("NOT") : left;
            }

            SqlSyntax Additive()
            {
                var left = Multiplicative();
                while (Peek is "+" or "-" or "||")
                {
                    var op = Peek!;
                    _at++;
                    left = new BinarySyntax(op, left, Multiplicative());
                }

                return left;
            }

            SqlSyntax Multiplicative()
            {
                var left = Unary();
                while (Peek is "*" or "/" or "%")
                {
                    var op = Peek!;
                    _at++;
                    left = new BinarySyntax(op, left, Unary());
                }

                return left;
            }

            SqlSyntax Unary()
            {
                if (Accept("NOT"))
                    return new UnarySyntax("NOT", Unary());

                if (Accept("-"))
                    return new UnarySyntax("-", Unary());

                return Postfix();
            }

            SqlSyntax Postfix()
            {
                var read = Primary();
                while (true)
                {
                    if (Peek == "." && PeekAt(1) is { } name && IsWord(name))
                    {
                        _at += 2;
                        read = read is PathSyntax path ? path with { Steps = [.. path.Steps, "." + name] } : new MemberSyntax(read, name);
                    }
                    else if (Accept("["))
                    {
                        var index = Expression();
                        Expect("]");
                        read = read is PathSyntax path && index is LiteralSyntax step ? path with { Steps = [.. path.Steps, "[" + step.Text + "]"] } : new IndexSyntax(read, index);
                    }
                    else
                        return read;
                }
            }

            SqlSyntax Primary()
            {
                var token = Peek ?? throw new FormatException("end");
                _at++;
                if (token == "(")
                {
                    var inner = Expression();
                    Expect(")");
                    return new GroupSyntax(inner);
                }

                if (token == "[")
                    return new ArraySyntax(List("]"));

                if (token[0] == '@')
                    return new ParameterSyntax(token);

                if (token[0] is '"' or '\'' || char.IsAsciiDigit(token[0]))
                    return new LiteralSyntax(token);

                if (IsWord(token) == false || token is "AND" or "OR" or "NOT" or "IN" or "LIKE")
                    throw new FormatException(token);

                if (Accept("("))
                    return new CallSyntax(token, List(")"));

                return token is "true" or "false" or "null" or "undefined" ? new LiteralSyntax(token) : new PathSyntax(token, []);
            }

            List<SqlSyntax> List(string close)
            {
                var items = new List<SqlSyntax>();
                if (Accept(close))
                    return items;

                do
                    items.Add(Expression());
                while (Accept(","));

                Expect(close);
                return items;
            }

            static bool IsWord(string token) => token is [(>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_', ..];

        }

        /// <summary>
        /// A recursive descent over the text's tokens, each production answering its form and its text as written; a primary
        /// outside the grammar falls back to an opaque reading of the source it spans.
        /// </summary>
        sealed class Parser
        {

            readonly string _text;
            readonly IReadOnlyList<CosmosParameter> _parameters;
            readonly bool _notOverAnd;
            readonly IReadOnlyDictionary<string, string>? _places;
            readonly List<(string Text, int Start, int End)> _tokens = [];
            int _at;

            public Parser(string text, IReadOnlyList<CosmosParameter> parameters, bool notOverAnd, IReadOnlyDictionary<string, string>? places = null)
            {
                _text = text;
                _parameters = parameters;
                _notOverAnd = notOverAnd;
                _places = places;
                Tokenize();
            }

            string? Peek => _at < _tokens.Count ? _tokens[_at].Text : null;

            /// <summary>Branch-only, the Stage 3 census's (deleted at h16): the text's tokens, which <see cref="SyntaxParser"/> reads.</summary>
            internal IReadOnlyList<(string Text, int Start, int End)> Tokens => _tokens;

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
                var start = _at;
                if (Accept("NOT") == false)
                    return Primary();

                // NOT p = @v compares NOT p, which no form reads.
                var operand = _at;
                if (TryPath() is not null && Peek is { } op && Operators.ContainsKey(op))
                    return Opaque(start);

                _at = operand;
                var (form, written) = _notOverAnd ? And() : Not();
                return (ServiceForm.NotOf(form), "NOT " + written);
            }

            (ServiceForm, string) Primary()
            {
                var start = _at;
                if (TryPrimary() is { } read && Peek is null or ")" or "AND" or "OR" or "?" or ":")
                    return read;

                _at = start;
                return Opaque(start);
            }

            (ServiceForm, string)? TryPrimary()
            {
                // A parenthesis opens a condition, or a ternary the printer writes a CASE as, unless it opens a place's call.
                if (PlaceAt(_at) is null && Accept("("))
                {
                    var (inner, written) = Or();
                    if (Accept("?"))
                    {
                        var (then, thenWritten) = Or();
                        if (Accept(":") == false)
                            return null;

                        var (otherwise, otherwiseWritten) = Or();
                        return Accept(")") ? (ServiceForm.ChosenOf(inner, then, otherwise), "(" + written + " ? " + thenWritten + " : " + otherwiseWritten + ")") : null;
                    }

                    return Accept(")") ? (inner, "(" + written + ")") : null;
                }

                if (Peek is "true" or "false")
                    return (ServiceForm.ConstantOf(_tokens[_at++].Text == "true"), _tokens[_at - 1].Text);

                if (Peek is { } word && Tests.TryGetValue(word, out var test))
                {
                    _at++;
                    return Accept("(") && TryPath() is { } tested && Accept(")")
                        ? (tested.Spelled is { } spelled ? ServiceForm.TestOf(test, spelled) : Opaque(word + "(" + tested.Written + ")"), word + "(" + tested.Written + ")")
                        : null;
                }

                if (Peek is { } name && Affixes.TryGetValue(name, out var affix))
                {
                    _at++;
                    if (Accept("(") == false || TryPath() is not { } matched || Accept(",") == false || TryParameter() is not { } argument || Accept(")") == false)
                        return null;

                    var call = name + "(" + matched.Written + ", " + argument.Name + ")";
                    return (matched.Spelled is { } spelled && Literal(argument.Value) is { } text && JsonValues.IsString(text) ? ServiceForm.AffixOf(affix, spelled, JsonValues.StringOf(text)) : Opaque(call), call);
                }

                var at = _at;
                if (TryPath() is { } subject && Accept("LIKE"))
                {
                    if (TryParameter() is not { } pattern)
                        return null;

                    var like = subject.Written + " LIKE " + pattern.Name;
                    return (subject.Spelled is { } spelled && Literal(pattern.Value) is { } text && JsonValues.IsString(text) ? ServiceForm.AffixOf(CosmosServiceTest.Like, spelled, JsonValues.StringOf(text)) : Opaque(like), like);
                }

                _at = at;
                if (TryPath() is { } member && Accept("IN"))
                {
                    if (Accept("(") == false)
                        return null;

                    var points = new List<CosmosParameter>();
                    do
                    {
                        if (TryParameter() is not { } point)
                            return null;

                        points.Add(point);
                    }
                    while (Accept(","));

                    if (Accept(")") == false)
                        return null;

                    var @in = member.Written + " IN (" + string.Join(", ", points.Select(p => p.Name)) + ")";
                    return (member.Spelled is { } spelled && points.Select(p => Literal(p.Value)).ToList() is var literals && literals.All(l => l is not null)
                        ? ServiceForm.InOf(spelled, ValueSet.From(literals.OfType<string>())) : Opaque(@in), @in);
                }

                _at = at;
                if (TryPath() is not { } path)
                    return null;

                // A path alone, a condition the service reads as itself (h14's bare reading).
                if (Peek is null or ")" or "AND" or "OR" or "?" or ":")
                    return path.Spelled is { } bare ? (ServiceForm.BareOf(bare), path.Written) : null;

                if (Peek is not { } op || Operators.TryGetValue(op, out var compare) == false)
                    return null;

                _at++;
                // Two readings compared (h11), the right a path as the left is.
                if (TryParameter() is not { } right)
                    return TryPath() is { } other ? (path.Spelled is { } l && other.Spelled is { } r ? ServiceForm.ComparePathsOf(compare, l, r) : Opaque(path.Written + " " + op + " " + other.Written),
                        path.Written + " " + op + " " + other.Written) : null;

                var comparison = path.Written + " " + op + " " + right.Name;
                return (path.Spelled is { } compared && Literal(right.Value) is { } literal ? ServiceForm.CompareOf(compare, compared, literal) : Opaque(comparison), comparison);
            }

            /// <summary>
            /// The computed place whose call the tokens from one on spell, and the token after it, or <c>null</c>: a call or a
            /// parenthesised operation whose text, its parameters inlined, the caller named a place by.
            /// </summary>
            (string Spelled, int End)? PlaceAt(int at)
            {
                if (_places is null || at >= _tokens.Count)
                    return null;

                var open = _tokens[at].Text == "(" ? at : at + 1 < _tokens.Count && IsWord(_tokens[at].Text) && _tokens[at + 1].Text == "(" ? at + 1 : -1;
                if (open < 0)
                    return null;

                var depth = 0;
                for (var i = open; i < _tokens.Count; i++)
                {
                    depth += _tokens[i].Text switch { "(" => 1, ")" => -1, _ => 0 };
                    if (depth == 0)
                        return _places.TryGetValue(Inline(_text[_tokens[at].Start.._tokens[i].End], _parameters), out var spelled) ? (spelled, i + 1) : null;
                }

                return null;
            }

            /// <summary>A path from the root alias, spelled as the IR spells it, an element's index among its steps; or a computed place's call.</summary>
            (string? Spelled, string Written)? TryPath()
            {
                if (PlaceAt(_at) is { } place)
                {
                    var call = _text[_tokens[_at].Start.._tokens[place.End - 1].End];
                    _at = place.End;
                    return (place.Spelled, call);
                }

                if (Peek is not { } alias || IsWord(alias) == false || Tests.ContainsKey(alias) || Affixes.ContainsKey(alias) || alias is "NOT" or "AND" or "OR" or "LIKE" or "IN" or "true" or "false")
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
                        path = path?.Element(index);
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
