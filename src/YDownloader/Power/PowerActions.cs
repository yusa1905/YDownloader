using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace YDownloader.Power;

public enum PowerAction
{
    None,
    Shutdown,
    Hibernate,
    /// <summary>Energie sparen (Standby).</summary>
    Sleep,
}

/// <summary>
/// Löst die Windows-Aktion aus. Das echte <see cref="WindowsPowerActions"/> hängt nur <c>App</c> ein: Tests und
/// Werkzeuge bekommen vom <c>DownloadManager</c> die wirkungslose <see cref="NoPowerActions"/>.
/// </summary>
public interface IPowerActions
{
    /// <summary>Kann Windows diese Aktion gerade ausführen (etwa Ruhezustand nur, wenn er eingeschaltet ist)?</summary>
    bool IsAvailable(PowerAction action);

    /// <summary>Führt die Aktion aus. Liefert eine Fehlermeldung oder null.</summary>
    string? Execute(PowerAction action);
}

/// <summary>Tut nichts. Vorgabe, solange niemand die echten Aktionen einhängt.</summary>
public sealed class NoPowerActions : IPowerActions
{
    public static readonly NoPowerActions Instance = new();

    public bool IsAvailable(PowerAction action) => action != PowerAction.None;

    public string? Execute(PowerAction action) => null;
}

public static class PowerText
{
    /// <summary>Kurzname für Auswahl und Statusleiste.</summary>
    public static string Name(PowerAction action) => action switch
    {
        PowerAction.Shutdown => Loc.T("Power.Shutdown"),
        PowerAction.Hibernate => Loc.T("Power.Hibernate"),
        PowerAction.Sleep => Loc.T("Power.Sleep"),
        _ => Loc.T("Power.Nothing"),
    };

    /// <summary>Satz für den Countdown.</summary>
    public static string Sentence(PowerAction action) => action switch
    {
        PowerAction.Shutdown => Loc.T("Power.Sentence.Shutdown"),
        PowerAction.Hibernate => Loc.T("Power.Sentence.Hibernate"),
        PowerAction.Sleep => Loc.T("Power.Sentence.Sleep"),
        _ => "",
    };
}

/// <summary>
/// Die echten Aktionen. Herunterfahren und Ruhezustand über <c>shutdown.exe</c> (ohne <c>/f</c>: Programme mit ungespeicherten
/// Daten dürfen Einspruch erheben), Energie sparen über <c>SetSuspendState</c>. Wird nie in Tests benutzt.
/// </summary>
internal sealed class WindowsPowerActions : IPowerActions
{
    public bool IsAvailable(PowerAction action) => action switch
    {
        PowerAction.Shutdown => true,
        PowerAction.Hibernate => IsPwrHibernateAllowed() != 0,
        PowerAction.Sleep => IsPwrSuspendAllowed() != 0,
        _ => false,
    };

    public string? Execute(PowerAction action)
    {
        try
        {
            switch (action)
            {
                case PowerAction.Shutdown:
                    return RunShutdown("/s /t 0");
                case PowerAction.Hibernate:
                    return RunShutdown("/h");
                case PowerAction.Sleep:
                    // Kehrt erst nach dem Aufwachen zurück: nicht auf dem UI-Thread warten.
                    Task.Run(() => SetSuspendState(false, false, false));
                    return null;
                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return ex.Message;
        }
    }

    private static string? RunShutdown(string arguments)
    {
        string exe = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
        using var process = Process.Start(new ProcessStartInfo(exe, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        if (process is null) return Loc.T("Power.StartFailed");
        if (!process.WaitForExit(10_000)) return null; // läuft noch, also im Gange
        return process.ExitCode == 0 ? null : Loc.T("Power.Refused", process.ExitCode);
    }

    [DllImport("powrprof.dll")]
    private static extern byte IsPwrHibernateAllowed();

    [DllImport("powrprof.dll")]
    private static extern byte IsPwrSuspendAllowed();

    [DllImport("powrprof.dll")]
    private static extern byte SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);
}
