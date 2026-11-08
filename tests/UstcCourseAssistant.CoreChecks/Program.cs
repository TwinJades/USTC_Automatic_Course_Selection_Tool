using UstcCourseAssistant.Core.Services;

if (args.Length == 2 && args[0] == "--process-identity-probe")
{
    ProcessIdentityChecks.WriteIdentity(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture));
    return;
}

await ProcessIdentityChecks.RunAsync();
LoginFieldsChecks.Run();

var instant = new DateTimeOffset(2026, 11, 4, 1, 0, 0, TimeSpan.Zero);
var clock = new FixedClock(instant);
Assert(BeijingClock.Now(clock) == new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.FromHours(8)),
    "UTC 01:00 必须对应北京时间09:00");
Assert(BeijingClock.Now(clock).Offset == TimeSpan.FromHours(8), "对外业务时间必须携带+08:00");
foreach (var offset in new[] { TimeSpan.Zero, TimeSpan.FromHours(-7), TimeSpan.FromHours(8) })
{
    var represented = instant.ToOffset(offset);
    Assert(BeijingClock.IsInWindow(represented, new TimeOnly(9, 0), new TimeOnly(10, 0)),
        "同一时刻的偏移表示不得改变监测窗口");
    Assert(!BeijingClock.IsInWindow(represented.AddTicks(-1), new TimeOnly(9, 0), new TimeOnly(10, 0)),
        "开始前不得提前进入窗口");
}
foreach (var hour in new[] { 23, 0, 1 })
{
    var local = new DateTimeOffset(2026, 11, 4, hour, 0, 0, TimeSpan.FromHours(8));
    Assert(BeijingClock.IsInWindow(local.ToUniversalTime(), new TimeOnly(23, 0), new TimeOnly(1, 0)),
        "跨午夜窗口应包含端点及凌晨");
}
foreach (var hour in new[] { 2, 12, 22 })
{
    var local = new DateTimeOffset(2026, 11, 4, hour, 0, 0, TimeSpan.FromHours(8));
    Assert(!BeijingClock.IsInWindow(local, new TimeOnly(23, 0), new TimeOnly(1, 0)),
        "跨午夜窗口不得扩大到白天");
}
Console.WriteLine("[通过] 北京时间偏移、窗口边界与跨午夜行为");

var repository = new DirectoryInfo(AppContext.BaseDirectory);
while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "UstcCourseAssistant.sln"))) repository = repository.Parent;
if (repository is null) throw new InvalidOperationException("找不到测试仓库根目录。");
var testData = Path.Combine(repository.FullName, "artifacts", "core-checks");
var testRoot = Path.Combine(testData, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
try
{
    ControllerChecks.CheckRuntimeStateStore(Path.Combine(testRoot, "runtime"));
    await ControllerChecks.CheckCommandsAsync(Path.Combine(testRoot, "commands"));
    await ControllerChecks.CheckRestartAsync(Path.Combine(testRoot, "restart"));
    ControllerChecks.CheckMissedAppointments(Path.Combine(testRoot, "appointments"));
    await ControllerChecks.CheckPendingImportAsync(Path.Combine(testRoot, "import"));
    await IpcChecks.RunAsync(Path.Combine(testRoot, "ipc"));
    PortableMigrationChecks.Run(Path.Combine(testRoot, "migration"));
    await HttpAuthenticationChecks.RunAsync(testRoot);
    Console.WriteLine("[通过] 控制器、控制协议及迁移行为检查");
}
finally { new RecycleService(testData).Recycle(testRoot); }

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class FixedClock(DateTimeOffset instant) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => instant;
}
