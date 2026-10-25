using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace YDownloader.Downloads;

public enum SidebarKind { Header, All, Active, Paused, Completed, Failed, Category, Uncategorized, Queue, DefaultQueue }

/// <summary>Welches Bearbeiten-Sheet der kleine Knopf an einer Überschrift öffnet.</summary>
public enum SidebarEdit { None, Categories, Queues }

/// <summary>Ein Eintrag der Seitenleiste: Ansicht nach Zustand, Kategorie oder Queue, mit Anzahl.</summary>
public sealed class SidebarEntry : Observable
{
    private int _count;
    private string _title;

    public SidebarEntry(SidebarKind kind, string title, string iconKey, string? category = null, string? queueId = null,
        SidebarEdit edit = SidebarEdit.None)
    {
        Kind = kind;
        Edit = edit;
        _title = title;
        IconKey = iconKey;
        Category = category;
        QueueId = queueId;
    }

    public SidebarKind Kind { get; }
    public string Title { get => _title; set => Set(ref _title, value); }

    /// <summary>Schlüssel der Symbol-Ressource in Icons.xaml.</summary>
    public string IconKey { get; }

    public string Icon => IconKey.Length > 0 ? System.Windows.Application.Current?.TryFindResource(IconKey) as string ?? "" : "";
    public string? Category { get; }

    /// <summary>Bei <see cref="SidebarKind.Queue"/> die Id der Queue.</summary>
    public string? QueueId { get; }

    public int Count
    {
        get => _count;
        set { if (Set(ref _count, value)) Raise(nameof(CountText)); }
    }

    public string CountText => _count > 0 ? _count.ToString() : "";
    public bool IsHeader => Kind == SidebarKind.Header;

    /// <summary>Nur Überschriften: das Sheet, das der Bearbeiten-Knopf öffnet.</summary>
    public SidebarEdit Edit { get; }

    public bool HasEdit => Edit != SidebarEdit.None;

    public string EditTip => Edit switch
    {
        SidebarEdit.Categories => Loc.T("Main.EditCategories"),
        SidebarEdit.Queues => Loc.T("Main.EditQueues"),
        _ => "",
    };

    /// <summary>Name für Screenreader (UI Automation nimmt ToString der Einträge).</summary>
    public override string ToString() => _title;

    public bool Matches(DownloadItem item) => Kind switch
    {
        SidebarKind.Active => item.Status is DownloadStatus.Running or DownloadStatus.Queued,
        SidebarKind.Paused => item.Status == DownloadStatus.Paused,
        SidebarKind.Completed => item.Status == DownloadStatus.Completed,
        SidebarKind.Failed => item.Status == DownloadStatus.Failed,
        SidebarKind.Category => string.Equals(item.Category, Category, StringComparison.OrdinalIgnoreCase),
        SidebarKind.Uncategorized => item.Category is null,
        SidebarKind.Queue => item.QueueId == QueueId,
        SidebarKind.DefaultQueue => item.QueueId is null,
        _ => true,
    };
}

/// <summary>Einträge der Seitenleiste. Zählt bei jeder Änderung der Liste neu (ein Durchlauf, nicht pro Takt).</summary>
public sealed class Sidebar
{
    private readonly List<SidebarEntry> _fixed;

    public Sidebar()
    {
        All = new SidebarEntry(SidebarKind.All, Loc.T("Sidebar.All"), "IconAll");
        _fixed = new List<SidebarEntry>
        {
            All,
            new(SidebarKind.Active, Loc.T("Sidebar.Active"), "IconActive"),
            new(SidebarKind.Paused, Loc.T("Sidebar.Paused"), "IconPause"),
            new(SidebarKind.Completed, Loc.T("Sidebar.Completed"), "IconDone"),
            new(SidebarKind.Failed, Loc.T("Sidebar.Failed"), "IconWarning"),
        };
    }

    public ObservableCollection<SidebarEntry> Entries { get; } = new();
    public SidebarEntry All { get; }

    private string _signature = "";

    /// <summary>Stimmen Kategorien und Queues noch mit der Seitenleiste überein? Sonst <see cref="Rebuild"/> (sonst springt die Auswahl).</summary>
    public bool IsCurrent(IEnumerable<CategorySetting> categories, IEnumerable<QueueSetting> queues) =>
        string.Equals(_signature, Signature(categories, queues), StringComparison.Ordinal);

    private static string Signature(IEnumerable<CategorySetting> categories, IEnumerable<QueueSetting> queues) =>
        string.Join('\u0001', categories.Select(c => c.Key + '\u0003' + c.Name)) + '\u0002' + string.Join('\u0001', queues.Select(q => q.Id + '\u0003' + q.Name));

    /// <summary>
    /// Baut Kategorien und Queues neu auf. Queues erscheinen erst, wenn es eigene gibt. Liefert den Eintrag, der
    /// ausgewählt bleiben soll.
    /// </summary>
    public SidebarEntry Rebuild(IEnumerable<CategorySetting> categories, IEnumerable<QueueSetting> queues, SidebarEntry? selected)
    {
        var queueList = queues.ToList();
        _signature = Signature(categories, queueList);

        var wanted = new List<SidebarEntry>(_fixed);
        // Die Überschrift steht immer da, sie trägt den Knopf, mit dem man die erste eigene Queue anlegt.
        wanted.Add(new(SidebarKind.Header, Loc.T("Sidebar.Queues"), "", edit: SidebarEdit.Queues));
        if (queueList.Count > 0)
        {
            wanted.Add(Reuse(SidebarKind.DefaultQueue, null, null) ?? new(SidebarKind.DefaultQueue, Loc.T("Queue.Default"), "IconQueue"));
            foreach (var q in queueList)
            {
                var entry = Reuse(SidebarKind.Queue, null, q.Id) ?? new(SidebarKind.Queue, q.Name, "IconQueue", queueId: q.Id);
                entry.Title = q.Name;
                wanted.Add(entry);
            }
        }
        wanted.Add(new(SidebarKind.Header, Loc.T("Sidebar.Categories"), "", edit: SidebarEdit.Categories));
        foreach (var c in categories)
        {
            var entry = Reuse(SidebarKind.Category, c.Key, null) ?? new(SidebarKind.Category, c.Name, "IconTag", c.Key);
            entry.Title = c.Name;
            wanted.Add(entry);
        }
        wanted.Add(Reuse(SidebarKind.Uncategorized, null, null) ?? new(SidebarKind.Uncategorized, Loc.T("Sidebar.Uncategorized"), "IconTag"));

        Entries.Clear();
        foreach (var e in wanted) Entries.Add(e);
        return selected is not null && Entries.Contains(selected) ? selected : All;
    }

    private SidebarEntry? Reuse(SidebarKind kind, string? category, string? queueId)
    {
        foreach (var e in Entries)
            if (e.Kind == kind && string.Equals(e.Category, category, StringComparison.Ordinal)
                && string.Equals(e.QueueId, queueId, StringComparison.Ordinal)) return e;
        return null;
    }

    public void Recount(IEnumerable<DownloadItem> items)
    {
        foreach (var e in Entries) e.Count = 0;
        foreach (var item in items)
            foreach (var e in Entries)
                if (!e.IsHeader && e.Matches(item)) e.Count++;
    }
}
