using System.Text.Encodings.Web;
using System.Text.Json;

namespace PowerOps.Core.Output;

/// <summary>Findings accepted as known. The key is (solution unique name, rule id, component, message); the file is left out so a baseline built from a zip also matches the unpacked folder.</summary>
public sealed class Baseline
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HashSet<Entry> _entries;

    private Baseline(HashSet<Entry> entries) => _entries = entries;

    /// <summary>Reads a baseline file; anything that is not a version 1 baseline throws <see cref="InvalidDataException"/>.</summary>
    public static Baseline Load(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != CurrentVersion)
            {
                throw new InvalidDataException($"{path} is not a PowerOps baseline (expected \"version\": {CurrentVersion}).");
            }

            if (!root.TryGetProperty("findings", out var findings) || findings.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"{path}: \"findings\" must be an array.");
            }

            var entries = new HashSet<Entry>();
            foreach (var f in findings.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException($"{path}: every finding must be an object.");
                }

                // "component" may be null or absent: some findings have none.
                entries.Add(new Entry(Str(f, "solution", path)!, Str(f, "ruleId", path)!, Str(f, "component", path, required: false), Str(f, "message", path)!));
            }

            return new Baseline(entries);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} is not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>Writes every finding of <paramref name="result"/> as a baseline (sorted, duplicates collapsed, "\n" line endings, trailing newline).</summary>
    public static void Write(AnalysisResult result, TextWriter writer)
    {
        var findings = result.Solutions
            .SelectMany(s => s.Findings.Select(f => new Entry(s.Solution.UniqueName, f.RuleId, f.Component, f.Message)))
            .Distinct()
            .OrderBy(e => e.Solution, StringComparer.Ordinal)
            .ThenBy(e => e.RuleId, StringComparer.Ordinal)
            .ThenBy(e => e.Component, StringComparer.Ordinal)
            .ThenBy(e => e.Message, StringComparer.Ordinal)
            .ToList();
        writer.Write(JsonSerializer.Serialize(new { version = CurrentVersion, findings }, Options) + "\n");
    }

    /// <summary>Returns <paramref name="result"/> without the baselined findings, counting them in <see cref="AnalysisResult.Baselined"/>.</summary>
    public AnalysisResult Apply(AnalysisResult result)
    {
        var dropped = 0;
        var solutions = result.Solutions.Select(s =>
        {
            var kept = s.Findings.Where(f => !_entries.Contains(new Entry(s.Solution.UniqueName, f.RuleId, f.Component, f.Message))).ToList();
            dropped += s.Findings.Count - kept.Count;
            return s with { Findings = kept };
        }).ToList();
        return new AnalysisResult(solutions) { Baselined = dropped };
    }

    private static string? Str(JsonElement obj, string name, string path, bool required = true)
    {
        if (obj.TryGetProperty(name, out var p))
        {
            if (p.ValueKind == JsonValueKind.String)
            {
                return p.GetString();
            }

            if (!required && p.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
        }
        else if (!required)
        {
            return null;
        }

        throw new InvalidDataException($"{path}: every finding needs a string \"{name}\".");
    }

    private sealed record Entry(string Solution, string RuleId, string? Component, string Message);
}