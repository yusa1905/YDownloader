using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YDownloader.Engine;

namespace YDownloader.Tests;

public class ConnectionGateTests
{
    [Fact]
    public async Task Never_lets_more_than_the_limit_through_at_once()
    {
        var gate = new ConnectionGate(3);
        int inside = 0, max = 0;

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            using var slot = await gate.AcquireAsync(default);
            int now = Interlocked.Increment(ref inside);
            int seen;
            while (now > (seen = Volatile.Read(ref max)) && Interlocked.CompareExchange(ref max, now, seen) != seen) { }
            await Task.Delay(15);
            Interlocked.Decrement(ref inside);
        })).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(3, max);
        Assert.Equal(0, gate.Active);
        Assert.Equal(0, gate.Waiting);
    }

    [Fact]
    public async Task Waiters_are_served_in_arrival_order()
    {
        var gate = new ConnectionGate(1);
        var first = await gate.AcquireAsync(default);
        var order = new List<int>();
        var waiters = Enumerable.Range(1, 4).Select(async i =>
        {
            using var slot = await gate.AcquireAsync(default);
            lock (order) order.Add(i);
            await Task.Delay(5);
        }).ToList();
        // Jeder Wartende steht in der Schlange, bevor der nächste kommt (die Aufrufe oben laufen synchron bis zum Warten).
        Assert.Equal(4, gate.Waiting);

        first.Dispose();
        await Task.WhenAll(waiters).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { 1, 2, 3, 4 }, order);
    }

    [Fact]
    public async Task Canceled_waiter_gives_up_and_leaks_no_slot()
    {
        var gate = new ConnectionGate(1);
        var held = await gate.AcquireAsync(default);
        using var cts = new CancellationTokenSource();
        var waiting = gate.AcquireAsync(cts.Token).AsTask();
        Assert.Equal(1, gate.Waiting);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, gate.Waiting);

        held.Dispose();
        Assert.Equal(0, gate.Active);
        var next = gate.AcquireAsync(default);
        Assert.True(next.IsCompletedSuccessfully); // der Platz ist wirklich frei
        (await next).Dispose();
    }

    [Fact]
    public async Task Already_canceled_token_does_not_take_a_slot()
    {
        var gate = new ConnectionGate(2);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await gate.AcquireAsync(cts.Token));

        Assert.Equal(0, gate.Active);
    }

    [Fact]
    public async Task Raising_the_limit_wakes_waiters_and_lowering_it_holds_back_new_ones()
    {
        var gate = new ConnectionGate(1);
        var a = await gate.AcquireAsync(default);
        var b = gate.AcquireAsync(default).AsTask();
        var c = gate.AcquireAsync(default).AsTask();
        Assert.False(b.IsCompleted);

        gate.Limit = 3;
        var leaseB = await b.WaitAsync(TimeSpan.FromSeconds(2));
        var leaseC = await c.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(3, gate.Active);

        gate.Limit = 1; // laufende bleiben, neue warten
        var d = gate.AcquireAsync(default).AsTask();
        Assert.False(d.IsCompleted);
        a.Dispose();
        leaseB.Dispose();
        Assert.False(d.IsCompleted); // erst wenn weniger als das Limit aktiv sind
        leaseC.Dispose();
        (await d.WaitAsync(TimeSpan.FromSeconds(2))).Dispose();
    }

    [Fact]
    public async Task Zero_means_unlimited()
    {
        var gate = new ConnectionGate();
        var leases = new List<IDisposable>();
        for (int i = 0; i < 100; i++)
        {
            var t = gate.AcquireAsync(default);
            Assert.True(t.IsCompletedSuccessfully);
            leases.Add(await t);
        }
        Assert.Equal(100, gate.Active);
        leases.ForEach(l => l.Dispose());
    }

    [Fact]
    public async Task Disposing_a_lease_twice_frees_only_one_slot()
    {
        var gate = new ConnectionGate(2);
        var a = await gate.AcquireAsync(default);
        var b = await gate.AcquireAsync(default);

        a.Dispose();
        a.Dispose();

        Assert.Equal(1, gate.Active);
        b.Dispose();
    }
}

