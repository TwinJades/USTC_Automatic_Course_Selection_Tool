using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UstcCourseAssistant.Core.Services;
using UstcCourseAssistant.Core.Update;

namespace UstcCourseAssistant.Core.Authentication;

public sealed class HttpAuthenticationService : IAuthenticationService
{
    private const int MaxBody = 1024 * 1024;
    private static readonly Uri Entry = new("https://jw.ustc.edu.cn/ucas-sso/login");
    private static readonly Uri Fingerprint = new("https://analytics.ustc.edu.cn/fp");
    private readonly HttpMessageHandler? _handler;
    private readonly EamsSession? _injectedSession;
    private readonly Func<EamsSession, CancellationToken, Task> _validate;
    private readonly string _dataDirectory;
    private EamsSession? _session;
    private int _generation;
    private EamsSession? _challengeSession;
    private PreparedLogin? _challengeLogin;
    private string? _challengePhone;
    private byte[]? _captchaImage;
    private string? _pendingUser;
    private string? _pendingEncryptedPassword;
    private Uri? _passwordCaptchaUri;
    private readonly List<string> _screenshots = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _smsSent;
    private AuthenticationSnapshot _snapshot = new(AuthenticationState.SignedOut, null, "请登录教务系统。", null);
    public AuthenticationSnapshot Snapshot => _snapshot;

    public HttpAuthenticationService() : this("") { }
    public HttpAuthenticationService(string dataDirectory) : this(null, null, async (session, ct) => { await new ReadOnlyEamsClient(session.Client).ValidateAsync(ct); }, dataDirectory) { }
    internal HttpAuthenticationService(HttpMessageHandler? handler, EamsSession? session, Func<EamsSession, CancellationToken, Task> validate, string? dataDirectory = null)
    {
        _handler = handler;
        _injectedSession = session;
        _dataDirectory = dataDirectory ?? "";
        _validate = validate;
    }

    internal sealed record PreparedLogin(Uri LoginUri, string Key, string Execution, string RiskSwitch, string TargetSystem, string SiteId, string GroupId, string Token, string CaptchaVendor);

    internal async Task<PreparedLogin> PrepareAsync(HttpClient client, CookieContainer cookies, CancellationToken cancellationToken)
    {
        var (uri, html) = await FollowAsync(client, Entry, cancellationToken);
        if (uri.Host != "id.ustc.edu.cn" || uri.AbsolutePath != "/cas/login") throw new InvalidOperationException("统一身份认证入口不正确。");
        var fields = LoginFields.Parse(html);
        string Required(string name) => fields.TryGetValue(name, out var value) && value.Length > 0 ? value : throw new InvalidOperationException($"登录页面缺少 {name}。");
        string Optional(string name) => fields.GetValueOrDefault(name, "");
        var key = Required("login-croypto");
        var execution = Required("login-page-flowkey");
        var risk = Optional("riskSystemSwitch");
        if (Optional("recaptchaVendor") != "system") throw new InvalidOperationException("学校要求第三方验证码，HTTP 客户端不支持；未提交凭据。");
        var group = Optional("riskSystemGroupId");
        var token = risk == "USTC" ? await GetFingerprintTokenAsync(client, cookies, group, cancellationToken) : "";
        return new(uri, key, execution, risk, Optional("targetSystem"), Optional("siteId"), group, token, Optional("recaptchaVendor"));
    }

