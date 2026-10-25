using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YDownloader.Engine;
using YDownloader.Packages;

namespace YDownloader.Sheets;

/// <summary>
/// Kopfzeile eines Pakets im Hinzufügen-Dialog (Linkgrabber): Name und Zielordner sind änderbar, das Häkchen wählt alle Teile.
/// Der Ordner folgt dem Namen und dem Zielordner des Dialogs, bis der Benutzer ihn selbst eingibt.
/// </summary>
public sealed class PackageHeader : Observable
{
    private readonly Func<string> _baseFolder;
    private IReadOnlyList<AddCandidate> _members = Array.Empty<AddCandidate>();
    private IReadOnlyList<AddCandidate> _visible = Array.Empty<AddCandidate>();
    private IReadOnlyList<string> _missing = Array.Empty<string>();
    private string _name;
    private string? _folderOverride;

    public PackageHeader(string name, PartKind kind, Func<string> baseFolder)
    {
        _name = name;
        Kind = kind;
        _baseFolder = baseFolder;
        Key = KeyOf(name, kind);
    }

    /// <summary>Gleiche Teile ergeben bei jedem Neuaufbau der Liste denselben Schlüssel, so bleibt das Paket (und was der Benutzer eintrug) bestehen.</summary>
    public static string KeyOf(string name, PartKind kind) => kind + "|" + name.ToLowerInvariant();

    public string Key { get; }

    /// <summary>Kennung für <see cref="Downloads.DownloadItem.PackageId"/>, je Dialog neu.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public PartKind Kind { get; }

    public string Badge => Kind switch
    {
        PartKind.RarPart or PartKind.RarOld => "RAR",
        PartKind.SevenZip => "7Z",
        _ => "ZIP",
    };

    public IReadOnlyList<AddCandidate> Members => _members;

    /// <summary>Paketname, auch Name des Ordners. Leer gilt wie "package".</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (!Set(ref _name, value)) return;
            Raise(nameof(FolderPath));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Der Name, der an die Downloads geht (nie leer).</summary>
    public string EffectiveName => string.IsNullOrWhiteSpace(_name) ? "package" : _name.Trim();

    /// <summary>Ordner, den das Eingabefeld zeigt. Eine Eingabe ersetzt die automatische Wahl, leeren stellt sie wieder her.</summary>
    public string FolderText
    {
        get => _folderOverride ?? AutoFolder;
        set
        {
            string text = (value ?? "").Trim();
            string? next = text.Length == 0 || string.Equals(text, AutoFolder, StringComparison.Ordinal) ? null : text;
            if (next == _folderOverride) return;
            _folderOverride = next; // FolderText wird nicht gemeldet: Das Feld hält gerade die Eingabe
            Raise(nameof(FolderPath));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Zielordner der Teile.</summary>
    public string FolderPath => _folderOverride ?? AutoFolder;

    private string AutoFolder
    {
        get
        {
            // Hat die Kategorie der Teile einen eigenen Ordner, liegt das Paket darin.
            string root = _members.FirstOrDefault()?.Folder ?? _baseFolder();
            return Path.Combine(root, PackageDetector.FolderName(EffectiveName));
        }
    }

    public IReadOnlyList<string> Missing => _missing;

    /// <summary>Alle Teile gewählt = wahr, keines = falsch, sonst unbestimmt. Setzen wählt oder entfernt alle (unbestimmt zählt als „keines“).</summary>
    public bool? IsChecked
    {
        get
        {
            int n = _visible.Count(m => m.IsChecked);
            return n == 0 ? false : n == _visible.Count ? true : null;
        }
        set
        {
            bool on = value == true; // auf einem Teil-Zustand führt der Klick des Dreifach-Häkchens über „unbestimmt“ zu „keines“
            foreach (var m in _visible) m.IsChecked = on;
            Raise(nameof(IsChecked));
        }
    }

    public string Summary
    {
        get
        {
            // Die Zahl zeigt die sichtbaren Teile (der Filter blendet Teile aus, das Paket bleibt, solange einer passt).
            var parts = new List<string>
            {
                _visible.Count < _members.Count
                    ? Loc.T("Package.PartsOf", _visible.Count, _members.Count)
                    : Loc.Plural("Package.Parts", _members.Count),
            };
            long bytes = _visible.Where(m => m.Size >= 0).Sum(m => m.Size);
            if (bytes > 0) parts.Add(Format.Bytes(bytes));
            int offline = _visible.Count(m => m.Check == CheckStatus.Offline);
            if (offline > 0) parts.Add(Loc.Plural("Package.Offline", offline));
            if (_missing.Count > 0) parts.Add(Loc.T("Package.Missing", string.Join(", ", _missing)));
            return string.Join(" · ", parts);
        }
    }

    // Ein einzelner Offline-Teil ist eine Warnung, Fehlerton erst, wenn kein sichtbarer Teil mehr online sein kann.
    public Tone Tone => _visible.Count > 0 && _visible.All(m => m.Check == CheckStatus.Offline) ? Tone.Error
        : _missing.Count > 0 || _visible.Any(m => m.Check == CheckStatus.Offline) ? Tone.Warning
        : Tone.Normal;

    /// <summary>Name oder Ordner wurden geändert.</summary>
    public event EventHandler? Changed;

    /// <param name="visible">Die Teile, die der Filter durchlässt. Häkchen und Auswahl gelten nur für sie.</param>
    internal void SetMembers(IReadOnlyList<AddCandidate> members, IReadOnlyList<AddCandidate> visible, IReadOnlyList<string> missing)
    {
        _members = members;
        _visible = visible;
        _missing = missing;
        Refresh();
    }

    /// <summary>Häkchen, Zusammenfassung und (automatischer) Ordner neu melden, etwa nach Änderungen an den Teilen oder am Zielordner.</summary>
    internal void Refresh()
    {
        Raise(nameof(IsChecked));
        Raise(nameof(Summary));
        Raise(nameof(Tone));
        Raise(nameof(Missing));
        if (_folderOverride is null) Raise(nameof(FolderText));
        Raise(nameof(FolderPath));
    }
}