public class HostRegistryTests
{
    private static Uri U(string host) => new($"http://{host}/file");

    [Fact]
    public void Same_host_shares_one_context_and_hosts_are_separate()
    {
        var hosts = new HostRegistry();
        Assert.Same(hosts.Resolve(U("a.example")), hosts.Resolve(U("A.Example")));
        Assert.NotSame(hosts.Resolve(U("a.example")), hosts.Resolve(U("b.example")));
    }

    [Fact]
    public void Default_limit_applies_to_all_hosts_and_follows_changes()
    {
        var hosts = new HostRegistry { DefaultMaxConnections = 6 };
        var ctx = hosts.Resolve(U("a.example"));
        Assert.Equal(6, ctx.Gate.Limit);

        hosts.DefaultMaxConnections = 2;
        Assert.Equal(2, ctx.Gate.Limit);
        Assert.Equal(2, hosts.Resolve(U("new.example")).Gate.Limit);
    }

    [Fact]
    public void Rule_covers_subdomains_but_not_look_alikes()
    {
        var hosts = new HostRegistry { DefaultMaxConnections = 8 };
        hosts.SetRules(new[] { new HostRule("example.com", MaxConnections: 2) });

        Assert.Equal(2, hosts.Resolve(U("example.com")).Gate.Limit);
        Assert.Equal(2, hosts.Resolve(U("cdn.example.com")).Gate.Limit);
        Assert.Equal(8, hosts.Resolve(U("notexample.com")).Gate.Limit);
        Assert.Equal(8, hosts.Resolve(U("example.com.evil.test")).Gate.Limit);
    }

    [Fact]
    public void Subdomains_under_one_rule_share_their_connections()
    {
        var hosts = new HostRegistry();
        hosts.SetRules(new[] { new HostRule("example.com", MaxConnections: 2) });
        Assert.Same(hosts.Resolve(U("a.example.com")), hosts.Resolve(U("b.example.com")));
    }

    [Fact]
    public void Most_specific_rule_wins()
    {
        var hosts = new HostRegistry();
        hosts.SetRules(new[]
        {
            new HostRule("example.com", MaxConnections: 2, UserAgent: "Allgemein"),
            new HostRule("cdn.example.com", MaxConnections: 5, UserAgent: "Cdn"),
        });

        var cdn = hosts.Resolve(U("eu.cdn.example.com"));
        Assert.Equal(5, cdn.Gate.Limit);
        Assert.Equal("Cdn", cdn.UserAgent);
        Assert.Equal(2, hosts.Resolve(U("www.example.com")).Gate.Limit);
    }

    [Fact]
    public void Changing_rules_updates_a_context_that_is_already_in_use()
    {
        var hosts = new HostRegistry { DefaultMaxConnections = 4 };
        hosts.SetRules(new[] { new HostRule("example.com", 3, 500_000, "Alt/1") });
        var ctx = hosts.Resolve(U("example.com"));
        Assert.Equal(3, ctx.Gate.Limit);
        Assert.Equal(500_000, ctx.Limiter.BytesPerSecond);
        Assert.Equal("Alt/1", ctx.UserAgent);

        hosts.SetRules(new[] { new HostRule("example.com", 1, 0, null) });
        Assert.Same(ctx, hosts.Resolve(U("example.com")));
        Assert.Equal(1, ctx.Gate.Limit);
        Assert.Equal(0, ctx.Limiter.BytesPerSecond);
        Assert.Null(ctx.UserAgent);

        hosts.SetRules(Array.Empty<HostRule>()); // Regel weg: wieder die Vorgabe
        Assert.Equal(4, ctx.Gate.Limit);
    }

