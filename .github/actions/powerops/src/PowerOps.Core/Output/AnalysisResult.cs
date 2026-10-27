using PowerOps.Core.Model;
using PowerOps.Core.Readers;
using PowerOps.Core.Rules;

namespace PowerOps.Core.Output;

/// <summary>One analyzed solution. <paramref name="Path"/> is the display path (forward slashes, no absolute paths when avoidable).</summary>
public sealed record SolutionResult(string Path, Solution Solution, IReadOnlyList<Finding> Findings)
{
    /// <summary>Distinct, non-empty owning solutions of declared missing dependencies, sorted ordinal.</summary>
    public IReadOnlyList<string> Requires { get; } = Solution.MissingDependencies
        .Select(d => d.Required.Solution)
        .Where(s => !string.IsNullOrEmpty(s))
        .Select(s => s!)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToList();
}

public sealed record AnalysisResult(IReadOnlyList<SolutionResult> Solutions)
{
    /// <summary>Findings dropped because they are in the baseline.</summary>
    public int Baselined { get; init; }

    public int Errors => Count(Severity.Error);

    public int Warnings => Count(Severity.Warning);

    private int Count(Severity severity) => Solutions.Sum(s => s.Findings.Count(f => f.Severity == severity));

    /// <summary>Discovers, loads and analyzes every solution under <paramref name="paths"/>; display paths are relative to <paramref name="baseDir"/> when underneath it.</summary>
    public static AnalysisResult Run(IEnumerable<string> paths, string baseDir)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var results = new List<SolutionResult>();
        var context = new LoadContext();
        foreach (var root in paths)
        {
            foreach (var found in SolutionDiscovery.Find(root))
            {
                if (!seen.Add(System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(found))))
                {
                    continue;
                }

                var solution = SolutionLoader.Load(found, context);
                // A YAML solution is reported as its root: finding files are relative to it.
                var shown = YamlReader.IsSolutionYml(found) ? YamlReader.RootFolder(found) : found;
                results.Add(new SolutionResult(DisplayPath(shown, baseDir), solution, Analyze(solution, found)));
            }
        }

        return new AnalysisResult(results);
    }

    private static IReadOnlyList<Finding> Analyze(Solution solution, string path)
    {
        try
        {
            return Analyzer.Run(solution);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
    }

    private static string DisplayPath(string path, string baseDir)
    {
        var rel = System.IO.Path.GetRelativePath(baseDir, path);
        var underBase = !System.IO.Path.IsPathRooted(rel) && rel != ".." &&
            !rel.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);
        return System.IO.Path.TrimEndingDirectorySeparator(underBase ? rel : path).Replace('\\', '/');
    }
}