using UstcCourseAssistant.Route2.Models;

namespace UstcCourseAssistant.Route2.Services;

public static class AutomationTaskRules
{
    public static void DisableConflictingLowerPriorityTasks(
        IEnumerable<AutomationTask> tasks,
        AutomationTask selectedTask,
        Action<string, string, ActivityEventKind> writeLog)
    {
        var selectedCode = selectedTask.CourseCode.Trim();
        foreach (var lower in tasks)
        {
            if (!lower.Enabled
                || lower.Priority <= selectedTask.Priority
                || lower.StudentId != selectedTask.StudentId
                || lower.TurnId != selectedTask.TurnId)
            {
                continue;
            }

            var lowerCode = lower.CourseCode.Trim();
            if (!ContainsCode(lower.ConflictingCourseCodes, selectedCode)
                && !ContainsCode(selectedTask.ConflictingCourseCodes, lowerCode))
            {
                continue;
            }

            lower.Enabled = false;
            lower.Status = $"已停用：与高优先级 {selectedCode} 冲突";
            writeLog("信息", $"{lowerCode}：与已选高优先级任务 {selectedCode} 冲突，已停用。", ActivityEventKind.Detail);
        }
    }

    private static bool ContainsCode(IEnumerable<string> codes, string expected)
    {
        foreach (var code in codes)
        {
            if (string.Equals(code.Trim(), expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
