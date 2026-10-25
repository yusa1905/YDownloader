using System;
using System.Globalization;

namespace YDownloader;

/// <summary>Größen, Raten und Zeiten für die Anzeige. Einheiten wie im Explorer (1 KB = 1024 Bytes).</summary>
public static class Format
{
    // Zahlenformat, Wörter und Datumsmuster stehen in der Sprachdatei (Schlüssel Format.*): Die App läuft im Invariant-Modus.
    private static readonly string[] Units = { "KB", "MB", "GB", "TB" };

    /// <summary>Drei Stellen: "812 KB", "45,6 MB", "1,23 GB" (Dezimaltrenner je Sprache). Negativ = unbekannt.</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0) return "?";
        if (bytes < 1000) return Loc.Plural("Format.Bytes", bytes);
        double v = bytes / 1024.0;
        int unit = 0;
        while (v >= 1000 && unit < Units.Length - 1)
        {
            v /= 1024;
            unit++;
        }
        string pattern = v < 10 ? "0.00" : v < 100 ? "0.0" : "0";
        return v.ToString(pattern, Loc.Number) + " " + Units[unit];
    }

    /// <summary>Genaue Zahl mit Tausendertrenner: "13.107.200 Bytes".</summary>
    public static string Exact(long bytes) => Loc.Plural("Format.BytesExact", bytes);

    public static string Speed(double bytesPerSecond) => Loc.T("Format.Speed", Bytes((long)bytesPerSecond));

    /// <summary>Abgerundet, damit vor dem Ende nie "100 %" steht.</summary>
    public static string Percent(double fraction) =>
        Loc.T("Format.Percent", Math.Floor(Math.Clamp(fraction, 0, 1) * 100));

    /// <summary>"42 s", "3:12 min", "1:05 h", "2 T 4 h" (Einheiten je Sprache). Leer, wenn unbekannt.</summary>
    public static string Duration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "";
        long s = (long)Math.Ceiling(seconds);
        if (s < 60) return Loc.T("Format.Seconds", s);
        if (s < 3600) return Loc.T("Format.Minutes", s / 60, s % 60);
        if (s < 86400) return Loc.T("Format.Hours", s / 3600, s % 3600 / 60);
        return Loc.T("Format.Days", s / 86400, s % 86400 / 3600);
    }

    /// <summary>"heute 14:32", "gestern 09:10", sonst "16.10.26 14:32" (Wörter und Datumsmuster je Sprache). Mit <paramref name="seconds"/> "heute 14:32:07".</summary>
    public static string When(DateTime local, bool seconds = false)
    {
        var today = DateTime.Today;
        string time = local.ToString(seconds ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today) return Loc.T("Format.Today", time);
        if (local.Date == today.AddDays(-1)) return Loc.T("Format.Yesterday", time);
        return local.ToString(Loc.T("Format.DatePattern"), CultureInfo.InvariantCulture) + " " + time;
    }
}
