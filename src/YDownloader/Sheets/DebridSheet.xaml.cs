using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using YDownloader.Debrid;
using YDownloader.Downloads;

namespace YDownloader.Sheets;

/// <summary>
/// AllDebrid: ein-/ausschalten, API-Key eintragen und prüfen, Liste der unterstützten Hoster. Erst „Übernehmen“ speichert.
/// Der Key wird nie angezeigt (PasswordBox, ein gespeicherter erscheint nicht im Feld): ein leeres Feld heißt „Key behalten“.
/// </summary>
public partial class DebridSheet : UserControl
{
    private readonly MainWindow _host;
    private readonly DownloadManager _downloads;
    private readonly CancellationTokenSource _closed = new();
    private bool _removeKey;
    private bool _busy;
    private bool _checkedOk; // „Prüfen“ war erfolgreich und der Nutzer hat den Schalter seither nicht angefasst
    // Zählen jede Änderung am Key und am Schalter. Ein Prüf-Ergebnis gilt nur, wenn sich während der Prüfung nichts geändert hat.
    private int _keyVersion, _switchVersion;
    private IReadOnlyList<string> _hosts = Array.Empty<string>();

    private DebridSheet(MainWindow host, DownloadManager downloads)
    {
        InitializeComponent();
        _host = host;
        _downloads = downloads;
        EnabledBox.IsChecked = downloads.Settings.Debrid.Enabled;
        _hosts = downloads.Debrid.KnownHosts;
        Unloaded += (_, _) => _closed.Cancel(); // eine laufende Prüfung endet mit dem Sheet
        RefreshKeyState();
        RefreshHosts();
    }

    public static void Open(MainWindow host, DownloadManager downloads) =>
        host.ShowSheet(new DebridSheet(host, downloads));

    private bool HasStoredKey => _downloads.Settings.Debrid.HasKey && !_removeKey;

    private void RefreshKeyState()
    {
        bool typing = KeyBox.Password.Length > 0;
        KeyState.Text = _removeKey
            ? Loc.T("Debrid.KeyWillBeRemoved")
            : typing ? Loc.T("Debrid.KeyNew")
            : HasStoredKey && !_downloads.Settings.Debrid.KeyUsable ? Loc.T("Debrid.KeyUnreadable")
            : HasStoredKey ? Loc.T("Debrid.KeyStored")
            : Loc.T("Debrid.KeyNone");
        RemoveKeyButton.Visibility = _downloads.Settings.Debrid.HasKey && !_removeKey ? Visibility.Visible : Visibility.Collapsed;
        CheckButton.IsEnabled = !_busy && (typing || HasStoredKey);
    }

