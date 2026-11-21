using System.Text;
using System.Text.RegularExpressions;

namespace PersonalServerManager.Hydrus;

public sealed class HydrusFeedQuery
{
    private const int MaximumClauses = 4096;
    private const int MaximumLiterals = 128;
    private const int MaximumConversionSteps = 65536;
    private readonly string _query;
    private int _position;
    private int _depth;
    private int _literals;

    private HydrusFeedQuery(string query) => _query = query;

    public static IReadOnlyList<object> Parse(string query)
    {
        if (query is not null && query.Length > 16384) throw new ArgumentException("Hydrus feed query is too long.", nameof(query));
        if (string.IsNullOrWhiteSpace(query)) return ["system:everything"];
        if (query.Trim().Equals("system:everything", StringComparison.Ordinal)) return ["system:everything"];
        var parser = new HydrusFeedQuery(query);
        var expression = parser.Expression(0);
        parser.SkipWhitespace();
        if (parser._position != query.Length) throw Invalid();
        var steps = 0;
        var clauses = ToCnf(expression, false, ref steps);
        var result = new List<object>();
        foreach (var clause in clauses)
        {
            var literals = clause.Distinct().ToArray();
            if (literals.Any(literal => literals.Contains(literal with { Negated = !literal.Negated }))) continue;
            var values = new List<string>();
            foreach (var literal in literals)
            {
                if (literal.Text.StartsWith("system:", StringComparison.Ordinal))
                {
                    if (literal.Negated) throw new NotSupportedException("This system predicate cannot be negated.");
                }
                values.Add(literal.Negated ? "-" + literal.Text : literal.Text);
            }
            if (values.Count == 0) throw Invalid();
            result.Add(values.Count == 1 ? values[0] : values.ToArray());
        }
        var topLevel = result.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if ((topLevel.Contains("system:has audio") && topLevel.Contains("system:no audio")) ||
            (topLevel.Contains("system:inbox") && topLevel.Contains("system:archive")))
            throw new NotSupportedException("Conflicting system predicates cannot be combined safely.");
        if (result.Count == 0) result.Add("system:everything");
        return result;
    }

    private Node Expression(int minimumPrecedence)
    {
        if (++_depth > 128) throw Invalid();
        try
        {
            var left = Unary();
            while (true)
            {
                var saved = _position;
                var operation = ReadOperator();
                var precedence = operation switch
                {
                    "and" => 6,
                    "or" => 5,
                    "nand" or "nor" => 4,
                    "xor" => 3,
                    "implies" => 2,
                    "xnor" or "iff" => 1,
                    _ => -1
                };
                if (precedence < minimumPrecedence)
                {
                    _position = saved;
                    break;
                }
                var right = Expression(precedence + 1);
                left = new Node(operation!, null, left, right);
            }
            return left;
        }
        finally { _depth--; }
    }

