using System.Net;
using System.Net.Http;
using System.Text.Json;
using UstcCourseAssistant.Route2.Models;

namespace UstcCourseAssistant.Route2.Services;

/// <summary>
/// 阶段 C 的单次受控写操作客户端。只接受枚举动作和已经由只读预检查解析出的数字 ID。
/// </summary>
public sealed class ControlledEamsClient : IAutomationMutationClient
{
    private const string AddRequestPath = "/ws/for-std/course-select/add-request";
    private const string DropRequestPath = "/ws/for-std/course-select/drop-request";
    private const string ResponsePath = "/ws/for-std/course-select/add-drop-response";

    private static readonly TimeSpan[] PollDelays =
    [
        TimeSpan.FromMilliseconds(600),
        TimeSpan.FromMilliseconds(900),
        TimeSpan.FromMilliseconds(1200),
        TimeSpan.FromMilliseconds(1600),
        TimeSpan.FromMilliseconds(2200),
        TimeSpan.FromMilliseconds(3000)
    ];

    private readonly HttpClient _client;

    public ControlledEamsClient(AuthenticatedSessionBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        _client = bridge.Client;
    }

    public async Task<ControlledOperationResponse> ExecuteOnceAsync(
        CourseMutationKind kind,
        long studentId,
        long turnId,
        long lessonId,
        CancellationToken cancellationToken = default)
    {
        if (studentId <= 0 || turnId <= 0 || lessonId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lessonId), "受控操作只能使用预检查得到的有效数字 ID。");
        }

        var requestPath = kind switch
        {
            CourseMutationKind.Add => AddRequestPath,
            CourseMutationKind.Drop => DropRequestPath,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var requestId = await RequestOperationIdAsync(
            requestPath,
            studentId,
            turnId,
            lessonId,
            cancellationToken);

        foreach (var delay in PollDelays)
        {
            await Task.Delay(delay, cancellationToken);
            var response = await TryReadResponseAsync(studentId, requestId, kind, cancellationToken);
            if (response is not null)
            {
                return response;
            }
        }

        return new ControlledOperationResponse(
            kind,
            Completed: false,
            Success: false,
            "服务器在限定轮询次数内没有给出最终结果。已停止轮询，请以随后读取的已选课程状态为准，切勿立即重复提交。");
    }

    private async Task<string> RequestOperationIdAsync(
        string path,
        long studentId,
        long turnId,
        long lessonId,
        CancellationToken cancellationToken)
    {
        using var response = await SendFormAsync(
            path,
            new Dictionary<string, string>
            {
                ["courseSelectTurnAssoc"] = turnId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["studentAssoc"] = studentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["lessonAssoc"] = lessonId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            cancellationToken);
        EnsureUsableResponse(response, "受控操作提交");

        var raw = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        var requestId = raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"'
            ? raw[1..^1]
            : raw;
        if (requestId.Length is < 1 or > 256 || requestId.Contains('<', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("服务器没有返回有效的受控操作请求编号，已停止。");
        }

        return requestId;
    }

    private async Task<ControlledOperationResponse?> TryReadResponseAsync(
        long studentId,
        string requestId,
        CourseMutationKind kind,
        CancellationToken cancellationToken)
    {
        using var response = await SendFormAsync(
            ResponsePath,
            new Dictionary<string, string>
            {
                ["studentId"] = studentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["requestId"] = requestId
            },
            cancellationToken);
        EnsureUsableResponse(response, "受控操作结果");

        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("success", out var successElement)
            || successElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        var success = successElement.GetBoolean();
        var message = success ? "服务器报告操作成功。" : ReadErrorMessage(root);
        return new ControlledOperationResponse(kind, Completed: true, success, message);
    }

    private async Task<HttpResponseMessage> SendFormAsync(
        string path,
        IEnumerable<KeyValuePair<string, string>> form,
        CancellationToken cancellationToken)
    {
        var target = new Uri(_client.BaseAddress!, path);
        if (!AuthenticatedSessionBridge.IsEamsHost(target)
            || path is not (AddRequestPath or DropRequestPath or ResponsePath))
        {
            throw new InvalidOperationException("阶段 C 安全策略拒绝了未列入白名单的请求。");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.TryAddWithoutValidation("Origin", _client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        return await _client.SendAsync(request, cancellationToken);
    }

    private static void EnsureUsableResponse(HttpResponseMessage response, string operationName)
    {
        if (IsRedirect(response.StatusCode)
            || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AuthenticationRequiredException();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{operationName}失败（HTTP {(int)response.StatusCode}）。");
        }
    }

    private static string ReadErrorMessage(JsonElement root)
    {
        if (!root.TryGetProperty("errorMessage", out var error))
        {
            return "服务器报告操作失败，但没有提供原因。";
        }

        if (error.ValueKind == JsonValueKind.String)
        {
            return error.GetString() ?? "服务器报告操作失败。";
        }

        if (error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString() ?? "服务器报告操作失败。";
        }

        return "服务器报告操作失败。";
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
}
