using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using YDownloader.Engine;
using YDownloader.Packages;

namespace YDownloader.Downloads;

public enum DownloadStatus
{
    /// <summary>Angehalten oder noch nie gestartet.</summary>
    Paused,
    /// <summary>Wartet auf einen freien Platz.</summary>
    Queued,
    Running,
    Completed,
    Failed,
}

/// <summary>
/// Ein Eintrag der Download-Liste. Die Engine meldet Fortschritt aus dem Thread-Pool über <see cref="IProgress{T}"/>,
/// der Wert wird nur abgelegt. Der zentrale Takt im <see cref="DownloadManager"/> holt ihn auf dem UI-Thread ab.
/// Dasselbe gilt für <see cref="IDownloadEvents"/> (Abfrage-Ergebnis, Wiederholungen): abgelegt und im Takt übernommen.
/// </summary>
public sealed class DownloadItem : Observable, IProgress<DownloadProgress>, IDownloadEvents
{
    private static readonly PropertyChangedEventArgs ProgressArgs = new(nameof(Progress));
    private static readonly PropertyChangedEventArgs FractionArgs = new(nameof(Fraction));
    private static readonly PropertyChangedEventArgs DetailArgs = new(nameof(DetailText));
    private static readonly PropertyChangedEventArgs SpeedArgs = new(nameof(SpeedText));
    private static readonly PropertyChangedEventArgs EtaArgs = new(nameof(EtaText));

    /// <summary>Wie viele Einträge des Verlaufs ein Download behält (die neuesten).</summary>
    internal const int MaxLog = 50;

    private string _url;
    private string _fileName;
    private string _folder;
    private string? _category;
    private string? _queueId;
    private string? _waitNote;
    private string? _checksum;
    private string? _error;
    private string? _finalPath;
    private DownloadStatus _status;
    private long _total = -1;
    private long _done;
    private DateTime? _completedAt;
    private DownloadProgress? _progress;
    private DownloadProgress? _pending;
    private double _speed;
    private double _secondsLeft = double.NaN;

    private SpeedMeter? _meter;
    private bool _firstReport;

    // Verlauf für die Detailansicht, wird mit gespeichert.
    private string? _finalUrl;
    private bool? _resumable;
    private bool? _checksumOk;
    private DateTime? _startedAt, _endedAt;
    private double _activeSeconds;
    private long _transferred;
    private int _retries;
    private List<HistoryEntry>? _log;
    private long _runStart;       // TickCount64 des laufenden Laufs, 0 = keiner
    private long _countFrom = -1; // Stand der Engine beim letzten Zählen der übertragenen Bytes
    private RemoteFileInfo? _probed;
    private ConcurrentQueue<HistoryEntry>? _incoming;
    private DownloadDetail? _watcher;

    // Was jede Verbindung geladen hat, für die Zusammenfassung nach dem Abschluss. Gespeichert wird es nur bei fertigen Downloads.
    private List<ConnectionStat>? _connections;
    private long[]? _connLast; // Stand der Segmente beim letzten Takt, null = neuer Lauf (der erste Stand ist nur der Ausgangswert)
    private long _connAt;      // TickCount64 des letzten Takts

    public DownloadItem(string url, string fileName, string folder)
    {
        _url = url;
        _fileName = fileName;
        _folder = folder;
    }

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime AddedAt { get; init; } = DateTime.Now;

    public string Url { get => _url; set => Set(ref _url, value); }

    public string FileName
    {
        get => _fileName;
        set
        {
            if (!Set(ref _fileName, value)) return;
            Raise(nameof(Badge));
            Raise(nameof(TargetPath));
        }
    }

    public string Folder
    {
        get => _folder;
        set { if (Set(ref _folder, value)) Raise(nameof(TargetPath)); }
    }

    public string? Category { get => _category; set => Set(ref _category, value); }

    /// <summary>Id der Queue, zu der der Download gehört. Null = Standard-Queue.</summary>
    public string? QueueId { get => _queueId; set => Set(ref _queueId, value); }

