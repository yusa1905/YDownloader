using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace YDownloader.Debrid;

/// <summary>Debrid-Einstellungen in <c>settings.json</c>. Der Key steht nur verschlüsselt darin (siehe <see cref="SecretProtector"/>).</summary>
public sealed class DebridSettings
{
    /// <summary>Hoster-Links über den Dienst entsperren. Ohne Key hat der Schalter keine Wirkung.</summary>
    public bool Enabled { get; set; }

    /// <summary>API-Key, mit DPAPI verschlüsselt und als Base64 gespeichert.</summary>
    public string? ProtectedKey { get; set; }

    /// <summary>Klartext-Key (entschlüsselt) oder null, wenn keiner da ist oder er sich auf diesem Konto nicht lesen lässt.</summary>
    [JsonIgnore]
    public string? ApiKey
    {
        get => SecretProtector.Unprotect(ProtectedKey);
        set => ProtectedKey = string.IsNullOrWhiteSpace(value) ? null : SecretProtector.Protect(value.Trim());
    }

    /// <summary>Ein Key ist gespeichert, ob lesbar oder nicht (etwa von einem anderen Windows-Konto).</summary>
    [JsonIgnore]
    public bool HasKey => !string.IsNullOrEmpty(ProtectedKey);

    /// <summary>
    /// Ein gespeicherter Key lässt sich auf diesem Konto entschlüsseln. Das Ergebnis gilt, bis sich <see cref="ProtectedKey"/>
    /// ändert, so kostet die häufige Abfrage (je Link im Hinzufügen-Dialog) nur beim ersten Mal einen DPAPI-Aufruf.
    /// </summary>
    [JsonIgnore]
    public bool KeyUsable
    {
        get
        {
            string? cipher = ProtectedKey;
            if (string.IsNullOrEmpty(cipher)) return false;
            if (!ReferenceEquals(cipher, _checkedCipher))
            {
                _usable = SecretProtector.Unprotect(cipher) is not null;
                _checkedCipher = cipher;
            }
            return _usable;
        }
    }

    private string? _checkedCipher;
    private bool _usable;

    /// <summary>
    /// Ein Key besteht nur aus sichtbaren ASCII-Zeichen ohne Leerraum. Alles andere (Zeilenumbruch, Leerzeichen in der Mitte,
    /// unsichtbare Zeichen aus dem Kopieren) kippt sonst erst beim Senden den Header.
    /// </summary>
    public static bool IsValidKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength) return false;
        foreach (char c in key)
            if (c is < '!' or > '~') return false;
        return true;
    }

    private const int MaxKeyLength = 256;
}

/// <summary>
/// Entscheidet für einen Link, ob er über den Dienst läuft, und entsperrt ihn. Kennt weder Oberfläche noch Engine.
/// Die Liste der Hoster wird beim ersten Bedarf geladen und danach eine Weile gemerkt. Nur Links zu Hostern dieser Liste
/// gehen an den Dienst: Ohne Liste geht kein Link hinaus, normale Links laden dann direkt.
/// </summary>
public sealed class DebridService
{
    private static readonly TimeSpan HostsMaxAge = TimeSpan.FromHours(12);

    private readonly Func<DebridSettings> _settings;
    private readonly object _gate = new();
    private Task<IReadOnlyList<string>>? _loading; // laufender Abruf der Liste, alle Aufrufer teilen ihn
    private HashSet<string>? _hosts;
    private IReadOnlyList<string> _hostList = Array.Empty<string>();
    private DateTime _hostsAt;

    public DebridService(IDebridProvider provider, Func<DebridSettings> settings)
    {
        Provider = provider;
        _settings = settings;
    }

    public IDebridProvider Provider { get; }

    /// <summary>Dienst ist eingeschaltet und ein auf diesem Konto lesbarer Key ist eingetragen.</summary>
    public bool IsActive => _settings() is { Enabled: true, KeyUsable: true };

    /// <summary>Die zuletzt geladene Liste der Hoster (leer, solange nichts geladen ist).</summary>
    public IReadOnlyList<string> KnownHosts => _hostList;

    /// <summary>Die Hoster-Liste ist geladen (auch eine leere).</summary>
    public bool HostsLoaded => _hosts is not null;

