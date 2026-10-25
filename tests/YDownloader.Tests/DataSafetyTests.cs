using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using YDownloader.Downloads;
using YDownloader.Engine;
using Xunit;

namespace YDownloader.Tests;

/// <summary>
/// Laden und Speichern von <c>downloads.json</c> und <c>settings.json</c>: gesperrte oder kaputte Dateien dürfen nie durch
/// leere Vorgaben ersetzt werden. Nur Dateien in eigenen Temp-Ordnern, nie die echten Daten.
/// </summary>
public class DataSafetyTests
{
    private static readonly TimeSpan NoPause = TimeSpan.FromMilliseconds(1);

    private static DownloadList OneItem() => new() { Items = { new DownloadEntry { Id = "a", Url = "http://x.invalid/a.bin" } } };

    // ---------------------------------------------------------------- downloads.json

    [Fact]
    public void First_start_without_file_loads_empty_and_may_save()
    {
        using var dir = new TempDir();
        var store = new DownloadStore(dir.File("downloads.json"), NoPause);

        var list = store.Load();

        Assert.Empty(list.Items);
        Assert.Equal(DataFileState.Missing, store.LoadState);
        Assert.True(DataFile.MaySave(store.LoadState));
        store.SaveNow(OneItem());
        Assert.Single(new DownloadStore(dir.File("downloads.json"), NoPause).Load().Items);
    }

    [Fact]
    public void Locked_file_is_not_touched_and_may_not_be_saved()
    {
        using var dir = new TempDir();
        string path = dir.File("downloads.json");
        new DownloadStore(path, NoPause).SaveNow(OneItem());
        byte[] before = File.ReadAllBytes(path);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) // z. B. Virenscanner
        {
            var store = new DownloadStore(path, NoPause);
            var list = store.Load();

            Assert.Empty(list.Items);
            Assert.Equal(DataFileState.Locked, store.LoadState);
            Assert.False(DataFile.MaySave(store.LoadState));
        }

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bad"));
    }

    [Fact]
    public async Task Brief_lock_is_waited_out()
    {
        using var dir = new TempDir();
        string path = dir.File("downloads.json");
        new DownloadStore(path, NoPause).SaveNow(OneItem());

        var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(150); hold.Dispose(); });

        var store = new DownloadStore(path, TimeSpan.FromMilliseconds(100));
        var list = store.Load();
        await release;

        Assert.Equal(DataFileState.Ok, store.LoadState);
        Assert.Single(list.Items);
    }

    [Fact]
    public void Corrupt_file_is_copied_to_bad_before_starting_empty()
    {
        using var dir = new TempDir();
        string path = dir.File("downloads.json");
        File.WriteAllText(path, "{ das ist kein JSON");

        var store = new DownloadStore(path, NoPause);
        var list = store.Load();

        Assert.Empty(list.Items);
        Assert.Equal(DataFileState.Corrupt, store.LoadState);
        Assert.True(DataFile.MaySave(store.LoadState));
        Assert.Equal("{ das ist kein JSON", File.ReadAllText(path + ".bad"));
    }

    [Fact]
    public void Corrupt_file_that_cannot_be_copied_counts_as_locked()
    {
        using var dir = new TempDir();
        string path = dir.File("downloads.json");
        File.WriteAllText(path, "kaputt");
        using var blocker = new FileStream(path + ".bad", FileMode.Create, FileAccess.Write, FileShare.None);

        var store = new DownloadStore(path, NoPause);
        store.Load();

        Assert.Equal(DataFileState.Locked, store.LoadState);
    }

    [Fact]
    public void Bom_file_from_a_text_editor_still_loads()
    {
        using var dir = new TempDir();
        string path = dir.File("downloads.json");
        var json = Encoding.UTF8.GetPreamble().Concat(DownloadStore.ToJson(OneItem())).ToArray();
        File.WriteAllBytes(path, json);

        var store = new DownloadStore(path, NoPause);

        Assert.Single(store.Load().Items);
        Assert.Equal(DataFileState.Ok, store.LoadState);
    }
}
