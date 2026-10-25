using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace YDownloader;

/// <summary>Autostart über den Run-Schlüssel des Benutzers. Der Eintrag startet die App ohne Fenster, nur im Tray.</summary>
public static class Autostart
{
    public const string Argument = "--autostart";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "YDownloader";

    public static bool IsEnabled()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string) return false;
            // Wer den Eintrag im Task-Manager oder in den Windows-Einstellungen abschaltet, setzt hier ein ungerades erstes Byte.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Schreibt den Eintrag mit dem aktuellen Pfad (korrigiert auch einen verschobenen Programmordner) oder löscht ihn.</summary>
    public static void Set(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (!enabled)
        {
            run.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }
        string path = Environment.ProcessPath ?? throw new IOException(Loc.T("Autostart.PathUnknown"));
        run.SetValue(ValueName, $"\"{path}\" {Argument}");
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
