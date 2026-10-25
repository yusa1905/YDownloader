using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YDownloader.Browser;
using Xunit;

namespace YDownloader.Tests;

/// <summary>Die Registrierung läuft hier nur gegen einen Speicher im Arbeitsspeicher, nie gegen die echte Registry.</summary>
public sealed class BrowserSetupTests : IDisposable
{
    private sealed class MemoryRegistry : IRegistryStore
    {
        public readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);
        public string? GetDefault(string subKey) => Values.GetValueOrDefault(subKey);
        public void SetDefault(string subKey, string value) => Values[subKey] = value;
        public void Delete(string subKey) => Values.Remove(subKey);
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ydl-browser-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryRegistry _registry = new();
    private const string Exe = @"C:\Apps\YDownloader\YDownloader.exe";

    private BrowserSetup Setup(string exe = Exe) => new(_registry, Path.Combine(_dir, "NativeMessaging"), exe);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Register_WritesManifestAndRegistryEntry()
    {
        var setup = Setup();
        Assert.Equal(BrowserState.NotRegistered, setup.GetState(BrowserKind.Chrome));

        setup.Register(BrowserKind.Chrome);

        Assert.Equal(BrowserState.Registered, setup.GetState(BrowserKind.Chrome));
        Assert.Equal(BrowserState.NotRegistered, setup.GetState(BrowserKind.Firefox)); // je Browser getrennt
        string path = setup.ManifestPath(BrowserKind.Chrome);
        Assert.Equal(path, _registry.Values[@"Software\Google\Chrome\NativeMessagingHosts\ydownloader.host"]);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void ChromeManifest_AllowsOnlyOurExtension()
    {
        var json = JsonDocument.Parse(Setup().ManifestJson(BrowserKind.Chrome)).RootElement;
        Assert.Equal("ydownloader.host", json.GetProperty("name").GetString());
        Assert.Equal("stdio", json.GetProperty("type").GetString());
        Assert.Equal(Exe, json.GetProperty("path").GetString());
        var origins = json.GetProperty("allowed_origins").EnumerateArray();
        Assert.Equal(new[] { $"chrome-extension://{BrowserSetup.ChromeExtensionId}/" }, origins.Select(o => o.GetString()!));
        Assert.False(json.TryGetProperty("allowed_extensions", out _));
    }

    [Fact]
    public void FirefoxManifest_AllowsOnlyOurExtension()
    {
        var json = JsonDocument.Parse(Setup().ManifestJson(BrowserKind.Firefox)).RootElement;
        Assert.Equal(new[] { BrowserSetup.FirefoxExtensionId },
            json.GetProperty("allowed_extensions").EnumerateArray().Select(o => o.GetString()!));
        Assert.False(json.TryGetProperty("allowed_origins", out _));
        Assert.Equal(@"Software\Mozilla\NativeMessagingHosts\ydownloader.host", BrowserSetup.RegistryKey(BrowserKind.Firefox));
    }

    [Fact]
    public void ManifestFile_HasNoBom()
    {
        var setup = Setup();
        setup.Register(BrowserKind.Firefox);
        byte[] bytes = File.ReadAllBytes(setup.ManifestPath(BrowserKind.Firefox));
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal((byte)'{', bytes[0]);
    }

    [Fact]
    public void MovedProgramFolder_IsOutdated_AndRegisterFixesIt()
    {
        Setup().Register(BrowserKind.Chrome);
        var moved = Setup(@"D:\Neu\YDownloader.exe");
        Assert.Equal(BrowserState.Outdated, moved.GetState(BrowserKind.Chrome));

        moved.Register(BrowserKind.Chrome);
        Assert.Equal(BrowserState.Registered, moved.GetState(BrowserKind.Chrome));
    }

    [Fact]
    public void MissingManifestFile_IsOutdated()
    {
        var setup = Setup();
        setup.Register(BrowserKind.Chrome);
        File.Delete(setup.ManifestPath(BrowserKind.Chrome));
        Assert.Equal(BrowserState.Outdated, setup.GetState(BrowserKind.Chrome));
    }

    [Fact]
    public void ForeignEntry_IsOutdated_NotOverwrittenByGetState()
    {
        _registry.SetDefault(BrowserSetup.RegistryKey(BrowserKind.Chrome), @"C:\Anderes\host.json");
        Assert.Equal(BrowserState.Outdated, Setup().GetState(BrowserKind.Chrome));
        Assert.Equal(@"C:\Anderes\host.json", _registry.Values[BrowserSetup.RegistryKey(BrowserKind.Chrome)]);
    }

    [Fact]
    public void Unregister_RemovesEntryAndFile_AndIsRepeatable()
    {
        var setup = Setup();
        setup.Register(BrowserKind.Chrome);
        setup.Register(BrowserKind.Firefox);

        setup.Unregister(BrowserKind.Chrome);
        Assert.Equal(BrowserState.NotRegistered, setup.GetState(BrowserKind.Chrome));
        Assert.False(File.Exists(setup.ManifestPath(BrowserKind.Chrome)));
        Assert.Equal(BrowserState.Registered, setup.GetState(BrowserKind.Firefox));

        setup.Unregister(BrowserKind.Chrome); // zweites Mal darf nicht werfen
    }

    // ---------------------------------------------------------------- Erweiterung im Repo

    private static string ExtensionDir([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "extension"));

    private static JsonElement ExtensionManifest() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(ExtensionDir(), "manifest.json"))).RootElement;

    [Fact]
    public void ExtensionKey_YieldsTheFixedChromeId()
    {
        byte[] der = Convert.FromBase64String(ExtensionManifest().GetProperty("key").GetString()!);
        byte[] hash = SHA256.HashData(der);
        var id = new StringBuilder();
        for (int i = 0; i < 16; i++)
        {
            id.Append((char)('a' + (hash[i] >> 4)));
            id.Append((char)('a' + (hash[i] & 15)));
        }
        Assert.Equal(BrowserSetup.ChromeExtensionId, id.ToString());
    }

    private static JsonElement FirefoxManifest() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(ExtensionDir(), "manifest.firefox.json"))).RootElement;

    [Fact]
    public void ChromeManifest_UsesOnlyServiceWorker()
    {
        var bg = ExtensionManifest().GetProperty("background");
        Assert.True(bg.TryGetProperty("service_worker", out _));
        Assert.False(bg.TryGetProperty("scripts", out _));
        Assert.False(ExtensionManifest().TryGetProperty("browser_specific_settings", out _));
    }

    [Fact]
    public void FirefoxManifest_UsesOnlyScripts_AndHasNoKey()
    {
        var m = FirefoxManifest();
        var bg = m.GetProperty("background");
        Assert.False(bg.TryGetProperty("service_worker", out _));
        Assert.Equal("background.js", bg.GetProperty("scripts")[0].GetString());
        Assert.False(m.TryGetProperty("key", out _));
        Assert.Equal(BrowserSetup.FirefoxExtensionId,
            m.GetProperty("browser_specific_settings").GetProperty("gecko").GetProperty("id").GetString());
        foreach (string name in new[] { "manifest_version", "version", "name", "description", "default_locale", "icons", "permissions", "action" })
            Assert.Equal(ExtensionManifest().GetProperty(name).ToString(), m.GetProperty(name).ToString());
    }

    [Fact]
    public void ExtensionManifest_MatchesHostAndStaysMinimal()
    {
        var m = ExtensionManifest();
        Assert.Equal(3, m.GetProperty("manifest_version").GetInt32());
        // Nur das Kontextmenü und die Verbindung zum Host. Keine Seitenrechte, keine Inhaltsskripte.
        Assert.Equal(new[] { "contextMenus", "nativeMessaging" },
            m.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).OrderBy(p => p));
        Assert.False(m.TryGetProperty("host_permissions", out _));
        Assert.False(m.TryGetProperty("content_scripts", out _));
        Assert.False(m.TryGetProperty("externally_connectable", out _));
        Assert.Contains($"\"{NativeHost.Name}\"", File.ReadAllText(Path.Combine(ExtensionDir(), "background.js")));
    }

    [Fact]
    public void ExtensionFiles_ReferencedByManifestExist()
    {
        var m = ExtensionManifest();
        var files = new List<string> { m.GetProperty("background").GetProperty("service_worker").GetString()! };
        foreach (var icon in m.GetProperty("icons").EnumerateObject()) files.Add(icon.Value.GetString()!);
        foreach (string f in files) Assert.True(File.Exists(Path.Combine(ExtensionDir(), f)), f);
    }

    [Fact]
    public void ExtensionLocales_HaveSameKeys()
    {
        static string[] Keys(string lang) => JsonDocument
            .Parse(File.ReadAllText(Path.Combine(ExtensionDir(), "_locales", lang, "messages.json")))
            .RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(Keys("en"), Keys("de"));
    }
}
