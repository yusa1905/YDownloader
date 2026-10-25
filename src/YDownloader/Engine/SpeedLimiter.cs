using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace YDownloader.Engine;

/// <summary>Gemeinsamer Token-Bucket für alle Segmente aller Downloads. Rate 0 = unbegrenzt.</summary>
public sealed class SpeedLimiter
{
    private readonly object _lock = new();
    private long _rate;
    private double _tokens;
    private long _lastTicks = Stopwatch.GetTimestamp();

    public SpeedLimiter(long bytesPerSecond = 0) => _rate = Math.Max(0, bytesPerSecond);

    public long BytesPerSecond
    {
        get { lock (_lock) return _rate; }
        set { lock (_lock) { _rate = Math.Max(0, value); _tokens = 0; _lastTicks = Stopwatch.GetTimestamp(); } }
    }

    /// <summary>Wartet, bis <paramref name="bytes"/> Bytes gesendet werden dürfen.</summary>
    public async ValueTask WaitAsync(int bytes, CancellationToken ct)
    {
        TimeSpan delay;
        lock (_lock)
        {
            if (_rate == 0) return;

            long now = Stopwatch.GetTimestamp();
            double elapsed = (now - _lastTicks) / (double)Stopwatch.Frequency;
            _lastTicks = now;

            // Burst höchstens eine Viertelsekunde, damit die Rate nach Pausen nicht überschossen wird.
            double capacity = Math.Max(_rate / 4.0, 16 * 1024);
            _tokens = Math.Min(capacity, _tokens + elapsed * _rate);
            _tokens -= bytes;
            delay = _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / _rate);
        }
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
    }
}
