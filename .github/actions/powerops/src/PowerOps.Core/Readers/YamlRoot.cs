using System.Xml.Linq;

namespace PowerOps.Core.Readers;

/// <summary>
/// The files of one Dataverse Git-integration YAML root (the folder holding `solutions/`). Several solutions can share it,
/// so each file is listed and read once against one <see cref="ReadBudget"/>.
/// </summary>
internal sealed class YamlRoot
{
    // Symlinks are never followed: a root must not read files outside its folder.
    private static readonly EnumerationOptions Options = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };

    private readonly ReadBudget _budget;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> _parsed = new(StringComparer.Ordinal);

    public YamlRoot(string folder, ReadBudget? budget = null)
    {
        Folder = Path.GetFullPath(folder);
        _budget = budget ?? new ReadBudget();
        if (!Directory.Exists(Folder))
        {
            throw new InvalidDataException($"{folder}: folder not found.");
        }

        foreach (var p in Directory.EnumerateFiles(Folder, "*", Options)
            .Select(f => Path.GetRelativePath(Folder, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal))
        {
            Paths.TryAdd(p, p);
        }
    }

    public string Folder { get; }

    /// <summary>All files as relative forward-slash paths, keyed case-insensitively, in ordinal order.</summary>
    public Dictionary<string, string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string ReadText(string path) =>
        _cache.TryGetValue(path, out var text)
            ? text
            : _cache[path] = SolutionParser.ReadCapped(File.OpenRead(System.IO.Path.Combine(Folder, path)), path, _budget);

    /// <exception cref="InvalidDataException">The message starts with <paramref name="path"/>.</exception>
    /// <remarks>Parsed once per path and shared by every solution of the root, so callers must not change it.</remarks>
    public XElement LoadYaml(string path)
    {
        if (_parsed.TryGetValue(path, out var parsed))
        {
            return parsed;
        }

        var text = ReadText(path);
        try
        {
            return _parsed[path] = YamlXml.Convert(text);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
    }
}