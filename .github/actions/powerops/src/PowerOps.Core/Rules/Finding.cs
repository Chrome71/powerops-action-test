namespace PowerOps.Core.Rules;

public enum Severity { Warning, Error }

public sealed record Finding(string RuleId, Severity Severity, string Message, string File, string? Component);