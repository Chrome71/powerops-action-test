using System.Text.Json;
using System.Text.RegularExpressions;

using PowerOps.Core.Drift;
using PowerOps.Core.Output;

namespace PowerOps.Core.Deploy;

/// <summary>The fields of a plan file that <c>deploy apply</c> uses.</summary>
public sealed record ReviewedPlan(int SchemaVersion, string Tool, string Hash, string Target, bool Blocked, PlanInputs Inputs);

/// <summary>What reading a plan file gave: the plan, or (for a file of another schema version or powerops version) the refusal message and no plan.</summary>
public sealed record PlanFileRead(ReviewedPlan? Plan, string? Refusal);

/// <summary>Reads the plan file (M9's JSON report). The file is an artifact and so untrusted: no message of a rejection quotes it.</summary>
public static partial class DeployPlanFile
{
    private const int MaxToolText = 64;

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex HashPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9._-]+\z")]
    private static partial Regex SafeName();

    /// <summary>True for a zip or settings file name: letters, digits, dot, underscore and dash only (no trailing line break).</summary>
    public static bool IsSafeName(string name) => SafeName().IsMatch(name);

    /// <summary>Checks, in this order: valid JSON; <c>schemaVersion</c> and <c>tool</c> present; a refusal when either differs from this powerops'; then <c>hash</c>, <c>target</c>, <c>blocked</c> and <c>inputs</c>.</summary>
    /// <param name="tool">This powerops' version.</param>
    /// <exception cref="InvalidDataException">The text is not JSON, or a field is missing or wrong; the message names the field (or the line and position), never a value.</exception>
    public static PlanFileRead Read(string json, string tool)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"the plan file is not JSON (line {e.LineNumber}, position {e.BytePositionInLine})", e);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Bad("the file is not a JSON object");
            }

            var version = Prop(root, "schemaVersion", JsonValueKind.Number, "schemaVersion").TryGetInt32(out var v) ? v : throw Bad("schemaVersion is not an integer");
            var fileTool = Prop(root, "tool", JsonValueKind.String, "tool").GetString()!;
            if (version != JsonOutput.SchemaVersion || fileTool != tool)
            {
                var shown = TextReport.Clean(fileTool.Length > MaxToolText ? fileTool[..MaxToolText] : fileTool);
                return new PlanFileRead(null, $"the plan was made by powerops {shown}; this is {tool}; plan again with this version");
            }

            var hash = Hex(root, "hash", "hash");
            var target = Prop(root, "target", JsonValueKind.String, "target").GetString()!;
            if (target.Length == 0)
            {
                throw Bad("target is empty");
            }

            var blocked = root.TryGetProperty("blocked", out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? b.GetBoolean()
                : throw Bad("blocked is missing or has the wrong type");
            var inputs = Prop(root, "inputs", JsonValueKind.Object, "inputs");
            var zips = Items(inputs, "zips", "inputs.zips", (z, path) =>
            {
                var settings = Nullable(z, "settings", path) is { } s ? SafeText(s, $"{path}.settings") : null;
                var settingsHash = Nullable(z, "settingsSha256", path) is { } h ? Check(h, $"{path}.settingsSha256") : null;
                return settings is null == settingsHash is null
                    ? new ZipInput(
                        SafeText(Prop(z, "zip", JsonValueKind.String, $"{path}.zip").GetString()!, $"{path}.zip"),
                        Hex(z, "zipSha256", $"{path}.zipSha256"), settings, settingsHash, Hex(z, "snapshotSha256", $"{path}.snapshotSha256"))
                    : throw Bad($"{path}.settings and {path}.settingsSha256 are not both set or both null");
            });
            var dependencies = Items(inputs, "dependencies", "inputs.dependencies", (d, path) =>
            {
                var name = Prop(d, "name", JsonValueKind.String, $"{path}.name").GetString()!;
                return DriftQueries.IsValidName(name)
                    ? new DependencyInput(name, Nullable(d, "version", path))
                    : throw Bad($"{path}.name is not a solution unique name");
            });
            NoDuplicates(zips.Select(z => z.Zip), "inputs.zips[].zip");
            NoDuplicates(zips.Select(z => z.Settings).OfType<string>(), "inputs.zips[].settings");
            NoDuplicates(dependencies.Select(d => d.Name), "inputs.dependencies[].name");
            return new PlanFileRead(new ReviewedPlan(version, fileTool, hash, target, blocked, new PlanInputs(zips, dependencies)), null);
        }
    }

    private static void NoDuplicates(IEnumerable<string> names, string path)
    {
        if (names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            throw Bad($"{path} names the same file or solution twice");
        }
    }

    private static InvalidDataException Bad(string why) => new($"not a plan file: {why}");

    private static JsonElement Prop(JsonElement o, string name, JsonValueKind kind, string path) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var p) && p.ValueKind == kind ? p : throw Bad($"{path} is missing or has the wrong type");

    // A string, or null when the property is present and null.
    private static string? Nullable(JsonElement o, string name, string path)
    {
        if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return Prop(o, name, JsonValueKind.String, $"{path}.{name}").GetString();
    }

    private static string Hex(JsonElement o, string name, string path) => Check(Prop(o, name, JsonValueKind.String, path).GetString()!, path);

    private static string Check(string value, string path) => HashPattern().IsMatch(value) ? value : throw Bad($"{path} is not 64 lowercase hex characters");

    private static string SafeText(string value, string path) => IsSafeName(value) ? value : throw Bad($"{path} is not a plain file name");

    private static List<T> Items<T>(JsonElement o, string name, string path, Func<JsonElement, string, T> read)
    {
        var list = new List<T>();
        foreach (var item in Prop(o, name, JsonValueKind.Array, path).EnumerateArray())
        {
            list.Add(read(item, $"{path}[{list.Count}]"));
        }

        return list;
    }
}