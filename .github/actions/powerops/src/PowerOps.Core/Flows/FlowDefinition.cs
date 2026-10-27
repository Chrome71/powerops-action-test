using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerOps.Core.Flows;

/// <summary>A connection reference entry of a cloud flow. <paramref name="LogicalName"/> is null for embedded/direct connections.</summary>
public sealed record FlowConnectionReference(string Key, string? LogicalName, string? ApiName);

public sealed record StringLiteral(string JsonPath, string Value);

/// <summary>A trigger of a cloud flow. <paramref name="Message"/> is the Dataverse subscription message (1 create, 2 delete, 3 update, ...), null when absent or not a number.</summary>
public sealed record FlowTrigger(string Name, string? OperationId, int? Message, string? Entity, string? FilteringAttributes);

/// <summary>The parts of a cloud flow's clientdata JSON that the analyzer rules need.</summary>
public sealed partial record FlowDefinition(
    IReadOnlyList<FlowConnectionReference> ConnectionReferences,
    IReadOnlyList<string> EnvironmentVariables,
    IReadOnlyList<string> ChildFlowIds,
    IReadOnlyList<StringLiteral> StringLiterals,
    IReadOnlyList<FlowTrigger> Triggers)
{
    private static readonly string[] SkippedParameters = ["$connections", "$authentication"];
    private static readonly string[] SkippedProperties = ["metadata", "$schema", "operationMetadataId"];

    [GeneratedRegex(@"\(([^()\s]+)\)\s*$")]
    private static partial Regex LabelSuffix();

    /// <summary>The environment variable schema name a flow parameter stands for: <c>metadata.schemaName</c>, else the <c>(schema)</c> suffix of its label. Null for <c>$connections</c>, <c>$authentication</c> and parameters that are no environment variable.</summary>
    internal static string? EnvironmentVariableSchema(string parameterName, string? metadataSchemaName)
    {
        if (SkippedParameters.Contains(parameterName))
        {
            return null;
        }

        var schema = metadataSchemaName;
        if (string.IsNullOrEmpty(schema) && LabelSuffix().Match(parameterName) is { Success: true } m)
        {
            schema = m.Groups[1].Value;
        }

        return string.IsNullOrEmpty(schema) ? null : schema;
    }

    /// <exception cref="InvalidDataException">The text is not a JSON object.</exception>
    public static FlowDefinition Parse(string clientDataJson)
    {
        try
        {
            // Parse takes raw strings, so a BOM that a stream reader would have removed can still be present.
            using var doc = JsonDocument.Parse(clientDataJson.TrimStart('\uFEFF'), new JsonDocumentOptions { MaxDepth = 256 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Flow definition JSON is not an object.");
            }

            var connections = new List<FlowConnectionReference>();
            var envVars = new List<string>();
            var children = new List<string>();
            var literals = new List<StringLiteral>();
            var triggers = new List<FlowTrigger>();

            if (Get(doc.RootElement, "properties") is not { } props)
            {
                return new FlowDefinition(connections, envVars, children, literals, []);
            }

            if (Get(props, "connectionReferences") is { ValueKind: JsonValueKind.Object } refs)
            {
                foreach (var r in refs.EnumerateObject())
                {
                    connections.Add(new FlowConnectionReference(
                        r.Name,
                        Str(Get(Get(r.Value, "connection"), "connectionReferenceLogicalName")),
                        Str(Get(Get(r.Value, "api"), "name"))));
                }
            }

            if (Get(props, "definition") is { } definition)
            {
                if (Get(definition, "parameters") is { ValueKind: JsonValueKind.Object } parameters)
                {
                    foreach (var p in parameters.EnumerateObject())
                    {
                        if (EnvironmentVariableSchema(p.Name, Str(Get(Get(p.Value, "metadata"), "schemaName"))) is { } schema)
                        {
                            envVars.Add(schema);
                        }
                    }
                }

                if (Get(definition, "triggers") is { ValueKind: JsonValueKind.Object } triggerObjects)
                {
                    foreach (var t in triggerObjects.EnumerateObject())
                    {
                        var inputs = Get(t.Value, "inputs");
                        var triggerParams = Get(inputs, "parameters");
                        var message = Get(triggerParams, "subscriptionRequest/message");
                        triggers.Add(new FlowTrigger(
                            t.Name,
                            Str(Get(Get(inputs, "host"), "operationId")),
                            message switch
                            {
                                { ValueKind: JsonValueKind.Number } n when n.TryGetInt32(out var i) => i,
                                { ValueKind: JsonValueKind.String } sv when int.TryParse(sv.GetString(), out var i) => i,
                                _ => null,
                            },
                            Str(Get(triggerParams, "subscriptionRequest/entityname")),
                            Str(Get(triggerParams, "subscriptionRequest/filteringattributes"))));
                    }
                }

                Walk(definition, "$", false, children, literals);
            }

            return new FlowDefinition(connections, envVars, children.Distinct().ToList(), literals, triggers);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Flow definition is not valid JSON: " + ex.Message, ex);
        }
    }

    private static void Walk(JsonElement e, string path, bool skipped, List<string> children, List<StringLiteral> literals)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == "workflowReferenceName" && p.Value.ValueKind == JsonValueKind.String)
                    {
                        children.Add(p.Value.GetString()!.ToLowerInvariant());
                    }

                    // Skipped subtrees still contribute child flow ids, but no literals.
                    Walk(p.Value, path + "." + p.Name, skipped || SkippedProperties.Contains(p.Name), children, literals);
                }

                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in e.EnumerateArray())
                {
                    Walk(item, $"{path}[{i++}]", skipped, children, literals);
                }

                break;
            case JsonValueKind.String when !skipped:
                literals.Add(new StringLiteral(path, e.GetString()!));
                break;
        }
    }

    private static JsonElement? Get(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) ? v : null;

    private static string? Str(JsonElement? e) => e is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;
}