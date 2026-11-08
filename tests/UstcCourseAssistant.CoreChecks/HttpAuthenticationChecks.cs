using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UstcCourseAssistant.Core.Authentication;

internal static class HttpAuthenticationChecks
{
    private const string User = "fixture-user";
    private const string Password = "fixture-password-非ASCII";
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("0123456789abcdef");
    private static readonly Uri Cas = new("https://id.ustc.edu.cn/cas/login?service=https%3A%2F%2Fjw.ustc.edu.cn%2Fucas-sso%2Flogin");

    public static async Task RunAsync(string testRoot)
    {
        await CheckSuccessfulLoginAsync();
        await CheckValidationFailureAsync();
        foreach (var status in new[] { HttpStatusCode.TemporaryRedirect, HttpStatusCode.PermanentRedirect })
            await CheckCredentialRedirectAsync(status);
        await CheckCrossDomainRedirectAsync();
        await CheckMissingFingerprintTokenAsync();
        await CheckUnsupportedCaptchaVendorAsync();
        await CheckSmsChallengeAsync(testRoot, "group");
        await CheckNormalSmsEntryAsync(testRoot);
        await CheckRiskSmsMissingKeyAsync(testRoot);
        await CheckPasswordCaptchaAsync(testRoot);
    }

    private static async Task CheckSuccessfulLoginAsync()
    {
        var handler = new LoginHandler();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new EamsSession();
        using var service = new HttpAuthenticationService(handler, session, async (candidate, _) =>
        {
            Require(ReferenceEquals(candidate, session), "identity validation must use the login session");
            var cookies = candidate.Cookies.GetCookies(new Uri("https://id.ustc.edu.cn/"));
            Require(cookies["device"] is not null && cookies["rememberToken"] is not null, "fingerprint cookies must remain in the session used for validation");
            entered.SetResult();
            await finish.Task;
        });
        var login = service.LoginAsync(User, Password, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Require(service.Snapshot.State != AuthenticationState.Authenticated, "authentication must wait for read-only identity validation");
        finish.SetResult();
        var result = await login;
        Require(result.State == AuthenticationState.Authenticated, "validated session must authenticate");
        Require(ReferenceEquals(service.GetAuthenticatedSession(), session), "the validated session and its cookies must be preserved");
        Require(handler.FixtureError is null, "credential fixture assertions must succeed: " + handler.FixtureError);
        Require(handler.PostCount == 1 && handler.ReturnCount == 1, "login must complete the CAS redirect before validation");
        service.Logout();
        Require(service.Snapshot.State == AuthenticationState.SignedOut, "logout must revoke the authenticated state");
    }

    private static async Task CheckValidationFailureAsync()
    {
        var handler = new LoginHandler();
        using var service = new HttpAuthenticationService(handler, new EamsSession(), (_, _) => throw new InvalidOperationException("fixture identity rejection"));
        try { await service.LoginAsync(User, Password, CancellationToken.None); }
        catch (InvalidOperationException) { }
        Require(service.Snapshot.State != AuthenticationState.Authenticated, "CAS redirect alone must not authenticate without identity validation");
        var rejected = false;
        try { service.GetAuthenticatedSession(); }
        catch (Exception) { rejected = true; }
        Require(rejected, "failed identity validation must not expose an authenticated session");
    }

    private static async Task CheckCredentialRedirectAsync(HttpStatusCode status)
    {
        var handler = new LoginHandler { LoginRedirect = status, LoginLocation = new Uri("https://id.ustc.edu.cn/cas/redirected") };
        var validated = false;
        using var service = new HttpAuthenticationService(handler, new EamsSession(), (_, _) => { validated = true; return Task.CompletedTask; });
        try { await service.LoginAsync(User, Password, CancellationToken.None); }
        catch (HttpRequestException) { }
        Require(handler.PostCount == 1, $"{(int)status} must not replay the credential-bearing POST");
        Require(!handler.RedirectTargetVisited, $"{(int)status} must not forward credentials to a redirect target");
        Require(!validated && service.Snapshot.State != AuthenticationState.Authenticated, "credential redirects must fail closed");
    }

    private static async Task CheckCrossDomainRedirectAsync()
    {
        var handler = new LoginHandler { LoginLocation = new Uri("https://attacker.example/collect") };
        var validated = false;
        using var service = new HttpAuthenticationService(handler, new EamsSession(), (_, _) => { validated = true; return Task.CompletedTask; });
        try { await service.LoginAsync(User, Password, CancellationToken.None); }
        catch (HttpRequestException) { }
        Require(!handler.RedirectTargetVisited, "CAS redirects must reject untrusted domains");
        Require(!validated && service.Snapshot.State != AuthenticationState.Authenticated, "untrusted redirects must fail closed");
    }

    private static async Task CheckMissingFingerprintTokenAsync()
    {
        var handler = new LoginHandler { FingerprintResponse = "{}" };
        var validated = false;
        using var service = new HttpAuthenticationService(handler, new EamsSession(), (_, _) => { validated = true; return Task.CompletedTask; });
        var result = await service.LoginAsync(User, Password, CancellationToken.None);
        Require(handler.PostCount == 0 && !validated && result.State != AuthenticationState.Authenticated,
            "a missing fingerprint token must prevent credential submission and authentication");
    }

    private static async Task CheckUnsupportedCaptchaVendorAsync()
    {
        var handler = new LoginHandler { CaptchaVendor = "third-party" };
        var validated = false;
        using var service = new HttpAuthenticationService(handler, new EamsSession(), (_, _) => { validated = true; return Task.CompletedTask; });
        var result = await service.LoginAsync(User, Password, CancellationToken.None);
        Require(handler.PassportRequests == 1, "login must follow the official passport intermediary");
        Require(result.State != AuthenticationState.Authenticated && !validated && handler.PostCount == 0 && handler.FingerprintRequests == 0,
            "unsupported captcha vendor must fail before fingerprint or credential submission");
    }

    private static async Task CheckNormalSmsEntryAsync(string directory)
    {
        var handler = new LoginHandler { RequireSms = true, SmsRule = "normal", SmsRisk = false };
        var validated = false;
        using var service = new HttpAuthenticationService(handler, new EamsSession(), (_, _) => { validated = true; return Task.CompletedTask; }, directory);
        var result = await service.LoginAsync(User, Password, CancellationToken.None);
        var id = result.ChallengeId ?? throw new FixtureFailureException("normal SMS page must create a challenge");
        Require(result.State == AuthenticationState.AwaitingSms && handler.SmsRequests == 0 && handler.CaptchaRequests == 0 && handler.FingerprintRequests == 1,
            "normal SMS must wait for manual action without a second risk token");
        await service.RequestSmsCodeAsync(id, CancellationToken.None);
        var imagePath = await service.ScreenshotChallengeAsync(id, CancellationToken.None);
        Require((await File.ReadAllBytesAsync(imagePath)).SequenceEqual(LoginHandler.Image), "normal SMS captcha must be the server response");
        await service.SubmitCaptchaAsync(id, "correct", CancellationToken.None);
        Require(handler.SmsRequests == 1 && handler.SmsChecks == 0, "only manual captcha submission may send the normal SMS");
        var done = await service.SubmitSmsCodeAsync(id, "123456", CancellationToken.None);
        Require(done.State == AuthenticationState.Authenticated && validated && handler.SmsChecks == 1 && handler.SmsPosts == 1 && handler.FingerprintRequests == 1 && handler.FixtureError is null,
            "normal SMS must complete using the current account and flow without final risk collection");
    }

    private static async Task CheckRiskSmsMissingKeyAsync(string directory)
    {
        // Official frontend reads a missing login-croypto as empty and submits without risk fields; the page still becomes a challenge.
        var handler = new LoginHandler { RequireSms = true, SmsKey = false };
        var validated = false;
        using var service = new HttpAuthenticationService(handler, new EamsSession(), (_, _) => { validated = true; return Task.CompletedTask; }, directory);
        var result = await service.LoginAsync(User, Password, CancellationToken.None);
        var id = result.ChallengeId ?? throw new FixtureFailureException("USTC SMS page without a key must still create a challenge");
        Require(result.State == AuthenticationState.AwaitingSms && handler.SmsRequests == 0 && handler.FingerprintRequests == 1,
            "a keyless USTC SMS page must wait for manual action without sending SMS");
        await service.RequestSmsCodeAsync(id, CancellationToken.None);
        await service.SubmitCaptchaAsync(id, "correct", CancellationToken.None);
        var done = await service.SubmitSmsCodeAsync(id, "123456", CancellationToken.None);
        Require(done.State == AuthenticationState.Authenticated && validated && handler.FixtureError is null && handler.FingerprintRequests == 1,
            "keyless USTC SMS must submit without risk fields and complete identity validation");
    }

    private static async Task CheckSmsChallengeAsync(string directory, string rule)
    {
        var handler = new LoginHandler { RequireSms = true, SmsRule = rule };
        var validated = false;
        using var service = new HttpAuthenticationService(handler, new EamsSession(), (_, _) => { validated = true; return Task.CompletedTask; }, directory);
        var login = await service.LoginAsync(User, Password, CancellationToken.None);
        Require(login.State == AuthenticationState.AwaitingSms && login.ChallengeId.HasValue, "SMS page must create a challenge");
        var id = login.ChallengeId!.Value;
        Require(!validated && handler.CaptchaRequests == 0 && handler.SmsRequests == 0, "challenge must not automatically send SMS");
        await service.RequestSmsCodeAsync(id, CancellationToken.None);
        var imagePath = await service.ScreenshotChallengeAsync(id, CancellationToken.None);
        Require((await File.ReadAllBytesAsync(imagePath)).SequenceEqual(LoginHandler.Image), "challenge image must be the server response");
        var wrong = await service.SubmitCaptchaAsync(id, "wrong", CancellationToken.None);
        Require(wrong.ChallengeId == id && wrong.State == AuthenticationState.AwaitingSms && handler.SmsRequests == 1, "wrong captcha must leave challenge available for manual retry");
        Require(handler.CaptchaRequests == 1 && handler.SmsChecks == 0, "wrong captcha must not automatically retry or submit SMS code");
        await service.RequestSmsCodeAsync(id, CancellationToken.None);
        await service.SubmitCaptchaAsync(id, "correct", CancellationToken.None);
        Require(handler.CaptchaRequests == 2 && handler.SmsRequests == 2 && handler.SmsChecks == 0, "only explicit actions may request captcha or send SMS");
        var badSms = await service.SubmitSmsCodeAsync(id, "000000", CancellationToken.None);
        Require(badSms.State == AuthenticationState.AwaitingSms && badSms.ChallengeId == id && handler.SmsChecks == 1 && handler.SmsPosts == 0,
            "invalid SMS code must retain the challenge without an automatic retry or login POST");
        var done = await service.SubmitSmsCodeAsync(id, "123456", CancellationToken.None);
        Require(done.State == AuthenticationState.Authenticated && validated && handler.SmsChecks == 2 && handler.SmsPosts == 1 && handler.FingerprintRequests == 2 && handler.FixtureError is null, "USTC SMS login must collect its own risk token and validate identity before authenticating");

        var secondHandler = new LoginHandler { RequireSms = true };
        using var second = new HttpAuthenticationService(secondHandler, new EamsSession(), (_, _) => Task.CompletedTask, directory);
        var secondLogin = await second.LoginAsync(User, Password, CancellationToken.None);
        var stale = secondLogin.ChallengeId ?? throw new FixtureFailureException("expected SMS challenge before logout");
        second.Logout();
        var rejected = false;
        try { await second.RequestSmsCodeAsync(stale, CancellationToken.None); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected && secondHandler.CaptchaRequests == 0 && second.Snapshot.State == AuthenticationState.SignedOut, "logout must invalidate the challenge before network activity");
    }

    private static async Task CheckPasswordCaptchaAsync(string directory)
    {
        var handler = new LoginHandler { RequirePasswordCaptcha = true };
        var validated = false;
        using var service = new HttpAuthenticationService(handler, new EamsSession(), (_, _) => { validated = true; return Task.CompletedTask; }, directory);
        var login = await service.LoginAsync(User, Password, CancellationToken.None);
        var id = login.ChallengeId ?? throw new FixtureFailureException("expected password captcha challenge");
        Require(login.State == AuthenticationState.AwaitingSms && handler.PostCount == 0 && !validated, "password must not be posted before manual captcha");
        await service.RequestSmsCodeAsync(id, CancellationToken.None);
        var image = await service.ScreenshotChallengeAsync(id, CancellationToken.None);
        Require((await File.ReadAllBytesAsync(image)).SequenceEqual(LoginHandler.Image), "password captcha image must be the server response");
        var completed = await service.SubmitCaptchaAsync(id, "correct", CancellationToken.None);
        Require(completed.State == AuthenticationState.Authenticated && validated && handler.PostCount == 1 && handler.FixtureError is null,
            "manual captcha must precede exactly one password POST and identity validation");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new FixtureFailureException(message);
    }

    private sealed class FixtureFailureException(string message) : Exception(message);

    private sealed class LoginHandler : HttpMessageHandler
    {
        public static readonly byte[] Image = [137, 80, 78, 71, 13, 10, 26, 10];
        public bool RequireSms { get; init; }
        public string SmsRule { get; init; } = "group";
        public bool SmsRisk { get; init; } = true;
        public bool SmsKey { get; init; } = true;
        public bool RequirePasswordCaptcha { get; init; }
        public string CaptchaVendor { get; init; } = "system";
        public int PassportRequests { get; private set; }
        public int FingerprintRequests { get; private set; }
        public int CaptchaRequests { get; private set; }
        public int SmsRequests { get; private set; }
        public int SmsChecks { get; private set; }
        public int SmsPosts { get; private set; }
        public string FingerprintResponse { get; init; } = "{\"responsetoken\":\"fixture-token\"}";
        public HttpStatusCode LoginRedirect { get; init; } = HttpStatusCode.Redirect;
        public Uri LoginLocation { get; init; } = new("https://jw.ustc.edu.cn/ucas-sso/login?ticket=fixture");
        public int PostCount { get; private set; }
        public int ReturnCount { get; private set; }
        public Exception? FixtureError { get; private set; }
        public bool RedirectTargetVisited { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "jw.ustc.edu.cn" && uri.AbsolutePath == "/ucas-sso/login")
            {
                if (uri.Query.Contains("ticket=fixture", StringComparison.Ordinal))
                {
                    ReturnCount++;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
                }
                return Redirect(HttpStatusCode.Redirect, new Uri("https://passport.ustc.edu.cn/login"));
            }
            if (uri.Host == "passport.ustc.edu.cn" && uri.AbsolutePath == "/login" && request.Method == HttpMethod.Get)
            {
                PassportRequests++;
                return Redirect(HttpStatusCode.Redirect, Cas);
            }
            if (uri.Host == "id.ustc.edu.cn" && uri.AbsolutePath == "/cas/login" && request.Method == HttpMethod.Get)
            {
                var html = $"<p id='login-croypto'>{Convert.ToBase64String(Key)}</p><p id='login-page-flowkey'>FLOW</p><p id='riskSystemSwitch'>USTC</p><p id='targetSystem'>sso</p><p id='siteId'></p><p id='recaptchaVendor'>{CaptchaVendor}</p>";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
            }
            if (uri.Host == "id.ustc.edu.cn" && uri.AbsolutePath == "/gate/sso-extend/protected/api/dictconfig/get" && request.Method == HttpMethod.Post)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"code\":200,\"data\":{\"URL_CRYPTO_DATA\":{\"SID\":[],\"5G\":[]}}}", Encoding.UTF8, "application/json") };
            if (uri.Host == "id.ustc.edu.cn" && uri.AbsolutePath == "/cas/api/protected/user/findCaptchaCount/" + User && request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RequirePasswordCaptcha ? "{\"code\":200,\"data\":{\"captchaInvisible\":true,\"captchaUrl\":\"/cas/api/protected/captcha/generate/DEFAULT\"}}" : "{\"code\":200,\"data\":{\"captchaInvisible\":false,\"captchaUrl\":\"\"}}", Encoding.UTF8, "application/json") };
            if (uri.Host == "analytics.ustc.edu.cn" && uri.AbsolutePath == "/fp" && request.Method == HttpMethod.Post)
            {
                FingerprintRequests++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FingerprintResponse, Encoding.UTF8, "application/json") };
            }
            if (uri.Host == "id.ustc.edu.cn" && request.Method == HttpMethod.Post && uri.AbsolutePath == "/gate/sso-extend/protected/api/aggregate/sms/publicNoToken/generate")
            {
                CaptchaRequests++;
                var content = new ByteArrayContent(Image);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            if (uri.Host == "id.ustc.edu.cn" && uri.AbsolutePath == "/cas/api/protected/captcha/generate/DEFAULT" && request.Method == HttpMethod.Get)
            {
                CaptchaRequests++;
                var content = new ByteArrayContent(Image);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            if (uri.Host == "id.ustc.edu.cn" && uri.AbsolutePath.StartsWith("/gate/sso-extend/protected/api/aggregate/sms/publicNoToken/sendCheckCaptcha/DEFAULT/", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                SmsRequests++;
                var destination = SmsRule == "normal" ? User : "13800000000";
                var expected = $"/gate/sso-extend/protected/api/aggregate/sms/publicNoToken/sendCheckCaptcha/DEFAULT/correct/{destination}/0008";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(uri.AbsolutePath == expected ? "{\"code\":200}" : "{\"code\":412}", Encoding.UTF8, "application/json") };
            }
            if (uri.Host == "id.ustc.edu.cn" && uri.AbsolutePath == "/cas/api/protected/sms/checkTokenResult" && request.Method == HttpMethod.Post)
            {
                SmsChecks++;
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var token = json.RootElement.GetProperty("token").GetString();
                Require(json.RootElement.GetProperty("phone").GetString() == (SmsRule == "normal" ? User : "13800000000") && !json.RootElement.GetProperty("delete").GetBoolean(), "SMS check must use the current account for normal or the bound phone for group");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(token == "123456" ? "{\"code\":200}" : "{\"code\":412}", Encoding.UTF8, "application/json") };
            }
            if (uri.Host == "id.ustc.edu.cn" && uri.AbsolutePath == "/cas/login" && request.Method == HttpMethod.Post)
            {
                PostCount++;
                try
                {
                    var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                    Require(!body.Contains(Password, StringComparison.Ordinal) && !Uri.UnescapeDataString(body).Contains(Password, StringComparison.Ordinal), "password must never occur in plaintext in the submitted form");
                    var fields = body.Split('&').Select(part => part.Split('=', 2)).ToDictionary(part => Decode(part[0]), part => Decode(part.Length == 2 ? part[1] : ""));
                    if (fields["type"] == "smsLogin")
                    {
                        SmsPosts++;
                        Require(fields["username"] == (SmsRule == "normal" ? User : "13800000000") && fields["password"] == "123456" && fields["execution"] == "SMS-FLOW" && fields.ContainsKey("captcha_code"), "SMS form must carry the correct account, code and new flow");
                        if (SmsRisk && SmsKey)
                        {
                            Require(fields["croypto"] == Convert.ToBase64String(Key) && fields["riskEngine"] == "true", "USTC SMS must use its own key and risk engine");
                            using var smsRisk = JsonDocument.Parse(Decrypt(fields["risk_payload"]));
                            Require(smsRisk.RootElement.GetProperty("token").GetString() == "fixture-token", "USTC SMS must include its risk token");
                        }
                        else
                        {
                            Require(!fields.ContainsKey("croypto") && !fields.ContainsKey("risk_payload") && !fields.ContainsKey("riskEngine"), "SMS without risk must not submit risk fields");
                        }
                        return Redirect(LoginRedirect, LoginLocation);
                    }
                    Require(fields["username"] == User && fields["execution"] == "FLOW", "login must submit the supplied username and current flow");
                    Require(fields["type"] == "UsernamePassword" && fields["_eventId"] == "submit", "login must use the official password flow");
                    Require(Decrypt(fields["password"]) == Password, "password must use the page AES key, ECB and PKCS7");
                    Require(Decrypt(fields["captcha_payload"]) == "{}", "initial captcha payload must be encrypted");
                    if (RequirePasswordCaptcha) Require(fields["captcha_code"] == "correct", "password form must contain the manually supplied captcha");
                    using var risk = JsonDocument.Parse(Decrypt(fields["risk_payload"]));
                    Require(risk.RootElement.GetProperty("token").GetString() == "fixture-token", "risk payload must contain the issued fingerprint token");
                }
                catch (Exception ex) { FixtureError = ex; throw; }
                if (RequireSms)
                {
                    var risk = SmsRisk ? "<p id='riskSystemSwitch'>USTC</p>" : "";
                    var key = SmsRisk && SmsKey ? Convert.ToBase64String(Key) : "";
                    var html = $"<p id='login-rule-type'>{SmsRule}</p><p id='current-login-type'>smsLogin</p><p id='phone-number'>13800000000</p><p id='login-page-flowkey'>SMS-FLOW</p><p id='login-croypto'>{key}</p>{risk}";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
                }
                return Redirect(LoginRedirect, LoginLocation);
            }
            RedirectTargetVisited = true;
            throw new InvalidOperationException("Unexpected HTTP request: " + request.Method + " " + uri);
        }

        private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
        private static string Decrypt(string value)
        {
            using var aes = Aes.Create();
            aes.Key = Key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.PKCS7;
            return Encoding.UTF8.GetString(aes.DecryptEcb(Convert.FromBase64String(value), PaddingMode.PKCS7));
        }
        private static HttpResponseMessage Redirect(HttpStatusCode status, Uri location) => new(status) { Headers = { Location = location } };
    }
}
