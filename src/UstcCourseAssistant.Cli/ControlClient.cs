using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;

namespace UstcCourseAssistant.Cli;

internal static class ControlClient
{
    internal static async Task<JsonElement> SendAsync(string dataRoot, object request, CancellationToken token, bool waitForSafeStop = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (!waitForSafeStop) timeout.CancelAfter(TimeSpan.FromSeconds(30));
        Stream stream;
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", ControlProtocol.PipeName(dataRoot), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.ConnectAsync(timeout.Token); }
            catch { pipe.Dispose(); throw; }
            stream = pipe;
        }
        else if (OperatingSystem.IsLinux())
        {
            var path = Path.Combine(Path.GetFullPath(dataRoot), "control.sock");
            if (System.Text.Encoding.UTF8.GetByteCount(path) > 107) throw new IOException("包路径过长，请移至较短路径。");
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token); }
            catch { socket.Dispose(); throw; }
            stream = new NetworkStream(socket, ownsSocket: true);
        }
        else throw new PlatformNotSupportedException();
        await using (stream)
        {
            var element = JsonSerializer.SerializeToElement(request);
            if (!element.TryGetProperty("Id", out var id) || !id.TryGetGuid(out var expectedId)) throw new InvalidDataException("控制请求缺少标识。 ");
            await ControlProtocol.WriteAsync(stream, request, timeout.Token);
            return await ControlProtocol.ReadReplyAsync(stream, expectedId, timeout.Token);
        }
    }
}
