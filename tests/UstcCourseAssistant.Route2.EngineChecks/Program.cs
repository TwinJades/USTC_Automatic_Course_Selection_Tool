using UstcCourseAssistant.Route2.Models;
using UstcCourseAssistant.Route2.Services;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
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
    RunCheck("旧预约完整迁移与恢复边界", CheckScheduledLegacyMigration);
    RunCheck("旧监测课程字段清理与保留", CheckLegacyMonitoringTasks);
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
    var path = IOPath.Combine(root, "scheduled-selections.json");
    var store = new ScheduledSelectionStore(path);
    Assert(store.Load().Appointment is null, "不存在的预约文件应加载为空单预约");
    var now = ScheduledCheckTime();
    var appointment = CreateAppointment(now.AddSeconds(1), CreateTask("FIRST.01", "OLD.01"));
    appointment.State = ScheduledSelectionState.Armed;
    appointment.RunWhenMissed = true;
    store.Save(new ScheduledSelectionSettings { Appointment = appointment });
    var loaded = new ScheduledSelectionStore(path).Load().Appointment!;
    Assert(loaded.Id == appointment.Id && loaded.ExecuteAt == appointment.ExecuteAt
           && loaded.RunWhenMissed && loaded.RealOperationsAuthorized
           && loaded.Tasks.Single().ConflictingCourseCodes.SequenceEqual(["OLD.01"]), "单预约内容没有完整持久化");
    using (var json = JsonDocument.Parse(File.ReadAllBytes(path)))
        Assert(json.RootElement.TryGetProperty("Appointment", out _) && !json.RootElement.TryGetProperty("Appointments", out _),
            "新格式仍保存多个预约属性");

    foreach (var state in new[] { ScheduledSelectionState.Armed, ScheduledSelectionState.Queued, ScheduledSelectionState.Running })
    {
        var recoveryPath = IOPath.Combine(root, state + ".json");
        appointment.State = state;
        appointment.StartedAt = state == ScheduledSelectionState.Running ? now : null;
        var recoveryStore = new ScheduledSelectionStore(recoveryPath);
        recoveryStore.Save(new ScheduledSelectionSettings { Appointment = appointment });
        recoveryStore.Save(new ScheduledSelectionSettings { Appointment = null });
        File.WriteAllText(recoveryPath, "{corrupt");
        var restoredStore = new ScheduledSelectionStore(recoveryPath);
        var restored = restoredStore.Load().Appointment!;
        Assert(!string.IsNullOrWhiteSpace(restoredStore.LoadWarning), "备份恢复必须告知来源");
        Assert(restored.State == (state == ScheduledSelectionState.Armed ? ScheduledSelectionState.Draft : ScheduledSelectionState.Interrupted)
               && !restored.RealOperationsAuthorized, "新格式备份恢复没有保守解除执行资格");
        var service = new ScheduledSelectionService(new ScheduledSelectionStore(recoveryPath), new CheckTimeProvider(now));
        service.RestoreAfterRestart(now.AddDays(2));
        Assert(service.CollectDue(now.AddDays(2), true) is null && service.PendingImport is null, "备份恢复重放了选课或导入");
    }

    var brokenPath = IOPath.Combine(root, "unrecoverable.json");
    File.WriteAllText(brokenPath, "broken-main");
    File.WriteAllText(brokenPath + ".bak", "broken-backup");
    ExpectFailure<InvalidDataException>(() => new ScheduledSelectionStore(brokenPath).Load(), "损坏副本不得静默加载为空");
    Assert(File.ReadAllText(brokenPath) == "broken-main" && File.ReadAllText(brokenPath + ".bak") == "broken-backup",
        "无法恢复时必须保留损坏文件");
    foreach (var invalid in new[] { "{\"Appointment\":null,\"Appointments\":[]}", "{\"Appointments\":{}}", "{\"Appointment\":[]}", "{}", "[]" })
    {
        var invalidPath = IOPath.Combine(CreateCheckDirectory("scheduled-structure"), "scheduled-selections.json");
        File.WriteAllText(invalidPath, invalid);
        ExpectFailure<InvalidDataException>(() => new ScheduledSelectionStore(invalidPath).Load(), "非法或歧义结构未被拒绝：" + invalid);
        Assert(File.ReadAllText(invalidPath) == invalid, "非法配置被空配置覆盖");
    }
}

