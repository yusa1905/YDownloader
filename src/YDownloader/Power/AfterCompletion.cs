using System;

namespace YDownloader.Power;

/// <summary>
/// „Nach Abschluss“: Herunterfahren, Ruhezustand oder Energie sparen, wenn alle Downloads fertig sind oder wenn ein
/// bestimmter Download fertig ist. Davor läuft ein Countdown (Vorgabe 60 s), den man abbrechen kann.
/// Der Plan gilt nur für diese Sitzung und wird nicht gespeichert: Nach einem Neustart soll nie ein Rechner
/// überraschend ausgehen. Die Klasse hängt weder an der Oberfläche noch an Windows: Der Aufrufer bringt die Ereignisse
/// (<see cref="NotifyAllDone"/>, <see cref="NotifyCompleted"/>) und den Takt (<see cref="Tick"/>), die Aktion selbst
/// kommt von <see cref="IPowerActions"/>.
/// </summary>
public sealed class AfterCompletion : Observable
{
    public static readonly TimeSpan DefaultCountdown = TimeSpan.FromSeconds(60);

    /// <summary>Ab dieser Lücke zwischen zwei Takten gilt der Rechner als zwischendurch schlafen gewesen.</summary>
    private static readonly TimeSpan SuspendGap = TimeSpan.FromSeconds(3);

    private readonly IPowerActions _actions;
    private readonly TimeSpan _countdown;
    private readonly Func<DateTime> _now;
    private PowerAction _action;
    private string? _targetId;
    private string? _targetName;
    private DateTime? _deadline;
    private DateTime _lastTick;
    private int _remainingSeconds;
    private string? _error;

    public AfterCompletion(IPowerActions actions, TimeSpan? countdown = null, Func<DateTime>? clock = null)
    {
        _actions = actions;
        _countdown = countdown ?? DefaultCountdown;
        _now = clock ?? (() => DateTime.Now);
    }

    public IPowerActions Actions => _actions;

    /// <summary>Was geschehen soll. <see cref="PowerAction.None"/> = kein Plan.</summary>
    public PowerAction Kind => _action;

    /// <summary>Id des Downloads, nach dem es losgeht. Null = wenn alle fertig sind.</summary>
    public string? TargetId => _targetId;

    public string? TargetName => _targetName;

    public bool IsArmed => _action != PowerAction.None;

    public bool IsCountingDown => _deadline is not null;

    /// <summary>Ganze Sekunden bis zur Aktion, aufgerundet. 0, solange kein Countdown läuft.</summary>
    public int RemainingSeconds
    {
        get => _remainingSeconds;
        private set => Set(ref _remainingSeconds, value);
    }

    /// <summary>Fehler der letzten Aktion (etwa Ruhezustand nicht eingeschaltet), sonst null.</summary>
    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    /// <summary>Kurz für die Statusleiste, leer ohne Plan.</summary>
    public string StatusText => !IsArmed
        ? ""
        : _targetId is null
            ? Loc.T("After.Status.All", PowerText.Name(_action))
            : Loc.T("After.Status.One", _targetName, PowerText.Name(_action));

    /// <summary>
    /// Wird beim Takt des Countdowns gefragt, ob die Bedingung „alle fertig“ noch gilt. Kommt inzwischen neue Arbeit
    /// dazu, bricht der Countdown ab, der Plan bleibt aber für die neue Runde bestehen. Null = gilt immer.
    /// </summary>
    public Func<bool>? StillApplies { get; set; }

    /// <summary>Läuft kurz vor der Aktion: etwa die Liste speichern.</summary>
    public Action? BeforeExecute { get; set; }

    /// <summary>Plan oder Countdown haben sich geändert.</summary>
    public event EventHandler? Changed;

    /// <summary>Ein Countdown beginnt: Die Oberfläche soll ihn zeigen.</summary>
    public event EventHandler? CountdownStarted;

    // ---------------------------------------------------------------- Plan

    /// <summary>Legt den Plan fest (ersetzt einen alten, bricht einen laufenden Countdown ab). <see cref="PowerAction.None"/> löscht ihn.</summary>
    public void Arm(PowerAction action, string? targetId = null, string? targetName = null)
    {
        _deadline = null;
        RemainingSeconds = 0;
        _action = action;
        _targetId = action == PowerAction.None ? null : targetId;
        _targetName = action == PowerAction.None ? null : targetName;
        Error = null;
        RaisePlanChanged();
    }

