using System;
using YDownloader.Engine;

namespace YDownloader.Downloads;

/// <summary>
/// Eine Verbindung (Segment) in der Detailansicht: Bereich in der Datei, Fortschritt, Tempo und ein eigener Verlauf.
/// Die Werte kommen im Takt des <see cref="DownloadManager"/>, nicht von einem eigenen Timer.
/// </summary>
public sealed class ConnectionInfo : Observable
{
    private readonly SpeedMeter _meter = new();
    private double _fraction;
    private string _doneText = "";
    private string _percentText = "";
    private string _speedText = "";
    private bool _isDone;

    /// <param name="length">Länge des Bereichs, -1 = unbekannt (Einzelstrom ohne Größe).</param>
    internal ConnectionInfo(int index, long start, long length, bool wholeFile)
    {
        Length = length;
        Name = (index + 1).ToString();
        (RangeText, RangeTip) = Describe(start, length, wholeFile);
    }

    /// <summary>Bereich in der Datei als Text und als Tooltip mit den genauen Byte-Grenzen.</summary>
    internal static (string Text, string Tip) Describe(long start, long length, bool wholeFile) => wholeFile
        ? (Loc.T("Detail.WholeFile"), Loc.T("Detail.WholeFileTip"))
        : (Format.Bytes(start) + " – " + Format.Bytes(start + length),
            Loc.T("Detail.RangeTip", Format.Exact(start), Format.Exact(start + length - 1)));

    public long Length { get; }
    public string Name { get; }
    public string RangeText { get; }
    public string RangeTip { get; }

    /// <summary>Geschwindigkeit dieser Verbindung der letzten Minute, ein Wert pro Takt.</summary>
    public SpeedHistory History { get; } = new();

    public double Fraction { get => _fraction; private set => Set(ref _fraction, value); }
    public string DoneText { get => _doneText; private set => Set(ref _doneText, value); }
    public string PercentText { get => _percentText; private set => Set(ref _percentText, value); }
    public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }
    public bool IsDone { get => _isDone; private set => Set(ref _isDone, value); }

    internal void Update(long bytes, bool running, long now)
    {
        if (Length > 0 && bytes > Length) bytes = Length;
        bool done = Length > 0 && bytes >= Length;
        double speed = running && !done ? _meter.Sample(bytes, now) : 0;

        Fraction = Length > 0 ? (double)bytes / Length : 0;
        DoneText = Length > 0 ? Loc.T("Detail.ConnProgress", Format.Bytes(bytes), Format.Bytes(Length)) : Loc.T("Item.AmountUnknown", Format.Bytes(bytes));
        PercentText = Length > 0 ? Format.Percent(Fraction) : "";
        SpeedText = done ? Loc.T("Detail.ConnDone") : running ? Format.Speed(speed) : "–";
        IsDone = done;
        if (running) History.Add(speed);
    }
}

/// <summary>Eine Verbindung eines fertigen Downloads: Bereich, Größe, Zeit und durchschnittliches Tempo.</summary>
public sealed class ConnectionSummary
{
    /// <summary>Unter einer halben Sekunde ist das Tempo reiner Zufall des Takts.</summary>
    private const double MinSeconds = 0.5;

    internal ConnectionSummary(int index, ConnectionStat stat, bool wholeFile)
    {
        Name = (index + 1).ToString();
        (RangeText, RangeTip) = ConnectionInfo.Describe(stat.Start, stat.Length, wholeFile);

        string size = Format.Bytes(stat.Length >= 0 ? stat.Length : stat.Bytes);
        bool timed = stat.Seconds >= MinSeconds && stat.Bytes > 0;
        DoneText = timed ? Loc.T("Detail.SummaryIn", size, Format.Duration(stat.Seconds)) : size;
        SpeedText = timed ? Loc.T("Detail.AvgSpeed", Format.Speed(stat.Bytes / stat.Seconds)) : "";
    }

    public string Name { get; }
    public string RangeText { get; }
    public string RangeTip { get; }
    public string DoneText { get; }
    public string SpeedText { get; }
}

/// <summary>
/// Alle Angaben zu einem Download für die Detailansicht. Bindet sich beim Anlegen an den Download und bekommt von dort
/// jeden Takt und jeden Zustandswechsel gemeldet (<see cref="Update"/>). <see cref="Close"/> löst die Bindung wieder.
/// </summary>
public sealed class DownloadDetail : Observable
{
    private readonly DownloadItem _item;
    private long[]? _lengths; // das Längen-Feld der Engine: ein neues bedeutet einen neuen Lauf
    private bool _wholeFile;

    private string _fileName = "", _status = "", _live = "";
    private string _url = "", _finalUrl = "", _server = "", _target = "", _size = "", _resume = "";
    private string _start = "", _end = "", _duration = "", _average = "", _retries = "", _checksum = "", _checksumValue = "";
    private string _connectionsHint = "";
    private bool _isFailed, _checksumFailed, _hasLog, _showHint;

    public DownloadDetail(DownloadItem item)
    {
        _item = item;
        for (int i = item.Log.Count - 1; i >= 0; i--) Log.Add(item.Log[i]); // neueste zuerst
        _hasLog = Log.Count > 0;
        item.Watch(this);
        Update(Environment.TickCount64);
    }

