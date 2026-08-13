namespace HalimRecovery.Core.Scanning;

/// <summary>Cooperative pause/resume switch for long scans.</summary>
public sealed class PauseToken
{
    private readonly ManualResetEventSlim _resumed = new(true);
    public bool IsPaused => !_resumed.IsSet;
    public void Pause() => _resumed.Reset();
    public void Resume() => _resumed.Set();

    /// <summary>Blocks the scanning thread while paused; observes cancellation.</summary>
    public void WaitIfPaused(CancellationToken ct) => _resumed.Wait(ct);
}
