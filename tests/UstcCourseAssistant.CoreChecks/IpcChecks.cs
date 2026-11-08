using System.Text;
using System.Text.Json;
using UstcCourseAssistant.Cli;
using UstcCourseAssistant.Core.Services;

internal static class IpcChecks
{
    public static async Task RunAsync(string directory)
    {
        await CliSecurityChecks.RunAsync(Path.Combine(directory, "security"));
        var id = Guid.NewGuid();
        var payload = new string('测', 50000);
        var reply = new ControlReply(id, true, "ok", "成功", JsonSerializer.SerializeToElement(payload));
        using var stream = new MemoryStream();
        await ControlProtocol.WriteReplyAsync(stream, id, reply, CancellationToken.None);
        if (stream.Length <= ControlProtocol.MaxBytes) throw new InvalidOperationException("大回复没有分帧。 ");
        stream.Position = 0;
        var read = await ControlProtocol.ReadReplyAsync(stream, id, CancellationToken.None);
        if (read.GetProperty("Data").GetString() != payload) throw new InvalidOperationException("大回复重组不一致。 ");

        const string secret = "fixture-secret-must-not-leak";
        await ExpectFailureAsync(JsonSerializer.Serialize(new { Id = id, Sequence = 1, End = true, Data = Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)) }) + "\n", id, secret);
        await ExpectFailureAsync(JsonSerializer.Serialize(new { Id = Guid.NewGuid(), Sequence = 0, End = true, Data = Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)) }) + "\n", id, secret);
        await ExpectFailureAsync(JsonSerializer.Serialize(new { Id = id, Sequence = 0, End = false, Data = Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)) }) + "\n", id, secret);
        await ExpectFailureAsync(new string('x', ControlProtocol.MaxBytes + 1) + secret + "\n", id, secret);

        using var oversized = new MemoryStream();
        var big = new ControlReply(id, true, "ok", "", JsonSerializer.SerializeToElement(new string('x', ControlProtocol.MaxReplyBytes + 1)));
        await ControlProtocol.WriteReplyAsync(oversized, id, big, CancellationToken.None);
        oversized.Position = 0;
        var failure = await ControlProtocol.ReadReplyAsync(oversized, id, CancellationToken.None);
        if (failure.GetProperty("Success").GetBoolean() || failure.GetProperty("Code").GetString() != "response_too_large") throw new InvalidOperationException("过大回复没有返回结构化失败。 ");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = new ControlServer(directory, (request, _) =>
        {
            var command = request.GetProperty("Command").GetString();
            var requestId = request.GetProperty("Id").GetGuid();
            return Task.FromResult<object>(new ControlReply(requestId, command == "status", command == "status" ? "ok" : "invalid_arguments", "fixture", null));
        }, _ => Task.CompletedTask, () => Task.CompletedTask);
        var running = server.RunAsync(cancellation.Token);
        try
        {
            var requestId = Guid.NewGuid();
            JsonElement response;
            if (OperatingSystem.IsLinux())
            {
                for (var attempt = 0; !Path.Exists(Path.Combine(directory, "control.sock")) && attempt < 100; attempt++) await Task.Delay(20, cancellation.Token);
            }
            response = await ControlClient.SendAsync(directory, new ControlRequest(requestId, "status", JsonSerializer.SerializeToElement(new { })), cancellation.Token);
            if (!response.GetProperty("Success").GetBoolean() || response.GetProperty("Id").GetGuid() != requestId) throw new InvalidOperationException("IPC 状态请求失败。");
        }
        finally
        {
            cancellation.Cancel();
            try { await running; } catch (OperationCanceledException) { }
        }
    }

    private static async Task ExpectFailureAsync(string frame, Guid id, string secret)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(frame));
        try { await ControlProtocol.ReadReplyAsync(stream, id, CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            if (ex.ToString().Contains(secret, StringComparison.Ordinal)) throw new InvalidOperationException("异常泄露敏感正文。 ");
            return;
        }
        throw new InvalidOperationException("错误帧未被拒绝。 ");
    }
}
