using UstcCourseAssistant.Route2.Models;
using UstcCourseAssistant.Route2.Services;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json;
using IOPath = System.IO.Path;

Environment.SetEnvironmentVariable(
    "USTC_ROUTE2_TEST_DATA_ROOT",
    IOPath.Combine(FindRepositoryRoot(), "artifacts", "engine-checks", Guid.NewGuid().ToString("N")));

try
{
    var skipUi = args.Contains("--skip-ui", StringComparer.OrdinalIgnoreCase);
    if (skipUi)
    {
        Console.WriteLine("[跳过] 界面状态回归（CI 托管环境不提供稳定的 WPF 布局与性能条件）");
    }
    else
    {
        RunCheck("界面状态回归", RunUiStateRegressionChecks);
    }

    RunCheck("自动登录脚本安全", CheckAutoLoginScriptSafety);
    RunCheck("偏好设置升级兼容", CheckPreferenceUpgradeCompatibility);
    RunCheck("检查间隔抖动边界", CheckIntervalJitterBounds);
    RunCheck("定时延迟与网络分类", CheckTimerDelayAndNetworkClassification);
    RunCheck("原子配置恢复", CheckAtomicConfigurationRecovery);
    RunCheck("日志脱敏与保留期", CheckLogSanitizationAndRetention);
    await RunCheckAsync("中断操作保护", CheckInterruptedOperationProtectionAsync);
    await RunCheckAsync("严格换课恢复", CheckStrictInterruptedSwapRecoveryAsync);
    RunCheck("损坏事务记录恢复", CheckCorruptJournalRecovery);
    RunCheck("预约持久化与损坏恢复", CheckScheduledStorage);
    RunCheck("预约一次性到期与重启恢复", CheckScheduledLifecycle);
    RunCheck("预约轮次解析", CheckScheduledTurnResolution);
    RunCheck("预约幂等导入与隔离边界", CheckScheduledImport);
    RunCheck("预约保存失败与导入恢复", CheckScheduledPersistenceFailure);
    await RunCheckAsync("真实操作队列排序与取消", CheckCourseOperationQueueAsync);
    await RunCheckAsync("无余量时禁止写入", NoSeatNeverWritesAsync);
    await RunCheckAsync("成功换课严格串行", SuccessfulSwapIsStrictlySerialAsync);
    await RunCheckAsync("目标失败时恢复旧课", FailedTargetRestoresOldCourseAsync);
    await RunCheckAsync("重复目标禁止写入", DuplicateTargetNeverWritesAsync);
    await RunCheckAsync("高优先级成功后禁用冲突任务", HigherPrioritySelectionDisablesConflictingLowerTaskAsync);
    Console.WriteLine("路线二任务引擎离线场景检查全部通过。");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}

static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(IOPath.Combine(directory.FullName, "UstcCourseAssistant.sln")))
            return directory.FullName;
    }
    throw new DirectoryNotFoundException("找不到仓库根目录，拒绝将检查产物写入系统临时目录。");
}

