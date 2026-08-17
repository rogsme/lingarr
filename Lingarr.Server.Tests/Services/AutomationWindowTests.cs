using System;
using Lingarr.Server.Services;
using Xunit;

namespace Lingarr.Server.Tests.Services;

public class AutomationWindowTests
{
    private static DateTime Utc(int hour, int minute = 0) =>
        new(2026, 8, 17, hour, minute, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(4, true)]  // inside
    [InlineData(1, false)] // before
    [InlineData(9, false)] // after
    public void IsOpenNow_SameDayWindow(int utcHour, bool expected)
    {
        Assert.Equal(expected, AutomationWindow.IsOpenNow("02:00", "08:00", "UTC", Utc(utcHour)));
    }

    [Theory]
    [InlineData(23, true)]  // late evening
    [InlineData(5, true)]   // early morning
    [InlineData(12, false)] // midday
    public void IsOpenNow_OvernightWindow(int utcHour, bool expected)
    {
        Assert.Equal(expected, AutomationWindow.IsOpenNow("22:00", "06:00", "UTC", Utc(utcHour)));
    }

    [Fact]
    public void IsOpenNow_StartInclusiveEndExclusive()
    {
        Assert.True(AutomationWindow.IsOpenNow("02:00", "08:00", "UTC", Utc(2)));
        Assert.False(AutomationWindow.IsOpenNow("02:00", "08:00", "UTC", Utc(8)));
    }

    [Fact]
    public void IsOpenNow_EqualStartAndEnd_IsAlwaysOpen()
    {
        Assert.True(AutomationWindow.IsOpenNow("08:00", "08:00", "UTC", Utc(15)));
    }

    [Fact]
    public void IsOpenNow_ConvertsToConfiguredTimezone()
    {
        // 03:00 UTC is 00:00 in Sao Paulo (UTC-3), which is inside a 00:00-08:00 window
        Assert.True(AutomationWindow.IsOpenNow("00:00", "08:00", "America/Sao_Paulo", Utc(3)));
        // but 03:00 UTC is outside the same window expressed in UTC+2
        Assert.False(AutomationWindow.IsOpenNow("00:00", "04:00", "Europe/Amsterdam", Utc(3)));
    }

    [Theory]
    [InlineData("garbage", "08:00", "UTC")]
    [InlineData("00:00", "08:00", "Not/AZone")]
    public void IsOpenNow_InvalidConfiguration_FailsOpen(string start, string end, string timezone)
    {
        Assert.True(AutomationWindow.IsOpenNow(start, end, timezone, Utc(15)));
    }

    [Fact]
    public void UntilNextOpen_LaterToday()
    {
        var delay = AutomationWindow.UntilNextOpen("22:00", "06:00", "UTC", Utc(12));
        Assert.Equal(TimeSpan.FromHours(10), delay);
    }

    [Fact]
    public void UntilNextOpen_Tomorrow()
    {
        var delay = AutomationWindow.UntilNextOpen("02:00", "08:00", "UTC", Utc(9));
        Assert.Equal(TimeSpan.FromHours(17), delay);
    }

    [Fact]
    public void UntilNextOpen_FlooredAtOneMinute()
    {
        var delay = AutomationWindow.UntilNextOpen("02:00", "08:00", "UTC", Utc(1, 59).AddSeconds(50));
        Assert.Equal(TimeSpan.FromMinutes(1), delay);
    }
}
