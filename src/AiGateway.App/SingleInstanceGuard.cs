namespace AiGateway.App;

internal sealed class SingleInstanceGuard : IDisposable
{
    internal const string AppMutexName = @"Local\AiGateway.App";

    private readonly Mutex _mutex;

    private SingleInstanceGuard(Mutex mutex) => _mutex = mutex;

    internal static SingleInstanceGuard? TryAcquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: false, name, out var createdNew);
        if (createdNew)
        {
            return new SingleInstanceGuard(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose() => _mutex.Dispose();
}
