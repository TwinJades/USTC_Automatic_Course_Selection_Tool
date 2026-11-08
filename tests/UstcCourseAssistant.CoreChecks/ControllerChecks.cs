using System.Net;
using System.Text;
using System.Text.Json;
using UstcCourseAssistant.Core.Authentication;
using UstcCourseAssistant.Core.Services;

internal static class ControllerChecks
{
    // The test runner supplies a private, disposable test directory.
    public static void CheckRuntimeStateStore(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "runtime-state.json");
        var store = new RuntimeStateStore(path);
        var initial = store.Load();
        Require(!initial.State.MonitoringAuthorized && !initial.State.SafetyPaused, "new state must not authorize monitoring");
        var authorized = new RuntimeState { MonitoringAuthorized = true, StudentId = 100, TurnId = 1, SelectedTurnId = 1 };
        store.Save(authorized);
        Require(store.Load().State == authorized, "authorized context must round-trip");
        store.Save(authorized with { MonitoringAuthorized = false });
        File.WriteAllText(path, "{");
        var corrupt = store.Load();
        Require(!corrupt.State.MonitoringAuthorized && corrupt.State.SafetyPaused && corrupt.Warning is not null,
            "corrupt primary and even a valid backup must not authorize execution");
        File.WriteAllText(path, "{}");
        Require(store.Load().State.SafetyPaused, "missing fields must fail closed");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static async Task CheckCommandsAsync(string directory)
    {
        using var auth = new TestAuthentication();
        using var client = new HttpClient(new SessionHandler()) { BaseAddress = new Uri("https://jw.ustc.edu.cn/") };
        await using var controller = new CourseAssistantController(directory, auth, true, TimeProvider.System, null, () => client);
        static ControlRequest Request(string command, object arguments) => new(Guid.NewGuid(), command, JsonSerializer.SerializeToElement(arguments));
        async Task<ControlReply> Send(string command, object arguments) => await controller.ExecuteAsync(Request(command, arguments), CancellationToken.None);
        var added = await Send("tasks add", new { Code = "TEST01", Priority = 1 });
        Require(added.Success, "task add must persist");
        var denied = await Send("monitor start", new { TurnId = 1, Authorize = false });
        Require(!denied.Success && denied.Code == "authorization_required", "monitor requires explicit authorization");
        var started = await Send("monitor start", new { TurnId = 1, Authorize = true });
        Require(started.Success && controller.Snapshot.MonitoringAuthorized, "monitor must bind validated identity");
        var stop = await Send("monitor stop", new { });
        Require(stop.Success && !controller.Snapshot.MonitoringAuthorized, "manual stop must revoke authorization");
        var independent = new UstcCourseAssistant.Core.Models.AutomationTask { CourseCode = "OTHER01", Priority = 1 };
        var draft = await Send("appointment save", new { At = "2099-01-01 09:00:00", RunWhenMissed = false, Completion = "stop", Tasks = new[] { independent } });
        Require(draft.Success && controller.Snapshot.Appointment?.Tasks.Single().CourseCode == "OTHER01", "appointment must preserve independent task snapshot");
        var duplicateForms = await Send("appointment save", new { At = "2099-01-01 09:00:00", RunWhenMissed = false, Completion = "stop", Task = new[] { independent.Id }, Tasks = new[] { independent } });
        Require(!duplicateForms.Success, "appointment task forms must be mutually exclusive");
        var unapprovedManual = await Send("operations execute", new { PrecheckId = Guid.NewGuid(), Authorize = false });
        Require(!unapprovedManual.Success && unapprovedManual.Code == "authorization_required", "manual operation requires explicit authorization");
        var update = await Send("update check", new { });
        Require(!update.Success && update.Code == "update_failed", "absent updater must not report success");
        var snapshot = controller.Snapshot;
        snapshot.Monitoring.Tasks.Clear();
        Require(controller.Snapshot.Monitoring.Tasks.Count == 1, "published state must be independent copies");
    }

    public static async Task CheckRestartAsync(string directory)
    {
        async Task Check(string name, bool linux, long expectedStudent, bool turnOpen, bool shouldRun, bool paused)
        {
            var path = Path.Combine(directory, name);
            Directory.CreateDirectory(path);
            new RuntimeStateStore(Path.Combine(path, "runtime-state.json")).Save(new RuntimeState
                { MonitoringAuthorized = true, StudentId = expectedStudent, TurnId = 1, SelectedTurnId = 1 });
            new AutomationStore(Path.Combine(path, "automation-settings.json")).Save(new UstcCourseAssistant.Core.Models.AutomationSettings
                { Tasks = [new UstcCourseAssistant.Core.Models.AutomationTask { CourseCode = "TEST01" }] });
            var handler = new SessionHandler(turnOpen);
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://jw.ustc.edu.cn/") };
            using var auth = new TestAuthentication();
            await using var controller = new CourseAssistantController(path, auth, linux, TimeProvider.System, null, () => client);
            using var cancel = new CancellationTokenSource();
            var run = controller.RunAsync(cancel.Token);
            for (var i = 0; i < 100 && controller.Snapshot.Session is null; i++) await Task.Delay(10);
            var snapshot = controller.Snapshot;
            Require(snapshot.Session is not null, name + " session validation must complete");
            Require(snapshot.MonitoringRunning == shouldRun && snapshot.SafetyPaused == paused, name + " restart state");
            Require(handler.Mutations == 0, name + " must not submit fixture writes");
            cancel.Cancel(); await run;
        }
        await Check("linux", true, 100, true, true, false);
        await Check("windows", false, 100, true, false, false);
        await Check("wrong-account", true, 101, true, false, true);
        await Check("closed-turn", true, 100, false, false, true);
    }