    [Fact]
    public void Rule_with_zero_connections_falls_back_to_the_default()
    {
        var hosts = new HostRegistry { DefaultMaxConnections = 7 };
        hosts.SetRules(new[] { new HostRule("example.com", 0, 100_000) });
        var ctx = hosts.Resolve(U("example.com"));
        Assert.Equal(7, ctx.Gate.Limit);
        Assert.Equal(100_000, ctx.Limiter.BytesPerSecond);
    }

    [Fact]
    public void Invalid_hosts_are_dropped_and_duplicates_keep_the_last()
    {
        var hosts = new HostRegistry();
        hosts.SetRules(new[]
        {
            new HostRule("  ", 2),
            new HostRule("Example.com", 2),
            new HostRule("https://example.com/x", 5),
        });
        Assert.Single(hosts.Rules);
        Assert.Equal("example.com", hosts.Rules[0].Host);
        Assert.Equal(5, hosts.Rules[0].MaxConnections);
    }

    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("  Example.COM ", "example.com")]
    [InlineData("*.example.com", "example.com")]
    [InlineData(".example.com.", "example.com")]
    [InlineData("example.com:8080", "example.com")]
    [InlineData("https://www.Example.com:8080/pfad?x=1", "www.example.com")]
    [InlineData("example.com/pfad", "example.com")]
    [InlineData("[::1]", "[::1]")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("http://", null)]
    [InlineData("a b.com", null)]
    [InlineData("user@example.com", null)]
    public void Normalizes_host_input(string input, string? expected) =>
        Assert.Equal(expected, HostRegistry.NormalizeHost(input));
}

public class HttpClientPoolTests
{
    [Fact]
    public void Same_network_settings_share_one_client_even_with_different_user_agents()
    {
        using var pool = new HttpClientPool();
        var a = pool.Get(new DownloadOptions { UserAgent = "Eins/1" });
        var b = pool.Get(new DownloadOptions { UserAgent = "Zwei/2", Segments = 2 });

        Assert.Same(a, b);
        Assert.Equal(1, pool.Count);
    }

    [Fact]
    public void Different_proxy_or_ssl_setting_gets_its_own_client()
    {
        using var pool = new HttpClientPool();
        var plain = pool.Get(new DownloadOptions());
        var proxy = pool.Get(new DownloadOptions { Proxy = "http://127.0.0.1:1" });
        var lax = pool.Get(new DownloadOptions { IgnoreSslErrors = true });

        Assert.NotSame(plain, proxy);
        Assert.NotSame(plain, lax);
        Assert.NotSame(proxy, lax);
        Assert.Same(proxy, pool.Get(new DownloadOptions { Proxy = " http://127.0.0.1:1 " }));
        Assert.Equal(3, pool.Count);
    }

    [Fact]
    public void Empty_proxy_counts_as_no_proxy()
    {
        using var pool = new HttpClientPool();
        Assert.Same(pool.Get(new DownloadOptions { Proxy = null }), pool.Get(new DownloadOptions { Proxy = "  " }));
    }

    [Fact]
    public void Pool_is_unusable_after_dispose()
    {
        var pool = new HttpClientPool();
        var client = pool.Get(new DownloadOptions());
        pool.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pool.Get(new DownloadOptions()));
        Assert.Throws<ObjectDisposedException>(() => client.GetAsync("http://127.0.0.1:1/").GetAwaiter().GetResult());
    }
}

public class HostRuleDownloadTests
{
    private static DownloadOptions Fast(int segments) => new()
    {
        Segments = segments,
        MinSegmentSize = 64 * 1024,
        RetryDelay = TimeSpan.FromMilliseconds(20),
        MaxRetries = 3,
        ReadTimeout = TimeSpan.FromSeconds(5),
        UserAgent = "Standard/1",
    };

