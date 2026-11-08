using System.Text.Json;
using UstcCourseAssistant.Core.Authentication;
using UstcCourseAssistant.Core.Models;

namespace UstcCourseAssistant.Core.Services;

public sealed record ControlRequest(Guid Id, string Command, JsonElement Arguments);
public sealed record ControlReply(Guid Id, bool Success, string Code, string Message, JsonElement? Data);

/// <summary>仅包含可对本地客户端公开的状态，不包含凭据或 Cookie。</summary>
public sealed record AssistantSnapshot(
    AuthenticationSnapshot Authentication,
    bool MonitoringAuthorized,
    bool MonitoringRunning,
    bool SafetyPaused,
    CourseExecutionContext? MonitoringContext,
    SessionValidationResult? Session,
    AutomationSettings Monitoring,
    ScheduledSelection? Appointment,
    OperationJournal? PendingOperation,
    string Status,
    AppUpdateState UpdateState);
