using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;

namespace YDownloader.Engine;

/// <summary>
/// Gibt für gleiche Netzwerk-Einstellungen (Proxy, SSL-Schalter, Verbindungs-Zeitlimit) immer denselben
/// <see cref="HttpClient"/> samt Handler heraus. So teilen sich alle Downloads Verbindungspool und TLS-Sitzungen,
/// statt je einen eigenen Pool aufzubauen. Der User-Agent hängt nicht am Handler, ihn setzt jede Anfrage selbst.
/// </summary>
public sealed class HttpClientPool : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<Key, HttpClient> _clients = new();
    private bool _disposed;

    /// <summary>Wie viele verschiedene Clients bisher entstanden sind.</summary>
    public int Count { get { lock (_lock) return _clients.Count; } }

    public HttpClient Get(DownloadOptions options)
    {
        string? proxy = string.IsNullOrWhiteSpace(options.Proxy) ? null : options.Proxy.Trim();
        var key = new Key(proxy, options.IgnoreSslErrors, options.ConnectTimeout);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_clients.TryGetValue(key, out var client))
                _clients[key] = client = new HttpClient(CreateHandler(options), disposeHandler: true)
                {
                    Timeout = Timeout.InfiniteTimeSpan,
                };
            return client;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (var client in _clients.Values) client.Dispose();
            _clients.Clear();
        }
    }

    internal static SocketsHttpHandler CreateHandler(DownloadOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None, // Range-Bytes müssen den Dateibytes entsprechen
            AllowAutoRedirect = true,
            // Die Grenze je Host setzt die Engine selbst (HostRegistry): Wartende im Handler würden ins Zeitlimit laufen.
            MaxConnectionsPerServer = int.MaxValue,
            ConnectTimeout = options.ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        if (!string.IsNullOrWhiteSpace(options.Proxy))
        {
            handler.Proxy = new WebProxy(options.Proxy);
            handler.UseProxy = true;
        }
        if (options.IgnoreSslErrors)
            handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        return handler;
    }

    private readonly record struct Key(string? Proxy, bool IgnoreSslErrors, TimeSpan ConnectTimeout);
}
