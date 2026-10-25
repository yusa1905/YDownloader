using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace YDownloader.Downloads;

/// <summary>
/// Freier Speicher am Zielordner für die Statusleiste. Ein eigener Timer alle 30 s, nicht der 4-Hz-Takt; die Abfrage
/// läuft im Thread-Pool, nur das Ergebnis geht zurück in den UI-Thread. Bei einem Fehler bleibt der Text leer.
/// </summary>
public sealed class FreeSpaceMonitor
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly Func<string> _folder;
    private readonly Action<string> _publish;
    private readonly DispatcherTimer _timer;
    private bool _busy, _stopped;

    /// <param name="folder">Der Zielordner, wird im UI-Thread gelesen.</param>
    /// <param name="publish">Bekommt den fertigen Text (leer bei Fehler), im UI-Thread.</param>
    public FreeSpaceMonitor(Func<string> folder, Action<string> publish)
    {
        _folder = folder;
        _publish = publish;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Interval };
        _timer.Tick += (_, _) => Refresh();
    }

    public void Start()
    {
        _stopped = false;
        _timer.Start();
        Refresh();
    }

    public void Stop()
    {
        _stopped = true;
        _timer.Stop();
    }

    /// <summary>Fragt jetzt ab (etwa nach einem neuen Zielordner). Läuft schon eine Abfrage, wird nichts doppelt gestartet.</summary>
    public async void Refresh()
    {
        if (_busy || _stopped) return;
        _busy = true;
        try
        {
            string folder = _folder();
            long? free = await Task.Run(() => Query(folder));
            if (!_stopped) _publish(Text(free));
        }
        finally { _busy = false; }
    }

    internal static string Text(long? free) => free is { } v ? Loc.T("Stats.Free", Format.Bytes(v)) : "";

    /// <summary>Freier Platz des Laufwerks, auf dem der Ordner liegt (nach Kontingent). Null, wenn nicht ermittelbar.</summary>
    internal static long? Query(string folder)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root)) return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
