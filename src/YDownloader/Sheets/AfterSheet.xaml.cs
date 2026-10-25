using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using YDownloader.Controls;
using YDownloader.Downloads;
using YDownloader.Power;

namespace YDownloader.Sheets;

/// <summary>„Nach Abschluss“ festlegen: welche Aktion, und wann (alle fertig oder ein bestimmter Download).</summary>
public partial class AfterSheet : UserControl
{
    private const string WhenAll = "all", WhenOne = "one";

    private readonly MainWindow _host;
    private readonly DownloadManager _downloads;
    private readonly List<DownloadItem> _targets;
    private readonly PopupChoice _action, _when, _target;

    private AfterSheet(MainWindow host, DownloadManager downloads, DownloadItem? preselected)
    {
        InitializeComponent();
        _host = host;
        _downloads = downloads;
        var plan = downloads.AfterCompletion;

        var actions = new List<(string Label, string? Value)> { (Loc.T("After.DoNothing"), nameof(PowerAction.None)) };
        foreach (var action in new[] { PowerAction.Shutdown, PowerAction.Hibernate, PowerAction.Sleep })
            if (plan.Actions.IsAvailable(action)) actions.Add((PowerText.Name(action), action.ToString()));
        if (!plan.Actions.IsAvailable(PowerAction.Hibernate))
        {
            NoteText.Text = Loc.T("After.NoHibernate");
            NoteText.Visibility = Visibility.Visible;
        }
        _action = new PopupChoice(ActionButton, actions, plan.Kind.ToString());

        // Auswahl: alles, was noch nicht fertig ist.
        _targets = downloads.Items.Where(i => i.Status != DownloadStatus.Completed).ToList();
        string? target = plan.TargetId is not null && _targets.Any(i => i.Id == plan.TargetId) ? plan.TargetId
            : preselected is not null && _targets.Contains(preselected) ? preselected.Id
            : _targets.FirstOrDefault()?.Id;
        bool specific = (plan.IsArmed && plan.TargetId is not null) || preselected is not null;

        var whens = new List<(string Label, string? Value)> { (Loc.T("After.WhenAll"), WhenAll) };
        if (_targets.Count > 0) whens.Add((Loc.T("After.WhenOne"), WhenOne));
        _when = new PopupChoice(WhenButton, whens, specific && _targets.Count > 0 ? WhenOne : WhenAll);
        _when.Changed += (_, _) => UpdateTargetRow();

        var targets = _targets.Select(i => (i.FileName, (string?)i.Id)).ToList();
        _target = new PopupChoice(TargetButton, targets, target);
        UpdateTargetRow();
    }

    public static void Open(MainWindow host, DownloadManager downloads, DownloadItem? preselected = null) =>
        host.ShowSheet(new AfterSheet(host, downloads, preselected));

    private void UpdateTargetRow()
    {
        var visibility = _when.Value == WhenOne ? Visibility.Visible : Visibility.Collapsed;
        TargetLabel.Visibility = visibility;
        TargetButton.Visibility = visibility;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var plan = _downloads.AfterCompletion;
        var action = Enum.Parse<PowerAction>(_action.Value!);
        if (action == PowerAction.None)
        {
            plan.Disarm();
        }
        else if (_when.Value == WhenOne)
        {
            var item = _targets.FirstOrDefault(i => i.Id == _target.Value);
            if (item is null)
            {
                ErrorText.Text = Loc.T("After.ChooseDownload");
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
            plan.Arm(action, item.Id, item.FileName);
        }
        else
        {
            plan.Arm(action);
        }
        _host.CloseSheet();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _host.CloseSheet();
}
