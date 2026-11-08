using System.Text;
using System.Text.Json;
using UstcCourseAssistant.Core.Services;

namespace UstcCourseAssistant.Cli;

internal static class ControlProtocol
{
    internal const int MaxBytes = 64 * 1024;
    internal const int MaxReplyBytes = 16 * 1024 * 1024;
    private const int ChunkBytes = 45 * 1024;
    private sealed record ReplyFrame(Guid Id, int Sequence, bool End, string Data);

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var next = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(next, token);
            if (read == 0) throw new EndOfStreamException("控制消息未完整结束。");
            if (next[0] == '\n') break;
            if (buffer.Length == MaxBytes) throw new InvalidDataException("控制消息超过64 KiB。");
            buffer.WriteByte(next[0]);
        }
        try
        {
            return JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)))
                ?? throw new InvalidDataException("空控制消息。");
        }
        catch (JsonException) { throw new InvalidDataException("控制消息格式无效。"); }
    }

    internal static async Task WriteAsync<T>(Stream stream, T message, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("控制消息超过64 KiB。");
        await stream.WriteAsync(bytes, token);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, token);
        await stream.FlushAsync(token);
    }

    internal static async Task WriteReplyAsync(Stream stream, Guid id, object reply, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(reply);
        if (bytes.Length > MaxReplyBytes)
            bytes = JsonSerializer.SerializeToUtf8Bytes(new ControlReply(id, false, "response_too_large", "回复超出大小限制，请缩小查询范围。", null));
        int sequence = 0;
        for (int offset = 0; offset < bytes.Length; offset += ChunkBytes)
        {
            var length = Math.Min(ChunkBytes, bytes.Length - offset);
            var encoded = Convert.ToBase64String(bytes, offset, length).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var frame = new ReplyFrame(id, sequence++, offset + length == bytes.Length, encoded);
            await WriteAsync(stream, frame, token);
        }
    }

    internal static async Task<JsonElement> ReadReplyAsync(Stream stream, Guid expectedId, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        for (int sequence = 0; ; sequence++)
        {
            var frame = await ReadAsync<ReplyFrame>(stream, token);
            if (frame.Id != expectedId || frame.Sequence != sequence || frame.Data is null) throw new InvalidDataException("控制回复帧标识或顺序无效。");
            byte[] chunk;
            try
            {
                var encoded = frame.Data.Replace('-', '+').Replace('_', '/');
                chunk = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
            }
            catch (FormatException) { throw new InvalidDataException("控制回复帧编码无效。"); }
            if (chunk.Length == 0 || chunk.Length > ChunkBytes || bytes.Length + chunk.Length > MaxReplyBytes) throw new InvalidDataException("控制回复超过大小限制或帧无效。");
            await bytes.WriteAsync(chunk, token);
            if (!frame.End) continue;
            JsonElement reply;
            try { reply = JsonSerializer.Deserialize<JsonElement>(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))); }
            catch (JsonException) { throw new InvalidDataException("控制回复格式无效。"); }
            if (reply.ValueKind != JsonValueKind.Object || !reply.TryGetProperty("Id", out var id) || !id.TryGetGuid(out var value) || value != expectedId)
                throw new InvalidDataException("控制回复标识无效。");
            return reply;
        }
    }

    internal static string PipeName(string dataRoot) => "ustc-assistant-" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataRoot).ToUpperInvariant())))[..24];
}