    private Node Unary()
    {
        SkipWhitespace();
        if (_position >= _query.Length) throw Invalid();
        if (_query[_position] is '!' or '-')
        {
            _position++;
            return new Node("not", null, Expression(7), null);
        }
        if (WordAt("not"))
        {
            _position += 3;
            return new Node("not", null, Expression(7), null);
        }
        if (_query[_position] == '(')
        {
            _position++;
            var child = Expression(0);
            SkipWhitespace();
            if (_position >= _query.Length || _query[_position++] != ')') throw Invalid();
            return child;
        }
        var text = new StringBuilder();
        while (_position < _query.Length)
        {
            var character = _query[_position];
            if (character == '\\')
            {
                if (++_position == _query.Length) throw Invalid();
                character = _query[_position++];
                if (character == '*' || (text.Length == 0 && character == '-'))
                    throw new NotSupportedException("This escaped tag cannot be represented by the Hydrus API.");
                text.Append(character);
                continue;
            }
            if (character is '(' or ')') break;
            if (character is '&' or '|' && !Peek("&&") && !Peek("||")) throw Invalid();
            if (WordAt("not") && text.Length > 0 && !text.ToString().TrimStart().StartsWith("system:", StringComparison.Ordinal)) throw Invalid();
            var saved = _position;
            if (ReadOperator() is not null) { _position = saved; break; }
            _position = saved;
            text.Append(character);
            _position++;
        }
        var value = text.ToString().Trim();
        if (value.Length == 0 || value.EndsWith(':') || value.StartsWith('-') || value.Any(char.IsControl)) throw Invalid();
        if (value.StartsWith("system:", StringComparison.OrdinalIgnoreCase))
        {
            if (!value.StartsWith("system:", StringComparison.Ordinal)) throw Invalid();
            var system = value[7..];
            const string comparison = @"(?:<=|>=|!=|==|=|<|>|~=)";
            const string number = @"(?:0|[1-9][0-9]{0,8})";
            const string duration = @"(?:(?:0|[1-9][0-9]{0,8}) *(?:hours|hour|hr|h) *)?(?:(?:0|[1-9][0-9]{0,8}) *(?:minutes|minute|mins) *)?(?:(?:0|[1-9][0-9]{0,8}) *(?:seconds|second|secs|sec|s) *)?(?:(?:0|[1-9][0-9]{0,8}) *(?:milliseconds|millisecond|msecs|msec|ms))?";
            var supported = Regex.IsMatch(system, @"\A(?:inbox|archive|untagged|has tags|has audio|no audio|has duration|no duration)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                Regex.IsMatch(system, $@"\A(?:width|height) *{comparison} *{number}(?: *(?:px|pixels?))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                Regex.IsMatch(system, $@"\Aduration *{comparison} *(?=[0-9]){duration}\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!supported)
                throw new NotSupportedException("Unsupported Hydrus system predicate or syntax in feed query.");
        }
        if (++_literals > MaximumLiterals) throw new NotSupportedException("Hydrus feed expression is too complex.");
        return new Node("literal", value, null, null);
    }

    private string? ReadOperator()
    {
        SkipWhitespace();
        foreach (var (token, operation) in new[] { ("<=>", "iff"), ("=>", "implies"), ("&&", "and"), ("||", "or") })
        {
            if (Peek(token)) { _position += token.Length; return operation; }
        }
        foreach (var word in new[] { "implies", "xnor", "nand", "xor", "iff", "and", "nor", "or" })
        {
            if (WordAt(word)) { _position += word.Length; return word; }
        }
        return null;
    }

    private bool Peek(string text) => _query.AsSpan(_position).StartsWith(text, StringComparison.Ordinal);
    private bool WordAt(string word) => _query.AsSpan(_position).StartsWith(word, StringComparison.OrdinalIgnoreCase) &&
        (_position == 0 || char.IsWhiteSpace(_query[_position - 1]) || _query[_position - 1] is '(' or ')') &&
        (_position + word.Length == _query.Length || char.IsWhiteSpace(_query[_position + word.Length]) || _query[_position + word.Length] == '(');
    private void SkipWhitespace() { while (_position < _query.Length && char.IsWhiteSpace(_query[_position])) _position++; }
    private static ArgumentException Invalid() => new("Invalid Hydrus feed query.", "query");

    private static List<List<Literal>> ToCnf(Node node, bool negated, ref int steps)
    {
        if (++steps > MaximumConversionSteps) throw new NotSupportedException("Hydrus feed expression is too complex.");
        if (node.Operation == "literal")
        {
            var text = node.Text!;
            if (negated && text.Equals("system:inbox", StringComparison.OrdinalIgnoreCase)) return [[new("system:archive", false)]];
            if (negated && text.Equals("system:archive", StringComparison.OrdinalIgnoreCase)) return [[new("system:inbox", false)]];
            if (negated && text.Equals("system:has audio", StringComparison.OrdinalIgnoreCase)) return [[new("system:no audio", false)]];
            if (negated && text.Equals("system:no audio", StringComparison.OrdinalIgnoreCase)) return [[new("system:has audio", false)]];
            return [[new(text, negated)]];
        }
        if (node.Operation == "not") return ToCnf(node.Left!, !negated, ref steps);
        if (node.Operation is "xor" or "xnor" or "iff")
        {
            var equal = (node.Operation is "xnor" or "iff") ^ negated;
            return And(Or(ToCnf(node.Left!, false, ref steps), ToCnf(node.Right!, equal, ref steps)),
                Or(ToCnf(node.Left!, true, ref steps), ToCnf(node.Right!, !equal, ref steps)));
        }
        if (node.Operation == "implies")
            return negated ? And(ToCnf(node.Left!, false, ref steps), ToCnf(node.Right!, true, ref steps)) :
                Or(ToCnf(node.Left!, true, ref steps), ToCnf(node.Right!, false, ref steps));
        var invert = negated ^ (node.Operation is "nand" or "nor");
        var conjunction = (node.Operation is "and" or "nand") ^ invert;
        var left = ToCnf(node.Left!, invert, ref steps);
        var right = ToCnf(node.Right!, invert, ref steps);
        return conjunction ? And(left, right) : Or(left, right);
    }

    private static List<List<Literal>> And(List<List<Literal>> left, List<List<Literal>> right)
    {
        if ((long)left.Count + right.Count > MaximumClauses) throw new NotSupportedException("Hydrus feed expression is too complex.");
        return [.. left, .. right];
    }

    private static List<List<Literal>> Or(List<List<Literal>> left, List<List<Literal>> right)
    {
        if ((long)left.Count * right.Count > MaximumClauses) throw new NotSupportedException("Hydrus feed expression is too complex.");
        var result = new List<List<Literal>>();
        foreach (var leftClause in left)
            foreach (var rightClause in right)
            {
                if (leftClause.Count + rightClause.Count > MaximumClauses) throw new NotSupportedException("Hydrus feed expression is too complex.");
                result.Add([.. leftClause, .. rightClause]);
            }
        return result;
    }

    private sealed record Node(string Operation, string? Text, Node? Left, Node? Right);
    private sealed record Literal(string Text, bool Negated);
}