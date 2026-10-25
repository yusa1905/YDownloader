using System.Collections.Generic;
using System.Linq;
using Xunit;
using YDownloader.Downloads;

namespace YDownloader.Tests;

public class StatsEtaTests
{
    [Fact]
    public void Total_eta_is_remaining_bytes_over_total_speed_and_a_dash_without_speed()
    {
        Assert.Equal(Loc.T("Stats.Eta", Format.Duration(50)), LiveStats.EtaFor(5_000, 100));
        Assert.Equal("–", LiveStats.EtaFor(5_000, 0));
        Assert.Equal("–", LiveStats.EtaFor(5_000, 0.5));
    }
}

public class SidebarTests
{
    private static readonly List<CategorySetting> Categories = new() { new() { Name = "Videos" } };

    private static DownloadItem Item(string? queue) => new("http://x/a", "a.bin", "C:\\") { QueueId = queue };

    [Fact]
    public void Without_own_queues_there_are_no_queue_entries_but_the_header_keeps_its_edit_button()
    {
        var sidebar = new Sidebar();
        sidebar.Rebuild(Categories, new List<QueueSetting>(), null);

        Assert.DoesNotContain(sidebar.Entries, e => e.Kind is SidebarKind.Queue or SidebarKind.DefaultQueue);
        Assert.Contains(sidebar.Entries, e => e.IsHeader && e.Title == "Queues" && e.Edit == SidebarEdit.Queues);
        Assert.Contains(sidebar.Entries, e => e.IsHeader && e.Title == "Kategorien" && e.Edit == SidebarEdit.Categories);
        Assert.DoesNotContain(sidebar.Entries, e => !e.IsHeader && e.HasEdit);
    }

    [Fact]
    public void Own_queues_come_with_a_header_and_the_standard_queue_before_the_categories()
    {
        var sidebar = new Sidebar();
        var night = new QueueSetting { Name = "Nacht" };
        sidebar.Rebuild(Categories, new[] { night }, null);

        var titles = sidebar.Entries.Select(e => e.Title).ToList();
        Assert.Equal(new[] { "Alle", "Aktiv", "Angehalten", "Fertig", "Fehler", "Queues", "Standard", "Nacht", "Kategorien", "Videos", "Ohne Kategorie" }, titles);
    }

    [Fact]
    public void Queue_entries_match_their_own_downloads_and_the_standard_queue_matches_the_rest()
    {
        var sidebar = new Sidebar();
        var night = new QueueSetting { Name = "Nacht" };
        sidebar.Rebuild(Categories, new[] { night }, null);
        var nightEntry = sidebar.Entries.Single(e => e.Kind == SidebarKind.Queue);
        var standardEntry = sidebar.Entries.Single(e => e.Kind == SidebarKind.DefaultQueue);

        Assert.True(nightEntry.Matches(Item(night.Id)));
        Assert.False(nightEntry.Matches(Item(null)));
        Assert.False(nightEntry.Matches(Item("andere")));
        Assert.True(standardEntry.Matches(Item(null)));
        Assert.False(standardEntry.Matches(Item(night.Id)));

        sidebar.Recount(new[] { Item(null), Item(night.Id), Item(night.Id) });
        Assert.Equal(2, nightEntry.Count);
        Assert.Equal(1, standardEntry.Count);
        Assert.Equal(3, sidebar.All.Count);
    }

    [Fact]
    public void Knows_when_it_has_to_be_rebuilt_and_keeps_entries_on_a_rename()
    {
        var sidebar = new Sidebar();
        var night = new QueueSetting { Name = "Nacht" };
        sidebar.Rebuild(Categories, new[] { night }, null);
        Assert.True(sidebar.IsCurrent(Categories, new[] { night }));
        Assert.False(sidebar.IsCurrent(Categories, new List<QueueSetting>()));
        Assert.False(sidebar.IsCurrent(new List<CategorySetting>(), new[] { night }));

        var entry = sidebar.Entries.Single(e => e.Kind == SidebarKind.Queue);
        night.Name = "Nachts";
        Assert.False(sidebar.IsCurrent(Categories, new[] { night }));
        sidebar.Rebuild(Categories, new[] { night }, entry);

        Assert.Same(entry, sidebar.Entries.Single(e => e.Kind == SidebarKind.Queue)); // gleicher Eintrag, die Auswahl bleibt
        Assert.Equal("Nachts", entry.Title);
        Assert.True(sidebar.IsCurrent(Categories, new[] { night }));
    }

    [Fact]
    public void Selection_falls_back_to_all_when_the_queue_is_gone()
    {
        var sidebar = new Sidebar();
        var night = new QueueSetting { Name = "Nacht" };
        sidebar.Rebuild(Categories, new[] { night }, null);
        var entry = sidebar.Entries.Single(e => e.Kind == SidebarKind.Queue);

        Assert.Same(sidebar.All, sidebar.Rebuild(Categories, new List<QueueSetting>(), entry));
    }
}