    public Task<AuthenticationSnapshot> LoginAsync(string userName, string password, CancellationToken cancellationToken) => LockedAsync(() => LoginCoreAsync(userName, password, cancellationToken), cancellationToken);
    private async Task<AuthenticationSnapshot> LoginCoreAsync(string userName, string password, CancellationToken cancellationToken)
    {
        LogoutCore();
        var generation = _generation;
        _snapshot = new(AuthenticationState.Authenticating, null, "正在验证身份。", null);
        var candidate = _injectedSession ?? new EamsSession();
        using var injectedClient = _handler is null ? null : new HttpClient(_handler, false) { Timeout = TimeSpan.FromSeconds(20) };
        var client = injectedClient ?? candidate.Client;
        try
        {
            var prepared = await PrepareAsync(client, candidate.Cookies, cancellationToken);
            var key = Convert.FromBase64String(prepared.Key);
            using var aes = Aes.Create();
            aes.Key = key;
            var encryptedPassword = Convert.ToBase64String(aes.EncryptEcb(Encoding.UTF8.GetBytes(password), PaddingMode.PKCS7));
            var path = "/cas/api/protected/user/findCaptchaCount/" + Uri.EscapeDataString(userName);
            using var check = new HttpRequestMessage(HttpMethod.Get, new Uri("https://id.ustc.edu.cn" + path));
            AddCsrf(check);
            using var checkResponse = await client.SendAsync(check, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            EnsureResponse(checkResponse, "密码提交前的验证码策略检查");
            using var json = JsonDocument.Parse(await ReadBodyAsync(checkResponse, cancellationToken));
            if (!json.RootElement.TryGetProperty("code", out var status) || !status.TryGetInt32(out var result) || result != 200 ||
                !json.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("captchaInvisible", out var captchaRequired) ||
                captchaRequired.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidOperationException("无法取得登录验证码状态。");
            if (captchaRequired.GetBoolean())
            {
                if (prepared.CaptchaVendor != "system") throw new InvalidOperationException("学校要求第三方验证码，HTTP 客户端不支持。");
                if (!data.TryGetProperty("captchaUrl", out var url) || url.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(url.GetString()))
                    throw new InvalidOperationException("学校未提供验证码图片地址。");
                var imageUri = new Uri(prepared.LoginUri, url.GetString());
                EnsureAllowed(imageUri);
                if (imageUri.Host != "id.ustc.edu.cn") throw new InvalidOperationException("拒绝非认证站点的验证码地址。");
                if (generation != _generation) throw new OperationCanceledException();
                _challengeSession = candidate; _challengeLogin = prepared; _pendingUser = userName;
                _pendingEncryptedPassword = encryptedPassword; _passwordCaptchaUri = imageUri;
                _snapshot = new(AuthenticationState.AwaitingSms, Guid.NewGuid(), "需要图形验证码，请先请求验证码图片。", DateTimeOffset.UtcNow.AddMinutes(10));
                return _snapshot;
            }
            return await SubmitPasswordAsync(client, candidate, prepared, userName, encryptedPassword, "", generation, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            candidate.Dispose();
            if (generation == _generation) _snapshot = new(AuthenticationState.SignedOut, null, "登录已取消。", null);
            throw;
        }
        catch (Exception ex)
        {
            candidate.Dispose();
            if (generation == _generation) _snapshot = new(AuthenticationState.Failed, null, $"登录失败：{ex.Message}", null);
            return _snapshot;
        }
    }

    private async Task<AuthenticationSnapshot> SubmitPasswordAsync(HttpClient client, EamsSession candidate, PreparedLogin prepared, string userName, string encryptedPassword, string captcha, int generation, CancellationToken cancellationToken)
    {
        using var aes = Aes.Create();
        aes.Key = Convert.FromBase64String(prepared.Key);
        string Encrypt(string value) => Convert.ToBase64String(aes.EncryptEcb(Encoding.UTF8.GetBytes(value), PaddingMode.PKCS7));
        var form = new Dictionary<string, string> {
            ["type"] = "UsernamePassword", ["_eventId"] = "submit", ["execution"] = prepared.Execution,
            ["geolocation"] = "", ["username"] = userName, ["password"] = encryptedPassword, ["croypto"] = prepared.Key,
            ["captcha_code"] = captcha, ["captcha_payload"] = Encrypt("{}"),
            ["targetSystem"] = prepared.TargetSystem, ["siteId"] = prepared.SiteId
        };
        if (prepared.RiskSwitch == "USTC")
        {
            form["risk_payload"] = Encrypt(JsonSerializer.Serialize(new { token = prepared.Token, groupId = prepared.GroupId }));
            form["riskEngine"] = "true";
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, prepared.LoginUri) { Content = new FormUrlEncodedContent(form) };
        EnsureAllowed(prepared.LoginUri);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var status = (int)response.StatusCode;
        if (status is 307 or 308) throw new InvalidOperationException("认证服务器要求重放凭据，已拒绝。");
        if (status is 302 or 303)
        {
            var location = response.Headers.Location ?? throw new InvalidOperationException("认证重定向缺少地址。");
            var (finalUri, finalBody) = await FollowAsync(client, new Uri(prepared.LoginUri, location), cancellationToken);
            if (TryBeginChallenge(candidate, prepared, finalUri, finalBody, userName, generation)) return _snapshot;
        }
        else
        {
            var body = await ReadBodyAsync(response, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"密码表单提交失败：HTTP {status}{DescribeFailure(body)}。未自动重试密码。");
            var fields = LoginFields.Parse(body);
            if (TryBeginChallenge(candidate, prepared, prepared.LoginUri, body, userName, generation)) return _snapshot;
            var error = fields.GetValueOrDefault("login-error-code", "");
            var errorCode = error.Length is > 0 and <= 12 && error.All(char.IsAsciiDigit) ? error : "未提供";
            throw new InvalidOperationException($"身份认证未完成（HTTP {status}，认证方式={fields.GetValueOrDefault("current-login-type", "未提供")}，规则={fields.GetValueOrDefault("login-rule-type", "未提供")}，学校错误码={errorCode}）。未自动重试密码。");
        }
        await _validate(candidate, cancellationToken);
        if (generation != _generation) throw new OperationCanceledException("登录已取消。");
        RecycleScreenshots();
        _session = candidate;
        return _snapshot = new(AuthenticationState.Authenticated, null, "教务身份已验证。", null);
    }

    private bool TryBeginChallenge(EamsSession candidate, PreparedLogin previous, Uri uri, string html, string userName, int generation)
    {
        if (uri.Host != "id.ustc.edu.cn" || uri.AbsolutePath != "/cas/login") return false;
        var fields = LoginFields.Parse(html);
        var rule = fields.GetValueOrDefault("login-rule-type");
        if (fields.GetValueOrDefault("current-login-type") != "smsLogin") return false;
        if (rule is not ("normal" or "group")) return false;
        var phone = rule == "normal" ? userName : fields.GetValueOrDefault("phone-number", "");
        var execution = fields.GetValueOrDefault("login-page-flowkey", "");
        var key = fields.GetValueOrDefault("login-croypto", "");
        var risk = key.Length > 0 && IsAesKey(key) ? fields.GetValueOrDefault("riskSystemSwitch", "") : "";
        if (phone.Length == 0) throw new InvalidOperationException("学校返回短信登录页面，但没有可用的登录账号；未发送短信。");
        if (execution.Length == 0) throw new InvalidOperationException("学校返回短信登录页面，但缺少流程字段；未发送短信。");
        if (generation != _generation) throw new OperationCanceledException("登录已取消。");
        RecycleScreenshots();
        _captchaImage = null; _smsSent = false;
        _challengeSession = candidate;
        _challengePhone = phone;
        _challengeLogin = previous with { LoginUri = uri, Key = key, Execution = execution, RiskSwitch = risk, Token = "",
            GroupId = fields.GetValueOrDefault("riskSystemGroupId", ""),
            TargetSystem = fields.GetValueOrDefault("targetSystem", previous.TargetSystem),
            SiteId = fields.GetValueOrDefault("siteId", previous.SiteId) };
        _snapshot = new(AuthenticationState.AwaitingSms, Guid.NewGuid(), rule == "normal" ? "学校返回短信登录入口；可手动请求验证码，短信将发送到该学校账号绑定的号码。" : "需要短信验证，请先请求验证码。", DateTimeOffset.UtcNow.AddMinutes(10));
        return true;
    }

    private static async Task<(Uri Uri, string Body)> FollowAsync(HttpClient client, Uri uri, CancellationToken ct)
    {
        for (var redirects = 0; redirects <= 10; redirects++)
        {
            EnsureAllowed(uri);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                uri = new Uri(uri, response.Headers.Location ?? throw new InvalidOperationException("重定向缺少地址。"));
                continue;
            }
            EnsureResponse(response, "认证页面读取或跳转（" + uri.Host + "）");
            return (uri, await ReadBodyAsync(response, ct));
        }
        throw new InvalidOperationException("认证重定向过多。");
    }

    private static void EnsureAllowed(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Host is not ("jw.ustc.edu.cn" or "passport.ustc.edu.cn" or "id.ustc.edu.cn" or "analytics.ustc.edu.cn"))
            throw new InvalidOperationException("拒绝不安全的认证地址。");
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct) => Encoding.UTF8.GetString(await ReadBytesAsync(response, ct));

    private static async Task<byte[]> ReadBytesAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxBody) throw new InvalidOperationException("认证响应过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) != 0)
        {
            if (output.Length + count > MaxBody) throw new InvalidOperationException("认证响应过大。");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private static async Task<string> GetFingerprintTokenAsync(HttpClient client, CookieContainer cookies, string group, CancellationToken ct)
    {
        static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        static string RandomToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var device = cookies.GetCookies(new Uri("https://id.ustc.edu.cn/"))["device"]?.Value ?? RandomToken();
        var remember = cookies.GetCookies(new Uri("https://id.ustc.edu.cn/"))["rememberToken"]?.Value ?? RandomToken();
        cookies.Add(new Uri("https://id.ustc.edu.cn/"), new Cookie("device", device, "/") { Secure = true });
        cookies.Add(new Uri("https://id.ustc.edu.cn/"), new Cookie("rememberToken", remember, "/") { Secure = true });
        var fonts = JsonSerializer.Serialize("unavailable");
        var memory = JsonSerializer.Serialize("unavailable");
        var cpu = JsonSerializer.Serialize(Environment.ProcessorCount);
        var timezone = JsonSerializer.Serialize(TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.Now).TotalMinutes * -1);
        var cpuClass = JsonSerializer.Serialize("unavailable");
        var platform = JsonSerializer.Serialize(Environment.OSVersion.Platform.ToString());
        var language = JsonSerializer.Serialize(CultureInfo.CurrentCulture.Name);
        var screen = JsonSerializer.Serialize("unavailable");
        var payload = new Dictionary<string, object> {
            ["fonts"] = Hash(fonts), ["deviceMemory"] = Hash(memory), ["hardwareConcurrency"] = Hash(cpu),
            ["timezone"] = timezone, ["cpuClass"] = Hash(cpuClass), ["platform"] = platform, ["language"] = language,
            ["screenResolution"] = screen, ["fingerprint"] = Hash(fonts + memory + cpu + timezone + cpuClass + platform + language + screen),
            ["localgroupId"] = group, ["cookieValue"] = device, ["rememberToken"] = remember, ["noticeAck"] = "0",
            ["webglFingerprint"] = "unavailable", ["maxTouchPoints"] = "unavailable", ["colorDepth"] = "unavailable",
            ["pixelRatio"] = "unavailable", ["userAgent"] = "USTC-Course-Assistant/2.2.1 HTTP client", ["platformAuthenticator"] = "unavailable"
        };
        EnsureAllowed(Fingerprint);
        using var request = new HttpRequestMessage(HttpMethod.Post, Fingerprint) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        EnsureResponse(response, "设备风控令牌获取");
        using var json = JsonDocument.Parse(await ReadBodyAsync(response, ct));
        if (!json.RootElement.TryGetProperty("responsetoken", out var token) || token.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(token.GetString()))
            throw new InvalidOperationException("风控服务未返回设备令牌。");
        return token.GetString()!;
    }

    private void RequireChallenge(Guid id)
    {
        if (_snapshot.State != AuthenticationState.AwaitingSms || _snapshot.ChallengeId != id || _snapshot.ExpiresAt <= DateTimeOffset.UtcNow || _challengeSession is null)
            throw new InvalidOperationException("没有有效的短信验证挑战。");
    }

    private HttpClient ChallengeClient() => _handler is null ? _challengeSession!.Client : new HttpClient(_handler, false) { Timeout = TimeSpan.FromSeconds(20) };

    private static void EnsureResponse(HttpResponseMessage response, string stage)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{stage}失败：HTTP {(int)response.StatusCode}。未自动重试凭据或短信。");
    }

