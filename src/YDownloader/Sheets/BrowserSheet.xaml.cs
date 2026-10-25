using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using YDownloader.Browser;

namespace YDownloader.Sheets;

/// <summary>
/// Browser verbinden: trägt den Native-Messaging-Host für Chrome und Firefox im Benutzerbereich der Registry ein (oder entfernt
/// ihn) und zeigt den Ordner der Erweiterung. Geändert wird nur auf Knopfdruck.
/// </summary>
public partial class BrowserSheet : UserControl
{
    private readonly MainWindow _host;
    private readonly BrowserSetup _setup;
    private readonly string _extensionDir = Path.Combine(AppContext.BaseDirectory, "Extension");

    private BrowserSheet(MainWindow host)
    {
        InitializeComponent();
        _host = host;
        _setup = BrowserSetup.ForThisApp();
        FolderText.Text = _extensionDir;
        Refresh();
    }

    public static void Open(MainWindow host) => host.ShowSheet(new BrowserSheet(host));

    private void Refresh()
    {
        Show(BrowserKind.Chrome, ChromeState, ChromeButton);
        Show(BrowserKind.Firefox, FirefoxState, FirefoxButton);
    }

    private void Show(BrowserKind kind, TextBlock state, Button button)
    {
        var current = _setup.GetState(kind);
        state.Text = Loc.T(current switch
        {
            BrowserState.Registered => "Browser.StateOk",
            BrowserState.Outdated => "Browser.StateOutdated",
            _ => "Browser.StateNone",
        });
        state.SetResourceReference(TextBlock.ForegroundProperty, current == BrowserState.Registered ? "TextPrimary" : "TextSecondary");
        button.Content = Loc.T(current switch
        {
            BrowserState.Registered => "Browser.Disconnect",
            BrowserState.Outdated => "Browser.Update",
            _ => "Browser.Connect",
        });
        button.Tag = current;
    }

    private void Chrome_Click(object sender, RoutedEventArgs e) => Toggle(BrowserKind.Chrome, ChromeButton);
    private void Firefox_Click(object sender, RoutedEventArgs e) => Toggle(BrowserKind.Firefox, FirefoxButton);

    /// <summary>Verbinden oder Aktualisieren, bei bestehender passender Verbindung trennen.</summary>
    private void Toggle(BrowserKind kind, Button button)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            if (button.Tag is BrowserState.Registered) _setup.Unregister(kind);
            else _setup.Register(kind);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ErrorText.Text = Loc.T("Browser.Err", ex.Message);
            ErrorText.Visibility = Visibility.Visible;
        }
        Refresh();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_extensionDir))
        {
            ErrorText.Text = Loc.T("Browser.NoFolder", _extensionDir);
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_extensionDir}\"") { UseShellExecute = true })?.Dispose();
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_extensionDir); }
        catch (System.Runtime.InteropServices.COMException) { } // Zwischenablage gerade belegt
    }

    private void Close_Click(object sender, RoutedEventArgs e) => _host.CloseSheet();
}
