using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace YDownloader.Engine;

/// <summary>
/// Lädt eine Datei in mehreren Segmenten (Range-Requests) in eine vorab angelegte <c>.part</c>-Datei.
/// Abbruch per CancellationToken speichert den Stand, ein erneuter Aufruf setzt fort.
/// </summary>
public sealed class HttpDownloader : IDisposable
{
    private const int BufferSize = 64 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly DownloadOptions _options;
    private readonly SpeedLimiter? _limiter;
    private readonly HttpClient _client;
    private readonly HostRegistry? _hosts;
    private readonly bool _ownsClient;

    /// <param name="client">Gemeinsamer Client (siehe <see cref="HttpClientPool"/>). Ohne eigenen Client entsteht einer, der mit dem Downloader endet.</param>
    /// <param name="hosts">Verbindungsgrenze, Tempolimit und User-Agent je Host. Ohne gelten keine Grenzen je Host.</param>
    public HttpDownloader(DownloadOptions options, SpeedLimiter? limiter = null, HttpClient? client = null,
        HostRegistry? hosts = null)
    {
        _options = options;
        _limiter = limiter;
        _hosts = hosts;
        _ownsClient = client is null;
        _client = client ?? CreateClient(options);
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    /// <summary>Eigener Client mit eigenem Handler. Für mehrere Downloads besser <see cref="HttpClientPool"/> nehmen.</summary>
    public static HttpClient CreateClient(DownloadOptions options)
    {
        var client = new HttpClient(HttpClientPool.CreateHandler(options), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        return client;
    }

    /// <summary>
    /// Wartet auf einen freien Verbindungsplatz zum Host der Adresse. Gibt es kein <see cref="HostRegistry"/>, geht es
    /// sofort weiter. <paramref name="host"/> liefert die Regel für Tempolimit und User-Agent (oder null).
    /// </summary>
    private async ValueTask<IDisposable?> EnterHostAsync(HostContext? host, CancellationToken ct) =>
        host is null ? null : await host.Gate.AcquireAsync(ct).ConfigureAwait(false);

    private string UserAgentFor(HostContext? host) => host?.UserAgent ?? _options.UserAgent;

    /// <summary>Wartet auf Allgemein- und Host-Limit, bevor <paramref name="bytes"/> weitergereicht werden.</summary>
    private async ValueTask ThrottleAsync(HostContext? host, int bytes, CancellationToken ct)
    {
        if (_limiter is not null) await _limiter.WaitAsync(bytes, ct).ConfigureAwait(false);
        if (host is not null) await host.Limiter.WaitAsync(bytes, ct).ConfigureAwait(false);
    }

    public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        // Einmal absolut machen: Dateidialoge können das Arbeitsverzeichnis ändern, dann fände ein Resume die .part nicht.
        // Ein Pfad, der nicht geht (zu lang, Laufwerk weg, Ordner nicht anlegbar), ist ein Speicherfehler, kein Netzwerkabbruch.
        string dest, part, metaPath;
        try
        {
            dest = Path.GetFullPath(request.DestinationPath);
            // NTFS erlaubt höchstens 255 Zeichen je Pfadteil; Windows meldet es sonst nur als „ungültiger Name“.
            if (dest.Split('\\', '/').Any(segment => segment.Length > 255)) throw new PathTooLongException();
            request = request with { DestinationPath = dest };
            if (request.IfExists == ExistingFileMode.Fail && Path.Exists(dest)) throw new DestinationExistsException(dest);

            part = dest + ".part";
            metaPath = dest + ".part.json";
            string? dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new StorageException(ex);
        }

        // Ein Name auf ein Archiv, Medium oder Programm kann keine Webseite sein: Eine solche Antwort ist Fehlerseite oder Login.
        if (!request.RejectHtml && FileNames.ExpectsBinary(Path.GetFileName(dest))) request = request with { RejectHtml = true };

        // Einmal geschützt, immer geschützt: Auch wenn der Aufrufer das Flag verloren hat, gilt es für dieselbe Quelle weiter.
        if (!request.ProtectPartial && TryLoadMeta(metaPath) is { Protected: true } old && old.Url == SourceOf(request))
            request = request with { ProtectPartial = true, RejectHtml = true };

        try
        {
            return await DownloadOnceAsync(request, part, metaPath, progress, ct).ConfigureAwait(false);
        }
        catch (RemoteFileChangedException) when (request.ProtectPartial)
        {
            throw new PartialKeptException(ResumeConflict.FileChanged); // nicht neu beginnen, die .part bleibt
        }
        catch (RemoteFileChangedException ex)
        {
            // Datei auf dem Server hat sich geändert: Der alte Stand ist wertlos, einmal ganz neu beginnen.
            request.Events?.Retrying(-1, 1, ex, TimeSpan.Zero);
            TryDelete(part);
            TryDelete(metaPath);
            return await DownloadOnceAsync(request, part, metaPath, progress, ct).ConfigureAwait(false);
        }
    }

    private async Task<DownloadResult> DownloadOnceAsync(DownloadRequest request, string part, string metaPath,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var (probe, body, bodyLease) = await ProbeWithBodyAsync(request.Url, ct, request.Events, gated: true)
            .ConfigureAwait(false);
        request.Events?.Probed(probe);

        if (request.RejectHtml && HtmlResponseException.IsHtml(probe.ContentType))
        {
            body?.Dispose();
            bodyLease?.Dispose();
            throw new HtmlResponseException();
        }

        if (!probe.SupportsRanges || probe.Size <= 0)
            return await DownloadSingleAsync(request, probe, body, bodyLease, part, metaPath, progress, ct).ConfigureAwait(false);
        body?.Dispose();
        bodyLease?.Dispose();

        using var handle = OpenPart(part);
        var state = LoadOrCreateState(request, probe, handle, metaPath);
        Preallocate(handle, state, part);
        SaveMeta(metaPath, state, handle);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = state.Segments
            .Select((s, i) => CancelAllOnErrorAsync(RunSegmentAsync(state, s, i, handle, cts.Token), cts))
            .ToList();
        var reporter = ReportLoopAsync(state, metaPath, handle, progress, cts.Token);

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch
        {
            cts.Cancel();
            try { await reporter.ConfigureAwait(false); } catch { /* Fehler der Segmente hat Vorrang */ }
            TrySaveMeta(metaPath, state, handle);
            ThrowCause(workers, ct);
            throw;
        }

        cts.Cancel();
        await reporter.ConfigureAwait(false);
        progress?.Report(Snapshot(state, 0));
        RandomAccess.FlushToDisk(handle); // erst danach umbenennen, sonst kann die fertige Datei Lücken haben
        NativeFile.TrySetSparse(handle, false); // voll geschrieben: als normale Datei abgeben
        handle.Dispose();

        string target = await FinishAsync(request, part, metaPath, ct).ConfigureAwait(false);
        return new DownloadResult(target, probe.Size, state.Segments.Count, request.Checksum is not null);
    }

    /// <summary>
    /// Bringt die .part auf volle Größe, als Sparse-Datei. Sonst füllt NTFS beim ersten Schreiben weit hinten alles
    /// davor mit Nullen (gemessen: 4-GB-Datei, Schreiben in die letzten 512 MB dauert 0,6 s statt 0 ms, plus 3,5 GB
    /// zusätzliche Schreiblast). Eine Sparse-Datei reserviert keinen Platz, deshalb wird der freie Platz vorher geprüft.
    /// </summary>
    private static void Preallocate(SafeFileHandle handle, State state, string part)
    {
        try
        {
            if (NativeFile.TrySetSparse(handle, true))
            {
                long needed = state.Segments.Sum(s => s.Remaining);
                long free = NativeFile.FreeSpace(Path.GetDirectoryName(part)!);
                if (free >= 0 && free < needed)
                    throw new StorageException(
                        $"Nicht genug Platz: {needed / (1024 * 1024)} MB nötig, {free / (1024 * 1024)} MB frei.",
                        NativeFile.ErrorDiskFull, needed, free);
            }
            // Ohne Sparse (FAT32, exFAT) reserviert SetLength den Platz, Platzmangel zeigt sich hier.
            if (RandomAccess.GetLength(handle) != state.Total) RandomAccess.SetLength(handle, state.Total);
        }
        catch (IOException ex) when (ex is not StorageException)
        {
            throw new StorageException(ex);
        }
    }

    /// <summary>Öffnet die .part zum Schreiben und sperrt sie: Ein zweiter Download auf dasselbe Ziel scheitert hier sofort.</summary>
    private static SafeFileHandle OpenPart(string part)
    {
        try
        {
            return File.OpenHandle(part, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
                FileOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StorageException(ex);
        }
    }

    // ---------------------------------------------------------------- Probe

    /// <summary>
    /// Fragt Name, Größe und Range-Unterstützung ab, ohne die Datei zu laden (etwa für den Hinzufügen-Dialog).
    /// Zählt nicht gegen die Verbindungsgrenze des Hosts: Die Abfrage soll auch bei vollem Host sofort antworten.
    /// </summary>
    public async Task<RemoteFileInfo> ProbeAsync(string url, CancellationToken ct)
    {
        var (info, body, lease) = await ProbeWithBodyAsync(url, ct, gated: false).ConfigureAwait(false);
        body?.Dispose();
        lease?.Dispose();
        return info;
    }

    /// <summary>
    /// Wie <see cref="ProbeAsync"/>. Ignoriert der Server Range (200), kommt die Antwort als <c>Body</c> mit und wird
    /// direkt weitergelesen, statt die Datei ein zweites Mal anzufordern. Dann gehört der Verbindungsplatz
    /// (<c>Lease</c>) dem Aufrufer, er gibt ihn nach dem Lesen frei.
    /// </summary>
    private async Task<(RemoteFileInfo Info, HttpResponseMessage? Body, IDisposable? Lease)> ProbeWithBodyAsync(
        string url, CancellationToken ct, IDownloadEvents? events = null, bool gated = false)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await ProbeOnceAsync(url, gated, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsRetryable(ex) && attempt <= _options.MaxRetries)
            {
                await WaitBeforeRetryAsync(events, -1, attempt, ex, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Meldet die Wiederholung und wartet die Pause ab.</summary>
    private async Task WaitBeforeRetryAsync(IDownloadEvents? events, int segment, int attempt, Exception error,
        CancellationToken ct)
    {
        var delay = RetryDelayFor(attempt, error);
        events?.Retrying(segment, attempt, error, delay);
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    private async Task<(RemoteFileInfo Info, HttpResponseMessage? Body, IDisposable? Lease)> ProbeOnceAsync(string url,
        bool gated, CancellationToken ct)
    {
        var uri = new Uri(url);
        var host = _hosts?.Resolve(uri);
        IDisposable? lease = gated ? await EnterHostAsync(host, ct).ConfigureAwait(false) : null;
        HttpResponseMessage? resp = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var req = NewRequest(uri, UserAgentFor(host), 0, 0);
            resp = await SendAsync(req, timeout, ct).ConfigureAwait(false);

            var finalUrl = resp.RequestMessage?.RequestUri ?? req.RequestUri!; // Ziel nach Weiterleitungen
            var response = resp;
            RemoteFileInfo Info(long size, bool ranges) => new(finalUrl, size, ranges,
                FileNames.FromResponse(response, finalUrl, uri), response.Content.Headers.ContentType?.MediaType,
                response.Headers.ETag?.ToString(), response.Content.Headers.LastModified?.ToString("R"));

            if (resp.StatusCode == HttpStatusCode.PartialContent)
            {
                long? total = resp.Content.Headers.ContentRange?.Length;
                return (Info(total ?? -1, total.HasValue), null, null);
            }
            if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // Leere Datei: "Content-Range: bytes */0"
                return (Info(resp.Content.Headers.ContentRange?.Length ?? -1, false), null, null);
            }
            EnsureSuccess(resp);
            // 200: Server ignoriert Range, Länge ist trotzdem bekannt. Antwort und Verbindungsplatz gehen an den Aufrufer.
            var result = (Info(resp.Content.Headers.ContentLength ?? -1, false), resp, lease);
            resp = null;
            lease = null;
            return result;
        }
        finally
        {
            resp?.Dispose();
            lease?.Dispose();
        }
    }

    // ---------------------------------------------------------------- Zustand

    private sealed class Segment
    {
        public long Start;
        public long End;
        public long Done;
        public long Length => End - Start + 1;
        public long Remaining => Length - Interlocked.Read(ref Done);
    }

    private sealed class State
    {
        public required PartMeta Meta;
        public required List<Segment> Segments;
        public required long[] Lengths;
        public long Total;
        public long LastSavedDone = -1;

        public IDownloadEvents? Events;
        public bool RejectHtml;

        public required Uri OriginalUrl;
        /// <summary>Ziel nach Weiterleitungen. Fällt bei 401/403/404/410 auf <see cref="OriginalUrl"/> zurück.</summary>
        public volatile Uri Url = null!;
        public EntityTagHeaderValue? ETag;
        public DateTimeOffset? LastModified;
        /// <summary>Validator für If-Range: starke ETag, sonst Last-Modified.</summary>
        public RangeConditionHeaderValue? IfRange;
    }

    private State LoadOrCreateState(DownloadRequest request, RemoteFileInfo probe, SafeFileHandle part, string metaPath)
    {
        PartMeta? meta = TryLoadMeta(metaPath);
        string source = SourceOf(request);
        bool stable = request.ResumeKey is null; // ein entsperrter Debrid-Link bringt bei jedem Entsperren neue Validatoren mit
        bool usable = meta is not null
            && meta.Url == source
            && meta.Total == probe.Size
            && RandomAccess.GetLength(part) == probe.Size
            && (!stable || meta.ETag is null || probe.ETag is null || meta.ETag == probe.ETag)
            && (!stable || meta.LastModified is null || probe.LastModified is null || meta.LastModified == probe.LastModified)
            && meta.Segments.Count > 0
            && meta.Segments.All(s => s.Start >= 0 && s.End >= s.Start && s.End < probe.Size && s.Done >= 0 && s.Done <= s.End - s.Start + 1);

        if (usable && !stable)
        {
            // Dieselbe Datei hinter einem neuen Link: die Validatoren des neuen Links gelten für den Rest des Laufs.
            meta!.ETag = probe.ETag;
            meta.LastModified = probe.LastModified;
        }
        if (!usable)
        {
            // Geschützte Teildaten derselben Quelle: lieber scheitern als die Bytes wegwerfen.
            if (request.ProtectPartial && meta is not null && meta.Url == source && HasProgress(meta))
                throw new PartialKeptException(meta.Total != probe.Size ? ResumeConflict.SizeChanged : ResumeConflict.FileChanged,
                    meta.Total, probe.Size);

            // Alter Stand passt nicht zur Datei auf dem Server: alte Bytes verwerfen, neu beginnen.
            RandomAccess.SetLength(part, 0);
            meta = new PartMeta
            {
                Url = source,
                Total = probe.Size,
                ETag = probe.ETag,
                LastModified = probe.LastModified,
                Segments = Segmenter.Split(probe.Size, _options.Segments, _options.MinSegmentSize)
                    .Select(r => new SegmentMeta { Start = r.Start, End = r.End }).ToList(),
            };
        }
        if (request.ProtectPartial) meta!.Protected = true;

        var segments = meta!.Segments.Select(s => new Segment { Start = s.Start, End = s.End, Done = s.Done }).ToList();
        var state = new State
        {
            Meta = meta,
            Segments = segments,
            Lengths = segments.Select(s => s.Length).ToArray(),
            Total = probe.Size,
            OriginalUrl = new Uri(request.Url),
            Url = probe.FinalUrl,
            Events = request.Events,
            RejectHtml = request.RejectHtml,
        };
        if (meta.ETag is not null && EntityTagHeaderValue.TryParse(meta.ETag, out var etag))
            state.ETag = etag;
        if (meta.LastModified is not null && DateTimeOffset.TryParseExact(meta.LastModified, "R",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var lastMod))
            state.LastModified = lastMod;
        if (state.ETag is { IsWeak: false })
            state.IfRange = new RangeConditionHeaderValue(state.ETag);
        else if (state.LastModified is not null)
            state.IfRange = new RangeConditionHeaderValue(state.LastModified.Value);
        return state;
    }

    /// <summary>Kennung der Quelle in der <c>.part.json</c>.</summary>
    private static string SourceOf(DownloadRequest request) => request.ResumeKey ?? request.Url;

    private static bool HasProgress(PartMeta meta) => meta.Segments.Any(s => s.Done > 0);

    private static PartMeta? TryLoadMeta(string metaPath)
    {
        try
        {
            if (!File.Exists(metaPath)) return null;
            return JsonSerializer.Deserialize(File.ReadAllBytes(metaPath), EngineJsonContext.Default.PartMeta);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Hält den Stand fest, bringt die Daten auf die Platte und schreibt erst dann die Meta-Datei.
    /// So zählt die <c>.part.json</c> auch nach einem Stromausfall keine Bytes, die nur im Cache lagen.
    /// </summary>
    private static void SaveMeta(string metaPath, State state, SafeFileHandle data)
    {
        long done = 0;
        for (int i = 0; i < state.Segments.Count; i++)
        {
            long d = Interlocked.Read(ref state.Segments[i].Done);
            state.Meta.Segments[i].Done = d;
            done += d;
        }
        RandomAccess.FlushToDisk(data);
        AtomicFile.WriteAllBytes(metaPath,
            JsonSerializer.SerializeToUtf8Bytes(state.Meta, EngineJsonContext.Default.PartMeta));
        state.LastSavedDone = done;
    }

    /// <summary>Speichert den Stand. Scheitert das (Datei kurz gesperrt, Platte voll), bleibt der alte Stand gültig.</summary>
    private static void TrySaveMeta(string metaPath, State state, SafeFileHandle data)
    {
        try
        {
            SaveMeta(metaPath, state, data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ---------------------------------------------------------------- Segmente

    /// <summary>Bricht bei einem Fehler sofort alle übrigen Segmente ab, statt auf deren Ende zu warten.</summary>
    private static async Task CancelAllOnErrorAsync(Task segment, CancellationTokenSource all)
    {
        try
        {
            await segment.ConfigureAwait(false);
        }
        catch
        {
            all.Cancel();
            throw;
        }
    }

    /// <summary>Wirft die eigentliche Ursache: Abbruch durch den Aufrufer, sonst den ersten echten Fehler eines Segments.</summary>
    private static void ThrowCause(List<Task> workers, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var errors = workers.Where(t => t.IsFaulted).SelectMany(t => t.Exception!.InnerExceptions).ToList();
        var cause = errors.FirstOrDefault(e => e is not OperationCanceledException) ?? errors.FirstOrDefault();
        if (cause is not null) ExceptionDispatchInfo.Throw(cause);
    }

    private async Task RunSegmentAsync(State state, Segment seg, int index, SafeFileHandle handle, CancellationToken ct)
    {
        int attempt = 0;
        while (seg.Remaining > 0)
        {
            long before = Interlocked.Read(ref seg.Done);
            Uri url = state.Url;
            try
            {
                await TransferSegmentAsync(state, url, seg, handle, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (!ct.IsCancellationRequested && IsLinkExpired(ex) && url != state.OriginalUrl)
            {
                // Ziel der Weiterleitung abgelaufen: ab jetzt wieder über die Original-URL, die neu weiterleitet.
                state.Url = state.OriginalUrl;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsRetryable(ex))
            {
                if (Interlocked.Read(ref seg.Done) > before) attempt = 0; // es ging voran
                if (++attempt > _options.MaxRetries) throw;
                await WaitBeforeRetryAsync(state.Events, index, attempt, ex, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Exponentielle Pause mit ±20 % Streuung, damit Segmente nicht im Gleichschritt wiederkommen.
    /// Nennt der Server per Retry-After eine längere Pause, gilt diese (höchstens 5 Minuten).
    /// </summary>
    internal TimeSpan RetryDelayFor(int attempt, Exception? error = null)
    {
        double ms = _options.RetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        ms = Math.Min(ms, _options.MaxRetryDelay.TotalMilliseconds) * (0.8 + Random.Shared.NextDouble() * 0.4);
        if (error is ServerBusyException busy)
            ms = Math.Max(ms, Math.Min(busy.RetryAfter.TotalMilliseconds, MaxServerRetryAfter.TotalMilliseconds));
        return TimeSpan.FromMilliseconds(ms);
    }

    private static readonly TimeSpan MaxServerRetryAfter = TimeSpan.FromMinutes(5);

    internal static bool IsRetryable(Exception ex) => ex switch
    {
        StorageException => false, // Platte voll, Datei gesperrt: ein neuer Versuch ändert nichts
        HttpRequestException h => h.StatusCode switch
        {
            null => true, // Verbindungsfehler
            HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests => true,
            HttpStatusCode.NotImplemented or HttpStatusCode.HttpVersionNotSupported => false,
            var code => (int)code >= 500,
        },
        IOException => true, // Verbindung abgebrochen (Schreibfehler kommen als StorageException)
        TimeoutException => true,
        _ => false,
    };

    /// <summary>Wie <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/>, merkt sich aber Retry-After bei 429 und 503.</summary>
    private static void EnsureSuccess(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            TimeSpan? retryAfter = resp.Headers.RetryAfter switch
            {
                { Delta: { } delta } => delta,
                { Date: { } date } => date - DateTimeOffset.UtcNow,
                _ => null,
            };
            if (retryAfter is not null) throw new ServerBusyException(resp.StatusCode, retryAfter.Value);
        }
        resp.EnsureSuccessStatusCode();
    }

    private static bool IsLinkExpired(HttpRequestException ex) => ex.StatusCode
        is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone;

    private async Task TransferSegmentAsync(State state, Uri url, Segment seg, SafeFileHandle handle, CancellationToken ct)
    {
        var host = _hosts?.Resolve(url);
        // Der Platz gilt für die ganze Übertragung. Wartezeit hier zählt nicht zum Zeitlimit der Anfrage.
        using var slot = await EnterHostAsync(host, ct).ConfigureAwait(false);

        long offset = seg.Start + Interlocked.Read(ref seg.Done);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var req = NewRequest(url, UserAgentFor(host), offset, seg.End);
        req.Headers.IfRange = state.IfRange;
        using var resp = await SendAsync(req, timeout, ct).ConfigureAwait(false);
        if (state.RejectHtml && HtmlResponseException.IsHtml(resp.Content.Headers.ContentType?.MediaType))
            throw new HtmlResponseException();
        long last = CheckRangeResponse(resp, state, offset, seg.End);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (offset <= last)
            {
                int want = (int)Math.Min(buffer.Length, last - offset + 1);
                int read = await ReadWithTimeoutAsync(stream, buffer.AsMemory(0, want), timeout, ct).ConfigureAwait(false);
                if (read == 0) throw new IOException("Verbindung vorzeitig beendet.");

                await ThrottleAsync(host, read, ct).ConfigureAwait(false);
                try
                {
                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), offset, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new StorageException(ex);
                }
                offset += read;
                Interlocked.Add(ref seg.Done, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Prüft die Antwort auf eine Range-Anfrage und liefert das letzte enthaltene Byte. Liefert der Server weniger
    /// als angefragt, holt die nächste Anfrage den Rest ohne Retry-Pause.
    /// </summary>
    private static long CheckRangeResponse(HttpResponseMessage resp, State state, long offset, long end)
    {
        if (resp.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = resp.Content.Headers.ContentRange;
            if (IsOtherVersion(resp, state, range?.Length)) throw new RemoteFileChangedException();
            if (range?.From is null)
            {
                // Ohne Content-Range nur glauben, wenn die Länge genau zur Anfrage passt.
                if (resp.Content.Headers.ContentLength == end - offset + 1) return end;
                throw new ServerProtocolException(ProtocolProblem.MissingContentRange, "Server liefert Teildaten ohne Content-Range.");
            }
            if (range.From != offset)
                throw new ServerProtocolException(ProtocolProblem.WrongRange, "Server liefert einen anderen Bereich als angefragt.");
            return Math.Min(end, range.To ?? end);
        }
        if (resp.StatusCode == HttpStatusCode.OK)
        {
            // Ganze Datei statt Bereich: Entweder schlug If-Range fehl (andere Version) oder der Server ignoriert Range.
            bool noValidators = resp.Headers.ETag is null && resp.Content.Headers.LastModified is null;
            if (IsOtherVersion(resp, state, resp.Content.Headers.ContentLength) || (state.IfRange is not null && noValidators))
                throw new RemoteFileChangedException();
            if (offset == 0 && end == state.Total - 1) return end;
            throw new ServerProtocolException(ProtocolProblem.RangeIgnored, "Server ignoriert den Range-Header.");
        }
        EnsureSuccess(resp);
        throw new ServerProtocolException(ProtocolProblem.UnexpectedStatus,
            $"Unerwartete Antwort {(int)resp.StatusCode} auf eine Range-Anfrage.", (int)resp.StatusCode);
    }

    /// <summary>Erkennt an Größe, ETag und Last-Modified, ob der Server inzwischen eine andere Datei liefert.</summary>
    private static bool IsOtherVersion(HttpResponseMessage resp, State state, long? total)
    {
        if (total is long t && t != state.Total) return true;
        if (resp.Headers.ETag is { } etag && state.ETag is not null) return etag.Tag != state.ETag.Tag;
        if (resp.Content.Headers.LastModified is { } lastMod && state.LastModified is not null) return lastMod != state.LastModified;
        return false;
    }

    /// <summary>GET mit <c>Accept-Encoding: identity</c>: Range-Bytes müssen den Dateibytes entsprechen.</summary>
    private static HttpRequestMessage NewRequest(Uri url, string userAgent, long? from = null, long? to = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        // Ohne Prüfung: Auch ungewöhnliche Eingaben aus den Host-Einstellungen sollen nicht die Anfrage kippen.
        req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        if (from is not null) req.Headers.Range = new RangeHeaderValue(from, to);
        return req;
    }

    /// <summary>
    /// Sendet mit Zeitlimit bis zu den Headern. <paramref name="timeout"/> wird danach für die Lesevorgänge
    /// weiterverwendet, damit pro Chunk kein neuer Timer entsteht.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationTokenSource timeout,
        CancellationToken ct)
    {
        timeout.CancelAfter(_options.ReadTimeout);
        try
        {
            var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            return resp;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Header-Zeitlimit oder ConnectTimeout des Handlers (kommt als Abbruch mit innerer TimeoutException).
            throw new TimeoutException("Server antwortet nicht innerhalb des Zeitlimits.", ex);
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<int> ReadWithTimeoutAsync(Stream stream, Memory<byte> buffer, CancellationTokenSource timeout,
        CancellationToken ct)
    {
        timeout.CancelAfter(_options.ReadTimeout);
        try
        {
            int read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
            timeout.CancelAfter(Timeout.InfiniteTimeSpan); // Limiter und Schreiben zählen nicht zum Lese-Zeitlimit
            return read;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Keine Daten innerhalb des Zeitlimits.");
        }
    }

    // ---------------------------------------------------------------- Fortschritt

    private async Task ReportLoopAsync(State state, string metaPath, SafeFileHandle data,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(ProgressInterval);
        long lastDone = state.Segments.Sum(s => Interlocked.Read(ref s.Done));
        long lastTicks = Environment.TickCount64;
        long lastSave = lastTicks;
        double speed = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                long done = state.Segments.Sum(s => Interlocked.Read(ref s.Done));
                long now = Environment.TickCount64;
                double inst = (done - lastDone) * 1000.0 / Math.Max(1, now - lastTicks);
                speed = speed == 0 ? inst : speed * 0.6 + inst * 0.4;
                lastDone = done;
                lastTicks = now;

                progress?.Report(Snapshot(state, speed));

                if (now - lastSave >= _options.StateSaveInterval.TotalMilliseconds && done != state.LastSavedDone)
                {
                    TrySaveMeta(metaPath, state, data); // scheitert es, folgt der nächste Versuch beim nächsten Intervall
                    lastSave = now;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static DownloadProgress Snapshot(State state, double speed)
    {
        var bytes = state.Segments.Select(s => Interlocked.Read(ref s.Done)).ToArray();
        return new DownloadProgress(state.Total, bytes.Sum(), speed, bytes, state.Lengths);
    }

    // ---------------------------------------------------------------- Abschluss und Fallback

    /// <summary>Prüft die Checksumme und benennt die .part um. Liefert den tatsächlichen Zielpfad.</summary>
    private static async Task<string> FinishAsync(DownloadRequest request, string part, string metaPath, CancellationToken ct)
    {
        if (request.Checksum is not null)
        {
            try
            {
                await Checksums.VerifyAsync(part, request.Checksum, ct).ConfigureAwait(false);
            }
            catch (ChecksumMismatchException)
            {
                TryDelete(part); // kaputte Daten nicht für ein Resume aufheben
                TryDelete(metaPath);
                throw;
            }
        }
        string target = MoveToDestination(part, request.DestinationPath, request.IfExists);
        TryDelete(metaPath);
        return target;
    }

    private static string MoveToDestination(string part, string dest, ExistingFileMode mode)
    {
        if (mode == ExistingFileMode.Overwrite)
        {
            File.Move(part, dest, overwrite: true);
            return dest;
        }
        for (int n = 0; n < 10_000; n++)
        {
            string target = n == 0 ? dest : NumberedName(dest, n);
            if (!Path.Exists(target))
            {
                try
                {
                    File.Move(part, target, overwrite: false);
                    return target;
                }
                catch (IOException) when (Path.Exists(target))
                {
                    // eben erst entstanden: nächsten Namen versuchen
                }
            }
            if (mode == ExistingFileMode.Fail) break;
        }
        throw new DestinationExistsException(dest);
    }

    /// <summary><c>name.ext</c> wird zu <c>name (n).ext</c>, <c>name.tar.gz</c> zu <c>name (n).tar.gz</c>.</summary>
    internal static string NumberedName(string path, int n)
    {
        string dir = Path.GetDirectoryName(path) ?? "";
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        if (stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            ext = stem[^4..] + ext;
            stem = stem[..^4];
        }
        return Path.Combine(dir, $"{stem} ({n}){ext}");
    }

    /// <summary>Server ohne Range-Unterstützung (oder unbekannte Länge): ein Strom, kein Resume. Abbrüche beginnen neu ab 0.</summary>
    private async Task<DownloadResult> DownloadSingleAsync(DownloadRequest request, RemoteFileInfo probe,
        HttpResponseMessage? body, IDisposable? bodyLease, string part, string metaPath,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        FileStream file;
        try
        {
            // Von vorn ginge nur mit Verwerfen der Teildaten: bei geschützten nicht.
            if (request.ProtectPartial && TryLoadMeta(metaPath) is { } old && old.Url == SourceOf(request) && HasProgress(old))
                throw new PartialKeptException(ResumeConflict.NoRanges, old.Total, probe.Size);
            file = CreatePartStream(part); // sperrt die .part: Ein zweiter Download auf dasselbe Ziel scheitert hier
        }
        catch
        {
            body?.Dispose();
            bodyLease?.Dispose();
            throw;
        }

        long done;
        await using (file)
        {
            TryDelete(metaPath); // alter segmentierter Stand gilt nicht mehr
            for (int attempt = 1; ; attempt++)
            {
                var first = body;
                var firstLease = bodyLease;
                body = null;
                bodyLease = null;
                try
                {
                    done = await StreamToFileAsync(probe.FinalUrl, first, firstLease, file, request.RejectHtml, progress, ct)
                        .ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && IsRetryable(ex) && attempt <= _options.MaxRetries)
                {
                    await WaitBeforeRetryAsync(request.Events, -1, attempt, ex, ct).ConfigureAwait(false);
                }
            }
            try { file.Flush(flushToDisk: true); }
            catch (IOException ex) { throw new StorageException(ex); }
        }

        progress?.Report(new DownloadProgress(done, done, 0, new[] { done }));
        string target = await FinishAsync(request, part, metaPath, ct).ConfigureAwait(false);
        return new DownloadResult(target, done, 1, request.Checksum is not null);
    }

    /// <summary>
    /// Liest eine ganze Antwort von vorn in <paramref name="file"/>. <paramref name="response"/> ist die Antwort der Probe,
    /// sonst wird neu angefragt.
    /// </summary>
    private async Task<long> StreamToFileAsync(Uri url, HttpResponseMessage? response, IDisposable? lease, FileStream file,
        bool rejectHtml, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var host = _hosts?.Resolve(url);
        // Die Antwort der Abfrage bringt ihren Verbindungsplatz mit, eine neue Anfrage holt sich einen.
        using var slot = response is null ? await EnterHostAsync(host, ct).ConfigureAwait(false) : lease;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var req = response is null ? NewRequest(url, UserAgentFor(host)) : null;
        using var resp = response ?? await SendAsync(req!, timeout, ct).ConfigureAwait(false);
        EnsureSuccess(resp);
        if (rejectHtml && HtmlResponseException.IsHtml(resp.Content.Headers.ContentType?.MediaType))
            throw new HtmlResponseException();
        long total = resp.Content.Headers.ContentLength ?? -1;
        long done = 0;

        try
        {
            file.SetLength(0);
        }
        catch (IOException ex)
        {
            throw new StorageException(ex);
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long lastReport = 0;
        try
        {
            while (true)
            {
                int read = await ReadWithTimeoutAsync(stream, buffer, timeout, ct).ConfigureAwait(false);
                if (read == 0) break;
                await ThrottleAsync(host, read, ct).ConfigureAwait(false);
                try
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new StorageException(ex);
                }
                done += read;
                if (watch.ElapsedMilliseconds - lastReport >= ProgressInterval.TotalMilliseconds)
                {
                    double speed = done * 1000.0 / Math.Max(1, watch.ElapsedMilliseconds);
                    progress?.Report(new DownloadProgress(total, done, speed, new[] { done }));
                    lastReport = watch.ElapsedMilliseconds;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (total >= 0 && done != total) throw new IOException("Verbindung vorzeitig beendet.");
        return done;
    }

    private static FileStream CreatePartStream(string part)
    {
        try
        {
            return new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.Read, BufferSize, FileOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new StorageException(ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
