using UstcCourseAssistant.Route2.Models;

namespace UstcCourseAssistant.Route2.Services;

public interface IAutomationReadClient
{
    Task<ShadowSnapshot> GetShadowSnapshotAsync(
        long studentId,
        long turnId,
        IReadOnlyCollection<string> targetCodes,
        CancellationToken cancellationToken = default);
}

public interface IAutomationMutationClient
{
    Task<ControlledOperationResponse> ExecuteOnceAsync(
        CourseMutationKind kind,
        long studentId,
        long turnId,
        long lessonId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 路线二任务引擎。所有选退课严格串行；本类从不并发提交，也不重试写请求。
/// </summary>
public sealed class Route2AutomationEngine(
    IAutomationReadClient readClient,
    IProtectedMutationExecutor? protectedMutations,
    long studentId,
    long turnId,
    Action<string, string, ActivityEventKind> writeLog)
{
    public async Task<AutomationCycleResult> RunOnceAsync(
        IEnumerable<AutomationTask> tasks,
        bool executeWhenAvailable,
        CancellationToken cancellationToken)
    {
        var taskList = tasks.OrderBy(item => item.Priority).ToArray();
        foreach (var task in taskList.Where(item => item.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var code = task.CourseCode.Trim();
            if (code.Length == 0)
            {
                task.Status = "请输入完整教学班代码";
                continue;
            }

            var conflictCodes = task.ConflictingCourseCodes
                .Select(item => item.Trim())
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (conflictCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                task.Status = "冲突旧课不能与目标课相同";
                task.Enabled = false;
                writeLog("错误", $"{code}：冲突旧课列表包含目标课自身，任务已停用。", ActivityEventKind.Detail);
                continue;
            }

            var requestedCodes = new[] { code }.Concat(conflictCodes).ToArray();
            var snapshot = await readClient.GetShadowSnapshotAsync(
                studentId,
                turnId,
                requestedCodes,
                cancellationToken);
            var byCode = snapshot.TargetLessons
                .GroupBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

            if (!TryGetUnique(byCode, code, out var targetLesson, out var targetError))
            {
                task.Status = targetError;
                writeLog("警告", $"{code}：{targetError}。", ActivityEventKind.Detail);
                if (targetError.StartsWith("存在多个", StringComparison.Ordinal))
                {
                    task.Enabled = false;
                    return new AutomationCycleResult(true, "目标教学班无法唯一确定；任务已停用并暂停监测。");
                }

                continue;
            }

            task.CourseName = targetLesson.CourseName;
            var selectedIds = snapshot.SelectedLessons.Select(item => item.Id).ToHashSet();
            if (selectedIds.Contains(targetLesson.Id))
            {
                task.Status = "已选中（最终状态确认）";
                task.Enabled = false;
                writeLog("成功", $"{code}：已在当前已选课程中，任务完成。", ActivityEventKind.Detail);
                DisableConflictingLowerPriorityTasks(taskList, task);
                continue;
            }

            if (targetLesson.SelectedCount is null)
            {
                task.Status = "已找到，但人数读取失败";
                writeLog("警告", $"{code}：无法确定剩余名额，本轮不提交。", ActivityEventKind.Detail);
                continue;
            }

            var remaining = Math.Max(0, targetLesson.LimitCount - targetLesson.SelectedCount.Value);
            if (remaining <= 0)
            {
                task.Status = "暂无余量";
                writeLog("信息", $"{code}：暂无余量。", ActivityEventKind.Detail);
                continue;
            }

            if (!executeWhenAvailable)
            {
                task.Status = $"可选（剩余 {remaining}）";
                writeLog("只读", $"{code}：检测到 {remaining} 个余量；只读检查未提交操作。", ActivityEventKind.Detail);
                continue;
            }

            var conflictsToDrop = new List<LessonInfo>();
            foreach (var conflictCode in conflictCodes)
            {
                if (!TryGetUnique(byCode, conflictCode, out var conflictLesson, out var conflictError))
                {
                    // 不在已选课程中的旧课无需处理；同代码多班则无法安全确定目标。
                    if (conflictError == "未在当前轮次找到")
                    {
                        continue;
                    }

                    task.Status = $"冲突课 {conflictCode}：{conflictError}";
                    task.Enabled = false;
                    writeLog("错误", $"{code}：无法唯一确定冲突旧课 {conflictCode}，任务已停用。", ActivityEventKind.Detail);
                    return new AutomationCycleResult(true, "冲突旧课无法唯一确定。已暂停监测。");
                }

                if (selectedIds.Contains(conflictLesson.Id))
                {
                    conflictsToDrop.Add(conflictLesson);
                }
            }

            var dropped = new List<LessonInfo>();
            var protectedExecutor = protectedMutations
                                    ?? throw new InvalidOperationException("真实任务监测缺少统一操作保护，已拒绝提交。");
            protectedExecutor.BeginWorkflow(
                "任务监测换课",
                studentId,
                turnId,
                code,
                conflictCodes);
            foreach (var conflict in conflictsToDrop)
            {
                task.Status = $"正在退旧课 {conflict.Code}";
                writeLog("受控写入", $"{code}：准备退旧课 {conflict.Code}。", ActivityEventKind.CourseDrop);
                var drop = await protectedExecutor.ExecuteAndVerifyAsync(
                    CourseMutationKind.Drop,
                    conflict,
                    $"退旧课 {conflict.Code}",
                    cancellationToken);
                if (!drop.ExpectedStateReached)
                {
                    task.Status = $"退旧课失败：{conflict.Code}";
                    writeLog("错误", $"{code}：退旧课 {conflict.Code} 未通过最终状态复核：{drop.Message}", ActivityEventKind.CourseDrop);
                    var restored = await RestoreAsync(code, dropped, task, protectedExecutor, cancellationToken);
                    if (restored)
                    {
                        protectedExecutor.CompleteWorkflow("退旧课失败；已确认此前旧课恢复，当前状态已知。");
                    }
                    else
                    {
                        protectedExecutor.RequireManualReview("退旧课失败且旧课恢复不完整。");
                    }

                    return new AutomationCycleResult(true, restored
                        ? "退旧课失败；已恢复此前旧课并暂停监测。"
                        : "退旧课失败且旧课恢复不完整；请立即人工核对。已暂停监测。");
                }

                dropped.Add(conflict);
                selectedIds.Remove(conflict.Id);
                protectedExecutor.RecordConfirmedDropBeforeTarget(conflict);
            }

            cancellationToken.ThrowIfCancellationRequested();
            protectedExecutor.MarkTargetRequestWillStart();
            task.Status = "正在提交目标课";
            writeLog("受控写入", $"{code}：检测到余量，正在提交一次选课。", ActivityEventKind.CourseAdd);
            var add = await protectedExecutor.ExecuteAndVerifyAsync(
                CourseMutationKind.Add,
                targetLesson,
                $"选目标课 {targetLesson.Code}",
                cancellationToken);
            if (add.ExpectedStateReached)
            {
                task.Status = "已选中（最终状态确认）";
                task.Enabled = false;
                protectedExecutor.CompleteWorkflow("目标课已选中，整个流程最终状态已确认。");
                writeLog("成功", $"{code}：选课成功并通过最终状态复核。", ActivityEventKind.CourseAdd);
                DisableConflictingLowerPriorityTasks(taskList, task);
                continue;
            }

            task.Status = "目标课未选中，正在恢复旧课";
            writeLog("警告", $"{code}：目标课最终未选中，开始串行恢复旧课。{add.Message}", ActivityEventKind.CourseAdd);
            var restoreSucceeded = await RestoreAsync(code, dropped, task, protectedExecutor, cancellationToken);
            task.Enabled = false;
            task.Status = restoreSucceeded
                ? "目标课失败；旧课已恢复"
                : "严重：目标课失败且旧课恢复不完整";
            if (restoreSucceeded)
            {
                protectedExecutor.CompleteWorkflow("目标课未选中；本轮已退旧课均已恢复，状态已知。");
            }
            else
            {
                protectedExecutor.RequireManualReview("目标课未选中且至少一门旧课恢复失败。");
            }

            return new AutomationCycleResult(true, restoreSucceeded
                ? "目标课失败；旧课已恢复，任务已停用并暂停监测。"
                : "旧课恢复不完整；请立即人工核对。已暂停监测。");
        }

        return new AutomationCycleResult(false, executeWhenAvailable ? "本轮监测完成。" : "本轮只读检查完成。");
    }

    private async Task<bool> RestoreAsync(
        string targetCode,
        IEnumerable<LessonInfo> dropped,
        AutomationTask task,
        IProtectedMutationExecutor protectedExecutor,
        CancellationToken cancellationToken)
    {
        var allRestored = true;
        foreach (var oldLesson in dropped)
        {
            task.Status = $"正在恢复旧课 {oldLesson.Code}";
            writeLog("恢复", $"{targetCode}：正在恢复旧课 {oldLesson.Code}。", ActivityEventKind.CourseRestore);
            var restore = await protectedExecutor.ExecuteAndVerifyAsync(
                CourseMutationKind.Add,
                oldLesson,
                $"恢复旧课 {oldLesson.Code}",
                cancellationToken);
            if (restore.ExpectedStateReached)
            {
                writeLog("恢复", $"{targetCode}：旧课 {oldLesson.Code} 已恢复。", ActivityEventKind.CourseRestore);
            }
            else
            {
                allRestored = false;
                writeLog("严重", $"{targetCode}：旧课 {oldLesson.Code} 恢复失败：{restore.Message}", ActivityEventKind.CourseRestore);
            }
        }

        return allRestored;
    }

    private void DisableConflictingLowerPriorityTasks(
        IEnumerable<AutomationTask> tasks,
        AutomationTask selectedTask)
    {
        foreach (var lower in tasks.Where(item => item.Enabled && item.Priority > selectedTask.Priority))
        {
            var conflicts = lower.ConflictingCourseCodes.Contains(
                                selectedTask.CourseCode,
                                StringComparer.OrdinalIgnoreCase)
                            || selectedTask.ConflictingCourseCodes.Contains(
                                lower.CourseCode,
                                StringComparer.OrdinalIgnoreCase);
            if (!conflicts)
            {
                continue;
            }

            lower.Enabled = false;
            lower.Status = $"已停用：与高优先级 {selectedTask.CourseCode} 冲突";
            writeLog("信息", $"{lower.CourseCode}：与已选高优先级任务 {selectedTask.CourseCode} 冲突，已停用。", ActivityEventKind.Detail);
        }
    }

    private static bool TryGetUnique(
        IReadOnlyDictionary<string, LessonInfo[]> byCode,
        string code,
        out LessonInfo lesson,
        out string error)
    {
        lesson = default!;
        if (!byCode.TryGetValue(code, out var matches) || matches.Length == 0)
        {
            error = "未在当前轮次找到";
            return false;
        }

        if (matches.Length != 1)
        {
            error = "存在多个同代码教学班，无法安全确定唯一目标";
            return false;
        }

        lesson = matches[0];
        error = string.Empty;
        return true;
    }
}
