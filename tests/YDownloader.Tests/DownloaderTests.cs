using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YDownloader.Engine;

namespace YDownloader.Tests;

public class DownloaderTests
{
    private static DownloadOptions Fast(int segments = 4) => new()
    {
        Segments = segments,
        MinSegmentSize = 64 * 1024,
        RetryDelay = TimeSpan.FromMilliseconds(20),
        MaxRetries = 5,
        ReadTimeout = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public async Task Downloads_with_multiple_segments_and_matches_content()
    {
        using var server = new TestServer(2_000_000);
        using var dir = new TempDir();
        using var dl = new HttpDownloader(Fast(4));

        var result = await dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("a.bin")), null, default);

        Assert.Equal(4, result.Segments);
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.False(File.Exists(dir.File("a.bin.part")));
        Assert.False(File.Exists(dir.File("a.bin.part.json")));
        Assert.True(server.Ranges.Distinct().Count() >= 4);
    }

    [Fact]
    public async Task Reports_progress()
    {
        using var server = new TestServer(1_000_000) { ThrottleBytesPerSecond = 2_000_000 };
        using var dir = new TempDir();
        using var dl = new HttpDownloader(Fast(2));
        DownloadProgress? last = null;
        var progress = new SyncProgress(p => last = p);

        await dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("p.bin")), progress, default);

        Assert.NotNull(last);
        Assert.Equal(1_000_000, last!.TotalBytes);
        Assert.Equal(1_000_000, last.DownloadedBytes);
    }

    [Fact]
    public async Task Cancel_then_resume_only_fetches_missing_bytes()
    {
        using var server = new TestServer(3_000_000) { ThrottleBytesPerSecond = 400_000 };
        using var dir = new TempDir();
        string path = dir.File("r.bin");
        using var dl = new HttpDownloader(Fast(3));

        using var cts = new CancellationTokenSource();
        long seen = 0;
        var progress = new SyncProgress(p =>
        {
            seen = p.DownloadedBytes;
            if (p.DownloadedBytes > 600_000) cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dl.DownloadAsync(new DownloadRequest(server.Url, path), progress, cts.Token));

        Assert.True(File.Exists(path + ".part"));
        Assert.True(File.Exists(path + ".part.json"));
        Assert.False(File.Exists(path));
        Assert.True(seen > 600_000);

        server.Ranges.Clear();
        server.ThrottleBytesPerSecond = 0;
        var result = await dl.DownloadAsync(new DownloadRequest(server.Url, path), null, default);

        Assert.Equal(server.Data, await File.ReadAllBytesAsync(path));
        Assert.Equal(3, result.Segments);

        // Fortsetzen: Es darf weniger als die ganze Datei angefordert werden.
        Assert.True(TotalRequested(server) < server.Data.Length, "Resume darf nicht alles erneut laden.");
    }

    private static long TotalRequested(TestServer server) => server.Ranges
        .Select(r => r.Substring(6).Split('-'))
        .Where(p => p[1].Length > 0)
        .Sum(p => long.Parse(p[1]) - long.Parse(p[0]) + 1);

    [Fact]
    public async Task Retries_after_dropped_connection_and_continues_where_it_stopped()
    {
        using var server = new TestServer(1_500_000) { DropFirstResponses = 3, DropAfterBytes = 50_000 };
        using var dir = new TempDir();
        using var dl = new HttpDownloader(Fast(2));

        await dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("d.bin")), null, default);

        Assert.Equal(server.Data, await File.ReadAllBytesAsync(dir.File("d.bin")));
    }

    [Fact]
    public async Task Retries_on_503()
    {
        using var server = new TestServer(300_000) { FailFirstWith503 = 2 };
        using var dir = new TempDir();
        var options = Fast(1);
        using var dl = new HttpDownloader(options);

        // Die ersten zwei Anfragen (Probe) liefern 503, der Download wiederholt automatisch.
        var result = await dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("e.bin")), null, default);
        Assert.Equal(300_000, result.Bytes);
    }

    [Fact]
    public async Task Gives_up_after_max_retries()
    {
        using var server = new TestServer(1_000_000) { DropFirstResponses = 1000, DropAfterBytes = 0 };
        using var dir = new TempDir();
        var options = Fast(1);
        options.MaxRetries = 2;
        using var dl = new HttpDownloader(options);

        await Assert.ThrowsAnyAsync<Exception>(
            () => dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("g.bin")), null, default));
        Assert.False(File.Exists(dir.File("g.bin")));
    }

    [Fact]
    public async Task Server_without_range_support_falls_back_to_single_stream()
    {
        using var server = new TestServer(800_000) { SupportsRanges = false };
        using var dir = new TempDir();
        using var dl = new HttpDownloader(Fast(4));

        var result = await dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("s.bin")), null, default);

        Assert.Equal(1, result.Segments);
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(dir.File("s.bin")));
    }

    [Fact]
    public async Task Preallocates_part_file_to_full_size()
    {
        using var server = new TestServer(1_000_000) { ThrottleBytesPerSecond = 300_000 };
        using var dir = new TempDir();
        string path = dir.File("pre.bin");
        using var dl = new HttpDownloader(Fast(2));
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.DownloadedBytes > 100_000) cts.Cancel(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dl.DownloadAsync(new DownloadRequest(server.Url, path), progress, cts.Token));

        Assert.Equal(1_000_000, new FileInfo(path + ".part").Length);
    }

    [Fact]
    public async Task Checksum_ok_and_mismatch()
    {
        using var server = new TestServer(500_000);
        using var dir = new TempDir();
        using var dl = new HttpDownloader(Fast(2));
        string good = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(server.Data));

        await dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("c.bin"),
            new ChecksumSpec(ChecksumAlgorithm.Sha256, good)), null, default);
        Assert.True(File.Exists(dir.File("c.bin")));

        await Assert.ThrowsAsync<ChecksumMismatchException>(() => dl.DownloadAsync(
            new DownloadRequest(server.Url, dir.File("bad.bin"),
                new ChecksumSpec(ChecksumAlgorithm.Sha256, new string('0', 64))), null, default));
        Assert.False(File.Exists(dir.File("bad.bin")));
        Assert.False(File.Exists(dir.File("bad.bin.part")));
    }

    [Fact]
    public async Task Changed_file_on_server_restarts_instead_of_resuming()
    {
        using var server = new TestServer(1_000_000);
        using var dir = new TempDir();
        string path = dir.File("x.bin");

        // Alten Stand mit falscher Gesamtgröße vortäuschen.
        File.WriteAllBytes(path + ".part", new byte[999]);
        File.WriteAllText(path + ".part.json",
            "{\"Url\":\"" + server.Url + "\",\"Total\":999,\"Segments\":[{\"Start\":0,\"End\":998,\"Done\":500}]}");

        using var dl = new HttpDownloader(Fast(2));
        await dl.DownloadAsync(new DownloadRequest(server.Url, path), null, default);

        Assert.Equal(server.Data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Speed_limiter_slows_download()
    {
        using var server = new TestServer(600_000);
        using var dir = new TempDir();
        var limiter = new SpeedLimiter(300_000);
        using var dl = new HttpDownloader(Fast(2), limiter);

        var sw = Stopwatch.StartNew();
        await dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("l.bin")), null, default);
        sw.Stop();

        // 600 kB bei 300 kB/s: etwa 2 s (minus kleiner Burst).
        Assert.InRange(sw.Elapsed.TotalSeconds, 1.4, 6.0);
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(dir.File("l.bin")));
    }

    [Fact]
    public void Unlimited_limiter_does_not_wait()
    {
        var limiter = new SpeedLimiter(0);
        var t = limiter.WaitAsync(10_000_000, default);
        Assert.True(t.IsCompletedSuccessfully);
    }

    [Fact]
    public void Client_options_apply_user_agent()
    {
        var options = new DownloadOptions { UserAgent = "Meins/9.9", Proxy = "http://127.0.0.1:1", IgnoreSslErrors = true };
        using var client = HttpDownloader.CreateClient(options);
        Assert.Contains("Meins/9.9", client.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Fact]
    public async Task Fatal_error_in_one_segment_stops_the_others_at_once()
    {
        using var server = new TestServer(4_000_000) { ThrottleBytesPerSecond = 200_000, FailRangesFrom = 3_000_000 };
        using var dir = new TempDir();
        using var dl = new HttpDownloader(Fast(4));

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("f.bin")), null, default));
        sw.Stop();

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        // Ohne sofortigen Abbruch laufen die übrigen Segmente noch etwa 5 s weiter.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"Dauer {sw.Elapsed}");
    }

    [Fact]
    public async Task Server_that_never_sends_headers_times_out_and_is_retried()
    {
        using var server = new TestServer(500_000) { StallSegmentRequests = 1 };
        using var dir = new TempDir();
        var options = Fast(1);
        options.ReadTimeout = TimeSpan.FromMilliseconds(300);
        using var dl = new HttpDownloader(options);

        await dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("h.bin")), null, default)
            .WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(server.Data, await File.ReadAllBytesAsync(dir.File("h.bin")));
    }

    [Fact]
    public async Task Connect_timeout_is_retried()
    {
        using var server = new TestServer(300_000);
        using var dir = new TempDir();
        int connects = 0;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
            ConnectCallback = async (context, token) =>
            {
                if (Interlocked.Increment(ref connects) == 1)
                    await Task.Delay(Timeout.Infinite, token); // erster Verbindungsaufbau hängt
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var dl = new HttpDownloader(Fast(1), client: client);

        await dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("t.bin")), null, default)
            .WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(connects >= 2);
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(dir.File("t.bin")));
    }

    [Fact]
    public async Task Failing_state_save_does_not_stop_progress_or_fail_the_download()
    {
        using var server = new TestServer(1_000_000) { ThrottleBytesPerSecond = 500_000 };
        using var dir = new TempDir();
        string path = dir.File("m.bin");
        var options = Fast(2);
        options.StateSaveInterval = TimeSpan.FromMilliseconds(250);
        using var dl = new HttpDownloader(options);

        int reportsAfterBlock = 0;
        bool blocked = false;
        var progress = new SyncProgress(p =>
        {
            if (blocked) reportsAfterBlock++;
            else if (p.DownloadedBytes > 0)
            {
                Directory.CreateDirectory(path + ".part.json.tmp"); // Speichern scheitert ab jetzt
                blocked = true;
            }
        });

        await dl.DownloadAsync(new DownloadRequest(server.Url, path), progress, default);

        Assert.Equal(server.Data, await File.ReadAllBytesAsync(path));
        Assert.True(reportsAfterBlock >= 2, $"nur {reportsAfterBlock} Meldungen");
    }

    [Fact]
    public async Task Saved_state_never_claims_bytes_missing_from_the_part_file()
    {
        // Einen Stromausfall kann der Test nicht nachstellen. Er sichert die Grundlage dafür:
        // Was die .part.json als geladen zählt, steht während des Downloads schon in der .part.
        using var server = new TestServer(2_000_000) { ThrottleBytesPerSecond = 1_000_000 };
        using var dir = new TempDir();
        string path = dir.File("v.bin");
        var options = Fast(4);
        options.StateSaveInterval = TimeSpan.FromMilliseconds(250);
        using var dl = new HttpDownloader(options);

        int checkedSegments = 0;
        var progress = new SyncProgress(_ =>
        {
            if (!File.Exists(path + ".part.json")) return;
            var meta = JsonSerializer.Deserialize<PartMeta>(File.ReadAllBytes(path + ".part.json"))!;
            using var part = new FileStream(path + ".part", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            foreach (var s in meta.Segments.Where(s => s.Done > 0))
            {
                var bytes = new byte[s.Done];
                part.Position = s.Start;
                part.ReadExactly(bytes);
                Assert.True(bytes.AsSpan().SequenceEqual(server.Data.AsSpan((int)s.Start, (int)s.Done)),
                    $"Segment ab {s.Start}: Meta zählt {s.Done} Bytes, die nicht in der .part stehen");
                checkedSegments++;
            }
        });

        await dl.DownloadAsync(new DownloadRequest(server.Url, path), progress, default);

        Assert.True(checkedSegments > 0);
    }

    [Fact]
    public async Task Cancel_is_reported_even_if_saving_state_fails()
    {
        using var server = new TestServer(1_000_000) { ThrottleBytesPerSecond = 300_000 };
        using var dir = new TempDir();
        string path = dir.File("k.bin");
        using var dl = new HttpDownloader(Fast(2));
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.DownloadedBytes == 0 || cts.IsCancellationRequested) return;
            Directory.CreateDirectory(path + ".part.json.tmp");
            cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dl.DownloadAsync(new DownloadRequest(server.Url, path), progress, cts.Token));
    }

    [Fact]
    public async Task Requests_ask_for_uncompressed_data()
    {
        using var server = new TestServer(500_000);
        using var dir = new TempDir();
        using var dl = new HttpDownloader(Fast(4));

        await dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("i.bin")), null, default);

        Assert.NotEmpty(server.AcceptEncodings);
        Assert.All(server.AcceptEncodings, v => Assert.Equal("identity", v));
    }

    [Fact]
    public async Task Segments_use_the_final_url_after_redirect()
    {
        using var server = new TestServer(2_000_000);
        using var dir = new TempDir();
        using var dl = new HttpDownloader(Fast(4));

        await dl.DownloadAsync(new DownloadRequest(server.RedirectUrl, dir.File("w.bin")), null, default);

        Assert.Equal(server.Data, await File.ReadAllBytesAsync(dir.File("w.bin")));
        Assert.Equal(1, server.Paths.Count(p => p == "/redirect"));
    }

    [Fact]
    public async Task Expired_redirect_target_falls_back_to_original_url()
    {
        // Jedes Weiterleitungsziel gilt nur für zwei Anfragen: Probe plus ein Segment.
        using var server = new TestServer(2_000_000) { TokenUses = 2 };
        using var dir = new TempDir();
        using var dl = new HttpDownloader(Fast(4));

        await dl.DownloadAsync(new DownloadRequest(server.RedirectUrl, dir.File("x.bin")), null, default);

        Assert.Equal(server.Data, await File.ReadAllBytesAsync(dir.File("x.bin")));
        Assert.True(server.Paths.Count(p => p == "/redirect") > 1);
    }

    [Fact]
    public async Task Changed_file_during_download_restarts_with_the_new_file()
    {
        using var server = new TestServer(2_000_000) { ThrottleBytesPerSecond = 1_000_000 };
        using var dir = new TempDir();
        string path = dir.File("n.bin");
        var newData = new byte[2_000_000];
        new Random(7).NextBytes(newData);
        using var dl = new HttpDownloader(Fast(4));

        bool swapped = false;
        var progress = new SyncProgress(p =>
        {
            if (swapped || p.DownloadedBytes < 300_000) return;
            swapped = true;
            server.Replace(newData, "\"v2\""); // gleiche Größe, neue ETag
        });

        await dl.DownloadAsync(new DownloadRequest(server.Url, path), progress, default);

        Assert.True(swapped);
        Assert.Contains("\"test-etag\"", server.IfRanges);
        Assert.Equal(newData, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Changed_file_between_pause_and_resume_restarts()
    {
        using var server = new TestServer(1_500_000) { ThrottleBytesPerSecond = 400_000 };
        using var dir = new TempDir();
        string path = dir.File("q.bin");
        using var dl = new HttpDownloader(Fast(3));
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.DownloadedBytes > 300_000) cts.Cancel(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dl.DownloadAsync(new DownloadRequest(server.Url, path), progress, cts.Token));

        var newData = new byte[1_500_000];
        new Random(8).NextBytes(newData);
        server.Replace(newData, "\"v2\"");
        server.ThrottleBytesPerSecond = 0;
        await dl.DownloadAsync(new DownloadRequest(server.Url, path), null, default);

        Assert.Equal(newData, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Shorter_range_than_requested_continues_without_retry_pause()
    {
        using var server = new TestServer(1_000_000) { MaxRangeLength = 100_000 };
        using var dir = new TempDir();
        var options = Fast(1);
        options.RetryDelay = TimeSpan.FromSeconds(5);
        using var dl = new HttpDownloader(options);

        var sw = Stopwatch.StartNew();
        await dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("o.bin")), null, default);
        sw.Stop();

        Assert.Equal(server.Data, await File.ReadAllBytesAsync(dir.File("o.bin")));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"Dauer {sw.Elapsed}");
    }

    [Fact]
    public async Task Single_stream_retries_and_reuses_the_probe_response()
    {
        using var server = new TestServer(800_000) { SupportsRanges = false, DropFirstResponses = 2, DropAfterBytes = 50_000 };
        using var dir = new TempDir();
        using var dl = new HttpDownloader(Fast(4));

        var result = await dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("u.bin")), null, default);

        Assert.Equal(1, result.Segments);
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(dir.File("u.bin")));
        Assert.Equal(3, server.RequestCount); // Probe-Antwort weitergelesen, zwei Abbrüche, kein zusätzlicher GET
    }

    [Fact]
    public async Task Retry_after_from_server_is_respected()
    {
        using var server = new TestServer(200_000) { FailFirstWith503 = 1, RetryAfterSeconds = 1 };
        using var dir = new TempDir();
        using var dl = new HttpDownloader(Fast(1)); // eigene Pause nur 20 ms

        var sw = Stopwatch.StartNew();
        await dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("ra.bin")), null, default);
        sw.Stop();

        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(900), $"Dauer {sw.Elapsed}");
    }

    [Fact]
    public async Task Not_implemented_is_not_retried()
    {
        using var server = new TestServer(200_000) { FailRangesFrom = 0, FailRangesStatus = 501 };
        using var dir = new TempDir();
        using var dl = new HttpDownloader(Fast(1));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("ni.bin")), null, default));

        Assert.Equal(HttpStatusCode.NotImplemented, ex.StatusCode);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public void Retry_classification()
    {
        Assert.False(HttpDownloader.IsRetryable(new StorageException(new IOException("Platte voll"))));
        Assert.False(HttpDownloader.IsRetryable(new ChecksumMismatchException("a", "b")));
        Assert.False(HttpDownloader.IsRetryable(new RemoteFileChangedException()));
        Assert.False(HttpDownloader.IsRetryable(new HttpRequestException("", null, HttpStatusCode.NotFound)));
        Assert.False(HttpDownloader.IsRetryable(new HttpRequestException("", null, HttpStatusCode.NotImplemented)));
        Assert.False(HttpDownloader.IsRetryable(new HttpRequestException("", null, HttpStatusCode.HttpVersionNotSupported)));

        Assert.True(HttpDownloader.IsRetryable(new HttpRequestException("Verbindung verweigert")));
        Assert.True(HttpDownloader.IsRetryable(new HttpRequestException("", null, HttpStatusCode.ServiceUnavailable)));
        Assert.True(HttpDownloader.IsRetryable(new HttpRequestException("", null, HttpStatusCode.TooManyRequests)));
        Assert.True(HttpDownloader.IsRetryable(new ServerBusyException(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(1))));
        Assert.True(HttpDownloader.IsRetryable(new IOException("Verbindung vorzeitig beendet.")));
        Assert.True(HttpDownloader.IsRetryable(new TimeoutException()));
    }

    [Fact]
    public void Retry_delays_are_spread_and_honor_retry_after()
    {
        using var dl = new HttpDownloader(new DownloadOptions { RetryDelay = TimeSpan.FromSeconds(1) });

        var delays = Enumerable.Range(0, 50).Select(_ => dl.RetryDelayFor(1).TotalMilliseconds).ToList();
        Assert.All(delays, d => Assert.InRange(d, 800, 1200));
        Assert.True(delays.Distinct().Count() > 1);

        var busy = new ServerBusyException(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(20));
        Assert.True(dl.RetryDelayFor(1, busy) >= TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Existing_destination_gets_a_free_name_by_default()
    {
        using var server = new TestServer(300_000);
        using var dir = new TempDir();
        string path = dir.File("e.bin");
        await File.WriteAllTextAsync(path, "alt");
        using var dl = new HttpDownloader(Fast(2));

        var result = await dl.DownloadAsync(new DownloadRequest(server.Url, path), null, default);

        Assert.Equal(dir.File("e (1).bin"), result.Path);
        Assert.Equal("alt", await File.ReadAllTextAsync(path));
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(result.Path));
    }

    [Fact]
    public async Task Existing_destination_fails_before_any_request_when_asked()
    {
        using var server = new TestServer(300_000);
        using var dir = new TempDir();
        string path = dir.File("f.bin");
        await File.WriteAllTextAsync(path, "alt");
        using var dl = new HttpDownloader(Fast(2));

        await Assert.ThrowsAsync<DestinationExistsException>(() => dl.DownloadAsync(
            new DownloadRequest(server.Url, path, IfExists: ExistingFileMode.Fail), null, default));

        Assert.Equal(0, server.RequestCount);
        Assert.Equal("alt", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Existing_destination_is_overwritten_when_asked()
    {
        using var server = new TestServer(300_000);
        using var dir = new TempDir();
        string path = dir.File("o.bin");
        await File.WriteAllTextAsync(path, "alt");
        using var dl = new HttpDownloader(Fast(2));

        var result = await dl.DownloadAsync(
            new DownloadRequest(server.Url, path, IfExists: ExistingFileMode.Overwrite), null, default);

        Assert.Equal(path, result.Path);
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Second_download_to_the_same_target_fails_without_disturbing_the_first(bool ranges)
    {
        using var server = new TestServer(1_000_000) { ThrottleBytesPerSecond = 250_000, SupportsRanges = ranges }; // lang genug, dass der erste auch auf einem ausgelasteten Rechner noch läuft
        using var dir = new TempDir();
        string path = dir.File("z.bin");
        using var dl = new HttpDownloader(Fast(2));
        var running = new TaskCompletionSource();

        var first = dl.DownloadAsync(new DownloadRequest(server.Url, path),
            new SyncProgress(p => { if (p.DownloadedBytes > 0) running.TrySetResult(); }), default);
        await running.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<StorageException>(
            () => dl.DownloadAsync(new DownloadRequest(server.Url, path), null, default));

        var result = await first;
        Assert.Equal(path, result.Path);
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(@"C:\d\a.bin", @"C:\d\a (2).bin")]
    [InlineData(@"C:\d\a.tar.gz", @"C:\d\a (2).tar.gz")]
    [InlineData(@"C:\d\ohne", @"C:\d\ohne (2)")]
    public void Numbered_names_keep_the_extension(string path, string expected)
        => Assert.Equal(expected, HttpDownloader.NumberedName(path, 2));

    [Fact]
    public async Task Probe_reports_name_size_and_final_url()
    {
        using var server = new TestServer(123_456) { ContentDisposition = "attachment; filename=\"Bericht: 2024.pdf\"" };
        using var dl = new HttpDownloader(Fast(1));

        var info = await dl.ProbeAsync(server.RedirectUrl, default);

        Assert.Equal(123_456, info.Size);
        Assert.True(info.SupportsRanges);
        Assert.Equal("Bericht_ 2024.pdf", info.FileName);
        Assert.Equal("/file.bin", info.FinalUrl.AbsolutePath);
        Assert.Equal("\"test-etag\"", info.ETag);
    }

    [Fact]
    public async Task Probe_falls_back_to_the_name_in_the_url()
    {
        using var server = new TestServer(1_000) { SupportsRanges = false };
        using var dl = new HttpDownloader(Fast(1));

        var info = await dl.ProbeAsync(server.Url, default);

        Assert.Equal("file.bin", info.FileName);
        Assert.Equal(1_000, info.Size);
        Assert.False(info.SupportsRanges);
    }

    [Fact]
    public async Task Part_file_is_sparse_while_loading_and_normal_when_done()
    {
        using var server = new TestServer(1_000_000) { ThrottleBytesPerSecond = 300_000 };
        using var dir = new TempDir();
        string path = dir.File("sp.bin");
        using var dl = new HttpDownloader(Fast(2));
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.DownloadedBytes > 100_000) cts.Cancel(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dl.DownloadAsync(new DownloadRequest(server.Url, path), progress, cts.Token));
        Assert.True(File.GetAttributes(path + ".part").HasFlag(FileAttributes.SparseFile));

        server.ThrottleBytesPerSecond = 0;
        await dl.DownloadAsync(new DownloadRequest(server.Url, path), null, default);

        Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.SparseFile));
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Not_enough_free_space_fails_before_loading()
    {
        using var server = new TestServer(100_000) { AnnouncedTotal = 1L << 50 }; // 1 PB
        using var dir = new TempDir();
        using var dl = new HttpDownloader(Fast(4));

        var ex = await Assert.ThrowsAsync<StorageException>(
            () => dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("big.bin")), null, default));

        Assert.Equal(unchecked((int)0x80070070), ex.HResult);
        Assert.Equal(1, server.RequestCount); // nur die Probe
    }

    [Fact]
    public async Task Events_report_final_url_and_range_support()
    {
        using var server = new TestServer(500_000);
        using var dir = new TempDir();
        using var dl = new HttpDownloader(Fast(2));
        var events = new EventLog();

        await dl.DownloadAsync(new DownloadRequest(server.RedirectUrl, dir.File("ev.bin"), Events: events), null, default);

        var info = Assert.Single(events.Probes);
        Assert.Equal("/file.bin", info.FinalUrl.AbsolutePath);
        Assert.Equal(500_000, info.Size);
        Assert.True(info.SupportsRanges);
        Assert.Empty(events.Retries);
    }

    [Fact]
    public async Task Events_report_no_range_support()
    {
        using var server = new TestServer(300_000) { SupportsRanges = false };
        using var dir = new TempDir();
        using var dl = new HttpDownloader(Fast(2));
        var events = new EventLog();

        await dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("ev2.bin"), Events: events), null, default);

        Assert.False(Assert.Single(events.Probes).SupportsRanges);
    }

    [Fact]
    public async Task Events_report_each_retry_of_the_probe_in_order()
    {
        using var server = new TestServer(300_000) { FailFirstWith503 = 2 };
        using var dir = new TempDir();
        using var dl = new HttpDownloader(Fast(1));
        var events = new EventLog();

        await dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("ev3.bin"), Events: events), null, default);

        Assert.Equal(new[] { 1, 2 }, events.Retries.Select(r => r.Attempt));
        Assert.All(events.Retries, r =>
        {
            Assert.Equal(-1, r.Segment); // die Abfrage gehört zu keiner Verbindung
            Assert.IsType<HttpRequestException>(r.Error);
            Assert.True(r.Delay > TimeSpan.Zero);
        });
    }

    [Fact]
    public async Task Events_name_the_connection_that_is_retried()
    {
        using var server = new TestServer(1_500_000) { DropFirstResponses = 2, DropAfterBytes = 50_000 };
        using var dir = new TempDir();
        using var dl = new HttpDownloader(Fast(3));
        var events = new EventLog();

        await dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("ev4.bin"), Events: events), null, default);

        Assert.NotEmpty(events.Retries);
        Assert.All(events.Retries, r =>
        {
            Assert.InRange(r.Segment, 0, 2);
            Assert.True(r.Attempt >= 1);
            Assert.IsAssignableFrom<IOException>(r.Error);
        });
        Assert.Single(events.Probes);
    }

    [Fact]
    public async Task Events_report_a_restart_when_the_file_changed()
    {
        using var server = new TestServer(2_000_000) { ThrottleBytesPerSecond = 1_000_000 };
        using var dir = new TempDir();
        var newData = new byte[2_000_000];
        new Random(9).NextBytes(newData);
        using var dl = new HttpDownloader(Fast(4));
        var events = new EventLog();

        bool swapped = false;
        var progress = new SyncProgress(p =>
        {
            if (swapped || p.DownloadedBytes < 300_000) return;
            swapped = true;
            server.Replace(newData, "\"v2\"");
        });

        await dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("ev5.bin"), Events: events), progress, default);

        Assert.Contains(events.Retries, r => r.Segment == -1 && r.Error is RemoteFileChangedException && r.Delay == TimeSpan.Zero);
        Assert.Equal(2, events.Probes.Count); // nach dem Neubeginn wird erneut abgefragt
    }

    [Fact]
    public async Task Result_tells_whether_the_checksum_was_verified()
    {
        using var server = new TestServer(300_000);
        using var dir = new TempDir();
        string good = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(server.Data));
        using var ranged = new HttpDownloader(Fast(2));

        var plain = await ranged.DownloadAsync(new DownloadRequest(server.Url, dir.File("v1.bin")), null, default);
        var checkedOne = await ranged.DownloadAsync(
            new DownloadRequest(server.Url, dir.File("v2.bin"), new ChecksumSpec(ChecksumAlgorithm.Sha256, good)), null, default);
        server.SupportsRanges = false;
        var single = await ranged.DownloadAsync(
            new DownloadRequest(server.Url, dir.File("v3.bin"), new ChecksumSpec(ChecksumAlgorithm.Sha256, good)), null, default);

        Assert.False(plain.ChecksumVerified);
        Assert.True(checkedOne.ChecksumVerified);
        Assert.True(single.ChecksumVerified);
    }

    private sealed record RetryEvent(int Segment, int Attempt, Exception Error, TimeSpan Delay);

    private sealed class EventLog : IDownloadEvents
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<RemoteFileInfo> _probes = new();
        private readonly System.Collections.Concurrent.ConcurrentQueue<RetryEvent> _retries = new();

        public System.Collections.Generic.IReadOnlyCollection<RemoteFileInfo> Probes => _probes;
        public System.Collections.Generic.IReadOnlyCollection<RetryEvent> Retries => _retries;

        public void Probed(RemoteFileInfo info) => _probes.Enqueue(info);
        public void Retrying(int segment, int attempt, Exception error, TimeSpan delay) =>
            _retries.Enqueue(new RetryEvent(segment, attempt, error, delay));
    }

    private sealed class SyncProgress : IProgress<DownloadProgress>
    {
        private readonly Action<DownloadProgress> _action;
        public SyncProgress(Action<DownloadProgress> action) => _action = action;
        public void Report(DownloadProgress value) => _action(value);
    }
}
