using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using YDownloader.Downloads;

namespace YDownloader.Sheets;

/// <summary>Eine bearbeitbare Zeile. Zahlen und Uhrzeiten bleiben Text, bis „Übernehmen“ sie prüft.</summary>
public sealed class QueueRow : Observable
{
    private readonly bool[] _days = new bool[7];
    private string _name = "";
    private string _concurrent = "";
    private bool _hasSchedule;
    private string _start = "02:00";
    private string _stop = "";

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Concurrent { get => _concurrent; set => Set(ref _concurrent, value); }
    public bool HasSchedule { get => _hasSchedule; set => Set(ref _hasSchedule, value); }
    public string Start { get => _start; set => Set(ref _start, value); }
    public string Stop { get => _stop; set => Set(ref _stop, value); }

    public bool Mon { get => _days[0]; set => SetDay(0, value); }
    public bool Tue { get => _days[1]; set => SetDay(1, value); }
    public bool Wed { get => _days[2]; set => SetDay(2, value); }
    public bool Thu { get => _days[3]; set => SetDay(3, value); }
    public bool Fri { get => _days[4]; set => SetDay(4, value); }
    public bool Sat { get => _days[5]; set => SetDay(5, value); }
    public bool Sun { get => _days[6]; set => SetDay(6, value); }

    public int Days
    {
        get => Enumerable.Range(0, 7).Where(i => _days[i]).Aggregate(0, (mask, i) => mask | (1 << i));
        set
        {
            for (int i = 0; i < 7; i++) _days[i] = (value & (1 << i)) != 0;
            foreach (string name in new[] { nameof(Mon), nameof(Tue), nameof(Wed), nameof(Thu), nameof(Fri), nameof(Sat), nameof(Sun) })
                Raise(name);
        }
    }

    private void SetDay(int index, bool value)
    {
        if (_days[index] == value) return;
        _days[index] = value;
        Raise(new[] { nameof(Mon), nameof(Tue), nameof(Wed), nameof(Thu), nameof(Fri), nameof(Sat), nameof(Sun) }[index]);
    }
}

/// <summary>Eigene Queues anlegen, umbenennen, mit Zeitplan versehen, löschen. Erst „Übernehmen“ speichert.</summary>
public partial class QueuesSheet : UserControl
{
    private const int MaxConcurrentLimit = 20;

    private readonly MainWindow _host;
    private readonly DownloadManager _downloads;
    private readonly ObservableCollection<QueueRow> _rows = new();

    private QueuesSheet(MainWindow host, DownloadManager downloads)
    {
        InitializeComponent();
        _host = host;
        _downloads = downloads;
        foreach (var q in downloads.Settings.Queues)
        {
            var row = new QueueRow
            {
                Id = q.Id,
                Name = q.Name,
                Concurrent = q.MaxConcurrent > 0 ? q.MaxConcurrent.ToString(CultureInfo.InvariantCulture) : "",
            };
            if (q.Schedule is { } s)
            {
                row.HasSchedule = true;
                row.Start = ScheduleSetting.Time(s.StartMinute);
                row.Stop = s.StopMinute is int stop ? ScheduleSetting.Time(stop) : "";
                row.Days = s.Days;
            }
            else
            {
                row.Days = ScheduleSetting.AllDays;
            }
            _rows.Add(row);
        }
        Rows.ItemsSource = _rows;
        _rows.CollectionChanged += (_, _) => UpdateEmptyText();
        UpdateEmptyText();
    }

    public static void Open(MainWindow host, DownloadManager downloads) =>
        host.ShowSheet(new QueuesSheet(host, downloads));

    private void UpdateEmptyText() => EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void AddRow_Click(object sender, RoutedEventArgs e) =>
        _rows.Add(new QueueRow { Name = Loc.T("Queues.NewName"), Days = ScheduleSetting.AllDays });

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is QueueRow row) _rows.Remove(row);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Loc.T("Queue.Default") }; // der Name der Standard-Queue ist vergeben
        var queues = new List<QueueSetting>();
        foreach (var row in _rows)
        {
            string name = row.Name.Trim();
            if (name.Length == 0) { ShowError(Loc.T("Queues.Err.NameRequired")); return; }
            if (!names.Add(name)) { ShowError(Loc.T("Categories.NameTwice", name)); return; }

            int concurrent = 0;
            string limit = row.Concurrent.Trim();
            if (limit.Length > 0 && (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out concurrent)
                || concurrent < 1 || concurrent > MaxConcurrentLimit))
            {
                ShowError(Loc.T("Queues.Err.Concurrent", name, MaxConcurrentLimit));
                return;
            }

            ScheduleSetting? schedule = null;
            if (row.HasSchedule)
            {
                if (!ScheduleSetting.TryParseTime(row.Start, out int start))
                {
                    ShowError(Loc.T("Queues.Err.Start", name));
                    return;
                }
                int? stop = null;
                if (row.Stop.Trim().Length > 0)
                {
                    if (!ScheduleSetting.TryParseTime(row.Stop, out int parsed))
                    {
                        ShowError(Loc.T("Queues.Err.Stop", name));
                        return;
                    }
                    if (parsed == start)
                    {
                        ShowError(Loc.T("Queues.Err.StartStop", name));
                        return;
                    }
                    stop = parsed;
                }
                if (row.Days == 0)
                {
                    ShowError(Loc.T("Queues.Err.Day", name));
                    return;
                }
                schedule = new ScheduleSetting { StartMinute = start, StopMinute = stop, Days = row.Days };
            }

            queues.Add(new QueueSetting { Id = row.Id, Name = name, MaxConcurrent = concurrent, Schedule = schedule });
        }

        _downloads.UpdateQueues(queues);
        _host.CloseSheet();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _host.CloseSheet();
}
