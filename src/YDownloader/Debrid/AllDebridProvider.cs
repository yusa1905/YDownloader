using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace YDownloader.Debrid;

/// <summary>
/// AllDebrid, API v4. Der Key geht nur im <c>Authorization</c>-Header mit, nie in der Adresse. Links und Passwörter gehen
/// im Anfragekörper (POST), nicht in der Adresse. Weder Key noch Link stehen in Ausnahmetexten.
/// </summary>
public sealed class AllDebridProvider : IDebridProvider
{
    public const string DefaultBase = "https://api.alldebrid.com/v4/";

    private readonly HttpClient _http;
    private readonly Uri _base;

    /// <param name="http">Gemeinsamer Client. Sein Zeitlimit ist unbegrenzt, jede Anfrage bekommt hier ihr eigenes.</param>
    /// <param name="baseUri">Nur für Tests (lokale Attrappe). Muss auf <c>/</c> enden.</param>
    public AllDebridProvider(HttpClient http, Uri? baseUri = null)
    {
        _http = http;
        _base = baseUri ?? new Uri(DefaultBase);
    }

    public string Name => "AllDebrid";

    /// <summary>Wie lange eine einzelne Anfrage höchstens dauern darf.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Abstand der Abfragen bei „delayed“-Links. Der Dienst bittet um mindestens 5 s.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Wie lange auf einen „delayed“-Link gewartet wird, bevor aufgegeben wird.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>Pause nach „zu viele Anfragen“ (HTTP 429), höchstens <see cref="RateLimitRetries"/> Mal.</summary>
    public TimeSpan RateLimitPause { get; set; } = TimeSpan.FromSeconds(2);

    public int RateLimitRetries { get; set; } = 2;

    public async Task<DebridAccount> CheckAccountAsync(string apiKey, CancellationToken ct)
    {
        RequireKey(apiKey);
        using var doc = await CallAsync(HttpMethod.Get, "user", apiKey, null, ct).ConfigureAwait(false);
        var user = Data(doc).TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? u : throw Bad();
        string name = Str(user, "username") ?? throw Bad();
        bool premium = user.TryGetProperty("isPremium", out var p) && p.ValueKind == JsonValueKind.True;
        return new DebridAccount(name, premium, premium ? UnixTime(Long(user, "premiumUntil")) : null);
    }

