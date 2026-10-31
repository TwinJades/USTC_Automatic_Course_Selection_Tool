using UstcCourseAssistant.Route2.Models;

namespace UstcCourseAssistant.Route2.Services;

public sealed class ScheduledSelectionService
{
    public static TimeZoneInfo BeijingTimeZone { get; } = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);
    private readonly ScheduledSelectionStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly HashSet<Guid> _restartRecoveryIds;
    private ScheduledSelectionSettings _settings;
    private DateTimeOffset? _previousTickAt;
    private bool _restartScanPending;
    private bool _restored;

    public ScheduledSelectionService(ScheduledSelectionStore store, TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _settings = _store.Load();
        _restartRecoveryIds = _settings.Appointments.Where(NeedsRestartRecovery).Select(item => item.Id).ToHashSet();
        _restartScanPending = _settings.Appointments.Any(item => item.State == ScheduledSelectionState.Armed);
        _previousTickAt = _timeProvider.GetUtcNow();
    }

    // 调用方编辑独立快照；只有持久化成功的事务才发布服务内存状态。
    public ScheduledSelectionSettings Settings
    {
        get
        {
            lock (_sync)
            {
                return CopySettings(_settings);
            }
        }
    }

    public IReadOnlyList<ScheduledSelection> Appointments => Settings.Appointments;
    public string? LoadWarning => _store.LoadWarning;
    public IReadOnlyList<ScheduledSelection> PendingImports => Appointments
        .Where(item => item.State == ScheduledSelectionState.Running && item.SelectionFinished && !item.ImportFinished
                       && item.CompletionAction == ScheduledCompletionAction.ImportToMonitoring)
        .OrderBy(item => item.ExecuteAt).ThenBy(item => item.CreatedAt).ThenBy(item => item.Id).ToArray();

    public ScheduledSelection Get(Guid id)
    {
        lock (_sync)
        {
            return Copy(Find(_settings, id));
        }
    }

    public void UpsertDraft(ScheduledSelection appointment)
    {
        ArgumentNullException.ThrowIfNull(appointment);
        var draft = Copy(appointment);
        Transact(settings =>
        {
            var existing = settings.Appointments.FindIndex(item => item.Id == draft.Id);
            if (existing >= 0)
            {
                var current = settings.Appointments[existing];
                RequireState(current, ScheduledSelectionState.Draft, ScheduledSelectionState.Armed);
                draft.CreatedAt = current.CreatedAt;
            }
            else if (draft.Id == Guid.Empty)
            {
                throw new InvalidOperationException("预约标识不能为空。");
            }

            ResetExecution(draft);
            if (existing < 0)
            {
                settings.Appointments.Add(draft);
            }
            else
            {
                settings.Appointments[existing] = draft;
            }
        });
    }

    public void BeginEdit(Guid id) => Transact(settings =>
    {
        var appointment = Find(settings, id);
        RequireState(appointment, ScheduledSelectionState.Draft, ScheduledSelectionState.Armed);
        ResetExecution(appointment);
    });

    public ScheduledSelection CopyAsDraft(Guid id)
    {
        var draft = Get(id);
        draft.Id = Guid.NewGuid();
        draft.CreatedAt = InBeijing(_timeProvider.GetUtcNow());
        draft.Tasks = draft.Tasks.Select(task => CopyTask(task, newId: true)).ToList();
        ResetExecution(draft);
        UpsertDraft(draft);
        return Get(draft.Id);
    }

    public void Delete(Guid id) => Transact(settings =>
    {
        var appointment = Find(settings, id);
        if (appointment.State is ScheduledSelectionState.Armed or ScheduledSelectionState.Queued or ScheduledSelectionState.Running)
        {
            throw new InvalidOperationException("请先取消尚未开始的预约；运行中的预约不能删除。");
        }

        settings.Appointments.Remove(appointment);
    });

    public void Arm(ScheduledSelection appointment, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(appointment);
        var armed = Copy(appointment);
        ValidateForArming(armed, now);
        Transact(settings =>
        {
            var index = settings.Appointments.FindIndex(item => item.Id == armed.Id);
            if (index >= 0)
            {
                var existing = settings.Appointments[index];
                RequireState(existing, ScheduledSelectionState.Draft, ScheduledSelectionState.Armed);
                armed.CreatedAt = existing.CreatedAt;
            }

            ResetExecution(armed);
            armed.RealOperationsAuthorized = true;
            armed.State = ScheduledSelectionState.Armed;
            armed.ExecuteAt = InBeijing(armed.ExecuteAt);
            armed.CreatedAt = InBeijing(armed.CreatedAt);
            if (index < 0)
            {
                settings.Appointments.Add(armed);
            }
            else
            {
                settings.Appointments[index] = armed;
            }
        });
    }

    public IReadOnlyList<ScheduledSelection> CollectDue(DateTimeOffset now, bool resumed)
    {
        lock (_sync)
        {
            if (!_restored)
            {
                RestoreAfterRestart(now);
            }

            var missedScan = resumed || _restartScanPending
                || (_previousTickAt is { } previous && AutomationTiming.IsSignificantlyLate(previous + TickInterval, now, TickInterval));
            var due = _settings.Appointments.Where(item => item.State == ScheduledSelectionState.Armed && item.ExecuteAt <= now)
                .OrderBy(item => item.ExecuteAt).ThenBy(item => item.CreatedAt).ToArray();
            var queuedIds = new List<Guid>();
            if (due.Length > 0)
            {
                Transact(settings =>
                {
                    foreach (var item in due)
                    {
                        var appointment = Find(settings, item.Id);
                        if (missedScan && !appointment.RunWhenMissed)
                        {
                            appointment.State = ScheduledSelectionState.Missed;
                            appointment.FinishedAt = InBeijing(now);
                            appointment.Result = "预约时间已错过，未勾选错过后补执行。";
                        }
                        else
                        {
                            appointment.State = ScheduledSelectionState.Queued;
                            appointment.Result = "已到预约时间，等待依次执行。";
                            queuedIds.Add(appointment.Id);
                        }
                    }
                });
            }

            _previousTickAt = now;
            _restartScanPending = false;
            return queuedIds.Select(id => Copy(Find(_settings, id))).ToArray();
        }
    }

    public void MarkRunning(Guid id, DateTimeOffset now) => Transact(settings =>
    {
        var appointment = Find(settings, id);
        RequireState(appointment, ScheduledSelectionState.Queued);
        if (!appointment.RealOperationsAuthorized || appointment.StartedAt is not null || appointment.SelectionFinished)
        {
            throw new InvalidOperationException("预约未经授权或已开始过，不能重新执行。");
        }

        appointment.State = ScheduledSelectionState.Running;
        appointment.StartedAt = InBeijing(now);
        appointment.Result = "正在执行一次选课检查。";
    });

    public void SetExecutedTurn(Guid id, TurnInfo turn) => Transact(settings =>
    {
        var appointment = Find(settings, id);
        RequireExecuting(appointment);
        AssignExecutedTurn(appointment, turn);
    });

    public void SaveExecutionProgress(Guid id, IEnumerable<AutomationTask> tasks, IEnumerable<Guid>? confirmedSelectedTaskIds = null)
    {
        var snapshot = tasks.Select(task => CopyTask(task)).ToList();
        var confirmed = confirmedSelectedTaskIds?.ToArray() ?? [];
        Transact(settings =>
        {
            var appointment = Find(settings, id);
            RequireExecuting(appointment);
            UpdateProgress(appointment, snapshot, confirmed);
        });
    }

    public void MarkSelectionFinished(Guid id, TurnInfo turn, IEnumerable<AutomationTask> tasks, IEnumerable<Guid>? confirmedSelectedTaskIds = null)
    {
        var snapshot = tasks.Select(task => CopyTask(task)).ToList();
        var confirmed = confirmedSelectedTaskIds?.ToArray() ?? [];
        Transact(settings =>
        {
            var appointment = Find(settings, id);
            RequireExecuting(appointment);
            AssignExecutedTurn(appointment, turn);
            UpdateProgress(appointment, snapshot, confirmed);
            appointment.SelectionFinished = true;
            appointment.ImportFinished = appointment.CompletionAction == ScheduledCompletionAction.Stop;
            appointment.Result = appointment.ImportFinished ? "一轮执行已结束，等待保存完成结果。" : "一轮执行已结束，等待导入剩余课程。";
        });
    }

    public void MarkImportFinished(Guid id) => Transact(settings =>
    {
        var appointment = Find(settings, id);
        RequireState(appointment, ScheduledSelectionState.Running);
        if (!appointment.SelectionFinished || appointment.ExecutedTurnId is null)
        {
            throw new InvalidOperationException("选课阶段尚未完整保存，不能确认导入完成。");
        }

        appointment.ImportFinished = true;
        appointment.Result = "剩余课程导入已保存，等待保存完成结果。";
    });

    public void MarkFinished(Guid id, ScheduledSelectionState state, string result, DateTimeOffset now) => Transact(settings =>
    {
        if (state is not (ScheduledSelectionState.Completed or ScheduledSelectionState.Failed or ScheduledSelectionState.Interrupted))
        {
            throw new ArgumentOutOfRangeException(nameof(state), "结束状态必须为完成、失败或中断。");
        }

        var appointment = Find(settings, id);
        RequireState(appointment, ScheduledSelectionState.Queued, ScheduledSelectionState.Running);
        if (state == ScheduledSelectionState.Completed && (!appointment.SelectionFinished || !appointment.ImportFinished))
        {
            throw new InvalidOperationException("执行或导入阶段尚未完整保存，不能标记预约完成。");
        }

        appointment.State = state;
        appointment.Result = result ?? string.Empty;
        appointment.FinishedAt = InBeijing(now);
    });

    public void Cancel(Guid id) => Transact(settings =>
    {
        var appointment = Find(settings, id);
        RequireState(appointment, ScheduledSelectionState.Draft, ScheduledSelectionState.Armed, ScheduledSelectionState.Queued);
        appointment.State = ScheduledSelectionState.Cancelled;
        appointment.FinishedAt = InBeijing(_timeProvider.GetUtcNow());
        appointment.Result = "预约已取消。";
    });

    public void RestoreAfterRestart(DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_restored)
            {
                return;
            }

            var needsRecovery = _settings.Appointments.Any(item => _restartRecoveryIds.Contains(item.Id) && NeedsRestartRecovery(item));
            if (needsRecovery)
            {
                Transact(settings =>
                {
                    foreach (var appointment in settings.Appointments.Where(item => _restartRecoveryIds.Contains(item.Id) && NeedsRestartRecovery(item)))
                    {
                        if (!appointment.SelectionFinished)
                        {
                            appointment.State = ScheduledSelectionState.Interrupted;
                            appointment.FinishedAt = InBeijing(now);
                            appointment.Result = "程序在预约入队或执行后退出，实际结果需人工核对；禁止自动重放。";
                        }
                        else if (appointment.CompletionAction == ScheduledCompletionAction.Stop || appointment.ImportFinished)
                        {
                            appointment.ImportFinished = true;
                            appointment.State = ScheduledSelectionState.Completed;
                            appointment.FinishedAt = InBeijing(now);
                            appointment.Result = "已恢复完整保存的执行结果，未重新执行选退课。";
                        }
                        else if (appointment.ExecutedTurnId is null)
                        {
                            appointment.State = ScheduledSelectionState.Interrupted;
                            appointment.FinishedAt = InBeijing(now);
                            appointment.Result = "执行记录缺少实际轮次，禁止自动导入或重放，请人工核对。";
                        }
                        else
                        {
                            appointment.State = ScheduledSelectionState.Running;
                            appointment.Result = "选课阶段已完成，仅等待恢复幂等导入，不会重跑课程。";
                        }
                    }
                });
            }

            _restored = true;
        }
    }

    public void RestoreQueuedToArmed()
    {
        lock (_sync)
        {
            if (!_settings.Appointments.Any(item => item.State == ScheduledSelectionState.Queued))
            {
                return;
            }

            Transact(settings =>
            {
                foreach (var appointment in settings.Appointments.Where(item => item.State == ScheduledSelectionState.Queued))
                {
                    if (appointment.StartedAt is not null || appointment.SelectionFinished)
                    {
                        throw new InvalidOperationException("已开始的预约不能恢复为未执行状态。");
                    }

                    appointment.State = ScheduledSelectionState.Armed;
                    appointment.Result = "程序关闭前已保存；下次启动按错过处理设置决定是否补执行。";
                }
            });
        }
    }

    public static TurnInfo ResolveTurn(SessionValidationResult session, ScheduledSelection appointment)
    {
        if (session.StudentId != appointment.StudentId)
        {
            throw new InvalidOperationException("当前登录账号与预约所属账号不一致，预约不会执行。");
        }

        if (appointment.TurnId is { } turnId)
        {
            return session.OpenTurns.FirstOrDefault(turn => turn.Id == turnId)
                ?? throw new InvalidOperationException($"预约指定轮次“{appointment.TurnName}”（{turnId}）已关闭或不可用。");
        }

        return session.OpenTurns.Count switch
        {
            0 => throw new InvalidOperationException("当前没有开放选课轮次，预约无法执行。"),
            1 => session.OpenTurns[0],
            _ => throw new InvalidOperationException("存在多个开放选课轮次，请指定轮次后重新启用预约。")
        };
    }

    public static int ImportRemainingTasks(ScheduledSelection appointment, AutomationSettings monitoring)
    {
        if (!appointment.SelectionFinished || appointment.ExecutedTurnId is not { } turnId)
        {
            throw new InvalidOperationException("必须先完整保存选课阶段及实际轮次，才可以导入剩余课程。");
        }

        if (appointment.State is ScheduledSelectionState.Interrupted or ScheduledSelectionState.Failed
            || appointment.CompletionAction != ScheduledCompletionAction.ImportToMonitoring)
        {
            throw new InvalidOperationException("该预约不允许自动导入剩余课程。");
        }

        var tasks = monitoring.Tasks ?? [];
        var selected = appointment.ConfirmedSelectedTaskIds.ToHashSet();
        var keys = new HashSet<(long?, long?, string)>(TaskKeyComparer.Instance);
        foreach (var task in tasks)
        {
            keys.Add((task.StudentId, task.TurnId, task.CourseCode.Trim()));
        }

        var priority = tasks.Count == 0 ? 0 : tasks.Max(task => task.Priority);
        var additions = new List<AutomationTask>();
        foreach (var task in appointment.Tasks.OrderBy(task => task.Priority))
        {
            var code = task.CourseCode.Trim();
            if (!task.Enabled || selected.Contains(task.Id) || code.Length == 0 || !keys.Add((appointment.StudentId, turnId, code)))
            {
                continue;
            }

            var imported = CopyTask(task, newId: true);
            imported.StudentId = appointment.StudentId;
            imported.TurnId = turnId;
            imported.TurnName = appointment.ExecutedTurnName;
            imported.CourseCode = code;
            imported.Priority = checked(++priority);
            imported.Status = "等待中";
            additions.Add(imported);
        }

        if (monitoring.Tasks is null)
        {
            monitoring.Tasks = tasks;
        }

        tasks.AddRange(additions);
        return additions.Count;
    }

    public static DateTimeOffset CreateBeijingTime(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, BeijingTimeZone.GetUtcOffset(local));
    }

    public static AutomationTask CopyTask(AutomationTask task, bool newId = false) => new()
    {
        Id = newId ? Guid.NewGuid() : task.Id,
        StudentId = task.StudentId,
        TurnId = task.TurnId,
        TurnName = task.TurnName ?? string.Empty,
        Enabled = task.Enabled,
        CourseCode = task.CourseCode ?? string.Empty,
        CourseName = task.CourseName ?? "待预检查",
        Priority = task.Priority,
        ConflictingCourseCodes = (task.ConflictingCourseCodes ?? []).ToList(),
        Status = task.Status ?? "等待中"
    };

    public static ScheduledSelection Copy(ScheduledSelection appointment) => new()
    {
        Id = appointment.Id,
        CreatedAt = appointment.CreatedAt,
        ExecuteAt = appointment.ExecuteAt,
        RunWhenMissed = appointment.RunWhenMissed,
        StudentId = appointment.StudentId,
        TurnId = appointment.TurnId,
        TurnName = appointment.TurnName ?? string.Empty,
        ExecutedTurnId = appointment.ExecutedTurnId,
        ExecutedTurnName = appointment.ExecutedTurnName ?? string.Empty,
        CompletionAction = appointment.CompletionAction,
        State = appointment.State,
        Tasks = (appointment.Tasks ?? []).Select(task => CopyTask(task)).ToList(),
        RealOperationsAuthorized = appointment.RealOperationsAuthorized,
        ConfirmedSelectedTaskIds = (appointment.ConfirmedSelectedTaskIds ?? []).ToList(),
        SelectionFinished = appointment.SelectionFinished,
        ImportFinished = appointment.ImportFinished,
        StartedAt = appointment.StartedAt,
        FinishedAt = appointment.FinishedAt,
        Result = appointment.Result ?? string.Empty
    };

    private void Transact(Action<ScheduledSelectionSettings> change)
    {
        lock (_sync)
        {
            var candidate = CopySettings(_settings);
            change(candidate);
            ScheduledSelectionStore.Normalize(candidate);
            _store.Save(candidate);
            _settings = candidate;
        }
    }

    private static ScheduledSelectionSettings CopySettings(ScheduledSelectionSettings settings) => new()
    {
        Appointments = settings.Appointments.Select(Copy).ToList()
    };

    private static ScheduledSelection Find(ScheduledSelectionSettings settings, Guid id) =>
        settings.Appointments.FirstOrDefault(item => item.Id == id)
        ?? throw new KeyNotFoundException("预约不存在或已被删除。");

    private static DateTimeOffset InBeijing(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, BeijingTimeZone);

    private static bool NeedsRestartRecovery(ScheduledSelection appointment) =>
        appointment.State is ScheduledSelectionState.Queued or ScheduledSelectionState.Running
        || (appointment.StartedAt is not null && appointment.FinishedAt is null
            && appointment.State is not (ScheduledSelectionState.Interrupted or ScheduledSelectionState.Failed));

    private static void RequireState(ScheduledSelection appointment, params ScheduledSelectionState[] allowed)
    {
        if (!allowed.Contains(appointment.State))
        {
            throw new InvalidOperationException($"预约当前状态为 {appointment.State}，不能执行该操作。");
        }
    }

    private static void RequireExecuting(ScheduledSelection appointment)
    {
        RequireState(appointment, ScheduledSelectionState.Running);
        if (appointment.SelectionFinished)
        {
            throw new InvalidOperationException("本次选课阶段已经结束，禁止重新执行或修改课程结果。");
        }
    }

    private static void AssignExecutedTurn(ScheduledSelection appointment, TurnInfo turn)
    {
        if (appointment.TurnId is { } selected && selected != turn.Id
            || appointment.ExecutedTurnId is { } executed && executed != turn.Id)
        {
            throw new InvalidOperationException("不能改变预约已指定或已开始执行的轮次。");
        }

        appointment.ExecutedTurnId = turn.Id;
        appointment.ExecutedTurnName = $"{turn.DisplayText}（{turn.Id}）";
    }

    private static void UpdateProgress(ScheduledSelection appointment, List<AutomationTask> tasks, IEnumerable<Guid> confirmed)
    {
        var taskIds = appointment.Tasks.Select(task => task.Id).ToHashSet();
        if (tasks.Count != taskIds.Count || tasks.Select(task => task.Id).Distinct().Count() != taskIds.Count
            || tasks.Any(task => !taskIds.Contains(task.Id)))
        {
            throw new InvalidOperationException("运行中不能增删预约课程。");
        }

        appointment.Tasks = tasks;
        appointment.ConfirmedSelectedTaskIds = appointment.ConfirmedSelectedTaskIds.Concat(confirmed)
            .Where(taskIds.Contains).Distinct().ToList();
        foreach (var task in appointment.Tasks.Where(task => appointment.ConfirmedSelectedTaskIds.Contains(task.Id)))
        {
            task.Enabled = false;
        }
    }

    private static void ResetExecution(ScheduledSelection appointment)
    {
        appointment.State = ScheduledSelectionState.Draft;
        appointment.RealOperationsAuthorized = false;
        appointment.ExecutedTurnId = null;
        appointment.ExecutedTurnName = string.Empty;
        appointment.SelectionFinished = false;
        appointment.ImportFinished = false;
        appointment.StartedAt = null;
        appointment.FinishedAt = null;
        appointment.ConfirmedSelectedTaskIds = [];
        appointment.Result = string.Empty;
    }

    private static void ValidateForArming(ScheduledSelection appointment, DateTimeOffset now)
    {
        if (appointment.Id == Guid.Empty || appointment.StudentId <= 0)
        {
            throw new InvalidOperationException("启用预约前必须验证所属账号。");
        }

        if (!appointment.RealOperationsAuthorized)
        {
            throw new InvalidOperationException("请先勾选真实选退课授权并确认预约。");
        }

        if (appointment.ExecuteAt <= now)
        {
            throw new InvalidOperationException("预约时间必须晚于当前北京时间。");
        }

        if (appointment.TurnId is <= 0)
        {
            throw new InvalidOperationException("指定选课轮次无效。");
        }

        var enabled = appointment.Tasks.Where(task => task.Enabled).ToArray();
        if (enabled.Length == 0)
        {
            throw new InvalidOperationException("请至少添加并启用一门有效课程。");
        }

        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in enabled)
        {
            var code = task.CourseCode.Trim();
            if (code.Length == 0 || code.Any(character => char.IsWhiteSpace(character) || character is ',' or '，' or ';' or '；'))
            {
                throw new InvalidOperationException("预约课程必须填写一个完整的教学班代码。");
            }

            if (task.ConflictingCourseCodes.Any(conflict => code.Equals(conflict.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"课程 {code} 的冲突旧课不能包含目标课自身。");
            }

            if (!codes.Add(code))
            {
                throw new InvalidOperationException($"教学班 {code} 存在重复启用任务。");
            }
        }
    }

    private sealed class TaskKeyComparer : IEqualityComparer<(long?, long?, string)>
    {
        public static TaskKeyComparer Instance { get; } = new();
        public bool Equals((long?, long?, string) x, (long?, long?, string) y) =>
            x.Item1 == y.Item1 && x.Item2 == y.Item2 && StringComparer.OrdinalIgnoreCase.Equals(x.Item3, y.Item3);
        public int GetHashCode((long?, long?, string) value) =>
            HashCode.Combine(value.Item1, value.Item2, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Item3));
    }
}
