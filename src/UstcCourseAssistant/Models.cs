namespace UstcCourseAssistant;

public sealed class CourseTask
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string CourseCode { get; set; } = "";
    public string CourseName { get; set; } = "待确认";
    public int Priority { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public List<string> ConflictingCourseCodes { get; set; } = [];
    public string ConflictsText
    {
        get => string.Join(", ", ConflictingCourseCodes);
        set => ConflictingCourseCodes = (value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
    }
    public string Status { get; set; } = "等待中";
}

public sealed class AppSettings
{
    public string StudentId { get; set; } = "";
    public int IntervalSeconds { get; set; } = 60;
    public bool EnableSchedule { get; set; }
    public TimeOnly StartAt { get; set; } = new(0, 0);
    public TimeOnly EndAt { get; set; } = new(23, 59);
    public bool WeekdaysOnly { get; set; }
    public bool RunAtStartup { get; set; }
    public List<CourseTask> Tasks { get; set; } = [];
}

public sealed record AppLog(DateTimeOffset At, string Level, string Message);
