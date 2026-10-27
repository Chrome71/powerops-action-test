using System.IO.Compression;

using PowerOps.Core.Model;

namespace PowerOps.Core.Readers;

/// <summary>Reads an exported solution zip (root `solution.xml`) by streaming entries; nothing is extracted to disk.</summary>
public static class ZipReader
{
    public static Solution Read(string zipPath) => Read(zipPath, new ReadBudget());

    internal static Solution Read(string zipPath, ReadBudget budget)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/') || !IsSafe(name))
            {
                continue; // directories, rooted and `..` entries (zip-slip) are never read
            }

            entries.TryAdd(name, entry);
        }

        return SolutionParser.Parse(new SolutionFiles(
            entries.Keys,
            p => ReadEntry(entries[p], p, budget),
            "solution.xml",
            "customizations.xml"));
    }

    private static bool IsSafe(string name) =>
        !name.StartsWith('/')
        && !(name.Length > 1 && name[1] == ':')
        && !name.Split('/').Contains("..");

    private static string ReadEntry(ZipArchiveEntry entry, string name, ReadBudget budget)
    {
        if (entry.Length > budget.MaxFileBytes)
        {
            throw new InvalidDataException($"{name}: larger than {budget.MaxFileBytes / (1024 * 1024)} MB.");
        }

        return SolutionParser.ReadCapped(entry.Open(), name, budget);
    }
}