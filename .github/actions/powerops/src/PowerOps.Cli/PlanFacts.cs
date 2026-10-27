using System.Text.Encodings.Web;
using System.Text.Json;

using PowerOps.Core.Output;

namespace PowerOps.Cli;

// Environment facts for the planning skills: names only, from two read-only pac commands.
public static class PlanFacts
{
    private static readonly string[] OrgWho = ["org", "who"];
    private static readonly string[] SolutionList = ["solution", "list"];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record Solution(string UniqueName, string Version, bool Managed);

    public static string Run(Func<IReadOnlyList<string>, PacResult> pac)
    {
        var who = pac(OrgWho);
        if (Failure(who, "pac org who") is { } whoFailure)
        {
            return whoFailure;
        }

        var environment = who.Stdout.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("Friendly Name:", StringComparison.Ordinal))
            .Select(l => l["Friendly Name:".Length..].Trim())
            .FirstOrDefault(n => n.Length > 0);
        if (environment is null)
        {
            return Unavailable("`pac org who` printed no environment name", SignInFix);
        }

        var list = pac(SolutionList);
        if (Failure(list, "pac solution list") is { } listFailure)
        {
            return listFailure;
        }

        var lines = list.Stdout.Split('\n').Select(l => l.Trim()).ToList();
        var header = lines.FindIndex(l => l.StartsWith("Unique Name", StringComparison.Ordinal));
        if (header < 0)
        {
            return Unavailable("`pac solution list` printed no solution table", SignInFix);
        }

        var solutions = new List<Solution>();
        foreach (var tokens in lines.Skip(header + 1).Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
        {
            // Friendly names contain spaces, so read from both ends instead of by column.
            if (tokens.Length >= 3 && bool.TryParse(tokens[^1], out var managed))
            {
                solutions.Add(new Solution(tokens[0], tokens[^2], managed));
            }
        }

        return Serialize(new { schemaVersion = JsonOutput.SchemaVersion, available = true, environment, solutions });
    }

    internal const string SignInFix = "Sign in with pac auth create, then pick the environment with pac org select.";

    private static string? Failure(PacResult result, string command) =>
        !result.Started
            ? Unavailable("pac was not found on PATH", "Install pac (dotnet tool install --global Microsoft.PowerApps.CLI.Tool) and add the dotnet tools folder to PATH.")
            : result.TimedOut
                ? Unavailable($"`{command}` timed out after 30 s", "Check that pac is signed in (pac auth list) and the network is reachable, then try again.")
                : result.ExitCode != 0
                    ? Unavailable($"`{command}` failed (exit code {result.ExitCode})", SignInFix)
                    : null;

    private static string Unavailable(string reason, string fix) => Serialize(new { schemaVersion = JsonOutput.SchemaVersion, available = false, reason, fix });

    private static string Serialize(object value) => JsonSerializer.Serialize(value, Options) + "\n";
}