using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using YDownloader.Engine;

namespace YDownloader.Browser;

public enum BrowserKind { Chrome, Firefox }

public enum BrowserState
{
    /// <summary>Kein Eintrag.</summary>
    NotRegistered,
    /// <summary>Eintrag und Manifest stimmen mit dieser exe überein.</summary>
    Registered,
    /// <summary>Ein Eintrag besteht, passt aber nicht (Programmordner verschoben, Manifest fehlt oder fremd).</summary>
    Outdated,
}

/// <summary>Ort für die Registrierung, in der App der Benutzerbereich der Registry. Austauschbar, damit Tests sie nie anfassen.</summary>
public interface IRegistryStore
{
    string? GetDefault(string subKey);
    void SetDefault(string subKey, string value);
    void Delete(string subKey);
}

/// <summary>Registry unter HKEY_CURRENT_USER, keine Adminrechte nötig.</summary>
internal sealed class CurrentUserRegistry : IRegistryStore
{
    public string? GetDefault(string subKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey);
        return key?.GetValue("") as string;
    }

    public void SetDefault(string subKey, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey);
        key.SetValue("", value);
    }

    public void Delete(string subKey) => Registry.CurrentUser.DeleteSubKey(subKey, throwOnMissingSubKey: false);
}

internal sealed class HostManifest
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "stdio";

    [JsonPropertyName("allowed_origins")]
    public string[]? AllowedOrigins { get; set; }

    [JsonPropertyName("allowed_extensions")]
    public string[]? AllowedExtensions { get; set; }
}

[JsonSerializable(typeof(HostManifest))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class ManifestJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Verbindet Chrome und Firefox mit dieser exe: schreibt die Manifest-Dateien des Native-Messaging-Hosts in den Datenordner und
/// trägt sie im Benutzerbereich der Registry ein. Das geschieht nur auf Wunsch des Nutzers, nie von selbst.
/// </summary>
public sealed class BrowserSetup
{
    /// <summary>
    /// Feste ID der Chrome-Erweiterung. Sie folgt aus dem Schlüssel <c>key</c> in <c>extension/manifest.json</c> und bleibt so
    /// auf jedem Rechner und in jedem Ordner gleich (ein Test prüft die Übereinstimmung).
    /// </summary>
    public const string ChromeExtensionId = "hlhnihbldecdlahkbbfocoadboddocpl";

    /// <summary>Feste ID der Firefox-Erweiterung (<c>browser_specific_settings.gecko.id</c>).</summary>
    public const string FirefoxExtensionId = "ydownloader@local";

    private const string Description = "YDownloader: Links aus dem Browser an die App übergeben";

    private readonly IRegistryStore _registry;
    private readonly string _manifestDir;
    private readonly string _exePath;

    public BrowserSetup(IRegistryStore registry, string manifestDir, string exePath)
    {
        _registry = registry;
        _manifestDir = manifestDir;
        _exePath = exePath;
    }

    /// <summary>Mit der echten Registry, dem Datenordner der App und der laufenden exe.</summary>
    public static BrowserSetup ForThisApp() => new(new CurrentUserRegistry(),
        Path.Combine(AppPaths.DataDir, "NativeMessaging"),
        Environment.ProcessPath ?? throw new IOException(Loc.T("Autostart.PathUnknown")));

    public static string RegistryKey(BrowserKind kind) => kind switch
    {
        BrowserKind.Chrome => @"Software\Google\Chrome\NativeMessagingHosts\" + NativeHost.Name,
        _ => @"Software\Mozilla\NativeMessagingHosts\" + NativeHost.Name,
    };

    public string ManifestPath(BrowserKind kind) =>
        Path.Combine(_manifestDir, kind == BrowserKind.Chrome ? NativeHost.ChromeManifestFile : NativeHost.FirefoxManifestFile);

    /// <summary>Inhalt der Manifest-Datei. Nur die eigene Erweiterung darf den Host starten.</summary>
    public string ManifestJson(BrowserKind kind)
    {
        var manifest = new HostManifest { Name = NativeHost.Name, Description = Description, Path = _exePath };
        if (kind == BrowserKind.Chrome) manifest.AllowedOrigins = new[] { $"chrome-extension://{ChromeExtensionId}/" };
        else manifest.AllowedExtensions = new[] { FirefoxExtensionId };
        return JsonSerializer.Serialize(manifest, ManifestJsonContext.Default.HostManifest);
    }

    public BrowserState GetState(BrowserKind kind)
    {
        string? registered;
        try
        {
            registered = _registry.GetDefault(RegistryKey(kind));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return BrowserState.NotRegistered;
        }
        if (registered is null) return BrowserState.NotRegistered;

        string expected = ManifestPath(kind);
        if (!string.Equals(registered, expected, StringComparison.OrdinalIgnoreCase)) return BrowserState.Outdated;
        try
        {
            return File.Exists(expected) && File.ReadAllText(expected) == ManifestJson(kind)
                ? BrowserState.Registered : BrowserState.Outdated;
        }
        catch (IOException)
        {
            return BrowserState.Outdated;
        }
    }

    /// <summary>Schreibt das Manifest (atomar) und den Registry-Eintrag. Auch zum Korrigieren eines verschobenen Ordners.</summary>
    public void Register(BrowserKind kind)
    {
        Directory.CreateDirectory(_manifestDir);
        string path = ManifestPath(kind);
        // Ohne BOM: Firefox verträgt es nicht.
        AtomicFile.WriteAllBytes(path, new System.Text.UTF8Encoding(false).GetBytes(ManifestJson(kind)));
        _registry.SetDefault(RegistryKey(kind), path);
    }

    /// <summary>Entfernt Eintrag und Manifest-Datei.</summary>
    public void Unregister(BrowserKind kind)
    {
        _registry.Delete(RegistryKey(kind));
        File.Delete(ManifestPath(kind));
    }
}