    private static void AddCsrf(HttpRequestMessage request)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var key = new string(Enumerable.Range(0, 32).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(key));
        var value = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(encoded[..22] + encoded + encoded[22..])));
        request.Headers.Add("Csrf-Key", key);
        request.Headers.Add("Csrf-Value", value);
    }
    private static bool IsAesKey(string value)
    {
        try { return Convert.FromBase64String(value) is { Length: 16 or 24 or 32 }; }
        catch (FormatException) { return false; }
    }
    // 只暴露学校返回的数字错误码，便于对照官方前端文案；不输出任意响应文本。
    private static string SchoolCode(JsonElement root, int businessCode)
    {
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("errorCode", out var error) && error.TryGetInt64(out var value)) return value.ToString(CultureInfo.InvariantCulture);
        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String &&
            long.TryParse(message.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var numeric)) return numeric.ToString(CultureInfo.InvariantCulture);
        return businessCode.ToString(CultureInfo.InvariantCulture);
    }

    // 非成功响应只提取学校数字错误码和已脱敏的短文本，便于判断是密码、锁定、验证码还是设备风险。
    private static string DescribeFailure(string body)
    {
        static string Clip(string value)
        {
            var clean = LocalActivityLog.Sanitize(new string(value.Where(c => !char.IsControl(c)).ToArray())).Trim();
            return clean.Length > 60 ? clean[..60] + "…" : clean;
        }
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var json = JsonDocument.Parse(trimmed);
                var parts = new List<string>();
                if (json.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number) parts.Add("code=" + code.GetRawText());
                if (json.RootElement.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String && Clip(message.GetString()!) is { Length: > 0 } text) parts.Add(text);
                if (json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                {
                    if (data.TryGetProperty("errorCode", out var error) && error.ValueKind == JsonValueKind.Number) parts.Add("errorCode=" + error.GetRawText());
                    if (data.TryGetProperty("errorMessage", out var reason) && reason.ValueKind == JsonValueKind.String && Clip(reason.GetString()!) is { Length: > 0 } reasonText) parts.Add(reasonText);
                }
                return parts.Count == 0 ? "" : "，" + string.Join("，", parts);
            }
            catch (JsonException) { return ""; }
        }
        var fields = LoginFields.Parse(body);
        var detail = new List<string>();
        if (fields.GetValueOrDefault("login-error-code", "") is { Length: > 0 } codeText) detail.Add("错误码 " + codeText);
        if (fields.GetValueOrDefault("login-error-msg", "") is { Length: > 0 } messageText) detail.Add(Clip(messageText));
        return detail.Count == 0 ? "" : "，" + string.Join("，", detail);
    }

    public Task<AuthenticationSnapshot> RequestSmsCodeAsync(Guid challengeId, CancellationToken cancellationToken) => LockedAsync(() => RequestSmsCoreAsync(challengeId, cancellationToken), cancellationToken);
    private async Task<AuthenticationSnapshot> RequestSmsCoreAsync(Guid challengeId, CancellationToken cancellationToken)
    {
        RequireChallenge(challengeId);
        var generation = _generation;
        var client = ChallengeClient();
        try
        {
            // 短信发送接口校验的是绑定到该手机号/账号的图形码，通用验证码不被接受。
            using var request = _passwordCaptchaUri is null
                ? new HttpRequestMessage(HttpMethod.Post, "https://id.ustc.edu.cn/gate/sso-extend/protected/api/aggregate/sms/publicNoToken/generate")
                { Content = new StringContent(JsonSerializer.Serialize(new { phone = _challengePhone, type = "DEFAULT" }), Encoding.UTF8, "application/json") }
                : new HttpRequestMessage(HttpMethod.Get, _passwordCaptchaUri);
            AddCsrf(request);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            EnsureResponse(response, "图形验证码图片获取");
            if (response.Content.Headers.ContentType?.MediaType != "image/png") throw new InvalidOperationException("服务器未返回图形验证码图片。");
            var image = await ReadBytesAsync(response, cancellationToken);
            RequireChallenge(challengeId);
            if (generation != _generation) throw new OperationCanceledException();
            _captchaImage = image;
            _smsSent = false;
            return _snapshot = _snapshot with { Message = _passwordCaptchaUri is null ? "请输入图形验证码以发送短信。" : "请输入图形验证码以继续登录。" };
        }
        finally { if (_handler is not null) client.Dispose(); }
    }

    public Task<string> ScreenshotChallengeAsync(Guid challengeId, CancellationToken cancellationToken) => LockedAsync(() => ScreenshotCoreAsync(challengeId, cancellationToken), cancellationToken);
    private async Task<string> ScreenshotCoreAsync(Guid challengeId, CancellationToken cancellationToken)
    {
        RequireChallenge(challengeId);
        if (_captchaImage is null) throw new InvalidOperationException("请先请求短信验证码。 ");
        if (string.IsNullOrEmpty(_dataDirectory)) throw new InvalidOperationException("未配置认证截图数据目录。");
        PackageManifest.RejectReparseAncestors(_dataDirectory);
        Directory.CreateDirectory(_dataDirectory);
        var path = Path.Combine(_dataDirectory, $"auth-challenge-{Guid.NewGuid():D}.png");
        PackageManifest.RejectReparseAncestors(path);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var file = new FileStream(path, options);
        _screenshots.Add(path);
        await file.WriteAsync(_captchaImage, cancellationToken);
        return path;
    }

    public Task<AuthenticationSnapshot> SubmitCaptchaAsync(Guid challengeId, string code, CancellationToken cancellationToken) => LockedAsync(() => SubmitCaptchaCoreAsync(challengeId, code, cancellationToken), cancellationToken);
    private async Task<AuthenticationSnapshot> SubmitCaptchaCoreAsync(Guid challengeId, string code, CancellationToken cancellationToken)
    {
        RequireChallenge(challengeId);
        if (_captchaImage is null) throw new InvalidOperationException("请先请求图形验证码。 ");
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("验证码不能为空。", nameof(code));
        var client = ChallengeClient();
        try
        {
            if (_passwordCaptchaUri is not null)
            {
                var session = _challengeSession!;
                var prepared = _challengeLogin!;
                var user = _pendingUser!;
                var encrypted = _pendingEncryptedPassword!;
                var generation = _generation;
                _pendingUser = null; _pendingEncryptedPassword = null; _passwordCaptchaUri = null; _captchaImage = null;
                _challengeSession = null; _challengeLogin = null;
                _snapshot = new(AuthenticationState.Authenticating, null, "正在验证身份。", null);
                try { return await SubmitPasswordAsync(client, session, prepared, user, encrypted, code, generation, cancellationToken); }
                catch
                {
                    session.Dispose();
                    if (generation == _generation) _snapshot = new(AuthenticationState.Failed, null, "登录验证失败。", null);
                    throw;
                }
            }
            var uri = new Uri("https://id.ustc.edu.cn/gate/sso-extend/protected/api/aggregate/sms/publicNoToken/sendCheckCaptcha/DEFAULT/" + Uri.EscapeDataString(code) + "/" + Uri.EscapeDataString(_challengePhone!) + "/0008");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            AddCsrf(request);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            EnsureResponse(response, "图形验证码校验及短信发送");
            using var json = JsonDocument.Parse(await ReadBodyAsync(response, cancellationToken));
            RequireChallenge(challengeId);
            var captchaStatus = json.RootElement.TryGetProperty("code", out var result) && result.TryGetInt32(out var status) ? status : 0;
            if (captchaStatus != 200)
            {
                _captchaImage = null;
                return _snapshot = _snapshot with { Message = $"图形验证码错误或短信发送失败（学校码 {SchoolCode(json.RootElement, captchaStatus)}）。请重新请求验证码。" };
            }
            _captchaImage = null;
            _smsSent = true;
            return _snapshot = _snapshot with { Message = "短信验证码已发送，请输入短信验证码。" };
        }
        finally { if (_handler is not null) client.Dispose(); }
    }

    public Task<AuthenticationSnapshot> SubmitSmsCodeAsync(Guid challengeId, string code, CancellationToken cancellationToken) => LockedAsync(() => SubmitSmsCoreAsync(challengeId, code, cancellationToken), cancellationToken);
    private async Task<AuthenticationSnapshot> SubmitSmsCoreAsync(Guid challengeId, string code, CancellationToken cancellationToken)
    {
        RequireChallenge(challengeId);
        if (!_smsSent) throw new InvalidOperationException("请先发送短信验证码。 ");
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("验证码不能为空。", nameof(code));
        var session = _challengeSession!;
        var prepared = _challengeLogin!;
        var generation = _generation;
        var client = ChallengeClient();
        try
        {
            using var check = new HttpRequestMessage(HttpMethod.Post, "https://id.ustc.edu.cn/cas/api/protected/sms/checkTokenResult") {
                Content = new StringContent(JsonSerializer.Serialize(new { phone = _challengePhone, token = code, delete = false }), Encoding.UTF8, "application/json") };
            AddCsrf(check);
            using var checkResponse = await client.SendAsync(check, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            EnsureResponse(checkResponse, "短信数字校验");
            using var result = JsonDocument.Parse(await ReadBodyAsync(checkResponse, cancellationToken));
            RequireChallenge(challengeId);
            var smsStatus = result.RootElement.TryGetProperty("code", out var status) && status.TryGetInt32(out var value) ? value : 0;
            if (smsStatus != 200)
                return _snapshot = _snapshot with { Message = $"短信验证码无效或已过期（学校码 {SchoolCode(result.RootElement, smsStatus)}）。" };
            var form = new Dictionary<string, string> {
                ["type"] = "smsLogin", ["_eventId"] = "submit", ["geolocation"] = "", ["execution"] = prepared.Execution,
                ["username"] = _challengePhone!, ["password"] = code, ["captcha_code"] = "",
                ["targetSystem"] = prepared.TargetSystem, ["siteId"] = prepared.SiteId
            };
            if (prepared.RiskSwitch == "USTC")
            {
                var token = await GetFingerprintTokenAsync(client, session.Cookies, prepared.GroupId, cancellationToken);
                using var aes = Aes.Create();
                aes.Key = Convert.FromBase64String(prepared.Key);
                form["croypto"] = prepared.Key;
                form["risk_payload"] = Convert.ToBase64String(aes.EncryptEcb(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { token, groupId = prepared.GroupId })), PaddingMode.PKCS7));
                form["riskEngine"] = "true";
            }
            RequireChallenge(challengeId);
            using var request = new HttpRequestMessage(HttpMethod.Post, prepared.LoginUri) { Content = new FormUrlEncodedContent(form) };
            EnsureAllowed(prepared.LoginUri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is not (302 or 303)) throw new InvalidOperationException("短信认证未完成，拒绝重放验证码。");
            var location = response.Headers.Location ?? throw new InvalidOperationException("认证重定向缺少地址。");
            await FollowAsync(client, new Uri(prepared.LoginUri, location), cancellationToken);
            await _validate(session, cancellationToken);
            if (generation != _generation) throw new OperationCanceledException();
            RecycleScreenshots();
            _challengeSession = null;
            _challengeLogin = null;
            _challengePhone = null;
            _session = session;
            return _snapshot = new(AuthenticationState.Authenticated, null, "教务身份已验证。", null);
        }
        catch
        {
            if (generation == _generation) { LogoutCore(); _snapshot = new(AuthenticationState.Failed, null, "短信认证未完成。", null); }
            throw;
        }
        finally { if (_handler is not null) client.Dispose(); }
    }
    public EamsSession GetAuthenticatedSession() => _session ?? throw new AuthenticationRequiredException();
    private async Task<T> LockedAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await action(); }
        finally { _gate.Release(); }
    }
    public void Logout()
    {
        _gate.Wait();
        try { LogoutCore(); }
        finally { _gate.Release(); }
    }
    private void LogoutCore()
    {
        Interlocked.Increment(ref _generation);
        Interlocked.Exchange(ref _session, null)?.Dispose();
        Interlocked.Exchange(ref _challengeSession, null)?.Dispose();
        _challengeLogin = null; _challengePhone = null; _captchaImage = null; _smsSent = false;
        _pendingUser = null; _pendingEncryptedPassword = null; _passwordCaptchaUri = null;
        RecycleScreenshots();
        _snapshot = new(AuthenticationState.SignedOut, null, "已退出登录。", null);
    }
    private void RecycleScreenshots()
    {
        foreach (var path in _screenshots) new RecycleService(_dataDirectory).Recycle(path);
        _screenshots.Clear();
    }
    public void Dispose() => Logout();
}
