using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using YDownloader.Power;

namespace YDownloader.Sheets;

/// <summary>
/// Der Countdown vor Herunterfahren, Ruhezustand oder Energie sparen. „Abbrechen“ ist Standard und Escape: Ein
/// versehentlicher Tastendruck verlängert die Wartezeit nicht, er stoppt die Aktion.
/// </summary>
public partial class CountdownSheet : UserControl
{
    private readonly MainWindow _host;
    private readonly AfterCompletion _plan;

    private CountdownSheet(MainWindow host, AfterCompletion plan)
    {
        InitializeComponent();
        _host = host;
        _plan = plan;

        var action = plan.Kind;
        TitleText.Text = Loc.T("Countdown.Title", PowerText.Name(action));
        string reason = plan.TargetId is null ? Loc.T("Countdown.ReasonAll") : Loc.T("Countdown.ReasonOne", plan.TargetName);
        ReasonText.Text = $"{reason} {PowerText.Sentence(action)}";
        NowButton.Content = Loc.T("Countdown.NowAction", PowerText.Name(action));
        ShowSeconds();

        plan.PropertyChanged += OnPlanChanged;
        Unloaded += (_, _) => plan.PropertyChanged -= OnPlanChanged;
    }

    /// <summary>Zeigt den Countdown und holt das Fenster aus dem Tray, damit ihn jemand sieht.</summary>
    public static void Open(MainWindow host, AfterCompletion plan)
    {
        var sheet = new CountdownSheet(host, plan);
        host.Reveal();
        host.ShowSheet(sheet, sheet.CancelButton);
    }

    private void OnPlanChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AfterCompletion.RemainingSeconds)) ShowSeconds();
        // Vorbei, abgebrochen oder durch neue Downloads unterbrochen: das Sheet hat nichts mehr zu sagen.
        else if (e.PropertyName == nameof(AfterCompletion.IsCountingDown) && !_plan.IsCountingDown)
            _host.CloseSheetIfShowing(this);
    }

    private void ShowSeconds() => SecondsText.Text = _plan.RemainingSeconds.ToString();

    private void Now_Click(object sender, RoutedEventArgs e)
    {
        NowButton.IsEnabled = false;
        _plan.ExecuteNow();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _plan.Cancel();
        _host.CloseSheetIfShowing(this);
    }
}
