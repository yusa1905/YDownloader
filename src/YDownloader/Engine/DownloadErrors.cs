using System;
using System.IO;
using System.Net;
using System.Net.Http;

namespace YDownloader.Engine;

/// <summary>Die Datei auf dem Server hat sich während des Downloads geändert (Größe, ETag oder Last-Modified).</summary>
public sealed class RemoteFileChangedException : Exception
{
    public RemoteFileChangedException()
        : base("Die Datei auf dem Server hat sich während des Downloads geändert.") { }
}

/// <summary>Warum geschützte Teildaten nicht fortgesetzt werden können.</summary>
public enum ResumeConflict
{
    /// <summary>Die Datei auf dem Server hat eine andere Größe.</summary>
    SizeChanged,
    /// <summary>Die Datei auf dem Server ist eine andere (Validatoren, Länge der .part, Stand unbrauchbar).</summary>
    FileChanged,
    /// <summary>Der Server liefert keine Teilstücke oder keine Größe mehr, es ginge nur von vorn.</summary>
    NoRanges,
}

/// <summary>
/// Die Teildaten passen nicht mehr zur Datei auf dem Server, dürfen aber nicht verworfen werden
/// (<see cref="DownloadRequest.ProtectPartial"/>). <c>.part</c> und <c>.part.json</c> bleiben, wie sie sind.
/// </summary>
public sealed class PartialKeptException : Exception
{
    public PartialKeptException(ResumeConflict conflict, long localSize = -1, long remoteSize = -1)
        : base($"Teildaten bleiben erhalten, Fortsetzen nicht möglich: {conflict}.")
    {
        Conflict = conflict;
        LocalSize = localSize;
        RemoteSize = remoteSize;
    }

    public ResumeConflict Conflict { get; }

    /// <summary>Größe laut <c>.part.json</c>, -1 = unbekannt.</summary>
    public long LocalSize { get; }

    /// <summary>Größe laut Server, -1 = unbekannt.</summary>
    public long RemoteSize { get; }
}

/// <summary>Der Server liefert eine Webseite statt der Datei (<see cref="DownloadRequest.RejectHtml"/>).</summary>
public sealed class HtmlResponseException : Exception
{
    public HtmlResponseException() : base("Der Server liefert eine Webseite statt der Datei.") { }

    internal static bool IsHtml(string? mediaType) =>
        mediaType is not null && (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Die Zieldatei existiert schon und <see cref="ExistingFileMode.Fail"/> war gewählt.</summary>
public sealed class DestinationExistsException : Exception
{
    public DestinationExistsException(string path)
        : base($"Die Zieldatei existiert schon: {path}") => Path = path;

    public string Path { get; }
}

/// <summary>
/// Fehler beim Schreiben auf die Platte (voll, gesperrt, kein Zugriff). Ein neuer Versuch ändert daran nichts.
/// Die Meldungen sind Entwicklertext: Die Anzeige baut ihren Text aus <see cref="NeededBytes"/>, <see cref="FreeBytes"/> und
/// der inneren Ausnahme (siehe <c>ErrorText</c>), damit er in der Sprache der Oberfläche erscheint.
/// </summary>
public sealed class StorageException : IOException
{
    public StorageException(Exception inner)
        : base("Speicherfehler: " + inner.Message, inner) => HResult = inner.HResult;

    /// <summary>Nicht genug Platz: <paramref name="needed"/> Bytes nötig, <paramref name="free"/> frei.</summary>
    public StorageException(string message, int hresult, long needed, long free) : base(message, hresult)
    {
        NeededBytes = needed;
        FreeBytes = free;
    }

    /// <summary>Bei Platzmangel die Bytes, die der Download braucht, sonst -1.</summary>
    public long NeededBytes { get; } = -1;

    /// <summary>Bei Platzmangel die freien Bytes, sonst -1.</summary>
    public long FreeBytes { get; } = -1;
}

/// <summary>Was ein Server bei einer Teilanfrage falsch gemacht hat.</summary>
public enum ProtocolProblem
{
    /// <summary>Teildaten (206) ohne Content-Range.</summary>
    MissingContentRange,
    /// <summary>Ein anderer Bereich als angefragt.</summary>
    WrongRange,
    /// <summary>Die ganze Datei statt des Bereichs: Der Server ignoriert Range.</summary>
    RangeIgnored,
    /// <summary>Ein Statuscode, den eine Teilanfrage nicht liefern sollte.</summary>
    UnexpectedStatus,
}

/// <summary>
/// Der Server hält sich bei einer Teilanfrage nicht ans Protokoll. Wie bisher <see cref="InvalidDataException"/> kein
/// <see cref="IOException"/>, also nicht wiederholbar. Die Meldung ist Entwicklertext, die Anzeige nutzt <see cref="Problem"/>.
/// </summary>
public sealed class ServerProtocolException : Exception
{
    public ServerProtocolException(ProtocolProblem problem, string message, int status = 0) : base(message)
    {
        Problem = problem;
        Status = status;
    }

    public ProtocolProblem Problem { get; }

    /// <summary>Bei <see cref="ProtocolProblem.UnexpectedStatus"/> der HTTP-Statuscode.</summary>
    public int Status { get; }
}

/// <summary>Server meldet Überlast (429 oder 503) und nennt per Retry-After, wann es weitergehen darf.</summary>
public sealed class ServerBusyException : HttpRequestException
{
    public ServerBusyException(HttpStatusCode status, TimeSpan retryAfter)
        : base($"Server ausgelastet ({(int)status}), neuer Versuch frühestens in {Math.Max(0, retryAfter.TotalSeconds):0} s.",
            null, status)
        => RetryAfter = retryAfter;

    public TimeSpan RetryAfter { get; }
}
