namespace PowerOps.Core.Readers;

/// <summary>Byte limits for reading one solution; solutions are untrusted, so a zip bomb must not exhaust memory or time.</summary>
internal sealed class ReadBudget(long maxTotalBytes = 256L * 1024 * 1024, long maxFileBytes = 50L * 1024 * 1024)
{
    private long _remaining = maxTotalBytes;

    public long MaxFileBytes { get; } = maxFileBytes;

    public long MaxTotalBytes { get; } = maxTotalBytes;

    public void Consume(long bytes, string name)
    {
        _remaining -= bytes;
        if (_remaining < 0)
        {
            throw new InvalidDataException($"{name}: solution is larger than {MaxTotalBytes / (1024 * 1024)} MB when read.");
        }
    }
}