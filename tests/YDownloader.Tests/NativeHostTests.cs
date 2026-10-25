using System;
using System.IO;
using System.Text;
using System.Text.Json;
using YDownloader.Browser;
using Xunit;

namespace YDownloader.Tests;

public class NativeHostTests
{
    private sealed class FakeLink : IAppLink
    {
        public bool Running;
        public int LaunchCount;
        public int SendsUntilReady; // Sendeversuche nach dem Start, bevor die App antwortet
        public string? Received;

        public bool TrySend(string text)
        {
            if (!Running && LaunchCount > 0 && --SendsUntilReady <= 0) Running = true;
            if (!Running) return false;
            if (text.Length > 0) Received = text;
            return true;
        }

        public bool LaunchFails;

        public bool Launch()
        {
            LaunchCount++;
            return !LaunchFails;
        }
    }

    [Fact]
    public void Add_LaunchFails_AnswersUnreachableAtOnce()
    {
        var link = new FakeLink { LaunchFails = true };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var replies = RunHost(link, TimeSpan.FromSeconds(10), out int exit,
            Frame("{\"type\":\"add\",\"text\":\"https://example.com/d.zip\"}"));

        Assert.Equal(0, exit);
        Assert.Equal("app-unreachable", replies[0].GetProperty("error").GetString());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), "ohne Start nicht bis zum Zeitlimit warten");
    }

    private static byte[] Frame(string json)
    {
        using var ms = new MemoryStream();
        NativeMessage.Write(ms, Encoding.UTF8.GetBytes(json));
        return ms.ToArray();
    }

    private static JsonElement[] RunHost(FakeLink link, TimeSpan timeout, out int exit, params byte[][] frames)
    {
        var input = new MemoryStream();
        foreach (var f in frames) input.Write(f);
        input.Position = 0;
        var output = new MemoryStream();
        exit = NativeHost.Run(input, output, link, timeout);
        output.Position = 0;
        var list = new System.Collections.Generic.List<JsonElement>();
        while (NativeMessage.Read(output) is { } body) list.Add(JsonDocument.Parse(body).RootElement.Clone());
        return list.ToArray();
    }

    [Fact]
    public void Frame_RoundTrip_AndUtf8()
    {
        using var ms = new MemoryStream();
        NativeMessage.Write(ms, Encoding.UTF8.GetBytes("{\"a\":\"Grüße\"}"));
        Assert.Equal(4 + Encoding.UTF8.GetByteCount("{\"a\":\"Grüße\"}"), ms.Length);
        ms.Position = 0;
        Assert.Equal("{\"a\":\"Grüße\"}", Encoding.UTF8.GetString(NativeMessage.Read(ms)!));
        Assert.Null(NativeMessage.Read(ms)); // Ende der Leitung
    }

    [Fact]
    public void Frame_HeaderIsLittleEndian()
    {
        using var ms = new MemoryStream();
        NativeMessage.Write(ms, new byte[258]);
        var bytes = ms.ToArray();
        Assert.Equal(new byte[] { 2, 1, 0, 0 }, bytes[..4]);
    }

    [Fact]
    public void Frame_TooLarge_Throws()
    {
        var ms = new MemoryStream(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F });
        Assert.Throws<InvalidDataException>(() => NativeMessage.Read(ms));
    }

    [Fact]
    public void Frame_Truncated_Throws()
    {
        Assert.Throws<InvalidDataException>(() => NativeMessage.Read(new MemoryStream(new byte[] { 1, 0 })));
        Assert.Throws<InvalidDataException>(() => NativeMessage.Read(new MemoryStream(new byte[] { 5, 0, 0, 0, 1, 2 })));
    }

    [Fact]
    public void Add_RunningApp_DeliversText()
    {
        var link = new FakeLink { Running = true };
        var replies = RunHost(link, TimeSpan.FromSeconds(1), out int exit,
            Frame("{\"type\":\"add\",\"text\":\"https://example.com/a.zip\"}"));
        Assert.Equal(0, exit);
        Assert.True(replies[0].GetProperty("ok").GetBoolean());
        Assert.Equal("https://example.com/a.zip", link.Received);
        Assert.Equal(0, link.LaunchCount);
    }

    [Fact]
    public void Add_AppNotRunning_LaunchesOnceAndWaits()
    {
        var link = new FakeLink { SendsUntilReady = 3 };
        var replies = RunHost(link, TimeSpan.FromSeconds(5), out _,
            Frame("{\"type\":\"add\",\"text\":\"x https://example.com/b.zip\"}"));
        Assert.True(replies[0].GetProperty("ok").GetBoolean());
        Assert.Equal(1, link.LaunchCount);
        Assert.Equal("x https://example.com/b.zip", link.Received);
    }

    [Fact]
    public void Add_AppNeverAnswers_FailsAfterTimeout()
    {
        var link = new FakeLink { SendsUntilReady = int.MaxValue };
        var replies = RunHost(link, TimeSpan.FromMilliseconds(250), out _,
            Frame("{\"type\":\"add\",\"text\":\"https://example.com/c\"}"));
        Assert.False(replies[0].GetProperty("ok").GetBoolean());
        Assert.Equal("app-unreachable", replies[0].GetProperty("error").GetString());
        Assert.Equal(1, link.LaunchCount);
    }

    [Fact]
    public void Add_WithoutLink_IsRejected_AndDoesNotLaunch()
    {
        var link = new FakeLink();
        var replies = RunHost(link, TimeSpan.FromSeconds(1), out _,
            Frame("{\"type\":\"add\",\"text\":\"kein link hier\"}"),
            Frame("{\"type\":\"add\",\"text\":\"ftp://example.com/x\"}"),
            Frame("{\"type\":\"add\"}"));
        Assert.All(replies, r => Assert.Equal("no-link", r.GetProperty("error").GetString()));
        Assert.Equal(3, replies.Length);
        Assert.Equal(0, link.LaunchCount);
    }

    [Fact]
    public void Ping_ReportsRunning_WithoutLaunching()
    {
        var link = new FakeLink();
        var replies = RunHost(link, TimeSpan.FromSeconds(1), out _, Frame("{\"type\":\"ping\"}"));
        Assert.True(replies[0].GetProperty("ok").GetBoolean());
        Assert.False(replies[0].GetProperty("running").GetBoolean());
        Assert.False(string.IsNullOrEmpty(replies[0].GetProperty("version").GetString()));
        Assert.Equal(0, link.LaunchCount);

        link.Running = true;
        replies = RunHost(link, TimeSpan.FromSeconds(1), out _, Frame("{\"type\":\"ping\"}"));
        Assert.True(replies[0].GetProperty("running").GetBoolean());
        Assert.Null(link.Received); // Ping übergibt nichts
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"type\":\"format-c\"}")]
    [InlineData("[]")]
    [InlineData("null")]
    public void BadRequest_AnswersAndContinues(string json)
    {
        var link = new FakeLink { Running = true };
        var replies = RunHost(link, TimeSpan.FromSeconds(1), out int exit, Frame(json), Frame("{\"type\":\"ping\"}"));
        Assert.Equal(0, exit);
        Assert.Equal("bad-request", replies[0].GetProperty("error").GetString());
        Assert.True(replies[1].GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void BrokenStream_EndsWithError()
    {
        var link = new FakeLink { Running = true };
        var replies = RunHost(link, TimeSpan.FromSeconds(1), out int exit, new byte[] { 9, 0, 0, 0, 1 });
        Assert.Equal(1, exit);
        Assert.Empty(replies);
    }

    [Theory]
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop/", true)]
    [InlineData("--native-host", true)]
    [InlineData("--autostart", false)]
    public void IsHostLaunch_SingleArgument(string arg, bool expected) =>
        Assert.Equal(expected, NativeHost.IsHostLaunch(new[] { arg }));

    [Fact]
    public void IsHostLaunch_Firefox_ManifestPathAndId()
    {
        Assert.True(NativeHost.IsHostLaunch(new[] { @"C:\x\" + NativeHost.FirefoxManifestFile, "ydownloader@local" }));
        Assert.False(NativeHost.IsHostLaunch(new[] { @"C:\x\andere.json", "ydownloader@local" }));
        Assert.False(NativeHost.IsHostLaunch(Array.Empty<string>()));
        Assert.False(NativeHost.IsHostLaunch(new[] { @"C:\x\" + NativeHost.FirefoxManifestFile }));
    }
}
