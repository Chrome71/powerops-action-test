namespace PowerOps.Core.Readers;

/// <summary>
/// State shared by the loads of one run: one <see cref="YamlRoot"/> per YAML root folder, so a file used by several
/// solutions of the root is read once and counted against one read budget once.
/// </summary>
public sealed class LoadContext
{
    private readonly Func<ReadBudget> _newBudget;
    private readonly Dictionary<string, YamlRoot> _roots = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public LoadContext()
        : this(() => new ReadBudget())
    {
    }

    internal LoadContext(Func<ReadBudget> newBudget) => _newBudget = newBudget;

    internal YamlRoot RootFor(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!_roots.TryGetValue(full, out var root))
        {
            _roots[full] = root = new YamlRoot(full, _newBudget());
        }

        return root;
    }
}