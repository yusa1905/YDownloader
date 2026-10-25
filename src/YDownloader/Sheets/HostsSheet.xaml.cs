using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using YDownloader.Controls;
using YDownloader.Downloads;
using YDownloader.Engine;

namespace YDownloader.Sheets;

/// <summary>Eine bearbeitbare Zeile. Zahlen bleiben Text, bis „Übernehmen“ sie prüft.</summary>
public sealed class HostRow : Observable
{
    private string _host = "";
    private string _connections = "";
    private string _speedKBps = "";
    private string _userAgent = "";

    public string Host { get => _host; set => Set(ref _host, value); }
    public string Connections { get => _connections; set => Set(ref _connections, value); }
    public string SpeedKBps { get => _speedKBps; set => Set(ref _speedKBps, value); }
    public string UserAgent { get => _userAgent; set => Set(ref _userAgent, value); }

    public bool IsEmpty => Host.Trim().Length == 0 && Connections.Trim().Length == 0
        && SpeedKBps.Trim().Length == 0 && UserAgent.Trim().Length == 0;
}

/// <summary>Verbindungen, Tempo und User-Agent je Host. Erst „Übernehmen“ speichert.</summary>
public partial class HostsSheet : UserControl
{
    // 0 = unbegrenzt. Die Liste hört bei 64 auf, das ist auch die Obergrenze für eine Regel.
    private static readonly int[] DefaultChoices = { 0, 1, 2, 4, 6, 8, 12, 16, 24, 32, 48, 64 };

    private readonly MainWindow _host;
    private readonly DownloadManager _downloads;
    private readonly ObservableCollection<HostRow> _rows = new();
    private readonly PopupChoice _default;

    private HostsSheet(MainWindow host, DownloadManager downloads)
    {
        InitializeComponent();
        _host = host;
        _downloads = downloads;
        var s = downloads.Settings;
        foreach (var h in s.Hosts)
            _rows.Add(new HostRow
            {
                Host = h.Host,
                Connections = h.MaxConnections > 0 ? h.MaxConnections.ToString(CultureInfo.InvariantCulture) : "",
                SpeedKBps = h.SpeedLimitKBps > 0 ? h.SpeedLimitKBps.ToString(CultureInfo.InvariantCulture) : "",
                UserAgent = h.UserAgent ?? "",
            });
        Rows.ItemsSource = _rows;

        var choices = DefaultChoices.Select(n => (n == 0 ? Loc.T("Hosts.Unlimited") : n.ToString(CultureInfo.InvariantCulture),
            (string?)n.ToString(CultureInfo.InvariantCulture))).ToList();
        string current = s.ConnectionsPerHost.ToString(CultureInfo.InvariantCulture);
        if (!DefaultChoices.Contains(s.ConnectionsPerHost)) choices.Add((current, current)); // von Hand eingetragener Wert
        _default = new PopupChoice(DefaultButton, choices, current);
    }

    public static void Open(MainWindow host, DownloadManager downloads) =>
        host.ShowSheet(new HostsSheet(host, downloads));

    private void AddRow_Click(object sender, RoutedEventArgs e) => _rows.Add(new HostRow());

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HostRow row) _rows.Remove(row);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var hosts = new List<HostSetting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            if (row.IsEmpty) continue; // eine unbenutzte Zeile stört nicht

            string? host = HostRegistry.NormalizeHost(row.Host);
            if (host is null) { ShowError(Loc.T("Hosts.Err.InvalidHost", row.Host.Trim())); return; }
            if (!seen.Add(host)) { ShowError(Loc.T("Hosts.Err.Twice", host)); return; }

            if (!TryNumber(row.Connections, HostSetting.MaxConnectionsLimit, out int connections))
            {
                ShowError(Loc.T("Hosts.Err.Connections", host, HostSetting.MaxConnectionsLimit));
                return;
            }
            if (!TryNumber(row.SpeedKBps, HostSetting.MaxSpeedKBps, out int speed))
            {
                ShowError(Loc.T("Hosts.Err.Limit", host));
                return;
            }
            string agent = row.UserAgent.Trim();
            if (agent.Any(char.IsControl))
            {
                ShowError(Loc.T("Hosts.Err.Agent", host));
                return;
            }

            hosts.Add(new HostSetting
            {
                Host = host,
                MaxConnections = connections,
                SpeedLimitKBps = speed,
                UserAgent = agent.Length > 0 ? agent : null,
            });
        }

        _downloads.UpdateHosts(int.Parse(_default.Value!, CultureInfo.InvariantCulture), hosts);
        _host.CloseSheet();
    }

    /// <summary>Leer ist 0 (Standard). Sonst eine ganze Zahl von 1 bis <paramref name="max"/>.</summary>
    private static bool TryNumber(string text, int max, out int value)
    {
        value = 0;
        text = text.Trim();
        if (text.Length == 0) return true;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1 && value <= max;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _host.CloseSheet();
}