static string CreateCheckDirectory(string name)
{
    var directory = IOPath.Combine(Environment.GetEnvironmentVariable("USTC_ROUTE2_TEST_DATA_ROOT")!, name + "-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    return directory;
}

static DateTimeOffset ScheduledCheckTime() => new(2026, 11, 1, 8, 59, 59, TimeSpan.FromHours(8));

static ScheduledSelection CreateAppointment(DateTimeOffset executeAt, params AutomationTask[] tasks) => new()
{
    CreatedAt = executeAt.AddHours(-1),
    ExecuteAt = executeAt,
    StudentId = 100,
    RealOperationsAuthorized = true,
    Tasks = tasks.Length == 0 ? [CreateTask("SCHEDULED.01")] : [.. tasks]
};

static void ExpectFailure<TException>(Action action, string message) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

static void CheckScheduledStorage()
{
    var root = CreateCheckDirectory("scheduled-storage");
    var path = IOPath.Combine(root, "appointments.json");
    var store = new ScheduledSelectionStore(path);
    Assert(store.Load().Appointments.Count == 0, "不存在的预约文件应可作为新配置加载");
    var now = ScheduledCheckTime();
    var first = CreateAppointment(now.AddSeconds(1), CreateTask("FIRST.01", "OLD.01"));
    first.State = ScheduledSelectionState.Armed;
    first.RunWhenMissed = true;
    var second = CreateAppointment(now.AddDays(1), CreateTask("SECOND.01"));
    second.State = ScheduledSelectionState.Armed;
    store.Save(new ScheduledSelectionSettings { Appointments = [first, second] });
    var loaded = new ScheduledSelectionStore(path).Load().Appointments;
    Assert(loaded.Count == 2 && loaded[0].ExecuteAt == first.ExecuteAt && loaded[1].ExecuteAt == second.ExecuteAt,
        "独立预约时间没有持久化");
    Assert(loaded[0].RunWhenMissed && !loaded[1].RunWhenMissed && loaded.All(item => item.RealOperationsAuthorized),
        "预约补执行设置或明确授权没有持久化");
    Assert(loaded[0].Tasks.Single().CourseCode == "FIRST.01"
           && loaded[0].Tasks[0].ConflictingCourseCodes.SequenceEqual(["OLD.01"]), "预约课程丢失");

    var queued = CreateAppointment(now.AddSeconds(2));
    queued.State = ScheduledSelectionState.Queued;
    var running = CreateAppointment(now.AddSeconds(3));
    running.State = ScheduledSelectionState.Running;
    running.StartedAt = now;
    var importOnly = CreateAppointment(now.AddSeconds(4));
    importOnly.State = ScheduledSelectionState.Running;
    importOnly.StartedAt = now;
    importOnly.SelectionFinished = true;
    importOnly.CompletionAction = ScheduledCompletionAction.ImportToMonitoring;
    store.Save(new ScheduledSelectionSettings { Appointments = [first, queued, running, importOnly] });
    store.Save(new ScheduledSelectionSettings { Appointments = [second] });
    File.WriteAllText(path, "{corrupt");
    var recoveredStore = new ScheduledSelectionStore(path);
    var recovered = recoveredStore.Load().Appointments;
    Assert(!string.IsNullOrWhiteSpace(recoveredStore.LoadWarning), "备份恢复必须显式告知来源");
    Assert(recovered.Single(item => item.Id == first.Id).State == ScheduledSelectionState.Draft,
        "备份中的启用预约不得自动执行");
    Assert(recovered.Where(item => item.Id != first.Id).All(item => item.State == ScheduledSelectionState.Interrupted),
        "备份执行阶段必须保守恢复为中断");
    var recoveredService = new ScheduledSelectionService(new ScheduledSelectionStore(path), new CheckTimeProvider(now));
    recoveredService.RestoreAfterRestart(now.AddDays(2));
    Assert(recoveredService.CollectDue(now.AddDays(2), true).Count == 0 && recoveredService.PendingImports.Count == 0,
        "备份恢复不得重放选课或自动导入");

    var brokenPath = IOPath.Combine(root, "unrecoverable.json");
    File.WriteAllText(brokenPath, "broken-main");
    File.WriteAllText(brokenPath + ".bak", "broken-backup");
    ExpectFailure<InvalidDataException>(() => new ScheduledSelectionStore(brokenPath).Load(),
        "所有配置副本损坏时不得静默加载空预约");
    Assert(File.ReadAllText(brokenPath) == "broken-main" && File.ReadAllText(brokenPath + ".bak") == "broken-backup",
        "无法恢复时必须保留损坏文件供核对");
}

static void CheckScheduledLifecycle()
{
    var now = ScheduledCheckTime();
    var clock = new CheckTimeProvider(now);
    var path = IOPath.Combine(CreateCheckDirectory("scheduled-lifecycle"), "appointments.json");
    var service = new ScheduledSelectionService(new ScheduledSelectionStore(path), clock);
    var first = CreateAppointment(now.AddSeconds(1));
    service.Arm(first, clock.GetUtcNow());
    Assert(service.CollectDue(clock.GetUtcNow(), false).Count == 0, "未来预约不得提前入队");
    clock.Advance(TimeSpan.FromSeconds(1));
    Assert(service.CollectDue(clock.GetUtcNow(), false).Select(item => item.Id).SequenceEqual([first.Id]),
        "跨越到期时间没有恰好入队一次");
    Assert(service.CollectDue(clock.GetUtcNow(), false).Count == 0, "同一到期预约重复入队");
    service.MarkRunning(first.Id, clock.GetUtcNow());
    ExpectFailure<InvalidOperationException>(() => service.BeginEdit(first.Id), "运行中预约仍可编辑");
    ExpectFailure<InvalidOperationException>(() => service.Cancel(first.Id), "运行中预约仍可取消");
    ExpectFailure<InvalidOperationException>(() => service.Delete(first.Id), "运行中预约仍可删除");
    var restart = new ScheduledSelectionService(new ScheduledSelectionStore(path), clock);
    restart.RestoreAfterRestart(clock.GetUtcNow());
    Assert(restart.Get(first.Id)!.State == ScheduledSelectionState.Interrupted
           && restart.CollectDue(clock.GetUtcNow(), true).Count == 0, "重启重放了已经开始的预约");

    var missed = CreateAppointment(now.AddMinutes(1));
    var late = CreateAppointment(now.AddMinutes(2));
    late.RunWhenMissed = true;
    var earlierCreated = CreateAppointment(late.ExecuteAt);
    earlierCreated.CreatedAt = late.CreatedAt.AddMinutes(-1);
    earlierCreated.RunWhenMissed = true;
    var latest = CreateAppointment(now.AddMinutes(3));
    latest.RunWhenMissed = true;
    foreach (var item in new[] { latest, late, missed, earlierCreated })
        restart.Arm(item, clock.GetUtcNow());
    clock.Advance(TimeSpan.FromDays(1));
    var due = restart.CollectDue(clock.GetUtcNow(), true);
    Assert(due.Select(item => item.Id).SequenceEqual([earlierCreated.Id, late.Id, latest.Id]),
        "过期预约未按执行时间、创建时间稳定排序");
    Assert(restart.Get(missed.Id)!.State == ScheduledSelectionState.Missed, "未授权补执行的过期预约没有转为错过");
    Assert(restart.CollectDue(clock.GetUtcNow(), true).Count == 0, "重复恢复扫描重复入队");
    var queuedRestart = new ScheduledSelectionService(new ScheduledSelectionStore(path), clock);
    queuedRestart.RestoreAfterRestart(clock.GetUtcNow());
    Assert(due.All(item => queuedRestart.Get(item.Id)!.State == ScheduledSelectionState.Interrupted),
        "重启时已持久化入队的预约被重放");

    var editable = CreateAppointment(clock.GetUtcNow().AddHours(1));
    queuedRestart.Arm(editable, clock.GetUtcNow());
    var snapshot = queuedRestart.Get(editable.Id)!;
    snapshot.Tasks.Clear();
    Assert(queuedRestart.Get(editable.Id)!.Tasks.Count == 1, "预约快照暴露了服务内部可变集合");
    queuedRestart.BeginEdit(editable.Id);
    Assert(new ScheduledSelectionStore(path).Load().Appointments.Single(item => item.Id == editable.Id).State == ScheduledSelectionState.Draft,
        "编辑启用预约前没有持久化撤销启用");
    queuedRestart.Arm(editable, clock.GetUtcNow());
    queuedRestart.Cancel(editable.Id);
    Assert(queuedRestart.Get(editable.Id)!.State == ScheduledSelectionState.Cancelled,
        "取消预约没有持久化终态");
    Assert(queuedRestart.CollectDue(clock.GetUtcNow().AddHours(2), false).Count == 0,
        "已取消预约仍然进入到期队列");

    var closePath = IOPath.Combine(CreateCheckDirectory("scheduled-close"), "appointments.json");
    var closing = new ScheduledSelectionService(new ScheduledSelectionStore(closePath), new CheckTimeProvider(now));
    closing.Arm(first, now);
    closing.CollectDue(now, false);
    closing.CollectDue(now.AddSeconds(1), false);
    closing.RestoreQueuedToArmed();
    Assert(new ScheduledSelectionStore(closePath).Load().Appointments.Single().State == ScheduledSelectionState.Armed,
        "正常退出未将尚未开始的预约原子恢复为 Armed");

    var delayedPath = IOPath.Combine(CreateCheckDirectory("scheduled-delayed-tick"), "appointments.json");
    var delayed = new ScheduledSelectionService(new ScheduledSelectionStore(delayedPath), new CheckTimeProvider(now));
    var notAllowed = CreateAppointment(now.AddSeconds(1));
    var allowed = CreateAppointment(now.AddSeconds(1));
    allowed.RunWhenMissed = true;
    delayed.Arm(notAllowed, now);
    delayed.Arm(allowed, now);
    delayed.CollectDue(now, false);
    Assert(delayed.CollectDue(now.AddMinutes(5), false).Select(item => item.Id).SequenceEqual([allowed.Id])
           && delayed.Get(notAllowed.Id).State == ScheduledSelectionState.Missed,
        "运行中显著迟到未遵循预约补执行授权");
}

static void CheckScheduledTurnResolution()
{
    var appointment = CreateAppointment(ScheduledCheckTime().AddHours(1));
    var first = new TurnInfo(200, "第一轮", "秋季");
    var second = new TurnInfo(201, "第二轮", "秋季");
    Assert(ScheduledSelectionService.ResolveTurn(new SessionValidationResult(100, [first]), appointment).Id == first.Id,
        "唯一开放轮次没有被采用");
    ExpectFailure<InvalidOperationException>(() => ScheduledSelectionService.ResolveTurn(new SessionValidationResult(100, []), appointment),
        "无开放轮次时不得执行");
    ExpectFailure<InvalidOperationException>(() => ScheduledSelectionService.ResolveTurn(new SessionValidationResult(100, [first, second]), appointment),
        "多个轮次时不得擅自选择第一项");
    ExpectFailure<InvalidOperationException>(() => ScheduledSelectionService.ResolveTurn(new SessionValidationResult(101, [first]), appointment),
        "账号变化时不得执行");
    appointment.TurnId = second.Id;
    Assert(ScheduledSelectionService.ResolveTurn(new SessionValidationResult(100, [first, second]), appointment).Id == second.Id,
        "指定轮次没有被保留");
    ExpectFailure<InvalidOperationException>(() => ScheduledSelectionService.ResolveTurn(new SessionValidationResult(100, [first]), appointment),
        "指定轮次关闭后不得自动改绑");
}

static void CheckScheduledImport()
{
    var existing = CreateTask(" b.01 ", "ORIGINAL.01");
    existing.StudentId = 100;
    existing.TurnId = 200;
    existing.Priority = 2;
    existing.Enabled = false;
    var otherTurn = CreateTask("A.01");
    otherTurn.StudentId = 100;
    otherTurn.TurnId = 201;
    otherTurn.Priority = 1;
    var a = CreateTask("A.01", "OLD.01");
    a.Priority = 3;
    var b = CreateTask("B.01");
    b.Priority = 4;
    var c = CreateTask("C.01");
    c.Priority = 5;
    var selected = CreateTask("SELECTED.01");
    selected.Priority = 1;
    var excluded = CreateTask("EXCLUDED.01");
    excluded.Priority = 2;
    excluded.Enabled = false;
    var appointment = CreateAppointment(ScheduledCheckTime(), c, b, selected, a, excluded);
    appointment.CompletionAction = ScheduledCompletionAction.ImportToMonitoring;
    appointment.SelectionFinished = true;
    appointment.State = ScheduledSelectionState.Running;
    appointment.ExecutedTurnId = 200;
    appointment.ExecutedTurnName = "执行轮次";
    appointment.ConfirmedSelectedTaskIds = [selected.Id];
    var monitoring = new AutomationSettings { Tasks = [otherTurn, existing] };
    Assert(ScheduledSelectionService.ImportRemainingTasks(appointment, monitoring) == 2, "剩余课程导入数量错误");
    Assert(ReferenceEquals(monitoring.Tasks[0], otherTurn) && ReferenceEquals(monitoring.Tasks[1], existing)
           && !existing.Enabled && existing.Priority == 2 && existing.ConflictingCourseCodes.SequenceEqual(["ORIGINAL.01"]),
        "导入修改了已有课程设置、启用状态或顺序");
    Assert(monitoring.Tasks.Skip(2).Select(task => task.CourseCode).SequenceEqual(["A.01", "C.01"])
           && monitoring.Tasks.Skip(2).Select(task => task.Priority).SequenceEqual([3, 4]), "剩余课程未按预约优先级稳定追加");
    var imported = monitoring.Tasks[2];
    Assert(imported.Id != a.Id && imported.StudentId == 100 && imported.TurnId == 200,
        "导入课程未分配新标识或未绑定实际执行账号轮次");
    imported.ConflictingCourseCodes.Add("LOCAL.01");
    Assert(!a.ConflictingCourseCodes.Contains("LOCAL.01"), "导入课程与预约共享可变冲突集合");
    Assert(ScheduledSelectionService.ImportRemainingTasks(appointment, monitoring) == 0 && monitoring.Tasks.Count == 4,
        "重复导入增加了课程");

    var high = CreateTask("HIGH.01", "LOW.01");
    high.StudentId = 100;
    high.TurnId = 200;
    high.Priority = 1;
    AutomationTask Low(long student, long turn, string code = "LOW.01", params string[] conflicts)
    {
        var task = CreateTask(code, conflicts);
        task.StudentId = student;
        task.TurnId = turn;
        task.Priority = 2;
        return task;
    }
    var same = Low(100, 200);
    var differentTurn = Low(100, 201);
    var differentAccount = Low(101, 200);
    var reverse = Low(100, 200, "REVERSE.01", "HIGH.01");
    AutomationTaskRules.DisableConflictingLowerPriorityTasks([high, same, differentTurn, differentAccount, reverse], high, (_, _, _) => { });
    Assert(!same.Enabled && !reverse.Enabled && differentTurn.Enabled && differentAccount.Enabled,
        "冲突规则未保留双向判断或跨账号、轮次错误停用课程");

    same.Enabled = reverse.Enabled = true;
    var snapshot = new List<AutomationTask> { reverse, differentAccount, same, differentTurn, high };
    var visited = new List<Guid>();
    foreach (var task in snapshot.OrderBy(task => task.Priority))
    {
        if (!task.Enabled) continue;
        visited.Add(task.Id);
        if (task.Id == high.Id)
            AutomationTaskRules.DisableConflictingLowerPriorityTasks(snapshot, task, (_, _, _) => { });
    }
    Assert(visited.Contains(high.Id) && !visited.Contains(same.Id) && !visited.Contains(reverse.Id)
           && visited.Contains(differentTurn.Id) && visited.Contains(differentAccount.Id),
        "有序快照遍历未跳过已由高优先级确认结果排除的同账号同轮次课程");
}

static void CheckScheduledPersistenceFailure()
{
    var now = ScheduledCheckTime();
    var root = CreateCheckDirectory("scheduled-persistence-failure");
    var path = IOPath.Combine(root, "appointments.json");
    var service = new ScheduledSelectionService(new ScheduledSelectionStore(path), new CheckTimeProvider(now));
    service.CollectDue(now, false);
    var appointment = CreateAppointment(now.AddSeconds(1));
    service.UpsertDraft(appointment);
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        ExpectFailure<IOException>(() => service.Arm(appointment, now), "启用保存失败未上报");
        Assert(service.Get(appointment.Id)!.State == ScheduledSelectionState.Draft, "保存失败后仍发布了启用状态");
    }
    service.Arm(appointment, now);
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        ExpectFailure<IOException>(() => service.CollectDue(now.AddSeconds(1), false), "入队保存失败未上报");
        Assert(service.Get(appointment.Id)!.State == ScheduledSelectionState.Armed, "保存失败后仍发布了入队状态");
    }
    service.CollectDue(now.AddSeconds(1), false);
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        ExpectFailure<IOException>(() => service.MarkRunning(appointment.Id, now.AddSeconds(1)), "开始保存失败未上报");
        Assert(service.Get(appointment.Id)!.State == ScheduledSelectionState.Queued, "保存失败后仍发布了开始状态");
    }

    var importPath = IOPath.Combine(root, "import-appointments.json");
    var imports = new ScheduledSelectionService(new ScheduledSelectionStore(importPath), new CheckTimeProvider(now));
    var remaining = CreateAppointment(now.AddSeconds(1), CreateTask("REMAINING.01"));
    remaining.CompletionAction = ScheduledCompletionAction.ImportToMonitoring;
    imports.Arm(remaining, now);
    imports.CollectDue(now, false);
    imports.CollectDue(now.AddSeconds(1), false);
    imports.MarkRunning(remaining.Id, now.AddSeconds(1));
    imports.MarkSelectionFinished(remaining.Id, new TurnInfo(200, "执行轮次", "秋季"), remaining.Tasks, []);
    var monitoringPath = IOPath.Combine(root, "monitoring.json");
    var monitoringStore = new AutomationStore(monitoringPath);
    monitoringStore.Save(new AutomationSettings());
    var monitoring = monitoringStore.Load().Settings;
    ScheduledSelectionService.ImportRemainingTasks(imports.Get(remaining.Id)!, monitoring);
    using (var locked = new FileStream(monitoringPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        ExpectFailure<IOException>(() => monitoringStore.Save(monitoring), "监测配置保存失败未上报");
    Assert(imports.Get(remaining.Id) is { SelectionFinished: true, ImportFinished: false, State: ScheduledSelectionState.Running },
        "监测保存失败后预约被错误标记完成");
    var restored = new ScheduledSelectionService(new ScheduledSelectionStore(importPath), new CheckTimeProvider(now));
    restored.RestoreAfterRestart(now.AddDays(1));
    Assert(restored.CollectDue(now.AddDays(1), true).Count == 0 && restored.PendingImports.Count == 1,
        "选课已结束的重启恢复应只导入而不重新执行");
    var recoveredMonitoring = monitoringStore.Load().Settings;
    Assert(ScheduledSelectionService.ImportRemainingTasks(restored.PendingImports.Single(), recoveredMonitoring) == 1,
        "恢复可写后未完成剩余课程导入");
    monitoringStore.Save(recoveredMonitoring);
    // 模拟监测配置已落盘、预约导入标记尚未落盘时再次重启。
    var secondRestart = new ScheduledSelectionService(new ScheduledSelectionStore(importPath), new CheckTimeProvider(now));
    secondRestart.RestoreAfterRestart(now.AddDays(1));
    var persistedMonitoring = monitoringStore.Load().Settings;
    Assert(ScheduledSelectionService.ImportRemainingTasks(secondRestart.PendingImports.Single(), persistedMonitoring) == 0,
        "阶段间重启重复导入了已持久化课程");
    monitoringStore.Save(persistedMonitoring);
    secondRestart.MarkImportFinished(remaining.Id);
    secondRestart.MarkFinished(remaining.Id, ScheduledSelectionState.Completed, "导入完成", now.AddDays(1));
    Assert(new ScheduledSelectionStore(importPath).Load().Appointments.Single() is { State: ScheduledSelectionState.Completed, ImportFinished: true },
        "恢复导入未持久化最终完成状态");
}

static async Task CheckCourseOperationQueueAsync()
{
    var queue = new CourseOperationQueue();
    var now = ScheduledCheckTime();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var order = new List<string>();
    var active = 0;
    var maximumActive = 0;
    async Task<int> Execute(string name, bool block = false)
    {
        var count = Interlocked.Increment(ref active);
        maximumActive = Math.Max(maximumActive, count);
        try
        {
            order.Add(name);
            if (block)
            {
                entered.TrySetResult();
                await release.Task;
            }
            return order.Count;
        }
        finally { Interlocked.Decrement(ref active); }
    }
    var current = queue.EnqueueAsync(false, now, now, _ => Execute("current", true), CancellationToken.None);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var ordinary = queue.EnqueueAsync(false, now, now, _ => Execute("ordinary"), CancellationToken.None);
    var ordinarySecond = queue.EnqueueAsync(false, now.AddDays(-1), now.AddDays(-1), _ => Execute("ordinary-second"), CancellationToken.None);
    var later = queue.EnqueueAsync(true, now.AddMinutes(1), now, _ => Execute("later"), CancellationToken.None);
    var earlier = queue.EnqueueAsync(true, now, now, _ => Execute("earlier"), CancellationToken.None);
    var sameTimeLaterCreated = queue.EnqueueAsync(true, now, now.AddSeconds(1), _ => Execute("later-created"), CancellationToken.None);
    var sameTimeStable = queue.EnqueueAsync(true, now, now.AddSeconds(1), _ => Execute("stable-tie"), CancellationToken.None);
    using var cancellation = new CancellationTokenSource();
    var cancelled = queue.EnqueueAsync(true, now.AddMinutes(-1), now, _ => Execute("cancelled"), cancellation.Token);
    cancellation.Cancel();
    var alreadyCancelled = queue.EnqueueAsync(false, now, now, _ => Execute("already-cancelled"), cancellation.Token);
    Assert(alreadyCancelled.IsCanceled, "已取消令牌仍创建可执行条目");
    try
    {
        await cancelled.WaitAsync(TimeSpan.FromSeconds(10));
        throw new InvalidOperationException("等待中的取消条目没有取消");
    }
    catch (OperationCanceledException) { }
    var readonlyCompleted = await Task.Run(() => 42).WaitAsync(TimeSpan.FromSeconds(10));
    Assert(readonlyCompleted == 42 && !current.IsCompleted && !ordinary.IsCompleted,
        "等待真实操作时独立只读工作无法完成或队列提前执行");
    release.SetResult();
    await Task.WhenAll(current, ordinary, ordinarySecond, later, earlier, sameTimeLaterCreated, sameTimeStable).WaitAsync(TimeSpan.FromSeconds(10));
    Assert(order.SequenceEqual(["current", "earlier", "later-created", "stable-tie", "later", "ordinary", "ordinary-second"])
           && maximumActive == 1, "队列没有保持完整回调串行、预约优先或稳定排序");

    var runningStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var runningRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var runningCancellation = new CancellationTokenSource();
    var running = queue.EnqueueAsync(false, now, now, async token =>
    {
        runningStarted.SetResult();
        await runningRelease.Task;
        return token.IsCancellationRequested;
    }, runningCancellation.Token);
    await runningStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var followerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var follower = queue.EnqueueAsync(true, now, now, _ =>
    {
        followerStarted.SetResult();
        return Task.FromResult(1);
    }, CancellationToken.None);
    runningCancellation.Cancel();
    Assert(!followerStarted.Task.IsCompleted, "取消运行中条目时下一回调提前进入");
    runningRelease.SetResult();
    Assert(await running.WaitAsync(TimeSpan.FromSeconds(10)), "运行回调没有接收到自身取消令牌");
    await follower.WaitAsync(TimeSpan.FromSeconds(10));
    var failed = queue.EnqueueAsync<int>(false, now, now, _ => throw new InvalidOperationException("expected queue failure"), CancellationToken.None);
    try { await failed.WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("队列丢失回调异常"); }
    catch (InvalidOperationException ex) when (ex.Message == "expected queue failure") { }
    Assert(await queue.EnqueueAsync(false, now, now, _ => Task.FromResult(7), CancellationToken.None)
        .WaitAsync(TimeSpan.FromSeconds(10)) == 7, "回调失败后队列未继续工作");
}

static void RunCheck(string name, Action check)
{
    Console.WriteLine($"[检查] {name}");
    check();
}

static async Task RunCheckAsync(string name, Func<Task> check)
{
    Console.WriteLine($"[检查] {name}");
    await check();
}

static void RunUiStateRegressionChecks()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        UstcCourseAssistant.Route2.App? app = null;
        UstcCourseAssistant.Route2.MainWindow? window = null;
        UstcCourseAssistant.Route2.LoginWindow? loginWindow = null;
        try
        {
            app = new UstcCourseAssistant.Route2.App();
            app.InitializeComponent();
            window = new UstcCourseAssistant.Route2.MainWindow();
            loginWindow = new UstcCourseAssistant.Route2.LoginWindow();
            var windowType = window.GetType();
            var runningField = windowType.GetField("_automationRunning", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var sessionField = windowType.GetField("_sessionValidation", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var turnsField = windowType.GetField("_turns", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var recoveryPendingField = windowType.GetField("_readNetworkRecoveryPending", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var plannedAtField = windowType.GetField("_automationTickPlannedAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var plannedDelayField = windowType.GetField("_automationTickPlannedDelay", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var settingsField = windowType.GetField("_automationSettings", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var tasksField = windowType.GetField("_automationTasks", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var shouldAutoShowOnboardingField = windowType.GetField("_shouldAutoShowOnboarding", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var setBusy = windowType.GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pause = windowType.GetMethod("PauseAutomation", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var runCycle = windowType.GetMethod("RunAutomationCycleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var beginReadNetworkWait = windowType.GetMethod("BeginReadNetworkRecoveryWait", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var timerTick = windowType.GetMethod("AutomationTimer_Tick", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var enabledClick = windowType.GetMethod("AutomationEnabledCheckBox_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var deleteTask = windowType.GetMethod("DeleteAutomationTaskButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var moveTask = windowType.GetMethod("MoveAutomationTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var saveCredential = windowType.GetMethod("SaveCredentialButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var showOnboarding = windowType.GetMethod("ShowOnboarding", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var updateOnboarding = windowType.GetMethod("UpdateOnboardingStep", BindingFlags.Instance | BindingFlags.NonPublic)!;

            Assert(!(bool)runningField.GetValue(window)!, "程序启动后不得自动开始监测");
            Assert(window.MinWidth >= 1040 && window.MinHeight >= 700, "主窗口最小尺寸约束发生变化");

            var operationTabs = (TabControl)window.FindName("OperationTabs");
            var advancedTabs = (TabControl)window.FindName("AdvancedToolsTabs");

            var sessionPanel = (Border)window.FindName("SessionInfoPanel");
            var functionPanel = (Border)window.FindName("FunctionPagesPanel");
            var armPanel = (Border)window.FindName("AutomationArmPanel");
            var armCheckBox = (CheckBox)window.FindName("AutomationArmCheckBox");
            foreach (var name in new[]
                     {
                         "StatusText",
                         "StudentIdText",
                         "NoTurnsText",
                         "AutomationStateText",
                         "SnapshotSummaryText",
                         "MissingTargetText",
                         "ControlledDetailsText",
                         "ControlledResultText"
                     })
            {
                var text = (TextBox)window.FindName(name);
                Assert(text.IsReadOnly, $"文字控件 {name} 不能直接选择复制");
                Assert(!text.IsReadOnlyCaretVisible, $"只读文字控件 {name} 仍会显示插入光标");
            }
            var activityLog = (FlowDocumentScrollViewer)window.FindName("ActivityLogViewer");
            Assert(activityLog.IsSelectionEnabled && activityLog.Cursor == System.Windows.Input.Cursors.Arrow,
                "活动记录必须可跨行选择复制且不能显示插入光标");

            var automationGrid = (DataGrid)window.FindName("AutomationTasksGrid");
            Assert(automationGrid.ClipboardCopyMode == DataGridClipboardCopyMode.IncludeHeader,
                "任务表不再支持 Ctrl+C 复制");
            Assert(automationGrid.SelectionUnit == DataGridSelectionUnit.FullRow
                   && automationGrid.SelectionMode == DataGridSelectionMode.Single,
                "任务表没有恢复单选整行模式");
            Assert(!automationGrid.CanUserDeleteRows,
                "任务表仍允许 DataGrid 默认 Delete 逻辑绕过焦点分流");
            Assert(((DataGrid)window.FindName("TargetsGrid")).ClipboardCopyMode == DataGridClipboardCopyMode.IncludeHeader,
                "课程影子表不再支持 Ctrl+C 复制");
            Assert(((TextBox)loginWindow.FindName("BrowserStatusText")).IsReadOnly,
                "教务窗口状态文字不能选择复制");
            var loginWindowType = loginWindow.GetType();
            var suppressionField = loginWindowType.GetField("_autoLoginSuppressed", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var suppressionMessageField = loginWindowType.GetField("_autoLoginSuppressionMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
            loginWindow.ConfigureAutoLogin(true, () => null);
            loginWindow.Logout();
            Assert((bool)suppressionField.GetValue(loginWindow)!
                   && suppressionMessageField.GetValue(loginWindow)?.ToString()?.Contains("主动退出", StringComparison.Ordinal) == true,
                "主动退出后没有保留明确的自动登录抑制原因");
            loginWindow.ConfigureAutoLogin(true, () => null);
            Assert((bool)suppressionField.GetValue(loginWindow)!,
                "更新凭据或重复配置不应悄悄解除主动退出抑制");
            loginWindow.ConfigureAutoLogin(false, null);
            loginWindow.ConfigureAutoLogin(true, () => null, releaseUserSuppression: true);
            Assert(!(bool)suppressionField.GetValue(loginWindow)!,
                "取消并重新启用自动登录后没有解除主动退出抑制");
            Assert(window.FindName("SavedAccountTextBox") is TextBox
                   && window.FindName("SavedPasswordBox") is PasswordBox
                   && window.FindName("AutoLoginCheckBox") is CheckBox
                   && window.FindName("SaveCredentialButton") is Button
                   && window.FindName("DeleteCredentialButton") is Button,
                "账号与安全页缺少必要控件");
            Assert(((TextBox)window.FindName("CredentialStatusText")).IsReadOnly,
                "凭据状态文字不能选择复制");
            Assert(((CheckBox)window.FindName("AutoLoginCheckBox")).IsChecked != true,
                "自动登录必须默认关闭");

            window.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.ShowInTaskbar = false;
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            window.Show();
            window.UpdateLayout();
            Assert((bool)shouldAutoShowOnboardingField.GetValue(window)!,
                "全新用户没有被标记为需要首次展示新手指引");
            showOnboarding.Invoke(window, null);
            window.UpdateLayout();
            updateOnboarding.Invoke(window, null);
            var onboardingOverlay = (Canvas)window.FindName("OnboardingOverlay");
            var onboardingHelp = (Button)window.FindName("OnboardingHelpButton");
            Assert(onboardingOverlay.Visibility == System.Windows.Visibility.Visible,
                "全新用户首次启动时没有显示新手指引");
            Assert(((TextBox)window.FindName("OnboardingStepText")).Text == "第 1 / 6 步",
                "新手指引不是六步或没有从第一步开始");
            ((Button)window.FindName("OnboardingSkipButton")).RaiseEvent(
                new System.Windows.RoutedEventArgs(Button.ClickEvent));
            Assert(onboardingOverlay.Visibility == System.Windows.Visibility.Collapsed,
                "跳过后新手指引没有关闭");
            onboardingHelp.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            Assert(onboardingOverlay.Visibility == System.Windows.Visibility.Visible,
                "帮助按钮不能重新打开新手指引");
            ((Button)window.FindName("OnboardingSkipButton")).RaiseEvent(
                new System.Windows.RoutedEventArgs(Button.ClickEvent));
            Assert(window.ActualWidth >= window.MinWidth && window.ActualHeight >= window.MinHeight,
                "主窗口无法按最小尺寸完成布局");
            Assert(sessionPanel.ActualWidth >= 280 && functionPanel.ActualWidth >= 600,
                "最小窗口下左侧会话栏或右侧功能页可用空间不足");
            var armTexts = VisualDescendants<TextBox>(armPanel).ToArray();
            Assert(armTexts.Length >= 2 && armTexts.All(text => text.IsReadOnly),
                "黄色确认区的安全提示文字不能直接选择复制");
            var armBottom = armPanel.TranslatePoint(new System.Windows.Point(0, armPanel.ActualHeight), sessionPanel).Y;
            Assert(armBottom <= sessionPanel.ActualHeight + 0.5,
                $"最小窗口下左侧黄色确认区被截断：底部 {armBottom:F1}，会话栏 {sessionPanel.ActualHeight:F1}");
            var armCheckBottom = armCheckBox.TranslatePoint(
                new System.Windows.Point(0, armCheckBox.ActualHeight), sessionPanel).Y;
            Assert(armCheckBox.ActualHeight >= 14 && armCheckBottom <= sessionPanel.ActualHeight - 4,
                $"最小窗口下黄色确认复选框未完整显示：高度 {armCheckBox.ActualHeight:F1}，底部 {armCheckBottom:F1}，会话栏 {sessionPanel.ActualHeight:F1}");

            var taskColumnWidths = automationGrid.Columns.Select(column => column.ActualWidth).ToArray();
            Assert(taskColumnWidths.All(width => width >= 35),
                "最小窗口下任务表存在被截断的列：" + string.Join(", ", taskColumnWidths.Select(width => width.ToString("F1"))));

            window.Width = 1240;
            window.Height = 800;
            window.UpdateLayout();
            var tasks = (IList<AutomationTask>)tasksField.GetValue(window)!;
            tasks.Clear();
            var firstTask = new AutomationTask { CourseCode = "001111.01", CourseName = "第一门", Priority = 1 };
            var secondTask = new AutomationTask { CourseCode = "002222.01", CourseName = "第二门", Priority = 2, Enabled = false };
            var thirdTask = new AutomationTask { CourseCode = "003333.01", CourseName = "第三门", Priority = 3 };
            tasks.Add(firstTask);
            tasks.Add(secondTask);
            tasks.Add(thirdTask);
            automationGrid.SelectedItem = secondTask;
            automationGrid.ScrollIntoView(secondTask);
            automationGrid.UpdateLayout();
            var selectedRow = (DataGridRow?)automationGrid.ItemContainerGenerator.ContainerFromItem(secondTask);
            Assert(selectedRow?.IsSelected == true, "普通任务行没有形成清楚的整行选择状态");

            var enabledColumn = (DataGridTemplateColumn)automationGrid.Columns[0];
            var enabledBox = (CheckBox)enabledColumn.CellTemplate.LoadContent();
            enabledBox.DataContext = secondTask;
            enabledBox.IsChecked = true;
            enabledBox.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateSource();
            enabledClick.Invoke(window, [enabledBox, new System.Windows.RoutedEventArgs()]);
            Assert(secondTask.Enabled && ReferenceEquals(automationGrid.SelectedItem, secondTask),
                "启用复选框没有同时切换状态并选中所在行");

            moveTask.Invoke(window, [-1]);
            Assert(ReferenceEquals(tasks[0], secondTask) && secondTask.Priority == 1,
                "优先级上移没有作用于当前整行选中任务");
            moveTask.Invoke(window, [1]);
            Assert(ReferenceEquals(tasks[1], secondTask) && secondTask.Priority == 2,
                "优先级下移没有作用于当前整行选中任务");
            deleteTask.Invoke(window, [window, new System.Windows.RoutedEventArgs()]);
            Assert(!tasks.Contains(secondTask) && tasks.Count == 2,
                "删除选中项没有作用于当前整行选中任务");
            automationGrid.SelectedItem = firstTask;

            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            window.UpdateLayout();
            operationTabs.SelectedItem = operationTabs.Items.Cast<TabItem>().Single(tab => ContainsLogicalContent(tab.Content, activityLog));
            window.UpdateLayout();
            Assert(((FlowDocumentScrollViewer)window.FindName("ActivityLogViewer")).ActualHeight >= 100,
                "活动记录页在最小窗口下没有可用空间");
            var addActivity = windowType.GetMethod(
                "AddActivity",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                [typeof(string), typeof(string), typeof(ActivityEventKind)],
                null)!;
            addActivity.Invoke(window, ["底层诊断明细", "信息", ActivityEventKind.Detail]);
            addActivity.Invoke(window, ["TEST.01：选课成功并通过最终状态复核。", "成功", ActivityEventKind.CourseAdd]);
            var importantText = new TextRange(activityLog.Document.ContentStart, activityLog.Document.ContentEnd).Text;
            Assert(importantText.Contains("TEST.01", StringComparison.Ordinal)
                   && !importantText.Contains("底层诊断明细", StringComparison.Ordinal),
                "关键记录没有正确筛选真实写入和普通诊断");
            ((Button)window.FindName("AllActivityButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            var allText = new TextRange(activityLog.Document.ContentStart, activityLog.Document.ContentEnd).Text;
            Assert(allText.Contains("TEST.01", StringComparison.Ordinal)
                   && allText.Contains("底层诊断明细", StringComparison.Ordinal),
                "全部记录没有保留普通诊断信息");
            Assert(activityLog.IsSelectionEnabled
                   && allText.Contains(Environment.NewLine, StringComparison.Ordinal),
                "活动记录不能跨越多行连续选择复制");
            ((Button)window.FindName("ImportantActivityButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            var allActivityEntries = (ObservableCollection<LocalLogEntry>)windowType
                .GetField("_activityEntries", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;
            var allActivityParagraph = (Paragraph)windowType
                .GetField("_allActivityParagraph", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;
            for (var index = 0; index < 20000; index++)
            {
                var entry = new LocalLogEntry(DateTimeOffset.Now, "信息", $"性能测试明细 {index}");
                allActivityEntries.Add(entry);
                allActivityParagraph.Inlines.Add(new Run(entry.DisplayText));
                allActivityParagraph.Inlines.Add(new LineBreak());
            }

            var switchTimer = System.Diagnostics.Stopwatch.StartNew();
            ((Button)window.FindName("AllActivityButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            switchTimer.Stop();
            Assert(switchTimer.Elapsed < TimeSpan.FromSeconds(1),
                $"两万条活动记录切换到全部记录仍然阻塞过久：{switchTimer.Elapsed.TotalMilliseconds:F0} ms");
            switchTimer.Restart();
            ((Button)window.FindName("ImportantActivityButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            switchTimer.Stop();
            Assert(switchTimer.Elapsed < TimeSpan.FromSeconds(1),
                $"两万条活动记录切回关键记录仍然阻塞过久：{switchTimer.Elapsed.TotalMilliseconds:F0} ms");
            switchTimer.Restart();
            ((Button)window.FindName("AllActivityButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            ((Button)window.FindName("ImportantActivityButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            switchTimer.Stop();
            Assert(switchTimer.Elapsed < TimeSpan.FromSeconds(1.5),
                $"缓存后的关键/全部往返切换仍然阻塞过久：{switchTimer.Elapsed.TotalMilliseconds:F0} ms");
            operationTabs.SelectedItem = operationTabs.Items.Cast<TabItem>().Single(tab => ContainsLogicalContent(tab.Content, advancedTabs));
            advancedTabs.SelectedIndex = 0;
            window.UpdateLayout();
            var shadowColumnWidths = ((DataGrid)window.FindName("TargetsGrid")).Columns.Select(column => column.ActualWidth).ToArray();
            Assert(shadowColumnWidths.All(width => width >= 35),
                "最小窗口下课程影子表存在被截断的列：" + string.Join(", ", shadowColumnWidths.Select(width => width.ToString("F1"))));
            advancedTabs.SelectedIndex = 1;
            window.UpdateLayout();
            Assert(((TextBox)window.FindName("ControlledDetailsText")).ActualWidth >= 300,
                "最小窗口下单门操作结果区被截断");
            operationTabs.SelectedItem = operationTabs.Items.Cast<TabItem>().Single(tab =>
                ContainsLogicalContent(tab.Content, window.FindName("SavedAccountTextBox")));
            window.UpdateLayout();
            var accountTextBox = (TextBox)window.FindName("SavedAccountTextBox");
            var passwordBox = (PasswordBox)window.FindName("SavedPasswordBox");
            var autoLoginCheckBox = (CheckBox)window.FindName("AutoLoginCheckBox");
            Assert(accountTextBox.ActualWidth >= 250 && passwordBox.ActualWidth >= 250,
                "最小窗口下账号或密码输入框被截断");
            const string offlineSecret = "offline-secret-7A!";
            accountTextBox.Text = "offline-user";
            passwordBox.Password = offlineSecret;
            saveCredential.Invoke(window, [window, new System.Windows.RoutedEventArgs()]);
            Assert(passwordBox.Password.Length == 0, "保存凭据后密码输入框没有清空");
            Assert(autoLoginCheckBox.IsEnabled && autoLoginCheckBox.IsChecked != true,
                "保存密码后不应自动开启自动登录");
            autoLoginCheckBox.IsChecked = true;
            var testRoot = Environment.GetEnvironmentVariable("USTC_ROUTE2_TEST_DATA_ROOT")!;
            var persistedText = string.Join("\n", Directory.EnumerateFiles(testRoot, "*", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
            Assert(!persistedText.Contains(offlineSecret, StringComparison.Ordinal),
                "密码被写入了普通配置或日志文件");
            Assert(persistedText.Contains("offline-user", StringComparison.Ordinal)
                   && persistedText.Contains("AutoLoginEnabled", StringComparison.Ordinal),
                "账号或自动登录偏好没有保存到普通配置");
            var activityPath = IOPath.Combine(testRoot, "activity.jsonl");
            Assert(!File.Exists(activityPath)
                   || !File.ReadAllText(activityPath).Contains("offline-user", StringComparison.Ordinal),
                "活动日志记录了普通配置中允许保存的账号");
            operationTabs.SelectedIndex = 0;
            window.UpdateLayout();

            if (Environment.GetEnvironmentVariable("USTC_ROUTE2_UI_PREVIEW") is { Length: > 0 } previewPath)
            {
                RenderPreview(previewPath, 1240, 800);
                automationGrid.IsEnabled = false;
                RenderPreview(Suffixed(previewPath, "monitoring-locked"), 1240, 800);
                automationGrid.IsEnabled = true;
                operationTabs.SelectedItem = operationTabs.Items.Cast<TabItem>().Single(tab => ContainsLogicalContent(tab.Content, activityLog));
                RenderPreview(Suffixed(previewPath, "activity"), 1240, 800);
                operationTabs.SelectedItem = operationTabs.Items.Cast<TabItem>().Single(tab => ContainsLogicalContent(tab.Content, advancedTabs));
                advancedTabs.SelectedIndex = 0;
                RenderPreview(Suffixed(previewPath, "shadow"), 1240, 800);
                advancedTabs.SelectedIndex = 1;
                RenderPreview(Suffixed(previewPath, "controlled"), 1240, 800);
                operationTabs.SelectedItem = operationTabs.Items.Cast<TabItem>().Single(tab => ContainsLogicalContent(tab.Content, accountTextBox));
                RenderPreview(Suffixed(previewPath, "account-security"), 1240, 800);
                operationTabs.SelectedIndex = 0;
                RenderPreview(Suffixed(previewPath, "minimum"), window.MinWidth, window.MinHeight);
                showOnboarding.Invoke(window, null);
                window.Width = 1240;
                window.Height = 800;
                window.UpdateLayout();
                updateOnboarding.Invoke(window, null);
                RenderPreview(Suffixed(previewPath, "onboarding"), 1240, 800);
                ((Button)window.FindName("OnboardingSkipButton")).RaiseEvent(
                    new System.Windows.RoutedEventArgs(Button.ClickEvent));

                void RenderPreview(string path, double width, double height)
                {
                    window.Width = width;
                    window.Height = height;
                    window.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(
                        Math.Max(1, (int)Math.Ceiling(window.ActualWidth)),
                        Math.Max(1, (int)Math.Ceiling(window.ActualHeight)),
                        96,
                        96,
                        PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    Directory.CreateDirectory(IOPath.GetDirectoryName(path)!);
                    using var stream = File.Create(path);
                    encoder.Save(stream);
                }

                static string Suffixed(string path, string suffix) =>
                    IOPath.Combine(
                        IOPath.GetDirectoryName(path)!,
                    IOPath.GetFileNameWithoutExtension(path) + "-" + suffix + IOPath.GetExtension(path));
            }

            window.Hide();

            runningField.SetValue(window, true);
            setBusy.Invoke(window, [false]);
            Assert(!Control("TurnSelector").IsEnabled, "监测时轮次输入应锁定");
            Assert(!Control("ControlledLessonCodeText").IsEnabled, "监测时单门课程输入应锁定");

            pause.Invoke(window, ["测试暂停", "测试"]);
            foreach (var name in new[]
                     {
                         "TurnSelector",
                         "ControlledActionSelector",
                         "ControlledLessonCodeText",
                         "AutomationTasksGrid",
                         "AutomationIntervalSelector",
                         "AutomationCustomIntervalText",
                         "AutomationIntervalJitterCheckBox",
                         "AutomationScheduleCheckBox",
                         "AutomationStartTimeText",
                         "AutomationEndTimeText",
                         "RunAtStartupCheckBox",
                         "SavedAccountTextBox",
                         "SavedPasswordBox",
                         "SaveCredentialButton"
                     })
            {
                Assert(Control(name).IsEnabled, $"暂停后控件 {name} 没有恢复");
            }

            var recoveryTurn = new TurnInfo(200, "恢复测试轮次", "测试学期");
            var turns = (System.Collections.ObjectModel.ObservableCollection<TurnInfo>)turnsField.GetValue(window)!;
            turns.Clear();
            turns.Add(recoveryTurn);
            ((ComboBox)window.FindName("TurnSelector")).SelectedItem = recoveryTurn;
            sessionField.SetValue(window, new SessionValidationResult(100, [recoveryTurn]));
            runningField.SetValue(window, true);
            beginReadNetworkWait.Invoke(window, ["离线模拟纯读取网络超时"]);
            Assert((bool)recoveryPendingField.GetValue(window)!
                   && (bool)runningField.GetValue(window)!
                   && ((TextBox)window.FindName("AutomationStateText")).Text == "等待网络恢复"
                   && plannedAtField.GetValue(window) is DateTimeOffset,
                "纯读取网络超时没有进入等待恢复状态或错误停止了监测意图");
            pause.Invoke(window, ["结束网络等待离线测试", "测试"]);

            runningField.SetValue(window, true);
            plannedAtField.SetValue(window, (DateTimeOffset?)(DateTimeOffset.Now - TimeSpan.FromHours(1)));
            plannedDelayField.SetValue(window, TimeSpan.FromSeconds(15));
            timerTick.Invoke(window, [null, EventArgs.Empty]);
            Assert(!(bool)runningField.GetValue(window)!
                   && ((TextBox)window.FindName("StatusText")).Text.Contains("明显晚于计划时间", StringComparison.Ordinal),
                "计时器长时间迟到没有在任何请求前暂停");

            var outsideTime = TimeOnly.FromDateTime(DateTime.Now.AddMinutes(5));
            settingsField.SetValue(window, new AutomationSettings
            {
                EnableSchedule = true,
                StartAt = outsideTime,
                EndAt = outsideTime
            });
            runningField.SetValue(window, true);
            var cycle = (Task)runCycle.Invoke(window, [true])!;
            cycle.GetAwaiter().GetResult();
            Assert(((TextBox)window.FindName("AutomationStateText")).Text == "等待运行时段",
                "运行时段外任务控制区没有显示简短的等待状态");

            runningField.SetValue(window, false);
            return;

            Control Control(string name) => (Control)window.FindName(name);
        }
        catch (Exception ex)
        {
            failure = ex is TargetInvocationException { InnerException: not null } invocation
                ? invocation.InnerException
                : ex;
        }
        finally
        {
            // Reflection checks may fail while their injected monitoring flag is still set.
            // Clear that test-only state so cleanup cannot hide the assertion behind a close prompt.
            window?.GetType().GetField("_automationRunning", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(window, false);
            loginWindow?.CloseForApplication();
            window?.Close();
            app?.Shutdown();
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null)
    {
        throw new InvalidOperationException("界面状态回归检查失败。", failure);
    }
}

static void CheckAutoLoginScriptSafety()
{
    var entry = LoginFormAutomation.BuildTeachingLoginEntryScript();
    var classification = LoginFormAutomation.BuildClassificationScript();
    var submission = LoginFormAutomation.BuildSubmissionScript("user\"name", "p'ass\\word");
    Assert(entry.Contains("id.ustc.edu.cn", StringComparison.Ordinal)
           && entry.Contains("统一身份认证", StringComparison.Ordinal)
           && entry.Contains("target.click()", StringComparison.Ordinal),
        "教务登录入口脚本不能识别并进入统一身份认证");
    Assert(!entry.Contains("password", StringComparison.OrdinalIgnoreCase)
           && !entry.Contains("username", StringComparison.OrdinalIgnoreCase),
        "教务登录入口脚本不应接触账号密码字段");
    Assert(classification.Contains("verificationInput", StringComparison.Ordinal)
           && classification.Contains("新设备", StringComparison.Ordinal),
        "登录页识别脚本没有覆盖验证页面");
    Assert(submission.IndexOf("verificationInput", StringComparison.Ordinal)
           < submission.IndexOf("setValue(accountInput", StringComparison.Ordinal),
        "自动填写脚本没有在写入账号密码前检查验证字段");
    Assert(submission.Contains("user\\u0022name", StringComparison.Ordinal)
           && submission.Contains("p\\u0027ass\\\\word", StringComparison.Ordinal)
           && !submission.Contains("user\"name", StringComparison.Ordinal),
        "账号密码没有安全编码进登录脚本");
    Assert(LoginFormAutomation.ParseResult("\"verification\"") == "verification"
           && LoginFormAutomation.ParseResult("not-json") == "other",
        "登录页脚本结果解析不符合安全默认值");
    Assert(AutoLoginNavigationPolicy.MaximumPageProbeAttempts is > 1 and <= 20
           && AutoLoginNavigationPolicy.PageProbeDelay >= TimeSpan.FromMilliseconds(200),
        "异步登录页探测不是有限、节制的重试");
    Assert(AutoLoginNavigationPolicy.IsTeachingLoginEntry(new Uri("https://jw.ustc.edu.cn/login?service=test"))
           && !AutoLoginNavigationPolicy.IsTeachingLoginEntry(new Uri("http://jw.ustc.edu.cn/login"))
           && !AutoLoginNavigationPolicy.IsTeachingLoginEntry(new Uri("https://jw.ustc.edu.cn/other"))
           && AutoLoginNavigationPolicy.IsIdentityProvider(new Uri("https://id.ustc.edu.cn/cas/login"))
           && !AutoLoginNavigationPolicy.IsIdentityProvider(new Uri("https://id.ustc.edu.cn.evil.test/login")),
        "自动登录允许域名或入口路径边界不正确");
}

static void CheckPreferenceUpgradeCompatibility()
{
    var testRoot = Environment.GetEnvironmentVariable("USTC_ROUTE2_TEST_DATA_ROOT")!;
    var preferencePath = IOPath.Combine(testRoot, "preferences.json");
    Directory.CreateDirectory(testRoot);
    File.WriteAllText(preferencePath, "{\"KeepLoginState\":false}");

    var migrated = LocalPreferences.Load();
    Assert(!migrated.KeepLoginState
           && migrated.SavedAccount.Length == 0
           && !migrated.AutoLoginEnabled
           && migrated.OnboardingCompleted is null,
        "阶段 D 旧偏好配置不能安全迁移到发布候选版");
    Assert(LocalPreferences.HasExistingInstallationData(),
        "已有偏好文件没有被识别为旧用户数据");

    var current = new LocalPreferences(false, "upgrade-user", true, true);
    current.Save();
    var reloaded = LocalPreferences.Load();
    var json = File.ReadAllText(preferencePath);
    Assert(reloaded == current, "阶段 E 偏好配置重新加载后发生变化");
    Assert(json.Contains("upgrade-user", StringComparison.Ordinal)
           && !json.Contains("Password", StringComparison.OrdinalIgnoreCase),
        "普通偏好配置缺少账号或出现密码字段");
}

static bool ContainsLogicalContent(object? root, object target)
{
    if (ReferenceEquals(root, target)) return true;
    return root is System.Windows.DependencyObject dependency
           && System.Windows.LogicalTreeHelper.GetChildren(dependency).Cast<object>()
               .Any(child => ContainsLogicalContent(child, target));
}

static IEnumerable<T> VisualDescendants<T>(System.Windows.DependencyObject root)
    where T : System.Windows.DependencyObject
{
    for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
    {
        var child = VisualTreeHelper.GetChild(root, index);
        if (child is T match)
        {
            yield return match;
        }

        foreach (var descendant in VisualDescendants<T>(child))
        {
            yield return descendant;
        }
    }
}

static void CheckIntervalJitterBounds()
{
    var fixedDelay = AutomationTiming.CalculateNextDelay(15, false, 0.37);
    Assert(Math.Abs(fixedDelay.TotalSeconds - 15) < 0.001, "关闭抖动后周期不应变化");

    var minimum = AutomationTiming.CalculateNextDelay(15, true, 0);
    var midpoint = AutomationTiming.CalculateNextDelay(15, true, 0.5);
    var maximum = AutomationTiming.CalculateNextDelay(15, true, 1);
    Assert(Math.Abs(minimum.TotalSeconds - 10) < 0.001, "抖动下界不是基准周期的 2/3");
    Assert(Math.Abs(midpoint.TotalSeconds - 15) < 0.001, "抖动中点不等于基准周期");
    Assert(Math.Abs(maximum.TotalSeconds - 20) < 0.001, "抖动上界不是基准周期的 4/3");

    for (var index = 0; index <= 100; index++)
    {
        var sample = AutomationTiming.CalculateNextDelay(5, true, index / 100d).TotalSeconds;
        Assert(sample >= 5d * 2d / 3d - 0.001 && sample <= 5d * 4d / 3d + 0.001, "抖动结果超出 ±1/3 范围");
    }
}

static void CheckTimerDelayAndNetworkClassification()
{
    var delay = TimeSpan.FromSeconds(15);
    var plannedAt = new DateTimeOffset(2026, 8, 29, 0, 25, 5, TimeSpan.FromHours(8));
    Assert(!AutomationTiming.IsSignificantlyLate(plannedAt, plannedAt.AddSeconds(30), delay),
        "计时器在容忍边界上不应误判为休眠");
    Assert(AutomationTiming.IsSignificantlyLate(plannedAt, plannedAt.AddSeconds(30.001), delay),
        "计时器明显迟到没有在任何请求前触发保护");
    Assert(AutomationTiming.CalculateLateTolerance(TimeSpan.FromMinutes(10)) == TimeSpan.FromMinutes(2),
        "长周期的迟到容忍没有限制在两分钟内");
    Assert(!AutomationTiming.IsSignificantlyLate(plannedAt, plannedAt.AddSeconds(-1), delay),
        "系统时间向前校正不应误判为计时器迟到");

    Assert(NetworkFailureClassifier.IsTransientReadFailure(new HttpRequestException("offline"), false),
        "纯读取 HttpRequestException 没有分类为可等待网络异常");
    Assert(NetworkFailureClassifier.IsTransientReadFailure(
            new TaskCanceledException("timeout", new TimeoutException()),
            false),
        "HttpClient 超时没有分类为可等待网络异常");
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    Assert(!NetworkFailureClassifier.IsTransientReadFailure(
            new OperationCanceledException(canceled.Token),
            cancellationRequested: true),
        "用户或安全策略主动取消被误当作纯读取网络超时");
    Assert(!NetworkFailureClassifier.IsTransientReadFailure(new JsonException("changed"), false)
           && !NetworkFailureClassifier.IsTransientReadFailure(new InvalidOperationException("HTTP 500"), false),
        "接口格式或服务器错误不应自动等待并恢复监测");
}

static void CheckAtomicConfigurationRecovery()
{
    var root = CreateCheckDirectory("configuration");
    var path = IOPath.Combine(root, "automation-settings.json");
    var store = new AutomationStore(path);
    store.Save(new AutomationSettings { Tasks = [CreateTask("FIRST.01")] });
    store.Save(new AutomationSettings { Tasks = [CreateTask("SECOND.01")] });
    File.WriteAllText(path, "{损坏的配置");
    var recovered = store.Load();
    Assert(recovered.Warning is not null, "配置损坏时没有给出恢复提示");
    Assert(recovered.Settings.Tasks.Count == 1, "配置损坏后任务全部丢失");
}

static void CheckLogSanitizationAndRetention()
{
    var root = CreateCheckDirectory("logs");
    var path = IOPath.Combine(root, "activity.jsonl");
    Directory.CreateDirectory(root);
    var expired = new LocalLogEntry(DateTimeOffset.Now.AddDays(-15), "旧日志", "password=old-secret");
    var recent = new LocalLogEntry(DateTimeOffset.Now.AddDays(-1), "近期日志", "保留此条");
    File.WriteAllLines(path, [JsonSerializer.Serialize(expired), JsonSerializer.Serialize(recent)]);
    File.WriteAllText(path + ".bak", JsonSerializer.Serialize(expired));
    File.WriteAllText(path + ".tmp-interrupted", JsonSerializer.Serialize(expired));
    var log = new LocalActivityLog(path);
    var retainedBeforeAppend = log.LoadRecent();
    Assert(retainedBeforeAppend.Count == 1 && retainedBeforeAppend[0].Message == "保留此条", "日志没有按最近 14 天清理");
    Assert(!File.ReadAllText(path + ".bak").Contains("old-secret", StringComparison.Ordinal), "日志备份仍保留超过 14 天的敏感内容");
    Assert(!File.ReadAllText(path + ".tmp-interrupted").Contains("old-secret", StringComparison.Ordinal), "中断日志临时文件绕过了 14 天清理");
    log.Append(
        "测试",
        "password=secret cookie=abcdef token=very-secret-value https://example.test/a?ticket=123",
        ActivityEventKind.CourseAdd);
    var entries = log.LoadRecent();
    Assert(entries.Count == 2, "脱敏日志没有正常保存");
    var message = entries[^1].Message;
    Assert(!message.Contains("secret", StringComparison.OrdinalIgnoreCase), "日志泄露了密码或令牌");
    Assert(!message.Contains("ticket=123", StringComparison.OrdinalIgnoreCase), "日志泄露了 URL 参数");
    Assert(entries[0].Kind == ActivityEventKind.Detail
           && entries[^1].Kind == ActivityEventKind.CourseAdd
           && entries[^1].IsKeyEvent
           && entries[^1].IsEmphasized,
        "旧日志兼容或关键写入分类不正确");
}

static async Task CheckInterruptedOperationProtectionAsync()
{
    var root = CreateCheckDirectory("journal");
    var store = new OperationJournalStore(IOPath.Combine(root, "operation-journal.json"));
    var coordinator = new RealOperationCoordinator(store);
    var fake = new FakeSession(selectedIds: []);
    fake.AddLesson(2, "NEW.01", selectedCount: 5, limit: 10);

    using (var lease = await coordinator.EnterAsync("离线中断测试", fake, fake, CancellationToken.None))
    {
        var duplicateBlocked = false;
        try
        {
            using var duplicate = await coordinator.EnterAsync("重复功能", fake, fake, CancellationToken.None);
        }
        catch (RealOperationBusyException)
        {
            duplicateBlocked = true;
        }

        Assert(duplicateBlocked, "同一进程内两个功能仍可同时进入真实操作");
        lease.BeginWorkflow("离线中断测试", 100, 200, "NEW.01", []);
        var result = await lease.ExecuteAndVerifyAsync(
            CourseMutationKind.Add,
            fake.Lesson(2),
            "选目标课 NEW.01",
            CancellationToken.None);
        Assert(result.ExpectedStateReached, "离线中断测试的状态复核失败");
        // 故意不调用 CompleteWorkflow，模拟进程在整个换课流程收尾前中断。
    }

    var restarted = new RealOperationCoordinator(store);
    Assert(restarted.HasUnresolvedOperation, "重启后没有识别未完成操作流程");
    var blocked = false;
    try
    {
        using var unexpected = await restarted.EnterAsync("不应允许", fake, fake, CancellationToken.None);
    }
    catch (UnresolvedOperationException)
    {
        blocked = true;
    }

    Assert(blocked, "存在中断流程时仍允许新的真实操作");
    restarted.AcknowledgeResolved("离线测试人工核对完成");
    Assert(!restarted.HasUnresolvedOperation, "人工核对后保护没有解除");

    fake.SelectedIds.Remove(2);
    fake.FailAddIds.Add(2);
    using (var lease = await restarted.EnterAsync("结果不一致测试", fake, fake, CancellationToken.None))
    {
        lease.BeginWorkflow("结果不一致测试", 100, 200, "NEW.01", []);
        var mismatch = await lease.ExecuteAndVerifyAsync(
            CourseMutationKind.Add,
            fake.Lesson(2),
            "选目标课 NEW.01",
            CancellationToken.None);
        Assert(!mismatch.ExpectedStateReached, "模拟结果不一致没有生效");
        lease.RequireManualReview("离线测试：最终状态与计划不一致");
    }

    Assert(restarted.HasUnresolvedOperation, "最终状态与计划不一致后没有锁定新的真实操作");
    restarted.AcknowledgeResolved("离线测试人工核对不一致结果");

    using (var lease = await restarted.EnterAsync("取消保护测试", fake, fake, CancellationToken.None))
    {
        lease.BeginWorkflow("取消保护测试", 100, 200, "NEW.01", []);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await lease.ExecuteAndVerifyAsync(
                CourseMutationKind.Add,
                fake.Lesson(2),
                "选目标课 NEW.01",
                canceled.Token);
            throw new InvalidOperationException("取消测试没有抛出 OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
        }
    }

    Assert(restarted.HasUnresolvedOperation, "真实操作取消后没有保留结果不确定保护");
    Assert(restarted.ActiveJournal?.CurrentRequestMayHaveBeenSent == true,
        "真实写入取消后没有记录请求可能已发送");
    restarted.AcknowledgeResolved("离线测试人工核对取消结果");

    fake.IncompleteMutationIds.Add(2);
    using (var lease = await restarted.EnterAsync("服务器结果不确定测试", fake, fake, CancellationToken.None))
    {
        lease.BeginWorkflow("服务器结果不确定测试", 100, 200, "NEW.01", []);
        try
        {
            await lease.ExecuteAndVerifyAsync(
                CourseMutationKind.Add,
                fake.Lesson(2),
                "选目标课 NEW.01",
                CancellationToken.None);
            throw new InvalidOperationException("服务器未给出最终结果时没有抛出结果不确定保护");
        }
        catch (WriteResultUncertainException)
        {
        }
    }

    Assert(restarted.HasUnresolvedOperation
           && restarted.ActiveJournal?.CurrentRequestMayHaveBeenSent == true,
        "服务器未给出最终写入结果后没有保持锁定");
    restarted.AcknowledgeResolved("离线测试人工核对服务器不确定结果");
}

static async Task CheckStrictInterruptedSwapRecoveryAsync()
{
    var (coordinator, fake) = await CreateEligibleInterruptedSwapAsync("success");
    Assert(coordinator.HasEligibleInterruptedSwapRecovery,
        "旧课退课已确认且目标请求未发送时没有形成严格恢复资格");
    var restored = await coordinator.TryRestoreConfirmedDropsAfterNetworkAsync(
        100,
        200,
        fake,
        fake,
        CancellationToken.None);
    Assert(restored.Outcome == InterruptedRestoreOutcome.Restored,
        "严格条件成立时没有执行一次旧课恢复");
    Assert(fake.Operations.SequenceEqual(["Drop:OLD.01", "Add:OLD.01"])
           && fake.SelectedIds.SetEquals([1L]),
        "旧课恢复请求不是严格串行一次或最终状态不正确");
    Assert(coordinator.HasUnresolvedOperation
           && coordinator.ActiveJournal is { AutomaticRecoveryEvaluationStarted: true, AutomaticRestoreAttempted: true },
        "自动恢复结束后没有继续保持人工核对锁");
    var repeated = await coordinator.TryRestoreConfirmedDropsAfterNetworkAsync(
        100,
        200,
        fake,
        fake,
        CancellationToken.None);
    Assert(repeated.Outcome == InterruptedRestoreOutcome.NotEligible && fake.Operations.Count == 2,
        "同一个中断流程重复发送了旧课恢复请求");

    var (targetStartedCoordinator, targetStartedFake) = await CreateEligibleInterruptedSwapAsync(
        "target-started",
        markTargetStarted: true);
    Assert(!targetStartedCoordinator.HasEligibleInterruptedSwapRecovery,
        "目标课请求已进入可能发送阶段后仍允许自动恢复");
    var targetStartedResult = await targetStartedCoordinator.TryRestoreConfirmedDropsAfterNetworkAsync(
        100,
        200,
        targetStartedFake,
        targetStartedFake,
        CancellationToken.None);
    Assert(targetStartedResult.Outcome == InterruptedRestoreOutcome.NotEligible
           && targetStartedFake.Operations.SequenceEqual(["Drop:OLD.01"]),
        "无法证明目标请求未发送时仍提交了恢复请求");

    var (identityCoordinator, identityFake) = await CreateEligibleInterruptedSwapAsync("identity");
    var identityMismatch = await identityCoordinator.TryRestoreConfirmedDropsAfterNetworkAsync(
        999,
        200,
        identityFake,
        identityFake,
        CancellationToken.None);
    Assert(identityMismatch.Outcome == InterruptedRestoreOutcome.IdentityMismatch
           && identityFake.Operations.SequenceEqual(["Drop:OLD.01"])
           && !identityCoordinator.HasEligibleInterruptedSwapRecovery,
        "账号不一致时没有永久禁止自动恢复");

    var (targetSelectedCoordinator, targetSelectedFake) = await CreateEligibleInterruptedSwapAsync("target-selected");
    targetSelectedFake.SelectedIds.Add(2);
    var targetSelected = await targetSelectedCoordinator.TryRestoreConfirmedDropsAfterNetworkAsync(
        100,
        200,
        targetSelectedFake,
        targetSelectedFake,
        CancellationToken.None);
    Assert(targetSelected.Outcome == InterruptedRestoreOutcome.StateNotEligible
           && targetSelectedFake.Operations.SequenceEqual(["Drop:OLD.01"]),
        "目标课已经选中时仍错误恢复了旧课");

    var (readFailedCoordinator, readFailedFake) = await CreateEligibleInterruptedSwapAsync("read-failed");
    readFailedFake.ReadFailure = new HttpRequestException("offline read");
    var readFailed = await readFailedCoordinator.TryRestoreConfirmedDropsAfterNetworkAsync(
        100,
        200,
        readFailedFake,
        readFailedFake,
        CancellationToken.None);
    Assert(readFailed.Outcome == InterruptedRestoreOutcome.StateReadFailed
           && readFailedFake.Operations.SequenceEqual(["Drop:OLD.01"])
           && !readFailedCoordinator.HasEligibleInterruptedSwapRecovery,
        "状态读取失败后仍保留自动恢复或重试资格");

    var (writeUnknownCoordinator, writeUnknownFake) = await CreateEligibleInterruptedSwapAsync("write-unknown");
    writeUnknownFake.MutationFailure = new HttpRequestException("unknown write");
    var writeUnknown = await writeUnknownCoordinator.TryRestoreConfirmedDropsAfterNetworkAsync(
        100,
        200,
        writeUnknownFake,
        writeUnknownFake,
        CancellationToken.None);
    Assert(writeUnknown.Outcome == InterruptedRestoreOutcome.ResultUnknown
           && writeUnknownFake.Operations.SequenceEqual(["Drop:OLD.01", "Add:OLD.01"])
           && writeUnknownCoordinator.ActiveJournal?.CurrentRequestMayHaveBeenSent == true,
        "恢复写入结果不确定时没有保持可能已发送标记");
    var unknownRepeated = await writeUnknownCoordinator.TryRestoreConfirmedDropsAfterNetworkAsync(
        100,
        200,
        writeUnknownFake,
        writeUnknownFake,
        CancellationToken.None);
    Assert(unknownRepeated.Outcome == InterruptedRestoreOutcome.NotEligible
           && writeUnknownFake.Operations.Count == 2,
        "结果不确定的恢复请求被自动重试");
}

static async Task<(RealOperationCoordinator Coordinator, FakeSession Fake)> CreateEligibleInterruptedSwapAsync(
    string suffix,
    bool markTargetStarted = false)
{
    var root = CreateCheckDirectory("strict-recovery-" + suffix);
    var coordinator = new RealOperationCoordinator(
        new OperationJournalStore(IOPath.Combine(root, "operation-journal.json")));
    var fake = new FakeSession(selectedIds: [1]);
    fake.AddLesson(1, "OLD.01", selectedCount: 5, limit: 10);
    fake.AddLesson(2, "NEW.01", selectedCount: 5, limit: 10);
    using (var lease = await coordinator.EnterAsync("严格恢复测试", fake, fake, CancellationToken.None))
    {
        lease.BeginWorkflow("严格恢复测试", 100, 200, "NEW.01", ["OLD.01"]);
        var drop = await lease.ExecuteAndVerifyAsync(
            CourseMutationKind.Drop,
            fake.Lesson(1),
            "退旧课 OLD.01",
            CancellationToken.None);
        Assert(drop.ExpectedStateReached, "严格恢复测试未确认旧课退课成功");
        lease.RecordConfirmedDropBeforeTarget(fake.Lesson(1));
        if (markTargetStarted)
        {
            lease.MarkTargetRequestWillStart();
        }
    }

    return (coordinator, fake);
}

static void CheckCorruptJournalRecovery()
{
    var root = CreateCheckDirectory("journal-recovery");
    var path = IOPath.Combine(root, "operation-journal.json");
    var store = new OperationJournalStore(path);
    var journal = new OperationJournal
    {
        Owner = "换课恢复测试",
        TargetCode = "NEW.01",
        CurrentCourseCode = "OLD.01",
        Stage = "退旧课请求即将提交"
    };
    store.Save(journal);
    journal.Stage = "退旧课最终状态已读取";
    store.Save(journal);
    File.WriteAllText(path, "{损坏的操作记录");

    var recovered = new OperationJournalStore(path).Load();
    Assert(recovered.ActiveJournal is { Active: true }, "操作记录损坏后没有保持安全锁");
    Assert(recovered.Warning?.Contains("安全副本", StringComparison.Ordinal) == true, "操作记录损坏后没有明确恢复提示");
    Assert(recovered.ActiveJournal!.TargetCode == "NEW.01", "操作记录损坏后没有恢复涉及课程");
}

static async Task NoSeatNeverWritesAsync()
{
    var fake = new FakeSession(selectedIds: []);
    fake.AddLesson(2, "NEW.01", selectedCount: 10, limit: 10);
    var task = CreateTask("NEW.01");
    var result = await Engine(fake).RunOnceAsync([task], true, CancellationToken.None);
    Assert(!result.ShouldPause, "无余量不应暂停整个监测");
    Assert(fake.Operations.Count == 0, "无余量不得发送写请求");
    Assert(task.Status == "暂无余量", "无余量状态不正确");
}

static async Task SuccessfulSwapIsStrictlySerialAsync()
{
    var fake = new FakeSession(selectedIds: [1]);
    fake.AddLesson(1, "OLD.01", selectedCount: 5, limit: 10);
    fake.AddLesson(2, "NEW.01", selectedCount: 5, limit: 10);
    var task = CreateTask("NEW.01", "OLD.01");
    var result = await Engine(fake).RunOnceAsync([task], true, CancellationToken.None);
    Assert(!result.ShouldPause, "成功换课不应暂停监测");
    Assert(fake.Operations.SequenceEqual(["Drop:OLD.01", "Add:NEW.01"]), "换课写入顺序错误");
    Assert(fake.SelectedIds.SetEquals([2L]), "成功换课后的最终状态错误");
    Assert(!task.Enabled, "成功任务应停用");
}

static async Task FailedTargetRestoresOldCourseAsync()
{
    var fake = new FakeSession(selectedIds: [1]);
    fake.AddLesson(1, "OLD.01", selectedCount: 5, limit: 10);
    fake.AddLesson(2, "NEW.01", selectedCount: 5, limit: 10);
    fake.FailAddIds.Add(2);
    var task = CreateTask("NEW.01", "OLD.01");
    var result = await Engine(fake).RunOnceAsync([task], true, CancellationToken.None);
    Assert(result.ShouldPause, "目标失败后必须暂停监测");
    Assert(fake.Operations.SequenceEqual(["Drop:OLD.01", "Add:NEW.01", "Add:OLD.01"]), "失败恢复顺序错误");
    Assert(fake.SelectedIds.SetEquals([1L]), "目标失败后旧课没有恢复");
    Assert(!task.Enabled, "失败任务必须停用以防下一轮重复操作");
}

static async Task DuplicateTargetNeverWritesAsync()
{
    var fake = new FakeSession(selectedIds: []);
    fake.AddLesson(2, "NEW.01", selectedCount: 5, limit: 10);
    fake.AddLesson(3, "NEW.01", selectedCount: 5, limit: 10);
    var task = CreateTask("NEW.01");
    var result = await Engine(fake).RunOnceAsync([task], true, CancellationToken.None);
    Assert(fake.Operations.Count == 0, "同代码多班不得发送写请求");
    Assert(result.ShouldPause && !task.Enabled, "同代码多班必须停用任务并暂停监测");
}

static async Task HigherPrioritySelectionDisablesConflictingLowerTaskAsync()
{
    var fake = new FakeSession(selectedIds: [2]);
    fake.AddLesson(2, "HIGH.01", selectedCount: 5, limit: 10);
    fake.AddLesson(3, "LOW.01", selectedCount: 5, limit: 10);
    var high = CreateTask("HIGH.01");
    high.Priority = 1;
    var low = CreateTask("LOW.01", "HIGH.01");
    low.Priority = 2;

    var result = await Engine(fake).RunOnceAsync([low, high], true, CancellationToken.None);
    Assert(!result.ShouldPause, "已选高优先级课程不应导致异常暂停");
    Assert(!high.Enabled && !low.Enabled, "高优先级课程已选后没有停用冲突低优先级任务");
    Assert(fake.Operations.Count == 0, "仅处理优先级冲突时不应发送写请求");
}

static AutomationTask CreateTask(string code, params string[] conflicts) => new()
{
    CourseCode = code,
    ConflictingCourseCodes = [.. conflicts]
};

static Route2AutomationEngine Engine(FakeSession fake) => new(fake, new FakeProtectedExecutor(fake), 100, 200, (_, _, _) => { });

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class CheckTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();
    public void Advance(TimeSpan elapsed) => _now += elapsed;
}

sealed class FakeSession(IEnumerable<long> selectedIds) : IAutomationReadClient, IAutomationMutationClient
{
    private readonly Dictionary<long, LessonInfo> _lessons = [];
    public HashSet<long> SelectedIds { get; } = [.. selectedIds];
    public HashSet<long> FailAddIds { get; } = [];
    public HashSet<long> IncompleteMutationIds { get; } = [];
    public List<string> Operations { get; } = [];
    public Exception? ReadFailure { get; set; }
    public Exception? MutationFailure { get; set; }

    public void AddLesson(long id, string code, int selectedCount, int limit)
    {
        _lessons[id] = new LessonInfo(id, code, code.Split('.')[0], code, limit, "测试教师", selectedCount);
    }

    public LessonInfo Lesson(long id) => _lessons[id];

    public Task<ShadowSnapshot> GetShadowSnapshotAsync(
        long studentId,
        long turnId,
        IReadOnlyCollection<string> targetCodes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ReadFailure is { } readFailure)
        {
            throw readFailure;
        }

        var selected = _lessons.Values.Where(item => SelectedIds.Contains(item.Id)).ToArray();
        var targets = _lessons.Values
            .Where(item => targetCodes.Contains(item.Code, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var missing = targetCodes
            .Where(code => targets.All(item => !item.Code.Equals(code, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return Task.FromResult(new ShadowSnapshot(selected, targets, missing, _lessons.Count - selected.Length));
    }

    public Task<ControlledOperationResponse> ExecuteOnceAsync(
        CourseMutationKind kind,
        long studentId,
        long turnId,
        long lessonId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lesson = _lessons[lessonId];
        Operations.Add($"{kind}:{lesson.Code}");
        if (MutationFailure is { } mutationFailure)
        {
            throw mutationFailure;
        }

        if (IncompleteMutationIds.Contains(lessonId))
        {
            return Task.FromResult(new ControlledOperationResponse(kind, false, false, "模拟服务器未完成"));
        }

        var success = kind switch
        {
            CourseMutationKind.Add when FailAddIds.Contains(lessonId) => false,
            CourseMutationKind.Add => SelectedIds.Add(lessonId),
            CourseMutationKind.Drop => SelectedIds.Remove(lessonId),
            _ => false
        };
        return Task.FromResult(new ControlledOperationResponse(kind, true, success, success ? "成功" : "模拟失败"));
    }
}

sealed class FakeProtectedExecutor(FakeSession fake) : IProtectedMutationExecutor
{
    private bool _workflowStarted;

    public void BeginWorkflow(string owner, long studentId, long turnId, string targetCode, IEnumerable<string> relatedCodes)
    {
        if (_workflowStarted)
        {
            throw new InvalidOperationException("模拟保护器检测到并行流程");
        }

        _workflowStarted = true;
    }

    public async Task<VerifiedMutationResult> ExecuteAndVerifyAsync(
        CourseMutationKind kind,
        LessonInfo lesson,
        string stepName,
        CancellationToken cancellationToken)
    {
        var response = await fake.ExecuteOnceAsync(kind, 100, 200, lesson.Id, cancellationToken);
        var snapshot = await fake.GetShadowSnapshotAsync(100, 200, [lesson.Code], cancellationToken);
        var selected = snapshot.SelectedLessons.Any(item => item.Id == lesson.Id);
        var expected = kind == CourseMutationKind.Add;
        return new VerifiedMutationResult(selected == expected, selected, response.Message);
    }

    public void RecordConfirmedDropBeforeTarget(LessonInfo lesson)
    {
    }

    public void MarkTargetRequestWillStart()
    {
    }

    public void CompleteWorkflow(string note) => _workflowStarted = false;

    public void RequireManualReview(string note) => _workflowStarted = false;
}
