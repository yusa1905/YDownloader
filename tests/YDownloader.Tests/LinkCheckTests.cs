using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YDownloader.Debrid;

namespace YDownloader.Tests;

/// <summary>Online-Check gegen die Attrappe <see cref="FakeAllDebrid"/> (ausgedachter Key, nie der echte Dienst).</summary>
public class LinkCheckTests : IDisposable
{
    private readonly FakeAllDebrid _fake = new();
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public void Dispose()
    {
        _http.Dispose();
        _fake.Dispose();
    }

    private AllDebridProvider Provider() => new(_http, _fake.BaseUri) { RateLimitPause = TimeSpan.FromMilliseconds(10) };

    private DebridService Service(bool enabled = true)
    {
        var settings = new DebridSettings { Enabled = enabled, ApiKey = FakeAllDebrid.GoodKey };
        return new DebridService(Provider(), () => settings);
    }

    private int InfoCalls => _fake.Log.Count(r => r.PathAndQuery.EndsWith("link/infos", StringComparison.Ordinal));

    [Fact]
    public async Task Provider_maps_online_offline_protected_and_unknown_in_input_order()
    {
        string[] links =
        {
            "https://mega.nz/file/ok", "https://mega.nz/file/down", "https://mega.nz/file/pass",
            "https://mega.nz/file/unsupported", "https://mega.nz/file/weird", "https://mega.nz/file/nosize",
        };

        var infos = await Provider().CheckLinksAsync(FakeAllDebrid.GoodKey, links, default);

        Assert.Equal(links, infos.Select(i => i.Link));
        Assert.Equal(new[] { LinkState.Online, LinkState.Offline, LinkState.PasswordProtected, LinkState.Unknown, LinkState.Unknown, LinkState.Online },
            infos.Select(i => i.State));
        Assert.Equal("Checked Name.bin", infos[0].FileName);
        Assert.Equal(4242, infos[0].Size);
        Assert.Null(infos[1].FileName);
        Assert.Equal(-1, infos[1].Size);
        Assert.Equal(-1, infos[5].Size);
    }

    [Fact]
    public async Task Provider_sends_links_in_the_body_with_the_bearer_header_never_in_the_address()
    {
        await Provider().CheckLinksAsync(FakeAllDebrid.GoodKey, new[] { "https://mega.nz/file/a?x=1&y=2", "https://mega.nz/file/b" }, default);

        var request = Assert.Single(_fake.Log);
        Assert.Equal("POST", request.Method);
        Assert.Equal("Bearer " + FakeAllDebrid.GoodKey, request.Auth);
        Assert.DoesNotContain("mega", request.PathAndQuery);
        Assert.DoesNotContain(FakeAllDebrid.GoodKey, request.PathAndQuery + request.Body);
        // Links mit & und = überstehen die Kodierung unverändert
        Assert.Equal(new[] { "https://mega.nz/file/a?x=1&y=2", "https://mega.nz/file/b" }, Assert.Single(_fake.InfoRequests));
    }

    [Fact]
    public async Task Provider_marks_links_the_service_left_out_as_unknown()
    {
        var infos = await Provider().CheckLinksAsync(FakeAllDebrid.GoodKey,
            new[] { "https://mega.nz/file/ok", "https://mega.nz/file/missing" }, default);

        Assert.Equal(LinkState.Online, infos[0].State);
        Assert.Equal(LinkState.Unknown, infos[1].State);
        Assert.Equal("https://mega.nz/file/missing", infos[1].Link);
    }

    [Fact]
    public async Task Provider_rejects_non_http_links_before_sending_and_bad_keys_with_an_error()
    {
        var ex = await Assert.ThrowsAsync<DebridException>(
            () => Provider().CheckLinksAsync(FakeAllDebrid.GoodKey, new[] { "ftp://mega.nz/x" }, default));
        Assert.Equal(DebridError.BadResponse, ex.Error);
        Assert.Equal(0, _fake.RequestCount);

        var bad = await Assert.ThrowsAsync<DebridException>(
            () => Provider().CheckLinksAsync("wrong-key", new[] { "https://mega.nz/file/ok" }, default));
        Assert.Equal(DebridError.BadKey, bad.Error);

        var format = await Assert.ThrowsAsync<DebridException>(
            () => Provider().CheckLinksAsync("key with space", new[] { "https://mega.nz/file/ok" }, default));
        Assert.Equal(DebridError.KeyFormat, format.Error);
    }

