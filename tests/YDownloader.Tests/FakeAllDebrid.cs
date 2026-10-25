using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YDownloader.Tests;

/// <summary>
/// Attrappe des AllDebrid-Dienstes auf 127.0.0.1. Antwortet nach dem Inhalt des Links (<c>ok</c>, <c>delayed</c>, <c>down</c> ...)
/// und merkt sich jede Anfrage. Der Key ist ein ausgedachter Testwert, nie ein echter.
/// </summary>
public sealed class FakeAllDebrid : IDisposable
{
    public const string GoodKey = "test-key-not-real-123";

    private readonly HttpListener _listener;
    private int _requests, _delayedPolls, _rateLimited;

    public Uri BaseUri { get; }

    /// <summary>Anfragen: Methode, Pfad samt Query, Authorization-Header, Körper.</summary>
    public ConcurrentQueue<(string Method, string PathAndQuery, string? Auth, string Body)> Log { get; } = new();

    public int RequestCount => _requests;
    public int DelayedPolls => _delayedPolls;

    /// <summary>So oft liefert <c>link/delayed</c> „in Arbeit“, bevor der Link fertig ist.</summary>
    public int DelayedProcessingPolls { get; set; } = 2;

    /// <summary>Der Link wird nie fertig, sondern meldet Status 3.</summary>
    public bool DelayedFails { get; set; }

    /// <summary>Der Link bleibt für immer in Arbeit.</summary>
    public bool DelayedNeverReady { get; set; }

    /// <summary>Die ersten N Anfragen bekommen HTTP 429.</summary>
    public int RateLimitFirst { get; set; }

    /// <summary>Alle Anfragen hängen, ohne zu antworten.</summary>
    public bool Hang { get; set; }

    public string[] Hosts { get; set; } = { "uploaded.net", "WWW.Rapidgator.net", "mega.nz", "mega.nz" };

    /// <summary>Statuscode und Körper für <c>hosts/domains</c> überschreiben (Ausfall nachstellen).</summary>
    public (int Status, string Body)? HostsOverride { get; set; }

    /// <summary>Wert von <c>premiumUntil</c> in der Antwort von <c>user</c>, roh als JSON.</summary>
    public string PremiumUntil { get; set; } = "1893456000";

    /// <summary>So lange braucht <c>hosts/domains</c> für die Antwort.</summary>
    public TimeSpan HostsDelay { get; set; }

    public string DirectUrl { get; set; } = "http://files.example.invalid/dl/abc/file.bin";

    public FakeAllDebrid()
    {
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
        BaseUri = new Uri($"http://127.0.0.1:{port}/v4/");
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            Interlocked.Increment(ref _requests);
            string body = "";
            if (ctx.Request.HasEntityBody)
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }
            string path = ctx.Request.Url!.AbsolutePath;
            Log.Enqueue((ctx.Request.HttpMethod, ctx.Request.Url.PathAndQuery, ctx.Request.Headers["Authorization"], body));

            if (Hang)
            {
                await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                return;
            }
            if (Interlocked.Increment(ref _rateLimited) <= RateLimitFirst)
            {
                Send(ctx, 429, "{}");
                return;
            }

            if (path == "/v4/hosts/domains" && HostsDelay > TimeSpan.Zero)
                await Task.Delay(HostsDelay).ConfigureAwait(false);

