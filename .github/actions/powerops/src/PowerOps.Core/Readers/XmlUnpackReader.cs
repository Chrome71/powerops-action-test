using PowerOps.Core.Model;

namespace PowerOps.Core.Readers;

/// <summary>Reads a solution unpacked by `pac solution unpack` (XML layout, `Other/Solution.xml`).</summary>
public static class XmlUnpackReader
{
    // Symlinks are never followed: an unpacked solution must not read files outside its folder.
    private static readonly EnumerationOptions Options = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };

    public static Solution Read(string folder) => Read(folder, new ReadBudget());

    internal static Solution Read(string folder, ReadBudget budget)
    {
        var root = Path.GetFullPath(folder);
        if (!Directory.Exists(root))
        {
            throw new InvalidDataException($"{folder}: folder not found.");
        }

        var paths = Directory.EnumerateFiles(root, "*", Options)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToList();

        return SolutionParser.Parse(new SolutionFiles(
            paths,
            p => SolutionParser.ReadCapped(File.OpenRead(Path.Combine(root, p)), p, budget),
            "Other/Solution.xml",
            "Other/Customizations.xml"));
    }
}