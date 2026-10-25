using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace YDownloader.Engine;

/// <summary>
/// Sonderregel für einen Host und seine Subdomains. 0 beziehungsweise null heißt: Vorgabe gilt.
/// </summary>
/// <param name="Host">Etwa <c>example.com</c>. Gilt auch für <c>cdn.example.com</c>.</param>
/// <param name="MaxConnections">Gleichzeitige Verbindungen zu diesem Host, über alle Downloads.</param>
/// <param name="BytesPerSecond">Tempolimit für diesen Host, über alle Downloads.</param>
/// <param name="UserAgent">Eigener User-Agent für diesen Host.</param>
public sealed record HostRule(string Host, int MaxConnections = 0, long BytesPerSecond = 0, string? UserAgent = null);

/// <summary>Was für einen Host gerade gilt: Verbindungsgrenze, Tempolimit und User-Agent. Wird bei Regeländerungen nachgeführt.</summary>
public sealed class HostContext
{
    private volatile string? _userAgent;

    internal HostContext(string key) => Key = key;

    /// <summary>Host der Regel, sonst der Host selbst.</summary>
    public string Key { get; }

    public ConnectionGate Gate { get; } = new();
    public SpeedLimiter Limiter { get; } = new();

    /// <summary>Null = der allgemeine User-Agent.</summary>
    public string? UserAgent
    {
        get => _userAgent;
        internal set => _userAgent = value;
    }
}

/// <summary>
/// Verwaltet die Verbindungsgrenze, das Tempolimit und den User-Agent je Host. Alle Downloads, die dieselbe Instanz
/// benutzen, teilen sich also die Verbindungen zu einem Host. Regeln dürfen im Betrieb geändert werden.
/// </summary>
public sealed class HostRegistry
{
    private readonly ConcurrentDictionary<string, HostContext> _contexts = new(StringComparer.OrdinalIgnoreCase);
    private volatile HostRule[] _rules = Array.Empty<HostRule>();
    private volatile int _defaultMax;

    /// <summary>Verbindungen pro Host, wenn keine Regel etwas anderes sagt. 0 = unbegrenzt.</summary>
    public int DefaultMaxConnections
    {
        get => _defaultMax;
        set
        {
            _defaultMax = Math.Max(0, value);
            Reconfigure();
        }
    }

    public IReadOnlyList<HostRule> Rules => _rules;

    /// <summary>Ersetzt alle Regeln. Ungültige Hosts fallen weg, bei doppelten gilt der letzte Eintrag.</summary>
    public void SetRules(IEnumerable<HostRule> rules)
    {
        var byHost = new Dictionary<string, HostRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            string? host = NormalizeHost(rule.Host);
            if (host is null) continue;
            byHost[host] = rule with
            {
                Host = host,
                MaxConnections = Math.Max(0, rule.MaxConnections),
                BytesPerSecond = Math.Max(0, rule.BytesPerSecond),
                UserAgent = string.IsNullOrWhiteSpace(rule.UserAgent) ? null : rule.UserAgent.Trim(),
            };
        }
        // Längster Host zuerst: die genaueste Regel gewinnt.
        _rules = byHost.Values.OrderByDescending(r => r.Host.Length).ToArray();
        Reconfigure();
    }

    /// <summary>Die Regel, die für den Host gilt (genau oder als Oberdomain), sonst null.</summary>
    public HostRule? FindRule(string host)
    {
        foreach (var rule in _rules)
            if (Matches(rule.Host, host)) return rule;
        return null;
    }

    /// <summary>Verbindungsgrenze, Tempolimit und User-Agent für einen Link. Hosts unter derselben Regel teilen sich alles.</summary>
    public HostContext Resolve(Uri url)
    {
        string host = url.Host.ToLowerInvariant();
        string key = FindRule(host)?.Host ?? host;
        if (_contexts.TryGetValue(key, out var existing)) return existing;
        return _contexts.GetOrAdd(key, k =>
        {
            var created = new HostContext(k);
            Configure(created, _rules);
            return created;
        });
    }

    private void Reconfigure()
    {
        var rules = _rules;
        foreach (var context in _contexts.Values) Configure(context, rules);
    }

    private void Configure(HostContext context, HostRule[] rules)
    {
        HostRule? rule = null;
        foreach (var r in rules)
            if (string.Equals(r.Host, context.Key, StringComparison.OrdinalIgnoreCase)) { rule = r; break; }

        int max = rule is { MaxConnections: > 0 } ? rule.MaxConnections : _defaultMax;
        if (context.Gate.Limit != max) context.Gate.Limit = max;
        long rate = rule?.BytesPerSecond ?? 0;
        if (context.Limiter.BytesPerSecond != rate) context.Limiter.BytesPerSecond = rate;
        context.UserAgent = rule?.UserAgent;
    }

    private static bool Matches(string ruleHost, string host) =>
        host.Length >= ruleHost.Length
        && host.EndsWith(ruleHost, StringComparison.OrdinalIgnoreCase)
        && (host.Length == ruleHost.Length || host[host.Length - ruleHost.Length - 1] == '.');

    /// <summary>
    /// Bringt Eingaben wie <c>https://www.Example.com:8080/pfad</c>, <c>*.example.com</c> oder <c>example.com</c> auf
    /// <c>www.example.com</c> beziehungsweise <c>example.com</c>. Null, wenn kein Host erkennbar ist.
    /// </summary>
    public static string? NormalizeHost(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        string text = input.Trim();
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return null;
            text = uri.Host;
        }
        else
        {
            int slash = text.IndexOfAny(new[] { '/', '?', '#' });
            if (slash >= 0) text = text[..slash];
            int colon = text.LastIndexOf(':');
            if (colon > 0 && text.IndexOf(':') == colon) text = text[..colon]; // Port, aber nicht bei IPv6
        }
        text = text.TrimStart('*', '.').TrimEnd('.').ToLowerInvariant();
        if (text.Length == 0 || text.Any(c => char.IsWhiteSpace(c) || c is '/' or '\\' or '@')) return null;
        return text;
    }
}