    private void KeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (KeyBox.Password.Length > 0) _removeKey = false;
        _checkedOk = false; // der geprüfte Key ist nicht mehr der im Feld
        _keyVersion++;
        CheckResult.Visibility = Visibility.Collapsed;
        RefreshKeyState();
    }

    private void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        _removeKey = true;
        _checkedOk = false;
        _keyVersion++;
        _switchVersion++; // der Schalter geht unten aus
        KeyBox.Clear();
        EnabledBox.IsChecked = false;
        RefreshKeyState();
    }

    // ---------------------------------------------------------------- Prüfen

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        string? key = KeyBox.Password.Trim();
        if (key.Length > 0 && !DebridSettings.IsValidKey(key))
        {
            ShowResult(Loc.T("Debrid.KeyInvalid"), error: true);
            return;
        }
        if (key.Length == 0) key = _downloads.Settings.Debrid.ApiKey; // gespeicherten Key prüfen
        if (string.IsNullOrEmpty(key))
        {
            ShowResult(Loc.T("Debrid.KeyUnreadable"), error: true); // gespeichert, aber auf diesem Konto nicht lesbar
            return;
        }

        _busy = true;
        CheckButton.IsEnabled = false;
        ShowResult(Loc.T("Debrid.Checking"), error: false);
        int keyVersion = _keyVersion, switchVersion = _switchVersion;
        try
        {
            var account = await _downloads.Debrid.CheckAsync(key, _closed.Token);
            if (keyVersion != _keyVersion)
            {
                CheckResult.Visibility = Visibility.Collapsed; // inzwischen ein anderer Key (oder entfernt): Ergebnis gilt nicht mehr
                return;
            }
            // Hat der Nutzer den Schalter während der Prüfung angefasst, entscheidet er, nicht das Ergebnis.
            _checkedOk = account.IsPremium && switchVersion == _switchVersion;
            ShowResult(account.IsPremium && account.PremiumUntil is { } until
                ? Loc.T("Debrid.CheckOk", account.UserName,
                    until.LocalDateTime.ToString(Loc.T("Format.DatePattern"), System.Globalization.CultureInfo.InvariantCulture))
                : account.IsPremium ? Loc.T("Debrid.CheckOkOpen", account.UserName)
                : Loc.T("Debrid.CheckNoPremium", account.UserName), error: !account.IsPremium, success: account.IsPremium);
        }
        catch (OperationCanceledException)
        {
            return; // Sheet geschlossen
        }
        catch (Exception ex) // auch Unerwartetes nur anzeigen: Ein async-void-Handler darf die App nicht beenden
        {
            if (keyVersion == _keyVersion) ShowResult(Downloads.ErrorText.Describe(ex), error: true); // nicht ErrorText: so heißt hier das Textfeld
            else CheckResult.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _busy = false;
            if (!_closed.IsCancellationRequested) RefreshKeyState();
        }
    }

    private void ShowResult(string text, bool error, bool success = false)
    {
        CheckResult.Text = text;
        CheckResult.SetResourceReference(TextBlock.ForegroundProperty, error ? "TextError" : success ? "TextSuccess" : "TextPrimary");
        CheckResult.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------- Hoster

    private async void LoadHosts_Click(object sender, RoutedEventArgs e)
    {
        LoadHostsButton.IsEnabled = false;
        HostsState.Text = Loc.T("Debrid.HostsLoading");
        try
        {
            _hosts = await _downloads.Debrid.LoadHostsAsync(force: true, _closed.Token);
            RefreshHosts();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) // wie oben: anzeigen statt abstürzen
        {
            HostsState.Text = Loc.T("Debrid.HostsFailed", Downloads.ErrorText.Describe(ex));
        }
        finally
        {
            LoadHostsButton.IsEnabled = true;
        }
    }

    private void HostFilter_TextChanged(object sender, TextChangedEventArgs e) => RefreshHosts();

    private void RefreshHosts()
    {
        LoadHostsButton.Content = Loc.T(_hosts.Count == 0 ? "Debrid.HostsLoad" : "Debrid.HostsReload");
        if (_hosts.Count == 0)
        {
            HostsState.Text = Loc.T("Debrid.HostsNotLoaded");
            HostList.ItemsSource = null;
            return;
        }
        string filter = HostFilter.Text.Trim();
        var shown = filter.Length == 0
            ? _hosts
            : _hosts.Where(h => h.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        HostList.ItemsSource = shown;
        HostsState.Text = filter.Length == 0
            ? Loc.Plural("Debrid.HostsCount", _hosts.Count)
            : shown.Count == 0 ? Loc.T("Debrid.HostsNoMatch") : Loc.Plural("Debrid.HostsMatches", shown.Count, _hosts.Count);
    }

    // ---------------------------------------------------------------- Speichern

    private void Enabled_Click(object sender, RoutedEventArgs e)
    {
        _checkedOk = false;
        _switchVersion++;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = EnabledBox.IsChecked == true || _checkedOk; // geprüfter, gültiger Key: einschalten
        string newKey = KeyBox.Password.Trim();
        if (newKey.Length > 0 && !DebridSettings.IsValidKey(newKey))
        {
            ErrorText.Text = Loc.T("Debrid.KeyInvalid");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        if (enabled && newKey.Length == 0 && !HasStoredKey)
        {
            ErrorText.Text = Loc.T("Debrid.NeedKey");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        if (enabled && newKey.Length == 0 && !_downloads.Settings.Debrid.KeyUsable)
        {
            ErrorText.Text = Loc.T("Debrid.KeyUnreadable"); // gespeichert, aber auf diesem Konto wertlos
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        _downloads.UpdateDebrid(enabled, newKey.Length > 0 ? newKey : null, _removeKey);
        KeyBox.Clear();
        _host.CloseSheet();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        KeyBox.Clear();
        _host.CloseSheet();
    }
}
