using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Xunit;
using YDownloader.Downloads;
using YDownloader.Packages;

namespace YDownloader.Tests;

public class PackageGroupingTests
{
    private static DownloadItem Item(string name, string? package = null, DownloadStatus status = DownloadStatus.Paused, long total = -1)
    {
        var item = new DownloadItem("http://x.invalid/" + name, name, "C:\\t") { PackageId = package, PackageName = package };
        item.TotalBytes = total;
        item.Status = status;
        return item;
    }

    private static string Names(IEnumerable<object> rows) =>
        string.Join(",", rows.Select(r => r is PackageHead h ? "[" + h.Id + "]" : ((DownloadItem)r).FileName));

    private static List<object> Build(PackageGrouping g, params DownloadItem[] items) => g.Build(items);

    // ---------------------------------------------------------------- Aufbau

    [Fact]
    public void Plain_downloads_stay_plain_rows()
    {
        var a = Item("a.bin");
        var b = Item("b.bin");
        Assert.Equal("a.bin,b.bin", Names(Build(new PackageGrouping(), a, b)));
    }

    [Fact]
    public void Two_parts_get_a_head_that_stays_collapsed_by_default()
    {
        var g = new PackageGrouping();
        var rows = Build(g, Item("a.part1.rar", "p"), Item("a.part2.rar", "p"));
        Assert.Equal("[p]", Names(rows));
        Assert.False(((PackageHead)rows[0]).IsExpanded);
    }

    [Fact]
    public void A_package_with_one_part_is_a_normal_row()
    {
        var g = new PackageGrouping();
        var single = Item("only.zip", "p");
        var rows = Build(g, single, Item("x.bin"));
        Assert.Equal("only.zip,x.bin", Names(rows));
        Assert.False(single.IsPackagePart);
        Assert.Empty(g.Heads);
    }

    [Fact]
    public void Head_counts_only_visible_parts_so_a_filter_can_turn_it_into_a_normal_row()
    {
        var g = new PackageGrouping();
        var a = Item("a.part1.rar", "p");
        var b = Item("a.part2.rar", "p");
        Assert.Equal("[p]", Names(Build(g, a, b)));
        Assert.Equal("a.part1.rar", Names(Build(g, a)));
        Assert.Empty(g.Heads);
    }

    [Fact]
    public void Expanded_head_is_followed_by_its_parts_which_are_marked_as_indented()
    {
        var g = new PackageGrouping();
        var a = Item("a.part1.rar", "p");
        var b = Item("a.part2.rar", "p");
        var x = Item("x.bin");
        g.SetExpanded("p", true);
        var rows = Build(g, a, x, b);
        // Der Kopf steht beim ersten Teil, der zweite Teil rückt unter ihn, auch wenn dazwischen etwas steht.
        Assert.Equal("[p],a.part1.rar,a.part2.rar,x.bin", Names(rows));
        Assert.True(a.IsPackagePart);
        Assert.True(b.IsPackagePart);
        Assert.False(x.IsPackagePart);
    }

    [Fact]
    public void Collapsing_clears_the_indent_flag()
    {
        var g = new PackageGrouping();
        var a = Item("a.part1.rar", "p");
        var b = Item("a.part2.rar", "p");
        g.SetExpanded("p", true);
        Build(g, a, b);
        g.SetExpanded("p", false);
        Build(g, a, b);
        Assert.False(a.IsPackagePart);
        Assert.False(b.IsPackagePart);
    }

    [Fact]
    public void Head_keeps_its_identity_and_expanded_state_across_builds()
    {
        var g = new PackageGrouping();
        var a = Item("a.part1.rar", "p");
        var b = Item("a.part2.rar", "p");
        var first = (PackageHead)Build(g, a, b)[0];
        g.Toggle(first);
        var second = (PackageHead)Build(g, a, b)[0];
        Assert.Same(first, second);
        Assert.True(second.IsExpanded);
    }

    [Fact]
    public void Expanded_state_survives_a_filter_that_hides_the_package()
    {
        var g = new PackageGrouping();
        var a = Item("a.part1.rar", "p");
        var b = Item("a.part2.rar", "p");
        g.SetExpanded("p", true);
        Build(g, a, b);
        Build(g, Item("other.bin"));
        Assert.Equal("[p],a.part1.rar,a.part2.rar", Names(Build(g, a, b)));
    }

    [Fact]
    public void Two_packages_keep_the_order_of_their_first_parts()
    {
        var g = new PackageGrouping();
        var rows = Build(g, Item("q1.rar", "q"), Item("p1.rar", "p"), Item("q2.rar", "q"), Item("p2.rar", "p"));
        Assert.Equal("[q],[p]", Names(rows));
    }

    // ---------------------------------------------------------------- Auswahl

