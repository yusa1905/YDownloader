using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;

namespace YDownloader.Engine;

/// <summary>Ergebnis beim Lesen einer Datendatei (<c>downloads.json</c>, <c>settings.json</c>).</summary>
public enum DataFileState
{
    /// <summary>Gelesen. Auch der Wert für „nie geladen“ (frisch angelegte Objekte): Speichern ist erlaubt.</summary>
    Ok,
    /// <summary>Die Datei gibt es nicht (erster Start): normal starten und speichern.</summary>
    Missing,
    /// <summary>Nicht lesbar (gesperrt, kein Zugriff), auch nach mehreren Versuchen: Datei nicht anfassen, nicht speichern.</summary>
    Locked,
    /// <summary>Inhalt kaputt. Die Datei liegt als <c>.bad</c> beiseite, mit Vorgaben starten und speichern ist erlaubt.</summary>
    Corrupt,
}

public static class DataFile
{
    public const int ReadAttempts = 5;
    public static readonly TimeSpan DefaultPause = TimeSpan.FromMilliseconds(250);

    /// <summary>Darf nach diesem Ergebnis geschrieben werden? Nein heißt: Die Datei hat noch Inhalt, den wir nicht kennen.</summary>
    public static bool MaySave(DataFileState state) => state != DataFileState.Locked;

    /// <summary>
    /// Liest und deutet eine JSON-Datei. Gesperrte Dateien (Virenscanner, Cloud-Sync, Backup) werden einige Male mit kurzer Pause
    /// neu gelesen. Kaputter Inhalt wird vorher als <c>.bad</c> kopiert; gelingt auch das nicht, gilt die Datei als gesperrt.
    /// Blockiert den aufrufenden Thread, deshalb nur im Hintergrund aufrufen.
    /// </summary>
    public static DataFileState Load<T>(string path, JsonTypeInfo<T> info, out T? value, TimeSpan? pause = null)
        where T : class
    {
        value = null;
        byte[]? data = null;
        for (int attempt = 1; attempt <= ReadAttempts; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return DataFileState.Missing;
                data = File.ReadAllBytes(path);
                break;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return DataFileState.Missing;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < ReadAttempts) Thread.Sleep(pause ?? DefaultPause);
            }
        }
        if (data is null) return DataFileState.Locked;

        try
        {
            int skip = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
            value = JsonSerializer.Deserialize(data.AsSpan(skip), info);
            if (value is not null) return DataFileState.Ok;
        }
        catch (JsonException)
        {
        }

        // Kaputt (oder „null“): erst beiseitelegen, dann darf mit Vorgaben begonnen werden.
        try
        {
            File.Copy(path, path + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DataFileState.Locked;
        }
        return DataFileState.Corrupt;
    }
}
