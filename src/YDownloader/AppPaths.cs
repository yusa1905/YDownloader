using System;
using System.IO;
using System.Runtime.InteropServices;

namespace YDownloader;

/// <summary>Speicherorte. Liegt eine Datei <c>.portable</c> neben der exe, bleiben die Daten im Programmordner.</summary>
public static class AppPaths
{
    private static readonly Guid FolderDownloads = new("374DE290-123F-4565-9164-39C4925E467B");
    private static string? _dataDir;

    private static string MarkerPath => Path.Combine(AppContext.BaseDirectory, ".portable");

    public static bool IsPortable => File.Exists(MarkerPath);

    public static string DataDir => _dataDir ??= CreateDataDir();

    /// <summary>
    /// Schaltet den Portable-Modus um: Marker anlegen oder löschen, ab dann gilt der andere Datenordner. Die Dateien dorthin
    /// zu schreiben ist Sache des Aufrufers. Wirft, wenn der Programmordner nicht beschreibbar ist.
    /// </summary>
    public static void SetPortable(bool portable)
    {
        if (portable) File.WriteAllBytes(MarkerPath, Array.Empty<byte>());
        else File.Delete(MarkerPath);
        _dataDir = null;
    }

    public static string DownloadsFile => Path.Combine(DataDir, "downloads.json");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    private static string CreateDataDir()
    {
        string dir = IsPortable
            ? Path.Combine(AppContext.BaseDirectory, "Data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YDownloader");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Downloads-Ordner des Benutzers, auch wenn er verschoben wurde.</summary>
    public static string UserDownloads()
    {
        IntPtr path = IntPtr.Zero;
        try
        {
            if (SHGetKnownFolderPath(FolderDownloads, 0, IntPtr.Zero, out path) == 0)
                return Marshal.PtrToStringUni(path)!;
        }
        finally
        {
            Marshal.FreeCoTaskMem(path); // laut Doku auch bei Fehlschlag
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags,
        IntPtr token, out IntPtr path);
}