            var form = System.Web.HttpUtility.ParseQueryString(body);
            var (status, json) = path switch
            {
                "/v4/hosts/domains" => HostsOverride ?? (200, Success("{\"hosts\":[" + string.Join(",", Hosts.Select(h => $"\"{h}\"")) +
                    "],\"streams\":[],\"redirectors\":[]}")),
                "/v4/user" => Authorized(ctx) ? (200, Success(
                    "{\"user\":{\"username\":\"tester\",\"isPremium\":true,\"premiumUntil\":" + PremiumUntil + "}}")) : Error("AUTH_BAD_APIKEY"),
                "/v4/link/unlock" => Authorized(ctx) ? Unlock(form["link"] ?? "") : Error("AUTH_BAD_APIKEY"),
                "/v4/link/infos" => Authorized(ctx) ? Infos(body) : Error("AUTH_BAD_APIKEY"),
                "/v4/link/delayed" => Authorized(ctx) ? Delayed(form["id"]) : Error("AUTH_BAD_APIKEY"),
                _ => (404, "{}"),
            };
            Send(ctx, status, json);
        }
        catch
        {
            // Verbindung vom Test geschlossen
        }
    }

    private static bool Authorized(HttpListenerContext ctx) => ctx.Request.Headers["Authorization"] == "Bearer " + GoodKey;

    private (int, string) Unlock(string link)
    {
        string Direct(string extra = "") =>
            Success("{\"link\":\"" + DirectUrl + "\",\"filename\":\"Real Name.bin\",\"filesize\":12345,\"host\":\"mega\",\"id\":\"x1\"" + extra + "}");

        if (link.Contains("delayed")) return (200, Success("{\"link\":\"\",\"filename\":\"Real Name.bin\",\"filesize\":\"999\",\"host\":\"mega\",\"delayed\":7}"));
        if (link.Contains("nosize")) return (200, Success("{\"link\":\"" + DirectUrl + "\"}"));
        if (link.Contains("badlink")) return (200, Success("{\"link\":\"ftp://x.invalid/a\"}"));
        if (link.Contains("nolink")) return (200, Success("{\"filename\":\"a\"}"));
        if (link.Contains("garbage")) return (200, "<html>nope</html>");
        if (link.Contains("outage")) return (503, "<html>down</html>");
        if (link.Contains("down")) return Error("LINK_DOWN");
        if (link.Contains("unsupported")) return Error("LINK_HOST_NOT_SUPPORTED");
        if (link.Contains("pass")) return Error("LINK_PASS_PROTECTED");
        if (link.Contains("premium")) return Error("MUST_BE_PREMIUM");
        if (link.Contains("full")) return Error("LINK_HOST_FULL");
        if (link.Contains("blocked")) return Error("AUTH_BLOCKED");
        if (link.Contains("weird")) return Error("SOMETHING_NEW");
        return (200, Direct());
    }

    /// <summary>Links, die jede Anfrage an <c>link/infos</c> in Reihenfolge gebracht hat (je Anfrage eine Liste).</summary>
    public ConcurrentQueue<string[]> InfoRequests { get; } = new();

    /// <summary>Ersetzt die Antwort von <c>link/infos</c> ganz (Statuscode, Körper).</summary>
    public (int Status, string Body)? InfosOverride { get; set; }

    private (int, string) Infos(string body)
    {
        var links = new List<string>();
        foreach (string pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0 && Uri.UnescapeDataString(pair[..eq].Replace('+', ' ')) == "link[]")
                links.Add(Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')));
        }
        InfoRequests.Enqueue(links.ToArray());
        if (InfosOverride is { } o) return o;

        var items = new List<string>();
        foreach (string link in links)
        {
            string l = "\"link\":" + System.Text.Json.JsonSerializer.Serialize(link);
            if (link.Contains("missing")) continue;
            if (link.Contains("down")) items.Add("{" + l + ",\"error\":{\"code\":\"LINK_DOWN\",\"message\":\"x\"}}");
            else if (link.Contains("pass")) items.Add("{" + l + ",\"error\":{\"code\":\"LINK_PASS_PROTECTED\",\"message\":\"x\"}}");
            else if (link.Contains("unsupported")) items.Add("{" + l + ",\"error\":{\"code\":\"LINK_HOST_NOT_SUPPORTED\",\"message\":\"x\"}}");
            else if (link.Contains("weird")) items.Add("{" + l + ",\"error\":{\"code\":\"SOMETHING_NEW\",\"message\":\"x\"}}");
            else
            {
                // Name wie ihn ein Hoster nennt: das letzte Stück der Adresse, wenn es eine Endung hat
                string last = link.TrimEnd('/')[(link.TrimEnd('/').LastIndexOf('/') + 1)..];
                string name = System.Text.Json.JsonSerializer.Serialize(last.Contains('.') ? last : "Checked Name.bin");
                string size = link.Contains("nosize") ? "" : ",\"size\":" + (4242 + links.IndexOf(link));
                items.Add("{" + l + ",\"filename\":" + name + size + ",\"host\":\"mega\",\"hostDomain\":\"mega.nz\"}");
            }
        }
        return (200, Success("{\"infos\":[" + string.Join(",", items) + "]}"));
    }

    private (int, string) Delayed(string? id)
    {
        if (id != "7") return Error("DELAYED_INVALID_ID");
        int n = Interlocked.Increment(ref _delayedPolls);
        if (DelayedNeverReady || n <= DelayedProcessingPolls)
            return (200, Success("{\"status\":1,\"time_left\":" + (30 - n) + "}"));
        if (DelayedFails) return (200, Success("{\"status\":3,\"time_left\":0}"));
        return (200, Success("{\"status\":2,\"time_left\":0,\"link\":\"" + DirectUrl + "\"}"));
    }

    private static string Success(string data) => "{\"status\":\"success\",\"data\":" + data + "}";

    private static (int, string) Error(string code) =>
        (200, "{\"status\":\"error\",\"error\":{\"code\":\"" + code + "\",\"message\":\"text\"}}");

    private static void Send(HttpListenerContext ctx, int status, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        try { _listener.Close(); }
        catch { }
    }
}
