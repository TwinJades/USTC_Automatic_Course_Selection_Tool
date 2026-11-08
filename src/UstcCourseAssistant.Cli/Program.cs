using System.Globalization;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Text.Json;
using UstcCourseAssistant.Cli;
using UstcCourseAssistant.Core.Authentication;
using UstcCourseAssistant.Core.Services;
using UstcCourseAssistant.Core.Update;

return await Cli.RunAsync(args);

internal static class Cli
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && args[0] == LinuxUpdateRunner.Switch) return await LinuxUpdateRunner.RunAsync(args[1..]);
        try
        {
            var words = args.ToList();
            string? root = null;
            var rootIndex = words.IndexOf("--package-root");
            if (rootIndex >= 0)
            {
                if (rootIndex + 1 >= words.Count || words.LastIndexOf("--package-root") != rootIndex) throw new ArgumentException("--package-root 需要唯一的绝对路径。 ");
                root = words[rootIndex + 1];
                words.RemoveRange(rootIndex, 2);
            }
            if (words.Count == 0 || words.SequenceEqual(["--help"]) || words.SequenceEqual(["help"])) { Help(); return 0; }
            var updatePending = words.Remove("--update-pending");
            if (updatePending && !words.SequenceEqual(["serve"])) throw new ArgumentException("更新就绪模式只允许用于服务启动。 ");
            var isolatedData = Environment.GetEnvironmentVariable("USTC_ROUTE2_TEST_DATA_ROOT");
            var isolated = !string.IsNullOrWhiteSpace(isolatedData);
            if (isolated)
            {
                if (!Path.IsPathFullyQualified(isolatedData!)) throw new ArgumentException("测试数据目录必须为绝对路径。 ");
                isolatedData = Path.GetFullPath(isolatedData!);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) throw new ArgumentException("必须通过 --package-root 指定绝对包根路径。 ");
                root = Path.GetFullPath(root);
                if (!Directory.Exists(root)) throw new ArgumentException("包根目录不存在。 ");
            }
            var data = isolated ? isolatedData! : Path.Combine(root!, "data");
            if (words.SequenceEqual(["serve"])) return await ServeAsync(data, root, isolated, updatePending);
            if (words[0] is "service" or "credentials")
            {
                if (isolated) throw new InvalidOperationException("测试数据隔离模式不允许管理系统服务或凭据。 ");
                return await ManageAsync(root!, data, words);
            }
            var (command, parameters, json) = Parse(words);
            if (command == "update apply")
            {
                if (isolated) throw new InvalidOperationException("测试数据隔离模式不允许安装更新。 ");
                return await ApplyUpdateAsync(root!, data);
            }
            if (command == "auth login")
            {
                RequireTerminal();
                Console.Write("使用服务已保存的系统凭据？[y/N]：");
                if (string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) parameters["UseSaved"] = true;
                else
                {
                    Console.Write("账号：");
                    var user = Console.ReadLine() ?? "";
                    if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("账号不能为空。 ");
                    parameters["UserName"] = user;
                    parameters["Password"] = ReadSecret("密码：");
                }
            }
            if (command is "auth submit-code" or "auth submit-captcha") { RequireTerminal(); parameters["Code"] = ReadSecret(command == "auth submit-code" ? "短信数字：" : "图形验证码："); }
            var id = Guid.NewGuid();
            var reply = await ControlClient.SendAsync(data, new ControlRequest(id, command, JsonSerializer.SerializeToElement(parameters)), CancellationToken.None);
            if (!reply.TryGetProperty("Id", out var replyId) || replyId.GetGuid() != id || !reply.TryGetProperty("Success", out var success)) throw new InvalidDataException("控制服务回复无效。 ");
            if (json) Console.WriteLine(JsonSerializer.Serialize(reply, Pretty));
            else
            {
                if (reply.TryGetProperty("Message", out var message)) Console.WriteLine(message.GetString());
                if (reply.TryGetProperty("Data", out var payload) && payload.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)) Console.WriteLine(JsonSerializer.Serialize(payload, Pretty));
            }
            return success.GetBoolean() ? 0 : 1;
        }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        catch (OperationCanceledException) { Console.Error.WriteLine("操作已取消或超时。 "); return 1; }
        catch (AggregateException ex)
        {
            Console.Error.WriteLine("服务或凭据维护失败。");
            foreach (var failure in ex.InnerExceptions)
            {
                var message = failure is MaintenanceProcessRunner.ProcessTerminationUncertainException ? "无法确认维护子进程已退出，产物保留。"
                    : failure.Message.StartsWith("恢复服务配置失败", StringComparison.Ordinal) || failure.Message.StartsWith("恢复服务运行失败", StringComparison.Ordinal)
                      || failure.Message.StartsWith("恢复原凭据或服务配置失败", StringComparison.Ordinal) || failure.Message.StartsWith("恢复原服务运行失败", StringComparison.Ordinal)
                      || failure.Message.StartsWith("临时凭据回收失败", StringComparison.Ordinal) ? failure.Message
                    : "维护步骤失败；请检查服务状态。";
                Console.Error.WriteLine(message);
            }
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or System.Net.Sockets.SocketException or System.Net.Http.HttpRequestException or TimeoutException or JsonException)
        { Console.Error.WriteLine(LocalActivityLog.Sanitize(ex.Message)); return 1; }
    }

    private static (string Command, Dictionary<string, object> Arguments, bool Json) Parse(List<string> words)
    {
        int n = words[0] is "status" or "logs" ? 1 : 2;
        if (words.Count < n) throw new ArgumentException("缺少子命令。 ");
        var command = string.Join(' ', words.Take(n));
        var allowed = command switch
        {
            "status" or "auth status" or "auth logout" or "courses turns" or "courses selected" or "tasks list" or "settings show" or "monitor stop" or "appointment show" or "appointment cancel" or "appointment edit" or "operations status" or "operations verify" or "update check" or "auth login" => "",
            "logs" => "count",
            "auth send-code" or "auth submit-code" or "auth screenshot" or "auth submit-captcha" => "challenge",
            "courses check" => "turn code",
            "tasks add" => "code priority conflict",
            "tasks edit" => "id code priority conflict",
            "tasks remove" or "tasks enable" or "tasks disable" => "id",
            "settings set" => "interval jitter schedule start end",
            "monitor start" => "turn authorize",
            "appointment save" => "at run-when-missed completion task",
            "appointment arm" => "authorize turn",
            "operations acknowledge" => "authorize",
            "update apply" => "confirm",
            _ => throw new ArgumentException("未知命令。使用 help 查看命令。")
        };
        var allowedSet = allowed.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        bool json = false;
        for (int i = n; i < words.Count; i++)
        {
            var option = words[i];
            if (option == "--json")
            {
                if (json || command is not ("status" or "logs" or "auth status" or "courses turns" or "courses selected" or "courses check" or "tasks list" or "settings show" or "appointment show" or "operations status" or "update check")) throw new ArgumentException("此命令不接受 --json。 ");
                json = true; continue;
            }
            if (!option.StartsWith("--", StringComparison.Ordinal) || !allowedSet.Contains(option[2..])) throw new ArgumentException("未知选项：" + option);
            var name = option[2..];
            var key = name switch { "turn" => "TurnId", "challenge" => "ChallengeId", "run-when-missed" => "RunWhenMissed", _ => char.ToUpperInvariant(name[0]) + name[1..] };
            if (name is "authorize" or "confirm")
            {
                if (!result.TryAdd(key, true)) throw new ArgumentException("选项重复：" + option);
                continue;
            }
            if (++i >= words.Count || words[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("选项缺少值：" + option);
            var value = words[i];
            if (name is "conflict" or "task")
            {
                if (!result.TryGetValue(key, out var list)) result[key] = list = new List<string>();
                ((List<string>)list).Add(value); continue;
            }
            object parsed = name switch
            {
                "turn" or "count" or "interval" or "priority" => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : throw new ArgumentException("数字参数无效：" + option),
                "jitter" or "schedule" or "run-when-missed" => value == "true" ? true : value == "false" ? false : throw new ArgumentException("布尔参数必须为 true 或 false：" + option),
                "id" or "challenge" => Guid.TryParse(value, out var guid) && guid != Guid.Empty ? guid.ToString() : throw new ArgumentException("标识无效：" + option),
                _ => value
            };
            if (!result.TryAdd(key, parsed)) throw new ArgumentException("选项重复：" + option);
        }
        string[] required = command switch
        {
            "courses check" => ["TurnId", "Code"], "tasks add" => ["Code"],
            "tasks edit" or "tasks remove" or "tasks enable" or "tasks disable" => ["Id"],
            "monitor start" => ["TurnId", "Authorize"], "appointment arm" or "operations acknowledge" => ["Authorize"],
            "appointment save" => ["At", "RunWhenMissed", "Completion", "Task"], "update apply" => ["Confirm"], _ => []
        };
        foreach (var key in required) if (!result.ContainsKey(key)) throw new ArgumentException("缺少必需参数：" + key);
        if (command == "settings set" && result.Count == 0) throw new ArgumentException("请指定要修改的设置。 ");
        if (command == "tasks edit" && result.Count == 1) throw new ArgumentException("请指定要修改的任务字段。 ");
        return (command, result, json);
    }

    private static async Task<int> ServeAsync(string data, string? root, bool isolated, bool updatePending)
    {
        if (OperatingSystem.IsLinux() && geteuid() == 0) throw new InvalidOperationException("禁止以 root 运行教务服务；请使用专用服务账户。 ");
        if (updatePending && (!OperatingSystem.IsLinux() || isolated || root is null)) throw new InvalidOperationException("更新就绪模式仅允许 systemd Linux 包服务使用。 ");
        if (!isolated && !updatePending && root is not null)
        {
            var transaction = new PackageUpdateTransaction(root);
            if (OperatingSystem.IsLinux())
            {
                if (Directory.Exists(transaction.ActivePath)) throw new InvalidOperationException("存在进行中或待恢复的更新事务，禁止启动业务；请使用包根启动器恢复。 ");
            }
            else
            {
                if (Directory.Exists(transaction.ActivePath) && (!transaction.TryCleanupCompleted() || Directory.Exists(transaction.ActivePath)))
                    throw new InvalidOperationException("存在进行中或待恢复的更新事务，禁止启动业务；请使用包根启动器恢复。 ");
                using var updateLock = transaction.AcquireLock();
            }
        }
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        using var sigterm = OperatingSystem.IsLinux() ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); }) : null;
        IAuthenticationService? authentication = null;
        CourseAssistantController? controller = null;
        Task? run = null;
        var server = new ControlServer(data, async (element, token) =>
        {
            var request = element.Deserialize<ControlRequest>() ?? throw new JsonException("无效请求。 ");
            var active = Volatile.Read(ref controller);
            if (active is null) return new ControlReply(request.Id, false, "busy", "程序正在等待更新提交，业务执行已暂停。", null);
            return await active.ExecuteAsync(request, token);
        }, async token =>
        {
            if (updatePending)
            {
                var transaction = new PackageUpdateTransaction(root!);
                var journal = await LinuxUpdateAuthorization.WaitAsync(transaction, token);
                PendingUpdatePreflight.Validate(root!, journal, Assembly.GetExecutingAssembly().GetName().Version!);
                transaction.SignalReady(journal.Id, journal.Nonce, journal.Version, journal.Rid);
                await transaction.WaitForCommitAndCleanupAsync(journal.Id, journal.Nonce, token);
            }
            var temp = Path.Combine(data, "tmp");
            Directory.CreateDirectory(temp);
            if ((File.GetAttributes(temp) & FileAttributes.ReparsePoint) != 0) throw new IOException("临时目录不能是符号链接。 ");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.SetEnvironmentVariable("TMPDIR", temp);
            Environment.SetEnvironmentVariable("TMP", temp);
            Environment.SetEnvironmentVariable("TEMP", temp);
            authentication = new HttpAuthenticationService(data);
            Func<(string UserName, string Password)?>? credentials = null;
            if (OperatingSystem.IsLinux() && !isolated)
            {
                var vault = new LinuxCredentialVault(new ServiceInstaller(root!, _ => Task.FromResult(false)));
                credentials = () =>
                {
                    if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
                    var stored = vault.ReadServiceCredential();
                    return stored is null ? null : (stored.UserName, stored.Password);
                };
            }
            var initialized = new CourseAssistantController(data, authentication, OperatingSystem.IsLinux(),
                update: async (request, ct) =>
                {
                    if (request.Command != "update check") return new ControlReply(request.Id, false, "update_failed", "请通过 sudo 命令行启动更新维护。", null);
                    if (isolated || root is null) return new ControlReply(request.Id, false, "update_failed", "测试数据隔离模式不访问正式更新。", null);
                    var rid = OperatingSystem.IsLinux() ? "linux-x64" : OperatingSystem.IsWindows() ? "win-x64" : throw new PlatformNotSupportedException();
                    using var updater = new DirectoryUpdateService(root, rid);
                    var release = await updater.CheckAsync(Assembly.GetExecutingAssembly().GetName().Version!, ct);
                    return new ControlReply(request.Id, true, "ok", release is null ? "当前已是最新正式版本。" : "发现新版本。", release is null ? null : JsonSerializer.SerializeToElement(release));
                },
                credentials: credentials);
            Volatile.Write(ref controller, initialized);
            run = initialized.RunAsync(stop.Token);
        }, async () =>
        {
            stop.Cancel();
            try
            {
                if (controller is not null)
                {
                    await controller.ShutdownPrepareAsync(CancellationToken.None);
                    if (run is not null) try { await run; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                    await controller.DisposeAsync();
                }
            }
            finally { authentication?.Dispose(); }
        });
        try { await server.RunAsync(stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { Console.CancelKeyPress -= cancel; }
        return 0;
    }

    private static async Task<int> ApplyUpdateAsync(string root, string data)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("应用内安装仅支持 systemd Linux 服务；请手动更新。 ");
        if (geteuid() != 0) throw new UnauthorizedAccessException("更新安装需要 sudo。 ");
        using var cancellation = new CancellationTokenSource();
        var installer = new ServiceInstaller(root, _ => Task.FromResult(false));
        await installer.ValidateInstalledPackageAsync(cancellation.Token);
        var serviceStatus = await installer.StatusAsync(cancellation.Token);
        if (!serviceStatus.Split('\n').Any(line => line.Trim() == "ActiveState=active"))
            throw new InvalidOperationException("应用内更新需要已运行的 systemd 服务；前台运行请手动更新。 ");
        using var updater = new DirectoryUpdateService(root, "linux-x64");
        var release = await updater.CheckAsync(Assembly.GetExecutingAssembly().GetName().Version!, cancellation.Token);
        if (release is null) { Console.WriteLine("当前已是最新正式版本。 "); return 0; }
        async Task<JsonElement> Send(string command, bool wait = false)
        {
            var id = Guid.NewGuid();
            return await ControlClient.SendAsync(data, new ControlRequest(id, command, JsonSerializer.SerializeToElement(new { })), cancellation.Token, wait);
        }
        var frozen = false;
        try
        {
            var reply = await Send("update prepare-stop", true);
            if (!reply.GetProperty("Success").GetBoolean()) throw new InvalidOperationException(reply.GetProperty("Message").GetString() ?? "安全停机准备失败。");
            frozen = true;
            var prepared = await updater.DownloadAndPrepareAsync(release, cancellation.Token);
            using (prepared.Lock)
            {
                try { using var helper = prepared.Transaction.StartHelper(prepared.Journal); }
                catch (PackageUpdateTransaction.HelperTerminationUncertainException) { throw; }
                catch
                {
                    prepared.Transaction.Rollback(prepared.Journal);
                    prepared.Transaction.Cleanup();
                    throw;
                }
            }
            frozen = false;
            Console.WriteLine("更新维护已启动；辅助进程将安全重启服务并完成或回滚更新。 ");
            return 0;
        }
        catch (PackageUpdateTransaction.HelperTerminationUncertainException)
        {
            frozen = false;
            throw;
        }
        finally
        {
            if (frozen) await Send("update cancel-prepare");
        }
    }

    private static async Task<int> ManageAsync(string root, string data, List<string> words)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("服务和系统凭据仅支持 Linux。 ");
        if (words.Count != 2) throw new ArgumentException("服务或凭据子命令参数无效。 ");
        async Task<bool> Prepare(CancellationToken token)
        {
            var id = Guid.NewGuid();
            var reply = await ControlClient.SendAsync(data, new ControlRequest(id, "service prepare-stop", JsonSerializer.SerializeToElement(new { })), token, waitForSafeStop: true);
            return reply.GetProperty("Id").GetGuid() == id && reply.GetProperty("Success").GetBoolean();
        }
        var installer = new ServiceInstaller(root, Prepare);
        if (words[0] == "service")
        {
            switch (words[1])
            {
                case "install": await installer.InstallAsync(); break;
                case "start": await installer.StartAsync(); break;
                case "stop": await installer.StopAsync(); break;
                case "status": Console.WriteLine(await installer.StatusAsync()); return 0;
                case "uninstall": await installer.UninstallAsync(); break;
                default: throw new ArgumentException("未知服务命令。 ");
            }
        }
        else
        {
            var vault = new LinuxCredentialVault(installer);
            switch (words[1])
            {
                case "status": Console.WriteLine(vault.Exists() ? "已保存系统加密凭据。" : "没有保存系统凭据。"); return 0;
                case "delete": await vault.DeleteAsync(); break;
                case "set":
                    RequireTerminal(); Console.Write("账号："); var user = Console.ReadLine() ?? "";
                    var password = ReadSecret("密码："); await vault.SetAsync(user, password); password = ""; break;
                default: throw new ArgumentException("未知凭据命令。 ");
            }
        }
        Console.WriteLine("完成。 "); return 0;
    }


    private static void RequireTerminal()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected) throw new ArgumentException("需要交互式终端；不接受参数、管道或环境变量中的凭据。 ");
    }
    private static string ReadSecret(string label)
    {
        Console.Write(label); var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Escape) throw new OperationCanceledException();
            if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
            else if (!char.IsControl(key.KeyChar)) value.Append(key.KeyChar);
        }
        Console.WriteLine(); return value.ToString();
    }
    [DllImport("libc")]
    private static extern uint geteuid();

    private static void Help() => Console.WriteLine("""
USTC 选课助手（使用 --package-root 指定包根）
  serve | status [--json] | logs [--count N] [--json]
  auth status|login|logout|send-code|submit-code|screenshot|submit-captcha [--challenge ID]
  courses turns|selected|check --turn ID --code CODE
  tasks list|add|edit|remove|enable|disable；add --code CODE [--priority N] [--conflict CODE ...]
  settings show|set [--interval SECONDS] [--jitter true|false] [--schedule true|false] [--start HH:mm] [--end HH:mm]
  monitor start --turn ID --authorize | monitor stop
  appointment show|save|arm|cancel|edit；save --at 'yyyy-MM-dd HH:mm:ss' --run-when-missed true|false --completion stop|monitor --task ID ...
  appointment arm --authorize [--turn ID]
  operations status|verify|acknowledge --authorize
  update check|apply --confirm
  service install|start|stop|status|uninstall
  credentials set|delete|status
密码和短信只从交互终端隐藏输入，不接受命令行、管道或环境变量。
""");
}
