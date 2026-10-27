using System.Text.Encodings.Web;
using System.Text.Json;

using PowerOps.Core.Settings;

namespace PowerOps.Core.Output;

public static class SettingsReport
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>One line per problem, then a summary line, with "\n" line endings.</summary>
    public static void WriteText(IReadOnlyList<SettingsProblem> problems, TextWriter writer)
    {
        foreach (var p in problems)
        {
            writer.Write(Line(p));
        }

        writer.Write(Count(problems.Count) + "\n");
    }

    internal static string Line(SettingsProblem p) => $"{p.Kind} {p.ComponentType} {p.Name}: {p.Message}\n";

    internal static string Count(int n) => n switch { 0 => "no problems", 1 => "1 problem", _ => $"{n} problems" };

    /// <summary>Writes the problems as JSON (stable property order, "\n" line endings, trailing newline).</summary>
    public static void WriteJson(string solution, string settings, IReadOnlyList<SettingsProblem> problems, TextWriter writer) =>
        writer.Write(JsonSerializer.Serialize(new Doc(JsonOutput.SchemaVersion, solution, settings, problems), Options) + "\n");

    private sealed record Doc(int SchemaVersion, string Solution, string Settings, IReadOnlyList<SettingsProblem> Problems);
}