    public static void CheckMissedAppointments(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var runWhenMissed in new[] { false, true })
        {
            var path = Path.Combine(directory, runWhenMissed ? "late-run.json" : "late-skip.json");
            var store = new ScheduledSelectionStore(path);
            var now = new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.FromHours(8));
            var service = new ScheduledSelectionService(store);
            var appointment = new UstcCourseAssistant.Core.Models.ScheduledSelection
            {
                StudentId = 100, ExecuteAt = now.AddMinutes(1), RunWhenMissed = runWhenMissed,
                RealOperationsAuthorized = true,
                Tasks = [new UstcCourseAssistant.Core.Models.AutomationTask { CourseCode = "TEST01" }]
            };
            service.Arm(appointment, now);
            var due = service.CollectDue(now.AddMinutes(2), resumed: true);
            Require(runWhenMissed ? due is not null : due is null && service.Current?.State == UstcCourseAssistant.Core.Models.ScheduledSelectionState.Missed,
                "late appointment must honor RunWhenMissed");
        }
    }
    public static async Task CheckPendingImportAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var now = DateTimeOffset.UtcNow;
        var service = new ScheduledSelectionService(new ScheduledSelectionStore(Path.Combine(directory, "scheduled-selections.json")));
        var appointment = new UstcCourseAssistant.Core.Models.ScheduledSelection
        {
            StudentId = 100, ExecuteAt = now.AddSeconds(1), RunWhenMissed = true, RealOperationsAuthorized = true,
            CompletionAction = UstcCourseAssistant.Core.Models.ScheduledCompletionAction.ImportToMonitoring,
            Tasks = [new UstcCourseAssistant.Core.Models.AutomationTask { CourseCode = "TEST01" }]
        };
        service.Arm(appointment, now);
        var due = service.CollectDue(now.AddSeconds(2), resumed: true)!;
        service.MarkRunning(due.Id, now.AddSeconds(2));
        service.MarkSelectionFinished(due.Id, new UstcCourseAssistant.Core.Models.TurnInfo(1, "test", "test"), due.Tasks);
        using var auth = new TestAuthentication();
        using var controller = new CancellationTokenSource();
        await using var assistant = new CourseAssistantController(directory, auth, true);
        var run = assistant.RunAsync(controller.Token);
        await Task.Delay(350);
        Require(assistant.Snapshot.Appointment is { SelectionFinished: true, ImportFinished: false },
            "pending handoff must survive when no session exists");
        controller.Cancel(); await run;
    }

 
    private sealed class TestAuthentication : IAuthenticationService
    {
        public AuthenticationSnapshot Snapshot { get; private set; } = new(AuthenticationState.SignedOut, null, "signed out", null);
        public Task<AuthenticationSnapshot> LoginAsync(string userName, string password, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected fixture login");
        public Task<AuthenticationSnapshot> RequestSmsCodeAsync(Guid challengeId, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected fixture SMS");
        public Task<AuthenticationSnapshot> SubmitSmsCodeAsync(Guid challengeId, string code, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected fixture SMS");
        public Task<string> ScreenshotChallengeAsync(Guid challengeId, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected fixture screenshot");
        public Task<AuthenticationSnapshot> SubmitCaptchaAsync(Guid challengeId, string code, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected fixture captcha");
        public void Logout() => Snapshot = new(AuthenticationState.SignedOut, null, "signed out", null);
        public EamsSession GetAuthenticatedSession() => throw new AuthenticationRequiredException();
        public void Dispose() { }
    }

    private sealed class SessionHandler(bool turnOpen = true) : HttpMessageHandler
     {
        public int Mutations { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/for-std/course-table")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("/for-std/course-table/100", UriKind.Relative) } });
            if (path == "/for-std/course-table/100") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            if (path == "/ws/for-std/course-select/open-turns")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(turnOpen ? "[{\"id\":1,\"name\":\"test\",\"semesterName\":\"test\"}]" : "[]", Encoding.UTF8, "application/json") });
            if (path is "/ws/for-std/course-select/selected-lessons" or "/ws/for-std/course-select/addable-lessons")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
            Mutations++;
            throw new InvalidOperationException("Unexpected fixture request: " + path);
        }
    }
}
