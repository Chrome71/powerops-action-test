using System.Text.Encodings.Web;
using System.Text.Json;

using PowerOps.Core.Model;

namespace PowerOps.Core.Settings;

public sealed record SettingsProblem(string Kind, string ComponentType, string Name, string Message);

/// <summary>The pac deployment settings file (deploymentsettings.json): generating an empty one from a solution and checking a filled one against it.</summary>
public static class DeploymentSettings
{
    public const string EnvironmentVariable = "environmentVariable";
    public const string ConnectionReference = "connectionReference";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The settings file for <paramref name="solution"/> with empty values to fill in; names sorted ordinally, "\n" line endings, trailing newline.</summary>
    public static string Generate(Solution solution) =>
        JsonSerializer.Serialize(
            new
            {
                EnvironmentVariables = solution.EnvironmentVariables
                    .OrderBy(e => e.SchemaName, StringComparer.Ordinal)
                    .Select(e => new { e.SchemaName, Value = "" }).ToList(),
                ConnectionReferences = solution.ConnectionReferences
                    .OrderBy(c => c.LogicalName, StringComparer.Ordinal)
                    .Select(c => new { c.LogicalName, ConnectionId = "", ConnectorId = c.ConnectorId ?? "" }).ToList(),
            },
            WriteOptions) + "\n";

    /// <summary>Compares a settings file with the solution. Unknown fields and arrays in the file are ignored; a name listed more than once is a <c>duplicate</c> problem, and its first entry is the one checked. Problems are sorted by component type, name, kind.</summary>
    /// <exception cref="InvalidDataException">The text is not JSON or has the wrong shape; the message is the reason, without a path.</exception>
    public static IReadOnlyList<SettingsProblem> Validate(Solution solution, string settingsJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(settingsJson);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"not valid JSON (line {e.LineNumber}, position {e.BytePositionInLine})", e);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("the root must be a JSON object");
            }

            var problems = new List<SettingsProblem>();
            var env = Entries(doc.RootElement, "EnvironmentVariables", "SchemaName", EnvironmentVariable, problems);
            var refs = Entries(doc.RootElement, "ConnectionReferences", "LogicalName", ConnectionReference, problems);

            // Observed with pac 2.13.1: import keeps a variable's default or current value when the file leaves it out, and rejects "Value": "".
            foreach (var e in solution.EnvironmentVariables)
            {
                var hasValue = !string.IsNullOrEmpty(e.DefaultValue) || !string.IsNullOrEmpty(e.CurrentValue);
                var required = e.IsRequired ? " (required)" : "";
                if (!env.TryGetValue(e.SchemaName, out var entry))
                {
                    if (!hasValue)
                    {
                        problems.Add(Missing(EnvironmentVariable, e.SchemaName));
                    }
                }
                else if (entry.TryGetProperty("Value", out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Length == 0)
                {
                    problems.Add(new("emptyValue", EnvironmentVariable, e.SchemaName, "has an empty Value, which pac solution import rejects; set a value"
                        + (hasValue ? ", or leave the entry out to keep its default or current value" : required)));
                }
                else if (Blank(entry, "Value") && !hasValue)
                {
                    problems.Add(new("emptyValue", EnvironmentVariable, e.SchemaName, "has no value and no default" + required));
                }
            }

            foreach (var c in solution.ConnectionReferences)
            {
                if (!refs.TryGetValue(c.LogicalName, out var entry))
                {
                    problems.Add(Missing(ConnectionReference, c.LogicalName));
                }
                else if (NoConnectionId(entry))
                {
                    problems.Add(new("emptyConnectionId", ConnectionReference, c.LogicalName, "has no ConnectionId"));
                }
            }

            problems.AddRange(env.Keys.Where(k => !solution.EnvironmentVariables.Any(e => Same(e.SchemaName, k))).Select(k => Extra(EnvironmentVariable, k)));
            problems.AddRange(refs.Keys.Where(k => !solution.ConnectionReferences.Any(c => Same(c.LogicalName, k))).Select(k => Extra(ConnectionReference, k)));

            return problems
                .OrderBy(p => p.ComponentType, StringComparer.Ordinal)
                .ThenBy(p => p.Name, StringComparer.Ordinal)
                .ThenBy(p => p.Kind, StringComparer.Ordinal)
                .ToList();
        }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static SettingsProblem Missing(string type, string name) => new("missing", type, name, "in the solution but not in the settings file");

    private static SettingsProblem Extra(string type, string name) => new("extra", type, name, "in the settings file but not in the solution");

    // A non-string Value (number, bool) still sets the variable; a ConnectionId must be a non-blank string.
    private static bool Blank(JsonElement entry, string property) =>
        !entry.TryGetProperty(property, out var v) || v.ValueKind == JsonValueKind.Null
        || (v.ValueKind == JsonValueKind.String && v.GetString()!.Length == 0);

    private static bool NoConnectionId(JsonElement entry) =>
        !entry.TryGetProperty("ConnectionId", out var v) || v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString());

    /// <summary>The objects of the array <paramref name="array"/> by <paramref name="nameProperty"/> (case-insensitive, first wins; one <c>duplicate</c> problem per repeated name goes to <paramref name="problems"/>); a missing array is empty. The elements are cloned, so they outlive the parsed document.</summary>
    private static Dictionary<string, JsonElement> Entries(JsonElement root, string array, string nameProperty, string type, List<SettingsProblem> problems)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty(array, out var items))
        {
            return result;
        }

        if (items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{array} must be an array");
        }

        var i = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{array}[{i}] must be an object");
            }

            if (!item.TryGetProperty(nameProperty, out var name) || name.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException($"{array}[{i}].{nameProperty} must be a string");
            }

            if (!result.TryAdd(name.GetString()!, item.Clone()) && duplicates.Add(name.GetString()!))
            {
                problems.Add(new("duplicate", type, result.Keys.First(k => Same(k, name.GetString()!)), "listed more than once in the settings file"));
            }
            i++;
        }

        return result;
    }
}