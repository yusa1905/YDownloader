using System;
using Xunit;

namespace YDownloader.Tests;

public class ScheduleTests
{
    // 2026-10-12 ist ein Montag.
    private static DateTime At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0);

    private static ScheduleSetting Night(int days = ScheduleSetting.AllDays) =>
        new() { StartMinute = 22 * 60, StopMinute = 6 * 60, Days = days };

    private static int Minutes(int hour, int minute = 0) => hour * 60 + minute;

    [Fact]
    public void Day_window_is_open_between_start_and_stop_only()
    {
        var s = new ScheduleSetting { StartMinute = Minutes(2), StopMinute = Minutes(7) };
        Assert.False(s.IsOpen(At(12, 1, 59)));
        Assert.True(s.IsOpen(At(12, 2)));
        Assert.True(s.IsOpen(At(12, 6, 59)));
        Assert.False(s.IsOpen(At(12, 7))); // Stopp gehört nicht mehr dazu
        Assert.False(s.IsOpen(At(12, 15)));
    }

    [Fact]
    public void Window_over_midnight_belongs_to_the_start_day()
    {
        var s = Night();
        Assert.False(s.IsOpen(At(12, 21, 59)));
        Assert.True(s.IsOpen(At(12, 22)));
        Assert.True(s.IsOpen(At(12, 23, 59)));
        Assert.True(s.IsOpen(At(13, 0)));
        Assert.True(s.IsOpen(At(13, 5, 59)));
        Assert.False(s.IsOpen(At(13, 6)));
    }

    [Fact]
    public void Over_midnight_the_morning_part_follows_the_start_day_not_its_own()
    {
        // Nur Freitag gewählt: Freitag 22:00 bis Samstag 06:00. Der Freitagmorgen gehört nicht dazu, der Samstagmorgen schon.
        var s = Night(days: 1 << 4);
        Assert.False(s.IsOpen(At(16, 3)));  // Freitag früh
        Assert.True(s.IsOpen(At(16, 23)));  // Freitag spät
        Assert.True(s.IsOpen(At(17, 3)));   // Samstag früh
        Assert.False(s.IsOpen(At(17, 23))); // Samstag spät
    }

    [Fact]
    public void Only_selected_weekdays_open()
    {
        var weekdays = new ScheduleSetting { StartMinute = Minutes(8), StopMinute = Minutes(17), Days = 0b001_1111 }; // Mo-Fr
        Assert.True(weekdays.IsOpen(At(12, 9)));   // Montag
        Assert.True(weekdays.IsOpen(At(16, 9)));   // Freitag
        Assert.False(weekdays.IsOpen(At(17, 9)));  // Samstag
        Assert.False(weekdays.IsOpen(At(18, 9)));  // Sonntag
    }

    [Fact]
    public void Without_stop_it_stays_open_until_midnight()
    {
        var s = new ScheduleSetting { StartMinute = Minutes(2) };
        Assert.False(s.IsOpen(At(12, 1)));
        Assert.True(s.IsOpen(At(12, 2)));
        Assert.True(s.IsOpen(At(12, 23, 59)));
        Assert.True(s.IsOpen(At(13, 3))); // der nächste Tag hat sein eigenes Fenster
        Assert.False(s.IsOpen(At(13, 1)));
    }

    [Fact]
    public void Same_start_and_stop_means_a_whole_day()
    {
        var monday = new ScheduleSetting { StartMinute = Minutes(2), StopMinute = Minutes(2), Days = 1 };
        Assert.False(monday.IsOpen(At(12, 1, 59)));
        Assert.True(monday.IsOpen(At(12, 2)));
        Assert.True(monday.IsOpen(At(13, 1, 59))); // bis Dienstag 02:00
        Assert.False(monday.IsOpen(At(13, 2)));
    }

    [Fact]
    public void No_day_never_opens_and_has_no_next_change()
    {
        var s = new ScheduleSetting { StartMinute = Minutes(2), StopMinute = Minutes(7), Days = 0 };
        Assert.False(s.IsOpen(At(12, 3)));
        Assert.Null(s.NextChange(At(12, 3)));
        Assert.Null(s.NextStart(At(12, 3)));
    }

    [Fact]
    public void Next_change_is_the_next_start_or_stop()
    {
        var s = new ScheduleSetting { StartMinute = Minutes(2), StopMinute = Minutes(7) };
        Assert.Equal(At(12, 2), s.NextChange(At(12, 0)));
        Assert.Equal(At(12, 7), s.NextChange(At(12, 3)));
        Assert.Equal(At(13, 2), s.NextChange(At(12, 8)));
        Assert.Equal(At(12, 7), s.NextChange(At(12, 2))); // genau am Start: der nächste Wechsel ist der Stopp
    }

    [Fact]
    public void Next_change_finds_a_window_days_ahead()
    {
        var sunday = new ScheduleSetting { StartMinute = Minutes(10), StopMinute = Minutes(12), Days = 1 << 6 };
        Assert.Equal(At(18, 10), sunday.NextChange(At(12, 9)));
        Assert.Equal(At(18, 10), sunday.NextStart(At(12, 9)));
    }

    [Fact]
    public void Next_change_inside_a_midnight_window_is_its_end()
    {
        Assert.Equal(At(13, 6), Night().NextChange(At(13, 1)));
        Assert.Equal(At(12, 22), Night().NextStart(At(12, 12)));
    }

    [Theory]
    [InlineData(ScheduleSetting.AllDays, "täglich 02:00–07:00")]
    [InlineData(0b001_1111, "Mo–Fr 02:00–07:00")]
    [InlineData(0b110_0000, "Sa, So 02:00–07:00")]
    [InlineData(0b100_0101, "Mo, Mi, So 02:00–07:00")]
    [InlineData(0b001_0111, "Mo–Mi, Fr 02:00–07:00")]
    public void Describes_days_and_times(int days, string expected) =>
        Assert.Equal(expected, new ScheduleSetting { StartMinute = Minutes(2), StopMinute = Minutes(7), Days = days }.Describe());

    [Fact]
    public void Describes_open_ended_schedules_and_the_next_start()
    {
        var s = new ScheduleSetting { StartMinute = Minutes(2) };
        Assert.Equal("täglich ab 02:00", s.Describe());
        Assert.Equal("Di 02:00", s.DescribeNextStart(At(12, 3))); // Montag 03:00, nächster Start Dienstag
        Assert.Equal("Mo 02:00", s.DescribeNextStart(At(12, 1)));
    }

    [Theory]
    [InlineData("02:00", true, 120)]
    [InlineData("2:00", true, 120)]
    [InlineData(" 23:59 ", true, 1439)]
    [InlineData("00:00", true, 0)]
    [InlineData("24:00", false, 0)]
    [InlineData("12:60", false, 0)]
    [InlineData("12:5", false, 0)]
    [InlineData("1200", false, 0)]
    [InlineData("-1:30", false, 0)]
    [InlineData("", false, 0)]
    [InlineData("ab:cd", false, 0)]
    public void Parses_clock_times(string text, bool ok, int minutes)
    {
        Assert.Equal(ok, ScheduleSetting.TryParseTime(text, out int parsed));
        if (ok) Assert.Equal(minutes, parsed);
    }

    [Fact]
    public void Time_formats_with_leading_zeros() => Assert.Equal("07:05", ScheduleSetting.Time(7 * 60 + 5));
}