    [Fact]
    public void Expand_turns_a_head_into_its_parts_and_drops_duplicates()
    {
        var g = new PackageGrouping();
        var a = Item("a.part1.rar", "p");
        var b = Item("a.part2.rar", "p");
        var x = Item("x.bin");
        g.SetExpanded("p", true);
        var rows = Build(g, a, b, x);
        var head = rows[0];
        // Kopf und ein Teil zugleich markiert: der Teil zählt nur einmal.
        var expanded = PackageGrouping.Expand(new[] { x, head, a });
        Assert.Equal(new[] { x, a, b }, expanded);
    }

    [Fact]
    public void Expand_of_a_head_only_covers_visible_parts()
    {
        var g = new PackageGrouping();
        var a = Item("a.part1.rar", "p");
        var b = Item("a.part2.rar", "p");
        var hidden = Item("a.part3.rar", "p");
        var head = Build(g, a, b)[0];
        Assert.Equal(new[] { a, b }, PackageGrouping.Expand(new[] { head }));
        Assert.DoesNotContain(hidden, PackageGrouping.Expand(new[] { head }));
    }

    // ---------------------------------------------------------------- Kopfwerte

    [Fact]
    public void Head_sums_size_progress_and_speed_of_its_parts()
    {
        var g = new PackageGrouping();
        var a = Item("a.part1.rar", "p", DownloadStatus.Completed, 1000);
        var b = Item("a.part2.rar", "p", DownloadStatus.Queued, 1000);
        var head = (PackageHead)Build(g, a, b)[0];
        Assert.Equal(DownloadStatus.Queued, head.Status);
        Assert.Equal(0.5, head.Fraction, 3);
        Assert.StartsWith("1 von 2 Teilen", head.DetailText);
        Assert.False(head.BarHidden);
    }

    [Fact]
    public void Head_of_finished_package_is_completed_without_bar()
    {
        var g = new PackageGrouping();
        var head = (PackageHead)Build(g,
            Item("a.part1.rar", "p", DownloadStatus.Completed, 1000),
            Item("a.part2.rar", "p", DownloadStatus.Completed, 1000))[0];
        Assert.Equal(DownloadStatus.Completed, head.Status);
        Assert.True(head.BarHidden);
        Assert.StartsWith("2 Teile", head.DetailText);
        Assert.Equal(1.0, head.Fraction);
    }

    [Theory]
    [InlineData(DownloadStatus.Running, DownloadStatus.Failed, DownloadStatus.Running)]
    [InlineData(DownloadStatus.Queued, DownloadStatus.Failed, DownloadStatus.Queued)]
    [InlineData(DownloadStatus.Paused, DownloadStatus.Failed, DownloadStatus.Failed)]
    [InlineData(DownloadStatus.Paused, DownloadStatus.Completed, DownloadStatus.Paused)]
    public void Head_status_prefers_running_then_queued_then_failed_then_paused(DownloadStatus s1, DownloadStatus s2, DownloadStatus expected)
    {
        var g = new PackageGrouping();
        var head = (PackageHead)Build(g, Item("a.part1.rar", "p", s1), Item("a.part2.rar", "p", s2))[0];
        Assert.Equal(expected, head.Status);
    }

    [Fact]
    public void Failed_part_shows_an_error_in_the_collapsed_head()
    {
        var g = new PackageGrouping();
        var head = (PackageHead)Build(g,
            Item("a.part1.rar", "p", DownloadStatus.Failed),
            Item("a.part2.rar", "p", DownloadStatus.Paused))[0];
        Assert.False(head.IsExpanded);
        Assert.Equal(PackageTone.Error, head.Tone);
        Assert.Equal("1 Fehler", head.StateText);
    }

    [Theory]
    [InlineData(ExtractState.NeedPassword, PackageTone.Warning)]
    [InlineData(ExtractState.NeedSevenZip, PackageTone.Warning)]
    [InlineData(ExtractState.Failed, PackageTone.Error)]
    [InlineData(ExtractState.Done, PackageTone.None)]
    public void Extract_problems_show_in_the_head_with_the_matching_tone(ExtractState state, PackageTone tone)
    {
        var g = new PackageGrouping();
        var a = Item("a.part1.rar", "p", DownloadStatus.Completed, 10);
        var b = Item("a.part2.rar", "p", DownloadStatus.Completed, 10);
        a.SetExtract(state);
        b.SetExtract(state);
        var head = (PackageHead)Build(g, a, b)[0];
        Assert.Equal(tone, head.Tone);
        Assert.False(string.IsNullOrEmpty(head.StateText));
    }

    [Fact]
    public void Extract_failure_wins_over_a_failed_download_and_warnings_come_after_errors()
    {
        var g = new PackageGrouping();
        var a = Item("a.part1.rar", "p", DownloadStatus.Failed);
        var b = Item("a.part2.rar", "p", DownloadStatus.Completed, 10);
        b.SetExtract(ExtractState.NeedPassword);
        var head = (PackageHead)Build(g, a, b)[0];
        Assert.Equal(PackageTone.Error, head.Tone); // der gescheiterte Teil schlägt „Passwort nötig“
        Assert.Equal("1 Fehler", head.StateText);
    }

