using System;
using System.IO;

namespace YDownloader.Engine;

/// <summary>Schreibt Dateien atomar: erst in eine temporäre Datei, dann ersetzen. Übersteht auch einen Stromausfall.</summary>
public static class AtomicFile
{
    public static void WriteAllBytes(string path, byte[] data)
    {
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 0))
        {
            fs.Write(data);
            fs.Flush(flushToDisk: true); // sonst kann nach Stromausfall eine leere Datei unter dem neuen Namen liegen
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Liest eine JSON-Datei ohne UTF-8-BOM (von Hand bearbeitete Dateien haben oft eins, der Leser verträgt es nicht).</summary>
    public static ReadOnlySpan<byte> ReadJson(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        return data.AsSpan(data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0);
    }
}
