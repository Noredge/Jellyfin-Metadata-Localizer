namespace Localizer.Unified;

/// <summary>Coalesced in-process hints. SQLite remains the durable source of queue state.</summary>
public sealed class QueueWakeup
{
    private readonly object gate = new();
    private long version;
    private TaskCompletionSource changed = NewCompletion();
    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public long Version { get { lock (gate) return version; } }
    public void Notify()
    {
        TaskCompletionSource previous;
        lock (gate) { version++; previous = changed; changed = NewCompletion(); }
        previous.TrySetResult();
    }
    public async Task<bool> WaitAsync(long observedVersion, TimeSpan timeout, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Task pending;
        lock (gate)
        {
            // Includes a commit between the last queue read and registration of this wait.
            if (version != observedVersion) return true;
            pending = changed.Task;
        }
        try { await pending.WaitAsync(timeout, token); return true; }
        catch (TimeoutException) { return false; }
    }
}

public sealed class QueueIdleBackoff
{
    public const int InitialMilliseconds = 250;
    public const int MaximumMilliseconds = 8000;
    private int milliseconds = InitialMilliseconds;
    private long emptyChecks;
    public TimeSpan Delay => TimeSpan.FromMilliseconds(Volatile.Read(ref milliseconds));
    public long EmptyChecks => Interlocked.Read(ref emptyChecks);
    public void RecordEmptyCheck() => Interlocked.Increment(ref emptyChecks);
    public void Reset() => Volatile.Write(ref milliseconds, InitialMilliseconds);
    public void Advance() => Volatile.Write(ref milliseconds, Math.Min(MaximumMilliseconds, Volatile.Read(ref milliseconds) * 2));
}
