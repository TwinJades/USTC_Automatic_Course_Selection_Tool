using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using UstcCourseAssistant.Route2.Models;

namespace UstcCourseAssistant.Route2.Services;

/// <summary>
/// 阶段 A/B 的只读教务客户端。所有网络目标均由固定白名单控制；本类没有任何选退课 API。
/// </summary>
public sealed partial class ReadOnlyEamsClient : IAutomationReadClient
{
    private const string CourseTablePath = "/for-std/course-table";
    private const string OpenTurnsPath = "/ws/for-std/course-select/open-turns";
    private const string SelectedLessonsPath = "/ws/for-std/course-select/selected-lessons";
    private const string AddableLessonsPath = "/ws/for-std/course-select/addable-lessons";
    private const string StudentCountPath = "/ws/for-std/course-select/std-count";
    private const int MaxRedirects = 5;

    private readonly HttpClient _client;

    public ReadOnlyEamsClient(AuthenticatedSessionBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        _client = bridge.Client;
    }

    public async Task<SessionValidationResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        var studentId = await DiscoverStudentIdAsync(cancellationToken);
        var turns = await GetOpenTurnsAsync(studentId, cancellationToken);
        return new SessionValidationResult(studentId, turns);
    }

    public async Task<ShadowSnapshot> GetShadowSnapshotAsync(
        long studentId,
        long turnId,
        IReadOnlyCollection<string> targetCodes,
        CancellationToken cancellationToken = default)
    {
        if (studentId <= 0 || turnId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(studentId), "学生编号和选课轮次必须先通过只读验证取得。");
        }

        var normalizedCodes = targetCodes
            .Select(code => code.Trim())
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var selectedLessons = await GetLessonsAsync(
            SelectedLessonsPath,
            studentId,
            turnId,
            "已选课程",
            cancellationToken);
        var addableLessons = await GetLessonsAsync(
            AddableLessonsPath,
            studentId,
            turnId,
            "可选课程",
            cancellationToken);

        var allByCode = selectedLessons
            .Concat(addableLessons)
            .DistinctBy(lesson => lesson.Id)
            .GroupBy(lesson => lesson.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var matchedTargets = new List<LessonInfo>();
        var missingCodes = new List<string>();
        foreach (var code in normalizedCodes)
        {
            if (allByCode.TryGetValue(code, out var matches))
            {
                matchedTargets.AddRange(matches);
            }
            else
            {
                missingCodes.Add(code);
            }
        }

        var counts = await GetStudentCountsAsync(matchedTargets, cancellationToken);
        var targetsWithCounts = matchedTargets
            .Select(lesson => lesson with
            {
                SelectedCount = counts.TryGetValue(lesson.Id, out var count) ? count : null
            })
            .ToArray();

        return new ShadowSnapshot(
            selectedLessons,
            targetsWithCounts,
            missingCodes,
            addableLessons.Count);
    }

    private async Task<long> DiscoverStudentIdAsync(CancellationToken cancellationToken)
    {
        var target = new Uri(_client.BaseAddress!, CourseTablePath);

        for (var redirectCount = 0; redirectCount <= MaxRedirects; redirectCount++)
        {
            EnsureAllowed(HttpMethod.Get, target);
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.Accept.Clear();
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");

            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (IsRedirect(response.StatusCode))
            {
                target = ResolveRedirect(target, response.Headers.Location);
                if (IsAuthenticationTarget(target))
                {
                    throw new AuthenticationRequiredException();
                }

                continue;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new AuthenticationRequiredException();
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"课程表只读请求失败（HTTP {(int)response.StatusCode}）。");
            }

            var match = StudentIdAtEndRegex().Match(target.AbsolutePath.TrimEnd('/'));
            if (match.Success && long.TryParse(match.Groups[1].Value, out var studentId))
            {
                return studentId;
            }

            throw new InvalidOperationException("教务会话有效，但未能从课程表地址识别内部学生编号。接口结构可能已经变化。");
        }

        throw new InvalidOperationException("课程表入口重定向次数过多，已停止只读验证。");
    }

    private async Task<IReadOnlyList<TurnInfo>> GetOpenTurnsAsync(
        long studentId,
        CancellationToken cancellationToken)
    {
        var target = new Uri(_client.BaseAddress!, OpenTurnsPath);
        EnsureAllowed(HttpMethod.Post, target);

        using var request = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["bizTypeId"] = "2",
                ["studentId"] = studentId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            })
        };
        request.Headers.TryAddWithoutValidation("Origin", _client.BaseAddress!.GetLeftPart(UriPartial.Authority));

        using var response = await _client.SendAsync(request, cancellationToken);
        if (IsRedirect(response.StatusCode)
            || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AuthenticationRequiredException();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"开放轮次只读请求失败（HTTP {(int)response.StatusCode}）。");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            throw new AuthenticationRequiredException();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("开放轮次接口返回了未知格式，已停止验证。");
        }

        var turns = new List<TurnInfo>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id))
            {
                continue;
            }

            turns.Add(new TurnInfo(
                id,
                GetString(item, "name", "未命名轮次"),
                GetString(item, "semesterName", "学期未知")));
        }

        return turns;
    }

    private async Task<IReadOnlyList<LessonInfo>> GetLessonsAsync(
        string path,
        long studentId,
        long turnId,
        string operationName,
        CancellationToken cancellationToken)
    {
        var root = await PostJsonAsync(
            path,
            new Dictionary<string, string>
            {
                ["studentId"] = studentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["turnId"] = turnId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            operationName,
            cancellationToken);

        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"{operationName}接口返回了未知格式，已停止影子验证。");
        }

        var lessons = new List<LessonInfo>();
        foreach (var item in root.EnumerateArray())
        {
            if (TryParseLesson(item, out var lesson))
            {
                lessons.Add(lesson);
            }
        }

        return lessons;
    }

    private async Task<IReadOnlyDictionary<long, int>> GetStudentCountsAsync(
        IReadOnlyCollection<LessonInfo> lessons,
        CancellationToken cancellationToken)
    {
        if (lessons.Count == 0)
        {
            return new Dictionary<long, int>();
        }

        var form = lessons
            .Select(lesson => new KeyValuePair<string, string>(
                "lessonIds[]",
                lesson.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();
        var root = await PostJsonAsync(StudentCountPath, form, "批量人数", cancellationToken);
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("批量人数接口返回了未知格式，已停止影子验证。");
        }

        var counts = new Dictionary<long, int>();
        foreach (var property in root.EnumerateObject())
        {
            if (long.TryParse(property.Name, out var lessonId)
                && property.Value.TryGetInt32(out var count))
            {
                counts[lessonId] = count;
            }
        }

        return counts;
    }

    private async Task<JsonElement> PostJsonAsync(
        string path,
        IEnumerable<KeyValuePair<string, string>> form,
        string operationName,
        CancellationToken cancellationToken)
    {
        var target = new Uri(_client.BaseAddress!, path);
        EnsureAllowed(HttpMethod.Post, target);

        using var request = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.TryAddWithoutValidation("Origin", _client.BaseAddress!.GetLeftPart(UriPartial.Authority));

        using var response = await _client.SendAsync(request, cancellationToken);
        if (IsRedirect(response.StatusCode)
            || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AuthenticationRequiredException();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{operationName}只读请求失败（HTTP {(int)response.StatusCode}）。");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            throw new AuthenticationRequiredException();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
    }

    private static bool TryParseLesson(JsonElement item, out LessonInfo lesson)
    {
        lesson = default!;
        if (!item.TryGetProperty("id", out var idElement)
            || !idElement.TryGetInt64(out var id)
            || !item.TryGetProperty("code", out var codeElement)
            || codeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var code = codeElement.GetString();
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var courseCode = string.Empty;
        var courseName = "课程名称未知";
        if (item.TryGetProperty("course", out var course) && course.ValueKind == JsonValueKind.Object)
        {
            courseCode = GetString(course, "code", string.Empty);
            courseName = GetString(course, "nameZh", courseName);
        }

        var limitCount = item.TryGetProperty("limitCount", out var limitElement)
                         && limitElement.TryGetInt32(out var parsedLimit)
            ? parsedLimit
            : 0;
        var teacherNames = new List<string>();
        if (item.TryGetProperty("teachers", out var teachers) && teachers.ValueKind == JsonValueKind.Array)
        {
            foreach (var teacher in teachers.EnumerateArray())
            {
                var name = GetString(teacher, "nameZh", string.Empty);
                if (name.Length > 0)
                {
                    teacherNames.Add(name);
                }
            }
        }

        lesson = new LessonInfo(
            id,
            code,
            courseCode,
            courseName,
            limitCount,
            string.Join("、", teacherNames));
        return true;
    }

    private static string GetString(JsonElement item, string propertyName, string fallback) =>
        item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static void EnsureAllowed(HttpMethod method, Uri target)
    {
        if (!AuthenticatedSessionBridge.IsEamsHost(target))
        {
            throw new InvalidOperationException("只读安全策略拒绝了非教务域名请求。");
        }

        var allowed = method == HttpMethod.Get
                      && (target.AbsolutePath.Equals(CourseTablePath, StringComparison.Ordinal)
                          || target.AbsolutePath.StartsWith(CourseTablePath + "/", StringComparison.Ordinal))
                      || method == HttpMethod.Post
                      && target.AbsolutePath is OpenTurnsPath
                          or SelectedLessonsPath
                          or AddableLessonsPath
                          or StudentCountPath;

        if (!allowed)
        {
            throw new InvalidOperationException("阶段 A/B 只读安全策略拒绝了未列入白名单的请求。");
        }
    }

    private static Uri ResolveRedirect(Uri current, Uri? location)
    {
        if (location is null)
        {
            throw new InvalidOperationException("教务系统返回了缺少目标地址的重定向。");
        }

        return location.IsAbsoluteUri ? location : new Uri(current, location);
    }

    private static bool IsAuthenticationTarget(Uri target) =>
        !AuthenticatedSessionBridge.IsEamsHost(target)
        || target.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase);

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    [GeneratedRegex(@"/(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex StudentIdAtEndRegex();
}
