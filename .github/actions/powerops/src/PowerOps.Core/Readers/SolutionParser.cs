using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

using PowerOps.Core.Model;

namespace PowerOps.Core.Readers;

/// <summary>The file-access seam: relative forward-slash paths plus a way to read one as text.</summary>
internal sealed record SolutionFiles(
    IReadOnlyCollection<string> Paths,
    Func<string, string> ReadText,
    string ManifestFile,
    string CustomizationsFile);

/// <summary>Parses the files of an unpacked or exported solution; folder and zip differ only in <see cref="SolutionFiles"/>.</summary>
internal static partial class SolutionParser
{
    /// <summary>Reads a stream as text, enforcing the per-file cap on the bytes actually read (declared sizes can lie) and the per-solution budget.</summary>
    public static string ReadCapped(Stream source, string name, ReadBudget budget)
    {
        using var stream = source;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + n > budget.MaxFileBytes)
            {
                throw new InvalidDataException($"{name}: larger than {budget.MaxFileBytes / (1024 * 1024)} MB.");
            }

            budget.Consume(n, name);
            buffer.Write(chunk, 0, n);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static Solution Parse(SolutionFiles files)
    {
        // Many references can point at one entry (zip-bomb amplification), so each file is read once.
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);
        var read = files.ReadText;
        files = files with { ReadText = p => cache.TryGetValue(p, out var text) ? text : cache[p] = read(p) };

        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in files.Paths.OrderBy(p => p, StringComparer.Ordinal))
        {
            paths.TryAdd(p, p);
        }

        if (!paths.TryGetValue(files.ManifestFile, out var manifestPath))
        {
            throw new InvalidDataException($"{files.ManifestFile} not found.");
        }

        var manifest = LoadXml(files, manifestPath).Element("SolutionManifest")
            ?? throw new InvalidDataException($"{manifestPath}: missing SolutionManifest element.");
        var publisher = manifest.Element("Publisher");

        var customizations = paths.TryGetValue(files.CustomizationsFile, out var customizationsPath)
            ? LoadXml(files, customizationsPath)
            : null;

        var workflows = new List<Workflow>();
        foreach (var p in paths.Values.Where(p => p.StartsWith("Workflows/", StringComparison.OrdinalIgnoreCase)
            && p.EndsWith(".data.xml", StringComparison.OrdinalIgnoreCase)))
        {
            workflows.Add(ReadWorkflow(files, paths, LoadXml(files, p), p));
        }

        foreach (var w in customizations?.Element("Workflows")?.Elements("Workflow") ?? [])
        {
            workflows.Add(ReadWorkflow(files, paths, w, customizationsPath!));
        }

        var canvasApps = paths.Values
            .Where(p => CanvasMetaPath().IsMatch(p))
            .Select(files.ReadText)
            .Concat((customizations?.Element("CanvasApps")?.Elements("CanvasApp") ?? []).Select(e => e.ToString(SaveOptions.DisableFormatting)))
            .ToList();

        return new Solution(
            UniqueName: Text(manifest, "UniqueName") ?? "",
            Version: Text(manifest, "Version") ?? "",
            Managed: Text(manifest, "Managed") == "1",
            Publisher: new Publisher(
                publisher is null ? "" : Text(publisher, "UniqueName") ?? "",
                publisher is null ? "" : Text(publisher, "CustomizationPrefix") ?? ""),
            RootComponents: ReadRootComponents(manifest.Element("RootComponents")),
            MissingDependencies: ReadMissingDependencies(manifest.Element("MissingDependencies")),
            ConnectionReferences: (customizations?.Element("connectionreferences")?.Elements("connectionreference") ?? [])
                .Select(e => ReadConnectionReference(e, customizationsPath!))
                .ToList(),
            EnvironmentVariables: paths.Values
                .Where(p => EnvVarDefinitionPath().IsMatch(p))
                .Select(p => ReadEnvironmentVariable(files, paths, p))
                .ToList(),
            Workflows: workflows,
            CanvasAppTexts: canvasApps,
            ManifestFile: manifestPath);
    }

    internal static List<RootComponent> ReadRootComponents(XElement? rootComponents) =>
        (rootComponents?.Elements("RootComponent") ?? [])
            .Select(e => new RootComponent((string?)e.Attribute("type") ?? "", Opt((string?)e.Attribute("schemaName")), NormalizeId((string?)e.Attribute("id"))))
            .ToList();

    internal static List<MissingDependency> ReadMissingDependencies(XElement? missingDependencies) =>
        (missingDependencies?.Elements("MissingDependency") ?? [])
            .Select(e => new MissingDependency(
                ReadNode(e.Element("Required") ?? new XElement("Required")),
                ReadNode(e.Element("Dependent") ?? new XElement("Dependent"))))
            .ToList();

    internal static ConnectionReference ReadConnectionReference(XElement e, string file) => new(
        (string?)e.Attribute("connectionreferencelogicalname") ?? "",
        Text(e, "connectorid"),
        file);

    internal static Workflow ReadWorkflow(SolutionFiles files, Dictionary<string, string> paths, XElement e, string definedIn)
    {
        var jsonName = Opt(Text(e, "JsonFileName")?.Replace('\\', '/').TrimStart('/'));
        var jsonPath = jsonName is not null && paths.TryGetValue(jsonName, out var actual) ? actual : null;
        var category = int.TryParse(Text(e, "Category"), out var c) ? c : 0;
        if (category == 5 && jsonName is not null && jsonPath is null)
        {
            throw new InvalidDataException($"{definedIn}: cloud flow '{(string?)e.Attribute("Name")}' declares {jsonName}, which is missing.");
        }

        return new Workflow(
            NormalizeId((string?)e.Attribute("WorkflowId")) ?? "",
            (string?)e.Attribute("Name") ?? "",
            category,
            int.TryParse(Text(e, "StateCode"), out var state) ? state : null,
            jsonPath ?? jsonName ?? definedIn,
            jsonPath is null ? null : files.ReadText(jsonPath));
    }

    private static EnvironmentVariable ReadEnvironmentVariable(SolutionFiles files, Dictionary<string, string> paths, string path)
    {
        var def = LoadXml(files, path);
        var valuesName = path[..path.LastIndexOf('/')] + "/environmentvariablevalues.json";
        string? current = null;
        if (paths.TryGetValue(valuesName, out var valuesPath))
        {
            current = ReadCurrentValue(files.ReadText(valuesPath), valuesPath);
        }

        return ReadEnvironmentVariable(def, current, path, valuesPath);
    }

    internal static EnvironmentVariable ReadEnvironmentVariable(XElement def, string? current, string path, string? valuesPath)
    {
        return new EnvironmentVariable(
            (string?)def.Attribute("schemaname") ?? "",
            Text(def, "type"),
            Text(def, "isrequired") is "1" or "true",
            Text(def, "defaultvalue"),
            current,
            path,
            valuesPath);
    }

    private static string? ReadCurrentValue(string json, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("environmentvariablevalues", out var values)
                && values.ValueKind == JsonValueKind.Object
                && values.TryGetProperty("environmentvariablevalue", out var value))
            {
                if (value.ValueKind == JsonValueKind.Array)
                {
                    value = value.EnumerateArray().FirstOrDefault();
                }

                if (value.ValueKind == JsonValueKind.Object
                    && value.TryGetProperty("value", out var v)
                    && v.ValueKind == JsonValueKind.String)
                {
                    return v.GetString();
                }
            }

            return null;
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
    }

    private static DependencyNode ReadNode(XElement e) => new(
        (string?)e.Attribute("type") ?? "",
        // Some nodes are keyed by `id.schemaname` / `id.connectionreferencelogicalname` instead of schemaName.
        Opt((string?)e.Attribute("schemaName"))
            ?? Opt(e.Attributes().FirstOrDefault(a => a.Name.LocalName.StartsWith("id.", StringComparison.Ordinal))?.Value),
        NormalizeId((string?)e.Attribute("id")),
        Opt((string?)e.Attribute("displayName")),
        Opt((string?)e.Attribute("solution")));

    internal const int MaxXmlDepth = 200;

    private static XElement LoadXml(SolutionFiles files, string path)
    {
        try
        {
            // DTDs are prohibited: solutions are untrusted input.
            var text = files.ReadText(path);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit };

            // XDocument.Load is quadratic in nesting depth, so reject deep input with a cheap forward-only pass first.
            using (var scan = XmlReader.Create(new StringReader(text), settings))
            {
                while (scan.Read())
                {
                    if (scan.Depth > MaxXmlDepth)
                    {
                        throw new XmlException($"XML is nested deeper than {MaxXmlDepth} levels.");
                    }
                }
            }

            using var reader = XmlReader.Create(new StringReader(text), settings);
            return XDocument.Load(reader).Root!;
        }
        catch (XmlException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
    }

    internal static string? Text(XElement e, string child) => Opt(e.Element(child)?.Value.Trim());

    internal static string? Opt(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    internal static string? NormalizeId(string? id) => Opt(id?.Trim().Trim('{', '}').ToLowerInvariant());

    [GeneratedRegex(@"^CanvasApps/[^/]+\.meta\.xml$", RegexOptions.IgnoreCase)]
    private static partial Regex CanvasMetaPath();

    [GeneratedRegex(@"^environmentvariabledefinitions/[^/]+/environmentvariabledefinition\.xml$", RegexOptions.IgnoreCase)]
    private static partial Regex EnvVarDefinitionPath();
}