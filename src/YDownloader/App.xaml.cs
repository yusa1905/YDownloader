using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using YDownloader.Browser;
using YDownloader.Downloads;
using YDownloader.Power;

namespace YDownloader;

public partial class App : Application
{
    private static readonly Uri LightUri = new("Theme/Light.xaml", UriKind.Relative);
    private static readonly Uri DarkUri = new("Theme/Dark.xaml", UriKind.Relative);

    private ResourceDictionary? _theme;
    private bool _dark;
    private double _scale = 1;
    private TrayIcon? _tray;
    private DownloadManager? _downloads;
    private Mutex? _instance; // bis zum Prozessende gehalten

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool autostart = e.Args.Contains(Autostart.Argument);

        // Eine Instanz reicht: zwei würden dieselben Dateien beschreiben. Ein zweiter Start holt die erste nach vorn.
        _instance = new Mutex(true, @"Local\YDownloader.SingleInstance", out bool first);
        if (!first)
        {
            if (!autostart) TrayIcon.ShowRunningInstance();
            Shutdown();
            return;
        }

        // Als einzige Stelle bekommt der Manager die echten Windows-Aktionen für „Nach Abschluss“.
        _downloads = new DownloadManager(new WindowsPowerActions());
        _downloads.BeginLoadSettings(); // im Hintergrund, während das Fenster entsteht
        ApplyTheme(IsSystemDark());

        var window = new MainWindow(_downloads);
        MainWindow = window;
        _downloads.AwaitSettings();
        ApplyAppearance();
        window.ApplySettings();
        if (!autostart) window.Show();