    [Theory]
    [InlineData("{\"status\":\"success\",\"data\":{}}")]
    [InlineData("{\"status\":\"success\",\"data\":{\"infos\":\"nope\"}}")]
    public async Task Provider_reports_a_nonsense_answer_as_bad_response(string body)
    {
        _fake.InfosOverride = (200, body);

        var ex = await Assert.ThrowsAsync<DebridException>(
            () => Provider().CheckLinksAsync(FakeAllDebrid.GoodKey, new[] { "https://mega.nz/file/ok" }, default));
        Assert.Equal(DebridError.BadResponse, ex.Error);
    }

    [Fact]
    public async Task Provider_matches_by_position_when_the_service_changes_the_echoed_link()
    {
        _fake.InfosOverride = (200, "{\"status\":\"success\",\"data\":{\"infos\":[" +
            "{\"link\":\"https://mega.nz/other\",\"filename\":\"A.bin\",\"size\":\"7\"}," +
            "{\"link\":\"https://mega.nz/other2\",\"error\":{\"code\":\"LINK_DOWN\",\"message\":\"x\"}}]}}");

        var infos = await Provider().CheckLinksAsync(FakeAllDebrid.GoodKey,
            new[] { "https://mega.nz/file/1", "https://mega.nz/file/2" }, default);

        Assert.Equal("https://mega.nz/file/1", infos[0].Link);
        Assert.Equal(7, infos[0].Size); // Zahl als Text
        Assert.Equal(LinkState.Offline, infos[1].State);
    }

    // ---------------------------------------------------------------- Dienst

    [Fact]
    public async Task Service_sends_only_links_of_listed_hosters()
    {
        var service = Service();
        await service.LoadHostsAsync(false, default);
        var got = new List<LinkInfo>();

        int sent = await service.CheckLinksAsync(
            new[] { "https://mega.nz/file/a", "https://cdn.mega.nz/b", "https://example.org/c", "ftp://mega.nz/d", "kein link", "https://mega.nz/file/a" },
            r => { lock (got) got.AddRange(r); }, default);

        Assert.Equal(2, sent); // das Duplikat zählt einmal
        Assert.Equal(new[] { "https://mega.nz/file/a", "https://cdn.mega.nz/b" }, got.Select(i => i.Link));
        Assert.Equal(new[] { "https://mega.nz/file/a", "https://cdn.mega.nz/b" }, Assert.Single(_fake.InfoRequests));
    }

    [Fact]
    public async Task Service_without_a_loaded_list_or_when_off_sends_nothing()
    {
        var withoutList = Service();
        Assert.Equal(0, await withoutList.CheckLinksAsync(new[] { "https://mega.nz/file/a" }, _ => { }, default));

        var off = Service(enabled: false);
        await off.LoadHostsAsync(false, default);
        Assert.Equal(0, await off.CheckLinksAsync(new[] { "https://mega.nz/file/a" }, _ => { }, default));

        Assert.Equal(0, InfoCalls);
    }

    [Fact]
    public async Task Service_splits_large_lists_into_batches_and_delivers_each()
    {
        var service = Service();
        await service.LoadHostsAsync(false, default);
        var links = Enumerable.Range(0, 120).Select(i => $"https://mega.nz/file/{i}").ToList();
        var batches = new List<int>();

        int sent = await service.CheckLinksAsync(links, r => { lock (batches) batches.Add(r.Count); }, default);

        Assert.Equal(120, sent);
        Assert.Equal(new[] { 50, 50, 20 }, batches);
        Assert.Equal(3, InfoCalls);
    }

    [Fact]
    public async Task Service_keeps_delivered_results_when_a_later_batch_fails()
    {
        var service = Service();
        await service.LoadHostsAsync(false, default);
        var links = Enumerable.Range(0, 60).Select(i => $"https://mega.nz/file/{i}").ToList();
        var delivered = 0;

        await Assert.ThrowsAsync<DebridException>(() => service.CheckLinksAsync(links, r =>
        {
            delivered += r.Count;
            _fake.InfosOverride = (200, "<html>nope</html>"); // die zweite Anfrage scheitert
        }, default));

        Assert.Equal(50, delivered);
    }

    [Fact]
    public async Task Service_stops_on_cancel()
    {
        var service = Service();
        await service.LoadHostsAsync(false, default);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CheckLinksAsync(new[] { "https://mega.nz/file/a" }, _ => { }, cts.Token));
        Assert.Equal(0, InfoCalls);
    }
}
