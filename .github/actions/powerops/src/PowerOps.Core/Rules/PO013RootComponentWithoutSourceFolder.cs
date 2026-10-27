using PowerOps.Core.Model;
using PowerOps.Core.Readers;

namespace PowerOps.Core.Rules;

public sealed class PO013RootComponentWithoutSourceFolder : IRule
{
    public string Id => "PO013";

    public Severity Severity => Severity.Error;

    public string Title => "Root component has no source folder in the YAML layout";

    public string Fix => "Commit the component's folder and list it in solutioncomponents.yml, or remove the component from rootcomponents.yml.";

    // Folder level only; classic workflows and other types are skipped until a real export shows their folder.
    public IEnumerable<Finding> Evaluate(Solution solution)
    {
        if (solution.SourceFolders is not { } folders)
        {
            yield break;
        }

        var file = solution.ManifestFile[..solution.ManifestFile.LastIndexOf('/')] + "/rootcomponents.yml";
        foreach (var r in solution.RootComponents)
        {
            var id = SolutionParser.NormalizeId(r.Id);
            var (folder, name) = r.Type switch
            {
                "1" => ("entities", r.SchemaName),
                "29" => ("modernflows", id),
                "300" => ("canvasapps", r.SchemaName),
                "380" => ("environmentvariabledefinitions", r.SchemaName),
                _ => ("", null),
            };

            if (name is null)
            {
                continue;
            }

            var expected = $"{folder}/{name}";
            if (!folders.Contains(expected, StringComparer.OrdinalIgnoreCase))
            {
                yield return new Finding(Id, Severity, $"Root component '{name}' (type {r.Type}) has no source folder '{expected}'.", file, name);
            }
        }
    }
}