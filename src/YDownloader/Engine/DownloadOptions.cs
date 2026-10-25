using System;

namespace YDownloader.Engine;

/// <summary>Einstellungen für einen Downloader (Client-weit: Proxy, User-Agent, SSL; je Download: Segmente, Retry).</summary>
public sealed class DownloadOptions
{
    public int Segments { get; set; } = 8;
    public long MinSegmentSize { get; set; } = 1024 * 1024;

    public int MaxRetries { get; set; } = 5;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Wie oft der Stand in die <c>.part.json</c> geschrieben wird. Jedes Speichern flusht die Daten auf die Platte.</summary>
    public TimeSpan StateSaveInterval { get; set; } = TimeSpan.FromSeconds(3);

    public string UserAgent { get; set; } = "YDownloader/1.0";

    /// <summary>Proxy-Adresse wie <c>http://host:8080</c>. Leer = Systemeinstellung.</summary>
    public string? Proxy { get; set; }

    public bool IgnoreSslErrors { get; set; }
}

/// <summary>Was passiert, wenn die Zieldatei schon existiert.</summary>
public enum ExistingFileMode
{
    /// <summary>Freien Namen wählen, etwa <c>name (1).ext</c>.</summary>
    Rename,
    Overwrite,
    /// <summary>Schon vor dem ersten Byte mit <see cref="DestinationExistsException"/> abbrechen.</summary>
    Fail,
}

/// <param name="ResumeKey">
/// Beständige Kennung der Quelle für das Fortsetzen (Standard: <paramref name="Url"/>). Bei Debrid-Links ist das der
/// ursprüngliche Hoster-Link: Der entsperrte Link ist bei jedem Entsperren ein anderer, die Datei dahinter dieselbe.
/// Gesetzt, vergleicht die Engine beim Fortsetzen nur Größe und Kennung, nicht ETag und Last-Modified (die ändern sich mit).
/// </param>
/// <param name="ProtectPartial">
/// Vorhandene Teildaten derselben Quelle nie verwerfen: Passt die Datei auf dem Server nicht mehr dazu, scheitert der Download
/// mit <see cref="PartialKeptException"/> und die <c>.part</c> bleibt unverändert. Die <c>.part.json</c> merkt sich das, ein
/// späterer Aufruf ohne das Flag ist genauso geschützt. Eine andere Quelle (der Nutzer hat den Link geändert) darf neu beginnen.
/// </param>
/// <param name="RejectHtml">Eine Antwort mit <c>text/html</c> ist ein Fehler (<see cref="HtmlResponseException"/>), nie die Datei.</param>
public sealed record DownloadRequest(string Url, string DestinationPath, ChecksumSpec? Checksum = null,
    ExistingFileMode IfExists = ExistingFileMode.Rename, IDownloadEvents? Events = null, string? ResumeKey = null,
    bool ProtectPartial = false, bool RejectHtml = false);

/// <summary>Ergebnis von <see cref="HttpDownloader.ProbeAsync"/>.</summary>
/// <param name="FinalUrl">Ziel nach Weiterleitungen.</param>
/// <param name="Size">Größe in Bytes, -1 = unbekannt.</param>
/// <param name="FileName">Vorschlag aus Content-Disposition oder URL, schon für Windows bereinigt.</param>
/// <param name="ETag">ETag samt <c>W/</c> bei schwachen Validatoren.</param>
/// <param name="LastModified">Im Format RFC 1123 ("R").</param>
public sealed record RemoteFileInfo(Uri FinalUrl, long Size, bool SupportsRanges, string FileName, string? ContentType,
    string? ETag, string? LastModified);

/// <summary><paramref name="Path"/> ist der tatsächliche Zielpfad (bei <see cref="ExistingFileMode.Rename"/> evtl. umbenannt).</summary>
/// <param name="ChecksumVerified">Eine Prüfsumme war angegeben und stimmt (bei Abweichung wirft die Engine stattdessen).</param>
public sealed record DownloadResult(string Path, long Bytes, int Segments, bool ChecksumVerified = false);

/// <param name="SegmentLengths">Länge je Segment. Die Segmente liegen lückenlos hintereinander. Null beim Einzelstrom.</param>
public sealed record DownloadProgress(long TotalBytes, long DownloadedBytes, double BytesPerSecond, long[] SegmentBytes,
    long[]? SegmentLengths = null);
