using System.Text;
using System.Text.Json;
using UstcCourseAssistant.Cli;
using UstcCourseAssistant.Core.Services;

internal static class CliSecurityChecks
{
    public static async Task RunAsync(string root)
    {
        const string secret = "fixture-secret-never-log-1729";
        var id = Guid.NewGuid();
        // The limit counts UTF-8 bytes before the newline, not characters.
        var prefix = "{\"Value\":\"";
        var suffix = "\"}";
        var exact = prefix + new string('x', ControlProtocol.MaxBytes - prefix.Length - suffix.Length) + suffix;
        using (var input = Input(exact + "\n"))
            if ((await ControlProtocol.ReadAsync<JsonElement>(input, CancellationToken.None)).GetProperty("Value").GetString()!.Length != ControlProtocol.MaxBytes - prefix.Length - suffix.Length)
                throw new InvalidOperationException("64 KiB 请求边界读取失败。");
        await RejectRequestAsync(exact + " " + secret + "\n", secret);
        await RejectRequestAsync("{\"Value\":\"" + secret + "\"}", secret);
        await RejectRequestAsync("{\"Value\":\"" + secret + "\",}\n", secret);
        await RejectReplyAsync(id, new { Id = id, Sequence = 0, End = true, Data = "***" }, secret);
        await RejectReplyAsync(id, new { Id = id, Sequence = 0, End = true, Data = "" }, secret);
        await RejectReplyAsync(id, new { Id = id, Sequence = 0, End = true, Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"Id\":\"" + Guid.NewGuid() + "\",\"Message\":\"" + secret + "\"}")) }, secret);
        await CheckStartupAndSingletonAsync(Path.Combine(root, "server"));
        if (OperatingSystem.IsWindows()) await CheckProcessCancellationAsync(root);
    }

    private static MemoryStream Input(string text) => new(Encoding.UTF8.GetBytes(text));

    private static async Task RejectRequestAsync(string text, string secret)
    {
        using var input = Input(text);
        try { await ControlProtocol.ReadAsync<JsonElement>(input, CancellationToken.None); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
        {
            if (error.ToString().Contains(secret, StringComparison.Ordinal)) throw new InvalidOperationException("请求解析错误泄露正文。");
            return;
        }
        throw new InvalidOperationException("非法请求未被拒绝。");
    }

    private static async Task RejectReplyAsync(Guid id, object frame, string secret)
    {
        using var input = Input(JsonSerializer.Serialize(frame) + "\n");
        try { await ControlProtocol.ReadReplyAsync(input, id, CancellationToken.None); }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException)
        {
            if (error.ToString().Contains(secret, StringComparison.Ordinal)) throw new InvalidOperationException("回复解析错误泄露正文。");
            return;
        }
        throw new InvalidOperationException("非法回复未被拒绝。");
    }

    private static async Task CheckStartupAndSingletonAsync(string root)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdowns = 0;
        var dispatched = 0;
        var server = new ControlServer(root, (request, _) =>
        {
            Interlocked.Increment(ref dispatched);
            var requestId = request.GetProperty("Id").GetGuid();
            if (request.GetProperty("Command").GetString() == "bad") throw new ArgumentException("fixture-secret-never-log-1729");
            return Task.FromResult<object>(new ControlReply(requestId, initialized.Task.IsCompletedSuccessfully, initialized.Task.IsCompletedSuccessfully ? "ok" : "busy", "fixture", null));
        }, async token => { entered.TrySetResult(); await initialized.Task.WaitAsync(token); }, () => { Interlocked.Increment(ref shutdowns); return Task.CompletedTask; });
        var running = server.RunAsync(stop.Token);
        try
        {
            await entered.Task.WaitAsync(stop.Token);
            var second = new ControlServer(root, (_, _) => Task.FromResult<object>(new object()), _ => Task.CompletedTask, () => Task.CompletedTask);
            try { await second.RunAsync(stop.Token); throw new InvalidOperationException("第二个服务获得了单实例锁。"); }
            catch (IOException) { }
            var requestId = Guid.NewGuid();
            var request = new ControlRequest(requestId, "status", JsonSerializer.SerializeToElement(new { }));
            var pending = await ControlClient.SendAsync(root, request, stop.Token);
            if (pending.GetProperty("Success").GetBoolean() || pending.GetProperty("Code").GetString() != "busy" || dispatched != 1)
                throw new InvalidOperationException("启动等待期间未保持拒绝业务状态。");
            initialized.TrySetResult();
            var ready = await ControlClient.SendAsync(root, request, stop.Token);
            if (!ready.GetProperty("Success").GetBoolean()) throw new InvalidOperationException("启动完成后控制连接不可用。");
            var bad = await ControlClient.SendAsync(root, new ControlRequest(Guid.NewGuid(), "bad", JsonSerializer.SerializeToElement(new { })), stop.Token);
            if (bad.GetProperty("Success").GetBoolean() || bad.ToString().Contains("fixture-secret-never-log-1729", StringComparison.Ordinal))
                throw new InvalidOperationException("控制异常未返回脱敏失败。");
            var after = await ControlClient.SendAsync(root, request, stop.Token);
            if (!after.GetProperty("Success").GetBoolean()) throw new InvalidOperationException("控制异常后服务不可用。");
        }
        finally
        {
            stop.Cancel();
            try { await running; } catch (OperationCanceledException) { }
        }
        if (shutdowns != 1) throw new InvalidOperationException("服务关闭回调次数错误。");
    }
    private static async Task CheckProcessCancellationAsync(string root)
    {
        Directory.CreateDirectory(root);
        var started = Path.Combine(root, "process-started");
        var finished = Path.Combine(root, "process-finished");
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var script = "[IO.File]::WriteAllText(" + Quote(started) + ",'started'); Start-Sleep -Seconds 3; [IO.File]::WriteAllText(" + Quote(finished) + ",'finished')";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var child = MaintenanceProcessRunner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], cancellation.Token);
        while (!File.Exists(started))
        {
            if (child.IsCompleted) { await child; throw new InvalidOperationException("子进程未启动。"); }
            await Task.Delay(25, cancellation.Token);
        }
        cancellation.Cancel();
        try { await child; throw new InvalidOperationException("取消未中止子进程。"); }
        catch (OperationCanceledException) { }
        await Task.Delay(TimeSpan.FromSeconds(4));
        if (File.Exists(finished)) throw new InvalidOperationException("取消返回后子进程仍然运行。");
    }
}