    [Fact]
    public void Update_reports_only_changed_values()
    {
        var g = new PackageGrouping();
        var a = Item("a.part1.rar", "p", DownloadStatus.Queued, 1000);
        var b = Item("a.part2.rar", "p", DownloadStatus.Queued, 1000);
        var head = (PackageHead)Build(g, a, b)[0];
        var changed = new List<string?>();
        head.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        head.Update();
        Assert.Empty(changed);
        a.Status = DownloadStatus.Completed;
        head.Update();
        Assert.Contains(nameof(PackageHead.DetailText), changed);
        Assert.Contains(nameof(PackageHead.Fraction), changed);
        Assert.DoesNotContain(nameof(PackageHead.Name), changed);
    }

    [Fact]
    public void UpdateRunning_skips_heads_without_running_parts()
    {
        var g = new PackageGrouping();
        var a = Item("a.part1.rar", "p", DownloadStatus.Queued, 1000);
        var b = Item("a.part2.rar", "p", DownloadStatus.Queued, 1000);
        var head = (PackageHead)Build(g, a, b)[0];
        a.Status = DownloadStatus.Completed; // ohne Update bleibt der Kopf auf dem alten Stand
        g.UpdateRunning();
        Assert.Equal(0, head.Fraction);
        g.UpdateAll();
        Assert.Equal(0.5, head.Fraction, 3);
    }

    // ---------------------------------------------------------------- Abgleich der Zeilen

    private static RangeCollection<object> Rows(params object[] rows)
    {
        var c = new RangeCollection<object>();
        c.AddRange(rows);
        return c;
    }

    private static List<NotifyCollectionChangedAction> Watch(RangeCollection<object> c)
    {
        var log = new List<NotifyCollectionChangedAction>();
        c.CollectionChanged += (_, e) => log.Add(e.Action);
        return log;
    }

    [Fact]
    public void Sync_into_an_empty_list_is_a_single_reset()
    {
        var c = Rows();
        var log = Watch(c);
        var a = new object();
        var b = new object();
        PackageGrouping.Sync(c, new[] { a, b });
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, log);
        Assert.Equal(new[] { a, b }, c);
    }

    [Fact]
    public void Sync_without_changes_reports_nothing()
    {
        var a = new object();
        var b = new object();
        var c = Rows(a, b);
        var log = Watch(c);
        PackageGrouping.Sync(c, new[] { a, b });
        Assert.Empty(log);
    }

    [Fact]
    public void Sync_small_change_never_resets_so_selection_and_containers_survive()
    {
        var o = Enumerable.Range(0, 10).Select(_ => new object()).ToArray();
        var c = Rows(o);
        var log = Watch(c);
        var added = new object();
        // o[3] entfernt, o[7] nach vorn, ein neuer Eintrag am Ende
        var desired = new List<object> { o[7], o[0], o[1], o[2], o[4], o[5], o[6], o[8], o[9], added };
        PackageGrouping.Sync(c, desired);
        Assert.Equal(desired, c);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, log);
    }

    [Fact]
    public void Sync_big_change_resets_once()
    {
        var old = Enumerable.Range(0, 100).Select(_ => new object()).ToArray();
        var c = Rows(old);
        var log = Watch(c);
        var desired = old.Take(5).ToArray();
        PackageGrouping.Sync(c, desired);
        Assert.Equal(desired, c);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, log);
    }

    [Fact]
    public void Sync_random_changes_always_end_in_the_desired_order()
    {
        var rng = new System.Random(7);
        var pool = Enumerable.Range(0, 60).Select(_ => new object()).ToArray();
        var c = Rows(pool.Take(30).ToArray());
        for (int round = 0; round < 200; round++)
        {
            var desired = pool.OrderBy(_ => rng.Next()).Take(rng.Next(0, 40)).ToList();
            PackageGrouping.Sync(c, desired);
            Assert.Equal(desired, c);
        }
    }

    // ---------------------------------------------------------------- Ganzes: Gruppen, Köpfe, Abgleich

    [Fact]
    public void Status_change_of_a_part_moves_the_head_and_keeps_its_row_object()
    {
        var g = new PackageGrouping();
        var done = Item("old.bin", status: DownloadStatus.Completed, total: 5);
        var a = Item("a.part1.rar", "p", DownloadStatus.Paused);
        var b = Item("a.part2.rar", "p", DownloadStatus.Paused);

        var rows = Rows();
        PackageGrouping.Sync(rows, g.Build(new[] { a, b, done }));
        var head = rows[0];

        // Ordnung nach Statusgruppe: ein fertiger Download rückt vor die angehaltenen Teile (hier von Hand nachgestellt).
        PackageGrouping.Sync(rows, g.Build(new[] { done, a, b }));
        Assert.Equal("old.bin,[p]", Names(rows));
        Assert.Same(head, rows[1]);
    }
}