static byte[] WriteLegacyAppointments(string path, params ScheduledSelection[] appointments)
{
    var node = JsonSerializer.SerializeToNode(appointments)!.AsArray();
    foreach (var item in node)
    {
        item!["TurnId"] = 200;
        item["TurnName"] = "旧独立预约轮次";
        foreach (var task in item["Tasks"]!.AsArray())
        {
            task!["StudentId"] = 100;
            task["TurnId"] = 200;
            task["TurnName"] = "旧逐课程轮次";
        }
    }
    var root = new System.Text.Json.Nodes.JsonObject { ["Appointments"] = node };
    var bytes = System.Text.Encoding.UTF8.GetBytes(" \r\n" + root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\r\n");
    File.WriteAllBytes(path, bytes);
    return bytes;
}

static void CheckScheduledLegacyMigration()
{
    var now = ScheduledCheckTime();
    var root = CreateCheckDirectory("scheduled-legacy");
    var path = IOPath.Combine(root, "scheduled-selections.json");
    var completed = CreateAppointment(now.AddDays(-2));
    completed.State = ScheduledSelectionState.Completed;
    completed.FinishedAt = now.AddMinutes(-1);
    var later = CreateAppointment(now.AddHours(2));
    later.State = ScheduledSelectionState.Armed;
    var earliest = CreateAppointment(now.AddHours(1), CreateTask("EARLY.01", "OLD.01"), CreateTask("DISABLED.01"));
    earliest.State = ScheduledSelectionState.Draft;
    earliest.Tasks[0].Priority = 7;
    earliest.Tasks[0].Status = "保留课程状态";
    earliest.Tasks[1].Enabled = false;
    earliest.CompletionAction = ScheduledCompletionAction.ImportToMonitoring;
    earliest.StartedAt = now.AddHours(-1);
    earliest.FinishedAt = now;
    earliest.ExecutedTurnId = 200;
    earliest.ExecutedTurnName = "旧执行轮次";
    earliest.SelectionFinished = earliest.ImportFinished = true;
    earliest.ConfirmedSelectedTaskIds = [earliest.Tasks[0].Id];
    var running = CreateAppointment(now.AddHours(3));
    running.State = ScheduledSelectionState.Running;
    var original = WriteLegacyAppointments(path, completed, later, running, earliest);
    var store = new ScheduledSelectionStore(path);
    var migrated = store.Load().Appointment!;
    Assert(migrated.Id == earliest.Id && migrated.State == ScheduledSelectionState.Draft && !migrated.RealOperationsAuthorized
           && migrated.StartedAt is null && migrated.FinishedAt is null && migrated.ExecutedTurnId is null
           && migrated.ExecutedTurnName.Length == 0 && !migrated.SelectionFinished && !migrated.ImportFinished
           && migrated.ConfirmedSelectedTaskIds.Count == 0, "旧预约迁移未选最早未完成项或未清除执行资格");
    Assert(migrated.ExecuteAt == earliest.ExecuteAt && migrated.CompletionAction == earliest.CompletionAction
           && migrated.Tasks.Select(task => task.Id).SequenceEqual(earliest.Tasks.Select(task => task.Id))
           && migrated.Tasks[0].Priority == 7 && migrated.Tasks[0].Status == "保留课程状态"
           && migrated.Tasks[0].ConflictingCourseCodes.SequenceEqual(["OLD.01"]) && !migrated.Tasks[1].Enabled,
        "迁移损失了时间、课程内容、状态、优先级或冲突设置");
    var backups = Directory.GetFiles(root, "scheduled-selections.json.pre-2.2.1-*.json");
    Assert(backups.Length == 1 && File.ReadAllBytes(backups[0]).SequenceEqual(original), "迁移备份不是完整原始字节");
    Assert(store.LoadWarning?.Contains(IOPath.GetFileName(backups[0]), StringComparison.Ordinal) == true, "迁移警告缺少实际备份名");
    var service = new ScheduledSelectionService(new ScheduledSelectionStore(path), new CheckTimeProvider(now));
    service.RestoreAfterRestart(now.AddDays(3));
    Assert(service.Current?.Id == earliest.Id && service.CollectDue(now.AddDays(3), true) is null && service.PendingImport is null,
        "迁移后的草稿自动执行或导入");
    Assert(Directory.GetFiles(root, "scheduled-selections.json.pre-2.2.1-*.json").Length == 1, "新格式重启重复迁移备份");

    var tiePath = IOPath.Combine(CreateCheckDirectory("scheduled-legacy-ties"), "scheduled-selections.json");
    var older = CreateAppointment(now.AddHours(1));
    older.Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
    older.State = ScheduledSelectionState.Queued;
    var tied = CreateAppointment(older.ExecuteAt);
    tied.Id = Guid.Parse("00000000-0000-0000-0000-000000000002");
    tied.CreatedAt = older.CreatedAt;
    tied.State = ScheduledSelectionState.Running;
    var newer = CreateAppointment(older.ExecuteAt);
    newer.CreatedAt = older.CreatedAt.AddSeconds(1);
    WriteLegacyAppointments(tiePath, newer, tied, older);
    Assert(new ScheduledSelectionStore(tiePath).Load().Appointment?.Id == older.Id, "相同到期时间未按创建时间与 Id 稳定选择");

    var terminalPath = IOPath.Combine(CreateCheckDirectory("scheduled-legacy-terminal"), "scheduled-selections.json");
    var failed = CreateAppointment(now.AddDays(-3));
    failed.State = ScheduledSelectionState.Failed;
    failed.FinishedAt = completed.FinishedAt!.Value.AddMinutes(1);
    WriteLegacyAppointments(terminalPath, failed, completed);
    Assert(new ScheduledSelectionStore(terminalPath).Load().Appointment is { State: ScheduledSelectionState.Draft } terminal
           && terminal.Id == failed.Id, "没有未完成项时未保留最新终态内容为草稿");
    var emptyPath = IOPath.Combine(CreateCheckDirectory("scheduled-legacy-empty"), "scheduled-selections.json");
    WriteLegacyAppointments(emptyPath);
    Assert(new ScheduledSelectionStore(emptyPath).Load().Appointment is null, "旧空数组未加载为空单预约");

    foreach (var suffix in new[] { ".bak", ".tmp-recovery" })
    {
        foreach (var corruptMain in new[] { false, true })
        {
            var recoveryRoot = CreateCheckDirectory("scheduled-legacy-recovery");
            var recoveryPath = IOPath.Combine(recoveryRoot, "scheduled-selections.json");
            if (corruptMain) File.WriteAllText(recoveryPath, "broken-main");
            var bytes = WriteLegacyAppointments(recoveryPath + suffix, later, older, tied);
            var recovered = new ScheduledSelectionStore(recoveryPath).Load().Appointment!;
            Assert(recovered.Id == older.Id && recovered.State == ScheduledSelectionState.Draft,
                "旧候选在选择迁移项前已被恢复状态降级");
            var backup = Directory.GetFiles(recoveryRoot, "scheduled-selections.json.pre-2.2.1-*.json").Single();
            Assert(File.ReadAllBytes(backup).SequenceEqual(bytes), "恢复迁移未备份实际候选原始字节");
            if (corruptMain)
                Assert(Directory.GetFiles(recoveryRoot, "scheduled-selections.json.corrupt-*").Any(file => File.ReadAllText(file) == "broken-main"),
                    "恢复迁移未归档损坏主文件");
        }
    }

    var lockedRoot = CreateCheckDirectory("scheduled-legacy-commit-failure");
    var lockedPath = IOPath.Combine(lockedRoot, "scheduled-selections.json");
    var lockedBytes = WriteLegacyAppointments(lockedPath, earliest);
    WriteLegacyAppointments(lockedPath + ".bak", later);
    using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        ExpectFailure<InvalidDataException>(() => new ScheduledSelectionService(new ScheduledSelectionStore(lockedPath), new CheckTimeProvider(now)),
            "迁移提交失败仍发布服务状态或继续读取其他候选");
        Assert(File.ReadAllBytes(lockedPath).SequenceEqual(lockedBytes), "迁移提交失败破坏原始主文件");
    }
    Assert(Directory.GetFiles(lockedRoot, "scheduled-selections.json.pre-2.2.1-*.json")
        .Any(file => File.ReadAllBytes(file).SequenceEqual(lockedBytes)), "提交失败前未保存完整迁移备份");
    var deniedRoot = CreateCheckDirectory("scheduled-legacy-backup-failure");
    var deniedPath = IOPath.Combine(deniedRoot, "scheduled-selections.json");
    var deniedBytes = WriteLegacyAppointments(deniedPath, earliest);
    WriteLegacyAppointments(deniedPath + ".bak", later);
    var directory = new DirectoryInfo(deniedRoot);
    var originalAcl = System.IO.FileSystemAclExtensions.GetAccessControl(directory);
    var deniedAcl = new System.Security.AccessControl.DirectorySecurity();
    deniedAcl.SetSecurityDescriptorBinaryForm(originalAcl.GetSecurityDescriptorBinaryForm(), System.Security.AccessControl.AccessControlSections.Access);
    using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
    {
        deniedAcl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity.User!,
            System.Security.AccessControl.FileSystemRights.CreateFiles, System.Security.AccessControl.AccessControlType.Deny));
        System.IO.FileSystemAclExtensions.SetAccessControl(directory, deniedAcl);
        try
        {
            ExpectFailure<InvalidDataException>(() => new ScheduledSelectionService(new ScheduledSelectionStore(deniedPath), new CheckTimeProvider(now)),
                "完整备份创建失败仍发布迁移结果");
            Assert(File.ReadAllBytes(deniedPath).SequenceEqual(deniedBytes)
                   && Directory.GetFiles(deniedRoot, "scheduled-selections.json.pre-2.2.1-*.json").Length == 0,
                "备份失败时改变原文件或保存不完整备份");
        }
        finally { System.IO.FileSystemAclExtensions.SetAccessControl(directory, originalAcl); }
    }

    var archivePath = IOPath.Combine(CreateCheckDirectory("scheduled-legacy-archive-failure"), "scheduled-selections.json");
    File.WriteAllText(archivePath, "locked-corrupt-main");
    WriteLegacyAppointments(archivePath + ".bak", earliest);
    WriteLegacyAppointments(archivePath + ".tmp-recovery", later);
    using (var locked = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        ExpectFailure<InvalidDataException>(() => new ScheduledSelectionStore(archivePath).Load(), "损坏主文件归档失败仍继续发布其他候选");
    Assert(File.ReadAllText(archivePath) == "locked-corrupt-main", "归档失败破坏损坏主文件证据");
}

