using System;
using System.Diagnostics;
using System.IO;

namespace YDownloader.Browser;

/// <summary>Echte Verbindung: Text per WM_COPYDATA an das Tray-Fenster der laufenden Instanz, Start über die eigene exe.</summary>
internal sealed class WindowsAppLink : IAppLink
{
    public bool TrySend(string text) => TrayIcon.SendLinks(text);

    public bool Launch()
    {
        string? path = Environment.ProcessPath;
        if (path is null) return false;
        try
        {
            // Über die Shell, damit die App nicht die Datenströme des Browsers erbt (der Browser wartet auf deren Ende).
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false; // die Erweiterung bekommt „app-unreachable“ statt eines abgestürzten Hosts
        }
    }
}
