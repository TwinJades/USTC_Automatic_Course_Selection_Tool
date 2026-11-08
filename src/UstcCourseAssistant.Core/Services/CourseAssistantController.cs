using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using UstcCourseAssistant.Core.Authentication;
using UstcCourseAssistant.Core.Models;

namespace UstcCourseAssistant.Core.Services;

/// <summary>串行提交控制状态；网络读取和课程队列在状态通道外执行。</summary>
public sealed class CourseAssistantController : IAsyncDisposable
{
    private readonly Channel<Action> _channel = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _actor;
    private readonly IAuthenticationService _authentication;
    private readonly RuntimeStateStore _runtimeStore;
    private readonly AutomationStore _automationStore;
    private readonly ScheduledSelectionService _appointments;
    private readonly RealOperationCoordinator _operations;
    private readonly CourseOperationQueue _queue = new();
    private readonly LocalActivityLog _log;
    private readonly TimeProvider _clock;
    private readonly bool _resumeMonitoring;
    private readonly Func<ControlRequest, CancellationToken, Task<ControlReply>>? _update;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<(string UserName, string Password)?>? _credentials;
    private bool _stopping;
    private readonly Func<HttpClient>? _testClient;
    private AutomationSettings _settings;
    private RuntimeState _runtime;
    private AuthenticationSnapshot _auth;
    private SessionValidationResult? _session;
    private string _status;
    private bool _running, _cycle, _appointmentBusy, _blockedDue, _verifiedJournal;
    private long _generation;
    private Task? _activeWork;
    private Task? _authenticationWork;
    private bool _networkRecoveryPending;
    private DateTimeOffset _nextRecovery;
    private bool _sessionRecoveryPending;
    private bool _pendingImportFromCurrentRun;
    private DateTimeOffset _nextSessionRecovery;
    private DateTimeOffset _nextCycle;
    private AssistantSnapshot _published;
    private sealed record ManualPrecheck(Guid Id, long Generation, long StudentId, long TurnId, CourseMutationKind Kind, LessonInfo Lesson);
    private ManualPrecheck? _manualPrecheck;

    public CourseAssistantController(string dataDirectory, IAuthenticationService authentication, bool resumeMonitoring,
        TimeProvider? timeProvider = null, Func<ControlRequest, CancellationToken, Task<ControlReply>>? update = null,
        Func<(string UserName, string Password)?>? credentials = null)
        : this(dataDirectory, authentication, resumeMonitoring, timeProvider, update, null, credentials) { }

    internal CourseAssistantController(string dataDirectory, IAuthenticationService authentication, bool resumeMonitoring,
        TimeProvider? timeProvider, Func<ControlRequest, CancellationToken, Task<ControlReply>>? update, Func<HttpClient>? testClient,
        Func<(string UserName, string Password)?>? credentials = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _resumeMonitoring = resumeMonitoring;
        _clock = timeProvider ?? TimeProvider.System;
        _update = update;
        _testClient = testClient;
        _credentials = credentials;
        _automationStore = new AutomationStore(Path.Combine(dataDirectory, "automation-settings.json"));
        var loaded = _automationStore.Load();
        _settings = loaded.Settings;
        _runtimeStore = new RuntimeStateStore(Path.Combine(dataDirectory, "runtime-state.json"));
        var runtime = _runtimeStore.Load();
        _runtime = runtime.State;
        _operations = new RealOperationCoordinator(new OperationJournalStore(Path.Combine(dataDirectory, "operation-journal.json")));
        _appointments = new ScheduledSelectionService(new ScheduledSelectionStore(Path.Combine(dataDirectory, "scheduled-selections.json")), _clock);
        _appointments.RestoreAfterRestart(Now);
        _log = new LocalActivityLog(Path.Combine(dataDirectory, "activity.jsonl"));
        _auth = testClient is null ? authentication.Snapshot : new(AuthenticationState.Authenticated, null, "测试会话", null);
        _status = runtime.Warning ?? _operations.LoadWarning ?? loaded.Warning ?? "等待登录";
        if (_operations.HasUnresolvedOperation)
        {
            _runtime = _runtime with { MonitoringAuthorized = false, SafetyPaused = true };
            _runtimeStore.Save(_runtime);
        }
        if (loaded.Warning is not null && _runtime.MonitoringAuthorized)
        {
            _runtime = _runtime with { MonitoringAuthorized = false, SafetyPaused = true };
            _runtimeStore.Save(_runtime);
        }
        if (!_resumeMonitoring && _runtime.MonitoringAuthorized)
        {
            _runtime = _runtime with { MonitoringAuthorized = false };
            _runtimeStore.Save(_runtime);
        }
        _published = BuildSnapshot();
        _actor = Task.Run(ConsumeAsync);
    }

    private DateTimeOffset Now => BeijingClock.Now(_clock);
    public AssistantSnapshot Snapshot => Clone(Volatile.Read(ref _published));
    public event Action<AssistantSnapshot>? Changed;

    public async Task ExecuteExternalAuthenticationAsync(Func<CancellationToken, Task> import, CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ArgumentNullException.ThrowIfNull(import);
        await Invoke(() =>
        {
            if (_authBusy || _cycle || _operations.IsOperationInProgress || _appointmentBusy || _stopping)
                throw new InvalidOperationException("认证或真实操作正在进行，不能切换会话。");
            _authBusy = true;
            _authenticationWork = completed.Task;
            Stop(false, "正在导入新会话");
            _session = null;
            _auth = new(AuthenticationState.Authenticating, null, "正在导入新会话", null);
            Publish();
            return 0;
        }).ConfigureAwait(false);
        try
        {
            await import(cancellationToken).ConfigureAwait(false);
            var authenticated = await Invoke(() => { _auth = _authentication.Snapshot; Publish(); return _auth.State == AuthenticationState.Authenticated; }).ConfigureAwait(false);
            if (authenticated) await RefreshSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { await Invoke(() => { _authBusy = false; if (_auth.State == AuthenticationState.Authenticating) _auth = _authentication.Snapshot; Publish(); return 0; }).ConfigureAwait(false); }
            finally { completed.TrySetResult(); }
        }
    }