static void CheckLegacyMonitoringTasks()
{
    foreach (var includeOldIdentity in new[] { false, true })
    {
        var path = IOPath.Combine(CreateCheckDirectory("monitoring-legacy"), "monitoring.json");
        var first = CreateTask("SAME.01", "OLD.01");
        first.Priority = 5;
        var second = CreateTask(" same.01 ");
        second.Priority = 9;
        second.Enabled = false;
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new AutomationSettings { Tasks = [first, second] }))!;
        if (includeOldIdentity)
        {
            var rows = json["Tasks"]!.AsArray();
            for (var index = 0; index < rows.Count; index++)
            {
                rows[index]!["StudentId"] = 100;
                rows[index]!["TurnId"] = 200 + index;
                rows[index]!["TurnName"] = "旧轮次" + index;
            }
        }
        File.WriteAllText(path, json.ToJsonString());
        var store = new AutomationStore(path);
        var loaded = store.Load().Settings;
        Assert(loaded.Tasks.Count == 2 && loaded.Tasks.Select(task => task.Id).SequenceEqual([first.Id, second.Id])
               && loaded.Tasks.Select(task => task.Priority).SequenceEqual([5, 9]) && !loaded.Tasks[1].Enabled
               && loaded.Tasks[0].ConflictingCourseCodes.SequenceEqual(["OLD.01"]), "旧监测课程顺序或同代码行被静默丢失");
        store.Save(loaded);
        using var saved = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert(saved.RootElement.GetProperty("Tasks").EnumerateArray().All(row =>
            !row.TryGetProperty("StudentId", out _) && !row.TryGetProperty("TurnId", out _) && !row.TryGetProperty("TurnName", out _)),
            "保存仍写出逐课程账号或轮次字段");
    }
}

static void CheckScheduledLifecycle()
{
    var now = ScheduledCheckTime();
    var clock = new CheckTimeProvider(now);
    var path = IOPath.Combine(CreateCheckDirectory("scheduled-lifecycle"), "scheduled-selections.json");
    var service = new ScheduledSelectionService(new ScheduledSelectionStore(path), clock);
    var first = CreateAppointment(now.AddSeconds(1));
    service.Arm(first, now);
    var saved = File.ReadAllBytes(path);
    var other = CreateAppointment(now.AddHours(1));
    ExpectFailure<InvalidOperationException>(() => service.Arm(other, now), "允许启用第二个预约");
    ExpectFailure<InvalidOperationException>(() => service.UpsertDraft(other), "允许保存第二个预约");
    Assert(service.Current?.Id == first.Id && File.ReadAllBytes(path).SequenceEqual(saved), "拒绝第二个预约时改变原项");
    Assert(service.CollectDue(now, false) is null, "未来预约提前入队");
    clock.Advance(TimeSpan.FromSeconds(1));
    Assert(service.CollectDue(clock.GetUtcNow(), false)?.Id == first.Id, "到期未返回唯一预约");
    Assert(service.CollectDue(clock.GetUtcNow(), false) is null, "同一预约重复入队");
    ExpectFailure<InvalidOperationException>(() => service.BeginEdit(first.Id), "Queued 预约仍可编辑");
    service.MarkRunning(first.Id, clock.GetUtcNow());
    ExpectFailure<InvalidOperationException>(() => service.BeginEdit(first.Id), "运行中预约仍可编辑");
    ExpectFailure<InvalidOperationException>(() => service.Cancel(first.Id), "运行中预约仍可取消");
    ExpectFailure<KeyNotFoundException>(() => service.SetExecutedTurn(other.Id, new TurnInfo(200, "轮次", "学期")), "过期 Id 回调改变当前预约");
    var restart = new ScheduledSelectionService(new ScheduledSelectionStore(path), clock);
    restart.RestoreAfterRestart(clock.GetUtcNow());
    Assert(restart.Current is { State: ScheduledSelectionState.Interrupted } && restart.CollectDue(clock.GetUtcNow(), true) is null,
        "重启重放已开始预约");
    restart.BeginEdit(first.Id);
    var edited = restart.Current!;
    Assert(edited.State == ScheduledSelectionState.Draft && !edited.RealOperationsAuthorized && edited.ExecuteAt == first.ExecuteAt,
        "终态编辑未清除授权或未保留时间");
    edited.ExecuteAt = now.AddHours(1);
    ExpectFailure<InvalidOperationException>(() => restart.Arm(edited, now), "终态修改后未重新授权仍可启用");
    edited.RealOperationsAuthorized = true;
    restart.Arm(edited, now);
    var snapshot = restart.Current!;
    snapshot.Tasks.Clear();
    Assert(restart.Current!.Tasks.Count == 1, "Current 暴露内部可变集合");
    restart.BeginEdit(first.Id);
    Assert(new ScheduledSelectionStore(path).Load().Appointment is { State: ScheduledSelectionState.Draft, RealOperationsAuthorized: false },
        "编辑 Armed 前未持久化解除授权");
    restart.Arm(edited, now);
    restart.Cancel(first.Id);
    Assert(restart.Current is { State: ScheduledSelectionState.Cancelled } && restart.CollectDue(now.AddHours(2), false) is null,
        "取消后仍到期执行");

    foreach (var state in new[] { ScheduledSelectionState.Completed, ScheduledSelectionState.Failed, ScheduledSelectionState.Missed,
                 ScheduledSelectionState.Cancelled, ScheduledSelectionState.Interrupted })
    {
        var terminalPath = IOPath.Combine(CreateCheckDirectory("scheduled-terminal-edit"), "scheduled-selections.json");
        var terminal = CreateAppointment(now.AddHours(-1), CreateTask("KEPT.01"));
        terminal.State = state;
        terminal.Tasks[0].Enabled = false;
        new ScheduledSelectionStore(terminalPath).Save(new ScheduledSelectionSettings { Appointment = terminal });
        var editable = new ScheduledSelectionService(new ScheduledSelectionStore(terminalPath), clock);
        editable.BeginEdit(terminal.Id);
        Assert(editable.Current is { State: ScheduledSelectionState.Draft, RealOperationsAuthorized: false } draft
               && draft.ExecuteAt == terminal.ExecuteAt && !draft.Tasks[0].Enabled, "终态编辑自动启用课程或丢失原时间");
    }

    var closePath = IOPath.Combine(CreateCheckDirectory("scheduled-close"), "scheduled-selections.json");
    var closing = new ScheduledSelectionService(new ScheduledSelectionStore(closePath), new CheckTimeProvider(now));
    closing.Arm(first, now);
    closing.CollectDue(now, false);
    closing.CollectDue(now.AddSeconds(1), false);
    closing.RestoreQueuedToArmed();
    Assert(new ScheduledSelectionStore(closePath).Load().Appointment is { State: ScheduledSelectionState.Armed }, "正常退出未恢复 Queued 为 Armed");
    foreach (var runWhenMissed in new[] { false, true })
    {
        var delayedPath = IOPath.Combine(CreateCheckDirectory("scheduled-delayed"), "scheduled-selections.json");
        var delayed = new ScheduledSelectionService(new ScheduledSelectionStore(delayedPath), new CheckTimeProvider(now));
        var item = CreateAppointment(now.AddSeconds(1));
        item.RunWhenMissed = runWhenMissed;
        delayed.Arm(item, now);
        delayed.CollectDue(now, false);
        Assert((delayed.CollectDue(now.AddMinutes(5), false) is not null) == runWhenMissed,
            "显著迟到未遵循补执行授权");
        var restarted = new ScheduledSelectionService(new ScheduledSelectionStore(delayedPath), clock);
        restarted.RestoreAfterRestart(now.AddDays(1));
        Assert(restarted.CollectDue(now.AddDays(1), true) is null && restarted.Current!.State ==
               (runWhenMissed ? ScheduledSelectionState.Interrupted : ScheduledSelectionState.Missed), "Queued 重启重放或错过状态丢失");
    }
    var draftPath = IOPath.Combine(CreateCheckDirectory("scheduled-empty-draft"), "scheduled-selections.json");
    var drafts = new ScheduledSelectionService(new ScheduledSelectionStore(draftPath), clock);
    var empty = new ScheduledSelection { ExecuteAt = now.AddHours(1) };
    drafts.UpsertDraft(empty);
    Assert(drafts.Current is { StudentId: 0, Tasks.Count: 0 }, "无会话空课程草稿不能保存");
    var incomplete = drafts.Current!;
    incomplete.Tasks.Add(new AutomationTask());
    drafts.UpsertDraft(incomplete);
    Assert(drafts.Current!.Tasks.Count == 1, "未完成课程行草稿不能保存");
    foreach (var mutation in new Action<ScheduledSelection>[]
             { item => item.Tasks.ForEach(task => task.Enabled = false), item => item.ExecuteAt = now,
               item => item.Tasks.Add(CreateTask(" scheduled.01 ")), item => item.Tasks[0].ConflictingCourseCodes.Add("SCHEDULED.01") })
    {
        var invalid = CreateAppointment(now.AddHours(1));
        mutation(invalid);
        var invalidService = new ScheduledSelectionService(new ScheduledSelectionStore(
            IOPath.Combine(CreateCheckDirectory("scheduled-invalid-arm"), "scheduled-selections.json")), clock);
        ExpectFailure<InvalidOperationException>(() => invalidService.Arm(invalid, now), "非法启用内容没有被校验");
    }
}

