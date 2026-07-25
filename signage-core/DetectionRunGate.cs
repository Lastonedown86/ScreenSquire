namespace PiSignage.Signage;

public sealed class DetectionRunGate : IDisposable
{
    readonly object _sync = new();
    CancellationTokenSource? _active;
    bool _disposed;

    public bool TryBegin(
        CancellationToken lifetimeToken,
        out CancellationToken runToken)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active is not null)
            {
                runToken = default;
                return false;
            }

            _active = CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeToken);
            runToken = _active.Token;
            return true;
        }
    }

    public void Cancel()
    {
        CancellationTokenSource? active;
        lock (_sync)
            active = _active;
        active?.Cancel();
    }

    public void Complete(CancellationToken runToken)
    {
        CancellationTokenSource? completed = null;
        lock (_sync)
        {
            if (_active is not null && _active.Token == runToken)
            {
                completed = _active;
                _active = null;
            }
        }
        completed?.Dispose();
    }

    public void Dispose()
    {
        CancellationTokenSource? active;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            active = _active;
            _active = null;
        }
        if (active is not null)
        {
            active.Cancel();
            active.Dispose();
        }
    }
}
