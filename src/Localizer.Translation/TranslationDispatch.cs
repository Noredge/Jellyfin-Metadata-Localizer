namespace Localizer.Translation;

// One in-process transport at a time across title and overview workflows.
public static class TranslationDispatch
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static async Task<IDisposable> EnterAsync(CancellationToken token)
    { await Gate.WaitAsync(token); return new Lease(); }
    private sealed class Lease : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (!disposed) { disposed = true; Gate.Release(); } }
    }
}