        // Liste und Tray erst nach dem ersten Fenster.
        Dispatcher.BeginInvoke(new Action(async () => await _downloads.LoadAsync()),
            System.Windows.Threading.DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _tray = new TrayIcon(window);
            _tray.LinksReceived += text => window.ReceiveLinks(text); // Links aus der Browser-Erweiterung
            // Beim Anmelden bleibt das Fenster zu. Fehlt das Symbol, wäre die App unsichtbar: dann doch zeigen.
            if (autostart && !_tray.IsAdded) window.Show();

            UpdateClipboardWatch();

            // Click'n'Load nur, wenn der Nutzer es eingeschaltet hat, und erst nach dem ersten Fenster.
            if (_downloads.Settings.ClickNLoad && StartClickNLoad() is { } problem)
                _tray.Notify(Loc.T("ClickNLoad.Title"), problem, error: true);
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        _downloads.ItemFinished += OnItemFinished;
        _downloads.AfterCompletion.CountdownStarted += OnCountdownStarted;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    // ---------------------------------------------------------------- Zwischenablage

    private sealed class WpfClipboardReader : IClipboardReader
    {
        public string? ReadText() => YDownloader.MainWindow.ClipboardText();
    }

    private ClipboardWatcher? _clipboardWatcher;

    /// <summary>
    /// Schaltet die Überwachung der Zwischenablage nach der Einstellung ein oder aus. Sie hört auf die Windows-Meldung für
    /// Änderungen (kein Abfragen im Takt) und startet erst, wenn das Tray-Fenster existiert.
    /// </summary>
    public void UpdateClipboardWatch()
    {
        if (_tray is null || _downloads is null) return;
        bool on = _downloads.Settings.WatchClipboard;
        if (on && _clipboardWatcher is null)
        {
            _clipboardWatcher = new ClipboardWatcher(new WpfClipboardReader(),
                uri => _downloads.Debrid.Supports(uri) == true,
                name => _downloads.Settings.CategoryFor(name) is not null,
                () => MainWindow is { IsActive: true },
                OnClipboardLinks);
            _tray.ClipboardChanged += _clipboardWatcher.OnClipboardChanged;
        }
        _tray.WatchClipboard = on;
    }

    private void OnClipboardLinks(ClipboardMatch match)
    {
        if (MainWindow is not MainWindow window) return;
        _tray?.Notify(Loc.T("Clipboard.Title"), Loc.Plural("Clipboard.Hint", match.Count), error: false,
            onClick: () => window.ReceiveLinks(match.Text));
    }

    /// <summary>Der Countdown vor Herunterfahren oder Ruhezustand beginnt. Das Fenster kommt nach vorn, der Tray meldet es zusätzlich.</summary>
    private void OnCountdownStarted(object? sender, EventArgs e)
    {
        var plan = _downloads!.AfterCompletion;
        _tray?.Notify(Loc.T("Countdown.Title", PowerText.Name(plan.Kind)),
            Loc.T("Countdown.TrayText", PowerText.Sentence(plan.Kind)), error: false);
    }

    /// <summary>Meldet fertige und fehlgeschlagene Downloads, solange das Fenster nicht im Vordergrund ist.</summary>
    private void OnItemFinished(DownloadItem item, int batch)
    {
        if (_tray is null || MainWindow is { IsActive: true }) return;
        var s = _downloads!.Settings;
        bool summary = batch > 1 && s.NotifyAllDone;
        if (item.Status == DownloadStatus.Failed && s.NotifyFailed)
            _tray.Notify(Loc.T("Notify.Failed"), $"{item.FileName}\n{item.Error}", error: true);
        else if (summary)
            _tray.Notify(Loc.T("Notify.AllDone"), Loc.Plural("Notify.AllDoneCount", batch), error: false);
        else if (item.Status == DownloadStatus.Completed && s.NotifyCompleted)
            _tray.Notify(Loc.T("Notify.Completed"), $"{item.FileName} · {Format.Bytes(item.TotalBytes)}", error: false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _clickNLoad?.Dispose();
        _downloads?.Shutdown();
        _tray?.Dispose();
        base.OnExit(e);
    }

    // ---------------------------------------------------------------- Click'n'Load

    private ClickNLoadServer? _clickNLoad;

    public bool ClickNLoadRunning => _clickNLoad is not null;

    /// <summary>Warum der Server beim letzten Versuch nicht startete, null wenn er läuft oder nicht gewollt ist.</summary>
    public string? ClickNLoadError { get; private set; }

    /// <summary>
    /// Startet den Click'n'Load-Server auf 127.0.0.1, wenn er nicht schon läuft. Eine klare Meldung statt eines Absturzes, wenn der
    /// Port belegt ist (etwa durch JDownloader). Was ankommt, öffnet nur den Hinzufügen-Dialog, geladen wird nichts von selbst.
    /// </summary>
    /// <returns>Null bei Erfolg, sonst die Meldung.</returns>
    public string? StartClickNLoad()
    {
        if (_clickNLoad is not null) return null;
        try
        {
            var server = ClickNLoadServer.Start();
            server.LinksReceived += s => Dispatcher.BeginInvoke(new Action(() => (MainWindow as MainWindow)?.ReceiveLinks(s.Text, s.Passwords)));
            _clickNLoad = server;
            ClickNLoadError = null;
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            ClickNLoadError = ex.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse
                ? Loc.T("ClickNLoad.PortBusy", ClickNLoadServer.DefaultPort)
                : Loc.T("ClickNLoad.Failed", ex.Message);
        }
        return ClickNLoadError;
    }

    public void StopClickNLoad()
    {
        _clickNLoad?.Dispose();
        _clickNLoad = null;
        ClickNLoadError = null;
    }

    /// <summary>Ob das Fenster beim Schließen ins Tray gehen kann: nur mit sichtbarem Symbol.</summary>
    public bool CanHideToTray => _tray is { IsAdded: true };

    /// <summary>Beendet wirklich, auch wenn das Schließen des Fensters sonst nur ins Tray ginge.</summary>
    public void Quit()
    {
        if (MainWindow is MainWindow window) window.ForceClose = true;
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Abmelden oder Herunterfahren darf nicht am ausgeblendeten Fenster hängen.
        if (MainWindow is MainWindow window) window.ForceClose = true;
        base.OnSessionEnding(e);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && _downloads?.Settings.Theme == ThemeMode.System)
            Dispatcher.BeginInvoke(new Action(ApplyAppearance));
    }

    /// <summary>Theme und Skalierung aus den Einstellungen übernehmen (beim Start und nach dem Einstellungs-Sheet).</summary>
    public void ApplyAppearance()
    {
        var settings = _downloads!.Settings;
        ApplyTheme(settings.Theme switch
        {
            ThemeMode.Light => false,
            ThemeMode.Dark => true,
            _ => IsSystemDark(),
        });

        // Das Wörterbuch hält die Transformation eingefroren. Ein neuer Wert unter demselben Schlüssel
        // aktualisiert alle DynamicResource-Verweise.
        double scale = settings.UiScaleFactor;
        if (scale != _scale)
        {
            _scale = scale;
            var transform = new ScaleTransform(scale, scale);
            transform.Freeze();
            Resources["UiScaleTransform"] = transform;
            (MainWindow as MainWindow)?.OnScaleChanged(scale);
        }
    }

    private void ApplyTheme(bool dark)
    {
        if (_theme != null && dark == _dark) return;
        _dark = dark;
        var merged = Resources.MergedDictionaries;
        int index = -1;
        if (_theme != null)
        {
            index = merged.IndexOf(_theme);
            merged.Remove(_theme);
        }
        else
        {
            // Das in App.xaml vorgeladene Light-Wörterbuch ersetzen.
            for (int i = merged.Count - 1; i >= 0; i--)
                if (merged[i].Source?.OriginalString.EndsWith("Light.xaml", StringComparison.Ordinal) == true)
                {
                    merged.RemoveAt(i);
                    index = i;
                }
        }
        _theme = new ResourceDictionary { Source = dark ? DarkUri : LightUri };
        // An derselben Stelle einsetzen: Controls.xaml dahinter sieht die Farben.
        if (index >= 0) merged.Insert(index, _theme);
        else merged.Add(_theme);
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }
}
