namespace PowerOps.Cli;

/// <summary>One Web API request's outcome. <paramref name="Failure"/> is a fixed text (<c>transport error</c> or <c>unreadable response</c>), never an exception's own message, which can name the host.</summary>
internal sealed record WebResult(int Status, string Body, bool TimedOut, string? Failure = null);