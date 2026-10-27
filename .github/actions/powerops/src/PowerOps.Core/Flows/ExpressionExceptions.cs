namespace PowerOps.Core.Flows;

/// <summary>An expression failed while it ran (a missing property, a wrong type); the simulator turns it into a Failed action with code InvalidTemplate.</summary>
public sealed class ExpressionException(string message) : Exception(message);

/// <summary>A form the simulator doesn't support (unreadable text, an unknown function, a format argument).</summary>
public sealed class UnsupportedException(string message) : Exception(message);

/// <summary>A value only the test can give is missing; the message names what to set.</summary>
public sealed class UnmockedException(string message) : Exception(message);