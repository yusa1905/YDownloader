namespace YDownloader.Downloads;

/// <summary>
/// Gleitendes Messfenster für die Geschwindigkeit: Bytes pro Sekunde über die letzten 12 Takte, also 3 Sekunden.
/// Für einen Download und für jede seiner Verbindungen. Wird nur auf dem UI-Thread im Takt benutzt.
/// </summary>
internal sealed class SpeedMeter
{
    private const int Window = 12;

    private readonly long[] _bytes = new long[Window];
    private readonly long[] _ticks = new long[Window];
    private int _count, _head;

    /// <summary>Messung neu beginnen, etwa wenn die Engine neu anläuft.</summary>
    public void Reset() => _count = 0;

    /// <summary>Nimmt den Stand zum Zeitpunkt <paramref name="now"/> (Millisekunden) auf und liefert Bytes/s.</summary>
    public double Sample(long bytes, long now)
    {
        int newest = (_head + Window - 1) % Window;
        if (_count > 0 && bytes < _bytes[newest]) _count = 0; // Engine hat neu begonnen
        _bytes[_head] = bytes;
        _ticks[_head] = now;
        _head = (_head + 1) % Window;
        if (_count < Window) _count++;
        if (_count < 2) return 0;

        newest = (_head + Window - 1) % Window;
        int oldest = (_head + Window - _count) % Window;
        long ms = _ticks[newest] - _ticks[oldest];
        return ms <= 0 ? 0 : (_bytes[newest] - _bytes[oldest]) * 1000.0 / ms;
    }
}
