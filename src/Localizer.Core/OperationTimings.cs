using System.Diagnostics;

namespace Localizer.Core;

// Request/operation-local, no payloads or identities. Nested phase times are inclusive.
public sealed class OperationTimings : IDisposable
{
    private static readonly AsyncLocal<OperationTimings?> Current = new();
    private readonly OperationTimings? previous;
    private readonly Dictionary<string, (long Ticks, int Count)> phases = new(StringComparer.Ordinal);
    public OperationTimings() { previous = Current.Value; Current.Value = this; }
    public static IDisposable Measure(string phase) => Current.Value is { } current ? new Phase(current, phase) : Empty.Instance;
    public Dictionary<string, double> Snapshot()
    {
        lock (phases) return phases.ToDictionary(x => x.Key, x => Math.Round(x.Value.Ticks * 1000d / Stopwatch.Frequency, 3));
    }
    public Dictionary<string, int> Counts()
    { lock (phases) return phases.ToDictionary(x => x.Key, x => x.Value.Count); }
    public void Dispose() => Current.Value = previous;
    private sealed class Phase(OperationTimings owner, string name) : IDisposable
    {
        private readonly long start = Stopwatch.GetTimestamp(); private bool disposed;
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            lock (owner.phases)
            {
                var prior = owner.phases.GetValueOrDefault(name);
                owner.phases[name] = (prior.Ticks + Stopwatch.GetTimestamp() - start, prior.Count + 1);
            }
        }
    }
    private sealed class Empty : IDisposable
    { internal static readonly Empty Instance = new(); public void Dispose() { } }
}