    private async Task ConsumeAsync()
    {
        await foreach (var action in _channel.Reader.ReadAllAsync()) action();
    }
    private Task<T> Invoke<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(() => { try { tcs.SetResult(action()); } catch (Exception ex) { tcs.SetException(ex); } }))
            tcs.SetException(new ObjectDisposedException(nameof(CourseAssistantController)));
        return tcs.Task;
    }
    private void Publish()
    {
        var snapshot = BuildSnapshot();
        Volatile.Write(ref _published, snapshot);
        var handler = Changed;
        if (handler is not null) ThreadPool.QueueUserWorkItem(_ => { try { handler(Clone(snapshot)); } catch { } });
    }
    private AssistantSnapshot BuildSnapshot() => new(_auth, _runtime.MonitoringAuthorized, _running, _runtime.SafetyPaused,
        _runtime.MonitoringAuthorized && _runtime.StudentId is { } student && _runtime.TurnId is { } turn ? new(student, turn) : null,
        _session, Clone(_settings), _appointments.Current, CloneNullable(_operations.ActiveJournal), _status, AppUpdateState.UpToDate);
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static T? CloneNullable<T>(T? value) where T : class => value is null ? null : Clone(value);
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static ControlReply Reply(ControlRequest request, bool ok, string code, string message, object? data = null) =>
        new(request.Id, ok, code, message, data is null ? null : Json(data));
    private static ControlReply Ok(ControlRequest r, object? data = null, string message = "完成") => Reply(r, true, "ok", message, data);
    private static ControlReply Error(ControlRequest r, string code, string message) => Reply(r, false, code, message);
    private static bool Has(JsonElement a, string key) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(key, out _);
    private static string String(JsonElement a, string key, bool required = true)
    {
        if (Has(a, key) && a.GetProperty(key).ValueKind == JsonValueKind.String) return a.GetProperty(key).GetString()!;
        if (!required) return "";
        throw new ArgumentException("缺少参数 " + key);
    }
    private static bool Bool(JsonElement a, string key, bool fallback = false) => !Has(a, key) ? fallback : a.GetProperty(key).ValueKind switch
    { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new ArgumentException("参数必须为布尔值：" + key) };
    private static long Long(JsonElement a, string key) => Has(a, key) && a.GetProperty(key).TryGetInt64(out var value) && value > 0 ? value : throw new ArgumentException("参数无效：" + key);
    private static Guid GuidArg(JsonElement a, string key) => Guid.TryParse(String(a, key), out var id) && id != Guid.Empty ? id : throw new ArgumentException("参数无效：" + key);
    private static Guid[] GuidArray(JsonElement a, string key) => Has(a, key) && a.GetProperty(key).ValueKind == JsonValueKind.Array
        ? a.GetProperty(key).EnumerateArray().Select(x => Guid.TryParse(x.GetString(), out var id) && id != Guid.Empty ? id : throw new ArgumentException("任务标识无效" )).ToArray()
        : throw new ArgumentException("缺少任务列表");
    private static List<string> Strings(JsonElement a, string key) => !Has(a, key) ? [] : a.GetProperty(key).ValueKind == JsonValueKind.Array
        ? a.GetProperty(key).EnumerateArray().Select(x => x.GetString()?.Trim() ?? throw new ArgumentException("课程代码无效")).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        : throw new ArgumentException("课程代码列表无效");
    private HttpClient Client() => _testClient?.Invoke() ?? _authentication.GetAuthenticatedSession().Client;
    private void SaveRuntime(RuntimeState state) { _runtimeStore.Save(state); _runtime = state; }
    private void SaveSettings(AutomationSettings settings) { _automationStore.Save(settings); _settings = settings; _generation++; }
    private void Log(string level, string message, ActivityEventKind kind) => _log.Append(level, message, kind);
    private void Stop(bool revoke, string reason, bool safety = false)
    {
        if (revoke || safety)
        {
            var state = _runtime with { MonitoringAuthorized = false, SafetyPaused = safety || _runtime.SafetyPaused };
            try { SaveRuntime(state); }
            catch { _runtime = state with { SafetyPaused = true }; _running = false; _generation++; throw; }
        }
        _networkRecoveryPending = false; _running = false; _generation++; _verifiedJournal = false; _status = reason;
    }
    private static void ValidateTasks(IEnumerable<AutomationTask> tasks)
    {
        var enabled = tasks.Where(t => t.Enabled).ToArray();
        if (enabled.Length == 0) throw new ArgumentException("请至少启用一个任务。");
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in enabled)
        {
            var code = t.CourseCode.Trim();
            if (code.Length == 0 || code.Any(c => char.IsWhiteSpace(c) || c is ',' or '，' or ';' or '；') || !codes.Add(code))
                throw new ArgumentException("任务教学班代码无效或重复。");
            if (t.ConflictingCourseCodes.Any(c => c.Trim().Equals(code, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("冲突课程不能包含目标课程。");
        }
    }

    public async Task<ControlReply> ExecuteAsync(ControlRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            if (request.Command == "service prepare-stop") { await ShutdownPrepareAsync(cancellationToken).ConfigureAwait(false); return Ok(request); }
            if (request.Command == "update cancel-prepare")
                return await Invoke(() => { _stopping = false; _status = "更新已取消；监测保持暂停。"; Publish(); return Ok(request); }).ConfigureAwait(false);
            if (request.Command == "update prepare-stop")
            {
                try { await ShutdownPrepareAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await Invoke(() => { _stopping = false; _status = "更新准备已取消；监测保持暂停。"; Publish(); return 0; }).ConfigureAwait(false);
                    throw;
                }
                return await Invoke(() =>
                {
                    if (_operations.HasUnresolvedOperation || _runtime.SafetyPaused)
                    {
                        _stopping = false; _status = "存在未解除的安全保护，更新已拒绝；请先核对。"; Publish();
                        return Error(request, "operation_unresolved", _status);
                    }
                    return Ok(request);
                }).ConfigureAwait(false);
            }
            if (await Invoke(() => _stopping && request.Command is not ("status" or "auth status" or "operations status")).ConfigureAwait(false))
                return Error(request, "busy", "服务正在安全停止。");
            if (request.Command is "update check" or "update apply")
            {
                if (_update is null) return Error(request, "update_failed", "更新服务不可用。");
                if (request.Command == "update apply" && !Bool(request.Arguments, "Confirm")) return Error(request, "authorization_required", "必须明确确认更新。");
                try { return await _update(request, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException)
                { return Error(request, "update_failed", LocalActivityLog.Sanitize(ex.Message)); }
            }
            if (request.Command == "auth screenshot") return await ScreenshotChallengeAsync(request, cancellationToken).ConfigureAwait(false);
            if (request.Command is "auth login" or "auth submit-code" or "auth send-code" or "auth submit-captcha")
                return await AuthenticateAsync(request, cancellationToken).ConfigureAwait(false);
            if (request.Command == "operations precheck") return await ManualPrecheckAsync(request, cancellationToken).ConfigureAwait(false);
            if (request.Command == "operations execute") return await ManualExecuteAsync(request).ConfigureAwait(false);
            if (request.Command is "courses turns" or "courses selected" or "courses check" or "monitor start" or "appointment arm" or "operations verify")
                return await ReadCommandAsync(request, cancellationToken).ConfigureAwait(false);
            return await Invoke(() => _stopping && request.Command is not ("status" or "auth status" or "operations status")
                ? Error(request, "busy", "服务正在安全停止。") : ExecuteLocal(request)).ConfigureAwait(false);
        }
        catch (WriteResultUncertainException) { return Error(request, "operation_unresolved", "真实写入结果不确定，必须人工核对。"); }
        catch (AuthenticationRequiredException) { return Error(request, "authentication_required", "请先登录。"); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return Error(request, "invalid_arguments", LocalActivityLog.Sanitize(ex.Message)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Error(request, "storage_failed", "无法保存或读取数据，操作未完成。"); }
        catch (HttpRequestException) { return Error(request, "service_unavailable", "教务网络暂不可用。"); }
    }

    private ControlReply ExecuteLocal(ControlRequest r)
    {
        var a = r.Arguments;
        switch (r.Command)
        {
            case "status": return Ok(r, Snapshot);
            case "auth status": return Ok(r, _auth);
            case "logs":
                var count = Has(a, "Count") ? checked((int)Long(a, "Count")) : 100;
                if (count > 1000) throw new ArgumentException("日志数量必须在1至1000之间。");
                return Ok(r, _log.LoadRecent().TakeLast(count).Select(x => x with { At = TimeZoneInfo.ConvertTime(x.At, BeijingClock.TimeZone) }).ToArray());
            case "auth logout":
                if (_authBusy || _cycle || _operations.IsOperationInProgress || _appointmentBusy) return Error(r, "busy", "认证或真实操作正在完成，暂不能退出登录。");
                Stop(true, "已退出登录"); _authentication.Logout(); _auth = _authentication.Snapshot; _session = null; Publish(); return Ok(r);
            case "tasks list": return Ok(r, Clone(_settings.Tasks));
            case "settings show": return Ok(r, Clone(_settings));
            case "appointment show": return Ok(r, _appointments.Current);
            case "operations status": return Ok(r, CloneNullable(_operations.ActiveJournal));
            case "monitor stop": Stop(true, "监测已停止"); Publish(); return Ok(r);
            case "operations acknowledge":
                if (!Bool(a, "Authorize")) return Error(r, "authorization_required", "必须明确确认解除保护。");
                if (_operations.IsOperationInProgress || (!_verifiedJournal && (_operations.HasUnresolvedOperation || _session is null)))
                    return Error(r, "operation_unresolved", "必须先完成最新只读核对。");
                if (_operations.HasUnresolvedOperation) _operations.AcknowledgeResolved("用户已核对最新状态并明确解除保护。");
                SaveRuntime(_runtime with { MonitoringAuthorized = false, SafetyPaused = false }); _verifiedJournal = false; _status = "保护已解除，需重新授权"; Publish(); return Ok(r);
            case "tasks reorder":
                if (_running || _cycle || _appointmentBusy) return Error(r, "busy", "任务执行期间不能编辑任务。");
                var reordered = Clone(_settings);
                var firstId = GuidArg(a, "Id");
                var secondId = GuidArg(a, "OtherId");
                if (firstId == secondId) throw new ArgumentException("请选择两个不同的任务。");
                var first = reordered.Tasks.SingleOrDefault(t => t.Id == firstId) ?? throw new ArgumentException("任务不存在。");
                var second = reordered.Tasks.SingleOrDefault(t => t.Id == secondId) ?? throw new ArgumentException("任务不存在。");
                (first.Priority, second.Priority) = (second.Priority, first.Priority);
                SaveSettings(reordered); Publish(); return Ok(r, Clone(reordered.Tasks));
            case "tasks add": case "tasks edit": case "tasks remove": case "tasks enable": case "tasks disable":
                if (_running || _cycle || _appointmentBusy) return Error(r, "busy", "任务执行期间不能编辑任务。");
                var settings = Clone(_settings);
                AutomationTask? task = null;
                if (r.Command != "tasks add") task = settings.Tasks.SingleOrDefault(t => t.Id == GuidArg(a, "Id")) ?? throw new ArgumentException("任务不存在。");
                if (r.Command == "tasks remove") settings.Tasks.Remove(task!);
                else if (r.Command is "tasks enable" or "tasks disable") task!.Enabled = r.Command == "tasks enable";
                else
                {
                    task ??= new AutomationTask();
                    if (Has(a, "Code")) task.CourseCode = String(a, "Code").Trim();
                    if (Has(a, "Priority")) task.Priority = checked((int)Long(a, "Priority"));
                    if (Has(a, "Conflict")) task.ConflictingCourseCodes = Strings(a, "Conflict");
                    if (task.CourseCode.Length == 0 || task.CourseCode.Any(c => char.IsWhiteSpace(c) || c is ',' or '，' or ';' or '；')) throw new ArgumentException("教学班代码无效。");
                    if (task.ConflictingCourseCodes.Any(c => c.Equals(task.CourseCode, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("冲突课程不能包含目标课程。");
                    if (r.Command == "tasks add") { if (settings.Tasks.Any(x => x.CourseCode.Equals(task.CourseCode, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("教学班代码重复。"); settings.Tasks.Add(task); }
                }
                if (settings.Tasks.Any(x => x.Enabled)) ValidateTasks(settings.Tasks);
                SaveSettings(settings); Publish(); return Ok(r, task);
            case "settings set":
                if (_running || _cycle || _appointmentBusy) return Error(r, "busy", "执行期间不能修改设置。");
                var changed = Clone(_settings);
                if (Has(a, "Interval")) { changed.IntervalSeconds = checked((int)Long(a, "Interval")); if (changed.IntervalSeconds is < 5 or > 3600) throw new ArgumentException("间隔必须为5至3600秒。"); }
                if (Has(a, "Jitter")) changed.EnableIntervalJitter = Bool(a, "Jitter");
                if (Has(a, "Schedule")) changed.EnableSchedule = Bool(a, "Schedule");
                if (Has(a, "Start")) changed.StartAt = TimeOnly.ParseExact(String(a, "Start"), "HH:mm", CultureInfo.InvariantCulture);
                if (Has(a, "End")) changed.EndAt = TimeOnly.ParseExact(String(a, "End"), "HH:mm", CultureInfo.InvariantCulture);
                SaveSettings(changed); Publish(); return Ok(r, Clone(changed));
            case "appointment save":
                if (_appointmentBusy) return Error(r, "busy", "预约正在执行。");
                var at = DateTime.ParseExact(String(a, "At"), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None);
                if (Has(a, "Task") == Has(a, "Tasks")) throw new ArgumentException("必须选择任务标识或独立预约任务，不能同时提供。");
                List<AutomationTask> tasks;
                if (Has(a, "Tasks"))
                {
                    tasks = a.GetProperty("Tasks").Deserialize<List<AutomationTask>>() ?? throw new ArgumentException("预约任务无效。");
                    if (tasks.Any(t => t is null || t.Id == Guid.Empty || t.Priority < 1 || t.ConflictingCourseCodes is null || t.ConflictingCourseCodes.Any(c => c is null) || t.CourseCode is null) || tasks.Select(t => t.Id).Distinct().Count() != tasks.Count)
                        throw new ArgumentException("预约任务无效或重复。");
                    tasks = tasks.Select(task => ScheduledSelectionService.CopyTask(task)).ToList();
                }
                else
                {
                    var ids = GuidArray(a, "Task");
                    if (ids.Length == 0 || ids.Distinct().Count() != ids.Length) throw new ArgumentException("预约任务列表为空或重复。");
                    tasks = ids.Select(id => ScheduledSelectionService.CopyTask(_settings.Tasks.SingleOrDefault(t => t.Id == id) ?? throw new ArgumentException("预约任务不存在。"))).ToList();
                }
                ValidateTasks(tasks);
                var completion = String(a, "Completion") switch { "stop" => ScheduledCompletionAction.Stop, "monitor" => ScheduledCompletionAction.ImportToMonitoring, _ => throw new ArgumentException("完成行为无效。") };
                var draft = _appointments.Current ?? new ScheduledSelection { CreatedAt = Now };
                draft.ExecuteAt = new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Unspecified), TimeSpan.FromHours(8)); draft.RunWhenMissed = Bool(a, "RunWhenMissed"); draft.CompletionAction = completion; draft.Tasks = tasks;
                _appointments.UpsertDraft(draft); _generation++; Publish(); return Ok(r, _appointments.Current);
            case "appointment edit":
                if (_appointmentBusy) return Error(r, "busy", "预约正在执行。");
                var editing = _appointments.Current ?? throw new ArgumentException("没有预约。"); _appointments.BeginEdit(editing.Id); _generation++; Publish(); return Ok(r, _appointments.Current);
            case "appointment cancel":
                var appointment = _appointments.Current ?? throw new ArgumentException("没有预约。");
                if (appointment.State == ScheduledSelectionState.Running) return Error(r, "busy", "预约已开始，不能取消已发送操作。");
                _appointments.Cancel(appointment.Id); _generation++; Publish(); return Ok(r);
            default: return Error(r, "invalid_arguments", "未知命令。");
        }
    }

    private bool _authBusy;
    private async Task<ControlReply> AuthenticateAsync(ControlRequest r, CancellationToken token)
    {
        string? user = null, password = null, code = null;
        Task<AuthenticationSnapshot>? work = null;
        Guid challenge = Guid.Empty;
        if (r.Command == "auth login")
        {
            if (Bool(r.Arguments, "UseSaved"))
            {
                var saved = _credentials?.Invoke() ?? throw new InvalidOperationException("没有可用的系统凭据。");
                (user, password) = saved;
            }
            else { user = String(r.Arguments, "UserName"); password = String(r.Arguments, "Password"); }
        }
        else
        {
            if (r.Command is "auth submit-code" or "auth submit-captcha") code = String(r.Arguments, "Code");
            challenge = Has(r.Arguments, "ChallengeId") ? GuidArg(r.Arguments, "ChallengeId") : Guid.Empty;
        }
        var prepared = await Invoke(() =>
        {
            if (_stopping || _authBusy || _cycle || _operations.IsOperationInProgress || _appointmentBusy) return "busy";
            if (r.Command != "auth login")
            {
                if (_auth.State != AuthenticationState.AwaitingSms || _auth.ChallengeId is null) return "sms_required";
                if (challenge == Guid.Empty) challenge = _auth.ChallengeId.Value;
                if (challenge != _auth.ChallengeId) return "invalid_arguments";
            }
            else
            {
                Stop(false, "正在认证"); _session = null;
                _auth = new(AuthenticationState.Authenticating, null, "正在认证", null);
            }
            _authBusy = true;
            work = CompleteAuthenticationAsync(r.Command, user, password, challenge, code, r.Command == "auth login" ? _lifetime.Token : token);
            _authenticationWork = work;
            Publish(); return "ok";
        }).ConfigureAwait(false);
        if (prepared != "ok") return Error(r, prepared, prepared == "busy" ? "认证或真实操作正在进行。" : "没有匹配的短信挑战。");
        if (r.Command == "auth login")
        {
            return Ok(r, Snapshot.Authentication, "认证已启动，请查询认证状态。");
        }
        var result = await work!.ConfigureAwait(false);
        return result.State == AuthenticationState.Failed ? Error(r, "authentication_required", result.Message)
            : result.State == AuthenticationState.AwaitingSms ? Reply(r, false, "sms_required", result.Message, result)
            : Ok(r, result);
    }

    private async Task<AuthenticationSnapshot> CompleteAuthenticationAsync(string command, string? user, string? password, Guid challenge, string? code, CancellationToken token)
    {
        AuthenticationSnapshot result;
        try
        {
            result = command switch
            {
                "auth login" => await _authentication.LoginAsync(user!, password!, token).ConfigureAwait(false),
                "auth send-code" => await _authentication.RequestSmsCodeAsync(challenge, token).ConfigureAwait(false),
                "auth submit-captcha" => await _authentication.SubmitCaptchaAsync(challenge, code!, token).ConfigureAwait(false),
                _ => await _authentication.SubmitSmsCodeAsync(challenge, code!, token).ConfigureAwait(false)
            };
        }
        catch (ArgumentException)
        {
            result = _authentication.Snapshot with { Message = "验证码格式无效。" };
        }
        catch (Exception ex)
        {
            result = _authentication.Snapshot with { Message = LocalActivityLog.Sanitize(ex.Message) };
            if (result.State is not (AuthenticationState.Failed or AuthenticationState.AwaitingSms))
                result = result with { State = AuthenticationState.Failed, ChallengeId = null };
        }
        await Invoke(() => { _auth = result; _status = result.Message; _generation++; Publish(); return 0; }).ConfigureAwait(false);
        try
        {
            if (result.State == AuthenticationState.Authenticated) await RefreshSessionAsync(token).ConfigureAwait(false);
            return await Invoke(() => _auth).ConfigureAwait(false);
        }
        finally { await Invoke(() => { _authBusy = false; Publish(); return 0; }).ConfigureAwait(false); }
    }

    private async Task<ControlReply> ScreenshotChallengeAsync(ControlRequest request, CancellationToken token)
    {
        Guid challenge = Has(request.Arguments, "ChallengeId") ? GuidArg(request.Arguments, "ChallengeId") : Guid.Empty;
        var valid = await Invoke(() =>
        {
            if (_stopping || _authBusy || _auth.State != AuthenticationState.AwaitingSms || _auth.ChallengeId is null) return false;
            if (challenge == Guid.Empty) challenge = _auth.ChallengeId.Value;
            if (challenge != _auth.ChallengeId) return false;
            _authBusy = true;
            return true;
        }).ConfigureAwait(false);
        if (!valid) return Error(request, "sms_required", "没有匹配的认证挑战或认证正在进行。");
        try { return Ok(request, await _authentication.ScreenshotChallengeAsync(challenge, token).ConfigureAwait(false)); }
        finally { await Invoke(() => { _authBusy = false; return 0; }).ConfigureAwait(false); }
    }

    private async Task RefreshSessionAsync(CancellationToken token)
    {
        var generation = await Invoke(() => _generation).ConfigureAwait(false);
        SessionValidationResult session;
        try { session = await new ReadOnlyEamsClient(Client()).ValidateAsync(token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is AuthenticationRequiredException or HttpRequestException or OperationCanceledException or InvalidOperationException or JsonException)
        {
            await Invoke(() =>
            {
                if (generation == _generation)
                {
                    _session = null; _running = false;
                    _sessionRecoveryPending = ex is HttpRequestException || ex is OperationCanceledException && !token.IsCancellationRequested;
                    _nextSessionRecovery = Now.AddSeconds(15);
                    if (ex is AuthenticationRequiredException) _auth = new(AuthenticationState.Failed, null, "教务会话失效，请重新登录。", null);
                    _status = "会话验证暂不可用，保留授权意图。"; Publish();
                }
                return 0;
            }).ConfigureAwait(false);
            return;
        }
        await Invoke(() =>
        {
            if (generation != _generation) return 0;
            _sessionRecoveryPending = false;
            _session = session;
            if (_runtime.MonitoringAuthorized && _runtime.StudentId != session.StudentId)
                Stop(true, "授权账号不匹配，已安全暂停。", true);
            else if (!_stopping && _runtime.MonitoringAuthorized && _resumeMonitoring && !_runtime.SafetyPaused && !_operations.HasUnresolvedOperation)
            {
                if (session.OpenTurns.All(t => t.Id != _runtime.TurnId)) Stop(true, "授权轮次不匹配，已安全暂停。", true);
                else
                {
                    try { ValidateTasks(_settings.Tasks); _running = true; _nextCycle = Now; _status = "授权已验证，监测已恢复"; }
                    catch (ArgumentException) { _running = false; _status = "任务配置无效，监测未恢复。"; }
                }
            }
            Publish(); return 0;
        }).ConfigureAwait(false);
    }

    private async Task<ControlReply> ManualPrecheckAsync(ControlRequest request, CancellationToken token)
    {
        long turn = Long(request.Arguments, "TurnId");
        string code = String(request.Arguments, "Code").Trim();
        if (code.Length == 0 || code.Any(c => char.IsWhiteSpace(c) || c is ',' or '，' or ';' or '；')) throw new ArgumentException("请输入一个教学班代码。");
        var kind = String(request.Arguments, "Action") switch { "add" => CourseMutationKind.Add, "drop" => CourseMutationKind.Drop, _ => throw new ArgumentException("操作类型无效。") };
        var captured = await Invoke(() =>
        {
            _manualPrecheck = null; _generation++;
            if (_auth.State != AuthenticationState.Authenticated) throw new AuthenticationRequiredException();
            if (_running || _cycle || _appointmentBusy || _operations.HasUnresolvedOperation || _runtime.SafetyPaused) throw new InvalidOperationException("监测运行中或安全保护未解除。");
            return (_generation, Client());
        }).ConfigureAwait(false);
        var reader = new ReadOnlyEamsClient(captured.Item2);
        var session = await reader.ValidateAsync(token).ConfigureAwait(false);
        if (session.OpenTurns.All(t => t.Id != turn)) throw new ArgumentException("选课轮次未开放。");
        var shadow = await reader.GetShadowSnapshotAsync(session.StudentId, turn, [code], token).ConfigureAwait(false);
        var matches = shadow.TargetLessons.Where(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("无法唯一确定教学班。");
        var selected = shadow.SelectedLessons.Any(x => x.Id == matches[0].Id);
        if (selected == (kind == CourseMutationKind.Add)) throw new InvalidOperationException("课程已处于目标状态。");
        return await Invoke(() =>
        {
            if (_stopping || captured.Item1 != _generation || _running || _cycle || _appointmentBusy || _operations.HasUnresolvedOperation || _runtime.SafetyPaused) return Error(request, "busy", "预检查上下文已变化。");
            _session = session;
            var precheck = new ManualPrecheck(Guid.NewGuid(), _generation, session.StudentId, turn, kind, matches[0]);
            _manualPrecheck = precheck;
            Publish();
            return Ok(request, new { PrecheckId = precheck.Id, Lesson = precheck.Lesson, Selected = selected, Action = kind == CourseMutationKind.Add ? "add" : "drop" });
        }).ConfigureAwait(false);
    }

    private async Task<ControlReply> ManualExecuteAsync(ControlRequest request)
    {
        if (!Bool(request.Arguments, "Authorize")) return Error(request, "authorization_required", "必须明确授权单次真实操作。");
        var id = GuidArg(request.Arguments, "PrecheckId");
        Task<VerifiedMutationResult>? work = null;
        var accepted = await Invoke(() =>
        {
            var precheck = _manualPrecheck;
            _manualPrecheck = null;
            if (precheck is null || precheck.Id != id || precheck.Generation != _generation || _running || _cycle || _authBusy || _appointmentBusy || _stopping || _auth.State != AuthenticationState.Authenticated || _runtime.SafetyPaused || _operations.HasUnresolvedOperation) return false;
            _cycle = true;
            work = ExecuteManualQueuedAsync(precheck);
            _activeWork = work;
            Publish();
            return true;
        }).ConfigureAwait(false);
        if (!accepted) return Error(request, "busy", "预检查已失效，请重新预检查。");
        var result = await work!.ConfigureAwait(false);
        return result.ExpectedStateReached ? Ok(request, result) : Reply(request, false, "operation_unresolved", result.Message, result);
    }

    private async Task<VerifiedMutationResult> ExecuteManualQueuedAsync(ManualPrecheck precheck)
    {
        try
        {
            return await _queue.EnqueueAsync(false, Now, Now, async token =>
            {
                var client = Client();
                var reader = new ReadOnlyEamsClient(client);
                var session = await reader.ValidateAsync(token).ConfigureAwait(false);
                if (session.StudentId != precheck.StudentId || session.OpenTurns.All(t => t.Id != precheck.TurnId)) throw new InvalidOperationException("账号或轮次已变化。");
                var shadow = await reader.GetShadowSnapshotAsync(session.StudentId, precheck.TurnId, [precheck.Lesson.Code], token).ConfigureAwait(false);
                var matches = shadow.TargetLessons.Where(x => x.Code.Equals(precheck.Lesson.Code, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1 || matches[0].Id != precheck.Lesson.Id || shadow.SelectedLessons.Any(x => x.Id == precheck.Lesson.Id) == (precheck.Kind == CourseMutationKind.Add)) throw new InvalidOperationException("教学班状态已变化，请重新预检查。");
                if (!await Invoke(() => !_stopping && _generation == precheck.Generation && !_runtime.SafetyPaused && !_operations.HasUnresolvedOperation && _auth.State == AuthenticationState.Authenticated).ConfigureAwait(false)) throw new InvalidOperationException("执行上下文已变化。");
                using var lease = await _operations.EnterAsync("单门受控操作", reader, new ControlledEamsClient(client), token).ConfigureAwait(false);
                if (!await Invoke(() => !_stopping && _generation == precheck.Generation && !_runtime.SafetyPaused && _auth.State == AuthenticationState.Authenticated).ConfigureAwait(false)) throw new InvalidOperationException("执行授权已撤销。");
                lease.BeginWorkflow("单门受控操作", session.StudentId, precheck.TurnId, precheck.Lesson.Code, []);
                var result = await lease.ExecuteAndVerifyAsync(precheck.Kind, matches[0], "单门受控操作", token).ConfigureAwait(false);
                if (result.ExpectedStateReached) lease.CompleteWorkflow("单门操作最终状态已确认。");
                else lease.RequireManualReview("单门操作最终状态不确定，请人工核对。");
                return result;
            }, _lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            await Invoke(() => { _cycle = false; if (_operations.HasUnresolvedOperation) Stop(true, "单门操作需要人工核对。", true); Publish(); return 0; }).ConfigureAwait(false);
        }
    }

    private async Task<ControlReply> ReadCommandAsync(ControlRequest r, CancellationToken token)
    {
        var captured = await Invoke(() =>
        {
            if (_auth.State != AuthenticationState.Authenticated) throw new AuthenticationRequiredException();
            return (_generation, Client());
        }).ConfigureAwait(false);
        var reader = new ReadOnlyEamsClient(captured.Item2);
        var session = await reader.ValidateAsync(token).ConfigureAwait(false);
        ShadowSnapshot? shadow = null;
        long turn = 0;
        if (r.Command is "courses selected" or "courses check")
        {
            turn = Has(r.Arguments, "TurnId") ? Long(r.Arguments, "TurnId") : await Invoke(() => _runtime.SelectedTurnId ?? session.OpenTurns.FirstOrDefault()?.Id ?? 0).ConfigureAwait(false);
            if (session.OpenTurns.All(t => t.Id != turn)) throw new ArgumentException("选课轮次未开放。");
            var codes = r.Command == "courses check" ? new[] { String(r.Arguments, "Code") } : Array.Empty<string>();
            shadow = await reader.GetShadowSnapshotAsync(session.StudentId, turn, codes, token).ConfigureAwait(false);
        }
        if (r.Command == "operations verify")
        {
            var journal = await Invoke(() => CloneNullable(_operations.ActiveJournal)).ConfigureAwait(false);
            if (journal is not null)
            {
                if (journal.StudentId != session.StudentId || session.OpenTurns.All(t => t.Id != journal.TurnId)) throw new InvalidOperationException("待核对账号或轮次不匹配。");
                shadow = await reader.GetShadowSnapshotAsync(journal.StudentId, journal.TurnId, journal.RelatedCodes.Append(journal.TargetCode).ToArray(), token).ConfigureAwait(false);
            }
        }
        return await Invoke(() =>
        {
            if (_stopping || captured.Item1 != _generation) return Error(r, "busy", "会话或执行上下文已变化，请重新操作。");
            _session = session;
            switch (r.Command)
            {
                case "courses turns": Publish(); return Ok(r, session.OpenTurns);
                case "courses selected": return Ok(r, shadow!.SelectedLessons);
                case "courses check": return Ok(r, shadow);
                case "operations verify": _verifiedJournal = true; Publish(); return Ok(r, shadow);
                case "monitor start":
                    if (!Bool(r.Arguments, "Authorize")) return Error(r, "authorization_required", "必须明确授权真实选退课。");
                    if (_operations.HasUnresolvedOperation || _runtime.SafetyPaused) return Error(r, "operation_unresolved", "安全保护尚未解除。");
                    if (_cycle || _authBusy || _operations.IsOperationInProgress || _appointmentBusy) return Error(r, "busy", "真实操作或认证正在进行。");
                    ValidateTasks(_settings.Tasks);
                    var selected = Long(r.Arguments, "TurnId");
                    if (session.OpenTurns.All(t => t.Id != selected)) throw new ArgumentException("选课轮次未开放。");
                    SaveRuntime(_runtime with { MonitoringAuthorized = true, StudentId = session.StudentId, TurnId = selected, SelectedTurnId = selected, SafetyPaused = false });
                    _generation++; _running = true; _nextCycle = Now; _status = "监测已授权"; Publish(); return Ok(r, Snapshot);
                case "appointment arm":
                    if (!Bool(r.Arguments, "Authorize")) return Error(r, "authorization_required", "必须明确授权预约真实选退课。");
                    if (_operations.HasUnresolvedOperation || _runtime.SafetyPaused) return Error(r, "operation_unresolved", "安全保护尚未解除。");
                    if (_appointmentBusy) return Error(r, "busy", "预约正在执行。");
                    var appointment = _appointments.Current ?? throw new ArgumentException("没有预约草稿。");
                    var requestedTurn = Has(r.Arguments, "TurnId") ? Long(r.Arguments, "TurnId") : _runtime.SelectedTurnId;
                    var resolved = ScheduledSelectionService.ResolveTurn(session, session.StudentId, requestedTurn);
                    appointment.StudentId = session.StudentId; appointment.RealOperationsAuthorized = true;
                    SaveRuntime(_runtime with { SelectedTurnId = resolved.Id });
                    _appointments.Arm(appointment, Now); _generation++; Publish(); return Ok(r, _appointments.Current);
                default: throw new InvalidOperationException();
            }
        }).ConfigureAwait(false);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        if (_auth.State == AuthenticationState.Authenticated) await RefreshSessionAsync(linked.Token).ConfigureAwait(false);
        else if (_resumeMonitoring && _credentials is not null && (_runtime.MonitoringAuthorized || _appointments.Current is { State: ScheduledSelectionState.Armed, RealOperationsAuthorized: true } || _appointments.PendingImport is not null))
        {
            try
            {
                var saved = _credentials();
                if (saved is { } credential)
                {
                    var request = new ControlRequest(Guid.NewGuid(), "auth login", Json(new { UserName = credential.UserName, Password = credential.Password }));
                    await AuthenticateAsync(request, linked.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                await Invoke(() => { _running = false; _status = "系统凭据不可用，请人工登录；不会自动重试。"; Publish(); return 0; }).ConfigureAwait(false);
            }
        }
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250), _clock);
            while (!linked.IsCancellationRequested)
            {
                try { await Invoke(Tick).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    await Invoke(() =>
                    {
                        try { Stop(true, "调度状态无法安全保存，执行已暂停。", true); }
                        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
                        Publish(); return 0;
                    }).ConfigureAwait(false);
                }
                if (!await timer.WaitForNextTickAsync(linked.Token).ConfigureAwait(false)) break;
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        await ShutdownPrepareAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private int Tick()
    {
        if (_stopping || _authBusy) return 0;
        var now = Now;
        if (_sessionRecoveryPending && !_cycle && !_appointmentBusy && now >= _nextSessionRecovery && _auth.State == AuthenticationState.Authenticated)
        {
            _nextSessionRecovery = now.AddSeconds(15);
            _cycle = true;
            _activeWork = RetrySessionAsync();
        }
        if (_networkRecoveryPending && !_cycle && !_appointmentBusy && now >= _nextRecovery)
        {
            _nextRecovery = now.AddSeconds(15);
            _cycle = true;
            _activeWork = RecoverInterruptedSwapAsync(_generation);
        }
        if (_appointments.PendingImport is { } pending && !_appointmentBusy && !_runtime.SafetyPaused && !_operations.HasUnresolvedOperation)
        {
            if (_session is not null && _auth.State == AuthenticationState.Authenticated) CompleteImport(pending, allowMonitoring: _resumeMonitoring || _pendingImportFromCurrentRun);
            else if (_status != "预约等待验证账号及轮次后交接监测。") { _status = "预约等待验证账号及轮次后交接监测。"; Publish(); }
        }
        var appointment = _appointments.Current;
        if (!_cycle && !_appointmentBusy && !_runtime.SafetyPaused && !_operations.HasUnresolvedOperation && appointment is { State: ScheduledSelectionState.Armed } && appointment.ExecuteAt <= now)
        {
            if (_auth.State != AuthenticationState.Authenticated || _session is null)
            {
                _blockedDue = true;
                var waiting = _auth.State == AuthenticationState.AwaitingSms ? "预约等待人工验证" : "预约等待登录";
                if (_status != waiting) { _status = waiting; Publish(); }
            }
            else
            {
                _appointmentBusy = true;
                _activeWork = RunAppointmentAsync(appointment, _generation, _blockedDue);
                _blockedDue = false;
            }
        }
        else if (!_appointmentBusy && appointment is { State: ScheduledSelectionState.Armed } && appointment.ExecuteAt > now)
            _appointments.CollectDue(now, resumed: false);
        if (_running && !_cycle && !_appointmentBusy && now >= _nextCycle && !_runtime.SafetyPaused && !_operations.HasUnresolvedOperation)
        {
            _nextCycle = now + AutomationTiming.CalculateNextDelay(Math.Clamp(_settings.IntervalSeconds, 5, 3600), _settings.EnableIntervalJitter, Random.Shared.NextDouble());
            if (!_settings.EnableSchedule || BeijingClock.IsInWindow(now, _settings.StartAt, _settings.EndAt))
            {
                _cycle = true;
                _activeWork = RunMonitoringAsync(_generation, Clone(_settings.Tasks), new(_runtime.StudentId!.Value, _runtime.TurnId!.Value));
            }
            else { _status = "等待运行时段"; Publish(); }
        }
        return 0;
    }
    private async Task RetrySessionAsync()
    {
        try { await RefreshSessionAsync(_lifetime.Token).ConfigureAwait(false); }
        finally { await Invoke(() => { _cycle = false; Publish(); return 0; }).ConfigureAwait(false); }
    }


    private async Task RunMonitoringAsync(long generation, List<AutomationTask> tasks, CourseExecutionContext context)
    {
        try
        {
            var client = Client();
            var reader = new ReadOnlyEamsClient(client);
            var session = await reader.ValidateAsync(_lifetime.Token).ConfigureAwait(false);
            if (session.StudentId != context.StudentId || session.OpenTurns.All(t => t.Id != context.TurnId))
                throw new InvalidOperationException("监测账号或轮次已变化。");
            foreach (var task in tasks.Where(t => t.Enabled).OrderBy(t => t.Priority))
            {
                if (!await Invoke(() => CurrentMonitoring(generation, context, task.Id)).ConfigureAwait(false)) break;
                var check = await new Route2AutomationEngine(reader, null, context.StudentId, context.TurnId, Log).RunOnceAsync([task], false, _lifetime.Token).ConfigureAwait(false);
                var accepted = await Invoke(() =>
                {
                    if (!CurrentMonitoring(generation, context, task.Id)) return false;
                    var actual = _settings.Tasks.Single(t => t.Id == task.Id);
                    actual.Status = task.Status; actual.CourseName = task.CourseName; actual.Enabled = task.Enabled;
                    if (check.ConfirmedSelectedTaskIds?.Contains(task.Id) == true) ApplySelection(actual);
                    if (check.ShouldPause) Stop(true, check.Summary);
                    _automationStore.Save(_settings); Publish(); return _running && check.AvailableTaskIds?.Contains(task.Id) == true;
                }).ConfigureAwait(false);
                if (!accepted) continue;
                await _queue.EnqueueAsync(false, Now, Now, async ct =>
                {
                    if (!await Invoke(() => CurrentMonitoring(generation, context, task.Id)).ConfigureAwait(false)) return false;
                    var result = await ExecuteProtectedAsync(task, context, generation, false, ct).ConfigureAwait(false);
                    await Invoke(() =>
                    {
                        if (generation != _generation) return 0;
                        var actual = _settings.Tasks.SingleOrDefault(t => t.Id == task.Id);
                        if (actual is not null)
                        {
                            actual.Status = task.Status; actual.CourseName = task.CourseName; actual.Enabled = task.Enabled;
                            if (result.ConfirmedSelectedTaskIds?.Contains(task.Id) == true) ApplySelection(actual);
                        }
                        if (result.ShouldPause || _operations.HasUnresolvedOperation) Stop(true, result.Summary, _operations.HasUnresolvedOperation);
                        _automationStore.Save(_settings); Publish(); return 0;
                    }).ConfigureAwait(false);
                    return true;
                }, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (AuthenticationRequiredException) { await Invoke(() => { if (generation == _generation) { if (_operations.HasUnresolvedOperation) Stop(true, "执行中认证失效，必须人工核对。", true); _running = false; _session = null; _auth = new(AuthenticationState.Failed, null, "教务会话失效，请重新登录。", null); _status = _auth.Message; Publish(); } return 0; }); }
        catch (OperationCanceledException) { }
        catch (HttpRequestException)
        {
            await Invoke(() =>
            {
                if (generation == _generation)
                {
                    if (_operations.HasEligibleInterruptedSwapRecovery) BeginInterruptedRecovery();
                    else if (_operations.HasUnresolvedOperation) Stop(true, "写入状态不确定，禁止重试，请人工核对。", true);
                    else _status = "读取网络暂不可用，保留监测授权。";
                    Publish();
                }
                return 0;
            }).ConfigureAwait(false);
        }
        catch (Exception) { await FailAutomaticAsync("监测执行失败，请人工核对。", generation).ConfigureAwait(false); }
        finally { await Invoke(() => { _cycle = false; Publish(); return 0; }).ConfigureAwait(false); }
    }

    private bool CurrentMonitoring(long generation, CourseExecutionContext context, Guid taskId) => generation == _generation && _running
        && _runtime.MonitoringAuthorized && !_runtime.SafetyPaused && !_operations.HasUnresolvedOperation
        && _runtime.StudentId == context.StudentId && _runtime.TurnId == context.TurnId
        && _session?.StudentId == context.StudentId && _session.OpenTurns.Any(t => t.Id == context.TurnId)
        && _auth.State == AuthenticationState.Authenticated && _settings.Tasks.Any(t => t.Id == taskId && t.Enabled)
        && (!_settings.EnableSchedule || BeijingClock.IsInWindow(Now, _settings.StartAt, _settings.EndAt));

    private void ApplySelection(AutomationTask selected)
    {
        AutomationTaskRules.DisableConflictingLowerPriorityTasks(_settings.Tasks, selected, Log);
        selected.Enabled = false; selected.Status = "已选中（最终状态确认）";
    }

    private async Task<AutomationCycleResult> ExecuteProtectedAsync(AutomationTask task, CourseExecutionContext context, long generation, bool scheduled, CancellationToken token)
    {
        var client = Client();
        var reader = new ReadOnlyEamsClient(client);
        var session = await reader.ValidateAsync(token).ConfigureAwait(false);
        if (session.StudentId != context.StudentId || session.OpenTurns.All(t => t.Id != context.TurnId)) throw new InvalidOperationException("执行账号或轮次不匹配。");
        var valid = await Invoke(() => scheduled ? CurrentAppointment(generation, context, task.Id) : CurrentMonitoring(generation, context, task.Id)).ConfigureAwait(false);
        if (!valid) throw new OperationCanceledException("执行上下文已变化。");
        using var lease = await _operations.EnterAsync("课程选退课", reader, new ControlledEamsClient(client), token).ConfigureAwait(false);
        valid = await Invoke(() => scheduled ? CurrentAppointment(generation, context, task.Id) : CurrentMonitoring(generation, context, task.Id)).ConfigureAwait(false);
        if (!valid) throw new OperationCanceledException("执行授权已撤销。");
        return await new Route2AutomationEngine(reader, lease, context.StudentId, context.TurnId, Log).RunOnceAsync([task], true, token).ConfigureAwait(false);
    }

    private bool CurrentAppointment(long generation, CourseExecutionContext context, Guid taskId) => !_stopping && generation == _generation
        && !_runtime.SafetyPaused && !_operations.HasUnresolvedOperation && _auth.State == AuthenticationState.Authenticated
        && _appointments.Current is { State: ScheduledSelectionState.Running, RealOperationsAuthorized: true } a
        && a.StudentId == context.StudentId && a.ExecutedTurnId == context.TurnId && a.Tasks.Any(t => t.Id == taskId && t.Enabled);

    private async Task RunAppointmentAsync(ScheduledSelection initial, long generation, bool resumed)
    {
        try
        {
            var session = await new ReadOnlyEamsClient(Client()).ValidateAsync(_lifetime.Token).ConfigureAwait(false);
            var contextAndAppointment = await Invoke(() =>
            {
                if (_stopping || generation != _generation || _auth.State != AuthenticationState.Authenticated || _runtime.SafetyPaused || _operations.HasUnresolvedOperation)
                    return ((CourseExecutionContext?)null, (ScheduledSelection?)null);
                var turn = ScheduledSelectionService.ResolveTurn(session, initial.StudentId, _runtime.SelectedTurnId);
                var due = _appointments.CollectDue(Now, resumed);
                if (due is null) { Publish(); return ((CourseExecutionContext?)null, (ScheduledSelection?)null); }
                return (new CourseExecutionContext(session.StudentId, turn.Id), due);
            }).ConfigureAwait(false);
            var context = contextAndAppointment.Item1;
            var appointment = contextAndAppointment.Item2;
            if (context is null || appointment is null) return;
            await _queue.EnqueueAsync(true, appointment.ExecuteAt, appointment.CreatedAt, async token =>
            {
                var started = await Invoke(() =>
                {
                    if (_stopping || generation != _generation || _auth.State != AuthenticationState.Authenticated || _runtime.SafetyPaused || _operations.HasUnresolvedOperation)
                    { _appointments.RestoreQueuedToArmed(); _blockedDue = true; Publish(); return false; }
                    _appointments.MarkRunning(appointment.Id, Now);
                    var turn = ScheduledSelectionService.ResolveTurn(session, context.StudentId, context.TurnId);
                    _appointments.SetExecutedTurn(appointment.Id, turn); Publish(); return true;
                }).ConfigureAwait(false);
                if (!started) return false;
                var confirmed = new HashSet<Guid>();
                foreach (var task in appointment.Tasks.OrderBy(t => t.Priority))
                {
                    if (!task.Enabled) continue;
                    var result = await ExecuteProtectedAsync(task, context, generation, true, token).ConfigureAwait(false);
                    foreach (var id in result.ConfirmedSelectedTaskIds ?? []) confirmed.Add(id);
                    if (confirmed.Contains(task.Id)) AutomationTaskRules.DisableConflictingLowerPriorityTasks(appointment.Tasks, task, Log);
                    await Invoke(() =>
                    {
                        if (confirmed.Contains(task.Id))
                        {
                            var actual = _settings.Tasks.Where(t => t.CourseCode.Equals(task.CourseCode, StringComparison.OrdinalIgnoreCase)).ToArray();
                            if (actual.Length == 1) ApplySelection(actual[0]);
                            _automationStore.Save(_settings);
                        }
                        _appointments.SaveExecutionProgress(appointment.Id, appointment.Tasks, confirmed); Publish(); return 0;
                    }).ConfigureAwait(false);
                    if (result.ShouldPause || _operations.HasUnresolvedOperation) throw new InvalidOperationException(result.Summary);
                }
                await Invoke(() =>
                {
                    var turn = ScheduledSelectionService.ResolveTurn(session, context.StudentId, context.TurnId);
                    _appointments.MarkSelectionFinished(appointment.Id, turn, appointment.Tasks, confirmed);
                    var current = _appointments.Get(appointment.Id);
                    if (current.CompletionAction == ScheduledCompletionAction.ImportToMonitoring) { _pendingImportFromCurrentRun = true; CompleteImport(current); }
                    else _appointments.MarkFinished(current.Id, ScheduledSelectionState.Completed, "已执行一轮。", Now);
                    Publish(); return 0;
                }).ConfigureAwait(false);
                return true;
            }, _lifetime.Token).ConfigureAwait(false);
        }
        catch (AuthenticationRequiredException)
        {
            await Invoke(() =>
            {
                var current = _appointments.Current;
                if (current?.State == ScheduledSelectionState.Queued) { _appointments.RestoreQueuedToArmed(); _blockedDue = true; }
                else if (current?.State == ScheduledSelectionState.Running && !current.SelectionFinished)
                { _appointments.MarkFinished(current.Id, ScheduledSelectionState.Failed, "执行中认证失效，请人工核对。", Now); Stop(true, "预约执行中认证失效。", true); }
                else _blockedDue = true;
                _session = null; _auth = new(AuthenticationState.Failed, null, "教务会话失效，请重新登录。", null); _status = "预约等待重新登录"; Publish(); return 0;
            }).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await Invoke(() =>
            {
                var current = _appointments.Current;
                if (current?.State == ScheduledSelectionState.Running && !current.SelectionFinished)
                { _appointments.MarkFinished(current.Id, ScheduledSelectionState.Failed, "执行中网络中断，请人工核对。", Now); if (_operations.HasEligibleInterruptedSwapRecovery) BeginInterruptedRecovery(); else Stop(true, "预约执行中网络中断。", true); }
                else { if (current?.State == ScheduledSelectionState.Queued) _appointments.RestoreQueuedToArmed(); _blockedDue = true; _status = "预约读取网络暂不可用，保留授权。"; }
                Publish(); return 0;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Invoke(() =>
            {
                var current = _appointments.Current;
                if (current?.Id == initial.Id && current.State == ScheduledSelectionState.Running && !current.SelectionFinished)
                    _appointments.MarkFinished(current.Id, ex is OperationCanceledException ? ScheduledSelectionState.Interrupted : ScheduledSelectionState.Failed, "执行中断，请人工核对。", Now);
                else if (current?.State == ScheduledSelectionState.Queued) _appointments.RestoreQueuedToArmed();
                if (ex is not OperationCanceledException) Stop(true, "预约执行失败，请人工核对。", true);
                Publish(); return 0;
            }).ConfigureAwait(false);
        }
        finally { await Invoke(() => { _appointmentBusy = false; Publish(); return 0; }).ConfigureAwait(false); }
    }

    private void CompleteImport(ScheduledSelection appointment, bool allowMonitoring = true)
    {
        if (_operations.HasUnresolvedOperation) { _appointments.MarkFinished(appointment.Id, ScheduledSelectionState.Interrupted, "存在待核对操作。", Now); return; }
        var settings = Clone(_settings);
        var added = ScheduledSelectionService.ImportRemainingTasks(appointment, settings);
        _automationStore.Save(settings); _settings = settings;
        if (allowMonitoring && settings.Tasks.Any(t => t.Enabled))
        {
            ValidateTasks(settings.Tasks);
            if (_session is null || _auth.State != AuthenticationState.Authenticated)
            {
                _status = "预约选课和导入已保存，等待验证账号及轮次后交接监测。";
                Publish(); return;
            }
            var turn = appointment.ExecutedTurnId ?? throw new InvalidOperationException("预约缺少执行轮次。");
            if (_session.StudentId != appointment.StudentId || _session.OpenTurns.All(t => t.Id != turn))
            {
                Stop(true, "预约账号或轮次不匹配，监测交接已暂停。", true); Publish(); return;
            }
            SaveRuntime(_runtime with { MonitoringAuthorized = true, SafetyPaused = false, StudentId = appointment.StudentId, TurnId = turn, SelectedTurnId = turn });
        }
        _appointments.MarkImportFinished(appointment.Id);
        _appointments.MarkFinished(appointment.Id, ScheduledSelectionState.Completed, $"执行完成，新增 {added} 门监测课程。", Now);
        if (allowMonitoring && !_stopping && _runtime.MonitoringAuthorized && settings.Tasks.Any(t => t.Enabled))
        { _running = true; _nextCycle = Now; }
        _generation++;
    }

    private void BeginInterruptedRecovery()
    {
        Stop(true, "网络中断后等待核对旧课；禁止重新执行原操作。", true);
        _networkRecoveryPending = true;
        _nextRecovery = Now.AddSeconds(15);
    }

    private async Task RecoverInterruptedSwapAsync(long generation)
    {
        try
        {
            var journal = await Invoke(() => CloneNullable(_operations.ActiveJournal)).ConfigureAwait(false);
            if (journal is null || !_operations.HasEligibleInterruptedSwapRecovery) { await Invoke(() => { _networkRecoveryPending = false; return 0; }); return; }
            var client = Client();
            var reader = new ReadOnlyEamsClient(client);
            var session = await reader.ValidateAsync(_lifetime.Token).ConfigureAwait(false);
            if (session.StudentId != journal.StudentId || session.OpenTurns.All(t => t.Id != journal.TurnId))
                throw new InvalidOperationException("恢复账号或轮次不一致。");
            await _queue.EnqueueAsync(false, Now, Now, async token =>
            {
                var valid = await Invoke(() => !_stopping && generation == _generation && _networkRecoveryPending && _operations.HasEligibleInterruptedSwapRecovery).ConfigureAwait(false);
                if (!valid) return false;
                var result = await _operations.TryRestoreConfirmedDropsAfterNetworkAsync(session.StudentId, journal.TurnId,
                    reader, new ControlledEamsClient(client), token).ConfigureAwait(false);
                await Invoke(() => { _networkRecoveryPending = false; _status = LocalActivityLog.Sanitize(result.Message); _verifiedJournal = false; Publish(); return 0; }).ConfigureAwait(false);
                return true;
            }, _lifetime.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await Invoke(() => { _status = "网络仍不可用，等待只读恢复核验。"; Publish(); return 0; }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await Invoke(() =>
            {
                _networkRecoveryPending = false;
                _operations.ProhibitAutomaticRecovery("恢复验证未完成，禁止自动恢复，必须人工确认。");
                _status = "恢复验证失败，必须人工核对。"; Publish(); return 0;
            }).ConfigureAwait(false);
        }
        finally { await Invoke(() => { _cycle = false; Publish(); return 0; }).ConfigureAwait(false); }
    }

    private Task<int> FailAutomaticAsync(string message, long generation) => Invoke(() =>
    {
        if (generation == _generation) { Stop(true, message, true); Publish(); }
        return 0;
    });

    public async Task ShutdownPrepareAsync(CancellationToken cancellationToken)
    {
        var active = await Invoke(() =>
        {
            if (!_stopping) { _stopping = true; _running = false; _generation++; }
            _status = "服务正在安全停止"; Publish(); return (Work: _activeWork, Authentication: _authenticationWork);
        }).ConfigureAwait(false);
        if (active.Work is not null)
        {
            try { await active.Work.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception) when (active.Work.IsCompleted && !cancellationToken.IsCancellationRequested) { }
        }
        if (active.Authentication is not null)
        {
            try { await active.Authentication.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception) when (active.Authentication.IsCompleted && !cancellationToken.IsCancellationRequested) { }
        }
        await Invoke(() => { if (_appointments.Current?.State == ScheduledSelectionState.Queued) _appointments.RestoreQueuedToArmed(); return 0; }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownPrepareAsync(CancellationToken.None).ConfigureAwait(false);
        _lifetime.Cancel(); _channel.Writer.TryComplete(); await _actor.ConfigureAwait(false); _lifetime.Dispose();
    }
}
