using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using YDownloader.Debrid;
using YDownloader.Engine;
using YDownloader.Packages;

namespace YDownloader;

public enum ThemeMode
{
    /// <summary>Folgt der Windows-Einstellung.</summary>
    System,
    Light,
    Dark,
}

public enum CloseAction
{
    /// <summary>Schließen beendet das Programm.</summary>
    Exit,
    /// <summary>Schließen blendet nur das Fenster aus, Downloads laufen im Tray weiter.</summary>
    Tray,
}

/// <summary>Einstellungen in <c>settings.json</c>, bearbeitet im Einstellungs-Sheet.</summary>
public sealed class AppSettings
{
    public const int MinScale = 50, MaxScale = 300;

    /// <summary>Standard-Zielordner. Leer = Downloads-Ordner des Benutzers.</summary>
    public string? DownloadFolder { get; set; }

    public int MaxConcurrent { get; set; } = 3;

    /// <summary>Gleichzeitige Verbindungen zu einem Host, über alle Downloads. 0 = unbegrenzt.</summary>
    public int ConnectionsPerHost { get; set; } = 16;

    /// <summary>Sonderregeln je Host (Verbindungen, Tempo, User-Agent).</summary>
    public List<HostSetting> Hosts
    {
        get => _hosts;
        set => _hosts = value ?? new(); // ein "hosts": null von Hand soll nichts kippen
    }

    private List<HostSetting> _hosts = new();

    /// <summary>Zusätzliche Queues. Die Standard-Queue gibt es immer und steht nicht in der Liste.</summary>
    public List<QueueSetting> Queues
    {
        get => _queues;
        set => _queues = value ?? new();
    }

    private List<QueueSetting> _queues = new();

    public QueueSetting? FindQueue(string? id) => id is null ? null : _queues.Find(q => q.Id == id);

    /// <summary>Debrid-Dienst (AllDebrid). Der API-Key steht darin nur verschlüsselt.</summary>
    public DebridSettings Debrid
    {
        get => _debrid;
        set => _debrid = value ?? new();
    }

    private DebridSettings _debrid = new();

    /// <summary>Passwortliste für geschützte Archive. Steht in <c>settings.json</c> nur verschlüsselt.</summary>
    public ArchivePasswordSettings ArchivePasswords
    {
        get => _archivePasswords;
        set => _archivePasswords = value ?? new();
    }

    private ArchivePasswordSettings _archivePasswords = new();

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>Sprache der Oberfläche (etwa <c>de</c>). Null = der Sprache von Windows folgen. Gilt nach dem Neustart.</summary>
    public string? Language { get; set; }

    /// <summary>Skalierung der Oberfläche in Prozent.</summary>
    public int UiScale { get; set; } = 100;

    [JsonIgnore]
    public double UiScaleFactor => Math.Clamp(UiScale, MinScale, MaxScale) / 100.0;

    /// <summary>Was das Schließen des Fensters tut. Ohne sichtbares Tray-Symbol beendet es immer.</summary>
    public CloseAction CloseAction { get; set; } = CloseAction.Tray;

    /// <summary>Windows nicht in den Ruhezustand gehen lassen, solange Downloads laufen. Der Bildschirm darf ausgehen.</summary>
    public bool KeepAwake { get; set; } = true;

    /// <summary>
    /// Click'n'Load-Server auf 127.0.0.1:9666 laufen lassen: Webseiten schicken Links an den Hinzufügen-Dialog. Standard aus.
    /// </summary>
    public bool ClickNLoad { get; set; }

    /// <summary>
    /// Die Zwischenablage auf Links prüfen (ereignisgesteuert, über den Windows-Mechanismus für Änderungen) und einen Hinweis
    /// zeigen, wenn passende Links darin stehen. Standard aus. Der Inhalt wird nie gespeichert oder geloggt.
    /// </summary>
    public bool WatchClipboard { get; set; }

    /// <summary>
    /// Regeln „Automatisch abwählen“ für den Hinzufügen-Dialog: je Eintrag ein Filtertext in der Schreibweise des Filterfelds
    /// (<c>.nfo</c>, <c>sample</c>, <c>size&lt;1MB</c>). Passende Links werden beim Einfügen abgewählt, nie gelöscht.
    /// </summary>
    public List<string> AutoDeselect
    {
        get => _autoDeselect;
        set => _autoDeselect = value ?? new(); // ein "autoDeselect": null von Hand soll nichts kippen
    }