    /// <summary>
    /// Lädt die Liste im Hintergrund, wenn der Dienst aktiv ist (Schalter an, Key da) und sie fehlt oder alt ist. Jeder Aufruf
    /// darf es erneut versuchen, ein laufender Abruf wird geteilt. Das Task scheitert nie (Fehler werden verschluckt, ohne Liste
    /// wird normal geprobt), der UI-Thread wartet nie darauf.
    /// </summary>
    public Task EnsureHosts()
    {
        if (!IsActive || (HostsLoaded && !HostsStale)) return Task.CompletedTask;
        return SwallowAsync(LoadHostsAsync(false, default));

        static async Task SwallowAsync(Task load)
        {
            try
            {
                await load.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // ohne Liste wird normal geprobt
            }
        }
    }

    /// <summary>Prüft einen Key, ohne ihn zu speichern.</summary>
    public Task<DebridAccount> CheckAsync(string apiKey, CancellationToken ct) => Provider.CheckAccountAsync(apiKey, ct);

    /// <summary>Die Liste ist älter als 12 h und wird beim nächsten Bedarf neu geladen.</summary>
    private bool HostsStale => _hosts is not null && DateTime.UtcNow - _hostsAt >= HostsMaxAge;

    /// <summary>
    /// Lädt die Liste der Hoster. Ohne <paramref name="force"/> genügt eine nicht zu alte Liste. Läuft schon ein Abruf, warten
    /// alle auf denselben. <paramref name="ct"/> beendet nur das eigene Warten, nicht den Abruf (der hat sein eigenes Zeitlimit).
    /// </summary>
    public Task<IReadOnlyList<string>> LoadHostsAsync(bool force, CancellationToken ct)
    {
        Task<IReadOnlyList<string>> load;
        lock (_gate)
        {
            if (!force && _hosts is not null && !HostsStale) return Task.FromResult(_hostList);
            // Task.Run: Der Abruf endet nie schon innerhalb dieser Sperre, sein finally räumt sicher erst danach auf.
            load = _loading ??= Task.Run(FetchHostsAsync);
        }
        return load.WaitAsync(ct);
    }

    /// <summary>Der letzte Versuch, die Liste der Hoster zu laden, ist gescheitert (und seither keiner geglückt).</summary>
    public bool HostsLoadFailed => Volatile.Read(ref _hostsFailed);

    private bool _hostsFailed;

    private async Task<IReadOnlyList<string>> FetchHostsAsync()
    {
        try
        {
            var list = await Provider.GetHostsAsync(CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                _hostList = list;
                _hosts = new HashSet<string>(list, StringComparer.Ordinal);
                _hostsAt = DateTime.UtcNow;
            }
            Volatile.Write(ref _hostsFailed, false);
            return list;
        }
        catch (Exception)
        {
            Volatile.Write(ref _hostsFailed, true);
            throw;
        }
        finally
        {
            lock (_gate) _loading = null;
        }
    }

    /// <summary>
    /// Ja oder nein, wenn die Liste bekannt ist, sonst null. <c>cdn.example.com</c> gilt, wenn <c>example.com</c> in der Liste steht.
    /// </summary>
    public bool? Supports(Uri uri)
    {
        var hosts = _hosts;
        if (hosts is null) return null;
        string host = uri.IdnHost.ToLowerInvariant();
        while (true)
        {
            if (hosts.Contains(host)) return true;
            int dot = host.IndexOf('.');
            if (dot < 0 || host.IndexOf('.', dot + 1) < 0) return false; // eine Domain mit nur noch einem Punkt ist das Ende
            host = host[(dot + 1)..];
        }
    }

    /// <summary>
    /// Der Link würde beim Start entsperrt: Dienst aktiv, http(s) und Hoster in der bereits geladenen Liste. Fragt den Dienst nie,
    /// ohne geladene Liste also false.
    /// </summary>
    public bool WillUnlock(string url) =>
        IsActive && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && Supports(uri) == true;

