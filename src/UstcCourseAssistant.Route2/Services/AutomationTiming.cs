namespace UstcCourseAssistant.Route2.Services;

public static class AutomationTiming
{
    private static readonly TimeSpan MinimumLateTolerance = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumLateTolerance = TimeSpan.FromMinutes(2);

    public static TimeSpan CalculateNextDelay(
        int baseSeconds,
        bool enableJitter,
        double randomUnit)
    {
        if (baseSeconds is < 5 or > 3600)
        {
            throw new ArgumentOutOfRangeException(nameof(baseSeconds));
        }

        if (randomUnit is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(randomUnit));
        }

        var seconds = enableJitter
            ? baseSeconds * ((2d / 3d) + randomUnit * (2d / 3d))
            : baseSeconds;
        return TimeSpan.FromSeconds(seconds);
    }

    public static TimeSpan CalculateLateTolerance(TimeSpan plannedDelay)
    {
        if (plannedDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(plannedDelay));
        }

        return plannedDelay < MinimumLateTolerance
            ? MinimumLateTolerance
            : plannedDelay > MaximumLateTolerance
                ? MaximumLateTolerance
                : plannedDelay;
    }

    public static bool IsSignificantlyLate(
        DateTimeOffset plannedAt,
        DateTimeOffset firedAt,
        TimeSpan plannedDelay)
    {
        if (firedAt <= plannedAt)
        {
            return false;
        }

        return firedAt - plannedAt > CalculateLateTolerance(plannedDelay);
    }
}
