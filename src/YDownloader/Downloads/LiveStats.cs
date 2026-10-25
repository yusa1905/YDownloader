using System;
using System.Collections.Generic;

namespace YDownloader.Downloads;

/// <summary>Gesamtgeschwindigkeit der letzten Minute als Ringpuffer, ein Wert pro Takt.</summary>
public sealed class SpeedHistory
{
    public const int Capacity = 240; // 60 s bei 4 Hz

    private readonly double[] _values = new double[Capacity];
    private int _head;

    public int Count { get; private set; }
    public double Max { get; private set; }

    /// <summary>0 = ältester Wert.</summary>
    public double this[int index] => _values[(_head - Count + index + Capacity) % Capacity];

    public event EventHandler? Changed;

    public void Add(double value)
    {
        _values[_head] = value;
        _head = (_head + 1) % Capacity;
        if (Count < Capacity) Count++;
        double max = 0;
        for (int i = 0; i < Capacity; i++) if (_values[i] > max) max = _values[i];
        Max = max;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Zahlen für die Statusleiste: freier Speicher, Gesamtgeschwindigkeit, Restzeit, Verlauf.</summary>
public sealed class LiveStats : Observable
{
    private string _freeSpace = "";
    private string _speedText = "";
    private string _etaText = "";
    private int _running;

    public SpeedHistory History { get; } = new();

    /// <summary>"1,51 TB frei" am Zielordner, leer solange unbekannt (siehe <see cref="FreeSpaceMonitor"/>).</summary>
    public string FreeSpaceText { get => _freeSpace; internal set => Set(ref _freeSpace, value); }
    public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }
    public string EtaText { get => _etaText; private set => Set(ref _etaText, value); }
    public int RunningCount { get => _running; private set => Set(ref _running, value); }

    internal void Recount(IEnumerable<DownloadItem> items)
    {
        int running = 0;
        foreach (var item in items)
            if (item.Status == DownloadStatus.Running) running++;
        RunningCount = running;
        if (running == 0) { SpeedText = ""; EtaText = ""; }
    }

    internal void Push(double bytesPerSecond, int running, long remainingBytes = 0)
    {
        History.Add(bytesPerSecond);
        SpeedText = running > 0 ? Format.Speed(bytesPerSecond) : "";
        EtaText = running > 0 ? EtaFor(remainingBytes, bytesPerSecond) : "";
    }

    /// <summary>Restzeit aller laufenden Downloads: verbleibende Bytes durch Gesamtgeschwindigkeit, "–" ohne Tempo.</summary>
    internal static string EtaFor(long remainingBytes, double bytesPerSecond) =>
        bytesPerSecond >= 1 ? Loc.T("Stats.Eta", Format.Duration(remainingBytes / bytesPerSecond)) : "–";
}