    private static readonly long MaxUnixSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    /// <summary>Sekunden seit 1970 als Datum. Null bei 0, negativ oder außerhalb dessen, was ein Datum fassen kann.</summary>
    private static DateTimeOffset? UnixTime(long seconds) =>
        seconds > 0 && seconds <= MaxUnixSeconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    public async Task<IReadOnlyList<string>> GetHostsAsync(CancellationToken ct)
    {
        using var doc = await CallAsync(HttpMethod.Get, "hosts/domains", null, null, ct).ConfigureAwait(false);
        if (!Data(doc).TryGetProperty("hosts", out var hosts) || hosts.ValueKind != JsonValueKind.Array) throw Bad();
        var list = new List<string>(hosts.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in hosts.EnumerateArray())
        {
            if (h.ValueKind != JsonValueKind.String) continue;
            string domain = h.GetString()!.Trim().ToLowerInvariant();
            if (domain.StartsWith("www.", StringComparison.Ordinal)) domain = domain[4..];
            if (domain.Length > 0 && seen.Add(domain)) list.Add(domain);
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    public async Task<IReadOnlyList<LinkInfo>> CheckLinksAsync(string apiKey, IReadOnlyList<string> links, CancellationToken ct)
    {
        RequireKey(apiKey);
        if (links.Count == 0) return Array.Empty<LinkInfo>();
        foreach (string link in links) CheckLink(link); // nur http(s) gehen hinaus

        var form = links.Select(l => new KeyValuePair<string, string>("link[]", l)).ToList();
        using var doc = await CallAsync(HttpMethod.Post, "link/infos", apiKey, form, ct).ConfigureAwait(false);
        if (!Data(doc).TryGetProperty("infos", out var infos) || infos.ValueKind != JsonValueKind.Array) throw Bad();

        // Die Antwort nennt je Eintrag den abgefragten Link. Danach wird zugeordnet, bei Gleichlauf in der Anzahl sonst nach Platz.
        var byLink = new Dictionary<string, LinkInfo>(StringComparer.Ordinal);
        var ordered = new List<LinkInfo>(infos.GetArrayLength());
        foreach (var item in infos.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { ordered.Add(new LinkInfo("", LinkState.Unknown, null, -1)); continue; }
            string echoed = Str(item, "link") ?? "";
            var info = ReadInfo(echoed, item);
            ordered.Add(info);
            byLink.TryAdd(echoed, info);
        }

        var result = new List<LinkInfo>(links.Count);
        for (int i = 0; i < links.Count; i++)
        {
            if (byLink.TryGetValue(links[i], out var found)) result.Add(found with { Link = links[i] });
            else if (ordered.Count == links.Count) result.Add(ordered[i] with { Link = links[i] });
            else result.Add(new LinkInfo(links[i], LinkState.Unknown, null, -1));
        }
        return result;
    }

    private static LinkInfo ReadInfo(string link, JsonElement item)
    {
        if (item.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var state = FromCode(Str(error, "code")).Error switch
            {
                DebridError.LinkDown => LinkState.Offline,
                DebridError.PasswordRequired => LinkState.PasswordProtected,
                _ => LinkState.Unknown, // Hoster nicht unterstützt, vorübergehend nicht erreichbar, neuer Code
            };
            return new LinkInfo(link, state, null, -1);
        }
        string? name = Str(item, "filename");
        long size = Long(item, "size", -1);
        return new LinkInfo(link, LinkState.Online, string.IsNullOrWhiteSpace(name) ? null : name, size < 0 ? -1 : size);
    }

    public async Task<DebridLink> UnlockAsync(string apiKey, string link, string? password, IProgress<DebridWait>? waiting,
        CancellationToken ct)
    {
        RequireKey(apiKey);
        var form = new List<KeyValuePair<string, string>> { new("link", link) };
        if (!string.IsNullOrEmpty(password)) form.Add(new("password", password));

        using var doc = await CallAsync(HttpMethod.Post, "link/unlock", apiKey, form, ct).ConfigureAwait(false);
        var data = Data(doc);
        string? fileName = Str(data, "filename");
        long size = data.TryGetProperty("filesize", out _) ? Long(data, "filesize", -1) : -1;
        string? host = Str(data, "host");

        string? direct = Str(data, "link");
        string? delayedId = Id(data, "delayed");
        if (delayedId is not null)
            direct = await WaitForDelayedAsync(apiKey, delayedId, waiting, ct).ConfigureAwait(false);
        return new DebridLink(CheckLink(direct), string.IsNullOrWhiteSpace(fileName) ? null : fileName, size, host);
    }

    private async Task<string?> WaitForDelayedAsync(string apiKey, string id, IProgress<DebridWait>? waiting,
        CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        while (true)
        {
            using var doc = await CallAsync(HttpMethod.Post, "link/delayed", apiKey,
                new[] { new KeyValuePair<string, string>("id", id) }, ct).ConfigureAwait(false);
            var data = Data(doc);
            switch (Long(data, "status"))
            {
                case 2:
                    return Str(data, "link");
                case 3:
                    throw new DebridException(DebridError.GenerationFailed, null, "Der Dienst konnte den Link nicht erzeugen.");
                case 1:
                    waiting?.Report(new DebridWait((int)Math.Clamp(Long(data, "time_left", -1), -1, int.MaxValue)));
                    break;
                default:
                    throw Bad();
            }
            if (DateTime.UtcNow - started + PollInterval > MaxDelay)
                throw new DebridException(DebridError.GenerationFailed, null, "Der Dienst braucht zu lange für den Link.");
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- Aufruf und Antwort

    private async Task<JsonDocument> CallAsync(HttpMethod method, string path, string? apiKey,
        IEnumerable<KeyValuePair<string, string>>? form, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, new Uri(_base, path));
            request.Headers.UserAgent.ParseAdd("YDownloader/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (apiKey is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            if (form is not null) request.Content = new FormUrlEncodedContent(form);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            HttpStatusCode status;
            string body;
            try
            {
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                    .ConfigureAwait(false);
                status = response.StatusCode;
                body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Der Debrid-Dienst antwortet nicht.");
            }

            if (status == HttpStatusCode.TooManyRequests)
            {
                if (attempt >= RateLimitRetries) throw new DebridException(DebridError.RateLimited, null, "Zu viele Anfragen.");
                await Task.Delay(RateLimitPause, ct).ConfigureAwait(false);
                continue;
            }
            return Parse(status, body);
        }
    }

    private static JsonDocument Parse(HttpStatusCode status, string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // Kein JSON: bei 5xx ein Ausfall beim Dienst, sonst eine unbrauchbare Antwort.
            throw (int)status >= 500
                ? new DebridException(DebridError.ServiceUnavailable, null, $"Der Dienst antwortet mit HTTP {(int)status}.")
                : Bad();
        }

        var root = doc.RootElement;
        string? state = root.ValueKind == JsonValueKind.Object ? Str(root, "status") : null;
        if (state == "success") return doc;

        using (doc)
        {
            if (state == "error" && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                throw FromCode(Str(error, "code"));
            throw Bad();
        }
    }

    private static DebridException FromCode(string? code)
    {
        var kind = code switch
        {
            "AUTH_MISSING_APIKEY" or "AUTH_BAD_APIKEY" => DebridError.BadKey,
            "AUTH_BLOCKED" or "AUTH_USER_BANNED" => DebridError.Blocked,
            "MUST_BE_PREMIUM" or "FREE_TRIAL_LIMIT_REACHED" => DebridError.NotPremium,
            "LINK_HOST_NOT_SUPPORTED" or "LINK_NOT_SUPPORTED" => DebridError.HostNotSupported,
            "LINK_HOST_UNAVAILABLE" or "LINK_HOST_FULL" or "LINK_HOST_LIMIT_REACHED" or "LINK_TOO_MANY_DOWNLOADS"
                or "LINK_TEMPORARY_UNAVAILABLE" or "NO_SERVER" => DebridError.HostUnavailable,
            "LINK_DOWN" or "LINK_ERROR" => DebridError.LinkDown,
            "LINK_PASS_PROTECTED" => DebridError.PasswordRequired,
            "MAINTENANCE" or "INTERNAL_ERROR" => DebridError.ServiceUnavailable,
            _ => DebridError.Other,
        };
        return new DebridException(kind, code);
    }

    private static void RequireKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new DebridException(DebridError.NoKey);
        if (!DebridSettings.IsValidKey(apiKey)) throw new DebridException(DebridError.KeyFormat); // nie in den Header
    }

    private static DebridException Bad() => new(DebridError.BadResponse, null, "Unerwartete Antwort des Debrid-Dienstes.");

    private static JsonElement Data(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : throw Bad();

    /// <summary>Nur http und https gehen an die Engine.</summary>
    private static string CheckLink(string? link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : throw Bad();

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Zahl, die der Dienst auch als Text schicken kann.</summary>
    private static long Long(JsonElement e, string name, long fallback = 0)
    {
        if (!e.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
            return n;
        return fallback;
    }

    /// <summary>Kennung als Text, null bei fehlend, <c>null</c>, 0, <c>false</c> oder leer.</summary>
    private static string? Id(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out long n) => n == 0 ? null : n.ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => string.IsNullOrEmpty(v.GetString()) || v.GetString() == "0" ? null : v.GetString(),
            _ => null,
        };
    }
}
