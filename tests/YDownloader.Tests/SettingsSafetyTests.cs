using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using YDownloader.Engine;
using Xunit;

namespace YDownloader.Tests;

/// <summary>
/// <c>settings.json</c>: gesperrte oder kaputte Dateien und die Schreibkette (der neueste Stand gewinnt). Nur Dateien in
/// eigenen Temp-Ordnern, nie die echten Daten.
/// </summary>
public class SettingsSafetyTests
{
    private static readonly TimeSpan NoPause = TimeSpan.FromMilliseconds(1);

    [Fact]
    public void Settings_without_file_start_normally()
    {
        using var dir = new TempDir();

        var s = AppSettings.Load(dir.File("settings.json"), NoPause);

        Assert.Equal(DataFileState.Missing, s.LoadState);
        Assert.True(DataFile.MaySave(s.LoadState));
    }

    [Fact]
    public void Locked_settings_use_defaults_and_leave_the_file_alone()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        var saved = new AppSettings { MaxConcurrent = 7 };
        AppSettings.Write(path, saved.ToJson());
        byte[] before = File.ReadAllBytes(path);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var s = AppSettings.Load(path, NoPause);

            Assert.Equal(DataFileState.Locked, s.LoadState);
            Assert.False(DataFile.MaySave(s.LoadState));
            Assert.NotEqual(7, s.MaxConcurrent);
        }

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".bad"));
    }

    [Fact]
    public void Corrupt_settings_are_copied_to_bad()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        File.WriteAllText(path, "{ \"maxConcurrent\": ");

        var s = AppSettings.Load(path, NoPause);

        Assert.Equal(DataFileState.Corrupt, s.LoadState);
        Assert.True(DataFile.MaySave(s.LoadState));
        Assert.Equal("{ \"maxConcurrent\": ", File.ReadAllText(path + ".bad"));
    }

    [Fact]
    public void Good_settings_load_as_ok()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        AppSettings.Write(path, new AppSettings { MaxConcurrent = 5 }.ToJson());

        var s = AppSettings.Load(path, NoPause);

        Assert.Equal(DataFileState.Ok, s.LoadState);
        Assert.Equal(5, s.MaxConcurrent);
    }

    [Fact]
    public async Task Writer_keeps_the_newest_state_when_saves_pile_up()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        var writer = new LatestWriter(() => path);

        Task<Exception?> last = Task.FromResult<Exception?>(null);
        for (int i = 1; i <= 200; i++) last = writer.Submit(Encoding.UTF8.GetBytes("stand " + i));
        Assert.Null(await last);
        writer.Flush();

        Assert.Equal("stand 200", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task Writer_waiters_of_skipped_states_get_the_result_of_a_newer_write()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        var writer = new LatestWriter(() => path);

        var tasks = Enumerable.Range(1, 50).Select(i => writer.Submit(Encoding.UTF8.GetBytes("stand " + i))).ToList();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, Assert.Null);
        Assert.Equal("stand 50", File.ReadAllText(path));
    }

    [Fact]
    public async Task Writer_reports_a_failure_and_recovers_with_the_next_save()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        File.WriteAllText(path, "alt");
        var writer = new LatestWriter(() => path);
        Exception? seen = null;
        writer.Completed += e => seen ??= e;

        using (new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None)) // .tmp gesperrt
        {
            var error = await writer.Submit(Encoding.UTF8.GetBytes("neu"));
            Assert.IsAssignableFrom<IOException>(error);
        }
        Assert.NotNull(seen);
        Assert.Equal("alt", File.ReadAllText(path));

        Assert.Null(await writer.Submit(Encoding.UTF8.GetBytes("neu")));
        Assert.Equal("neu", File.ReadAllText(path));
    }

    [Fact]
    public async Task Writer_handles_parallel_submitters()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        var writer = new LatestWriter(() => path);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 50; i++) writer.Submit(Encoding.UTF8.GetBytes($"t{t}-{i}"));
        })));
        var final = await writer.Submit(Encoding.UTF8.GetBytes("ende"));
        writer.Flush();

        Assert.Null(final);
        Assert.Equal("ende", File.ReadAllText(path));
    }
}