static void CheckScheduledTurnResolution()
{
    var first = new TurnInfo(200, "第一轮", "秋季");
    var second = new TurnInfo(201, "第二轮", "秋季");
    Assert(ScheduledSelectionService.ResolveTurn(new SessionValidationResult(100, [first]), 100, null).Id == first.Id,
        "单开放轮次未默认选中");
    Assert(ScheduledSelectionService.ResolveTurn(new SessionValidationResult(100, [first, second]), 100, null).Id == first.Id,
        "无原选择时未默认第一开放轮次");
    Assert(ScheduledSelectionService.ResolveTurn(new SessionValidationResult(100, [first, second]), 100, second.Id).Id == second.Id,
        "全局第二轮选择未保留");
    ExpectFailure<InvalidOperationException>(() => ScheduledSelectionService.ResolveTurn(new SessionValidationResult(100, []), 100, null), "无开放轮次未失败");
    ExpectFailure<InvalidOperationException>(() => ScheduledSelectionService.ResolveTurn(new SessionValidationResult(101, [first]), 100, null), "账号变化未失败");
    ExpectFailure<InvalidOperationException>(() => ScheduledSelectionService.ResolveTurn(new SessionValidationResult(100, [first]), 100, second.Id), "已选轮次关闭后改为第一轮");
}

