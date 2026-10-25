using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using YDownloader.Engine;

namespace YDownloader.Downloads;

public sealed class DownloadEntry
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Folder { get; set; } = "";
    public string? Category { get; set; }

    /// <summary>Id der Queue, null = Standard-Queue.</summary>
    public string? Queue { get; set; }
    public string? Checksum { get; set; }
    public DownloadStatus Status { get; set; }
    public string? Error { get; set; }
    public long TotalBytes { get; set; } = -1;
    public long DownloadedBytes { get; set; }
    public DateTime AddedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? FinalPath { get; set; }

    /// <summary>Lief oder wartete beim Beenden: nach dem nächsten Start wieder aufnehmen.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool WasActive { get; set; }

    /// <summary>Über den Debrid-Dienst angelegt oder schon einmal entsperrt: lädt nur noch darüber, nie direkt.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Debrid { get; set; }

    /// <summary>Kennung des Pakets (mehrteiliges Archiv), das beim Hinzufügen gebildet wurde. Alle Teile tragen dieselbe.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageId { get; set; }

    /// <summary>Name des Pakets, wie er im Hinzufügen-Dialog stand.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageName { get; set; }

    /// <summary>Mitgesendete Archivpasswörter (Click'n'Load), nur als DPAPI-Chiffre.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArchivePasswords { get; set; }

    /// <summary>Stand des Entpackens des Pakets (an allen Teilen gleich). „Entpackt“ wird gemerkt, damit ein Neustart nicht noch einmal entpackt.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public YDownloader.Packages.ExtractState Extract { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public YDownloader.Packages.ExtractFailure ExtractReason { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExtractDetail { get; set; }

    // Verlauf für die Detailansicht. Alles optional, ältere Dateien ohne diese Felder bleiben lesbar.

    /// <summary>Ziel nach Weiterleitungen, vom Server bei der letzten Abfrage gemeldet.</summary>
    public string? FinalUrl { get; set; }

    /// <summary>Kann der Server Teilstücke liefern? Null = noch nicht abgefragt.</summary>
    public bool? Resumable { get; set; }

    /// <summary>Null = nicht geprüft, sonst Ergebnis der letzten Prüfung.</summary>
    public bool? ChecksumOk { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }

    /// <summary>Summe der Laufzeiten aller Läufe.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double ActiveSeconds { get; set; }

    /// <summary>Bytes, die über das Netz kamen (ohne das, was bei einem Fortsetzen schon auf der Platte lag).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long TransferredBytes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Retries { get; set; }

    /// <summary>Wiederholungen und Fehler mit Uhrzeit, die letzten <see cref="DownloadItem.MaxLog"/>.</summary>
    public List<HistoryEntry>? Log { get; set; }

    /// <summary>Was jede Verbindung geladen hat. Nur bei fertigen Downloads gespeichert, für die Zusammenfassung in der Detailansicht.</summary>
    public List<ConnectionStat>? Connections { get; set; }
}

/// <summary>
/// Eine Verbindung (Segment) eines Downloads: Bereich in der Datei und was von ihr über das Netz kam.
/// <see cref="Bytes"/> und <see cref="Seconds"/> zählen nur, was die App beobachtet hat: Das Tempo ist also auch nach einem
/// Fortsetzen richtig, die Summe der Bytes kann aber unter der Länge liegen.
/// </summary>
public sealed class ConnectionStat
{
    public long Start { get; set; }

    /// <summary>Länge des Bereichs, -1 = unbekannt (Einzelstrom ohne Größe).</summary>
    public long Length { get; set; }

    public long Bytes { get; set; }

    /// <summary>Zeit, in der diese Verbindung Daten bekam. Wartezeiten und Pausen zählen nicht.</summary>
    public double Seconds { get; set; }
}

/// <summary>Ein Eintrag im Verlauf eines Downloads: eine Wiederholung oder ein Fehler.</summary>
public sealed class HistoryEntry
{
    public DateTime Time { get; set; }
    public string Text { get; set; } = "";

    /// <summary>Der Download ist daran gescheitert (sonst gab es danach einen neuen Versuch).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Fatal { get; set; }

    [JsonIgnore]
    public string TimeText => Format.When(Time, seconds: true);
}

public sealed class DownloadList
{
    public int Version { get; set; } = 1;
    public List<DownloadEntry> Items { get; set; } = new();
}

[JsonSerializable(typeof(DownloadList))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class DownloadJsonContext : JsonSerializerContext
{
}

/// <summary>Liest und schreibt <c>downloads.json</c>. Schreibt atomar und nacheinander im Hintergrund.</summary>
public sealed class DownloadStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private Task _writing = Task.CompletedTask;

    private readonly TimeSpan? _retryPause;

    public DownloadStore(string path, TimeSpan? retryPause = null)
    {
        _path = path;
        _retryPause = retryPause;
    }

    /// <summary>Wie das letzte <see cref="Load"/> ausging. Bei <see cref="DataFileState.Locked"/> darf nicht gespeichert werden.</summary>
    public DataFileState LoadState { get; private set; }

    /// <summary>
    /// Gesperrte Datei: mehrmals neu lesen, sonst <see cref="LoadState"/> = Locked, die Datei bleibt unberührt. Kaputte Datei:
    /// wird als <c>.bad</c> beiseitegelegt, statt die Liste beim nächsten Speichern zu verlieren.
    /// </summary>
    public DownloadList Load()
    {
        LoadState = DataFile.Load(_path, DownloadJsonContext.Default.DownloadList, out var list, _retryPause);
        return list ?? new DownloadList();
    }

    public static byte[] ToJson(DownloadList list) =>
        JsonSerializer.SerializeToUtf8Bytes(list, DownloadJsonContext.Default.DownloadList);

    /// <summary>Serialisiert sofort (Aufruf auf dem UI-Thread), schreibt im Hintergrund.</summary>
    public void SaveInBackground(DownloadList list)
    {
        byte[] data = ToJson(list);
        lock (_gate) _writing = _writing.ContinueWith(_ => TryWrite(data), TaskScheduler.Default);
    }

    /// <summary>Beim Beenden: laufende Schreibvorgänge abwarten, dann direkt schreiben.</summary>
    public void SaveNow(DownloadList list)
    {
        byte[] data = ToJson(list);
        Task pending;
        lock (_gate) pending = _writing;
        pending.Wait();
        TryWrite(data);
    }

    private void TryWrite(byte[] data)
    {
        try
        {
            AtomicFile.WriteAllBytes(_path, data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Beim nächsten Speichern neu versuchen.
        }
    }
}
