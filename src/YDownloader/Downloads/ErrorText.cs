using System;
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
        StorageException { InnerException: { } inner } => Loc.T("Error.Storage", inner.Message),
        RemoteFileChangedException => Loc.T("Error.FileChanged"),
        ChecksumMismatchException sum => Loc.T("Error.Checksum", sum.Expected, sum.Actual),
        DestinationExistsException exists => Loc.T("Error.DestinationExists", exists.Path),
        ServerProtocolException protocol => Protocol(protocol),
        PartialKeptException { Conflict: ResumeConflict.SizeChanged, RemoteSize: >= 0, LocalSize: >= 0 } size =>
            Loc.T("Error.Kept.SizeChanged", Format.Bytes(size.RemoteSize), Format.Bytes(size.LocalSize)),
        PartialKeptException { Conflict: ResumeConflict.NoRanges } => Loc.T("Error.Kept.NoRanges"),
        PartialKeptException => Loc.T("Error.Kept.FileChanged"),
        HtmlResponseException => Loc.T("Error.HtmlResponse"),
        DebridException debrid => Debrid(debrid),
        StorageException or InvalidDataException => ex.Message,
        IOException => Loc.T("Error.ConnectionAborted"),
        UnauthorizedAccessException => Loc.T("Error.NoAccess"),
        NotSupportedException => Loc.T("Error.Unsupported"),
        _ => ex.Message,
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
