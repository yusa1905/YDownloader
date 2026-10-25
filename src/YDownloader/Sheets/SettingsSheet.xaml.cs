using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using YDownloader.Controls;
using YDownloader.Downloads;
using YDownloader.Packages;

namespace YDownloader.Sheets;

/// <summary>Alle Einstellungen an einer Stelle. Erst „Übernehmen“ ändert und speichert sie.</summary>
public partial class SettingsSheet : UserControl
{
    private static readonly int[] Scales = { 80, 90, 100, 110, 125, 150, 175, 200 };

    private readonly MainWindow _host;
    private readonly DownloadManager _downloads;
    private readonly PopupChoice _concurrent, _theme, _scale, _language, _close;

    private SettingsSheet(MainWindow host, DownloadManager downloads)
    {
        InitializeComponent();
        _host = host;
        _downloads = downloads;
        var s = downloads.Settings;

        FolderBox.Text = s.DownloadFolder ?? "";
        FolderBox.Tag = AppPaths.UserDownloads();
        _concurrent = new PopupChoice(ConcurrentButton,
            Enumerable.Range(1, 10).Select(n => (n.ToString(), (string?)n.ToString())).ToList(), s.MaxConcurrent.ToString());
        _theme = new PopupChoice(ThemeButton, new List<(string, string?)>
        {
            (Loc.T("Settings.System"), nameof(ThemeMode.System)),
            (Loc.T("Settings.Theme.Light"), nameof(ThemeMode.Light)),
            (Loc.T("Settings.Theme.Dark"), nameof(ThemeMode.Dark)),
        }, s.Theme.ToString());
        _scale = new PopupChoice(ScaleButton,
            Scales.Select(p => (Loc.T("Format.Percent", p), (string?)p.ToString())).ToList(), s.UiScale.ToString());

        // Die Sprachen heißen in der eigenen Sprache. Leer = der Sprache von Windows folgen. Gilt nach dem Neustart.
        var languages = new List<(string, string?)> { (Loc.T("Settings.System"), "") };
        languages.AddRange(Loc.Languages.Select(l => (l.Name, (string?)l.Tag)));
        _language = new PopupChoice(LanguageButton, languages, s.Language ?? "");

        _close = new PopupChoice(CloseButton, new List<(string, string?)>
        {
            (Loc.T("Settings.Close.Exit"), nameof(CloseAction.Exit)),
            (Loc.T("Settings.Close.Tray"), nameof(CloseAction.Tray)),
        }, s.CloseAction.ToString());
        AutostartBox.IsChecked = Autostart.IsEnabled();
        ResumeBox.IsChecked = s.ResumeOnStart;
        KeepAwakeBox.IsChecked = s.KeepAwake;
        AutoExtractBox.IsChecked = s.AutoExtract;
        SevenZipBox.Text = s.SevenZipPath ?? "";
        ClickNLoadBox.IsChecked = s.ClickNLoad;
        RulesBox.Text = string.Join(Environment.NewLine, s.AutoDeselect);
        ClipboardBox.IsChecked = s.WatchClipboard;
        _passwordsUnreadable = s.ArchivePasswords.Unreadable;
        if (_passwordsUnreadable) // anderes Windows-Konto oder beschädigt: nichts anzeigen, aber auch nicht still überschreiben
        {
            PasswordsNote.Text = Loc.T("Settings.ArchivePasswordsUnreadable");
            PasswordsNote.SetResourceReference(TextBlock.ForegroundProperty, "TextWarning");
        }
        else PasswordsBox.Text = string.Join(Environment.NewLine, s.ArchivePasswords.Passwords);
        // Nur wenn es etwas zu sagen gibt: der letzte Startversuch scheiterte (Port belegt).
        if (s.ClickNLoad && ((App)Application.Current).ClickNLoadError is { } cnlProblem)
        {
            ClickNLoadStatus.Text = cnlProblem;
            ClickNLoadStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextError");
            ClickNLoadStatus.Visibility = Visibility.Visible;
        }
        PortableBox.IsChecked = AppPaths.IsPortable;
        DataDirText.Text = Loc.T("Settings.DataDir", AppPaths.DataDir);
        DataDirText.ToolTip = AppPaths.DataDir;
        NotifyCompletedBox.IsChecked = s.NotifyCompleted;
        NotifyFailedBox.IsChecked = s.NotifyFailed;
        NotifyAllDoneBox.IsChecked = s.NotifyAllDone;
    }

    private bool _passwordsUnreadable;

