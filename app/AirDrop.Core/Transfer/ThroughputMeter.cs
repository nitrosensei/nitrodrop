using System.Diagnostics;

namespace AirDrop.Core.Transfer;

/// <summary>Bytes done/total plus a smoothed rate. Total = 0 when unknown (e.g. receiving: /Ask has no sizes).</summary>
public readonly record struct TransferStatus(long BytesDone, long BytesTotal, double BytesPerSecond);

/// <summary>
/// Exponential moving average of throughput with a ~1 s time constant, robust to irregular sample spacing
/// (alpha = 1 - e^(-dt/tau)). Also throttles reports so UIs are not flooded by 16 KB callbacks.
/// </summary>
public sealed class ThroughputMeter
{
    private readonly Func<TimeSpan> _clock;
    private readonly double _tau;
    private readonly TimeSpan _minInterval;
    private TimeSpan _lastSample, _lastReport;
    private bool _reported;
    private long _lastBytes;
    private bool _started;
    private readonly object _lock = new();

    public double BytesPerSecond { get; private set; }

    public ThroughputMeter(TimeSpan? timeConstant = null, TimeSpan? minReportInterval = null, Func<TimeSpan>? clock = null)
    {
        _tau = (timeConstant ?? TimeSpan.FromSeconds(1)).TotalSeconds;
        _minInterval = minReportInterval ?? TimeSpan.FromMilliseconds(100);
        if (clock == null)
        {
            var sw = Stopwatch.StartNew();
            clock = () => sw.Elapsed;
        }
        _clock = clock;
    }

    /// <summary>Feeds the cumulative byte count. Returns a status when it is time to report (or <paramref name="force"/>).</summary>
    public TransferStatus? Update(long bytesDone, long bytesTotal, bool force = false)
    {
        lock (_lock)
        {
            var now = _clock();
            if (!_started)
            {
                _started = true;
                _lastSample = now;
                _lastBytes = bytesDone;
            }
            else
            {
                double dt = (now - _lastSample).TotalSeconds;
                if (dt >= 0.02) // ignore bursts closer than 20 ms; they are folded into the next sample
                {
                    double inst = Math.Max(0, bytesDone - _lastBytes) / dt;
                    double alpha = BytesPerSecond == 0 ? 1 : 1 - Math.Exp(-dt / _tau);
                    BytesPerSecond += alpha * (inst - BytesPerSecond);
                    _lastSample = now;
                    _lastBytes = bytesDone;
                }
            }
            bool done = bytesTotal > 0 && bytesDone >= bytesTotal;
            if (!force && !done && _reported && now - _lastReport < _minInterval) return null;
            _lastReport = now;
            _reported = true;
            return new TransferStatus(bytesDone, Math.Max(0, bytesTotal), BytesPerSecond);
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _started = false;
            _reported = false;
            BytesPerSecond = 0;
        }
    }
}
