using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using YDownloader.Debrid;
using YDownloader.Engine;

namespace YDownloader.Downloads;

/// <summary>
/// Kurze Fehlertexte für die Liste, in der Sprache der Oberfläche. Die Meldungen der Engine-Ausnahmen sind Entwicklertext:
/// Die Anzeige baut ihren Text aus den Angaben der Ausnahme.
/// </summary>
public static class ErrorText
{
    public static string Describe(Exception ex) => ex switch
    {
        ServerBusyException busy => Loc.T("Error.ServerBusy", (int?)busy.StatusCode ?? 0,
            Math.Max(0, Math.Round(busy.RetryAfter.TotalSeconds))),
        HttpRequestException { StatusCode: { } code } => Status(code),
        HttpRequestException { InnerException: AuthenticationException } => Loc.T("Error.SslInvalid"),
        HttpRequestException h => Socket(h) ?? Loc.T("Error.ConnectionFailed"),
        TimeoutException => Loc.T("Error.Timeout"),
        UriFormatException => Loc.T("Error.InvalidAddress"),
        StorageException { NeededBytes: >= 0 } disk => Loc.T("Error.DiskFull", disk.NeededBytes / (1024 * 1024), disk.FreeBytes / (1024 * 1024)),
        StorageException { InnerException: { } inner } => LocalFile(inner) ?? Loc.T("Error.Storage", inner.Message),
        RemoteFileChangedException => Loc.T("Error.FileChanged"),
        ChecksumMismatchException sum => Loc.T("Error.Checksum", sum.Expected, sum.Actual),
        DestinationExistsException exists => Loc.T("Error.DestinationExists", exists.Path),
        ServerProtocolException protocol => Protocol(protocol),
        PartialKeptException { Conflict: ResumeConflict.SizeChanged, RemoteSize: >= 0, LocalSize: >= 0 } size =>
            Loc.T("Error.Kept.SizeChanged", Format.Bytes(size.RemoteSize), Format.Bytes(size.LocalSize)),
        PartialKeptException { Conflict: ResumeConflict.NoRanges } => Loc.T("Error.Kept.NoRanges"),
        PartialKeptException => Loc.T("Error.Kept.FileChanged"),
        HtmlResponseException { HostsUnavailable: true } => Loc.T("Error.HtmlResponse.NoHosts"),
        HtmlResponseException => Loc.T("Error.HtmlResponse"),
        DebridException debrid => Debrid(debrid),
        StorageException or InvalidDataException => ex.Message,
        // Dateifehler von Windows (HRESULT 0x8007xxxx) sind lokal; was ohne solchen Code abbricht, ist die Verbindung.
        IOException io => LocalFile(io) ?? (IsWin32Error(io) ? Loc.T("Error.Storage", io.Message) : Loc.T("Error.ConnectionAborted")),
        UnauthorizedAccessException => Loc.T("Error.NoAccess"),
        NotSupportedException => Loc.T("Error.Unsupported"),
        _ => ex.Message,
    };

    // Win32-Fehler, bei denen Ordner oder Laufwerk nicht (mehr) da sind: Pfad nicht gefunden, Laufwerk nicht bereit, Netzpfad
    // oder Netzwerkname weg, unerwarteter Netzwerkfehler, Gerät nicht verbunden.
    private static readonly HashSet<uint> TargetGone = new() { 0x03, 0x15, 0x35, 0x3B, 0x40, 0x43, 0x48F };

    private static bool IsWin32Error(Exception ex) => ((uint)ex.HResult & 0xFFFF0000) == 0x80070000;

    /// <summary>Text für einen lokalen Dateifehler, den ein Benutzer beheben kann (Ordner weg, Pfad zu lang, kein Zugriff), sonst null.</summary>
    private static string? LocalFile(Exception ex) => ex switch
    {
        PathTooLongException => Loc.T("Error.PathTooLong"),
        DirectoryNotFoundException or DriveNotFoundException => Loc.T("Error.FolderNotFound"),
        UnauthorizedAccessException => Loc.T("Error.NoAccess"),
        _ when IsWin32Error(ex) && (uint)ex.HResult == 0x800700CE => Loc.T("Error.PathTooLong"),
        _ when IsWin32Error(ex) && TargetGone.Contains((uint)ex.HResult & 0xFFFF) => Loc.T("Error.FolderNotFound"),
        _ => null,
    };

    /// <summary>Ohne Key und ohne Link: nur die Art des Fehlers und bei unbekannten Fällen der Code des Dienstes.</summary>
    public static string Debrid(DebridException ex) => ex.Error == DebridError.Other
        ? Loc.T("Error.Debrid.Other", ex.Code ?? "?")
        : Loc.T("Error.Debrid." + ex.Error);

    private static string Protocol(ServerProtocolException ex) => ex.Problem switch
    {
        ProtocolProblem.MissingContentRange => Loc.T("Error.Protocol.MissingContentRange"),
        ProtocolProblem.WrongRange => Loc.T("Error.Protocol.WrongRange"),
        ProtocolProblem.RangeIgnored => Loc.T("Error.Protocol.RangeIgnored"),
        _ => Loc.T("Error.Protocol.UnexpectedStatus", ex.Status),
    };

    private static string Status(HttpStatusCode code) => (int)code switch
    {
        400 or 401 or 403 or 404 or 410 or 429 or 500 or 502 or 503 or 504 => Loc.T("Error.Http." + (int)code),
        var n => Loc.T("Error.Http.Other", n),
    };

    private static string? Socket(HttpRequestException ex) => (ex.InnerException as SocketException)?.SocketErrorCode switch
    {
        SocketError.HostNotFound or SocketError.NoData => Loc.T("Error.HostNotFound"),
        SocketError.ConnectionRefused => Loc.T("Error.ConnectionRefused"),
        SocketError.ConnectionReset => Loc.T("Error.ConnectionReset"),
        SocketError.NetworkUnreachable or SocketError.HostUnreachable => Loc.T("Error.NetworkUnreachable"),
        SocketError.TimedOut => Loc.T("Error.Timeout"),
        _ => null,
    };
}
