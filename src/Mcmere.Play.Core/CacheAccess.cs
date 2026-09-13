namespace Mcmere.Play.Core;

internal static class CacheAccess
{
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    public static async Task<IDisposable> AcquireAsync(string destination, CancellationToken ct)
    {
        var hash = (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(Path.GetFullPath(destination));
        var gate = Gates[hash % (uint)Gates.Length];
        await gate.WaitAsync(ct);
        return new Lease(gate);
    }
    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