static void CheckScheduledImport()
{
    var a = CreateTask("A.01", "KEPT.01");
    a.Priority = 2;
    var b = CreateTask(" b.01 ", "ORIGINAL.01");
    b.Priority = 10;
    b.Enabled = false;
    var c = CreateTask("C.01", "OLD.01");
    c.Priority = 3;
    var d = CreateTask("D.01");
    d.Priority = 5;
    var selected = CreateTask("SELECTED.01");
    var excluded = CreateTask("EXCLUDED.01");
    excluded.Enabled = false;
    var appointment = CreateAppointment(ScheduledCheckTime(), d, CreateTask("B.01"), selected, c, excluded);
    appointment.CompletionAction = ScheduledCompletionAction.ImportToMonitoring;
    appointment.SelectionFinished = true;
    appointment.State = ScheduledSelectionState.Running;
    appointment.ExecutedTurnId = 200;
    appointment.ExecutedTurnName = "执行轮次";
    appointment.ConfirmedSelectedTaskIds = [selected.Id];
    var monitoring = new AutomationSettings { Tasks = [a, b] };
    Assert(ScheduledSelectionService.ImportRemainingTasks(appointment, monitoring) == 2, "导入数量未排除已存在、成功和冲突停用行");
    Assert(ReferenceEquals(monitoring.Tasks[0], a) && ReferenceEquals(monitoring.Tasks[1], b) && !b.Enabled
           && b.Priority == 10 && b.ConflictingCourseCodes.SequenceEqual(["ORIGINAL.01"]), "导入改变既有行顺序或设置");
    Assert(monitoring.Tasks.Skip(2).Select(task => task.CourseCode).SequenceEqual(["C.01", "D.01"])
           && monitoring.Tasks.Skip(2).Select(task => task.Priority).SequenceEqual([11, 12]), "追加未按预约优先级和既有最大优先级排序");
    var imported = monitoring.Tasks[2];
    Assert(imported.Id != c.Id, "导入未分配新监测任务 Id");
    imported.ConflictingCourseCodes.Add("LOCAL.01");
    Assert(!c.ConflictingCourseCodes.Contains("LOCAL.01"), "导入共享可变课程集合");
    Assert(ScheduledSelectionService.ImportRemainingTasks(appointment, monitoring) == 0 && monitoring.Tasks.Count == 4, "重复导入不幂等");

    var high = CreateTask("HIGH.01", "LOW.01");
    high.Priority = 1;
    var low = CreateTask("LOW.01");
    low.Priority = 2;
    var reverse = CreateTask("REVERSE.01", "HIGH.01");
    reverse.Priority = 3;
    var unrelated = CreateTask("UNRELATED.01");
    unrelated.Priority = 4;
    var snapshot = new List<AutomationTask> { low, reverse, unrelated, high };
    var visited = new List<Guid>();
    foreach (var task in snapshot.OrderBy(task => task.Priority))
    {
        if (!task.Enabled) continue;
        visited.Add(task.Id);
        if (task.Id == high.Id)
            AutomationTaskRules.DisableConflictingLowerPriorityTasks(snapshot, task, (_, _, _) => { });
    }
    Assert(visited.SequenceEqual([high.Id, unrelated.Id]) && !low.Enabled && !reverse.Enabled,
        "单执行列表的双向冲突停用或同轮遍历跳过失败");
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
    Assert(restored.CollectDue(now.AddDays(1), true) is null && restored.PendingImport?.Id == remaining.Id,
        "选课已结束的重启恢复应只导入而不重新执行");
    var recoveredMonitoring = monitoringStore.Load().Settings;
    Assert(ScheduledSelectionService.ImportRemainingTasks(restored.PendingImport!, recoveredMonitoring) == 1,
        "恢复可写后未完成剩余课程导入");
    monitoringStore.Save(recoveredMonitoring);
    // 模拟监测配置已落盘、预约导入标记尚未落盘时再次重启。
    var secondRestart = new ScheduledSelectionService(new ScheduledSelectionStore(importPath), new CheckTimeProvider(now));
    secondRestart.RestoreAfterRestart(now.AddDays(1));
    var persistedMonitoring = monitoringStore.Load().Settings;
    Assert(ScheduledSelectionService.ImportRemainingTasks(secondRestart.PendingImport!, persistedMonitoring) == 0,
        "阶段间重启重复导入了已持久化课程");
    monitoringStore.Save(persistedMonitoring);
    secondRestart.MarkImportFinished(remaining.Id);
    secondRestart.MarkFinished(remaining.Id, ScheduledSelectionState.Completed, "导入完成", now.AddDays(1));
    Assert(new ScheduledSelectionStore(importPath).Load().Appointment is { State: ScheduledSelectionState.Completed, ImportFinished: true },
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
    if (Mutex.TryOpenExisting(@"Local\USTC.CourseAssistant.Route2.SingleInstance", out var existing))
    {
        existing.Dispose();
        throw new InvalidOperationException("已有生产实例，请正常关闭后再运行界面回归；检查不会绕过单实例保护。");
    }
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        var app = new UstcCourseAssistant.Route2.App();
        app.InitializeComponent();
        var backgroundStyle = new System.Windows.Style(typeof(UstcCourseAssistant.Route2.MainWindow));
        backgroundStyle.Setters.Add(new System.Windows.Setter(System.Windows.Window.ShowActivatedProperty, false));
        backgroundStyle.Setters.Add(new System.Windows.Setter(System.Windows.Window.ShowInTaskbarProperty, false));
        backgroundStyle.Setters.Add(new System.Windows.Setter(System.Windows.Window.LeftProperty, -20000d));
        backgroundStyle.Setters.Add(new System.Windows.Setter(System.Windows.Window.TopProperty, -20000d));
        app.Resources.Add(typeof(UstcCourseAssistant.Route2.MainWindow), backgroundStyle);
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            var window = app.MainWindow as UstcCourseAssistant.Route2.MainWindow;
            try
            {
                Assert(window is not null && app.Windows.OfType<UstcCourseAssistant.Route2.MainWindow>().Count() == 1,
                    "生产 Startup 未创建唯一主窗口");
                CheckWindowControls(window!);
                CheckWindowContextBoundaries(window!);
                CheckScheduledEditorControls(window!);
                await CheckScheduledWaitingCancellationAsync(window!);
            }
            catch (Exception ex)
            {
                failure = ex is TargetInvocationException { InnerException: not null } invocation ? invocation.InnerException : ex;
            }
            finally
            {
                if (window is not null)
                {
                    SetWindowField(window, "_automationRunning", false);
                    SetWindowField(window, "_scheduledRunning", false);
                    var service = GetWindowField<ScheduledSelectionService?>(window, "_scheduledService");
                    if (service?.Current?.State is ScheduledSelectionState.Armed or ScheduledSelectionState.Queued)
                        service.Cancel(service.Current.Id);
                    window.Close();
                }
                app.Shutdown();
            }
        }));
        try { app.Run(); }
        catch (Exception ex) { failure ??= ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw new InvalidOperationException("界面状态回归检查失败。", failure);
}

static T GetWindowField<T>(UstcCourseAssistant.Route2.MainWindow window, string name) =>
    (T)(window.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(name)).GetValue(window)!;

static void SetWindowField(UstcCourseAssistant.Route2.MainWindow window, string name, object? value) =>
    (window.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(name)).SetValue(window, value);

static object? CallWindow(UstcCourseAssistant.Route2.MainWindow window, string name, params object?[] arguments) =>
    (window.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(name)).Invoke(window, arguments);

