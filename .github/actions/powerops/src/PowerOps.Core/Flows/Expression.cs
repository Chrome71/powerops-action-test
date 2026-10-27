using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PowerOps.Core.Flows;

/// <summary>Evaluates the expression language of a cloud flow definition (the strings that start with <c>@</c> or hold <c>@{...}</c>), written from Microsoft Learn's Workflow Definition Language and expression functions pages.</summary>
public static class Expression
{
    private const int MaxDepth = 100;

    /// <summary>Evaluates a string value of a definition: <c>@expr</c> gives the value with its own type, anything else is text with <c>@@</c> and <c>@{expr}</c> applied.</summary>
    /// <exception cref="UnsupportedException">The text can't be read or names a function that isn't supported.</exception>
    /// <exception cref="ExpressionException">The expression failed while it ran.</exception>
    public static JsonNode? Evaluate(string text, IExpressionContext context)
    {
        if (text.StartsWith('@') && !text.StartsWith("@@", StringComparison.Ordinal) && !text.StartsWith("@{", StringComparison.Ordinal))
        {
            return Run(text[1..], context)?.DeepClone();
        }

        if (!text.Contains('@'))
        {
            return JsonValue.Create(text);
        }

        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '@' || i + 1 == text.Length)
            {
                sb.Append(text[i]);
            }
            else if (text[i + 1] == '@')
            {
                sb.Append('@');
                i++;
            }
            else if (text[i + 1] == '{')
            {
                var end = FindClosingBrace(text, i + 2);
                if (end < 0)
                {
                    throw new UnsupportedException($"Cannot read '{text}': '@{{' is never closed.");
                }

                var part = ToText(Run(text[(i + 2)..end], context));
                ExpressionFunctions.CheckLength((long)sb.Length + part.Length);
                sb.Append(part);
                i = end;
            }
            else
            {
                sb.Append('@');
            }
        }

        return JsonValue.Create(sb.ToString());
    }

    /// <summary>Evaluates every string inside a JSON value (object keys are not evaluated) and returns new nodes.</summary>
    public static JsonNode? EvaluateValue(JsonNode? value, IExpressionContext context)
    {
        switch (value)
        {
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (key, item) in obj)
                {
                    copy[key] = EvaluateValue(item, context);
                }

                return copy;
            case JsonArray arr:
                return new JsonArray(arr.Select(item => EvaluateValue(item, context)).ToArray());
            default:
                return IsString(value) ? Evaluate(value!.GetValue<string>(), context) : value?.DeepClone();
        }
    }

    /// <summary>Evaluates an If or Until condition: a string is an expression, <c>{"fn": args}</c> calls <c>fn</c> (an array is its arguments, any other value one argument), other values are literals.</summary>
    public static JsonNode? EvaluateCondition(JsonNode? condition, IExpressionContext context)
    {
        if (condition is JsonObject obj)
        {
            if (obj.Count != 1)
            {
                throw new ExpressionException($"A condition object must have exactly one property, the function; got {obj.Count}.");
            }

            var (name, value) = obj.Single();
            var args = (value is JsonArray arr ? arr.ToArray() : [value]).Select(arg => EvaluateArgument(arg, context)).ToArray();
            return ExpressionFunctions.Invoke(name, args, context)?.DeepClone();
        }

        return IsString(condition) ? Evaluate(condition!.GetValue<string>(), context) : condition?.DeepClone();
    }

    // An argument may also be an array or an object of several properties: both are literals whose strings and function objects are evaluated.
    private static JsonNode? EvaluateArgument(JsonNode? argument, IExpressionContext context) => argument switch
    {
        JsonArray arr => new JsonArray(arr.Select(item => EvaluateArgument(item, context)).ToArray()),
        JsonObject { Count: not 1 } obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, EvaluateArgument(p.Value, context)))),
        _ => EvaluateCondition(argument, context),
    };

    /// <summary>The text form used by <c>@{...}</c>, <c>string()</c> and <c>concat()</c>, always in the invariant culture.</summary>
    internal static string ToText(JsonNode? value) => value switch
    {
        null => "",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            _ => v.ToJsonString(),
        },
        _ => value.ToJsonString(),
    };

    /// <summary>Reads a property (string key) or an item (integer key); <paramref name="optional"/> is the <c>?</c> operator.</summary>
    internal static JsonNode? Read(JsonNode? target, JsonNode? key, bool optional)
    {
        if (target is null)
        {
            return optional ? null : throw new ExpressionException("Cannot read a property or item of null.");
        }

        if (IsString(key))
        {
            var name = key!.GetValue<string>();
            if (target is not JsonObject obj)
            {
                throw new ExpressionException($"Cannot read property '{name}' of {ExpressionFunctions.Describe(target)}.");
            }

            if (obj.TryGetPropertyValue(name, out var value))
            {
                return value;
            }

            if (name.Contains('/'))
            {
                // observed in exported flows, not on Learn: 'body/value' reads body, then value
                var node = target;
                foreach (var segment in name.Split('/'))
                {
                    node = Read(node, JsonValue.Create(segment), optional);
                }

                return node;
            }

            return optional ? null : throw new ExpressionException($"The property '{name}' does not exist.");
        }

        if (ExpressionFunctions.TryLong(key, out var index))
        {
            if (target is not JsonArray arr)
            {
                throw new ExpressionException($"Cannot read item {index} of {ExpressionFunctions.Describe(target)}.");
            }

            return index >= 0 && index < arr.Count ? arr[(int)index] : optional ? null : throw new ExpressionException($"The index {index} is outside the array of {arr.Count} items.");
        }

        throw new ExpressionException($"A property name must be a string and an item index an integer, not {ExpressionFunctions.Describe(key)}.");
    }

    private static bool IsString(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String;

    private static JsonNode? Run(string expression, IExpressionContext context) => Eval(new Parser(expression).Parse(), context);

    private static JsonNode? Eval(Node node, IExpressionContext context) => node switch
    {
        Literal l => l.Value,
        Call c => ExpressionFunctions.Invoke(c.Name, c.Args.Select(a => Eval(a, context)).ToArray(), context),
        Access a => Read(Eval(a.Target, context), Eval(a.Key, context), a.Optional),
        _ => throw new InvalidOperationException(),
    };

    /// <summary>The index of the <c>}</c> that closes an <c>@{</c> whose content starts at <paramref name="from"/>, skipping single-quoted literals; -1 when there is none.</summary>
    private static int FindClosingBrace(string text, int from)
    {
        for (var i = from; i < text.Length; i++)
        {
            if (text[i] == '}')
            {
                return i;
            }

            if (text[i] == '\'')
            {
                for (i++; i < text.Length; i++)
                {
                    if (text[i] != '\'')
                    {
                        continue;
                    }

                    if (i + 1 < text.Length && text[i + 1] == '\'')
                    {
                        i++;
                        continue;
                    }

                    break;
                }
            }
        }

        return -1;
    }

    private abstract record Node;

    private sealed record Literal(JsonNode? Value) : Node;

    private sealed record Call(string Name, Node[] Args) : Node;

    private sealed record Access(Node Target, Node Key, bool Optional) : Node;

    /// <summary>Recursive descent over: literal or call, then any chain of <c>.name</c>, <c>['name']</c>, <c>[expr]</c>, each optionally after <c>?</c>.</summary>
    private sealed class Parser(string text)
    {
        private int _pos;

        public Node Parse()
        {
            var node = ParseExpression(0);
            SkipSpace();
            return _pos < text.Length ? throw Bad($"unexpected '{text[_pos]}'") : node;
        }

        private UnsupportedException Bad(string why) => new($"Cannot read expression '{text}': {why}.");

        private char Peek() => _pos < text.Length ? text[_pos] : '\0';

        private bool PeekDigitAt(int offset) => _pos + offset < text.Length && char.IsAsciiDigit(text[_pos + offset]);

        private void SkipSpace()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
            {
                _pos++;
            }
        }

        /// <summary><paramref name="depth"/> is the height of the tree above this expression; calls and accessors add to it.</summary>
        private Node ParseExpression(int depth)
        {
            SkipSpace();
            var height = depth;
            var c = Peek();
            Node node;
            if (c == '\'')
            {
                node = new Literal(JsonValue.Create(ReadString()));
            }
            else if (char.IsAsciiDigit(c) || ((c == '-' || c == '.') && PeekDigitAt(1)))
            {
                node = new Literal(ReadNumber());
            }
            else if (char.IsAsciiLetter(c))
            {
                var name = ReadName();
                SkipSpace();
                if (Peek() == '(')
                {
                    if (!ExpressionFunctions.IsKnown(name))
                    {
                        throw new UnsupportedException($"Unsupported function '{name}' in expression '{text}'.");
                    }

                    height = Deeper(height);
                    _pos++;
                    node = new Call(name, ReadArguments(height));
                }
                else
                {
                    node = name switch
                    {
                        "true" => new Literal(JsonValue.Create(true)),
                        "false" => new Literal(JsonValue.Create(false)),
                        "null" => new Literal(null),
                        _ => throw Bad($"unexpected '{name}'"),
                    };
                }
            }
            else
            {
                throw Bad(_pos < text.Length ? $"unexpected '{c}'" : "unexpected end");
            }

            while (true)
            {
                SkipSpace();
                var optional = Peek() == '?';
                if (optional)
                {
                    _pos++;
                    SkipSpace();
                }

                if (Peek() == '.')
                {
                    _pos++;
                    SkipSpace();
                    if (!char.IsAsciiLetter(Peek()))
                    {
                        throw Bad("a property name must follow '.'");
                    }

                    height = Deeper(height);
                    node = new Access(node, new Literal(JsonValue.Create(ReadName())), optional);
                }
                else if (Peek() == '[')
                {
                    height = Deeper(height);
                    _pos++;
                    var key = ParseExpression(height);
                    SkipSpace();
                    if (Peek() != ']')
                    {
                        throw Bad("expected ']'");
                    }

                    _pos++;
                    node = new Access(node, key, optional);
                }
                else if (optional)
                {
                    throw Bad("'?' must be followed by '.' or '['");
                }
                else
                {
                    return node;
                }
            }
        }

        private int Deeper(int height) => height < MaxDepth ? height + 1 : throw Bad($"it is nested deeper than {MaxDepth}");

        private Node[] ReadArguments(int height)
        {
            var args = new List<Node>();
            SkipSpace();
            if (Peek() == ')')
            {
                _pos++;
                return [];
            }

            while (true)
            {
                args.Add(ParseExpression(height));
                SkipSpace();
                var c = Peek();
                _pos++;
                if (c == ')')
                {
                    return [.. args];
                }

                if (c != ',')
                {
                    throw Bad("expected ',' or ')'");
                }
            }
        }

        private string ReadName()
        {
            var start = _pos;
            while (_pos < text.Length && (char.IsAsciiLetterOrDigit(text[_pos]) || text[_pos] == '_'))
            {
                _pos++;
            }

            return text[start.._pos];
        }

        private string ReadString()
        {
            var sb = new StringBuilder();
            _pos++;
            while (_pos < text.Length)
            {
                var c = text[_pos++];
                if (c != '\'')
                {
                    sb.Append(c);
                }
                else if (Peek() == '\'')
                {
                    sb.Append('\'');
                    _pos++;
                }
                else
                {
                    return sb.ToString();
                }
            }

            throw Bad("a string is never closed");
        }

        private JsonNode ReadNumber()
        {
            var start = _pos;
            if (Peek() == '-')
            {
                _pos++;
            }

            SkipDigits();
            var isDecimal = Peek() == '.' && PeekDigitAt(1);
            if (isDecimal)
            {
                _pos++;
                SkipDigits();
            }

            var number = text[start.._pos];
            if (!isDecimal && long.TryParse(number, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
            {
                return JsonValue.Create(l);
            }

            // a number past the double range is infinity, which JSON cannot write
            var d = double.Parse(number, NumberStyles.Float, CultureInfo.InvariantCulture);
            return double.IsFinite(d) ? JsonValue.Create(d) : throw Bad("the number is too large");
        }

        private void SkipDigits()
        {
            while (PeekDigitAt(0))
            {
                _pos++;
            }
        }
    }
}