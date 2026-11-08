using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using UstcCourseAssistant.Core.Services;

namespace UstcCourseAssistant.Cli;

internal sealed class ControlServer(string dataRoot, Func<JsonElement, CancellationToken, Task<object>> dispatch, Func<CancellationToken, Task> initialize, Func<Task> shutdown)
{
    internal async Task RunAsync(CancellationToken token)
    {
        var root = Path.GetFullPath(dataRoot);
        for (string? current = root; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("控制目录路径不能包含符号链接或重解析点。 ");
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("控制目录不能是符号链接。");
        using var instance = new FileStream(Path.Combine(root, "service.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try { await RunLockedAsync(root, token); }
        finally { await shutdown(); }
    }

    private async Task RunLockedAsync(string root, CancellationToken token)
    {
        using var startupStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (OperatingSystem.IsWindows())
        {
            var connections = new List<Task>();
            Task? pipeStartup = null;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var pipe = new NamedPipeServerStream(ControlProtocol.PipeName(root), PipeDirection.InOut, 16,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    try
                    {
                        pipeStartup ??= initialize(startupStop.Token);
                        var accept = pipe.WaitForConnectionAsync(token);
                        if (await Task.WhenAny(accept, pipeStartup) == pipeStartup) await pipeStartup;
                        await accept;
                    }
                    catch { pipe.Dispose(); throw; }
                    connections.RemoveAll(t => t.IsCompleted);
                    connections.Add(ServeAsync(pipe, token));
                }
            }
            finally
            {
                startupStop.Cancel();
                try { if (pipeStartup is not null) await pipeStartup; }
                catch (OperationCanceledException) when (startupStop.IsCancellationRequested) { }
                finally { await Task.WhenAll(connections); }
            }
            return;
        }
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(root, "control.sock");
        if (System.Text.Encoding.UTF8.GetByteCount(path) > 107) throw new IOException("包路径过长，请移至较短路径。");
        var recycle = new RecycleService(root);
        if (Path.Exists(path)) recycle.Recycle(path);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(16);
        var clients = new List<Task>();
        Task? startup = null;
        try
        {
            startup = initialize(startupStop.Token);
            while (!token.IsCancellationRequested)
            {
                var accepting = listener.AcceptAsync(token).AsTask();
                if (await Task.WhenAny(accepting, startup) == startup) await startup;
                var client = await accepting;
                if (!AllowedPeer(client)) { client.Dispose(); continue; }
                clients.RemoveAll(t => t.IsCompleted);
                clients.Add(ServeAsync(new NetworkStream(client, ownsSocket: true), token));
            }
        }
        finally
        {
            startupStop.Cancel();
            try { if (startup is not null) await startup; }
            catch (OperationCanceledException) when (startupStop.IsCancellationRequested) { }
            finally
            {
                listener.Close();
                await Task.WhenAll(clients);
                if (Path.Exists(path)) recycle.Recycle(path);
            }
        }
    }

    private async Task ServeAsync(Stream stream, CancellationToken stop)
    {
        await using var owned = stream;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var request = await ControlProtocol.ReadAsync<JsonElement>(stream, timeout.Token);
            if (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("Id", out var id) || !id.TryGetGuid(out var requestId) || requestId == Guid.Empty || !request.TryGetProperty("Command", out var command) || command.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("控制请求格式无效。 ");
            if (command.GetString() is "service prepare-stop" or "update prepare-stop")
                timeout.CancelAfter(Timeout.InfiniteTimeSpan);
            object result;
            try { result = await dispatch(request, timeout.Token); }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or JsonException)
            { result = new ControlReply(requestId, false, "invalid_arguments", "控制请求参数无效。", null); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { result = new ControlReply(requestId, false, "internal_error", "控制请求处理失败。", null); }
            await ControlProtocol.WriteReplyAsync(stream, requestId, result, timeout.Token);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        {
            // 对端断连或坏输入不终止常驻服务；不记录请求正文及异常携带的敏感数据。
        }
    }

    private static bool AllowedPeer(Socket socket)
    {
        var credentials = new PeerCredentials();
        uint size = (uint)Marshal.SizeOf<PeerCredentials>();
        return getsockopt(socket.Handle.ToInt32(), 1, 17, ref credentials, ref size) == 0
            && (credentials.UserId == 0 || credentials.UserId == geteuid());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PeerCredentials { public int ProcessId; public uint UserId; public uint GroupId; }
    [DllImport("libc", SetLastError = true)]
    private static extern int getsockopt(int socket, int level, int option, ref PeerCredentials value, ref uint length);
    [DllImport("libc")]
    private static extern uint geteuid();
}