static void CheckWindowControls(UstcCourseAssistant.Route2.MainWindow window)
{
    GetWindowField<System.Windows.Threading.DispatcherTimer>(window, "_scheduledTimer").Stop();
    Assert(!GetWindowField<bool>(window, "_automationRunning"), "启动后自动开始监测");
    foreach (var name in new[] { "StatusText", "StudentIdText", "AutomationStateText", "SnapshotSummaryText",
                 "ControlledDetailsText", "ControlledResultText", "CredentialStatusText", "ScheduledStatusText" })
        Assert(window.FindName(name) is TextBox { IsReadOnly: true }, name + " 不可选择复制");
    Assert(((FlowDocumentScrollViewer)window.FindName("ActivityLogViewer")).IsSelectionEnabled, "活动记录无法跨行选择复制");
    var grid = (DataGrid)window.FindName("AutomationTasksGrid");
    Assert(grid.Columns.Count == 6 && grid.SelectionUnit == DataGridSelectionUnit.FullRow
           && grid.SelectionMode == DataGridSelectionMode.Single && !grid.CanUserDeleteRows
           && grid.ClipboardCopyMode == DataGridClipboardCopyMode.IncludeHeader, "监测六列表格选择/复制/Delete分流合同改变");
    Assert(window.FindName("MonitoringTaskTurnSelector") is null && window.FindName("AssignMonitoringTurnButton") is null
           && window.FindName("ScheduledTurnSelector") is null && window.FindName("ScheduledAppointmentsGrid") is null,
        "仍存在逐课程/独立预约轮次或多预约控件");
    ((Button)window.FindName("OnboardingSkipButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
    CheckPageLayout(window, grid, "AutomationIntervalSelector", "AutomationCustomIntervalText", "AutomationScheduleCheckBox",
        "AutomationStartTimeText", "AutomationEndTimeText", "RunAtStartupCheckBox", "StartAutomationButton");
    var tasks = GetWindowField<IList<AutomationTask>>(window, "_automationTasks");
    tasks.Clear();
    var first = CreateTask("001111.01");
    var second = CreateTask("002222.01"); second.Priority = 2; second.Enabled = false;
    tasks.Add(first); tasks.Add(second);
    grid.SelectedItem = second; grid.ScrollIntoView(second); grid.UpdateLayout();
    var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(second);
    var checkbox = VisualDescendants<CheckBox>(row).First();
    checkbox.IsChecked = true;
    checkbox.RaiseEvent(new System.Windows.RoutedEventArgs(CheckBox.ClickEvent));
    Assert(second.Enabled && ReferenceEquals(grid.SelectedItem, second), "监测复选框一次切换未选中所在行");
    CallWindow(window, "MoveAutomationTask", -1);
    Assert(ReferenceEquals(tasks[0], second) && second.Priority == 1, "监测上移优先级未应用");
    CallWindow(window, "MoveAutomationTask", 1);
    Assert(ReferenceEquals(tasks[1], second) && second.Priority == 2, "监测下移优先级未应用");
    CallWindow(window, "DeleteAutomationTaskButton_Click", window, new System.Windows.RoutedEventArgs());
    Assert(tasks.Count == 1 && ReferenceEquals(tasks[0], first), "监测删除未删除选中行");
    SetWindowField(window, "_automationRunning", true); CallWindow(window, "SetBusy", false);
    Assert(!((Control)window.FindName("TurnSelector")).IsEnabled && !grid.IsEnabled, "运行时轮次或课程仍可编辑");
    CallWindow(window, "PauseAutomation", "离线回归结束", "检查");
    Assert(GetWindowField<CourseExecutionContext?>(window, "_monitoringContext") is null && grid.IsEnabled, "暂停未清除上下文并恢复编辑");
    var login = new UstcCourseAssistant.Route2.LoginWindow();
    login.ConfigureAutoLogin(true, () => null); login.Logout();
    var suppressed = login.GetType().GetField("_autoLoginSuppressed", BindingFlags.Instance | BindingFlags.NonPublic)!;
    Assert((bool)suppressed.GetValue(login)!, "退出登录未抑制自动登录");
    login.ConfigureAutoLogin(true, () => null);
    Assert((bool)suppressed.GetValue(login)!, "重复配置解除主动退出抑制");
    login.ConfigureAutoLogin(false, null); login.ConfigureAutoLogin(true, () => null, releaseUserSuppression: true);
    Assert(!(bool)suppressed.GetValue(login)!, "明确重新启用未解除自动登录抑制");
    login.CloseForApplication();
}

static void CheckPageLayout(UstcCourseAssistant.Route2.MainWindow window, DataGrid grid, params string[] controls)
{
    var tabs = (TabControl)window.FindName("OperationTabs");
    tabs.SelectedItem = tabs.Items.Cast<TabItem>().Single(tab => ContainsLogicalContent(tab.Content, grid));
    var originalDpi = VisualTreeHelper.GetDpi(window);
    try
    {
        foreach (var scale in new[] { 1d, 1.25d })
        {
            VisualTreeHelper.SetRootDpi(window, new System.Windows.DpiScale(scale, scale));
            foreach (var size in new[] { (Width: 1240d, Height: 800d), (Width: 1040d, Height: 700d) })
            {
                window.Width = size.Width; window.Height = size.Height; window.UpdateLayout();
                foreach (var name in controls) AssertElementInside(window, (System.Windows.FrameworkElement)window.FindName(name));
                AssertElementInside(window, grid);
                if (!double.IsNaN(grid.RowHeight)) Assert(grid.ActualHeight >= grid.RowHeight * 2, "课程表不足两行可见");
            }
        }
    }
    finally { VisualTreeHelper.SetRootDpi(window, originalDpi); window.UpdateLayout(); }
}

static void AssertElementInside(System.Windows.FrameworkElement container, System.Windows.FrameworkElement element)
{
    Assert(element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0, element.Name + " 不可见");
    var bounds = element.TransformToAncestor(container).TransformBounds(new System.Windows.Rect(element.RenderSize));
    Assert(bounds.Left >= -0.5 && bounds.Top >= -0.5 && bounds.Right <= container.ActualWidth + 0.5
           && bounds.Bottom <= container.ActualHeight + 0.5, element.Name + " 超出窗口区域：" + bounds);
    for (System.Windows.DependencyObject? current = VisualTreeHelper.GetParent(element); current is Visual && current != container;
         current = VisualTreeHelper.GetParent(current))
    {
        if (current is System.Windows.UIElement { Clip: { } clip } clipped)
        {
            var inClip = element.TransformToAncestor(clipped).TransformBounds(new System.Windows.Rect(element.RenderSize));
            var visible = clip.Bounds; visible.Inflate(0.5, 0.5);
            Assert(visible.Contains(inClip), element.Name + " 被祖先裁切");
        }
    }
}

static void CheckWindowContextBoundaries(UstcCourseAssistant.Route2.MainWindow window)
{
    var first = new TurnInfo(200, "第一轮", "测试学期");
    var second = new TurnInfo(201, "第二轮", "测试学期");
    var context = new CourseExecutionContext(100, first.Id);
    var turns = GetWindowField<ObservableCollection<TurnInfo>>(window, "_turns");
    SetWindowField(window, "_refreshingTurns", true); turns.Clear(); turns.Add(first); turns.Add(second);
    var selector = (ComboBox)window.FindName("TurnSelector"); selector.SelectedItem = first;
    SetWindowField(window, "_refreshingTurns", false);
    SetWindowField(window, "_sessionValidation", new SessionValidationResult(100, [first, second]));
    SetWindowField(window, "_monitoringContext", context);
    var tasks = GetWindowField<IList<AutomationTask>>(window, "_automationTasks"); tasks.Clear();
    var task = CreateTask("SAME.01", "LOW.01"); task.Priority = 10;
    var higher = CreateTask("LOW.01"); higher.Priority = 2;
    var lower = CreateTask("LOW.02", "SAME.01"); lower.Priority = 11;
    tasks.Add(task); tasks.Add(higher); tasks.Add(lower);
    var epoch = GetWindowField<long>(window, "_mutationEpoch");
    var snapshot = ScheduledSelectionService.CopyTask(task); snapshot.Status = "fresh-snapshot";
    CallWindow(window, "MergeMonitoringSnapshot", snapshot, epoch - 1, context);
    Assert(task.Status != snapshot.Status, "旧 epoch snapshot 修改新输入");
    CallWindow(window, "MergeMonitoringSnapshot", snapshot, epoch, new CourseExecutionContext(101, 200));
    Assert(task.Status != snapshot.Status, "旧账号 snapshot 合并");
    selector.SelectedItem = second;
    CallWindow(window, "MergeMonitoringSnapshot", snapshot, epoch, context);
    Assert(task.Status != snapshot.Status && !(bool)CallWindow(window, "IsCurrentContext", context)!, "轮次变化后旧读取仍合并");
    selector.SelectedItem = first;
    SetWindowField(window, "_monitoringContext", new CourseExecutionContext(100, 201));
    epoch = GetWindowField<long>(window, "_mutationEpoch");
    CallWindow(window, "MergeMonitoringSnapshot", snapshot, epoch, context);
    Assert(task.Status != snapshot.Status, "运行上下文不一致时 snapshot 合并");
    SetWindowField(window, "_monitoringContext", context);
    CallWindow(window, "MergeMonitoringSnapshot", snapshot, epoch, context);
    Assert(task.Status == snapshot.Status, "当前上下文有效 snapshot 未合并");
    SetWindowField(window, "_activeMonitoringTaskId", task.Id);
    SetWindowField(window, "_automationRunning", true);
    SetWindowField(window, "_automaticWritesPaused", false);
    ((CheckBox)window.FindName("AutomationArmCheckBox")).IsChecked = true;
    CallWindow(window, "EnsureExecutionAuthorized", task, context, CancellationToken.None);
    foreach (var blocked in new[] { new CourseExecutionContext(100, 201), new CourseExecutionContext(101, 200) })
    {
        var rejected = false;
        try { CallWindow(window, "EnsureExecutionAuthorized", task, blocked, CancellationToken.None); }
        catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException) { rejected = true; }
        Assert(rejected, "旧监测排队上下文未在读取前拒绝");
    }
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        var rejected = false;
        try { CallWindow(window, "EnsureExecutionAuthorized", task, context, cancelled.Token); }
        catch (TargetInvocationException ex) when (ex.InnerException is OperationCanceledException) { rejected = true; }
        Assert(rejected, "已取消会话仍通过真实执行前保护");
    }
    SetWindowField(window, "_activeMonitoringTaskId", null);
    SetWindowField(window, "_automationRunning", false);
    var scheduled = CreateTask(" same.01 ", "LOW.01"); scheduled.Priority = 1;
    CallWindow(window, "ApplyConfirmedSelection", scheduled, new CourseExecutionContext(100, 201));
    Assert(task.Enabled && higher.Enabled && lower.Enabled, "不同 context 预约结果修改监测");
    CallWindow(window, "ApplyConfirmedSelection", scheduled, context);
    Assert(!task.Enabled && higher.Enabled && !lower.Enabled, "跨集合使用预约优先级而非监测对应项自身优先级");
    task.Enabled = higher.Enabled = lower.Enabled = true;
    var duplicate = CreateTask("same.01"); tasks.Add(duplicate);
    CallWindow(window, "ApplyConfirmedSelection", scheduled, context);
    Assert(task.Enabled && duplicate.Enabled && lower.Enabled, "非唯一监测代码仍应用预约冲突停用");
    var validator = window.GetType().GetMethod("ValidateCourseTasks", BindingFlags.NonPublic | BindingFlags.Static)!;
    object?[] validation = [tasks, null];
    Assert(!(bool)validator.Invoke(null, validation)! && !string.IsNullOrWhiteSpace(validation[1]?.ToString())
           && task.Status != "等待中" && duplicate.Status != "等待中", "重复启用代码未标记并阻止监测开始");
    duplicate.Enabled = false; validation = [tasks, null];
    Assert((bool)validator.Invoke(null, validation)!, "停用重复课程错误阻止监测");
    duplicate.Enabled = true;
    var appointment = CreateAppointment(DateTimeOffset.Now.AddHours(1), CreateTask("IMPORTED.01"));
    appointment.CompletionAction = ScheduledCompletionAction.ImportToMonitoring;
    appointment.SelectionFinished = true; appointment.ExecutedTurnId = first.Id; appointment.State = ScheduledSelectionState.Running;
    var importPath = IOPath.Combine(CreateCheckDirectory("window-import"), "scheduled-selections.json");
    new ScheduledSelectionStore(importPath).Save(new ScheduledSelectionSettings { Appointment = appointment });
    var importService = new ScheduledSelectionService(new ScheduledSelectionStore(importPath));
    var originalService = GetWindowField<ScheduledSelectionService>(window, "_scheduledService");
    SetWindowField(window, "_scheduledService", importService);
    try
    {
        CallWindow(window, "CompleteScheduledImport", appointment);
        Assert(importService.Current is { State: ScheduledSelectionState.Completed, ImportFinished: true }
               && tasks.Any(item => item.CourseCode == "IMPORTED.01") && !GetWindowField<bool>(window, "_automationRunning"),
            "重复启用监测行阻止本地导入完成或仍启动监测");
        var persisted = new AutomationStore(AppDataPaths.AutomationSettings).Load().Settings;
        Assert(persisted.Tasks.Any(item => item.CourseCode == "IMPORTED.01"), "导入完成标记早于监测持久化");
        object?[] start = [context, null, false];
        Assert(!(bool)CallWindow(window, "TryStartMonitoringFromScheduled", start)!
               && start[1]?.ToString()?.Contains("SAME.01", StringComparison.OrdinalIgnoreCase) == true,
            "自动开始未先报告重复课程具体原因");
    }
    finally
    {
        SetWindowField(window, "_scheduledService", originalService);
        SetWindowField(window, "_scheduledEditor", null);
        CallWindow(window, "RefreshScheduledUi");
    }
    SetWindowField(window, "_automationRunning", true);
    var refreshRejected = false;
    try { CallWindow(window, "ApplyRecoveredSession", new SessionValidationResult(100, [second]), first.Id); }
    catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { refreshRejected = true; }
    Assert(refreshRejected && selector.SelectedItem is TurnInfo { Id: 200 }
           && GetWindowField<SessionValidationResult>(window, "_sessionValidation").OpenTurns.Any(turn => turn.Id == first.Id),
        "运行轮次关闭时刷新替换了会话/选择而非拒绝");
    CallWindow(window, "PauseAutomation", "上下文检查结束", "检查"); SetWindowField(window, "_sessionValidation", null);
}