    [Fact]
    public async Task Host_rule_sets_the_user_agent_for_every_request()
    {
        using var server = new TestServer(300_000);
        using var dir = new TempDir();
        var hosts = new HostRegistry();
        hosts.SetRules(new[] { new HostRule("127.0.0.1", UserAgent: "Sonder/7") });
        using var dl = new HttpDownloader(Fast(2), hosts: hosts);

        await dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("a.bin")), null, default);
        await dl.ProbeAsync(server.Url, default);

        Assert.True(server.UserAgents.Count >= 3); // Abfrage, zwei Segmente, Abfrage des Dialogs
        Assert.All(server.UserAgents, ua => Assert.Equal("Sonder/7", ua));
    }

    [Fact]
    public async Task Hosts_without_a_rule_get_the_general_user_agent()
    {
        using var server = new TestServer(300_000);
        using var dir = new TempDir();
        var hosts = new HostRegistry();
        hosts.SetRules(new[] { new HostRule("other.example", UserAgent: "Sonder/7") });
        using var dl = new HttpDownloader(Fast(2), hosts: hosts);

        await dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("a.bin")), null, default);

        Assert.All(server.UserAgents, ua => Assert.Equal("Standard/1", ua));
    }

    [Fact]
    public async Task Host_speed_limit_is_shared_by_all_downloads_from_that_host()
    {
        using var server = new TestServer(300_000);
        using var dir = new TempDir();
        var hosts = new HostRegistry();
        hosts.SetRules(new[] { new HostRule("127.0.0.1", BytesPerSecond: 300_000) });
        using var dl = new HttpDownloader(Fast(2), hosts: hosts);

        var sw = Stopwatch.StartNew();
        await Task.WhenAll(
            dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("a.bin")), null, default),
            dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("b.bin")), null, default))
            .WaitAsync(TimeSpan.FromSeconds(30));
        sw.Stop();

        // 600 kB zusammen bei 300 kB/s: etwa 1,75 s (nach Abzug des kleinen Anfangsschubs). Ohne Limit unter 0,5 s.
        Assert.InRange(sw.Elapsed.TotalSeconds, 1.3, 8.0);
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(dir.File("b.bin")));
    }

    [Fact]
    public async Task Other_hosts_are_not_slowed_by_a_rule()
    {
        using var server = new TestServer(300_000);
        using var dir = new TempDir();
        var hosts = new HostRegistry();
        hosts.SetRules(new[] { new HostRule("other.example", BytesPerSecond: 10_000) });
        using var dl = new HttpDownloader(Fast(2), hosts: hosts);

        var sw = Stopwatch.StartNew();
        await dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("a.bin")), null, default)
            .WaitAsync(TimeSpan.FromSeconds(10));

        // Mit der Regel (10 kB/s) dauerte es 30 s; die Grenze liegt weit davon, aber über jeder Lastspitze.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"Dauer {sw.Elapsed}");
    }
}

public class HostSettingsTests
{
    [Fact]
    public void Setting_converts_to_a_rule_with_kilobytes_of_1024()
    {
        var rule = new HostSetting { Host = "example.com", MaxConnections = 3, SpeedLimitKBps = 500, UserAgent = "X/1" }.ToRule();
        Assert.Equal(new HostRule("example.com", 3, 512_000, "X/1"), rule);
    }

    [Fact]
    public void Hosts_survive_a_save_and_load_and_old_files_without_hosts_still_load()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        var settings = new AppSettings { ConnectionsPerHost = 0 };
        settings.Hosts.Add(new HostSetting { Host = "example.com", MaxConnections = 2, SpeedLimitKBps = 800, UserAgent = "Ä/1" });
        AppSettings.Write(path, settings.ToJson());

        var loaded = AppSettings.Load(path);
        Assert.Equal(0, loaded.ConnectionsPerHost);
        var host = Assert.Single(loaded.Hosts);
        Assert.Equal("example.com", host.Host);
        Assert.Equal(2, host.MaxConnections);
        Assert.Equal(800, host.SpeedLimitKBps);
        Assert.Equal("Ä/1", host.UserAgent);

        File.WriteAllText(dir.File("old.json"), "{ \"maxConcurrent\": 5 }");
        var old = AppSettings.Load(dir.File("old.json"));
        Assert.Equal(5, old.MaxConcurrent);
        Assert.Equal(16, old.ConnectionsPerHost);
        Assert.Empty(old.Hosts);

