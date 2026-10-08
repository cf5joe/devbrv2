namespace DevBR.App.Services;

/// <summary>
/// Decides whether a progress update may be shown now: at most one per <see cref="Interval"/>, so worker
/// events can arrive as often as they like while the UI repaints at a bounded rate.
/// </summary>
public sealed class UpdateGate(TimeSpan interval)
{
    private TimeSpan? _last;

    public TimeSpan Interval { get; } = interval;

    /// <summary>True (and the update is counted) when at least <see cref="Interval"/> has passed since the last one.</summary>
    public bool TryPass(TimeSpan now)
    {
        if (_last is { } last && now - last < Interval)
        {
            return false;
        }

        _last = now;
        return true;
    }

    /// <summary>Time left before the next update may pass.</summary>
    public TimeSpan Remaining(TimeSpan now) => _last is { } last && now - last < Interval ? Interval - (now - last) : TimeSpan.Zero;
}

/// <summary>
/// An <see cref="IProgress{T}"/> for the UI that coalesces reports: the handler runs on the creating
/// thread's synchronization context at most about ten times a second (by default), always with the most
/// recent value, and the last value is never lost. Disposing stops any pending delivery.
/// </summary>
public sealed class ThrottledProgress<T> : IProgress<T>, IDisposable
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(100);

    private readonly Action<T> _handler;
    private readonly SynchronizationContext? _context;
    private readonly TimeProvider _time;
    private readonly UpdateGate _gate;
    private readonly long _start;
    private readonly Lock _lock = new();
    private T _latest = default!;
    private bool _hasPending;
    private bool _scheduled;
    private bool _disposed;
    private ITimer? _timer;

    public ThrottledProgress(Action<T> handler, TimeSpan? interval = null, TimeProvider? time = null)
    {
        _handler = handler;
        _context = SynchronizationContext.Current;
        _time = time ?? TimeProvider.System;
        _gate = new UpdateGate(interval ?? DefaultInterval);
        _start = _time.GetTimestamp();
    }

    public void Report(T value)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _latest = value;
            _hasPending = true;
            if (_scheduled)
            {
                return; // a delivery is already on its way and will pick up this value
            }

            _scheduled = true;
            var wait = _gate.Remaining(Now);
            if (wait > TimeSpan.Zero)
            {
                _timer?.Dispose();
                _timer = _time.CreateTimer(_ => Post(), null, wait, Timeout.InfiniteTimeSpan);
                return;
            }
        }

        Post();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _hasPending = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private TimeSpan Now => _time.GetElapsedTime(_start);

    private void Post()
    {
        if (_context is null)
        {
            Deliver(null);
        }
        else
        {
            _context.Post(Deliver, null);
        }
    }

    private void Deliver(object? state)
    {
        T value;
        lock (_lock)
        {
            _scheduled = false;
            if (_disposed || !_hasPending)
            {
                return;
            }

            value = _latest;
            _hasPending = false;
            _gate.TryPass(Now);
        }

        _handler(value);
    }
}
