using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PowerOps.Core.Flows;

/// <summary>The 37 functions of the expression language that flow tests support, each written from its section of Learn's expression functions reference.</summary>
public static class ExpressionFunctions
{
    /// <summary>Learn's limit on the result of <c>concat</c>, applied to every string the simulator builds.</summary>
    internal const long MaxStringLength = 104_857_600;

    /// <summary>The bytes a recorded node is counted as, besides its text.</summary>
    internal const long NodeBytes = 64;

    /// <summary>The most a run may record (see <c>FlowSimulator</c>); a function that builds many nodes checks its result against it first.</summary>
    internal const long MaxRunBytes = 256L * 1024 * 1024;

    private delegate JsonNode? Function(JsonNode?[] args, IExpressionContext context);

    private static readonly Dictionary<string, Function> Table = new()
    {
        // Workflow
        ["outputs"] = (a, c) => c.Outputs(Str("outputs", Count("outputs", a, 1)[0])),
        ["body"] = (a, c) => Expression.Read(c.Outputs(Str("body", Count("body", a, 1)[0])), JsonValue.Create("body"), false),
        ["triggerOutputs"] = (a, c) => NoArguments("triggerOutputs", a, c).TriggerOutputs(),
        ["triggerBody"] = (a, c) => Expression.Read(NoArguments("triggerBody", a, c).TriggerOutputs(), JsonValue.Create("body"), false),
        ["variables"] = (a, c) => c.Variable(Str("variables", Count("variables", a, 1)[0])),
        ["parameters"] = (a, c) => c.Parameter(Str("parameters", Count("parameters", a, 1)[0])),
        ["items"] = (a, c) => c.Items(Str("items", Count("items", a, 1)[0])),
        ["item"] = (a, c) => NoArguments("item", a, c).Item(),
        ["workflow"] = (a, c) => NoArguments("workflow", a, c).Workflow(),
        ["result"] = (a, c) => c.Result(Str("result", Count("result", a, 1)[0])),

        // String
        ["concat"] = (a, _) => Concat(Count("concat", a, 2, int.MaxValue)),
        ["substring"] = (a, _) => Substring(Count("substring", a, 2, 3)),
        ["replace"] = (a, _) => Replace(Count("replace", a, 3)),
        ["split"] = (a, _) => Split(Count("split", a, 2)),
        ["toLower"] = (a, _) => JsonValue.Create(Str("toLower", Count("toLower", a, 1)[0]).ToLowerInvariant()),
        ["trim"] = (a, _) => JsonValue.Create(Str("trim", Count("trim", a, 1)[0]).Trim()),
        ["guid"] = (a, c) => a.Length > 0 ? throw new UnsupportedException("guid() with a format argument is not supported.") : JsonValue.Create(c.Guid()),

        // Collection
        ["contains"] = (a, _) => Contains(Count("contains", a, 2)),
        ["empty"] = (a, _) => Empty(Count("empty", a, 1)[0]),
        ["first"] = (a, _) => Edge("first", Count("first", a, 1)[0], first: true),
        ["last"] = (a, _) => Edge("last", Count("last", a, 1)[0], first: false),
        ["length"] = (a, _) => Length(Count("length", a, 1)[0]),

        // Logical
        ["equals"] = (a, _) => JsonValue.Create(Count("equals", a, 2) is [var x, var y] && Equal(x, y)),
        ["greater"] = (a, _) => JsonValue.Create(Compare("greater", Count("greater", a, 2)) > 0),
        ["greaterOrEquals"] = (a, _) => JsonValue.Create(Compare("greaterOrEquals", Count("greaterOrEquals", a, 2)) >= 0),
        ["lessOrEquals"] = (a, _) => JsonValue.Create(Compare("lessOrEquals", Count("lessOrEquals", a, 2)) <= 0),
        ["if"] = (a, _) => Count("if", a, 3) is [var test, var yes, var no] ? (Bool("if", test) ? yes : no) : null,
        ["and"] = (a, _) => JsonValue.Create(Bools("and", a).All(b => b)),
        ["or"] = (a, _) => JsonValue.Create(Bools("or", a).Any(b => b)),
        ["not"] = (a, _) => JsonValue.Create(!Bool("not", Count("not", a, 1)[0])),

        // Conversion
        ["json"] = (a, _) => Json(Count("json", a, 1)[0]),
        ["string"] = (a, _) => Concat(Count("string", a, 1)),
        ["decodeBase64"] = (a, _) => DecodeBase64(Str("decodeBase64", Count("decodeBase64", a, 1)[0])),
        ["createArray"] = (a, _) => new JsonArray(Count("createArray", a, 1, int.MaxValue).Select(x => x?.DeepClone()).ToArray()),

        // Math
        ["min"] = (a, _) => Min(a),

        // Date and time
        ["utcNow"] = (a, c) => a.Length > 0 ? throw new UnsupportedException("utcNow() with a format argument is not supported.") : JsonValue.Create(Format(Timestamp("utcNow", c.UtcNow()))),
        ["addDays"] = (a, _) => AddDays(a),
    };