    /// <summary>Plan und Countdown verwerfen.</summary>
    public void Disarm() => Arm(PowerAction.None);

    /// <summary>Der Nutzer bricht im Countdown ab: Die Aktion entfällt, der Plan auch.</summary>
    public void Cancel()
    {
        if (!IsCountingDown) return;
        Disarm();
    }

    /// <summary>Der Download ist aus der Liste verschwunden: Ein Plan, der auf ihn wartet, ist hinfällig.</summary>
    public void NotifyRemoved(string id)
    {
        if (IsArmed && _targetId == id) Disarm();
    }

    // ---------------------------------------------------------------- Auslöser

    /// <summary>Alle Downloads sind durch (nichts läuft, nichts wartet). Gilt nur für den Plan „wenn alle fertig sind“.</summary>
    public void NotifyAllDone()
    {
        if (IsArmed && _targetId is null && !IsCountingDown) StartCountdown();
    }

    /// <summary>Ein Download ist erfolgreich fertig. Gilt nur für den Plan „nach diesem Download“.</summary>
    public void NotifyCompleted(string id)
    {
        if (IsArmed && _targetId == id && !IsCountingDown) StartCountdown();
    }

    // ---------------------------------------------------------------- Countdown

    private void StartCountdown()
    {
        var now = _now();
        _deadline = now + _countdown;
        _lastTick = now;
        RemainingSeconds = SecondsLeft(now);
        Error = null;
        CountdownStarted?.Invoke(this, EventArgs.Empty);
        RaisePlanChanged();
    }

    /// <summary>
    /// Ein Takt des Countdowns (die Oberfläche ruft ihn etwa viermal pro Sekunde auf). Führt die Aktion aus, wenn die Zeit
    /// um ist. Schlief der Rechner zwischen zwei Takten, verlängert sich der Countdown um die Schlafzeit, damit er nach
    /// dem Aufwachen nicht sofort ausläuft.
    /// </summary>
    public void Tick()
    {
        if (_deadline is not { } deadline) return;
        var now = _now();

        if (now - _lastTick > SuspendGap)
        {
            deadline += now - _lastTick - TimeSpan.FromSeconds(1);
            _deadline = deadline;
        }
        _lastTick = now;

        if (_targetId is null && StillApplies is { } applies && !applies())
        {
            // Neue Downloads sind dazugekommen: nicht ausschalten. Der Plan bleibt für die nächste Runde.
            _deadline = null;
            RemainingSeconds = 0;
            RaisePlanChanged();
            return;
        }

        if (now >= deadline)
        {
            ExecuteNow();
            return;
        }
        RemainingSeconds = SecondsLeft(now);
    }

    /// <summary>Führt die Aktion sofort aus (Knopf „Jetzt“ im Countdown). Der Plan ist danach verbraucht.</summary>
    public void ExecuteNow()
    {
        if (!IsArmed || !IsCountingDown) return; // ohne Countdown nie
        var action = _action;
        // Zuerst verbrauchen: Selbst wenn die Aktion dauert oder der Takt noch einmal kommt, läuft sie nur einmal.
        _deadline = null;
        RemainingSeconds = 0;
        _action = PowerAction.None;
        _targetId = null;
        _targetName = null;
        RaisePlanChanged();

        try
        {
            BeforeExecute?.Invoke();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // Das Sichern der Liste darf die Aktion nicht verhindern.
        }
        Error = _actions.Execute(action);
        if (Error is not null) RaisePlanChanged();
    }

    private int SecondsLeft(DateTime now) =>
        _deadline is { } deadline ? Math.Max(0, (int)Math.Ceiling((deadline - now).TotalSeconds)) : 0;

    private void RaisePlanChanged()
    {
        Raise(nameof(Kind));
        Raise(nameof(TargetId));
        Raise(nameof(TargetName));
        Raise(nameof(IsArmed));
        Raise(nameof(IsCountingDown));
        Raise(nameof(StatusText));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
