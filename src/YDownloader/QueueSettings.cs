using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace YDownloader;

/// <summary>
/// Eine zusätzliche Queue neben der Standard-Queue (die gibt es immer, ohne Zeitplan und ohne eigene Grenze).
/// Downloads gehören über <c>QueueId</c> zu einer Queue.
/// </summary>
public sealed class QueueSetting
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>Gleichzeitige Downloads dieser Queue. 0 = nur die allgemeine Grenze aus den Einstellungen.</summary>
    public int MaxConcurrent { get; set; }

    /// <summary>Null = läuft immer.</summary>
    public ScheduleSetting? Schedule { get; set; }
}

/// <summary>
/// Wann eine Queue laufen darf: ab <see cref="StartMinute"/> an den gewählten Wochentagen, bis <see cref="StopMinute"/>.
/// Liegt der Stopp vor dem Start, reicht das Fenster über Mitternacht (22:00 bis 06:00 gehört zum Starttag).
/// Ohne Stopp läuft es bis Mitternacht des Starttags.
/// </summary>
public sealed class ScheduleSetting
{
    public const int AllDays = 0b111_1111;
    public const int MinutesPerDay = 24 * 60;

    private static readonly string[] DayKeys = { "Day.Mon", "Day.Tue", "Day.Wed", "Day.Thu", "Day.Fri", "Day.Sat", "Day.Sun" };

    /// <summary>Kurzname des Wochentags in der Sprache der Oberfläche, 0 = Montag.</summary>
    private static string DayName(int index) => Loc.T(DayKeys[index]);

    /// <summary>Minuten seit Mitternacht (0 bis 1439).</summary>
    public int StartMinute { get; set; } = 2 * 60;

    /// <summary>Minuten seit Mitternacht, null = bis Mitternacht.</summary>
    public int? StopMinute { get; set; }

    /// <summary>Bit 0 = Montag bis Bit 6 = Sonntag.</summary>
    public int Days { get; set; } = AllDays;

    public bool HasDay(DayOfWeek day) => (Days & DayBit(day)) != 0;

    private static int DayBit(DayOfWeek day) => 1 << (((int)day + 6) % 7);

    /// <summary>Das Fenster, das an <paramref name="day"/> beginnt. Null, wenn der Tag nicht gewählt ist.</summary>
    private (DateTime Start, DateTime End)? WindowStartingOn(DateTime day)
    {
        if (!HasDay(day.DayOfWeek)) return null;
        var midnight = day.Date;
        int startMinute = Math.Clamp(StartMinute, 0, MinutesPerDay - 1);
        var start = midnight.AddMinutes(startMinute);
        DateTime end;
        if (StopMinute is int stop)
        {
            stop = Math.Clamp(stop, 0, MinutesPerDay - 1);
            // Gleicher Wert wie der Start gilt als ganzer Tag.
            end = stop > startMinute ? midnight.AddMinutes(stop) : midnight.AddDays(1).AddMinutes(stop);
        }
        else
        {
            end = midnight.AddDays(1);
        }
        return (start, end);
    }

    /// <summary>Darf die Queue jetzt laufen?</summary>
    public bool IsOpen(DateTime now)
    {
        for (int back = 0; back <= 1; back++) // ein Fenster über Mitternacht begann gestern
        {
            if (WindowStartingOn(now.Date.AddDays(-back)) is { } w && now >= w.Start && now < w.End) return true;
        }
        return false;
    }

    /// <summary>Der nächste Zeitpunkt nach <paramref name="now"/>, an dem sich <see cref="IsOpen"/> ändern kann. Null, wenn nie.</summary>
    public DateTime? NextChange(DateTime now)
    {
        DateTime? best = null;
        for (int d = -1; d <= 8; d++)
        {
            if (WindowStartingOn(now.Date.AddDays(d)) is not { } w) continue;
            if (w.Start > now && (best is null || w.Start < best)) best = w.Start;
            if (w.End > now && (best is null || w.End < best)) best = w.End;
        }
        return best;
    }

    /// <summary>Der nächste Start eines Fensters nach <paramref name="now"/>. Null, wenn kein Tag gewählt ist.</summary>
    public DateTime? NextStart(DateTime now)
    {
        for (int d = 0; d <= 8; d++)
            if (WindowStartingOn(now.Date.AddDays(d)) is { } w && w.Start > now) return w.Start;
        return null;
    }

    /// <summary>Kurz für die Anzeige, etwa <c>Mo–Fr 02:00–07:00</c> oder <c>täglich ab 02:00</c> (Wörter je Sprache).</summary>
    public string Describe()
    {
        string days = DescribeDays();
        string start = Time(StartMinute);
        return StopMinute is int stop
            ? Loc.T("Schedule.Range", days, start, Time(stop))
            : Loc.T("Schedule.From", days, start);
    }

    /// <summary>Wochentag und Uhrzeit des nächsten Starts, etwa <c>Di 02:00</c>. Leer, wenn kein Tag gewählt ist.</summary>
    public string DescribeNextStart(DateTime now) =>
        NextStart(now) is { } start ? $"{DayName(((int)start.DayOfWeek + 6) % 7)} {start.ToString("HH:mm", CultureInfo.InvariantCulture)}" : "";

    private string DescribeDays()
    {
        int days = Days & AllDays;
        if (days == AllDays) return Loc.T("Schedule.Daily");
        if (days == 0) return Loc.T("Schedule.Never");

        var parts = new List<string>();
        for (int i = 0; i < 7; i++)
        {
            if ((days & (1 << i)) == 0) continue;
            int end = i;
            while (end + 1 < 7 && (days & (1 << (end + 1))) != 0) end++;
            parts.Add(end - i >= 2
                ? $"{DayName(i)}–{DayName(end)}"
                : string.Join(", ", Enumerable.Range(i, end - i + 1).Select(DayName)));
            i = end;
        }
        return string.Join(", ", parts);
    }

    public static string Time(int minutes) =>
        $"{Math.Clamp(minutes, 0, MinutesPerDay - 1) / 60:00}:{Math.Clamp(minutes, 0, MinutesPerDay - 1) % 60:00}";

    /// <summary>Liest <c>7:30</c> oder <c>07:30</c> als Minuten seit Mitternacht.</summary>
    public static bool TryParseTime(string? text, out int minutes)
    {
        minutes = 0;
        if (text is null) return false;
        var parts = text.Trim().Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int h) || parts[0].Length is 0 or > 2) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int m) || parts[1].Length != 2) return false;
        if (h > 23 || m > 59) return false;
        minutes = h * 60 + m;
        return true;
    }
}
