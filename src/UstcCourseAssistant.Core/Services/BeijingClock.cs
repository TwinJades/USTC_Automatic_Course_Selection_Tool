namespace UstcCourseAssistant.Core.Services;

/// <summary>现代选课业务统一使用 UTC+08:00，不依赖宿主系统时区及 Windows 时区数据库。</summary>
public static class BeijingClock
{
    public static TimeZoneInfo TimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone(
        "UTC+08:00", TimeSpan.FromHours(8), "北京时间", "北京时间");

    public static DateTimeOffset Now(TimeProvider? timeProvider = null) =>
        TimeZoneInfo.ConvertTime((timeProvider ?? TimeProvider.System).GetUtcNow(), TimeZone);

    public static bool IsInWindow(DateTimeOffset instant, TimeOnly start, TimeOnly end)
    {
        var time = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, TimeZone).DateTime);
        return start <= end ? time >= start && time <= end : time >= start || time <= end;
    }
}
