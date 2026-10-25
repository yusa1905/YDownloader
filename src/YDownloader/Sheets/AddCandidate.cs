using System;
using System.IO;
using YDownloader.Downloads;
using YDownloader.Engine;

namespace YDownloader.Sheets;

public enum CandidateState { Probing, Ready, Failed }

public enum Tone { Normal, Warning, Error }

/// <summary>Ergebnis des Online-Checks beim Debrid-Dienst. <see cref="None"/>: nicht geprüft (Dienst aus, anderer Hoster, keine Antwort).</summary>
public enum CheckStatus { None, Checking, Online, Offline, Protected }

/// <summary>Ein erkannter Link im Hinzufügen-Dialog. Name und Größe kommen aus der Probe.</summary>
public sealed class AddCandidate : Observable
{
    private string _name;
    private long _size = -1;
    private CandidateState _state = CandidateState.Probing;
    private string? _error;
    private bool _ranges = true;
    private bool _duplicate;
    private bool _exists;
    private bool _checked = true;
    private bool _userChecked;
    private string? _category;
    private string? _folder;
    private string? _debrid;
    private CheckStatus _check;
    private bool _ruleHit;
    private PackageHeader? _package;

    public AddCandidate(string url)
    {
        Url = url;
        Key = LinkParser.Normalize(url);
        Uri.TryCreate(url, UriKind.Absolute, out var uri);
        Host = uri?.Host ?? "";
        _name = FileNames.Sanitize(uri is null ? null : FileNames.FromUrl(uri));
    }

    public string Url { get; }

    /// <summary>Normalisierter Link für die Duplikat-Erkennung.</summary>
    public string Key { get; }

    /// <summary>Normalisiertes Ziel nach Weiterleitungen, sobald die Probe da ist.</summary>
    public string? FinalKey { get; private set; }

    public string Host { get; }

    public string Name
    {
        get => _name;
        private set { if (Set(ref _name, value)) Raise(nameof(Badge)); }
    }

    /// <summary>Wird beim Start über Debrid entsperrt, es gab keine Probe.</summary>
    public bool ViaDebrid => _debrid is not null;

    public long Size => _size;
    public CandidateState State => _state;

    public bool IsChecked
    {
        get => _checked;
        set
        {
            _userChecked = true;
            if (Set(ref _checked, value) && _ruleHit) Raise(nameof(StatusText)); // der Hinweis „durch Regel abgewählt“ hängt am Häkchen
        }
    }

    public bool IsDuplicate => _duplicate;

    public CheckStatus Check => _check;

    /// <summary>Was der Filter und die Abwähl-Regeln von diesem Link sehen: Name, Link, Größe, Host und Online-Status.</summary>
    public FilterItem FilterData => new(_name, Url, _size, Host, _check switch
    {
        CheckStatus.Online => FilterStatus.Online,
        CheckStatus.Offline => FilterStatus.Offline,
        _ => FilterStatus.Unknown,
    });

    /// <summary>Eine Regel „Automatisch abwählen“ passt auf diesen Link.</summary>
    public bool RuleHit => _ruleHit;

    /// <summary>Durch eine Regel abgewählt und noch nicht wieder angehakt.</summary>
    public bool RuleDeselected => _ruleHit && !_checked;

    /// <summary>
    /// Wendet die Regeln an: Ein Treffer wählt die Zeile ab, solange der Benutzer das Häkchen nicht selbst gesetzt hat; der Benutzer kann
    /// sie danach wieder anhaken. Trifft keine Regel mehr (der echte Name kam erst aus der Probe), wird eine nur durch die Regel
    /// abgewählte Zeile wieder angehakt, aber nie eine, die aus anderem Grund (doppelt, offline, nicht erreichbar) abgewählt ist.
    /// </summary>
    internal void ApplyRules(bool match)
    {
        if (match == _ruleHit) return;
        _ruleHit = match;
        if (match) AutoCheck(false);
        else if (!_duplicate && _state != CandidateState.Failed && _check != CheckStatus.Offline) AutoCheck(true);
        RaiseStatus();
        Raise(nameof(RuleHit));
    }

    /// <summary>Das Paket (mehrteiliges Archiv), zu dem dieser Link gehört, sonst null.</summary>
    public PackageHeader? Package
    {
        get => _package;
        internal set
        {
            if (ReferenceEquals(_package, value)) return;
            _package = value;
            Raise(nameof(Package));
            Raise(nameof(InPackage));
        }
    }

    public bool InPackage => _package is not null;

    /// <summary>Die Anfrage an den Dienst läuft: Der Link wartet auf sein Ergebnis.</summary>
    internal void MarkChecking()
    {
        if (_check is not CheckStatus.None) return;
        _check = CheckStatus.Checking;
        RaiseCheck();
    }

    /// <summary>Kein Ergebnis bekommen (Fehler oder der Dienst nannte den Link nicht): zurück auf „nicht geprüft“.</summary>
    internal void CheckUnknown()
    {
        if (_check != CheckStatus.Checking) return;
        _check = CheckStatus.None;
        RaiseCheck();
    }

