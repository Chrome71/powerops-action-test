using System.Globalization;
using System.Text;

namespace PowerOps.Core.Plans;

/// <summary>Exit code plus what to print: <see cref="Out"/> to stdout, <see cref="Err"/> to stderr.</summary>
public sealed record SignOffResult(int ExitCode, IReadOnlyList<string> Out, IReadOnlyList<string> Err);

public static class PlanSignOff
{
    /// <summary>Writes status, date and body hash into the plan header, or refuses and writes nothing.</summary>
    public static SignOffResult Run(string planPath, string baseDir, DateOnly today)
    {
        if (PlanChecker.ResolvePlanFile(planPath) is not { } file)
        {
            return Fail(2, $"plan not found: {planPath}");
        }

        var text = File.ReadAllText(file);
        var doc = PlanDocument.Parse(text);
        if (doc.Header is not { } h)
        {
            return Fail(2, $"malformed header: {doc.Error}");
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(file))!;
        var errors = new List<string>();
        if (h.Status != "approved")
        {
            errors.Add($"status is {h.Status}; sign-off needs status: approved");
        }

        errors.AddRange(PlanChecker.ReviewProblems(h.Review, Path.Combine(folder, "review.md")));
        var tenant = PlanChecker.ScanTenantData(folder, baseDir).ToList();
        if (tenant.Count > 0)
        {
            errors.AddRange(tenant);
            errors.Add("remove tenant data before signing off");
        }

        if (errors.Count > 0)
        {
            return new SignOffResult(1, [], errors.Select(e => $"error: {e}").ToList());
        }

        var hash = doc.BodyHash!;
        var fence = doc.Lines[doc.Close];
        var eol = fence[fence.TrimEnd('\r', '\n').Length..] is { Length: > 0 } ending ? ending : "\n";
        var set = new Dictionary<string, string>
        {
            ["status"] = "signed-off",
            ["signed-off"] = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["signed-off-hash"] = hash,
        };

        var sb = new StringBuilder(doc.Lines[0]);
        for (var i = 1; i < doc.Close; i++)
        {
            var line = doc.Lines[i];
            var key = PlanDocument.SplitLine(line).Key;
            if (key is not null && set.Remove(key, out var value))
            {
                sb.Append($"{key}: {value}{line[line.TrimEnd('\r', '\n').Length..]}");
            }
            else
            {
                sb.Append(line);
            }
        }

        foreach (var (key, value) in set)
        {
            sb.Append($"{key}: {value}{eol}");
        }

        for (var i = doc.Close; i < doc.Lines.Count; i++)
        {
            sb.Append(doc.Lines[i]);
        }

        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
        return new SignOffResult(0, [$"signed off {PlanChecker.Display(folder, baseDir)} (sha256 {hash[..12]}…)"], []);
    }

    private static SignOffResult Fail(int code, string message) => new(code, [], [$"error: {message}"]);
}