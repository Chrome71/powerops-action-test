namespace PowerOps.Cli;

/// <summary>A live read of one environment failed. The message must never contain the <c>--env</c> value, the environment URL or the token. <paramref name="forwarded"/> is the tool's own text for stderr only; it may repeat the <c>--env</c> value or the URL.</summary>
public sealed class DriftReadException(string message, string? forwarded = null, int exitCode = 3) : Exception(message)
{
    public string? Forwarded { get; } = forwarded;

    /// <summary>The exit code <c>drift</c> returns: 3 (a failed read) unless the failure is a usage error (2).</summary>
    public int ExitCode { get; } = exitCode;
}