    /// <summary>
    /// Übernimmt das Ergebnis des Dienstes. Name und Größe ersetzen die Angaben aus der Adresse (Links über Debrid werden nicht
    /// geprobt). Ein Link, der offline ist, wird abgewählt, solange der Benutzer das Häkchen nicht selbst gesetzt hat.
    /// </summary>
    internal void ApplyCheck(Debrid.LinkInfo info)
    {
        if (_debrid is null) return; // inzwischen geprobt, nicht mehr über den Dienst
        switch (info.State)
        {
            case Debrid.LinkState.Online:
                _check = CheckStatus.Online;
                if (info.FileName is { } name) Name = FileNames.Sanitize(name);
                if (info.Size >= 0) _size = info.Size;
                break;
            case Debrid.LinkState.Offline:
                _check = CheckStatus.Offline;
                AutoCheck(false);
                break;
            case Debrid.LinkState.PasswordProtected:
                _check = CheckStatus.Protected;
                break;
            default:
                _check = CheckStatus.None;
                break;
        }
        RaiseCheck();
        RaiseStatus();
    }

    private void RaiseCheck() => Raise(nameof(Check));

    public string? Category => _category;

    /// <summary>Eigener Ordner der Kategorie, sonst null (dann gilt der Ordner des Dialogs).</summary>
    public string? Folder => _folder;

    /// <summary>Anzeigename der Kategorie (nie die Kennung), nur für den Statustext.</summary>
    private string? _categoryLabel;

    internal void SetCategory(string? category, string? folder, string? label = null)
    {
        if (_category == category && _folder == folder && _categoryLabel == label) return;
        _category = category;
        _categoryLabel = label;
        _folder = folder;
        Raise(nameof(Category));
        RaiseStatus();
    }

    public string Badge
    {
        get
        {
            string ext = Path.GetExtension(_name).TrimStart('.');
            return ext.Length is > 0 and <= 4 ? ext.ToUpperInvariant() : "";
        }
    }

    public string SizeText => _size >= 0 ? Format.Bytes(_size) : _state == CandidateState.Probing ? "…" : "";

    public string StatusText
    {
        get
        {
            string note = _state switch
            {
                CandidateState.Probing => Loc.T("Candidate.Probing"),
                CandidateState.Failed => Loc.T("Candidate.Unreachable", _error),
                _ when _check == CheckStatus.Offline => Loc.T("Candidate.Offline"),
                _ when _check == CheckStatus.Protected => Loc.T("Candidate.Protected"),
                _ when _duplicate => Loc.T("Candidate.Duplicate"),
                _ when _exists => Loc.T("Candidate.Exists"),
                _ when RuleDeselected => Loc.T("Candidate.Rule"),
                _ when _check == CheckStatus.Checking => Loc.T("Candidate.Checking"),
                _ when _check == CheckStatus.Online && _debrid is not null => Loc.T("Candidate.OnlineDebrid", _debrid),
                _ when _debrid is not null => Loc.T("Candidate.Debrid", _debrid),
                _ when !_ranges => Loc.T("Candidate.NoResume"),
                _ => "",
            };
            string text = Host;
            if (_categoryLabel is { } shown) text += " · " + shown + (_folder is not null ? " → " + _folder : "");
            return note.Length == 0 ? text : text + " · " + note;
        }
    }

    public Tone Tone => _state == CandidateState.Failed || _check == CheckStatus.Offline ? Tone.Error
        : _check == CheckStatus.Protected || _duplicate || (_state == CandidateState.Ready && !_ranges) ? Tone.Warning
        : Tone.Normal;

    public override string ToString() => _name;

    internal void Apply(RemoteFileInfo info)
    {
        if (_debrid is not null) return; // inzwischen ein Hoster-Link über Debrid: die Probe brachte nur die Webseite
        Name = info.FileName;
        _size = info.Size;
        _ranges = info.SupportsRanges;
        FinalKey = LinkParser.Normalize(info.FinalUrl);
        _state = CandidateState.Ready;
        RaiseStatus();
    }

    /// <summary>
    /// Der Link wird beim Start über den Debrid-Dienst entsperrt, eine Probe beim Hoster brächte nur dessen Webseite. Name
    /// aus der Adresse (ohne angehängtes ".html"), Größe bleibt unbekannt; Name und Größe des Dienstes folgen beim Start.
    /// Geht auch nachträglich, wenn die Hoster-Liste erst nach der Probe kam: Deren Ergebnis zählt dann nicht mehr.
    /// </summary>
    internal void UseDebrid(string providerName)
    {
        _debrid = providerName;
        if (Uri.TryCreate(Url, UriKind.Absolute, out var uri) && FileNames.FromUrl(uri) is { } derived)
            Name = FileNames.Sanitize(FileNames.StripPageSuffix(derived));
        _size = -1;
        _ranges = true;
        _error = null;
        FinalKey = null;
        bool wasFailed = _state == CandidateState.Failed;
        _state = CandidateState.Ready;
        if (wasFailed && !_duplicate) AutoCheck(true); // das Häkchen hatte nur die gescheiterte Probe genommen
        RaiseStatus();
    }

    internal void Fail(string error)
    {
        if (_debrid is not null) return; // siehe Apply
        _error = error;
        _state = CandidateState.Failed;
        AutoCheck(false);
        RaiseStatus();
    }

    internal void MarkDuplicate()
    {
        if (_duplicate) return;
        _duplicate = true;
        AutoCheck(false);
        RaiseStatus();
    }

    internal void SetExists(bool exists)
    {
        if (_exists == exists) return;
        _exists = exists;
        RaiseStatus();
    }

    /// <summary>Häkchen nur ändern, solange der Benutzer es nicht selbst gesetzt hat.</summary>
    private void AutoCheck(bool value)
    {
        if (!_userChecked && Set(ref _checked, value, nameof(IsChecked)) && _ruleHit) Raise(nameof(StatusText));
    }

    private void RaiseStatus()
    {
        Raise(nameof(SizeText));
        Raise(nameof(StatusText));
        Raise(nameof(Tone));
    }
}