        File.WriteAllText(dir.File("null.json"), "{ \"hosts\": null }");
        Assert.Empty(AppSettings.Load(dir.File("null.json")).Hosts);
    }
}

public class HostLimitDownloadTests
{
    private static DownloadOptions Fast(int segments) => new()
    {
        Segments = segments,
        MinSegmentSize = 64 * 1024,
        RetryDelay = TimeSpan.FromMilliseconds(20),
        MaxRetries = 3,
        ReadTimeout = TimeSpan.FromSeconds(5),
    };

    [Fact]
    public async Task Host_limit_caps_connections_across_downloads_and_everything_arrives()
    {
        using var server = new TestServer(1_200_000) { ThrottleBytesPerSecond = 600_000 };
        using var dir = new TempDir();
        using var pool = new HttpClientPool();
        var hosts = new HostRegistry { DefaultMaxConnections = 3 };
        var options = Fast(4);
        using var dl = new HttpDownloader(options, null, pool.Get(options), hosts);

        var a = dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("a.bin")), null, default);
        var b = dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("b.bin")), null, default);
        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(30));

        // Ohne Grenze liefen 2 × 4 Segmente gleichzeitig.
        Assert.Equal(3, server.MaxConcurrentRequests);
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(dir.File("b.bin")));
        Assert.Equal(0, hosts.Resolve(new Uri(server.Url)).Gate.Active);
    }

    [Fact]
    public async Task Without_a_host_limit_all_segments_run_at_once()
    {
        using var server = new TestServer(1_200_000) { ThrottleBytesPerSecond = 600_000 };
        using var dir = new TempDir();
        var options = Fast(4);
        using var dl = new HttpDownloader(options, hosts: new HostRegistry());

        await dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("a.bin")), null, default)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(4, server.MaxConcurrentRequests);
    }

    [Fact]
    public async Task Single_stream_download_respects_the_limit_and_does_not_deadlock()
    {
        using var server = new TestServer(600_000) { SupportsRanges = false, ThrottleBytesPerSecond = 600_000 };
        using var dir = new TempDir();
        var hosts = new HostRegistry { DefaultMaxConnections = 1 };
        using var dl = new HttpDownloader(Fast(4), hosts: hosts);

        var a = dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("a.bin")), null, default);
        var b = dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("b.bin")), null, default);
        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, server.MaxConcurrentRequests);
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(dir.File("a.bin")));
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(dir.File("b.bin")));
        Assert.Equal(0, hosts.Resolve(new Uri(server.Url)).Gate.Active);
    }

    [Fact]
    public async Task Canceling_while_waiting_for_a_slot_stops_quickly_and_frees_nothing_it_never_had()
    {
        using var server = new TestServer(400_000);
        using var dir = new TempDir();
        var hosts = new HostRegistry { DefaultMaxConnections = 1 };
        using var dl = new HttpDownloader(Fast(2), hosts: hosts);
        var gate = hosts.Resolve(new Uri(server.Url)).Gate;
        var held = await gate.AcquireAsync(default); // ein anderer Download belegt den einzigen Platz

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("c.bin")), null, cts.Token));

        Assert.Equal(0, server.RequestCount);
        Assert.Equal(1, gate.Active);
        Assert.Equal(0, gate.Waiting);
        held.Dispose();

        // Danach läuft der Download normal durch.
        await dl.DownloadAsync(new DownloadRequest(server.Url, dir.File("c.bin")), null, default)
            .WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(dir.File("c.bin")));
    }

    [Fact]
    public async Task Probe_for_the_add_dialog_does_not_wait_for_a_full_host()
    {
        using var server = new TestServer(400_000);
        var hosts = new HostRegistry { DefaultMaxConnections = 1 };
        using var dl = new HttpDownloader(Fast(2), hosts: hosts);
        using var held = await hosts.Resolve(new Uri(server.Url)).Gate.AcquireAsync(default);

        var info = await dl.ProbeAsync(server.Url, default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(400_000, info.Size);
    }
}
