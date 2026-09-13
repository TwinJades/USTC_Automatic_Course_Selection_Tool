namespace UstcCourseAssistant.Route2.Models;

public sealed record TurnInfo(long Id, string Name, string SemesterName)
{
    public string DisplayText => $"{Name} · {SemesterName}";
}

public sealed record SessionValidationResult(long StudentId, IReadOnlyList<TurnInfo> OpenTurns);

public sealed record LessonInfo(
    long Id,
    string Code,
    string CourseCode,
    string CourseName,
    int LimitCount,
    string Teachers,
    int? SelectedCount = null)
{
    public string CapacityText => SelectedCount is null
        ? "未查询"
        : $"{SelectedCount}/{LimitCount}";

    public string RemainingText => SelectedCount is null
        ? "—"
        : Math.Max(0, LimitCount - SelectedCount.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record ShadowSnapshot(
    IReadOnlyList<LessonInfo> SelectedLessons,
    IReadOnlyList<LessonInfo> TargetLessons,
    IReadOnlyList<string> MissingTargetCodes,
    int AddableLessonCount);

public enum CourseMutationKind
{
    Add,
    Drop
}

public sealed record ControlledOperationResponse(
    CourseMutationKind Kind,
    bool Completed,
    bool Success,
    string Message);

public sealed class AutomationTask
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string CourseCode { get; set; } = string.Empty;
    public string CourseName { get; set; } = "待预检查";
    public int Priority { get; set; } = 1;
    public List<string> ConflictingCourseCodes { get; set; } = [];
    public string Status { get; set; } = "等待中";

    public string ConflictsText
    {
        get => string.Join(", ", ConflictingCourseCodes);
        set => ConflictingCourseCodes = (value ?? string.Empty)
            .Split([',', '，', ';', '；', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed class AutomationSettings
{
    public int IntervalSeconds { get; set; } = 15;
    public bool EnableIntervalJitter { get; set; }
    public bool EnableSchedule { get; set; }
    public TimeOnly StartAt { get; set; } = new(0, 0);
    public TimeOnly EndAt { get; set; } = new(23, 59);
    public bool RunAtWindowsStartup { get; set; }
    public List<AutomationTask> Tasks { get; set; } = [];
}

public sealed record VerifiedMutationResult(
    bool ExpectedStateReached,
    bool FinallySelected,
    string Message);

public sealed record AutomationCycleResult(bool ShouldPause, string Summary);
