namespace NvlogDetector;

public sealed class SingleInstance : IDisposable
{
    const string Name = @"Local\NvlogNvlgstgDetector";

    readonly Mutex _mutex;
    bool _released;

    public SingleInstance()
    {
        _mutex = new Mutex(false, Name);
        try
        {
            Acquired = _mutex.WaitOne(TimeSpan.FromSeconds(4));
        }
        catch (AbandonedMutexException)
        {
            Acquired = true;
        }
    }

    public bool Acquired { get; }

    public void Dispose()
    {
        if (_released)
            return;
        _released = true;
        if (Acquired)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Already released.
            }
        }

        _mutex.Dispose();
    }
}