    public static void Open(MainWindow host, DownloadManager downloads) =>
        host.ShowSheet(new SettingsSheet(host, downloads));

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        string current = FolderBox.Text.Trim();
        var dialog = new OpenFolderDialog
        {
            Title = Loc.T("Settings.ChooseFolder"),
            InitialDirectory = Directory.Exists(current) ? current : _downloads.Settings.EffectiveDownloadFolder,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) FolderBox.Text = dialog.FolderName;
    }

    // Ein Klick übernimmt zuerst die offenen Änderungen (wie „Übernehmen“, das schließt dieses Sheet).
    // Scheitert das, bleibt das Sheet offen und zeigt die Meldung.
    private async void Debrid_Click(object sender, RoutedEventArgs e) { if (await ApplyAsync()) DebridSheet.Open(_host, _downloads); }
    private async void Hosts_Click(object sender, RoutedEventArgs e) { if (await ApplyAsync()) HostsSheet.Open(_host, _downloads); }
    private async void Browser_Click(object sender, RoutedEventArgs e) { if (await ApplyAsync()) BrowserSheet.Open(_host); }

    private async void Save_Click(object sender, RoutedEventArgs e) => await ApplyAsync();

    // true = übernommen und das Sheet geschlossen.
    private async System.Threading.Tasks.Task<bool> ApplyAsync()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        string folder = FolderBox.Text.Trim();
        if (folder.Length > 0 && (folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(folder)))
        {
            ShowError(Loc.T("Settings.FolderInvalid"));
            return false;
        }
        string sevenZip = SevenZipBox.Text.Trim().Trim('"');
        if (sevenZip.Length > 0 && (sevenZip.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(sevenZip)))
        {
            ShowError(Loc.T("Settings.SevenZipInvalid"));
            return false;
        }

        // Was fehlschlagen kann, zuerst: dann bleibt bei einem Fehler alles beim Alten.
        try
        {
            // Bei gesetztem Haken auch ohne Änderung schreiben: Das korrigiert einen verschobenen Programmordner.
            if (AutostartBox.IsChecked == true || Autostart.IsEnabled()) Autostart.Set(AutostartBox.IsChecked == true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ShowError(Loc.T("Settings.AutostartFailed", ex.Message));
            return false;
        }

        // Der Datenordner zuerst, damit die neuen Einstellungen schon im neuen Ordner landen.
        bool portable = PortableBox.IsChecked == true;
        if (portable != AppPaths.IsPortable)
        {
            SaveButton.IsEnabled = false;
            if (await _downloads.MoveDataAsync(portable) is { } error)
            {
                SaveButton.IsEnabled = true;
                ShowError(error);
                return false;
            }
        }

        // Der Server auch zuerst: Ist der Port belegt, bleibt der Dialog offen und sagt es, statt still zu scheitern.
        var app = (App)Application.Current;
        bool clickNLoad = ClickNLoadBox.IsChecked == true;
        if (clickNLoad)
        {
            if (app.StartClickNLoad() is { } problem)
            {
                ShowError(problem);
                SaveButton.IsEnabled = true;
                return false;
            }
        }
        else app.StopClickNLoad();

        var s = _downloads.Settings;
        s.ClickNLoad = clickNLoad;
        s.AutoDeselect = DeselectRules.Normalize(RulesBox.Text.Split('\n'));
        s.WatchClipboard = ClipboardBox.IsChecked == true;
        // Eine nicht lesbare Liste bleibt, wie sie ist, solange nichts Neues eingetragen wird.
        if (!_passwordsUnreadable || PasswordsBox.Text.Trim().Length > 0)
            s.ArchivePasswords.Passwords = PasswordRules.ParseLines(PasswordsBox.Text);
        s.DownloadFolder = folder.Length > 0 ? folder : null;
        s.MaxConcurrent = int.Parse(_concurrent.Value!);
        s.Theme = Enum.Parse<ThemeMode>(_theme.Value!);
        s.UiScale = int.Parse(_scale.Value!);
        s.Language = string.IsNullOrEmpty(_language.Value) ? null : _language.Value;
        s.CloseAction = Enum.Parse<CloseAction>(_close.Value!);
        s.ResumeOnStart = ResumeBox.IsChecked == true;
        s.KeepAwake = KeepAwakeBox.IsChecked == true;
        s.AutoExtract = AutoExtractBox.IsChecked == true;
        s.SevenZipPath = sevenZip.Length > 0 ? sevenZip : null;
        s.NotifyCompleted = NotifyCompletedBox.IsChecked == true;
        s.NotifyFailed = NotifyFailedBox.IsChecked == true;
        s.NotifyAllDone = NotifyAllDoneBox.IsChecked == true;

        _host.CloseSheet();
        app.ApplyAppearance();
        app.UpdateClipboardWatch();
        _downloads.SettingsChanged();
        return true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _host.CloseSheet();
}
