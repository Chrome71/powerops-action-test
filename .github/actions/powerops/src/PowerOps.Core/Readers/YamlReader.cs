using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

using PowerOps.Core.Model;

namespace PowerOps.Core.Readers;

/// <summary>Reads one `solutions/&lt;name&gt;/solution.yml` of a Dataverse Git-integration YAML root.</summary>
public static partial class YamlReader
{
    internal static bool IsSolutionYml(string path) =>
        Path.GetFileName(path).Equals("solution.yml", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    /// <summary>The folder that holds `solutions/` for a `solutions/&lt;name&gt;/solution.yml` path.</summary>
    internal static string RootFolder(string solutionYml)
    {
        var solutions = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(solutionYml)));
        return solutions is not null && Path.GetFileName(solutions).Equals("solutions", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(solutions) ?? solutions
            : throw new InvalidDataException($"{solutionYml}: not in a solutions/<name> folder.");
    }

    internal static Solution Read(string solutionYml, YamlRoot root)
    {
        var manifestFile = Path.GetRelativePath(root.Folder, Path.GetFullPath(solutionYml)).Replace('\\', '/');
        if (!SolutionYmlPath().IsMatch(manifestFile))
        {
            throw new InvalidDataException($"{solutionYml}: not in a solutions/<name> folder.");
        }

        var dir = manifestFile[..manifestFile.LastIndexOf('/')];
        var manifest = root.LoadYaml(manifestFile).Element("ImportExportXml")?.Element("SolutionManifest")
            ?? throw new InvalidDataException($"{manifestFile}: missing SolutionManifest element.");

        var componentsFile = Find(root, dir + "/solutioncomponents.yml")
            ?? throw new InvalidDataException($"{solutionYml}: {dir}/solutioncomponents.yml not found.");
        var entries = ReadEntries(root.LoadYaml(componentsFile));
        var solutionFiles = root.Paths.Values.Where(p => Under(entries, p)).Order(StringComparer.Ordinal).ToList();
        var ownFiles = solutionFiles.ToDictionary(p => p, StringComparer.OrdinalIgnoreCase);

        var publisherName = SolutionParser.Text(manifest.Element("Publisher") ?? new XElement("Publisher"), "UniqueName") ?? "";
        var publisherFile = Find(root, $"publishers/{publisherName}/publisher.yml")
            ?? throw new InvalidDataException($"{solutionYml}: publishers/{publisherName}/publisher.yml not found.");
        var publisher = Required(root, publisherFile, "Publisher");

        var files = new SolutionFiles(root.Paths.Values.ToList(), root.ReadText, manifestFile, "");
        return new Solution(
            UniqueName: SolutionParser.Text(manifest, "UniqueName") ?? "",
            Version: SolutionParser.Text(manifest, "Version") ?? "",
            Managed: SolutionParser.Text(manifest, "Managed") == "1",
            Publisher: new Publisher(
                SolutionParser.Text(publisher, "UniqueName") ?? "",
                SolutionParser.Text(publisher, "CustomizationPrefix") ?? ""),
            RootComponents: SolutionParser.ReadRootComponents(Optional(root, dir + "/rootcomponents.yml", "RootComponents")),
            MissingDependencies: SolutionParser.ReadMissingDependencies(Optional(root, dir + "/missingdependencies.yml", "MissingDependencies")),
            ConnectionReferences: Matching(solutionFiles, ConnectionReferencePath())
                .Select(p => SolutionParser.ReadConnectionReference(Required(root, p, "connectionreference"), p))
                .ToList(),
            EnvironmentVariables: Matching(solutionFiles, EnvVarDefinitionPath())
                .Select(p => ReadEnvironmentVariable(root, p))
                .ToList(),
            Workflows: Matching(solutionFiles, WorkflowPath())
                .Select(p => SolutionParser.ReadWorkflow(files, ownFiles, Required(root, p, "Workflow"), p))
                .ToList(),
            // The CanvasApp element as XML, the same form the XML layout gives (SolutionDiff reads the name and AppVersion from it).
            CanvasAppTexts: Matching(solutionFiles, CanvasAppPath()).Select(p => Required(root, p, "CanvasApp").ToString(SaveOptions.DisableFormatting)).ToList(),
            ManifestFile: manifestFile,
            CanvasFormulas: Matching(solutionFiles, CanvasAppPath()).SelectMany(p => ReadCanvasFormulas(root, solutionFiles, p)).ToList(),
            // From the matched files, not the entries: a one-segment entry such as `/entities` still yields `entities/<name>`.
            SourceFolders: solutionFiles
                .Select(p => string.Join('/', p.Split('/').Take(2)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.Ordinal)
                .ToList());
    }

    private static string? Find(YamlRoot root, string path) => root.Paths.TryGetValue(path, out var actual) ? actual : null;

    private static IEnumerable<string> Matching(List<string> files, Regex pattern) => files.Where(p => pattern.IsMatch(p));

    private static XElement Required(YamlRoot root, string path, string key) =>
        root.LoadYaml(path).Element(key) ?? throw new InvalidDataException($"{path}: missing {key} element.");

    private static XElement? Optional(YamlRoot root, string path, string key) =>
        Find(root, path) is { } actual ? root.LoadYaml(actual).Element(key) : null;

    /// <summary>The `'@path'` entries, without leading slash; entries that could point outside the root are dropped.</summary>
    private static HashSet<string> ReadEntries(XElement components) =>
        (components.Element("SolutionComponents")?.Elements("Component") ?? [])
            .Select(c => ((string?)c.Attribute("path") ?? "").Replace('\\', '/').Trim('/'))
            .Where(e => e.Length > 0 && !Path.IsPathRooted(e) && !e.Split('/').Contains(".."))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> or one of its parent folders is a listed entry.</summary>
    private static bool Under(HashSet<string> entries, string path)
    {
        for (var end = path.Length; end > 0; end = path.LastIndexOf('/', end - 1))
        {
            if (entries.Contains(path[..end]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The formulas of the `.pa.yaml` files under `&lt;name&gt;_DocumentUri/Src/` next to <paramref name="canvasApp"/>; <paramref name="solutionFiles"/> is ordinal-sorted.</summary>
    private static IEnumerable<CanvasFormula> ReadCanvasFormulas(YamlRoot root, List<string> solutionFiles, string canvasApp)
    {
        var folder = canvasApp[..canvasApp.LastIndexOf('/')];
        var name = folder[(folder.LastIndexOf('/') + 1)..];
        var app = root.LoadYaml(canvasApp).Element("CanvasApp");
        var appName = (app is null ? null : SolutionParser.Text(app, "DisplayName") ?? SolutionParser.Text(app, "Name")) ?? name;

        var src = $"{folder}/{name}_DocumentUri/Src/";
        foreach (var file in solutionFiles.Where(p => p.StartsWith(src, StringComparison.OrdinalIgnoreCase) && p.EndsWith(".pa.yaml", StringComparison.OrdinalIgnoreCase)))
        {
            var top = root.LoadYaml(file);
            var holders = new[] { top.Element("App") }
                .Concat(top.Element("Screens")?.Elements() ?? [])
                .Concat(top.Element("ComponentDefinitions")?.Elements() ?? []);
            foreach (var holder in holders.OfType<XElement>().SelectMany(Controls))
            {
                foreach (var property in holder.Element("Properties")?.Elements() ?? [])
                {
                    if (!property.HasElements && property.Value.StartsWith('='))
                    {
                        yield return new CanvasFormula(appName, XmlConvert.DecodeName(holder.Name.LocalName), XmlConvert.DecodeName(property.Name.LocalName), property.Value.TrimEnd(), file);
                    }
                }
            }
        }
    }

    /// <summary>The element itself, then its nested controls (each `Children` item holds one control element), in document order.</summary>
    private static IEnumerable<XElement> Controls(XElement control) =>
        control.Elements("Children").SelectMany(c => c.Elements()).SelectMany(Controls).Prepend(control);

    private static EnvironmentVariable ReadEnvironmentVariable(YamlRoot root, string path)
    {
        var valuesPath = Find(root, path[..path.LastIndexOf('/')] + "/environmentvariablevalues/environmentvariablevalue.yml");
        var current = valuesPath is null ? null : Required(root, valuesPath, "environmentvariablevalue").Element("value")?.Value;
        return SolutionParser.ReadEnvironmentVariable(Required(root, path, "environmentvariabledefinition"), SolutionParser.Opt(current), path, valuesPath);
    }

    [GeneratedRegex(@"^solutions/[^/]+/solution\.yml$", RegexOptions.IgnoreCase)]
    private static partial Regex SolutionYmlPath();

    [GeneratedRegex(@"^modernflows/[^/]+/workflow\.yml$", RegexOptions.IgnoreCase)]
    private static partial Regex WorkflowPath();

    [GeneratedRegex(@"^connectionreferences/[^/]+/connectionreference\.yml$", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionReferencePath();

    [GeneratedRegex(@"^environmentvariabledefinitions/[^/]+/environmentvariabledefinition\.yml$", RegexOptions.IgnoreCase)]
    private static partial Regex EnvVarDefinitionPath();

    [GeneratedRegex(@"^canvasapps/[^/]+/canvasapp\.yml$", RegexOptions.IgnoreCase)]
    private static partial Regex CanvasAppPath();
}