using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace YDownloader.Tests;

/// <summary>Kleiner lokaler HTTP-Server mit Range-Unterstützung und Fehlerinjektion.</summary>
public sealed class TestServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, int> _tokenUses = new();
    private int _requests;
    private int _tokens;
    private int _generation;
    private int _stallsLeft;
    private int _active;
    private int _maxActive;

    public byte[] Data { get; private set; }
    public string ETag { get; private set; } = "\"test-etag\"";
    public bool SupportsRanges { get; set; } = true;

    /// <summary>Bytes pro Sekunde je Antwort, 0 = so schnell wie möglich.</summary>
    public int ThrottleBytesPerSecond { get; set; }

    /// <summary>Die ersten N Antworten mit Daten brechen nach <see cref="DropAfterBytes"/> Bytes ab.</summary>
    public int DropFirstResponses { get; set; }
    public int DropAfterBytes { get; set; } = 100_000;

    public int FailFirstWith503 { get; set; }

    /// <summary>Retry-After in Sekunden für die 503-Antworten (0 = ohne).</summary>
    public int RetryAfterSeconds { get; set; }

    /// <summary>Range-Anfragen ab diesem Start bekommen <see cref="FailRangesStatus"/> statt Daten.</summary>
    public long? FailRangesFrom { get; set; }
    public int FailRangesStatus { get; set; } = 404;

    /// <summary>Die ersten N Segment-Anfragen bekommen nie Header (Server hängt).</summary>
    public int StallSegmentRequests { get => _stallsLeft; set => _stallsLeft = value; }

    public string? ContentDisposition { get; set; }

    /// <summary>Content-Type der Antworten, null = keiner.</summary>
    public string? ContentType { get; set; }

    /// <summary>Gesamtgröße im Content-Range, falls abweichend von den echten Daten (z. B. riesige Datei vortäuschen).</summary>
    public long? AnnouncedTotal { get; set; }

    /// <summary>Range-Antworten enthalten höchstens so viele Bytes (0 = unbegrenzt).</summary>
    public long MaxRangeLength { get; set; }

    /// <summary>Jedes Weiterleitungsziel von <see cref="RedirectUrl"/> gilt für so viele Anfragen (0 = unbegrenzt), danach 403.</summary>
    public int TokenUses { get; set; }

    public ConcurrentQueue<string> Ranges { get; } = new();
    public ConcurrentQueue<string> Paths { get; } = new();
    public ConcurrentQueue<string> AcceptEncodings { get; } = new();
    public ConcurrentQueue<string> IfRanges { get; } = new();
    public ConcurrentQueue<string> UserAgents { get; } = new();

    /// <summary>Höchste Zahl gleichzeitig beantworteter Anfragen (zählt bis zum letzten gesendeten Byte).</summary>
    public int MaxConcurrentRequests => Volatile.Read(ref _maxActive);
    public string Url { get; }
    public string RedirectUrl { get; }
    public int RequestCount => _requests;

    public TestServer(int size)
    {
        Data = new byte[size];
        new Random(42).NextBytes(Data);

        // Parallel laufende Tests können denselben freien Port wählen, bevor er belegt ist: dann neu versuchen.
        int port = 0;
        for (int attempt = 1; ; attempt++)
        {
            using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            // Ein gescheiterter Start macht den Listener unbrauchbar: jeder Versuch bekommt einen neuen.
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { _listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 10) { }
        }
        Url = $"http://127.0.0.1:{port}/file.bin";
        RedirectUrl = $"http://127.0.0.1:{port}/redirect";
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>Tauscht die Datei auf dem Server aus (neue ETag). Laufende Antworten brechen ab.</summary>
    public void Replace(byte[] data, string etag)
    {
        Data = data;
        ETag = etag;
        Interlocked.Increment(ref _generation);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        int n = Interlocked.Increment(ref _requests);
        int generation = Volatile.Read(ref _generation);
        byte[] data = Data;
        string etag = ETag;
        var resp = ctx.Response;

        // Gleichzeitige Anfragen zählen. Freigegeben wird schon nach dem letzten Byte, vor dem Schließen: Der Client
        // kann die nächste Anfrage erst danach schicken, sonst würde ein später Abschluss hier eine Überlappung vortäuschen.
        int released = 0;
        int active = Interlocked.Increment(ref _active);
        int seen;
        while (active > (seen = Volatile.Read(ref _maxActive)) && Interlocked.CompareExchange(ref _maxActive, active, seen) != seen) { }
        void Release()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) Interlocked.Decrement(ref _active);
        }

        try
        {
            Paths.Enqueue(ctx.Request.Url!.PathAndQuery);
            AcceptEncodings.Enqueue(ctx.Request.Headers["Accept-Encoding"] ?? "");
            UserAgents.Enqueue(ctx.Request.Headers["User-Agent"] ?? "");

            if (n <= FailFirstWith503)
            {
                resp.StatusCode = 503;
                if (RetryAfterSeconds > 0) resp.AddHeader("Retry-After", RetryAfterSeconds.ToString());
                resp.Close();
                return;
            }

            if (ctx.Request.Url.AbsolutePath == "/redirect")
            {
                resp.Redirect($"/file.bin?t={Interlocked.Increment(ref _tokens)}");
                resp.Close();
                return;
            }
            string? token = ctx.Request.QueryString["t"];
            if (token is not null && TokenUses > 0 && _tokenUses.AddOrUpdate(token, 1, (_, c) => c + 1) > TokenUses)
            {
                resp.StatusCode = 403;
                resp.Close();
                return;
            }

            long start = 0, end = data.Length - 1;
            bool partial = false;
            string? range = ctx.Request.Headers["Range"];
            string? ifRange = ctx.Request.Headers["If-Range"];
            if (ifRange is not null) IfRanges.Enqueue(ifRange);
            if (SupportsRanges && range is not null && range.StartsWith("bytes=") && (ifRange is null || ifRange == etag))
            {
                Ranges.Enqueue(range);
                var parts = range.Substring(6).Split('-');
                start = long.Parse(parts[0]);
                if (parts.Length > 1 && parts[1].Length > 0) end = Math.Min(long.Parse(parts[1]), data.Length - 1);
                if (MaxRangeLength > 0) end = Math.Min(end, start + MaxRangeLength - 1);
                partial = true;
            }

            long length = end - start + 1;
            if (partial && FailRangesFrom is long from && start >= from)
            {
                resp.StatusCode = FailRangesStatus;
                resp.Close();
                return;
            }
            if (partial && length > 1 && Interlocked.Decrement(ref _stallsLeft) >= 0)
            {
                await Task.Delay(Timeout.Infinite, _cts.Token).ConfigureAwait(false);
                return;
            }

            resp.StatusCode = partial ? 206 : 200;
            if (SupportsRanges) resp.AddHeader("Accept-Ranges", "bytes");
            if (partial) resp.AddHeader("Content-Range", $"bytes {start}-{end}/{AnnouncedTotal ?? data.Length}");
            resp.AddHeader("ETag", etag);
            if (ContentDisposition is not null) resp.AddHeader("Content-Disposition", ContentDisposition);
            if (ContentType is not null) resp.ContentType = ContentType;
            resp.ContentLength64 = length;

            bool drop = n <= FailFirstWith503 + DropFirstResponses && length > 1;
            long toSend = drop ? Math.Min(length, DropAfterBytes) : length;

            const int chunk = 16 * 1024;
            long sent = 0;
            while (sent < toSend)
            {
                if (Volatile.Read(ref _generation) != generation)
                {
                    resp.Abort(); // Datei wurde getauscht
                    return;
                }
                int c = (int)Math.Min(chunk, toSend - sent);
                // Vor dem letzten Chunk freigeben: Hat der Client ihn, kann er sofort neu anfragen, und dieser Thread
                // käme sonst unter Last erst danach zum Zählen (vorgetäuschte Überlappung).
                if (!drop && sent + c >= toSend) Release();
                await resp.OutputStream.WriteAsync(data.AsMemory((int)(start + sent), c)).ConfigureAwait(false);
                sent += c;
                if (ThrottleBytesPerSecond > 0 && sent < toSend) // nach dem letzten Chunk nicht mehr warten
                    await Task.Delay((int)(c * 1000L / ThrottleBytesPerSecond)).ConfigureAwait(false);
            }

            Release();
            if (drop) resp.Abort();
            else resp.Close();
        }
        catch
        {
            try { resp.Abort(); } catch { }
        }
        finally
        {
            Release();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ydl-test-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}