    private List<string> _autoDeselect = new();

    /// <summary>Fertige Pakete (mehrteilige Archive) automatisch in den Paketordner entpacken. Archive werden nie gelöscht.</summary>
    public bool AutoExtract { get; set; } = true;

    /// <summary>Pfad zu <c>7z.exe</c> (oder dem Ordner davon). Leer = suchen, erst beim ersten Entpacken.</summary>
    public string? SevenZipPath { get; set; }

    /// <summary>Downloads, die beim Beenden liefen oder warteten, nach dem Start wieder aufnehmen.</summary>
    public bool ResumeOnStart { get; set; } = true;

    /// <summary>Benachrichtigungen erscheinen nur, solange das Fenster nicht im Vordergrund ist.</summary>
    public bool NotifyCompleted { get; set; } = true;
    public bool NotifyFailed { get; set; } = true;

    /// <summary>Eine Meldung, wenn eine Gruppe von Downloads durch ist (ab zwei fertigen), statt einer für den letzten.</summary>
    public bool NotifyAllDone { get; set; } = true;

    /// <summary>Häkchen „Sofort starten“ im Hinzufügen-Dialog, zuletzt gewählt.</summary>
    public bool StartImmediately { get; set; } = true;

    /// <summary>Zuletzt im Hinzufügen-Dialog gewählte Ordner, neuester zuerst.</summary>
    public List<string> RecentFolders { get; set; } = new();

    /// <summary>Null = noch nie gespeichert, dann gelten die Vorgaben.</summary>
    public List<CategorySetting>? Categories { get; set; }

    /// <summary>
    /// 0 = Kategorien aus einer Version ohne Kennungen (Namen sind Daten, <see cref="MigrateCategories"/> erkennt die
    /// eingebauten); 1 = Kennungen vergeben.
    /// </summary>
    public int CategoriesVersion { get; set; }

    /// <summary>Gesetzt, wenn <see cref="MigrateCategories"/> etwas geändert hat und die Datei neu geschrieben werden sollte.</summary>
    [JsonIgnore]
    public bool CategoriesMigrated { get; private set; }

    [JsonIgnore]
    public List<CategorySetting> EffectiveCategories
    {
        get
        {
            if (Categories is null)
            {
                Categories = CategorySetting.Defaults();
                CategoriesVersion = CategorySetting.CurrentVersion;
            }
            return Categories;
        }
    }

    /// <summary>Erste Kategorie, deren Dateiendungen passen, sonst null. Unabhängig von Namen und Sprache.</summary>
    public CategorySetting? CategoryFor(string fileName)
    {
        string ext = Path.GetExtension(fileName).TrimStart('.');
        if (ext.Length == 0) return null;
        foreach (var c in EffectiveCategories)
            if (c.HasExtension(ext)) return c;
        return null;
    }

