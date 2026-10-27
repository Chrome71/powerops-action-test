using System.IO.Compression;

namespace PowerOps.Core.Readers;

/// <summary>Finds solutions (unpacked XML folders, YAML layout `solution.yml` files and exported zips) under a path.</summary>
public static class SolutionDiscovery
{
    internal static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", "node_modules" };

    // Symlinks are not followed (loops, escapes); unreadable folders are skipped; `X.ZIP` matches too.
    internal static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    public static IReadOnlyList<string> Find(string path)
    {
        var found = new List<string>();
        if (File.Exists(path))
        {
            // An explicitly named zip is taken as is; the loader reports it if it is not a solution.
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                found.Add(path);
            }
        }
        else if (Directory.Exists(path))
        {
            Walk(path, found);
        }
        else if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            found.Add(path);
        }

        // Sorted by the forward-slash form so output is identical on Windows.
        found.Sort((a, b) => string.CompareOrdinal(a.Replace('\\', '/'), b.Replace('\\', '/')));
        return found;
    }

    private static void Walk(string dir, List<string> found)
    {
        if (File.Exists(Path.Combine(dir, "Other", "Solution.xml")))
        {
            found.Add(dir);
            return;
        }

        // A YAML root: solutions/<name>/solution.yml. Only roots at or below the given path are found.
        var yamlSolutions = Directory.EnumerateDirectories(dir, "solutions", Options)
            .SelectMany(s => Directory.EnumerateDirectories(s, "*", Options))
            .SelectMany(s => Directory.EnumerateFiles(s, "solution.yml", Options))
            .ToList();
        if (yamlSolutions.Count > 0)
        {
            found.AddRange(yamlSolutions);
            return;
        }

        foreach (var sub in Directory.EnumerateDirectories(dir, "*", Options).Where(d => !Skipped.Contains(Path.GetFileName(d))))
        {
            Walk(sub, found);
        }

        found.AddRange(Directory.EnumerateFiles(dir, "*.zip", Options).Where(HasRootSolutionXml));
    }

    private static bool HasRootSolutionXml(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return zip.Entries.Any(e => e.FullName.Equals("solution.xml", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}