    public RangeCollection<ConnectionInfo> Connections { get; } = new();
    /// <summary>Bei einem fertigen Download statt der Live-Zeilen: was jede Verbindung geleistet hat.</summary>
    public RangeCollection<ConnectionSummary> Summaries { get; } = new();
    /// <summary>Wiederholungen und Fehler, der neueste Eintrag zuerst.</summary>
    public RangeCollection<HistoryEntry> Log { get; } = new();

    // Kopf
    public string FileName { get => _fileName; private set => Set(ref _fileName, value); }
    public string StatusText { get => _status; private set => Set(ref _status, value); }
    public bool IsFailed { get => _isFailed; private set => Set(ref _isFailed, value); }
    public string Url { get => _url; private set => Set(ref _url, value); }
    public string FinalUrlText { get => _finalUrl; private set => Set(ref _finalUrl, value); }
    public string ServerText { get => _server; private set => Set(ref _server, value); }
    public string TargetText { get => _target; private set => Set(ref _target, value); }
    public string SizeText { get => _size; private set => Set(ref _size, value); }
    public string ResumeText { get => _resume; private set => Set(ref _resume, value); }

    // Live
    public string LiveText { get => _live; private set => Set(ref _live, value); }
    public string ConnectionsHint { get => _connectionsHint; private set => Set(ref _connectionsHint, value); }
    public bool HasConnections => Connections.Count > 0;
    public bool HasSummary => Summaries.Count > 0;
    /// <summary>Der Hinweis anstelle der Verbindungen steht nur da, wenn es weder Live-Zeilen noch eine Zusammenfassung gibt.</summary>
    public bool ShowHint { get => _showHint; private set => Set(ref _showHint, value); }

    // Verlauf
    public string StartText { get => _start; private set => Set(ref _start, value); }
    public string EndText { get => _end; private set => Set(ref _end, value); }
    public string DurationText { get => _duration; private set => Set(ref _duration, value); }
    public string AverageText { get => _average; private set => Set(ref _average, value); }
    public string RetriesText { get => _retries; private set => Set(ref _retries, value); }
    public string ChecksumResultText { get => _checksum; private set => Set(ref _checksum, value); }
    public string ChecksumValueText { get => _checksumValue; private set => Set(ref _checksumValue, value); }
    public bool ChecksumFailed { get => _checksumFailed; private set => Set(ref _checksumFailed, value); }
    public bool HasLog { get => _hasLog; private set => Set(ref _hasLog, value); }

    /// <summary>Nach dem Schließen der Ansicht: der Download meldet nichts mehr hierher.</summary>
    public void Close() => _item.Watch(null);

    /// <summary>Ein neuer Verlaufseintrag (Wiederholung oder Fehler) ist dazugekommen.</summary>
    internal void LogAdded(HistoryEntry entry)
    {
        Log.Insert(0, entry);
        if (Log.Count > DownloadItem.MaxLog) Log.RemoveAt(Log.Count - 1);
        HasLog = true;
    }

    /// <summary>Ein Takt oder ein Zustandswechsel des Downloads: alle Werte neu berechnen, gemeldet wird nur, was sich ändert.</summary>
    internal void Update(long now)
    {
        var item = _item;
        bool running = item.Status == DownloadStatus.Running;
        SyncConnections(item.Progress, running, now);
        SyncSummary(item);
        ShowHint = Connections.Count == 0 && Summaries.Count == 0;

        FileName = item.FileName;
        StatusText = item.DetailText;
        IsFailed = item.Status == DownloadStatus.Failed;
        LiveText = running ? Join(item.SpeedText, item.EtaText) : Summaries.Count switch
        {
            0 => "",
            int n => Loc.Plural("Item.Connections", n),
        };
        ConnectionsHint = item.Status switch
        {
            DownloadStatus.Running => Loc.T("Detail.Hint.Connecting"),
            DownloadStatus.Completed => Loc.T("Detail.Hint.Completed"),
            DownloadStatus.Queued => Loc.T("Detail.Hint.Queued"),
            _ => Loc.T("Detail.Hint.None"),
        };

        Url = Readable(item.Url);
        FinalUrlText = item.FinalUrl is null
            ? Loc.T("Detail.FinalNotAsked")
            : item.FinalUrl == item.Url ? Loc.T("Detail.FinalSame") : item.FinalUrl;
        ServerText = Server(item.FinalUrl, item.Url);
        TargetText = item.ShownPath;
        SizeText = item.TotalBytes < 0
            ? Loc.T("Detail.Unknown")
            : $"{Format.Bytes(item.TotalBytes)} ({Format.Exact(item.TotalBytes)})";
        ResumeText = item.Resumable switch
        {
            true => Loc.T("Detail.Resume.Yes"),
            false => Loc.T("Detail.Resume.No"),
            _ => Loc.T("Detail.Resume.Unknown"),
        };

        StartText = item.StartedAt is { } started ? Format.When(started, seconds: true) : Loc.T("Detail.NotStarted");
        EndText = item.Status switch
        {
            DownloadStatus.Running => Loc.T("Detail.End.Running"),
            DownloadStatus.Queued => Loc.T("Detail.End.Queued"),
            DownloadStatus.Completed => When(item.EndedAt ?? item.CompletedAt, null),
            DownloadStatus.Failed => When(item.EndedAt, "Detail.End.Failed"),
            _ => When(item.EndedAt, "Detail.End.Paused"),
        };
        double active = item.ActiveSeconds;
        // Gelaufen, aber unter einer Sekunde: "< 1 s". Nie gestartet (oder aus einer alten Datei ohne Angabe): "–".
        DurationText = active >= 1 ? Format.Duration(active) : item.StartedAt is not null ? Loc.T("Detail.UnderOneSecond") : "–";
        AverageText = item.AverageSpeed > 0 ? Format.Speed(item.AverageSpeed) : "–";
        RetriesText = item.Retries == 0 ? Loc.T("Detail.None") : item.Retries.ToString();

        var spec = ChecksumText.Parse(item.Checksum);
        ChecksumValueText = spec is null ? "" : ChecksumText.ToText(spec);
        ChecksumFailed = item.ChecksumOk == false;
        ChecksumResultText = spec is null
            ? Loc.T("Detail.Checksum.None")
            : item.ChecksumOk switch
            {
                true => Loc.T("Detail.Checksum.Ok"),
                false => Loc.T("Detail.Checksum.Bad"),
                _ => item.Status == DownloadStatus.Completed ? Loc.T("Detail.Checksum.NotChecked") : Loc.T("Detail.Checksum.Pending"),
            };
    }