    /// <summary>Kategorie zu einem Verweis (<see cref="CategorySetting.Key"/>); alte Verweise per Name finden sie auch.</summary>
    public CategorySetting? FindCategory(string? key)
    {
        if (key is null) return null;
        var list = EffectiveCategories;
        return list.Find(c => string.Equals(c.Key, key, StringComparison.Ordinal))
            ?? list.Find(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Anzeigename zu einem Verweis: Name der Kategorie (umbenannt oder Standardname der Sprache). Eine unbekannte Kennung
    /// (<c>builtin:…</c>) zeigt den Standardnamen der eingebauten Kategorie, nie die Kennung; ganz unbekannt gibt null.
    /// </summary>
    public string? CategoryDisplayName(string? reference)
    {
        if (reference is null) return null;
        var found = FindCategory(reference);
        if (found is not null) return found.Name;
        if (reference.StartsWith(CategorySetting.KeyPrefix, StringComparison.Ordinal))
        {
            string id = reference.Substring(CategorySetting.KeyPrefix.Length);
            var built = Array.Find(CategorySetting.BuiltIns, b => b.Id == id);
            return built.NameKey is { } k ? Loc.T(k) : null;
        }
        return reference;
    }

    /// <summary>
    /// Rechnet den Verweis eines Downloads auf den Schlüssel um. Alte Daten speichern den Anzeigenamen (auch in einer anderen
    /// Sprache, etwa „Musik“); passt nichts, bleibt der Verweis, wie er ist.
    /// </summary>
    public string? ResolveCategoryRef(string? reference)
    {
        if (reference is null) return null;
        var list = EffectiveCategories;
        if (list.Exists(c => string.Equals(c.Key, reference, StringComparison.Ordinal))) return reference;
        var byName = list.Find(c => string.Equals(c.Name, reference, StringComparison.OrdinalIgnoreCase));
        if (byName is not null) return byName.Key;
        string? id = CategorySetting.BuiltInIdForName(reference);
        var builtIn = id is null ? null : list.Find(c => c.Id == id);
        return builtIn?.Key ?? reference;
    }

    /// <summary>
    /// Vergibt den eingebauten Kategorien aus alten Dateien ihre Kennung: Entspricht der gespeicherte Name dem Standardnamen
    /// irgendeiner Sprache, gilt die Kategorie als eingebaute und nicht umbenannt. Erweiterungen und Ordner bleiben unberührt.
    /// Berührt <see cref="Loc"/> nur über die Sprachtabellen, deshalb auch auf dem Hintergrund-Thread beim Laden möglich.
    /// </summary>
    public void MigrateCategories()
    {
        if (CategoriesVersion >= CategorySetting.CurrentVersion) return;
        if (Categories is null) // Vorgaben: kein Name gespeichert, nichts zu übernehmen und nichts zu schreiben
        {
            CategoriesVersion = CategorySetting.CurrentVersion;
            return;
        }
        {
            var taken = new HashSet<string>(Categories.Where(c => c.Id is not null).Select(c => c.Id!), StringComparer.Ordinal);
            foreach (var c in Categories)
            {
                if (c.Id is not null || c.StoredName is null) continue;
                string? id = CategorySetting.BuiltInIdForName(c.StoredName);
                if (id is null || !taken.Add(id)) continue;
                c.Id = id;
                c.StoredName = null;
            }
        }
        CategoriesVersion = CategorySetting.CurrentVersion;
        CategoriesMigrated = true;
    }

    [JsonIgnore]
    public string EffectiveDownloadFolder =>
        string.IsNullOrWhiteSpace(DownloadFolder) ? AppPaths.UserDownloads() : DownloadFolder;

    /// <summary>
    /// Wie das Laden ausging. <see cref="DataFileState.Locked"/>: Die Datei konnte nicht gelesen werden, diese Vorgaben dürfen
    /// sie nicht überschreiben (der Manager speichert dann nicht). <see cref="DataFileState.Corrupt"/>: als <c>.bad</c> gesichert.
    /// </summary>
    [JsonIgnore]
    public DataFileState LoadState { get; private set; }

    /// <summary>
    /// Liest die Einstellungen. Gesperrte Dateien werden mehrmals neu gelesen; scheitert das, bleibt die Datei unberührt und
    /// es gelten die Vorgaben im Speicher. Kaputte Dateien werden vorher als <c>.bad</c> kopiert. Blockiert, nur im Hintergrund aufrufen.
    /// </summary>
    public static AppSettings Load(string path, TimeSpan? retryPause = null)
    {
        var state = DataFile.Load(path, SettingsJsonContext.Default.AppSettings, out var settings, retryPause);
        settings ??= new AppSettings();
        settings.LoadState = state;
        settings.MigrateCategories();
        return settings;
    }

    /// <summary>Auf dem UI-Thread serialisieren, dann mit <see cref="Write"/> im Hintergrund schreiben.</summary>
    public byte[] ToJson() => JsonSerializer.SerializeToUtf8Bytes(this, SettingsJsonContext.Default.AppSettings);

    public static void Write(string path, byte[] json)
    {
        try
        {
            AtomicFile.WriteAllBytes(path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Kategorie: Name, optional eigener Zielordner, Dateiendungen für die automatische Zuordnung.</summary>
public sealed class CategorySetting
{
    private static readonly char[] Separators = { ' ', ',', ';' };

    public const int CurrentVersion = 1;

    /// <summary>Eingebaute Kategorien: feste Kennung, Schlüssel des Standardnamens in den Sprachdateien, Standard-Endungen.</summary>
    internal static readonly (string Id, string NameKey, string Extensions)[] BuiltIns =
    {
        ("videos", "Category.Default.Videos", "mkv mp4 avi mov webm m4v wmv flv ts"),
        ("music", "Category.Default.Music", "mp3 flac m4a aac ogg opus wav"),
        ("archives", "Category.Default.Archives", "zip rar 7z tar gz bz2 xz zst iso"),
        ("programs", "Category.Default.Programs", "exe msi msix appx apk"),
        ("documents", "Category.Default.Documents", "pdf doc docx xls xlsx ppt pptx txt epub"),
        ("pictures", "Category.Default.Pictures", "jpg jpeg png gif webp heic svg"),
    };

    internal const string KeyPrefix = "builtin:";

    /// <summary>Kennung einer eingebauten Kategorie (etwa <c>videos</c>), sonst null. Eine umbenannte behält sie.</summary>
    public string? Id { get; set; }

    /// <summary>Vom Nutzer vergebener Name. Null bei einer eingebauten Kategorie heißt: Standardname der Sprache.</summary>
    [JsonPropertyName("name")]
    public string? StoredName { get; set; }

    /// <summary>
    /// Anzeigename: der eigene, sonst der Standardname in der Sprache der Oberfläche (folgt also dem Sprachwechsel).
    /// Das Setzen auf den Standardnamen einer eingebauten Kategorie gilt als „nicht umbenannt“.
    /// </summary>
    [JsonIgnore]
    public string Name
    {
        get => StoredName ?? DefaultName ?? "";
        set => StoredName = Id is not null && string.Equals(value, DefaultName, StringComparison.OrdinalIgnoreCase) ? null : value;
    }

    [JsonIgnore]
    public string? DefaultName => Id is null ? null : Array.Find(BuiltIns, b => b.Id == Id).NameKey is { } k ? Loc.T(k) : null;

    /// <summary>Wie Downloads und Seitenleiste die Kategorie ansprechen: bei eingebauten die Kennung, sonst der Name.</summary>
    [JsonIgnore]
    public string Key => Id is not null && DefaultName is not null ? KeyPrefix + Id : Name;

    /// <summary>Kennung der eingebauten Kategorie, deren Standardname in irgendeiner Sprache so heißt, sonst null.</summary>
    internal static string? BuiltInIdForName(string name)
    {
        string trimmed = name.Trim();
        foreach (var (id, nameKey, _) in BuiltIns)
            foreach (string text in Loc.TextsInAllLanguages(nameKey))
                if (string.Equals(text, trimmed, StringComparison.OrdinalIgnoreCase)) return id;
        return null;
    }

    /// <summary>Leer = Ordner aus dem Hinzufügen-Dialog.</summary>
    public string? Folder { get; set; }

    /// <summary>Endungen ohne Punkt, getrennt durch Leerzeichen oder Komma, etwa <c>mkv mp4</c>.</summary>
    public string Extensions { get; set; } = "";

    public bool HasExtension(string ext)
    {
        foreach (string e in Extensions.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(e.TrimStart('.', '*'), ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Die eingebauten Kategorien ohne eigenen Namen: Sie zeigen den Standardnamen der jeweiligen Sprache.</summary>
    public static List<CategorySetting> Defaults()
    {
        var list = new List<CategorySetting>(BuiltIns.Length);
        foreach (var (id, _, extensions) in BuiltIns) list.Add(new() { Id = id, Extensions = extensions });
        return list;
    }
}

/// <summary>Regel für einen Host samt Subdomains. 0 beziehungsweise leer heißt: Standard.</summary>
public sealed class HostSetting
{
    public const int MaxConnectionsLimit = 64, MaxSpeedKBps = 1_000_000;

    /// <summary>Etwa <c>example.com</c>. Gilt auch für <c>cdn.example.com</c>.</summary>
    public string Host { get; set; } = "";

    /// <summary>Gleichzeitige Verbindungen zu diesem Host, über alle Downloads. 0 = <see cref="AppSettings.ConnectionsPerHost"/>.</summary>
    public int MaxConnections { get; set; }

    /// <summary>Tempolimit in KB/s (1 KB = 1024 Bytes), über alle Downloads von diesem Host. 0 = unbegrenzt.</summary>
    public int SpeedLimitKBps { get; set; }

    /// <summary>Null = der allgemeine User-Agent.</summary>
    public string? UserAgent { get; set; }

    public HostRule ToRule() => new(Host, MaxConnections, SpeedLimitKBps * 1024L, UserAgent);
}

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}
