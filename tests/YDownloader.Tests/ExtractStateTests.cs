using System;
using Xunit;
using YDownloader.Downloads;
using YDownloader.Packages;

namespace YDownloader.Tests;

public class ExtractStateTests
{
    [Theory]
    [InlineData(ExtractState.Extracting, "Entpacke")]
    [InlineData(ExtractState.Done, "Entpackt")]
    [InlineData(ExtractState.NeedSevenZip, "7-Zip nötig")]
    [InlineData(ExtractState.NeedPassword, "Passwort nötig")]
    public void States_have_texts(ExtractState state, string expected) =>
        Assert.Contains(expected, ExtractText.Describe(state, ExtractFailure.None, null));

    [Fact]
    public void None_has_no_text() => Assert.Equal("", ExtractText.Suffix(ExtractState.None, ExtractFailure.None, null));

    [Fact]
    public void Failure_shows_short_reason()
    {
        Assert.Contains("Teile fehlen", ExtractText.Describe(ExtractState.Failed, ExtractFailure.MissingParts, null));
        Assert.Contains("unsichere Pfade", ExtractText.Describe(ExtractState.Failed, ExtractFailure.UnsafePath, null));
        Assert.Contains("ERROR: Data Error", ExtractText.Describe(ExtractState.Failed, ExtractFailure.ToolFailed, "ERROR: Data Error"));
        string space = ExtractText.Describe(ExtractState.Failed, ExtractFailure.NoSpace, "1073741824|1048576");
        Assert.Contains("zu wenig Platz", space);
        Assert.Contains("1", space);
    }

    [Fact]
    public void Item_round_trips_state_but_not_running()
    {
        var item = new DownloadItem("https://example.invalid/a.part1.rar", "a.part1.rar", @"C:\x") { PackageId = "p" };
        item.SetExtract(ExtractState.Failed, ExtractFailure.NoSpace, "5|1");
        var back = DownloadItem.FromEntry(item.ToEntry());
        Assert.Equal(ExtractState.Failed, back.Extract);
        Assert.Equal(ExtractFailure.NoSpace, back.ExtractReason);
        Assert.Equal("5|1", back.ExtractDetail);

        item.SetExtract(ExtractState.Extracting);
        Assert.Equal(ExtractState.None, DownloadItem.FromEntry(item.ToEntry()).Extract); // überlebt keinen Neustart

        item.SetExtract(ExtractState.Done);
        Assert.Equal(ExtractState.Done, DownloadItem.FromEntry(item.ToEntry()).Extract);
    }

    [Fact]
    public void Item_shows_state_in_completed_line_only_when_set()
    {
        var item = new DownloadItem("https://example.invalid/a.zip", "a.zip", @"C:\x") { Status = DownloadStatus.Completed };
        Assert.DoesNotContain("Entpack", item.DetailText);
        item.SetExtract(ExtractState.Done);
        Assert.EndsWith(" · Entpackt", item.DetailText);
    }

    [Fact]
    public void Old_json_without_fields_reads_as_none()
    {
        var entry = new DownloadEntry { Id = "x", Url = "https://example.invalid/a", FileName = "a", Folder = @"C:\x" };
        Assert.Equal(ExtractState.None, DownloadItem.FromEntry(entry).Extract);
    }

    [Fact]
    public void Auto_extract_defaults_on()
    {
        var s = new AppSettings();
        Assert.True(s.AutoExtract);
        Assert.Null(s.SevenZipPath);
    }
}
