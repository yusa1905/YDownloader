using System.IO;
using Xunit;
using YDownloader.Downloads;

namespace YDownloader.Tests;

public class FreeSpaceTests
{
    [Fact]
    public void Query_finds_the_free_space_of_an_existing_drive_even_for_a_folder_that_does_not_exist()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ydl-free-" + System.Guid.NewGuid().ToString("N"));
        Assert.True(FreeSpaceMonitor.Query(folder) > 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\0path")]
    public void Query_gives_null_instead_of_throwing_for_unusable_paths(string folder)
    {
        Assert.Null(FreeSpaceMonitor.Query(folder));
    }

    [Fact]
    public void Text_is_empty_when_unknown_and_formatted_when_known()
    {
        Assert.Equal("", FreeSpaceMonitor.Text(null));
        Assert.Equal(Loc.T("Stats.Free", Format.Bytes(1_660_000_000_000)), FreeSpaceMonitor.Text(1_660_000_000_000));
        Assert.Equal("1,51 TB frei", FreeSpaceMonitor.Text(1_660_000_000_000));
    }
}