static void CheckScheduledEditorControls(UstcCourseAssistant.Route2.MainWindow window)
{
    var grid = (DataGrid)window.FindName("ScheduledTasksGrid");
    var time = (TextBox)window.FindName("ScheduledTimeText"); var date = (DatePicker)window.FindName("ScheduledDatePicker");
    var authorization = (CheckBox)window.FindName("ScheduledAuthorizationCheckBox");
    var tasks = GetWindowField<IList<AutomationTask>>(window, "_scheduledTasks");
    var service = GetWindowField<ScheduledSelectionService>(window, "_scheduledService");
    Assert(service.Current is null && tasks.Count == 0 && authorization.IsChecked != true, "首次进入预约即写盘或预先授权");
    Assert(grid.Columns.Count == 6 && grid.ClipboardCopyMode == DataGridClipboardCopyMode.IncludeHeader, "预约未共享六列可复制课程表");
    CheckPageLayout(window, grid, "ScheduledSummaryText", "ScheduledDatePicker", "ScheduledTimeText", "ScheduledCompletionSelector",
        "ScheduledRunWhenMissedCheckBox", "ScheduledAuthorizationCheckBox", "SaveScheduledButton", "ArmScheduledButton");
    var future = DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(8)).AddDays(2);
    date.SelectedDate = future.Date; date.Text = future.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture); time.Text = "12:34:56";
    ((Button)window.FindName("AddScheduledTaskButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
    ((Button)window.FindName("AddScheduledTaskButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
    window.UpdateLayout();
    for (var index = 0; index < 2; index++)
    {
        grid.ScrollIntoView(tasks[index]); grid.UpdateLayout();
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(tasks[index]);
        var input = VisualDescendants<TextBox>(row).First(box => box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path == "CourseCode");
        input.Text = index == 0 ? "001111.01" : "002222.01"; input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
    }
    var secondTask = tasks[1]; grid.SelectedItem = secondTask;
    ((Button)window.FindName("MoveScheduledTaskUpButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
    Assert(ReferenceEquals(tasks[0], secondTask) && secondTask.Priority == 1, "预约优先级上移未应用");
    ((Button)window.FindName("MoveScheduledTaskDownButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
    Assert(ReferenceEquals(tasks[1], secondTask) && secondTask.Priority == 2, "预约优先级下移未应用");
    ((Button)window.FindName("AddScheduledTaskButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
    ((Button)window.FindName("DeleteScheduledTaskButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
    Assert(tasks.Count == 2, "预约删除没有作用于选中行");
    time.Text = "12:"; CallWindow(window, "RefreshScheduledUi");
    Assert(time.Text == "12:" && tasks.Count == 2, "tick 覆盖未完成时间输入或课程");
    time.Text = "12:34:56";
    ((Button)window.FindName("SaveScheduledButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
    var path = AppDataPaths.ScheduledSelections;
    var restarted = new ScheduledSelectionService(new ScheduledSelectionStore(path));
    Assert(restarted.Current is { State: ScheduledSelectionState.Draft, RealOperationsAuthorized: false } saved
           && saved.Tasks.Select(task => task.CourseCode).SequenceEqual(["001111.01", "002222.01"])
           && saved.ExecuteAt.ToOffset(TimeSpan.FromHours(8)).TimeOfDay == new TimeSpan(12, 34, 56), "同页草稿保存/服务重启丢失输入或秒数");
    var draft = service.Current!; draft.StudentId = 100; draft.RealOperationsAuthorized = true;
    service.Arm(draft, DateTimeOffset.Now);
    SetWindowField(window, "_scheduledEditorDirty", false); CallWindow(window, "RefreshScheduledUi");
    time.Text = "12:34:57";
    Assert(service.Current is { State: ScheduledSelectionState.Draft, RealOperationsAuthorized: false } && authorization.IsChecked != true
           && new ScheduledSelectionStore(path).Load().Appointment?.State == ScheduledSelectionState.Draft && time.Text == "12:34:57",
        "Armed 首次文本编辑未持久化撤销授权或覆盖新输入");
    draft = service.Current!; draft.RealOperationsAuthorized = true; service.Arm(draft, DateTimeOffset.Now);
    SetWindowField(window, "_scheduledEditorDirty", false); CallWindow(window, "RefreshScheduledUi");
    grid.ScrollIntoView(tasks[0]); grid.UpdateLayout();
    var firstRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(tasks[0]);
    var checkbox = VisualDescendants<CheckBox>(firstRow).First(); checkbox.IsChecked = false;
    checkbox.RaiseEvent(new System.Windows.RoutedEventArgs(CheckBox.ClickEvent));
    Assert(!tasks[0].Enabled && service.Current is { State: ScheduledSelectionState.Draft, RealOperationsAuthorized: false }
           && authorization.IsChecked != true, "Armed 首次复选编辑未解除授权或一次切换失败");
    ((Button)window.FindName("SaveScheduledButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
    draft = service.Current!; draft.RealOperationsAuthorized = true; service.Arm(draft, DateTimeOffset.Now); service.Cancel(draft.Id);
    SetWindowField(window, "_scheduledEditorDirty", false); CallWindow(window, "RefreshScheduledUi"); time.Text = "12:34:58";
    Assert(service.Current is { State: ScheduledSelectionState.Draft, RealOperationsAuthorized: false } terminal
           && !terminal.Tasks[0].Enabled && time.Text == "12:34:58", "终态编辑丢失课程停用状态或新输入");
    draft = service.Current!; draft.RealOperationsAuthorized = true; service.Arm(draft, DateTimeOffset.Now);
    SetWindowField(window, "_scheduledEditorDirty", false); CallWindow(window, "RefreshScheduledUi");
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        time.Text = "12:34:59";
        Assert(GetWindowField<bool>(window, "_scheduledEditFailed") && GetWindowField<bool>(window, "_automaticWritesPaused")
               && !time.IsEnabled && !grid.IsEnabled && !((Button)window.FindName("ArmScheduledButton")).IsEnabled
               && service.Current is { State: ScheduledSelectionState.Armed, RealOperationsAuthorized: true }, "解除授权保存失败仍允许编辑/执行或发布未保存 Draft");
    }
    service.Cancel(draft.Id);
}

static async Task CheckScheduledWaitingCancellationAsync(UstcCourseAssistant.Route2.MainWindow window)
{
    SetWindowField(window, "_scheduledEditFailed", false);
    SetWindowField(window, "_automaticWritesPaused", false);
    var service = GetWindowField<ScheduledSelectionService>(window, "_scheduledService");
    var current = service.Current!;
    service.BeginEdit(current.Id);
    current = service.Current!;
    var now = DateTimeOffset.Now;
    current.ExecuteAt = now.AddSeconds(1);
    current.RealOperationsAuthorized = true;
    service.Arm(current, now);
    service.CollectDue(now, false);
    var queued = service.CollectDue(now.AddSeconds(1), false)!;
    var queue = GetWindowField<CourseOperationQueue>(window, "_courseOperations");
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var blocker = queue.EnqueueAsync(false, now, now, async _ => { entered.SetResult(); await release.Task; return true; }, CancellationToken.None);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var cancellation = new CancellationTokenSource();
    SetWindowField(window, "_scheduledCancellation", cancellation);
    SetWindowField(window, "_scheduledWaitingId", queued.Id);
    var waiting = (Task)CallWindow(window, "RunScheduledAppointmentAsync", queued, cancellation)!;
    try
    {
        service.Cancel(queued.Id);
        CallWindow(window, "CancelScheduledWaiting", queued.Id);
        CallWindow(window, "UpdateScheduledControls");
        Assert(!((Button)window.FindName("ArmScheduledButton")).IsEnabled, "旧等待回调结束前允许重新启用");
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(GetWindowField<CancellationTokenSource?>(window, "_scheduledCancellation") is null
               && service.Current?.State == ScheduledSelectionState.Cancelled
               && !GetWindowField<bool>(window, "_scheduledRunning"), "等待取消未清理自己的状态或误启动真实执行");
    }
    finally { release.TrySetResult(); await blocker.WaitAsync(TimeSpan.FromSeconds(10)); }
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
