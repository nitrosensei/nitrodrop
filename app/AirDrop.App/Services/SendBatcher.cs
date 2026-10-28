using System;
using System.Collections.Generic;
using System.Threading;

namespace AirDrop.App.Services;

/// <summary>
/// Collects paths from rapid <c>--send</c> calls into one set. The classic Explorer verb with
/// MultiSelectModel=Player starts <c>NitroDrop.exe --send "%1"</c> once per selected item; each extra process forwards
/// its command line to the running instance (<see cref="SingleInstance"/>). Without batching every forward would
/// replace the previous file, so the last of N selected files would win.
/// <para>Debounce: <see cref="Flushed"/> fires once no new path arrived for <see cref="Quiet"/> (300 ms). A batch that
/// starts less than <see cref="ContinuationWindow"/> after the previous flush is marked as a continuation (slow
/// process starts on a big selection), so the caller appends instead of replacing.</para>
/// WPF-free (linked into AirDrop.Tests); the callback runs on a timer thread.
/// </summary>
public sealed class SendBatcher : IDisposable
{
    public static readonly TimeSpan DefaultQuiet = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan DefaultContinuationWindow = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly List<string> _paths = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private long _lastFlush = long.MinValue;
    private bool _batchContinues;
    private bool _pending;
    private bool _disposed;

    public SendBatcher(TimeProvider? time = null, TimeSpan? quiet = null, TimeSpan? continuationWindow = null)
    {
        _time = time ?? TimeProvider.System;
        Quiet = quiet ?? DefaultQuiet;
        ContinuationWindow = continuationWindow ?? DefaultContinuationWindow;
        _timer = _time.CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public TimeSpan Quiet { get; }
    public TimeSpan ContinuationWindow { get; }

    /// <summary>(paths in arrival order without duplicates, continues the previous batch).</summary>
    public event Action<IReadOnlyList<string>, bool>? Flushed;

    /// <summary>Adds paths and restarts the quiet timer. An empty <paramref name="paths"/> still (re)arms it, so a
    /// <c>--send</c> whose files all vanished produces an (empty) flush and the window is shown.</summary>
    public void Add(IEnumerable<string> paths)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_pending)
            {
                _pending = true;
                _batchContinues = _lastFlush != long.MinValue &&
                                  _time.GetElapsedTime(_lastFlush) < ContinuationWindow;
            }
            foreach (var p in paths)
                if (_seen.Add(p)) _paths.Add(p);
            _timer.Change(Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Delivers the current batch now (also used by the timer).</summary>
    public void Flush()
    {
        string[] batch;
        bool continues;
        lock (_gate)
        {
            if (!_pending || _disposed) return;
            batch = _paths.ToArray();
            continues = _batchContinues;
            _paths.Clear();
            _seen.Clear();
            _pending = false;
            _lastFlush = _time.GetTimestamp();
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        Flushed?.Invoke(batch, continues);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
        }
    }
}
