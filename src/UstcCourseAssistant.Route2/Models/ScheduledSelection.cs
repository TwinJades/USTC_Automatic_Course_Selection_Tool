namespace UstcCourseAssistant.Route2.Models;

public enum ScheduledSelectionState
{
    Draft,
    Armed,
    Queued,
    Running,
    Completed,
    Failed,
    Missed,
    Cancelled,
    Interrupted
}

public enum ScheduledCompletionAction
{
    Stop,
    ImportToMonitoring
}

public sealed class ScheduledSelection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset ExecuteAt { get; set; }
    public bool RunWhenMissed { get; set; }
    public long StudentId { get; set; }
    public long? ExecutedTurnId { get; set; }
    public string ExecutedTurnName { get; set; } = string.Empty;
    public ScheduledCompletionAction CompletionAction { get; set; }
    public ScheduledSelectionState State { get; set; } = ScheduledSelectionState.Draft;
    public List<AutomationTask> Tasks { get; set; } = [];
    public bool RealOperationsAuthorized { get; set; }
    public List<Guid> ConfirmedSelectedTaskIds { get; set; } = [];
    public bool SelectionFinished { get; set; }
    public bool ImportFinished { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Result { get; set; } = string.Empty;
}

public sealed class ScheduledSelectionSettings
{
    public ScheduledSelection? Appointment { get; set; }
}
