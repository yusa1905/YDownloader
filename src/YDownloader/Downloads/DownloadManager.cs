using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using YDownloader.Debrid;
using YDownloader.Engine;
using YDownloader.Packages;
using YDownloader.Power;

namespace YDownloader.Downloads;

/// <summary>ObservableCollection, die viele Einträge auf einmal meldet statt einzeln.</summary>
public class RangeCollection<T> : ObservableCollection<T>
{
    public void AddRange(IEnumerable<T> items)
    {
        foreach (var item in items) Items.Add(item);
        RaiseReset();
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        AddRange(items);
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public sealed class ItemCollection : RangeCollection<DownloadItem>
{
}

/// <summary>
/// Hält die Liste, startet Downloads bis zum Limit und treibt den einen 4-Hz-Takt für alle laufenden Downloads.
/// Alle öffentlichen Methoden laufen auf dem UI-Thread, die Engine im Thread-Pool.
/// </summary>
public sealed class DownloadManager
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private readonly DispatcherTimer _ticker;
    private readonly DispatcherTimer _saveDelay;
    private readonly List<DownloadItem> _running = new();
    private readonly SpeedLimiter _limiter = new();
    private readonly HttpClientPool _pool = new();
    private DownloadStore? _store;
    private HttpDownloader? _engine;
    private HttpDownloader? _prober;
    private Task<AppSettings>? _settingsLoad;
    private int _idleTicks;
    private bool _shuttingDown;

    /// <param name="power">
    /// Die Aktionen für „Nach Abschluss“. Ohne Angabe passiert nichts: Nur <c>App</c> reicht die echten Windows-Aktionen
    /// durch, so löst kein Test und kein Werkzeug versehentlich ein Herunterfahren aus.
    /// </param>
    /// <param name="afterCountdown">Dauer des Countdowns vor der Aktion. Null = 60 s. Kürzer nur für Prüfungen.</param>
    /// <param name="debrid">
    /// Der Debrid-Dienst. Ohne Angabe AllDebrid über das Netz: Nur so kann ein Test oder Werkzeug gegen eine Attrappe laufen,
    /// ohne dass ein echter Aufruf (und ein echter Key) im Spiel ist.
    /// </param>
    public DownloadManager(IPowerActions? power = null, TimeSpan? afterCountdown = null, IDebridProvider? debrid = null)
    {
        _debridProvider = debrid;
        _ticker = new DispatcherTimer(DispatcherPriority.Background) { Interval = TickInterval };
        _ticker.Tick += OnTick;
        _saveDelay = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _saveDelay.Tick += (_, _) =>
        {
            _saveDelay.Stop();
            Store.SaveInBackground(Snapshot());
        };

        AfterCompletion = new AfterCompletion(power ?? NoPowerActions.Instance, afterCountdown)
        {
            StillApplies = () => !HasPendingWork() && !ExtractionBusy,
            BeforeExecute = FlushSave,
        };
        _countdownTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
        _countdownTimer.Tick += (_, _) => AfterCompletion.Tick();
        AfterCompletion.CountdownStarted += (_, _) => _countdownTimer.Start();
        AfterCompletion.Changed += (_, _) =>
        {
            if (!AfterCompletion.IsCountingDown) _countdownTimer.Stop();
        };
    }

    /// <summary>Herunterfahren, Ruhezustand oder Energie sparen, wenn alles oder ein bestimmter Download fertig ist.</summary>
    public AfterCompletion AfterCompletion { get; }

    private readonly DispatcherTimer _countdownTimer;

    /// <summary>Schreibt die Liste sofort, bevor Windows heruntergefahren oder schlafen gelegt wird.</summary>
    private void FlushSave()
    {
        _saveDelay.Stop();
        Store.SaveNow(Snapshot());
    }

    /// <summary>Läuft oder wartet etwas, ohne Rücksicht auf Zeitpläne? Angehaltene Downloads zählen nicht.</summary>
    private bool HasPendingWork() => _running.Count > 0 || Items.Any(i => i.Status == DownloadStatus.Queued);

    /// <summary>Erst beim Laden angelegt: Der Datenordner wird nicht vor dem ersten Fenster angefasst.</summary>
    private DownloadStore Store => _store ??= new DownloadStore(AppPaths.DownloadsFile);

    public ItemCollection Items { get; } = new();
    public LiveStats Stats { get; } = new();
    public DownloadOptions Options { get; } = new();
    public AppSettings Settings { get; private set; } = new();
    public SpeedLimiter Limiter => _limiter;

    /// <summary>Verbindungen, Tempolimit und User-Agent je Host, gemeinsam für alle Downloads.</summary>
    public HostRegistry Hosts { get; } = new();

    private int MaxConcurrent => Math.Max(1, Settings.MaxConcurrent);

    private void ApplyHostSettings()
    {
        Hosts.DefaultMaxConnections = Settings.ConnectionsPerHost;
        Hosts.SetRules(Settings.Hosts.Select(h => h.ToRule()));
    }

    /// <summary>Neue Host-Regeln: gelten sofort, auch für laufende Downloads (Verbindungen ab der nächsten Anfrage).</summary>
    public void UpdateHosts(int connectionsPerHost, List<HostSetting> hosts)
    {
        Settings.ConnectionsPerHost = connectionsPerHost;
        Settings.Hosts = hosts;
        ApplyHostSettings();
        SaveSettings();
    }

    public void SaveSettings()
    {
        byte[] json = Settings.ToJson();
        Task.Run(() => AppSettings.Write(AppPaths.SettingsFile, json));
    }

    /// <summary>Nach Änderungen im Einstellungs-Sheet: speichern, ein höheres Limit sofort nutzen.</summary>
    private FreeSpaceMonitor? _freeSpace;

    /// <summary>Startet die Anzeige des freien Speichers (nach dem ersten Fenster, die Abfrage läuft im Hintergrund).</summary>
    public void StartFreeSpace()
    {
        _freeSpace ??= new FreeSpaceMonitor(() => Settings.EffectiveDownloadFolder, text => Stats.FreeSpaceText = text);
        _freeSpace.Start();
    }

    public void SettingsChanged()
    {
        _freeSpace?.Refresh(); // der Zielordner kann ein anderes Laufwerk sein
        ApplyHostSettings();
        UpdateKeepAwake();
        SaveSettings();
        Pump();
    }

    /// <summary>Windows wach halten, solange etwas läuft (und der Nutzer es nicht abgeschaltet hat).</summary>
    private void UpdateKeepAwake() =>
        KeepAwake.Set(Settings.KeepAwake && (_running.Count > 0 || _extractingId is not null) && !_shuttingDown);

    /// <summary>
    /// Wechselt zwischen Portable (Programmordner) und <c>%AppData%</c>: schreibt Liste und Einstellungen in den neuen
    /// Ordner, erst danach gilt er. Die alten Dateien bleiben liegen. Liefert eine Fehlermeldung oder null.
    /// </summary>
    public async Task<string?> MoveDataAsync(bool portable)
    {
        byte[] downloads = DownloadStore.ToJson(Snapshot());
        byte[] settings = Settings.ToJson();
        try
        {
            await Task.Run(() =>
            {
                AppPaths.SetPortable(portable);
                AtomicFile.WriteAllBytes(AppPaths.DownloadsFile, downloads);
                AtomicFile.WriteAllBytes(AppPaths.SettingsFile, settings);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { AppPaths.SetPortable(!portable); }
            catch (Exception back) when (back is IOException or UnauthorizedAccessException) { }
            return Loc.T(portable ? "Manager.ProgramFolderReadOnly" : "Manager.DataFolderReadOnly", ex.Message);
        }
        _store = null; // der nächste Zugriff nimmt den neuen Pfad
        Changed();     // Änderungen, die während des Schreibens kamen, landen im neuen Ordner
        return null;
    }

    /// <summary>
    /// Liest <c>settings.json</c> schon im Hintergrund, während das Fenster entsteht. Theme und Skalierung müssen
    /// feststehen, bevor es erscheint, sonst blitzt kurz das falsche auf.
    /// </summary>
    public void BeginLoadSettings()
    {
        _settingsLoad = Task.Run(() => AppSettings.Load(AppPaths.SettingsFile));
        // Auch die Sprache hängt an dieser Datei: Der erste Text, der gebraucht wird, wartet darauf.
        Loc.BeginInit(_settingsLoad.ContinueWith(t => t.Result.Language, TaskContinuationOptions.OnlyOnRanToCompletion));
    }

    /// <summary>Wartet auf <see cref="BeginLoadSettings"/>. Das Lesen dauert kürzer als der Bau des Fensters.</summary>
    public void AwaitSettings()
    {
        if (_settingsLoad is null) return;
        Settings = _settingsLoad.GetAwaiter().GetResult();
        _settingsLoad = null;
        if (Settings.CategoriesMigrated) SaveSettings(); // Kennungen der eingebauten Kategorien, einmalig
        ApplyHostSettings();
        ApplyQueueSettings();
    }

    /// <summary>Gemeinsamer Client für alle Downloads, erst beim ersten Bedarf angelegt (hält den Start schlank).</summary>
    private HttpClient Client => _pool.Get(Options);
    private HttpDownloader Engine => _engine ??= new HttpDownloader(Options, _limiter, Client, Hosts);

    /// <summary>Für Name und Größe im Hinzufügen-Dialog: gleicher Client, aber nur ein Versuch und kurze Zeitlimits.</summary>
    public HttpDownloader Prober => _prober ??= new HttpDownloader(new DownloadOptions
    {
        MaxRetries = 1,
        RetryDelay = TimeSpan.FromMilliseconds(500),
        ReadTimeout = TimeSpan.FromSeconds(15),
        ConnectTimeout = Options.ConnectTimeout,
        UserAgent = Options.UserAgent,
    }, null, Client, Hosts);

    public async Task LoadAsync()
    {
        var list = await Task.Run(() => Store.Load());
        Items.AddRange(list.Items.Select(DownloadItem.FromEntry));
        foreach (var item in Items) // eine Queue, die es nicht mehr gibt (settings.json von Hand geändert): Standard-Queue
            if (item.QueueId is not null && Settings.FindQueue(item.QueueId) is null) item.QueueId = null;
        ResolveCategoryRefs();
        Stats.Recount(Items);
        ListChanged?.Invoke(this, EventArgs.Empty);

        // Was beim Beenden lief, läuft weiter, in einer Queue außerhalb ihrer Zeit aber erst zur nächsten Startzeit.
        // Die Markierung gilt nur für diesen Start, auch bei ausgeschalteter Option.
        var resume = Items.Where(i => i.WasActive).ToList();
        foreach (var item in resume) item.WasActive = false;
        if (Settings.ResumeOnStart && resume.Count > 0) StartCore(resume, manual: false);
    }

    /// <summary>Alte Downloads verweisen mit dem Namen auf ihre Kategorie, neue mit dem Schlüssel; hier wird umgestellt.</summary>
    internal void ResolveCategoryRefs()
    {
        bool changed = false;
        foreach (var item in Items)
        {
            if (item.Category is null) continue;
            string? key = Settings.ResolveCategoryRef(item.Category);
            if (key == item.Category) continue;
            item.Category = key;
            changed = true;
        }
        if (changed) Changed();
    }

    // ---------------------------------------------------------------- Befehle

    /// <summary>Hängt neue Downloads an. Gleiche Zielpfade bekommen eine Nummer, damit sich zwei .part nicht stören.</summary>
    public void Add(IEnumerable<DownloadItem> items, bool start)
    {
        foreach (var item in items)
        {
            item.FileName = UniqueName(item.Folder, item.FileName, null);
            item.Status = start ? DownloadStatus.Queued : DownloadStatus.Paused;
            Items.Add(item);
        }
        Pump();
        Changed();
    }

    /// <summary>
    /// Startet auf Wunsch des Nutzers. Das gilt auch, wenn die Queue des Downloads gerade nicht dran ist:
    /// Wer selbst auf Start drückt, will ihn jetzt.
    /// </summary>
    public void Start(IEnumerable<DownloadItem> items) => StartCore(items, manual: true);

    /// <summary>Wie <see cref="Start"/>, aber Queues außerhalb ihrer Zeit halten ihre Downloads zurück.</summary>
    public void StartAll() => StartCore(Items.ToList(), manual: false);

    private void StartCore(IEnumerable<DownloadItem> items, bool manual)
    {
        foreach (var item in items)
        {
            if (item.Status is DownloadStatus.Paused or DownloadStatus.Failed) item.Status = DownloadStatus.Queued;
            else if (item.Status != DownloadStatus.Queued) continue; // läuft schon oder ist fertig
            // Ein Wartender, auf den der Nutzer drückt, soll jetzt los, auch wenn seine Queue noch nicht dran ist.
            if (manual) item.OverrideSchedule = true;
        }
        Pump();
        Changed();
    }

    /// <summary>Laufende Downloads speichern ihren Stand und gehen danach auf „Angehalten“.</summary>
    public void Pause(IEnumerable<DownloadItem> items)
    {
        foreach (var item in items)
        {
            item.OverrideSchedule = false;
            if (item.Status == DownloadStatus.Queued) item.Status = DownloadStatus.Paused;
            else if (item.Status == DownloadStatus.Running) item.Cts?.Cancel();
        }
        Changed();
    }

    public void Toggle(DownloadItem item)
    {
        if (item.Status is DownloadStatus.Running or DownloadStatus.Queued) Pause(new[] { item });
        else Start(new[] { item });
    }

    public void PauseAll() => Pause(Items);

    /// <summary>Entfernt aus der Liste. Teildaten unfertiger Downloads werden gelöscht, fertige Dateien bleiben.</summary>
    public void Remove(IEnumerable<DownloadItem> items)
    {
        foreach (var item in items.ToList())
        {
            if (!Items.Remove(item)) continue;
            AfterCompletion.NotifyRemoved(item.Id);
            if (item.Status == DownloadStatus.Running)
            {
                item.RemoveRequested = true; // gelöscht wird, sobald die Engine die Datei freigibt
                item.Cts?.Cancel();
            }
            else if (item.Status != DownloadStatus.Completed)
            {
                DeletePartialInBackground(item.TargetPath);
            }
        }
        Changed();
    }

    public void RemoveCompleted() => Remove(Items.Where(i => i.Status == DownloadStatus.Completed));

    // ---------------------------------------------------------------- Bearbeiten und Kategorien

    /// <summary>Liste oder Zustände haben sich geändert (für Seitenleiste und Zähler).</summary>
    public event EventHandler? ListChanged;

    /// <summary>Nach jedem 4-Hz-Takt, wenn der Fortschritt der laufenden Downloads übernommen ist (für zusammenfassende Zeilen).</summary>
    public event EventHandler? Ticked;

    public void SetCategory(IEnumerable<DownloadItem> items, string? category)
    {
        foreach (var item in items) item.Category = category;
        Changed();
    }

    /// <summary>Neuer Link. Passen die Teildaten nicht dazu, beginnt die Engine beim nächsten Start von vorn.</summary>
    public void SetUrl(DownloadItem item, string url)
    {
        if (item.Status == DownloadStatus.Running || item.Url == url) return;
        item.Url = url;
        item.ForgetRemote();
        item.ViaDebrid = false; // galt für den alten Link; der neue wird beim Start neu bewertet
        Changed();
    }

    public void SetChecksum(DownloadItem item, string? checksum)
    {
        if (item.Status == DownloadStatus.Running) return;
        item.Checksum = checksum;
        Changed();
    }

    /// <summary>
    /// Benennt um oder verschiebt: fertige Downloads samt Datei, unfertige samt <c>.part</c> und <c>.part.json</c>,
    /// damit das Fortsetzen weiter klappt. Liefert eine Fehlermeldung oder null.
    /// </summary>
    public async Task<string?> RelocateAsync(DownloadItem item, string folder, string fileName)
    {
        if (item.Status == DownloadStatus.Running) return Loc.T("Manager.RunningNoRename");
        string name = FileNames.Sanitize(fileName, item.FileName);
        folder = Path.GetFullPath(folder);
        bool completed = item.Status == DownloadStatus.Completed;
        string from = completed ? item.ShownPath : item.TargetPath;
        if (!completed) name = UniqueName(folder, name, item);
        string to = Path.Combine(folder, name);
        if (string.Equals(from, to, StringComparison.Ordinal)) return null;

        // Wartende nicht mitten im Verschieben starten lassen.
        bool queued = item.Status == DownloadStatus.Queued;
        if (queued) item.Status = DownloadStatus.Paused;
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                if (completed)
                {
                    if (File.Exists(from))
                    {
                        bool sameFile = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
                        if (!sameFile && Path.Exists(to)) throw new DestinationExistsException(to);
                        File.Move(from, to);
                    }
                }
                else
                {
                    MoveIfExists(from + ".part", to + ".part");
                    MoveIfExists(from + ".part.json", to + ".part.json");
                }
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DestinationExistsException)
        {
            if (queued) StartCore(new[] { item }, manual: false);
            return ex is DestinationExistsException
                ? Loc.T("Manager.TargetExists")
                : Loc.T("Manager.MoveFailed", ex.Message);
        }

        item.Folder = folder;
        item.FileName = name;
        if (completed) item.FinalPath = to;
        if (queued) StartCore(new[] { item }, manual: false);
        Changed();
        return null;
    }

    private static void MoveIfExists(string from, string to)
    {
        if (!File.Exists(from)) return;
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) File.Move(from, to);
        else File.Move(from, to, overwrite: true); // Zielname ist in der Liste frei, eine alte .part dort ist verwaist
    }

    /// <summary>Speichert geänderte Kategorien. Umbenennungen und gelöschte Kategorien gehen auf die Downloads über.</summary>
    public void UpdateCategories(List<CategorySetting> categories, IReadOnlyDictionary<string, string?> renames)
    {
        Settings.Categories = categories;
        foreach (var item in Items)
        {
            if (item.Category is null) continue;
            if (renames.TryGetValue(item.Category, out var renamed)) item.Category = renamed;
            else if (Settings.FindCategory(item.Category) is null) item.Category = null;
        }
        SaveSettings();
        Changed();
    }

    /// <summary>Freier Dateiname im Ordner, gemessen an den unfertigen Downloads der Liste.</summary>
    internal string UniqueName(string folder, string name, DownloadItem? self)
    {
        string first = Path.Combine(folder, name);
        string path = first;
        for (int n = 1; IsTaken(path, self); n++) path = HttpDownloader.NumberedName(first, n);
        return Path.GetFileName(path);
    }

    private bool IsTaken(string path, DownloadItem? self)
    {
        foreach (var item in Items)
            if (item != self && item.Status != DownloadStatus.Completed
                && string.Equals(item.TargetPath, path, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // ---------------------------------------------------------------- Ablauf

    /// <summary>
    /// Startet wartende Downloads in Listenreihenfolge, bis die allgemeine Grenze erreicht ist. Eine Queue mit eigener
    /// Grenze bekommt höchstens so viele Plätze, eine Queue außerhalb ihrer Zeit gar keinen (außer für Downloads,
    /// die der Nutzer selbst gestartet hat).
    /// </summary>
    private void Pump()
    {
        if (_shuttingDown) return;
        Dictionary<string, int>? perQueue = null;
        foreach (var item in Items)
        {
            if (_running.Count >= MaxConcurrent) break;
            if (item.Status != DownloadStatus.Queued) continue;

            if (Settings.FindQueue(item.QueueId) is { } queue)
            {
                if (!item.OverrideSchedule && !IsOpen(queue)) continue;
                if (queue.MaxConcurrent > 0)
                {
                    perQueue ??= RunningPerQueue();
                    perQueue.TryGetValue(queue.Id, out int running);
                    if (running >= queue.MaxConcurrent) continue;
                    perQueue[queue.Id] = running + 1;
                }
            }
            Run(item);
        }
    }

    private Dictionary<string, int> RunningPerQueue()
    {
        var counts = new Dictionary<string, int>();
        foreach (var item in _running)
            if (item.QueueId is { } id) counts[id] = counts.GetValueOrDefault(id) + 1;
        return counts;
    }

    // ---------------------------------------------------------------- Queues und Zeitplan

    private readonly Dictionary<string, bool> _queueOpen = new();
    private DispatcherTimer? _scheduleTimer;

    /// <summary>Ist die Queue jetzt dran? Ohne Zeitplan immer, sonst nach dem Stand, den die Zeitplan-Prüfung zuletzt festgehalten hat.</summary>
    private bool IsOpen(QueueSetting queue) =>
        queue.Schedule is null || (_queueOpen.TryGetValue(queue.Id, out bool open) ? open : queue.Schedule.IsOpen(DateTime.Now));

    /// <summary>Beim Laden der Einstellungen: Stand der Zeitpläne festhalten, ohne dass dabei etwas startet oder stoppt.</summary>
    private void ApplyQueueSettings() => ReconcileSchedules(allowStart: false, apply: false);

    /// <summary>
    /// Prüft die Zeitpläne. Öffnet sich eine Queue, starten ihre angehaltenen Downloads (nur bei <paramref name="allowStart"/>),
    /// schließt sie sich, werden ihre Downloads angehalten. Ohne Wechsel kostet das fast nichts und ändert nichts.
    /// </summary>
    private void ReconcileSchedules(bool allowStart, bool apply = true)
    {
        var now = DateTime.Now;
        var scheduled = new HashSet<string>();
        bool changed = false, stoppedAny = false;
        foreach (var queue in Settings.Queues)
        {
            if (queue.Schedule is not { } schedule) continue;
            scheduled.Add(queue.Id);
            bool open = schedule.IsOpen(now);
            bool was = _queueOpen.TryGetValue(queue.Id, out bool known) ? known : open; // neu: kein Wechsel
            _queueOpen[queue.Id] = open;
            if (open == was || !apply) continue;
            if (open)
            {
                if (allowStart) StartQueue(queue);
            }
            else
            {
                stoppedAny |= StopQueue(queue, track: allowStart);
            }
            changed = true;
        }
        foreach (string stale in _queueOpen.Keys.Where(id => !scheduled.Contains(id)).ToList()) _queueOpen.Remove(stale);

        ArmScheduleTimer(now);
        if (!changed) return;
        Pump();
        Changed();

        // Schließt sich ein Zeitfenster und danach ist nichts mehr offen, gilt das wie „alle fertig“, damit die Nachtqueue
        // am Morgen den Rechner ausschalten kann. Nur beim Takt des Zeitplans, nicht beim Bearbeiten der Queues.
        // Liefen noch Downloads, melden sie ihr Anhalten gleich: Der letzte löst es in RaiseFinished aus.
        if (allowStart && stoppedAny && _stoppedBySchedule.Count == 0 && !HasPendingWork()) NotifyAllDoneAfterExtraction();
    }

    private void StartQueue(QueueSetting queue)
    {
        foreach (var item in Items)
            if (item.QueueId == queue.Id && item.Status == DownloadStatus.Paused) item.Status = DownloadStatus.Queued;
    }

    /// <summary>
    /// Laufende Downloads, die der Zeitplan angehalten hat und die noch nicht fertig angehalten sind (die Engine meldet
    /// den Abbruch erst etwas später). Ist der letzte durch, gilt „nach Abschluss“ als erreicht.
    /// </summary>
    private readonly HashSet<DownloadItem> _stoppedBySchedule = new();

    /// <summary>Hält die laufenden und wartenden Downloads der Queue an. Liefert, ob es welche gab.</summary>
    private bool StopQueue(QueueSetting queue, bool track)
    {
        var active = Items.Where(i => i.QueueId == queue.Id && i.Status is DownloadStatus.Running or DownloadStatus.Queued).ToList();
        if (track)
            foreach (var item in active)
                if (item.Status == DownloadStatus.Running) _stoppedBySchedule.Add(item);
        Pause(active);
        return active.Count > 0;
    }

    /// <summary>Weckt zum nächsten Wechsel, spätestens nach einer Minute (so fängt es Uhrumstellung und Ruhezustand ab).</summary>
    private void ArmScheduleTimer(DateTime now)
    {
        DateTime? next = null;
        foreach (var queue in Settings.Queues)
            if (queue.Schedule?.NextChange(now) is { } change && (next is null || change < next)) next = change;
        if (next is null)
        {
            _scheduleTimer?.Stop();
            return;
        }

        var delay = next.Value - now + TimeSpan.FromMilliseconds(250); // kurz nach dem Wechsel, nicht knapp davor
        if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
        if (delay > TimeSpan.FromMinutes(1)) delay = TimeSpan.FromMinutes(1);
        if (_scheduleTimer is null)
        {
            _scheduleTimer = new DispatcherTimer(DispatcherPriority.Background);
            _scheduleTimer.Tick += (_, _) =>
            {
                _scheduleTimer!.Stop();
                if (!_shuttingDown) ReconcileSchedules(allowStart: true);
            };
        }
        _scheduleTimer.Stop();
        _scheduleTimer.Interval = delay;
        _scheduleTimer.Start();
    }

    /// <summary>Hängt die Downloads in eine andere Queue (null = Standard-Queue). Was schon läuft, läuft weiter.</summary>
    public void SetQueue(IEnumerable<DownloadItem> items, string? queueId)
    {
        if (Settings.FindQueue(queueId) is null) queueId = null;
        foreach (var item in items) item.QueueId = queueId;
        Pump();
        Changed();
    }

    /// <summary>
    /// Speichert geänderte Queues. Gelöschte Queues geben ihre Downloads an die Standard-Queue ab. Ein neues Zeitfenster
    /// hält laufende Downloads an, wenn es gerade geschlossen ist, startet aber nichts von selbst.
    /// </summary>
    public void UpdateQueues(List<QueueSetting> queues)
    {
        Settings.Queues = queues;
        foreach (var item in Items)
            if (item.QueueId is not null && Settings.FindQueue(item.QueueId) is null) item.QueueId = null;
        SaveSettings();
        ReconcileSchedules(allowStart: false);
        Pump();
        Changed();
    }

    /// <summary>Wartet noch etwas, das jetzt laufen dürfte? Wartende in einer Queue außerhalb ihrer Zeit zählen nicht.</summary>
    private bool HasRunnableWork()
    {
        if (_running.Count > 0) return true;
        foreach (var item in Items)
        {
            if (item.Status != DownloadStatus.Queued) continue;
            if (item.OverrideSchedule || Settings.FindQueue(item.QueueId) is not { } queue || IsOpen(queue)) return true;
        }
        return false;
    }

    /// <summary>Schreibt bei wartenden Downloads in geschlossenen Queues dazu, wann es weitergeht.</summary>
    private void UpdateWaitNotes()
    {
        var now = DateTime.Now;
        foreach (var item in Items)
        {
            string? note = null;
            if (item.Status == DownloadStatus.Queued && !item.OverrideSchedule
                && Settings.FindQueue(item.QueueId) is { Schedule: { } schedule } queue && !IsOpen(queue))
            {
                string next = schedule.DescribeNextStart(now);
                note = next.Length > 0 ? Loc.T("Manager.StartsAt", next) : Loc.T("Manager.NoDay");
            }
            item.WaitNote = note;
        }
    }

    private async void Run(DownloadItem item)
    {
        var cts = new CancellationTokenSource();
        item.Cts = cts;
        item.BeginRun();
        _running.Add(item);
        UpdateKeepAwake();
        _idleTicks = 0;
        if (!_ticker.IsEnabled) _ticker.Start();

        string target = item.TargetPath;
        try
        {
            var unlocking = BuildRequestAsync(item, cts.Token);
            item.EngineTask = unlocking; // Beenden wartet auch auf das Entsperren (der Abbruch beendet es sofort)
            var request = await unlocking;
            target = request.DestinationPath;
            item.Phase = null;

            var engine = Engine;
            var task = Task.Run(() => engine.DownloadAsync(request, item, cts.Token));
            item.EngineTask = task;
            item.Complete(await task);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            item.EndRun(DownloadStatus.Paused);
        }
        catch (Exception ex)
        {
            item.Fail(ex);
        }
        finally
        {
            _running.Remove(item);
            UpdateKeepAwake();
            item.Cts = null;
            item.EngineTask = null;
            item.OverrideSchedule = false;
            cts.Dispose();
            if (item.RemoveRequested) DeletePartialInBackground(target);
            Pump();
            RaiseFinished(item);
            Changed();
        }
    }

    // ---------------------------------------------------------------- Debrid

    private DebridService? _debrid;
    private readonly IDebridProvider? _debridProvider;

    /// <summary>Erst beim ersten Bedarf angelegt (hält den Start schlank). Nutzt den gemeinsamen Client.</summary>
    public DebridService Debrid =>
        _debrid ??= new DebridService(_debridProvider ?? new AllDebridProvider(Client), () => Settings.Debrid);

    /// <summary>Speichert die Debrid-Einstellungen. <paramref name="newKey"/> null = Key behalten, <paramref name="removeKey"/> löscht ihn.</summary>
    public void UpdateDebrid(bool enabled, string? newKey, bool removeKey)
    {
        var debrid = Settings.Debrid;
        debrid.Enabled = enabled;
        if (newKey is not null) debrid.ApiKey = newKey;
        else if (removeKey) debrid.ApiKey = null;
        SaveSettings();
    }

    /// <summary>
    /// Baut die Anfrage für die Engine. Ist Debrid an und kennt der Dienst den Hoster, wird der Link zuerst entsperrt
    /// (auf dem UI-Thread gestartet, die Netzarbeit läuft im Hintergrund). Der entsperrte Link ist die Adresse, der
    /// Hoster-Link bleibt die Kennung fürs Fortsetzen. Ein über Debrid angelegter Download lädt nie direkt: Fällt der Dienst
    /// weg, scheitert er hier, und die Engine verwirft seine Teildaten nie und nimmt keine Webseite als Datei.
    /// </summary>
    private async Task<DownloadRequest> BuildRequestAsync(DownloadItem item, CancellationToken ct)
    {
        string url = item.Url;
        string? resumeKey = null;
        bool required = item.ViaDebrid;
        if (Settings.Debrid.Enabled || required)
        {
            item.Phase = Loc.T("Item.Debrid.Unlocking");
            bool resolving = true; // eine verspätete Meldung darf den Text nach dem Entsperren nicht zurückholen
            var waiting = new Progress<DebridWait>(w =>
            {
                if (resolving) item.Phase = w.SecondsLeft >= 0
                    ? Loc.T("Item.Debrid.Delayed", Format.Duration(w.SecondsLeft))
                    : Loc.T("Item.Debrid.DelayedUnknown");
            });
            DebridLink? link;
            try
            {
                link = await Debrid.TryUnlockAsync(url, waiting, ct, required);
            }
            finally
            {
                resolving = false;
            }
            if (link is not null)
            {
                if (!item.ViaDebrid)
                {
                    item.ViaDebrid = true;
                    Changed(); // bald speichern: ab jetzt nie mehr direkt laden
                }
                resumeKey = url;
                url = link.Url;
                item.LogNote(Loc.T("Item.Debrid.Unlocked", Debrid.Provider.Name));
                if (link.Size > 0) item.TotalBytes = link.Size;
                if (link.FileName is { } name && item.DownloadedBytes == 0 && NameIsGuessed(item.FileName, item.Url))
                    item.FileName = UniqueName(item.Folder, FileNames.Sanitize(name, item.FileName), item);
            }
        }
        return new DownloadRequest(url, item.TargetPath, ChecksumText.Parse(item.Checksum), Events: item, ResumeKey: resumeKey,
            ProtectPartial: item.ViaDebrid, RejectHtml: item.ViaDebrid);
    }

    /// <summary>
    /// Der Name kam nur aus der Adresse oder von einer Webseite (Hoster-Links führen zu einer Seite, nicht zur Datei),
    /// der Nutzer hat ihn nicht selbst gewählt. Dann darf der Name des Dienstes ihn ersetzen.
    /// </summary>
    internal static bool NameIsGuessed(string fileName, string url)
    {
        if (Path.GetExtension(fileName).ToLowerInvariant() is "" or ".html" or ".htm" or ".php" or ".asp" or ".aspx") return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || FileNames.FromUrl(uri) is not { } derived) return false;
        string name = System.Text.RegularExpressions.Regex.Replace(fileName, @" \(\d+\)(?=\.[^.]*$|$)", "");
        return string.Equals(name, FileNames.Sanitize(derived), StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, FileNames.Sanitize(FileNames.StripPageSuffix(derived)), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ein Download ist fertig oder fehlgeschlagen. Der zweite Wert zählt die fertigen der Gruppe, wenn danach nichts mehr
    /// läuft oder wartet, sonst 0. Anhalten und Entfernen melden nichts.
    /// </summary>
    public event Action<DownloadItem, int>? ItemFinished;

    private int _batchCompleted;

    private void RaiseFinished(DownloadItem item)
    {
        bool lastStopped = _stoppedBySchedule.Remove(item) && _stoppedBySchedule.Count == 0;
        if (item.Status == DownloadStatus.Completed) _batchCompleted++;
        bool idle = !HasRunnableWork();
        int batch = idle ? _batchCompleted : 0;
        if (idle) _batchCompleted = 0;
        if (item.Status is DownloadStatus.Completed or DownloadStatus.Failed && !_shuttingDown && !item.RemoveRequested)
        {
            ItemFinished?.Invoke(item, batch);

            // Zuerst das Entpacken planen: „Nach Abschluss“ wartet darauf, auch wenn es erst jetzt beginnt.
            if (item.Status == DownloadStatus.Completed) ScheduleExtraction(item, manual: false);

            // „Nach Abschluss“ löst nur ein fertiger oder gescheiterter Download aus, nie ein Anhalten oder Entfernen.
            // Angehaltene zählen nicht als offen, wartende (auch auf einen Zeitplan) schon.
            if (item.Status == DownloadStatus.Completed) NotifyCompletedAfterExtraction(item.Id);
            if (!HasPendingWork()) NotifyAllDoneAfterExtraction();
        }
        else if (lastStopped && !_shuttingDown && !HasPendingWork())
        {
            NotifyAllDoneAfterExtraction(); // der Zeitplan hat zuletzt angehalten, danach ist nichts mehr offen
        }
    }

    // ---------------------------------------------------------------- Entpacken

    private readonly List<string> _extractQueue = new();   // Paket-Ids, die warten
    private readonly List<string> _completedWaiting = new();
    private string? _extractingId;
    private CancellationTokenSource? _extractCts;
    private Task? _extractTask;
    private bool _allDoneWaiting;

    /// <summary>Entpackt gerade ein Paket oder wartet eins auf seinen Platz. „Nach Abschluss“ wartet darauf.</summary>
    private bool ExtractionBusy => _extractingId is not null || _extractQueue.Count > 0;

    private void NotifyAllDoneAfterExtraction()
    {
        if (ExtractionBusy) _allDoneWaiting = true;
        else AfterCompletion.NotifyAllDone();
    }

    private void NotifyCompletedAfterExtraction(string id)
    {
        if (ExtractionBusy) _completedWaiting.Add(id);
        else AfterCompletion.NotifyCompleted(id);
    }

    /// <summary>
    /// Schlüssel der Gruppe, die zusammen entpackt wird: die Paket-Id, bei einem einzelnen Archiv (kein Paket, siehe
    /// <see cref="IsSingleArchive"/>) die Id des Downloads selbst.
    /// </summary>
    private static string GroupKey(DownloadItem i) => i.PackageId ?? i.Id;

    private List<DownloadItem> GroupOf(string key) => Items.Where(i => GroupKey(i) == key).ToList();

    /// <summary>
    /// Ein einzelnes Archiv (<c>.zip</c>, <c>.rar</c>, <c>.7z</c>) ohne Paket, zu dem kein weiterer Teil in der Liste gehört
    /// (<c>a.rar</c> neben <c>a.r00</c> ist kein einzelnes Archiv).
    /// </summary>
    private bool IsSingleArchive(DownloadItem item) =>
        item.PackageId is null && ExtractPlanner.IsSingleArchive(item.FileName)
        && !Items.Any(o => o != item && ExtractPlanner.IsSiblingPart(item.FileName, o.FileName));

    /// <summary>Zeigt das Menü „Entpacken“ für diesen Download an? Pakete und Dateien, die wie ein einzelnes Archiv heißen.</summary>
    public bool CanOfferExtract(DownloadItem item) => item.PackageId is not null || ExtractPlanner.IsSingleArchive(item.FileName);

    private Readiness ReadinessOf(List<DownloadItem> group)
    {
        var facts = group.Select(i => new PartFacts(i.FinalPath, i.Status == DownloadStatus.Completed, i.Checksum, i.ChecksumOk)).ToList();
        if (group[0].PackageId is not null) return ExtractPlanner.Evaluate(facts);
        return group.Count == 1 && IsSingleArchive(group[0]) ? ExtractPlanner.EvaluateSingle(facts[0]) : Readiness.NotReady;
    }

    /// <summary>Kann das Paket (oder einzelne Archiv) dieses Downloads jetzt entpackt werden: fertig, vollständig, noch nicht entpackt?</summary>
    public bool CanExtract(DownloadItem item)
    {
        string key = GroupKey(item);
        if (_extractQueue.Contains(key)) return false;
        var group = GroupOf(key);
        return group.All(i => i.Extract != ExtractState.Extracting) && !group.All(i => i.Extract == ExtractState.Done)
            && ReadinessOf(group) == Readiness.Ready;
    }

    /// <summary>Menü „Entpacken“: auch bei ausgeschaltetem Auto-Entpacken und nach „7-Zip nötig“ oder einem Fehler.</summary>
    public void ExtractNow(IEnumerable<DownloadItem> items)
    {
        foreach (string key in items.Select(GroupKey).Distinct().ToList())
            if (GroupOf(key).FirstOrDefault() is { } first) ScheduleExtraction(first, manual: true);
    }

    /// <summary>
    /// Ein Download ist fertig: Sind jetzt alle Teile seines Pakets da (und eine gesetzte Prüfsumme bestätigt, keine Lücke),
    /// kommt das Paket in die Warteschlange. Entpackt wird immer nur eins, mit niedriger Priorität.
    /// </summary>
    private void ScheduleExtraction(DownloadItem item, bool manual)
    {
        if (_shuttingDown) return;
        if (!manual && (!Settings.AutoExtract || item.Status != DownloadStatus.Completed)) return;
        if (!CanExtract(item)) return;
        _extractQueue.Add(GroupKey(item));
        PumpExtract();
    }

    /// <summary>Eingegebenes Passwort je Gruppe für den nächsten Versuch. Nur im Speicher, nie gespeichert.</summary>
    private readonly Dictionary<string, (string Password, bool Remember)> _enteredPasswords = new();

    /// <summary>Gibt das Menü „Passwort eingeben …“ für diesen Download? Pakete und Archive, die nicht schon entpackt sind oder gerade entpackt werden.</summary>
    public bool CanEnterPassword(DownloadItem item) =>
        CanOfferExtract(item) && item.Extract is not (ExtractState.Done or ExtractState.Extracting);

    /// <summary>
    /// Das Passwort gilt für den nächsten Versuch jeder Gruppe der Downloads (zuerst probiert) und der Versuch beginnt, sobald das
    /// Paket bereit ist. Klappt es und ist <paramref name="remember"/> gesetzt, kommt es in die Liste der Einstellungen.
    /// </summary>
    public void EnterPassword(IEnumerable<DownloadItem> items, string password, bool remember)
    {
        if (PasswordRules.Clean(password) is not { } clean) return;
        var list = items.Where(CanEnterPassword).ToList();
        foreach (string key in list.Select(GroupKey).Distinct())
            _enteredPasswords[key] = (clean, remember);
        ExtractNow(list);
    }

    private async void PumpExtract()
    {
        if (_extractingId is not null) return;
        while (_extractQueue.Count > 0 && !_shuttingDown)
        {
            string id = _extractQueue[0];
            _extractQueue.RemoveAt(0);
            var group = GroupOf(id);
            if (group.Count == 0) continue;

            var names = group.Select(i => i.FinalPath ?? "").ToList();
            int entry;
            ExtractJob job;
            if (group[0].PackageId is null)
            {
                // Einzelnes Archiv: wie ein Paket mit einem Teil, entpackt in einen Unterordner mit dem Archivnamen.
                entry = 0;
                var type = ExtractPlanner.SingleType(names[0]);
                if (type is null)
                {
                    SetExtract(group, new ExtractResult(ExtractState.Failed, ExtractFailure.Unreadable));
                    continue;
                }
                job = new ExtractJob(names[0], ExtractPlanner.SingleTargetDir(names[0]), type.Value, false, names);
            }
            else
            {
                entry = ExtractPlanner.EntryIndex(names);
                if (entry < 0)
                {
                    SetExtract(group, new ExtractResult(ExtractState.Failed, ExtractFailure.MissingParts));
                    continue;
                }
                var kind = PackageDetector.Parse(Path.GetFileName(names[entry]))!.Kind;
                job = new ExtractJob(names[entry], Path.GetDirectoryName(names[entry])!, ExtractPlanner.TypeOf(kind), group.Count > 1, names);
            }
            string target = job.TargetDir;

            // Passwörter: das eben eingegebene zuerst, dann die mitgesendeten des Pakets, dann die Liste (nach dem Versuch ohne Passwort).
            _enteredPasswords.Remove(id, out var entered);
            var plan = PasswordRules.Plan(
                entered.Password is null ? null : new[] { entered.Password },
                group.SelectMany(i => PasswordRules.Decode(i.ArchivePasswords)),
                Settings.ArchivePasswords.Passwords);
            job = job with { Passwords = plan.OfType<string>().ToList() };

            // Die Suche nach 7-Zip läuft erst im Hintergrund beim Entpacken. Nie im Programm-, Download- oder Paketordner.
            string? configured = Settings.SevenZipPath;
            var env = ToolSearchEnv.Current(new[] { AppContext.BaseDirectory, Settings.EffectiveDownloadFolder, target }
                .Concat(Settings.EffectiveCategories.Select(c => c.Folder ?? "")));
            var extractor = new ArchiveExtractor(
                () => ToolLocator.FindSevenZip(configured, env),
                () => ToolLocator.FindTar(Environment.SystemDirectory, File.Exists));

            _extractingId = id;
            var cts = _extractCts = new CancellationTokenSource();
            SetExtract(group, new ExtractResult(ExtractState.Extracting));
            UpdateKeepAwake();

            ExtractResult? result;
            try
            {
                var task = Task.Run(() => extractor.Run(job, cts.Token));
                _extractTask = task;
                result = await task;
            }
            catch (OperationCanceledException)
            {
                result = null;
            }
            catch (Exception ex)
            {
                result = new ExtractResult(ExtractState.Failed, ExtractFailure.Io, ex.Message.Length > 200 ? ex.Message[..200] : ex.Message);
            }
            finally
            {
                cts.Dispose();
                _extractCts = null;
                _extractTask = null;
                _extractingId = null;
                UpdateKeepAwake();
            }

            if (result is null)
            {
                SetExtract(group, new ExtractResult(ExtractState.None)); // abgebrochen (Beenden): beim nächsten Mal von vorn
                continue;
            }
            if (result.State == ExtractState.Done)
            {
                group[entry].LogNote(Loc.T("Extract.Log.Done", result.Files, target));
                if (entered.Remember && result.UsedPassword is not null && result.UsedPassword == entered.Password)
                    RememberPassword(group[entry], result.UsedPassword);
            }
            SetExtract(group, result);
        }

        if (_shuttingDown || ExtractionBusy) return;
        foreach (string done in _completedWaiting.ToList()) AfterCompletion.NotifyCompleted(done);
        _completedWaiting.Clear();
        if (_allDoneWaiting)
        {
            _allDoneWaiting = false;
            if (!HasPendingWork()) AfterCompletion.NotifyAllDone();
        }
    }

    /// <summary>Das eingegebene Passwort hat gepasst und der Nutzer wollte es merken: an die Liste in den Einstellungen anhängen.</summary>
    private void RememberPassword(DownloadItem logItem, string password)
    {
        var (list, added) = PasswordRules.Remember(Settings.ArchivePasswords.Passwords, password);
        if (added)
        {
            Settings.ArchivePasswords.Passwords = list;
            SaveSettings();
            logItem.LogNote(Loc.T("Extract.Log.PasswordRemembered"));
        }
        else if (!list.Contains(password, StringComparer.Ordinal))
            logItem.LogNote(Loc.T("Extract.Log.PasswordListFull", PasswordRules.MaxCandidates));
    }

    private void SetExtract(List<DownloadItem> group, ExtractResult r)
    {
        foreach (var i in group) i.SetExtract(r.State, r.Failure, r.Detail);
        Changed();
    }

    /// <summary>Der eine Takt für alle: Fortschritt übernehmen, Geschwindigkeit messen, Verlauf fortschreiben.</summary>
    private void OnTick(object? sender, EventArgs e)
    {
        long now = Environment.TickCount64;
        double total = 0;
        long remaining = 0;
        foreach (var item in _running)
        {
            total += item.Tick(now);
            if (item.TotalBytes > 0) remaining += Math.Max(0, item.TotalBytes - item.DownloadedBytes);
        }
        Stats.Push(total, _running.Count, remaining);
        Ticked?.Invoke(this, EventArgs.Empty);

        // Ohne laufende Downloads weiter, bis der Graph auf null ausgelaufen ist, dann ruht der Takt.
        if (_running.Count > 0) _idleTicks = 0;
        else if (++_idleTicks >= SpeedHistory.Capacity) _ticker.Stop();
    }

    private void Changed()
    {
        if (_shuttingDown) return;
        UpdateWaitNotes();
        Stats.Recount(Items);
        ListChanged?.Invoke(this, EventArgs.Empty);
        _saveDelay.Stop();
        _saveDelay.Start();
    }

    private DownloadList Snapshot() => new() { Items = Items.Select(i => i.ToEntry()).ToList() };

    private static void DeletePartialInBackground(string target) => Task.Run(() => DeletePartial(target));

    private static void DeletePartial(string target)
    {
        foreach (string path in new[] { target + ".part", target + ".part.json" })
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Beim Beenden: Engines anhalten (sie speichern ihren Stand), kurz warten, Liste sofort schreiben.</summary>
    public void Shutdown()
    {
        _shuttingDown = true;
        KeepAwake.Set(false);
        _ticker.Stop();
        _saveDelay.Stop();
        _scheduleTimer?.Stop();
        _countdownTimer.Stop();
        _freeSpace?.Stop();

        _extractQueue.Clear();
        _extractCts?.Cancel(); // beendet 7-Zip samt Kindprozessen, der Staging-Ordner wird aufgeräumt
        var running = _running.ToList();
        var tasks = new List<Task>();
        if (_extractTask is { } extracting) tasks.Add(extracting);
        foreach (var item in running)
        {
            item.Cts?.Cancel();
            if (item.EngineTask is { } t) tasks.Add(t);
        }
        try
        {
            Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Abbrüche und Fehler: der Stand liegt in der .part.json
        }
        foreach (var item in running)
        {
            if (item.RemoveRequested) DeletePartial(item.TargetPath);
            else
            {
                item.WasActive = true; // der Status wird gleich „Angehalten“, der nächste Start soll es wissen
                item.EndRun(DownloadStatus.Paused);
            }
        }
        Store.SaveNow(Snapshot());
        _pool.Dispose();
    }
}