    /// <summary>The supported function names, sorted.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. Table.Keys.Order(StringComparer.Ordinal)];

    /// <exception cref="UnsupportedException">A string of <paramref name="length"/> characters is past <see cref="MaxStringLength"/>.</exception>
    internal static void CheckLength(long length)
    {
        if (length > MaxStringLength)
        {
            throw new UnsupportedException($"a string longer than {MaxStringLength} characters is not supported (Learn's limit on concat).");
        }
    }

    internal static bool IsKnown(string name) => Table.ContainsKey(name);

    /// <exception cref="UnsupportedException">The function isn't one of <see cref="Names"/>.</exception>
    internal static JsonNode? Invoke(string name, JsonNode?[] args, IExpressionContext context) =>
        Table.TryGetValue(name, out var function) ? function(args, context) : throw new UnsupportedException($"Unsupported function '{name}'.");

    internal static string Describe(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "an object",
        JsonArray => "an array",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "a string",
            JsonValueKind.Number => "a number",
            _ => "a boolean",
        },
        _ => "a value",
    };

    /// <summary>Parses JSON text and rejects a duplicate property at once: a JsonObject would find it only when first read, as an ArgumentException.</summary>
    /// <exception cref="JsonException">The text is not valid JSON, is nested deeper than 64 levels or has a duplicate property.</exception>
    internal static JsonNode? ParseJson(string text) => JsonNode.Parse(text, null, new JsonDocumentOptions { AllowDuplicateProperties = false });

    internal static bool TryLong(JsonNode? node, out long value)
    {
        value = 0;
        return node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && long.TryParse(v.ToJsonString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    private static JsonNode?[] Count(string function, JsonNode?[] args, int min, int max)
    {
        if (args.Length < min || args.Length > max)
        {
            var expected = max == int.MaxValue ? $"at least {min}" : min == max ? $"{min}" : $"{min} to {max}";
            throw new ExpressionException($"{function}() takes {expected} argument(s), got {args.Length}.");
        }

        return args;
    }

    private static IExpressionContext NoArguments(string function, JsonNode?[] args, IExpressionContext context)
    {
        Count(function, args, 0);
        return context;
    }

    private static JsonNode?[] Count(string function, JsonNode?[] args, int count) => Count(function, args, count, count);

    private static bool IsString(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String;

    private static bool IsNumber(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number;

    private static string Str(string function, JsonNode? node) =>
        IsString(node) ? node!.GetValue<string>() : throw new ExpressionException($"{function}() expects a string, got {Describe(node)}.");

    private static long Int(string function, JsonNode? node) =>
        TryLong(node, out var value) ? value : throw new ExpressionException($"{function}() expects an integer, got {Describe(node)}.");

    private static bool Bool(string function, JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? v.GetValueKind() == JsonValueKind.True
            : throw new ExpressionException($"{function}() expects a boolean, got {Describe(node)}.");

    private static bool[] Bools(string function, JsonNode?[] args) => [.. Count(function, args, 1, int.MaxValue).Select(a => Bool(function, a))];

    private static double Double(JsonNode node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.True ? 1
        : node is JsonValue f && f.GetValueKind() == JsonValueKind.False ? 0
        : double.Parse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static int CompareNumbers(JsonNode x, JsonNode y) =>
        TryLong(x, out var lx) && TryLong(y, out var ly) ? lx.CompareTo(ly) : Double(x).CompareTo(Double(y));

    /// <summary>Learn: null compares as an empty string; strings are compared ordinally (not on Learn, plan "Not on Learn"); a boolean equals 1 and 0.</summary>
    private static bool Equal(JsonNode? x, JsonNode? y)
    {
        x ??= "";
        y ??= "";
        if (IsString(x) && IsString(y))
        {
            return x.GetValue<string>() == y.GetValue<string>();
        }

        var xNumeric = IsNumber(x) || x.GetValueKind() is JsonValueKind.True or JsonValueKind.False;
        var yNumeric = IsNumber(y) || y.GetValueKind() is JsonValueKind.True or JsonValueKind.False;
        if (xNumeric && yNumeric)
        {
            return CompareNumbers(x, y) == 0;
        }

        return (x is JsonObject && y is JsonObject || x is JsonArray && y is JsonArray) && JsonNode.DeepEquals(x, y);
    }

    private static int Compare(string function, JsonNode?[] args)
    {
        var x = args[0] ?? "";
        var y = args[1] ?? "";
        if (IsString(x) && IsString(y))
        {
            return string.CompareOrdinal(x.GetValue<string>(), y.GetValue<string>());
        }

        return IsNumber(x) && IsNumber(y)
            ? CompareNumbers(x, y)
            : throw new ExpressionException($"{function}() cannot compare {Describe(x)} with {Describe(y)}.");
    }

    private static JsonNode? Substring(JsonNode?[] a)
    {
        var text = Str("substring", a[0]);
        var start = Int("substring", a[1]);
        var length = a.Length == 3 ? Int("substring", a[2]) : text.Length - start;
        if (start < 0 || length < 0 || start > text.Length || length > text.Length - start)
        {
            // Learn: a start and length that reach past the end are an error, not a shorter result
            throw new ExpressionException($"substring() cannot take {length} characters from index {start} of a string of {text.Length}.");
        }

        return JsonValue.Create(text.Substring((int)start, (int)length));
    }

    private static JsonNode? Concat(JsonNode?[] a)
    {
        var parts = a.Select(Expression.ToText).ToArray();
        CheckLength(parts.Sum(p => (long)p.Length));
        return JsonValue.Create(string.Concat(parts));
    }

    // Non-overlapping, left to right, as string.Replace and string.Split find them.
    private static long CountMatches(string text, string find)
    {
        long count = 0;
        for (var at = text.IndexOf(find, StringComparison.Ordinal); at >= 0; at = text.IndexOf(find, at + find.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static JsonNode? Replace(JsonNode?[] a)
    {
        var oldText = Str("replace", a[1]);
        if (oldText.Length == 0)
        {
            throw new ExpressionException("replace() cannot replace an empty string.");
        }

        var text = Str("replace", a[0]);
        var newText = Str("replace", a[2]);
        CheckLength(text.Length + (CountMatches(text, oldText) * (newText.Length - oldText.Length)));
        return JsonValue.Create(text.Replace(oldText, newText, StringComparison.Ordinal));
    }

    private static JsonNode? Split(JsonNode?[] a)
    {
        var text = Str("split", a[0]);
        var separator = Str("split", a[1]);
        if (separator.Length > 0 && (CountMatches(text, separator) + 1) * NodeBytes > MaxRunBytes)
        {
            throw new UnsupportedException("split() would give more parts than a run can record.");
        }

        return new JsonArray(text.Split(separator, StringSplitOptions.None).Select(part => (JsonNode?)JsonValue.Create(part)).ToArray());
    }

    private static JsonNode? Contains(JsonNode?[] a) => a[0] switch
    {
        JsonObject obj => JsonValue.Create(obj.ContainsKey(Str("contains", a[1]))),
        JsonArray arr => JsonValue.Create(arr.Any(item => Equal(item, a[1]))),
        var other when IsString(other) => JsonValue.Create(other!.GetValue<string>().Contains(Str("contains", a[1]), StringComparison.Ordinal)),
        var other => throw new ExpressionException($"contains() expects a string, an array or an object, got {Describe(other)}."),
    };

    // not on Learn, plan "Not on Learn": null counts as empty, like the empty string it converts to in comparisons
    private static JsonNode? Empty(JsonNode? a) => a switch
    {
        null => JsonValue.Create(true),
        JsonObject obj => JsonValue.Create(obj.Count == 0),
        JsonArray arr => JsonValue.Create(arr.Count == 0),
        _ when IsString(a) => JsonValue.Create(a.GetValue<string>().Length == 0),
        _ => throw new ExpressionException($"empty() expects a string, an array or an object, got {Describe(a)}."),
    };

    // not on Learn, plan "Not on Learn": first and last of an empty collection are null
    private static JsonNode? Edge(string function, JsonNode? a, bool first) => a switch
    {
        JsonArray arr => arr.Count == 0 ? null : arr[first ? 0 : arr.Count - 1],
        _ when IsString(a) => a!.GetValue<string>() is { Length: > 0 } s ? JsonValue.Create(s[first ? 0 : s.Length - 1].ToString()) : null,
        _ => throw new ExpressionException($"{function}() expects a string or an array, got {Describe(a)}."),
    };

    private static JsonNode? Length(JsonNode? a) => a switch
    {
        JsonArray arr => JsonValue.Create(arr.Count),
        _ when IsString(a) => JsonValue.Create(a!.GetValue<string>().Length),
        _ => throw new ExpressionException($"length() expects a string or an array, got {Describe(a)}."),
    };

    private static JsonNode? Json(JsonNode? a)
    {
        if (a is null)
        {
            return new JsonObject(); // Learn: a null string gives an empty object
        }

        try
        {
            return ParseJson(Str("json", a));
        }
        catch (JsonException e)
        {
            throw new ExpressionException($"json() cannot read its argument: {e.Message}");
        }
    }

    private static JsonNode? DecodeBase64(string value)
    {
        try
        {
            return JsonValue.Create(new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value)));
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            throw new ExpressionException($"decodeBase64() cannot decode its argument: {e.Message}");
        }
    }

    private static JsonNode? Min(JsonNode?[] a)
    {
        var numbers = a is [JsonArray array] ? array.ToArray() : Count("min", a, 2, int.MaxValue);
        if (numbers.Length == 0 || !numbers.All(IsNumber))
        {
            throw new ExpressionException("min() takes an array of numbers, or two or more numbers.");
        }

        return numbers.Aggregate((lowest, next) => CompareNumbers(next!, lowest!) < 0 ? next : lowest);
    }

    private static DateTime Timestamp(string function, string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.UtcDateTime
            : throw new ExpressionException($"{function}() cannot read '{text}' as a timestamp.");

    /// <summary>Learn's default format, "o": <c>2018-03-25T00:00:00.0000000Z</c> for UTC.</summary>
    private static string Format(DateTime utc) => utc.ToString("o", CultureInfo.InvariantCulture);

    private static JsonNode? AddDays(JsonNode?[] a)
    {
        if (Count("addDays", a, 2, 3).Length == 3)
        {
            throw new UnsupportedException("addDays() with a format argument is not supported.");
        }

        var start = Timestamp("addDays", Str("addDays", a[0]));
        try
        {
            return JsonValue.Create(Format(start.AddDays(Int("addDays", a[1]))));
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ExpressionException("addDays() gives a date outside the supported range.");
        }
    }
}