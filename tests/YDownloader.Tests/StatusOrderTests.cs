using System.Collections.Generic;
using System.Linq;
using Xunit;
using YDownloader.Downloads;

namespace YDownloader.Tests;

public class StatusOrderTests
{
    private sealed class Row
    {
        public string Name = "";
        public DownloadStatus Status;
        public override string ToString() => Name;
    }

    private static Row R(string name, DownloadStatus s) => new() { Name = name, Status = s };

    private static (StatusOrder<Row> Order, List<Row> Source) Make(params Row[] rows)
    {
        var source = rows.ToList();
        var order = new StatusOrder<Row>(r => StatusGroups.Of(r.Status), r => source.IndexOf(r));
        order.Commit(source);
        return (order, source);
    }

    private static string Names(IEnumerable<Row> rows) => string.Join(",", rows.Select(r => r.Name));

    [Theory]
    [InlineData(DownloadStatus.Running, StatusGroup.Active)]
    [InlineData(DownloadStatus.Queued, StatusGroup.Active)]
    [InlineData(DownloadStatus.Paused, StatusGroup.Stopped)]
    [InlineData(DownloadStatus.Failed, StatusGroup.Stopped)]
    [InlineData(DownloadStatus.Completed, StatusGroup.Done)]
    public void Status_maps_to_group(DownloadStatus status, StatusGroup group) =>
        Assert.Equal(group, StatusGroups.Of(status));

    [Fact]
    public void Groups_come_in_order_and_keep_source_order_within_a_group()
    {
        var (order, src) = Make(
            R("done1", DownloadStatus.Completed), R("pause1", DownloadStatus.Paused), R("run1", DownloadStatus.Running),
            R("fail1", DownloadStatus.Failed), R("queue1", DownloadStatus.Queued), R("done2", DownloadStatus.Completed),
            R("run2", DownloadStatus.Running));
        Assert.Equal("run1,queue1,run2,pause1,fail1,done1,done2", Names(order.Arrange(src)));
    }

    [Fact]
    public void Status_change_inside_the_same_group_is_not_stale()
    {
        var (order, src) = Make(R("a", DownloadStatus.Queued), R("b", DownloadStatus.Paused));
        src[0].Status = DownloadStatus.Running;
        src[1].Status = DownloadStatus.Failed;
        Assert.False(order.IsStale(src));
    }

    [Fact]
    public void Group_change_is_stale_but_order_stays_until_commit()
    {
        var (order, src) = Make(R("a", DownloadStatus.Running), R("b", DownloadStatus.Running), R("c", DownloadStatus.Paused));
        src[0].Status = DownloadStatus.Completed;
        Assert.True(order.IsStale(src));
        Assert.Equal("a,b,c", Names(order.Arrange(src))); // eingefroren: nichts bewegt sich
        order.Commit(src);
        Assert.False(order.IsStale(src));
        Assert.Equal("b,c,a", Names(order.Arrange(src)));
    }

    [Fact]
    public void Progress_ticks_never_change_the_order()
    {
        var (order, src) = Make(R("a", DownloadStatus.Running), R("b", DownloadStatus.Completed));
        for (int i = 0; i < 100; i++)
        {
            Assert.False(order.IsStale(src));
            Assert.Equal("a,b", Names(order.Arrange(src)));
        }
    }

    [Fact]
    public void Item_returning_to_a_group_goes_back_to_its_source_position()
    {
        var (order, src) = Make(R("a", DownloadStatus.Paused), R("b", DownloadStatus.Paused), R("c", DownloadStatus.Paused));
        src[1].Status = DownloadStatus.Running;
        order.Commit(src);
        Assert.Equal("b,a,c", Names(order.Arrange(src)));
        src[1].Status = DownloadStatus.Paused;
        order.Commit(src);
        Assert.Equal("a,b,c", Names(order.Arrange(src)));
    }

    [Fact]
    public void New_item_sorts_by_its_current_group_and_position_before_the_next_commit()
    {
        var (order, src) = Make(R("a", DownloadStatus.Running), R("b", DownloadStatus.Completed));
        src.Add(R("n", DownloadStatus.Paused));
        Assert.True(order.IsStale(src)); // unbekannt: ein Statuswechsel soll beim nächsten Mal sortieren
        Assert.Equal("a,n,b", Names(order.Arrange(src)));
        src.Add(R("m", DownloadStatus.Running));
        Assert.Equal("a,m,n,b", Names(order.Arrange(src)));
    }

    [Fact]
    public void Removed_items_and_empty_list_are_fine()
    {
        var (order, src) = Make(R("a", DownloadStatus.Running), R("b", DownloadStatus.Paused));
        src.RemoveAt(0);
        Assert.False(order.IsStale(src));
        Assert.Equal("b", Names(order.Arrange(src)));
        src.Clear();
        order.Commit(src);
        Assert.Empty(order.Arrange(src));
    }

    [Fact]
    public void Removing_an_item_shifts_source_positions_but_not_the_order_within_groups()
    {
        var (order, src) = Make(R("a", DownloadStatus.Paused), R("b", DownloadStatus.Paused), R("c", DownloadStatus.Paused));
        src.RemoveAt(0);
        Assert.Equal("b,c", Names(order.Arrange(src)));
    }
}
