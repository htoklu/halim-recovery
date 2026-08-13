namespace HalimRecovery.Core.Models;

/// <summary>Progress snapshot for long-running scan/recovery operations.</summary>
public sealed class ScanProgress
{
    public string Phase { get; init; } = "";
    public long BytesProcessed { get; init; }
    public long BytesTotal { get; init; }
    public long FilesFound { get; init; }
    public TimeSpan Elapsed { get; init; }

    public double Percent => BytesTotal > 0 ? Math.Min(100.0, 100.0 * BytesProcessed / BytesTotal) : 0;

    /// <summary>Bytes per second over the whole run.</summary>
    public double Speed => Elapsed.TotalSeconds > 0.5 ? BytesProcessed / Elapsed.TotalSeconds : 0;

    public TimeSpan? EstimatedRemaining
    {
        get
        {
            if (Speed <= 0 || BytesTotal <= 0 || BytesProcessed <= 0) return null;
            var remaining = (BytesTotal - BytesProcessed) / Speed;
            return remaining is >= 0 and < 30 * 24 * 3600 ? TimeSpan.FromSeconds(remaining) : null;
        }
    }
}