    /// <summary>
    /// Wie viele der Links ein Hoster aus der bekannten Liste sind, obwohl der Dienst ausgeschaltet ist (für den Hinweis im
    /// Hinzufügen-Dialog). 0, wenn er an ist oder die Liste noch nicht geladen wurde. Fragt den Dienst nie.
    /// </summary>
    public int CountSupportedWhileOff(IEnumerable<string> urls)
    {
        if (_settings().Enabled || _hosts is null) return 0;
        int n = 0;
        foreach (string url in urls)
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && Supports(uri) == true) n++;
        return n;
    }

    /// <summary>So viele Links gehen in eine Anfrage des Online-Checks.</summary>
    public const int CheckBatchSize = 50;

    /// <summary>
    /// Online-Check für die Links, die <see cref="WillUnlock"/> bestätigt: Dienst aktiv und Hoster in der bereits geladenen Liste.
    /// Alle anderen Links gehen nie an den Dienst und kommen auch nicht im Ergebnis vor (ihr Status bleibt „unbekannt“). Die
    /// Ergebnisse werden je Anfrage (höchstens <see cref="CheckBatchSize"/> Links) an <paramref name="onResults"/> gegeben, der Aufruf
    /// kommt auf einem beliebigen Thread. Bei einem Fehler enden die Abfragen; was bis dahin ankam, wurde schon geliefert.
    /// Wirft <see cref="DebridException"/>, Netzwerkfehler oder <see cref="TimeoutException"/>.
    /// </summary>
    /// <returns>Wie viele Links an den Dienst gingen.</returns>
    public async Task<int> CheckLinksAsync(IEnumerable<string> urls, Action<IReadOnlyList<LinkInfo>> onResults,
        CancellationToken ct)
    {
        var eligible = urls.Distinct(StringComparer.Ordinal).Where(WillUnlock).ToList();
        if (eligible.Count == 0) return 0;

        var settings = _settings();
        string key = settings.ApiKey
            ?? throw new DebridException(settings.HasKey ? DebridError.KeyUnreadable : DebridError.NoKey);
        for (int i = 0; i < eligible.Count; i += CheckBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = eligible.GetRange(i, Math.Min(CheckBatchSize, eligible.Count - i));
            var infos = await Provider.CheckLinksAsync(key, batch, ct).ConfigureAwait(false);
            onResults(infos);
        }
        return eligible.Count;
    }

    /// <summary>
    /// Entsperrt den Link, wenn der Dienst an ist und der Hoster in der Liste steht. Null heißt: normal herunterladen (Dienst aus,
    /// kein http-Link, Hoster nicht in der Liste, Liste nicht zu bekommen oder vom Dienst abgelehnt). Fehlt die Liste oder ist sie
    /// alt, wird sie hier neu geladen; ohne Liste geht der Link nie an den Dienst. Alle anderen Fehler wirft die Methode als
    /// <see cref="DebridException"/> oder Netzwerkfehler.
    /// </summary>
    /// <param name="required">
    /// Der Download wurde über den Dienst angelegt und darf nie direkt laden (der Hoster liefert nur eine Webseite). Dann gibt es
    /// kein Null: Wo sonst direkt geladen würde, wirft die Methode <see cref="DebridError.Disabled"/>,
    /// <see cref="DebridError.HostsUnavailable"/> oder <see cref="DebridError.NoLongerSupported"/>. Steht der Hoster nicht in
    /// der Liste, wird sie vorher einmal frisch geladen (sie könnte veraltet sein).
    /// </param>
    public async Task<DebridLink?> TryUnlockAsync(string url, IProgress<DebridWait>? waiting, CancellationToken ct,
        bool required = false)
    {
        var settings = _settings();
        if (!settings.Enabled) return required ? throw new DebridException(DebridError.Disabled) : null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return required ? throw new DebridException(DebridError.NoLongerSupported) : null;

        bool? supported = Supports(uri);
        if (supported is null || HostsStale || (required && supported == false))
        {
            try
            {
                await LoadHostsAsync(force: required && supported == false, ct).ConfigureAwait(false);
                supported = Supports(uri);
            }
            catch (Exception ex) when (ex is DebridException or System.Net.Http.HttpRequestException or TimeoutException)
            {
                // Eine vorhandene (alte) Liste gilt weiter. Ohne Liste geht kein Link an den Dienst.
                if (supported is null) return required ? throw new DebridException(DebridError.HostsUnavailable) : null;
            }
        }
        if (supported != true) return required ? throw new DebridException(DebridError.NoLongerSupported) : null;

        // Erst hier braucht es den Key: Normale Links laden auch dann direkt, wenn er fehlt oder unlesbar ist.
        string key = settings.ApiKey
            ?? throw new DebridException(settings.HasKey ? DebridError.KeyUnreadable : DebridError.NoKey);
        try
        {
            return await Provider.UnlockAsync(key, url, null, waiting, ct).ConfigureAwait(false);
        }
        catch (DebridException ex) when (ex.Error == DebridError.HostNotSupported)
        {
            return required ? throw new DebridException(DebridError.NoLongerSupported, ex.Code) : null;
        }
    }
}
