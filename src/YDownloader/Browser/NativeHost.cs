using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using YDownloader.Downloads;

namespace YDownloader.Browser;

/// <summary>Verbindung zur laufenden App. Hinter einer Schnittstelle, damit der Host ohne Fenster und Prozess prüfbar bleibt.</summary>
public interface IAppLink
{
    /// <summary>Gibt Text mit Links an die laufende App. <c>false</c>, wenn keine läuft oder sie nicht antwortet.</summary>
    bool TrySend(string text);

    /// <summary>Startet die App (mit Fenster). <c>false</c>, wenn der Start gar nicht erst geht.</summary>
    bool Launch();
}

/// <summary>Anfrage der Erweiterung. <c>type</c> ist <c>ping</c> oder <c>add</c>.</summary>
internal sealed class HostRequest
{
    public string? Type { get; set; }
    public string? Text { get; set; }
}

/// <summary>Antwort an die Erweiterung. Fehler sind feste Kennungen, die Erweiterung übersetzt sie.</summary>
internal sealed class HostResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Version { get; set; }
    public bool? Running { get; set; }
}

[JsonSerializable(typeof(HostRequest))]
[JsonSerializable(typeof(HostResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class HostJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Native-Messaging-Host: die exe selbst, vom Browser gestartet. Nimmt Links von der Erweiterung entgegen und gibt sie an die
/// laufende App weiter; läuft keine, wird sie gestartet. Die App zeigt die Links im Hinzufügen-Sheet, geladen wird erst nach
/// Bestätigung.
/// </summary>
public static class NativeHost
{
    /// <summary>Name des Hosts, so wie ihn die Erweiterung und die Registrierung kennen.</summary>
    public const string Name = "ydownloader.host";

    public const string ChromeManifestFile = "ydownloader.host.chrome.json";
    public const string FirefoxManifestFile = "ydownloader.host.firefox.json";

    /// <summary>Zum Testen von Hand: erzwingt den Host-Modus.</summary>
    public const string Argument = "--native-host";

    public static readonly TimeSpan DefaultLaunchTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Chrome übergibt den Ursprung (<c>chrome-extension://…/</c>) als erstes Argument, Firefox Pfad der Manifest-Datei und
    /// Erweiterungs-ID. Der normale Start der App kennt beides nicht.
    /// </summary>
    public static bool IsHostLaunch(string[] args)
    {
        foreach (string a in args)
            if (a == Argument || a.StartsWith("chrome-extension://", StringComparison.Ordinal)) return true;
        if (args.Length < 2) return false;
        string file = Path.GetFileName(args[0]);
        return file.Equals(FirefoxManifestFile, StringComparison.OrdinalIgnoreCase)
            || file.Equals(ChromeManifestFile, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Host-Schleife auf den Standard-Datenströmen mit der echten App-Verbindung.</summary>
    public static int RunStandard()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        return Run(input, output, new WindowsAppLink(), DefaultLaunchTimeout);
    }

    /// <summary>Beantwortet Nachrichten, bis der Browser die Leitung schließt. 0 = sauberes Ende, 1 = kaputtes Protokoll.</summary>
    public static int Run(Stream input, Stream output, IAppLink link, TimeSpan launchTimeout)
    {
        while (true)
        {
            byte[]? frame;
            try
            {
                frame = NativeMessage.Read(input);
            }
            catch (InvalidDataException)
            {
                return 1;
            }
            if (frame is null) return 0;
            NativeMessage.Write(output, JsonSerializer.SerializeToUtf8Bytes(Handle(frame, link, launchTimeout),
                HostJsonContext.Default.HostResponse));
        }
    }

    private static HostResponse Handle(byte[] frame, IAppLink link, TimeSpan launchTimeout)
    {
        HostRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(frame, HostJsonContext.Default.HostRequest);
        }
        catch (JsonException)
        {
            request = null;
        }

        switch (request?.Type)
        {
            case "ping":
                // Pong nur mit der Programmversion: Die Erweiterung zeigt damit, dass die Kette bis hierher steht.
                return new HostResponse { Ok = true, Version = AppVersion(), Running = link.TrySend("") };
            case "add":
                if (!LinkParser.ContainsLink(request.Text)) return Fail("no-link");
                return Deliver(request.Text!, link, launchTimeout) ? new HostResponse { Ok = true } : Fail("app-unreachable");
            default:
                return Fail("bad-request");
        }
    }

    private static HostResponse Fail(string code) => new() { Ok = false, Error = code };

    /// <summary>Gibt die Links an die App. Läuft sie nicht, wird sie einmal gestartet und bis zum Zeitlimit abgewartet.</summary>
    private static bool Deliver(string text, IAppLink link, TimeSpan timeout)
    {
        if (link.TrySend(text)) return true;
        if (!link.Launch()) return false;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            Thread.Sleep(100);
            if (link.TrySend(text)) return true;
        }
        return false;
    }

    private static string AppVersion() =>
        typeof(NativeHost).Assembly.GetName().Version?.ToString(3) ?? "0";
}
