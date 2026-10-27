using System.Text.Json.Nodes;

namespace PowerOps.Core.Flows;

/// <summary>What the workflow functions read. Implementations throw <see cref="ExpressionException"/>, <see cref="UnmockedException"/> or <see cref="UnsupportedException"/> when a value is missing.</summary>
public interface IExpressionContext
{
    JsonNode? Outputs(string action);

    JsonNode? TriggerOutputs();

    JsonNode? Variable(string name);

    JsonNode? Parameter(string name);

    JsonNode? Items(string loop);

    JsonNode? Item();

    JsonNode? Workflow();

    JsonNode? Result(string scope);

    /// <summary>The fixed timestamp of the run, any ISO 8601 text; <c>utcNow()</c> formats it.</summary>
    string UtcNow();

    string Guid();
}