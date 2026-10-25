using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using YDownloader.Debrid;
using YDownloader.Engine;

namespace YDownloader.Tests;

/// <summary>Meldet sofort auf dem aufrufenden Thread (Progress&lt;T&gt; würde über den Thread-Pool verzögern).</summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _action;
    public SyncProgress(Action<T> action) => _action = action;
    public void Report(T value) => _action(value);
}

public class DebridTests : IDisposable
{
    private readonly FakeAllDebrid _fake = new();
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public void Dispose()
    {
        _http.Dispose();
        _fake.Dispose();
    }

    private AllDebridProvider Provider() => new(_http, _fake.BaseUri)
    {
        PollInterval = TimeSpan.FromMilliseconds(10),
        RateLimitPause = TimeSpan.FromMilliseconds(10),
    };

    private static async Task<DebridException> Fails(Task task)
    {
        var ex = await Assert.ThrowsAsync<DebridException>(() => task);
        return ex;
    }

    // ---------------------------------------------------------------- Konto

    [Fact]
    public async Task Account_is_read_with_bearer_header_and_key_never_in_the_address()
    {
        var account = await Provider().CheckAccountAsync(FakeAllDebrid.GoodKey, default);

        Assert.Equal("tester", account.UserName);
        Assert.True(account.IsPremium);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893456000), account.PremiumUntil);
        var request = Assert.Single(_fake.Log);
        Assert.Equal("Bearer " + FakeAllDebrid.GoodKey, request.Auth);
        Assert.DoesNotContain(FakeAllDebrid.GoodKey, request.PathAndQuery);
        Assert.DoesNotContain("apikey", request.PathAndQuery, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("1893456000000")]        // Millisekunden statt Sekunden
    [InlineData("99999999999999999")]
    [InlineData("-5")]
    [InlineData("\"bald\"")]
    public async Task Odd_premium_end_gives_an_account_without_date_instead_of_a_crash(string value)
    {
        _fake.PremiumUntil = value;

        var account = await Provider().CheckAccountAsync(FakeAllDebrid.GoodKey, default);

        Assert.True(account.IsPremium);
        Assert.Null(account.PremiumUntil);
    }

    [Fact]
    public async Task Wrong_key_is_reported_as_bad_key_and_message_has_no_key()
    {
        const string wrong = "wrong-key-not-real";
        var ex = await Fails(Provider().CheckAccountAsync(wrong, default));

        Assert.Equal(DebridError.BadKey, ex.Error);
        Assert.Equal("AUTH_BAD_APIKEY", ex.Code);
        Assert.DoesNotContain(wrong, ex.ToString());
    }

    [Theory]
    [InlineData("test-key\nnot-real")]
    [InlineData("test key not real")]
    [InlineData("test-key-ä-not-real")]
    [InlineData("test-key-​-not-real")] // unsichtbares Zeichen aus dem Kopieren
    [InlineData("test-key\r\nX-Other: 1")]
    public async Task Key_with_invalid_characters_is_never_sent(string key)
    {
        Assert.False(DebridSettings.IsValidKey(key));

        var ex = await Fails(Provider().CheckAccountAsync(key, default));
        var unlock = await Fails(Provider().UnlockAsync(key, "https://mega.nz/file/ok", null, null, default));

        Assert.Equal(DebridError.KeyFormat, ex.Error);
        Assert.Equal(DebridError.KeyFormat, unlock.Error);
        Assert.Equal(0, _fake.RequestCount);
        Assert.DoesNotContain(key, ex.ToString());
        Assert.DoesNotContain(key, Downloads.ErrorText.Describe(ex));
    }

    [Fact]
    public void Normal_key_is_valid()
    {
        Assert.True(DebridSettings.IsValidKey(FakeAllDebrid.GoodKey));
        Assert.True(DebridSettings.IsValidKey("AbC123_-.~"));
        Assert.False(DebridSettings.IsValidKey(null));
        Assert.False(DebridSettings.IsValidKey(new string('a', 300)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_key_fails_without_asking_the_service(string key)
    {
        var ex = await Fails(Provider().CheckAccountAsync(key, default));

        Assert.Equal(DebridError.NoKey, ex.Error);
        Assert.Equal(0, _fake.RequestCount);
    }

    // ---------------------------------------------------------------- Hoster

    [Fact]
    public async Task Host_list_is_normalized_sorted_and_needs_no_key()
    {
        var hosts = await Provider().GetHostsAsync(default);

        Assert.Equal(new[] { "mega.nz", "rapidgator.net", "uploaded.net" }, hosts);
        Assert.All(_fake.Log, r => Assert.Null(r.Auth));
    }

    [Fact]
    public async Task Host_list_with_unexpected_shape_is_a_bad_response()
    {
        _fake.HostsOverride = (200, "{\"status\":\"success\",\"data\":{\"hosts\":\"nope\"}}");

        Assert.Equal(DebridError.BadResponse, (await Fails(Provider().GetHostsAsync(default))).Error);
    }

    // ---------------------------------------------------------------- Entsperren

    [Fact]
    public async Task Unlock_returns_direct_link_name_and_size_and_keeps_link_out_of_the_address()
    {
        var link = await Provider().UnlockAsync(FakeAllDebrid.GoodKey, "https://mega.nz/file/ok", null, null, default);

        Assert.Equal(_fake.DirectUrl, link.Url);
        Assert.Equal("Real Name.bin", link.FileName);
        Assert.Equal(12345, link.Size);
        var request = Assert.Single(_fake.Log);
        Assert.Equal("POST", request.Method);
        Assert.DoesNotContain("mega.nz", request.PathAndQuery);
        Assert.Contains("link=https%3A%2F%2Fmega.nz%2Ffile%2Fok", request.Body);
        Assert.DoesNotContain(FakeAllDebrid.GoodKey, request.Body);
    }

    [Fact]
    public async Task Unlock_without_name_and_size_still_works()
    {
        var link = await Provider().UnlockAsync(FakeAllDebrid.GoodKey, "https://mega.nz/file/nosize", null, null, default);

        Assert.Null(link.FileName);
        Assert.Equal(-1, link.Size);
    }

    [Fact]
    public async Task Delayed_link_is_polled_until_ready_and_progress_is_reported()
    {
        var waits = new System.Collections.Generic.List<int>();
        var progress = new SyncProgress<DebridWait>(w => waits.Add(w.SecondsLeft));

        var link = await Provider().UnlockAsync(FakeAllDebrid.GoodKey, "https://mega.nz/file/delayed", null, progress, default);

        Assert.Equal(_fake.DirectUrl, link.Url);
        Assert.Equal("Real Name.bin", link.FileName);
        Assert.Equal(999, link.Size); // Größe als Text kommt auch an
        Assert.Equal(3, _fake.DelayedPolls);
        Assert.Equal(new[] { 29, 28 }, waits);
    }

    [Fact]
    public async Task Delayed_link_that_fails_is_a_generation_error()
    {
        _fake.DelayedFails = true;

        var ex = await Fails(Provider().UnlockAsync(FakeAllDebrid.GoodKey, "https://mega.nz/file/delayed", null, null, default));

        Assert.Equal(DebridError.GenerationFailed, ex.Error);
    }

    [Fact]
    public async Task Delayed_link_that_never_gets_ready_gives_up()
    {
        _fake.DelayedNeverReady = true;
        var provider = Provider();
        provider.MaxDelay = TimeSpan.FromMilliseconds(600); // genug Luft, damit auch auf einem trägen Rechner mehr als eine Abfrage hineinpasst

        var ex = await Fails(provider.UnlockAsync(FakeAllDebrid.GoodKey, "https://mega.nz/file/delayed", null, null, default));

        Assert.Equal(DebridError.GenerationFailed, ex.Error);
        Assert.True(_fake.DelayedPolls >= 2);
    }

    [Fact]
    public async Task Cancelling_while_waiting_for_a_delayed_link_stops_at_once()
    {
        _fake.DelayedNeverReady = true;
        var provider = Provider();
        provider.PollInterval = TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<DebridWait>(_ => cts.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.UnlockAsync(FakeAllDebrid.GoodKey, "https://mega.nz/file/delayed", null, progress, cts.Token));
        Assert.Equal(1, _fake.DelayedPolls);
    }

    [Theory]
    [InlineData("down", DebridError.LinkDown)]
    [InlineData("unsupported", DebridError.HostNotSupported)]
    [InlineData("pass", DebridError.PasswordRequired)]
    [InlineData("premium", DebridError.NotPremium)]
    [InlineData("full", DebridError.HostUnavailable)]
    [InlineData("blocked", DebridError.Blocked)]
    [InlineData("weird", DebridError.Other)]
    [InlineData("garbage", DebridError.BadResponse)]
    [InlineData("outage", DebridError.ServiceUnavailable)]
    [InlineData("badlink", DebridError.BadResponse)]
    [InlineData("nolink", DebridError.BadResponse)]
    public async Task Service_errors_are_mapped(string marker, DebridError expected)
    {
        var ex = await Fails(Provider().UnlockAsync(FakeAllDebrid.GoodKey, "https://mega.nz/file/" + marker, null, null, default));

        Assert.Equal(expected, ex.Error);
        Assert.DoesNotContain(FakeAllDebrid.GoodKey, ex.ToString());
        Assert.DoesNotContain("mega.nz", ex.ToString()); // auch der Link steht nicht in der Meldung
    }

    [Fact]
    public async Task Unknown_error_code_is_kept_for_the_log_line()
    {
        var ex = await Fails(Provider().UnlockAsync(FakeAllDebrid.GoodKey, "https://mega.nz/file/weird", null, null, default));

        Assert.Equal("SOMETHING_NEW", ex.Code);
    }

    [Fact]
    public async Task Too_many_requests_is_retried_a_few_times()
    {
        _fake.RateLimitFirst = 2;

        var link = await Provider().UnlockAsync(FakeAllDebrid.GoodKey, "https://mega.nz/file/ok", null, null, default);

        Assert.Equal(_fake.DirectUrl, link.Url);
        Assert.Equal(3, _fake.RequestCount);
    }

    [Fact]
    public async Task Too_many_requests_for_good_is_rate_limited()
    {
        _fake.RateLimitFirst = 100;

        var ex = await Fails(Provider().UnlockAsync(FakeAllDebrid.GoodKey, "https://mega.nz/file/ok", null, null, default));

        Assert.Equal(DebridError.RateLimited, ex.Error);
        Assert.Equal(3, _fake.RequestCount);
    }

    [Fact]
    public async Task Silent_service_ends_with_a_timeout_not_a_hang()
    {
        _fake.Hang = true;
        var provider = Provider();
        provider.RequestTimeout = TimeSpan.FromMilliseconds(200);

        await Assert.ThrowsAsync<TimeoutException>(
            () => provider.UnlockAsync(FakeAllDebrid.GoodKey, "https://mega.nz/file/ok", null, null, default));
    }

    [Fact]
    public async Task Unreachable_service_is_a_network_error()
    {
        _fake.Dispose();

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Provider().UnlockAsync(FakeAllDebrid.GoodKey, "https://mega.nz/file/ok", null, null, default));
    }

    // ---------------------------------------------------------------- Dienst: wann wird entsperrt

    private DebridService Service(bool enabled = true, string? key = FakeAllDebrid.GoodKey)
    {
        var settings = new DebridSettings { Enabled = enabled, ApiKey = key };
        return new DebridService(Provider(), () => settings);
    }

    private int Unlocks => _fake.Log.Count(r => r.PathAndQuery.EndsWith("link/unlock", StringComparison.Ordinal));

    [Fact]
    public async Task Service_off_or_without_key_never_calls_the_service()
    {
        Assert.Null(await Service(enabled: false).TryUnlockAsync("https://mega.nz/file/ok", null, default));
        Assert.False(Service(enabled: true, key: null).IsActive);
        Assert.Equal(0, _fake.RequestCount);
    }

    [Fact]
    public async Task Hint_counts_hoster_links_only_while_off_and_with_a_known_list()
    {
        string[] links = { "https://mega.nz/file/a", "https://cdn.mega.nz/b", "https://example.org/c", "ftp://mega.nz/d", "kein link" };
        var off = Service(enabled: false);

        Assert.Equal(0, off.CountSupportedWhileOff(links)); // Liste noch nicht geladen
        await off.LoadHostsAsync(false, default);
        Assert.Equal(2, off.CountSupportedWhileOff(links));

        var on = Service(enabled: true);
        await on.LoadHostsAsync(false, default);
        Assert.Equal(0, on.CountSupportedWhileOff(links));
    }

    [Fact]
    public async Task Enabled_without_key_is_an_error_not_a_silent_direct_download()
    {
        var ex = await Fails(Service(enabled: true, key: null).TryUnlockAsync("https://mega.nz/file/ok", null, default));

        Assert.Equal(DebridError.NoKey, ex.Error);
    }

    [Fact]
    public async Task Unreadable_key_is_not_active_and_only_hoster_links_report_it()
    {
        // Chiffre, die sich hier nicht entschlüsseln lässt (wie ein Key von einem anderen Konto oder Rechner).
        var settings = new DebridSettings { Enabled = true, ProtectedKey = Convert.ToBase64String(new byte[64]) };
        var service = new DebridService(Provider(), () => settings);
        await service.LoadHostsAsync(false, default);

        Assert.True(settings.HasKey);
        Assert.False(settings.KeyUsable);
        Assert.False(service.IsActive);
        Assert.False(service.WillUnlock("https://mega.nz/file/x.rar")); // der Dialog probt normal
        Assert.Null(await service.TryUnlockAsync("https://example.org/file.zip", null, default)); // normale Links laden
        Assert.Equal(DebridError.KeyUnreadable,
            (await Fails(service.TryUnlockAsync("https://mega.nz/file/ok", null, default))).Error);
        Assert.Equal(DebridError.KeyUnreadable,
            (await Fails(service.TryUnlockAsync("https://mega.nz/file/ok", null, default, required: true))).Error);
        Assert.Equal(0, Unlocks);

        settings.ApiKey = FakeAllDebrid.GoodKey; // neu eingetragen
        Assert.True(settings.KeyUsable);
        Assert.True(service.IsActive);
    }

    [Theory]
    [InlineData("https://mega.nz/file/ok")]
    [InlineData("https://www.mega.nz/file/ok")]
    [InlineData("https://eu.cdn.rapidgator.net/file/ok")]
    public async Task Known_hoster_and_its_subdomains_are_unlocked(string url)
    {
        var link = await Service().TryUnlockAsync(url, null, default);

        Assert.NotNull(link);
        Assert.Equal(1, Unlocks);
    }

    [Theory]
    [InlineData("https://example.org/file.zip")]
    [InlineData("https://notmega.nz/file")]
    [InlineData("http://localhost:8080/a")]
    [InlineData("ftp://mega.nz/a")]
    [InlineData("magnet:?xt=urn:btih:abc")]
    public async Task Other_links_download_directly_without_an_unlock_call(string url)
    {
        Assert.Null(await Service().TryUnlockAsync(url, null, default));
        Assert.Equal(0, Unlocks);
    }

    [Fact]
    public async Task Host_list_is_loaded_once()
    {
        var service = Service();
        await service.TryUnlockAsync("https://mega.nz/file/ok", null, default);
        await service.TryUnlockAsync("https://mega.nz/file/ok", null, default);

        Assert.Equal(1, _fake.Log.Count(r => r.PathAndQuery.EndsWith("hosts/domains", StringComparison.Ordinal)));
        Assert.Equal(new[] { "mega.nz", "rapidgator.net", "uploaded.net" }, service.KnownHosts);
    }

    private int HostLoads => _fake.Log.Count(r => r.PathAndQuery.EndsWith("hosts/domains", StringComparison.Ordinal));

    [Fact]
    public async Task Without_a_host_list_no_link_goes_to_the_service_and_each_start_tries_the_list_again()
    {
        _fake.HostsOverride = (503, "<html>down</html>");
        var service = Service();

        Assert.Null(await service.TryUnlockAsync("https://example.org/file.zip", null, default)); // lädt direkt
        Assert.Null(await service.TryUnlockAsync("https://mega.nz/file/ok", null, default));
        var ex = await Fails(service.TryUnlockAsync("https://mega.nz/file/ok", null, default, required: true));
        Assert.Equal(DebridError.HostsUnavailable, ex.Error);
        Assert.Equal(0, Unlocks);
        Assert.All(_fake.Log, r => Assert.DoesNotContain("mega.nz", r.Body));
        Assert.Equal(3, HostLoads); // jeder Start fragt die Liste erneut

        _fake.HostsOverride = null; // der Dienst ist wieder da
        Assert.NotNull(await service.TryUnlockAsync("https://mega.nz/file/ok", null, default, required: true));
        Assert.Equal(1, Unlocks);
    }

    [Fact]
    public async Task Debrid_download_whose_host_is_missing_reloads_the_list_once_before_it_fails()
    {
        var service = Service();
        await service.LoadHostsAsync(false, default);
        _fake.Hosts = new[] { "mega.nz", "newhost.example" }; // die Liste beim Dienst hat sich geändert

        Assert.NotNull(await service.TryUnlockAsync("https://newhost.example/f/1", null, default, required: true));
        Assert.Equal(2, HostLoads);

        Assert.Equal(DebridError.NoLongerSupported,
            (await Fails(service.TryUnlockAsync("https://gone.example/f/1", null, default, required: true))).Error);
        Assert.Equal(3, HostLoads);
        Assert.Equal(1, Unlocks);
    }

    [Fact]
    public async Task Parallel_host_loads_share_one_request_and_a_cancelled_wait_does_not_stop_it()
    {
        _fake.HostsDelay = TimeSpan.FromMilliseconds(800);
        var service = Service();
        using var cts = new CancellationTokenSource();

        var cancelled = service.LoadHostsAsync(true, cts.Token);
        var first = service.LoadHostsAsync(true, default);
        var second = service.LoadHostsAsync(false, default);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        await Task.WhenAll(first, second);
        Assert.Equal(1, HostLoads);
        Assert.True(service.HostsLoaded);
    }

    [Fact]
    public async Task Bad_key_during_unlock_is_an_error_for_the_download()
    {
        var service = Service(key: "other-fake-key");

        var ex = await Fails(service.TryUnlockAsync("https://mega.nz/file/ok", null, default));

        Assert.Equal(DebridError.BadKey, ex.Error);
    }

    // ---------------------------------------------------------------- Key-Schutz

    [Fact]
    public void Key_is_stored_encrypted_and_can_be_read_back()
    {
        var settings = new DebridSettings { ApiKey = "  " + FakeAllDebrid.GoodKey + "\n" };

        Assert.True(settings.HasKey);
        Assert.Equal(FakeAllDebrid.GoodKey, settings.ApiKey); // Leerraum am Rand weg
        Assert.DoesNotContain(FakeAllDebrid.GoodKey, settings.ProtectedKey);

        settings.ApiKey = "";
        Assert.False(settings.HasKey);
        Assert.Null(settings.ApiKey);
    }

    [Fact]
    public void Settings_file_does_not_contain_the_key_in_clear_text_and_round_trips()
    {
        var settings = new AppSettings();
        settings.Debrid.Enabled = true;
        settings.Debrid.ApiKey = FakeAllDebrid.GoodKey;

        string json = System.Text.Encoding.UTF8.GetString(settings.ToJson());
        Assert.DoesNotContain(FakeAllDebrid.GoodKey, json);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);

        using var dir = new TempDir();
        string path = dir.File("settings.json");
        AppSettings.Write(path, settings.ToJson());
        var loaded = AppSettings.Load(path);
        Assert.True(loaded.Debrid.Enabled);
        Assert.Equal(FakeAllDebrid.GoodKey, loaded.Debrid.ApiKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 !!")]
    [InlineData("AAAA")] // gültiges Base64, aber keine DPAPI-Daten
    public void Unreadable_protected_key_gives_null_instead_of_throwing(string? text)
    {
        Assert.Null(SecretProtector.Unprotect(text));
    }

    [Fact]
    public void Old_settings_file_without_debrid_section_loads_with_defaults()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        File.WriteAllText(path, "{\"maxConcurrent\":2}");

        var loaded = AppSettings.Load(path);

        Assert.False(loaded.Debrid.Enabled);
        Assert.False(loaded.Debrid.HasKey);
        File.WriteAllText(path, "{\"debrid\":null}");
        Assert.NotNull(AppSettings.Load(path).Debrid);
    }

    // ---------------------------------------------------------------- Anzeige

    [Fact]
    public void Every_debrid_error_has_a_text()
    {
        foreach (var error in Enum.GetValues<DebridError>())
        {
            string text = Downloads.ErrorText.Describe(new DebridException(error, "SOME_CODE"));
            Assert.False(text.StartsWith('['), $"Text fehlt für {error}");
            Assert.False(string.IsNullOrWhiteSpace(text));
        }
        Assert.Contains("SOME_CODE", Downloads.ErrorText.Describe(new DebridException(DebridError.Other, "SOME_CODE")));
    }

    [Theory]
    [InlineData("abc123.html", "https://rapidgator.net/file/abc123.html", true)]
    [InlineData("file", "https://mega.nz/file/ok", true)]
    [InlineData("download.php", "https://x.invalid/download.php?id=1", true)]
    [InlineData("archive.part1.rar", "https://uploaded.net/file/archive.part1.rar", true)]
    [InlineData("archive.part1 (2).rar", "https://uploaded.net/file/archive.part1.rar", true)] // Nummer vom Namenskonflikt
    [InlineData("My Movie.mkv", "https://uploaded.net/file/archive.part1.rar", false)]         // vom Nutzer gewählt
    [InlineData("Pack.part1.rar", "https://rapidgator.net/file/abc/Pack.part1.rar.html", true)] // Name aus dem Hinzufügen-Dialog
    [InlineData("Pack.part1 (2).rar", "https://rapidgator.net/file/abc/Pack.part1.rar.html", true)]
    [InlineData("Other.rar", "https://rapidgator.net/file/abc/Pack.part1.rar.html", false)]
    public void Name_from_the_service_only_replaces_a_guessed_name(string name, string url, bool guessed)
    {
        Assert.Equal(guessed, Downloads.DownloadManager.NameIsGuessed(name, url));
    }

    [Theory]
    [InlineData("Pack.part1.rar.html", "Pack.part1.rar")]
    [InlineData("Pack.rar.HTM", "Pack.rar")]
    [InlineData("abc123.html", "abc123.html")] // davor keine Endung: unverändert
    [InlineData("Pack.rar", "Pack.rar")]
    public void Page_suffix_is_stripped_only_behind_a_real_extension(string name, string expected)
    {
        Assert.Equal(expected, Engine.FileNames.StripPageSuffix(name));
    }

    [Fact]
    public async Task Background_host_load_runs_only_for_an_active_service_and_only_while_the_list_is_missing()
    {
        var off = Service(enabled: false);
        var noKey = Service(enabled: true, key: null);
        await off.EnsureHosts();
        await noKey.EnsureHosts();
        Assert.Equal(0, _fake.RequestCount);

        _fake.HostsDelay = TimeSpan.FromMilliseconds(200);
        var on = Service();
        await Task.WhenAll(on.EnsureHosts(), on.EnsureHosts()); // gleichzeitig: ein Abruf
        await on.EnsureHosts();                                 // geladen: keiner mehr
        Assert.True(on.HostsLoaded);
        Assert.True(on.WillUnlock("https://mega.nz/file/x.rar"));
        Assert.Equal(1, _fake.RequestCount);
    }

    [Fact]
    public async Task Failed_background_host_load_is_silent_and_the_next_dialog_tries_again()
    {
        _fake.HostsOverride = (200, "{\"status\":\"success\",\"data\":{\"hosts\":\"nope\"}}");
        var on = Service();

        await on.EnsureHosts(); // scheitert still
        Assert.False(on.HostsLoaded);
        Assert.False(on.WillUnlock("https://mega.nz/file/x.rar"));

        _fake.HostsOverride = null;
        await on.EnsureHosts();
        Assert.True(on.HostsLoaded);
        Assert.Equal(2, _fake.RequestCount);
    }

    [Fact]
    public void Candidate_switches_to_debrid_after_a_probe_and_late_probe_results_do_not_count()
    {
        var failed = new Sheets.AddCandidate("https://rapidgator.net/file/abc/Pack.rar.html");
        failed.Fail("404");
        Assert.False(failed.IsChecked);
        failed.UseDebrid("AllDebrid");
        Assert.Equal(Sheets.CandidateState.Ready, failed.State);
        Assert.True(failed.IsChecked); // das Häkchen hatte nur die gescheiterte Probe genommen
        Assert.Equal("Pack.rar", failed.Name);

        var late = new Sheets.AddCandidate("https://rapidgator.net/file/abc/Pack.rar.html");
        late.UseDebrid("AllDebrid");
        late.Apply(new RemoteFileInfo(new Uri("https://rapidgator.net/file/abc/Pack.rar.html"), 2_000, false,
            "Pack.rar.html", "text/html", null, null));
        late.Fail("timeout");
        Assert.Equal("Pack.rar", late.Name);
        Assert.Equal(-1, late.Size);
        Assert.Equal(Sheets.CandidateState.Ready, late.State);
    }

    [Fact]
    public async Task Hoster_links_are_unlocked_only_when_active_and_the_list_is_known()
    {
        const string link = "https://cdn.mega.nz/file/Pack.part1.rar.html";
        var on = Service(enabled: true);
        Assert.False(on.WillUnlock(link)); // Liste nicht geladen: nie nachfragen
        await on.LoadHostsAsync(false, default);
        Assert.True(on.WillUnlock(link));
        Assert.False(on.WillUnlock("https://example.org/x.rar"));
        Assert.False(on.WillUnlock("ftp://mega.nz/x.rar"));

        var off = Service(enabled: false);
        await off.LoadHostsAsync(false, default);
        Assert.False(off.WillUnlock(link));
        var noKey = Service(enabled: true, key: null);
        await noKey.LoadHostsAsync(false, default);
        Assert.False(noKey.WillUnlock(link));
    }

    [Fact]
    public void Debrid_candidate_has_a_clean_name_unknown_size_and_no_resume_warning()
    {
        var c = new Sheets.AddCandidate("https://rapidgator.net/file/abc/Pack.part1.rar.html");
        c.UseDebrid("AllDebrid");

        Assert.Equal("Pack.part1.rar", c.Name);
        Assert.Equal("RAR", c.Badge);
        Assert.Equal(-1, c.Size);
        Assert.Equal(Sheets.CandidateState.Ready, c.State);
        Assert.Equal(Sheets.Tone.Normal, c.Tone);
        Assert.Contains("AllDebrid", c.StatusText);
    }

    // ---------------------------------------------------------------- Engine: Fortsetzen mit neuem Link

    private static DownloadOptions Fast() => new()
    {
        Segments = 3,
        MinSegmentSize = 64 * 1024,
        RetryDelay = TimeSpan.FromMilliseconds(20),
        ReadTimeout = TimeSpan.FromSeconds(5),
    };

    private static async Task<long> CancelAfterSomeBytes(HttpDownloader dl, DownloadRequest request)
    {
        using var cts = new CancellationTokenSource();
        long seen = 0;
        var progress = new SyncProgress<DownloadProgress>(p =>
        {
            seen = p.DownloadedBytes;
            if (p.DownloadedBytes > 600_000) cts.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dl.DownloadAsync(request, progress, cts.Token));
        return seen;
    }

    private static long Requested(TestServer server) => server.Ranges
        .Select(r => r.Substring(6).Split('-'))
        .Where(p => p[1].Length > 0)
        .Sum(p => long.Parse(p[1]) - long.Parse(p[0]) + 1);

    [Fact]
    public async Task Resume_with_a_new_unlocked_link_keeps_the_bytes_when_the_resume_key_is_the_same()
    {
        using var server = new TestServer(3_000_000) { ThrottleBytesPerSecond = 400_000 };
        using var dir = new TempDir();
        string path = dir.File("d.bin");
        using var dl = new HttpDownloader(Fast());
        const string hoster = "https://mega.nz/file/ok";

        await CancelAfterSomeBytes(dl, new DownloadRequest(server.Url + "?token=1", path, ResumeKey: hoster));

        // Neues Entsperren: andere Adresse, andere ETag (gleiche Datei).
        server.Replace(server.Data, "\"other-generation\"");
        server.Ranges.Clear();
        server.ThrottleBytesPerSecond = 0;
        await dl.DownloadAsync(new DownloadRequest(server.Url + "?token=2", path, ResumeKey: hoster), null, default);

        Assert.Equal(server.Data, await File.ReadAllBytesAsync(path));
        Assert.True(Requested(server) < server.Data.Length, "Der neue Link soll den alten Stand fortsetzen.");
    }

    [Fact]
    public async Task Without_a_resume_key_a_new_address_still_starts_from_scratch()
    {
        using var server = new TestServer(3_000_000) { ThrottleBytesPerSecond = 400_000 };
        using var dir = new TempDir();
        string path = dir.File("e.bin");
        using var dl = new HttpDownloader(Fast());

        await CancelAfterSomeBytes(dl, new DownloadRequest(server.Url + "?token=1", path));
        server.Ranges.Clear();
        server.ThrottleBytesPerSecond = 0;
        await dl.DownloadAsync(new DownloadRequest(server.Url + "?token=2", path), null, default);

        Assert.Equal(server.Data, await File.ReadAllBytesAsync(path));
        Assert.True(Requested(server) >= server.Data.Length);
    }

    [Fact]
    public async Task A_different_resume_key_does_not_reuse_foreign_bytes()
    {
        using var server = new TestServer(3_000_000) { ThrottleBytesPerSecond = 400_000 };
        using var dir = new TempDir();
        string path = dir.File("f.bin");
        using var dl = new HttpDownloader(Fast());

        await CancelAfterSomeBytes(dl, new DownloadRequest(server.Url, path, ResumeKey: "https://mega.nz/file/one"));
        server.Ranges.Clear();
        server.ThrottleBytesPerSecond = 0;
        await dl.DownloadAsync(new DownloadRequest(server.Url, path, ResumeKey: "https://mega.nz/file/two"), null, default);

        Assert.Equal(server.Data, await File.ReadAllBytesAsync(path));
        Assert.True(Requested(server) >= server.Data.Length);
    }

    // ---------------------------------------------------------------- Engine: geschützte Teildaten (über Debrid angelegt)

    private const string Hoster = "https://mega.nz/file/ok";

    private static DownloadRequest Protected(string url, string path, bool flag = true) =>
        new(url, path, ResumeKey: Hoster, ProtectPartial: flag, RejectHtml: flag);

    private sealed record PartSnapshot(byte[] Part, string Meta);

    private static PartSnapshot Snapshot(string path) =>
        new(File.ReadAllBytes(path + ".part"), File.ReadAllText(path + ".part.json"));

    private static void AssertUnchanged(PartSnapshot before, string path)
    {
        Assert.Equal(before.Part, File.ReadAllBytes(path + ".part"));
        Assert.Equal(before.Meta, File.ReadAllText(path + ".part.json"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Protected_partial_data_is_kept_when_the_new_link_has_another_size()
    {
        using var server = new TestServer(3_000_000) { ThrottleBytesPerSecond = 400_000 };
        using var dir = new TempDir();
        string path = dir.File("p.bin");
        using var dl = new HttpDownloader(Fast());
        await CancelAfterSomeBytes(dl, Protected(server.Url + "?token=1", path));
        var before = Snapshot(path);
        Assert.Contains("\"Protected\":true", before.Meta);

        server.Replace(new byte[1_000_000], "\"small\""); // etwa die Webseite des Hosters oder eine andere Datei
        var ex = await Assert.ThrowsAsync<PartialKeptException>(
            () => dl.DownloadAsync(Protected(server.Url + "?token=2", path), null, default));

        Assert.Equal(ResumeConflict.SizeChanged, ex.Conflict);
        Assert.Equal(3_000_000, ex.LocalSize);
        Assert.Equal(1_000_000, ex.RemoteSize);
        AssertUnchanged(before, path);
    }

    [Fact]
    public async Task Protection_is_remembered_in_the_meta_file_even_if_the_caller_forgets_it()
    {
        using var server = new TestServer(3_000_000) { ThrottleBytesPerSecond = 400_000 };
        using var dir = new TempDir();
        string path = dir.File("q.bin");
        using var dl = new HttpDownloader(Fast());
        await CancelAfterSomeBytes(dl, Protected(server.Url, path));
        var before = Snapshot(path);

        // Dieselbe Quelle ohne Flag (etwa eine App, die das Flag verloren hat): kurze Antwort statt der Datei.
        server.Replace(new byte[5_000], "\"page\"");
        await Assert.ThrowsAsync<PartialKeptException>(
            () => dl.DownloadAsync(new DownloadRequest(server.Url, path, ResumeKey: Hoster), null, default));

        AssertUnchanged(before, path);
    }

    [Fact]
    public async Task Protected_partial_data_is_kept_when_the_file_changes_during_the_download()
    {
        using var server = new TestServer(3_000_000) { ThrottleBytesPerSecond = 400_000 };
        using var dir = new TempDir();
        string path = dir.File("r.bin");
        using var dl = new HttpDownloader(Fast());
        var other = new byte[3_000_000];
        new Random(7).NextBytes(other);
        int replaced = 0;
        var progress = new SyncProgress<DownloadProgress>(p =>
        {
            if (p.DownloadedBytes > 300_000 && Interlocked.Exchange(ref replaced, 1) == 0) server.Replace(other, "\"gen2\"");
        });

        var ex = await Assert.ThrowsAsync<PartialKeptException>(
            () => dl.DownloadAsync(Protected(server.Url, path), progress, default));

        Assert.Equal(ResumeConflict.FileChanged, ex.Conflict);
        Assert.True(new FileInfo(path + ".part").Length > 0);
        Assert.True(File.Exists(path + ".part.json"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Protected_partial_data_is_kept_when_the_new_link_cannot_resume()
    {
        using var server = new TestServer(3_000_000) { ThrottleBytesPerSecond = 400_000 };
        using var dir = new TempDir();
        string path = dir.File("s.bin");
        using var dl = new HttpDownloader(Fast());
        await CancelAfterSomeBytes(dl, Protected(server.Url + "?token=1", path));
        var before = Snapshot(path);

        server.SupportsRanges = false;
        var ex = await Assert.ThrowsAsync<PartialKeptException>(
            () => dl.DownloadAsync(Protected(server.Url + "?token=2", path), null, default));

        Assert.Equal(ResumeConflict.NoRanges, ex.Conflict);
        AssertUnchanged(before, path);
    }

    [Fact]
    public async Task Protected_download_with_another_source_may_start_over()
    {
        using var server = new TestServer(3_000_000) { ThrottleBytesPerSecond = 400_000 };
        using var dir = new TempDir();
        string path = dir.File("t.bin");
        using var dl = new HttpDownloader(Fast());
        await CancelAfterSomeBytes(dl, Protected(server.Url, path));

        server.ThrottleBytesPerSecond = 0;
        await dl.DownloadAsync(new DownloadRequest(server.Url, path, ResumeKey: "https://mega.nz/file/edited",
            ProtectPartial: true), null, default); // der Nutzer hat den Link geändert

        Assert.Equal(server.Data, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("text/html; charset=utf-8")]
    [InlineData("application/xhtml+xml")]
    public async Task Html_answer_is_never_a_finished_file_when_rejected(string type)
    {
        using var server = new TestServer(50_000) { ContentType = type };
        using var dir = new TempDir();
        string path = dir.File("u.rar");
        using var dl = new HttpDownloader(Fast());

        await Assert.ThrowsAsync<HtmlResponseException>(
            () => dl.DownloadAsync(Protected(server.Url, path), null, default));
        Assert.False(File.Exists(path));

        server.SupportsRanges = false; // Einzelstrom: dieselbe Regel
        await Assert.ThrowsAsync<HtmlResponseException>(
            () => dl.DownloadAsync(Protected(server.Url, path), null, default));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Html_answer_without_the_flag_still_downloads_as_before()
    {
        using var server = new TestServer(50_000) { ContentType = "text/html" };
        using var dir = new TempDir();
        string path = dir.File("page.html");
        using var dl = new HttpDownloader(Fast());

        await dl.DownloadAsync(new DownloadRequest(server.Url, path), null, default);

        Assert.Equal(server.Data, await File.ReadAllBytesAsync(path));
    }

    // ---------------------------------------------------------------- Über Debrid angelegt: nie direkt laden

    [Fact]
    public async Task Required_unlock_never_falls_back_to_a_direct_download()
    {
        Assert.Equal(DebridError.Disabled,
            (await Fails(Service(enabled: false).TryUnlockAsync(Hoster, null, default, required: true))).Error);

        var on = Service();
        await on.LoadHostsAsync(false, default);
        Assert.Equal(DebridError.NoLongerSupported,
            (await Fails(on.TryUnlockAsync("https://example.org/x.rar", null, default, required: true))).Error);
        var refused = await Fails(on.TryUnlockAsync("https://mega.nz/file/unsupported", null, default, required: true));
        Assert.Equal(DebridError.NoLongerSupported, refused.Error);
        Assert.Equal("LINK_HOST_NOT_SUPPORTED", refused.Code);

        Assert.NotNull(await on.TryUnlockAsync(Hoster, null, default, required: true));
    }

    [Fact]
    public void Debrid_flag_of_a_download_is_saved_and_loaded()
    {
        var item = new Downloads.DownloadItem(Hoster, "x.rar", @"C:\d") { ViaDebrid = true };

        Assert.True(item.ToEntry().Debrid);
        Assert.True(Downloads.DownloadItem.FromEntry(item.ToEntry()).ViaDebrid);
        Assert.False(Downloads.DownloadItem.FromEntry(new Downloads.DownloadEntry { Url = Hoster }).ViaDebrid);
    }

    [Fact]
    public void Kept_partial_data_and_html_answers_have_texts()
    {
        foreach (var ex in new Exception[]
        {
            new PartialKeptException(ResumeConflict.SizeChanged, 3_000_000, 1_000),
            new PartialKeptException(ResumeConflict.FileChanged),
            new PartialKeptException(ResumeConflict.NoRanges),
            new HtmlResponseException(),
        })
        {
            string text = Downloads.ErrorText.Describe(ex);
            Assert.False(text.StartsWith('['), $"Text fehlt für {ex.GetType().Name}");
        }
    }
}