    /// <summary>
    /// Legt die Verbindungen an, sobald die Engine ihre Segmente gemeldet hat, und führt sie im Takt nach.
    /// Ein neues Längen-Feld ist ein neuer Lauf: dann beginnen Tempo und Graphen wieder leer.
    /// </summary>
    private void SyncConnections(DownloadProgress? p, bool running, long now)
    {
        if (p is null)
        {
            if (Connections.Count > 0)
            {
                Connections.Clear();
                _lengths = null;
                Raise(nameof(HasConnections));
            }
            return;
        }

        bool whole = p.SegmentLengths is null;
        long[] lengths = p.SegmentLengths ?? new[] { p.TotalBytes };
        if (!ReferenceEquals(_lengths, p.SegmentLengths) || Connections.Count != p.SegmentBytes.Length || _wholeFile != whole
            || (whole && Connections.Count == 1 && Connections[0].Length != p.TotalBytes))
        {
            var list = new ConnectionInfo[p.SegmentBytes.Length];
            long start = 0;
            for (int i = 0; i < list.Length; i++)
            {
                long length = i < lengths.Length ? lengths[i] : -1;
                list[i] = new ConnectionInfo(i, start, length, whole);
                start += Math.Max(0, length);
            }
            Connections.ReplaceAll(list);
            _lengths = p.SegmentLengths;
            _wholeFile = whole;
            Raise(nameof(HasConnections));
        }
        for (int i = 0; i < Connections.Count; i++) Connections[i].Update(p.SegmentBytes[i], running, now);
    }

    /// <summary>Ein fertiger Download zeigt, was jede Verbindung geladen hat. Das ändert sich nicht mehr, also nur bei anderer Anzahl neu aufbauen.</summary>
    private void SyncSummary(DownloadItem item)
    {
        var stats = item.Status == DownloadStatus.Completed ? item.ConnectionStats : Array.Empty<ConnectionStat>();
        if (Summaries.Count == stats.Count) return;
        var list = new ConnectionSummary[stats.Count];
        for (int i = 0; i < list.Length; i++) list[i] = new ConnectionSummary(i, stats[i], wholeFile: list.Length == 1);
        Summaries.ReplaceAll(list);
        Raise(nameof(HasSummary));
    }

    /// <summary>Zeitpunkt, mit dem Text <paramref name="key"/> davor (Platzhalter {0}), sonst nur die Zeit. "–" ohne Zeit.</summary>
    private static string When(DateTime? time, string? key) => time is { } t
        ? key is null ? Format.When(t, seconds: true) : Loc.T(key, Format.When(t, seconds: true))
        : "–";

    /// <summary>
    /// Der Link zum Lesen: %20, %C3%A4 usw. als Zeichen. Nur für die Anzeige, Kopieren und Bearbeiten nutzen den echten Link
    /// (<see cref="DownloadItem.Url"/>). Lässt sich der Text nicht dekodieren, bleibt er, wie er ist.
    /// </summary>
    internal static string Readable(string url)
    {
        try { return Uri.UnescapeDataString(url); }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException) { return url; }
    }

    private static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + " · " + b;

    /// <summary>Host (mit Port, wenn nicht der übliche) der finalen Adresse, sonst des Links.</summary>
    private static string Server(string? finalUrl, string url)
    {
        foreach (string? text in new[] { finalUrl, url })
            if (text is not null && Uri.TryCreate(text, UriKind.Absolute, out var uri)) return uri.Authority;
        return "–";
    }
}