    /// <summary>Warum ein wartender Download nicht startet (Queue außerhalb ihrer Zeit), sonst null.</summary>
    public string? WaitNote
    {
        get => _waitNote;
        set { if (Set(ref _waitNote, value)) Raise(DetailArgs); }
    }

    /// <summary>Erwartete Prüfsumme als Text, etwa <c>sha256:ab12…</c>.</summary>
    public string? Checksum { get => _checksum; set => Set(ref _checksum, value); }

    public DownloadStatus Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value)) return;
            Raise(DetailArgs);
            Raise(SpeedArgs);
            Raise(EtaArgs);
            Raise(BarHiddenArgs);
            RaiseCompact();
        }
    }

    // ---------------------------------------------------------------- Kompakte Zeile (fertige Downloads)

    /// <summary>Größe der fertigen Datei.</summary>
    public string SizeText => _total >= 0 ? Format.Bytes(_total) : "";

    /// <summary>Wann der Download fertig wurde, leer wenn unbekannt.</summary>
    public string DoneTimeText => _completedAt is { } t ? Format.When(t) : "";

    /// <summary>Zustand des Entpackens („Entpackt“, „Passwort nötig“ …), leer wenn es nichts zu sagen gibt.</summary>
    public string StateText => ExtractText.Describe(_extract, _extractReason, _extractDetail);

    /// <summary>Etwas, das der Nutzer beheben muss (7-Zip fehlt, Passwort, Fehler beim Entpacken).</summary>
    public bool StateIsProblem => _extract is ExtractState.NeedSevenZip or ExtractState.NeedPassword or ExtractState.Failed;

    /// <summary>Paketname für die kompakte Zeile, leer bei einzelnen Dateien.</summary>
    public string PackageTag => string.IsNullOrEmpty(PackageName) ? "" : Loc.T("Item.PackageTag", PackageName);

    private static readonly string[] CompactNames =
        { nameof(SizeText), nameof(DoneTimeText), nameof(StateText), nameof(StateIsProblem), nameof(PackageTag) };

    private void RaiseCompact()
    {
        foreach (string name in CompactNames) Raise(name);
    }

    private static readonly PropertyChangedEventArgs BarHiddenArgs = new(nameof(BarHidden));

    /// <summary>Fertige Zeilen und Fehler ohne geladene Bytes zeigen keinen Balken (die Zeile behält ihre Höhe).</summary>
    public bool BarHidden => _status == DownloadStatus.Completed || (_status == DownloadStatus.Failed && _done == 0);

    public string? Error { get => _error; set { if (Set(ref _error, value)) Raise(DetailArgs); } }

    /// <summary>
    /// Über den Debrid-Dienst angelegt (Hoster-Link im Hinzufügen-Dialog) oder schon einmal darüber entsperrt. Ein solcher
    /// Download lädt nie direkt vom Hoster, der liefert nur eine Webseite: Fällt der Dienst weg, scheitert er, die Teildaten bleiben.
    /// </summary>
    public bool ViaDebrid { get; set; }

    /// <summary>Kennung des Pakets (mehrteiliges Archiv), zu dem der Download beim Hinzufügen gehörte. Null = einzelne Datei.</summary>
    public string? PackageId { get; set; }

    /// <summary>Name des Pakets, wie er beim Hinzufügen stand (nicht der Ordnername).</summary>
    public string? PackageName { get; set; }

    /// <summary>
    /// Passwörter, die beim Hinzufügen mitkamen (Click'n'Load, Feld <c>passwords</c>), mit DPAPI verschlüsselt wie die Liste in den
    /// Einstellungen. Beim Entpacken kommen sie vor der Liste dran. Nie im Klartext gespeichert.
    /// </summary>
    public string? ArchivePasswords { get; set; }

    /// <summary>
    /// Die Zeile steht in der Hauptliste eingerückt unter einem aufgeklappten Paketkopf. Reiner Anzeigezustand, nie gespeichert;
    /// <see cref="PackageGrouping"/> setzt ihn beim Aufbau der Zeilen.
    /// </summary>
    public bool IsPackagePart { get => _isPackagePart; internal set => Set(ref _isPackagePart, value); }

    private bool _isPackagePart;

    /// <summary>Stand des Entpackens des Pakets. An allen Teilen gleich.</summary>
    public ExtractState Extract => _extract;

    public ExtractFailure ExtractReason => _extractReason;

    public string? ExtractDetail => _extractDetail;

    internal void SetExtract(ExtractState state, ExtractFailure reason = ExtractFailure.None, string? detail = null)
    {
        _extract = state;
        _extractReason = reason;
        _extractDetail = detail;
        Raise(DetailArgs);
        RaiseCompact();
    }

    private ExtractState _extract;
    private ExtractFailure _extractReason;
    private string? _extractDetail;

    /// <summary>Tatsächlicher Pfad nach dem Abschluss (kann wegen eines Namenskonflikts umbenannt sein).</summary>
    public string? FinalPath { get => _finalPath; set => Set(ref _finalPath, value); }

    public DateTime? CompletedAt { get => _completedAt; set { if (Set(ref _completedAt, value)) RaiseCompact(); } }

    /// <summary>Größe in Bytes, -1 = unbekannt.</summary>
    public long TotalBytes
    {
        get => _total;
        set { if (Set(ref _total, value)) RaiseTick(); }
    }

    public long DownloadedBytes => _done;

    /// <summary>Letzter Stand der Engine mit den Segmenten, null außerhalb eines Laufs.</summary>
    public DownloadProgress? Progress => _progress;

    public double Fraction => _total > 0 ? Math.Min(1, (double)_done / _total) : 0;
    public double Speed => _speed;

    // ---------------------------------------------------------------- Verlauf

    /// <summary>Ziel nach Weiterleitungen laut letzter Abfrage, null = noch nie abgefragt.</summary>
    public string? FinalUrl => _finalUrl;

    /// <summary>Server liefert Teilstücke (Fortsetzen möglich). Null = noch nie abgefragt.</summary>
    public bool? Resumable => _resumable;

    /// <summary>Null = nicht geprüft.</summary>
    public bool? ChecksumOk => _checksumOk;

    /// <summary>Erster Start dieses Downloads.</summary>
    public DateTime? StartedAt => _startedAt;

    /// <summary>Ende des letzten Laufs (fertig, Fehler oder angehalten). Null während des Laufs.</summary>
    public DateTime? EndedAt => _endedAt;

    public int Retries => _retries;

    /// <summary>Bytes, die über das Netz kamen. Beim Fortsetzen zählt nur der neue Teil.</summary>
    public long TransferredBytes => _transferred;

    /// <summary>Summe der Laufzeiten aller Läufe, der laufende zählt mit.</summary>
    public double ActiveSeconds =>
        _activeSeconds + (_runStart != 0 ? Math.Max(0, Environment.TickCount64 - _runStart) / 1000.0 : 0);

    /// <summary>Durchschnittliche Geschwindigkeit über alle Läufe. 0, solange noch nichts gemessen ist.</summary>
    public double AverageSpeed => ActiveSeconds >= 0.5 ? _transferred / ActiveSeconds : 0;

    public IReadOnlyList<HistoryEntry> Log => _log ?? (IReadOnlyList<HistoryEntry>)Array.Empty<HistoryEntry>();

    /// <summary>Was jede Verbindung geladen hat. Leer, solange nichts beobachtet wurde (auch bei Dateien aus älteren Versionen).</summary>
    public IReadOnlyList<ConnectionStat> ConnectionStats =>
        _connections ?? (IReadOnlyList<ConnectionStat>)Array.Empty<ConnectionStat>();

    public string TargetPath => Path.Combine(_folder, _fileName);
    public string ShownPath => _finalPath ?? TargetPath;

    /// <summary>Dateiendung für das Symbol, höchstens vier Zeichen.</summary>
    public string Badge
    {
        get
        {
            string ext = Path.GetExtension(_fileName).TrimStart('.');
            return ext.Length is > 0 and <= 4 ? ext.ToUpperInvariant() : "";
        }
    }

    public string DetailText => _status switch
    {
        DownloadStatus.Queued when _waitNote is not null => Loc.T("Item.WaitingSchedule", _waitNote),
        DownloadStatus.Queued => _total > 0 ? Loc.T("Item.WaitingSize", Format.Bytes(_total)) : Loc.T("Item.Waiting"),
        DownloadStatus.Running when _phase is not null => _phase,
        DownloadStatus.Running when _done == 0 && _progress is null => Loc.T("Item.Connecting"),
        DownloadStatus.Running => Amount() + Connections(),
        DownloadStatus.Paused when _done > 0 => Loc.T("Item.PausedAmount", Amount()),
        DownloadStatus.Paused => _total > 0 ? Loc.T("Item.ReadySize", Format.Bytes(_total)) : Loc.T("Item.Ready"),
        DownloadStatus.Completed => (_completedAt is { } t
            ? Loc.T("Item.DoneAt", Format.Bytes(_total), Format.When(t))
            : Format.Bytes(_total)) + ExtractText.Suffix(_extract, _extractReason, _extractDetail),
        DownloadStatus.Failed => Loc.T("Item.Failed", _error ?? Loc.T("Item.UnknownError")),
        _ => "",
    };

    public string SpeedText => _status == DownloadStatus.Running && _speed >= 1 ? Format.Speed(_speed) : "";

    public string EtaText => _status == DownloadStatus.Running && !double.IsNaN(_secondsLeft)
        ? Loc.T("Item.Eta", Format.Duration(_secondsLeft))
        : "";

    /// <summary>Name für Screenreader (UI Automation nimmt ToString der Einträge).</summary>
    public override string ToString() => _fileName;

    private string Amount() => _total > 0
        ? Loc.T("Item.Amount", Format.Bytes(_done), Format.Bytes(_total), Format.Percent(Fraction))
        : Loc.T("Item.AmountUnknown", Format.Bytes(_done));

    private string Connections()
    {
        if (_progress?.SegmentLengths is not { } lengths) return "";
        int open = 0;
        for (int i = 0; i < lengths.Length; i++)
            if (_progress.SegmentBytes[i] < lengths[i]) open++;
        return open == 0 ? "" : " · " + Loc.Plural("Item.Connections", open);
    }

    // ---------------------------------------------------------------- Engine und Takt

    /// <summary>
    /// Was vor dem ersten Byte passiert (Link entsperren, auf den Dienst warten). Ersetzt „Verbinde …“ in der Zeile,
    /// null = nichts Besonderes. Nur auf dem UI-Thread setzen.
    /// </summary>
    internal string? Phase
    {
        get => _phase;
        set { if (_phase != value) { _phase = value; Raise(DetailArgs); } }
    }

    private string? _phase;

    /// <summary>Eine Zeile im Verlauf der Detailansicht, die kein Fehler und keine Wiederholung ist.</summary>
    internal void LogNote(string text) => AddLog(new HistoryEntry { Time = DateTime.Now, Text = text });

    internal CancellationTokenSource? Cts;
    internal Task? EngineTask;
    internal bool RemoveRequested;

    /// <summary>
    /// Der Nutzer hat den Download selbst gestartet: Er läuft auch, wenn seine Queue gerade nicht dran ist.
    /// Gilt bis zum Ende des Laufs oder bis zum Anhalten.
    /// </summary>
    internal bool OverrideSchedule;

    /// <summary>
    /// Lief oder wartete beim Beenden (oder beim Absturz). Gilt nur bis zum nächsten Start, danach hängt der Zustand
    /// wieder allein am Status: wer dann angehalten wird, soll nicht noch einmal anlaufen.
    /// </summary>
    internal bool WasActive;

    void IProgress<DownloadProgress>.Report(DownloadProgress value) => Volatile.Write(ref _pending, value);

    void IDownloadEvents.Probed(RemoteFileInfo info) => Volatile.Write(ref _probed, info);

    void IDownloadEvents.Retrying(int segment, int attempt, Exception error, TimeSpan delay)
    {
        string text = error is RemoteFileChangedException
            ? Loc.T("Item.Retry.FileChanged")
            : (segment >= 0 ? Loc.T("Item.Retry.Connection", segment + 1) : "") + ErrorText.Describe(error)
                + " · " + Loc.T("Item.Retry.Attempt", attempt, Format.Duration(delay.TotalSeconds));
        _incoming?.Enqueue(new HistoryEntry { Time = DateTime.Now, Text = text });
    }

    internal void BeginRun()
    {
        Error = null;
        CompletedAt = null;
        FinalPath = null;
        _progress = null;
        _speed = 0;
        _secondsLeft = double.NaN;
        _meter?.Reset();
        _firstReport = true;
        _runStart = Environment.TickCount64;
        _startedAt ??= DateTime.Now;
        _endedAt = null;
        _checksumOk = null;
        if (_extract != ExtractState.None) SetExtract(ExtractState.None); // wird die Datei neu geladen, gilt das Entpacken nicht mehr
        _countFrom = _done; // was schon auf der Platte liegt, kam nicht in diesem Lauf über das Netz
        _connLast = null;
        if (_done == 0) _connections = null; // ein frischer Start beginnt die Zusammenfassung neu, ein Fortsetzen zählt weiter
        _incoming = new ConcurrentQueue<HistoryEntry>();
        Status = DownloadStatus.Running;
        RaiseTick();
        _watcher?.Update(Environment.TickCount64);
    }

    /// <summary>Ein Takt: letzten Stand der Engine übernehmen, Geschwindigkeit und Restzeit messen. Liefert Bytes/s.</summary>
    internal double Tick(long now)
    {
        if (Interlocked.Exchange(ref _pending, null) is { } p)
        {
            Apply(p, now);
            if (_firstReport)
            {
                // Der gespeicherte Stand kann älter sein als die .part.json: nicht als Sprung mitmessen.
                _meter?.Reset();
                _firstReport = false;
            }
        }
        DrainEvents();
        _speed = (_meter ??= new SpeedMeter()).Sample(_done, now);
        _secondsLeft = _total > 0 && _speed >= 1 ? (_total - _done) / _speed : double.NaN;
        RaiseTick();
        _watcher?.Update(now);
        return _speed;
    }

    /// <summary>Nach dem Lauf: letzten Stand übernehmen, Messung beenden.</summary>
    internal void EndRun(DownloadStatus status)
    {
        if (Interlocked.Exchange(ref _pending, null) is { } p) Apply(p, Environment.TickCount64);
        DrainEvents();
        if (_runStart != 0)
        {
            _activeSeconds += Math.Max(0, Environment.TickCount64 - _runStart) / 1000.0;
            _runStart = 0;
        }
        _endedAt = DateTime.Now;
        _incoming = null; // die Engine ist beendet, es kommt nichts mehr
        _phase = null;
        _speed = 0;
        _secondsLeft = double.NaN;
        _meter = null;
        Status = status;
        RaiseTick();
        _watcher?.Update(Environment.TickCount64);
    }

    internal void Complete(DownloadResult result)
    {
        _pending = null;
        _progress = null;
        _transferred += Math.Max(0, result.Bytes - Math.Max(0, _countFrom)); // der Rest nach dem letzten Bericht
        _checksumOk = result.ChecksumVerified ? true : null;
        _total = _done = result.Bytes;
        if (_connections is [{ Length: < 0 } whole]) whole.Length = result.Bytes; // Einzelstrom ohne bekannte Größe
        FinalPath = result.Path;
        FileName = Path.GetFileName(result.Path);
        CompletedAt = DateTime.Now;
        EndRun(DownloadStatus.Completed);
    }

    internal void Fail(Exception error)
    {
        if (error is ChecksumMismatchException) _checksumOk = false;
        string text = ErrorText.Describe(error);
        Error = text;
        DrainEvents(); // erst die Wiederholungen, dann der Fehler, der sie beendet hat
        AddLog(new HistoryEntry { Time = DateTime.Now, Text = text, Fatal = true });
        EndRun(DownloadStatus.Failed);
    }

    /// <summary>Stand nach dem Löschen der Teildaten: alles auf Anfang.</summary>
    internal void ResetProgress()
    {
        _progress = null;
        _done = 0;
        RaiseTick();
    }

    /// <summary>Neuer Link: was die Abfrage über den alten wusste, gilt nicht mehr.</summary>
    internal void ForgetRemote()
    {
        _finalUrl = null;
        _resumable = null;
    }

    /// <summary>Die Detailansicht schaut zu: Sie bekommt jeden Takt und jeden Zustandswechsel gemeldet. Null = niemand.</summary>
    internal void Watch(DownloadDetail? detail) => _watcher = detail;

    private void Apply(DownloadProgress p, long now)
    {
        _progress = p;
        if (_countFrom >= 0 && p.DownloadedBytes > _countFrom) _transferred += p.DownloadedBytes - _countFrom;
        _countFrom = p.DownloadedBytes; // sinkt der Stand, hat die Engine von vorn begonnen: nichts zählen
        _done = p.DownloadedBytes;
        if (p.TotalBytes >= 0) _total = p.TotalBytes;
        CountConnections(p, now);
    }

    /// <summary>
    /// Zählt je Verbindung, was seit dem letzten Takt dazukam, und die Zeit, in der etwas dazukam. Ein neues Längen-Feld
    /// mit anderen Bereichen (neue Datei, neue Aufteilung) beginnt von vorn. Das Ganze sind ein paar Zahlen pro Takt.
    /// </summary>
    private void CountConnections(DownloadProgress p, long now)
    {
        long[] lengths = p.SegmentLengths ?? new[] { p.TotalBytes };
        if (_connections is null || !SameLayout(_connections, lengths))
        {
            _connections = new List<ConnectionStat>(lengths.Length);
            long start = 0;
            foreach (long length in lengths)
            {
                _connections.Add(new ConnectionStat { Start = start, Length = length });
                start += Math.Max(0, length);
            }
            _connLast = null;
        }

        long[] bytes = p.SegmentBytes;
        if (_connLast is null || _connLast.Length != bytes.Length)
            _connLast = new long[bytes.Length]; // neuer Lauf: der erste Stand ist nur der Ausgangswert
        else
        {
            double seconds = Math.Clamp((now - _connAt) / 1000.0, 0, 2); // nach einem Ruhezustand nicht die ganze Pause zählen
            for (int i = 0; i < _connections.Count && i < bytes.Length; i++)
            {
                long grown = bytes[i] - _connLast[i];
                if (grown <= 0) continue; // sinkt der Stand, hat die Verbindung von vorn begonnen: nichts zählen
                _connections[i].Bytes += grown;
                _connections[i].Seconds += seconds;
            }
        }
        bytes.CopyTo(_connLast, 0);
        _connAt = now;
    }

    private static bool SameLayout(List<ConnectionStat> stats, long[] lengths)
    {
        if (stats.Count != lengths.Length) return false;
        for (int i = 0; i < lengths.Length; i++)
            if (stats[i].Length != lengths[i]) return false;
        return true;
    }

    /// <summary>Übernimmt, was die Engine seit dem letzten Mal aus dem Thread-Pool gemeldet hat.</summary>
    private void DrainEvents()
    {
        if (Interlocked.Exchange(ref _probed, null) is { } info)
        {
            _finalUrl = info.FinalUrl.AbsoluteUri;
            _resumable = info.SupportsRanges && info.Size > 0; // ohne bekannte Größe läuft die Engine als Einzelstrom
        }
        if (_incoming is not { } queue) return;
        while (queue.TryDequeue(out var entry))
        {
            _retries++;
            AddLog(entry);
        }
    }

    private void AddLog(HistoryEntry entry)
    {
        var log = _log ??= new List<HistoryEntry>();
        log.Add(entry);
        if (log.Count > MaxLog) log.RemoveRange(0, log.Count - MaxLog);
        _watcher?.LogAdded(entry);
    }

    private void RaiseTick()
    {
        Raise(ProgressArgs);
        Raise(FractionArgs);
        Raise(DetailArgs);
        Raise(SpeedArgs);
        Raise(EtaArgs);
    }

    // ---------------------------------------------------------------- Speichern

    internal DownloadEntry ToEntry() => new()
    {
        Id = Id,
        Url = _url,
        FileName = _fileName,
        Folder = _folder,
        Category = _category,
        Queue = _queueId,
        Checksum = _checksum,
        Status = _status,
        Error = _error,
        TotalBytes = _total,
        DownloadedBytes = _done,
        AddedAt = AddedAt,
        CompletedAt = _completedAt,
        FinalPath = _finalPath,
        WasActive = WasActive || _status is DownloadStatus.Running or DownloadStatus.Queued,
        Debrid = ViaDebrid,
        PackageId = PackageId,
        PackageName = PackageName,
        ArchivePasswords = ArchivePasswords,
        Extract = _extract == ExtractState.Extracting ? ExtractState.None : _extract, // ein laufendes Entpacken überlebt keinen Neustart
        ExtractReason = _extractReason,
        ExtractDetail = _extractDetail,
        FinalUrl = _finalUrl,
        Resumable = _resumable,
        ChecksumOk = _checksumOk,
        StartedAt = _startedAt,
        EndedAt = _endedAt,
        ActiveSeconds = ActiveSeconds, // ein laufender Lauf zählt schon mit, so geht er bei einem Absturz nicht ganz verloren
        TransferredBytes = _transferred,
        Retries = _retries,
        Log = _log is { Count: > 0 } ? new List<HistoryEntry>(_log) : null,
        // Nur bei fertigen Downloads. Nach dem Abschluss ändert sich die Liste nicht mehr, ein neuer Start legt eine neue an.
        Connections = _status == DownloadStatus.Completed && _connections is { Count: > 0 } ? _connections : null,
    };

    /// <summary>Was beim Beenden lief oder wartete, ist nach dem Laden angehalten, <see cref="WasActive"/> merkt es sich.</summary>
    internal static DownloadItem FromEntry(DownloadEntry e) => new(e.Url, e.FileName, e.Folder)
    {
        Id = e.Id,
        AddedAt = e.AddedAt,
        _category = e.Category,
        _queueId = e.Queue,
        _checksum = e.Checksum,
        _status = e.Status is DownloadStatus.Running or DownloadStatus.Queued ? DownloadStatus.Paused : e.Status,
        _error = e.Error,
        _total = e.TotalBytes,
        _done = e.DownloadedBytes,
        _completedAt = e.CompletedAt,
        _finalPath = e.FinalPath,
        WasActive = e.WasActive || e.Status is DownloadStatus.Running or DownloadStatus.Queued,
        ViaDebrid = e.Debrid,
        PackageId = e.PackageId,
        PackageName = e.PackageName,
        ArchivePasswords = e.ArchivePasswords,
        _extract = e.Extract == ExtractState.Extracting ? ExtractState.None : e.Extract,
        _extractReason = e.ExtractReason,
        _extractDetail = e.ExtractDetail,
        _finalUrl = e.FinalUrl,
        _resumable = e.Resumable,
        _checksumOk = e.ChecksumOk,
        _startedAt = e.StartedAt,
        _endedAt = e.EndedAt,
        _activeSeconds = e.ActiveSeconds,
        _transferred = e.TransferredBytes,
        _retries = e.Retries,
        _log = e.Log is { Count: > 0 } ? e.Log : null,
        _connections = e.Status == DownloadStatus.Completed && e.Connections is { Count: > 0 } ? e.Connections : null,
    };
}
