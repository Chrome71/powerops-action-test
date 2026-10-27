using PowerOps.Core.Model;

namespace PowerOps.Core.Readers;

/// <summary>Loads a solution from a path returned by <see cref="SolutionDiscovery"/>.</summary>
public static class SolutionLoader
{
    /// <exception cref="InvalidDataException">The solution is unreadable or malformed; the message starts with <paramref name="path"/>.</exception>
    public static Solution Load(string path, LoadContext? context = null)
    {
        try
        {
            context ??= new LoadContext();
            return Directory.Exists(path) ? XmlUnpackReader.Read(path)
                : YamlReader.IsSolutionYml(path) ? YamlReader.Read(path, context.RootFor(YamlReader.RootFolder(path)))
                : path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ZipReader.Read(path)
                : throw new InvalidDataException($"{path}: not a solution folder or zip.");
        }
        catch (InvalidDataException e) when (e.Message.StartsWith(path, StringComparison.Ordinal))
        {
            throw;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
    }
}