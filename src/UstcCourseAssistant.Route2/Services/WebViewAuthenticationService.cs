using System.Net;
using Microsoft.Web.WebView2.Core;
using UstcCourseAssistant.Core.Authentication;
using UstcCourseAssistant.Core.Services;

namespace UstcCourseAssistant.Route2.Services;

/// <summary>将用户在 WebView2 完成的登录转换为内存中的教务会话。</summary>
public sealed class WebViewAuthenticationService : IAuthenticationService
{
    private EamsSession? _session;
    private AuthenticationSnapshot _snapshot = new(AuthenticationState.SignedOut, null, "请在教务窗口登录。", null);
    public AuthenticationSnapshot Snapshot => _snapshot;

    public async Task<AuthenticationSnapshot> ImportAsync(CoreWebView2 browser, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(browser);
        var cookies = await browser.CookieManager.GetCookiesAsync("https://jw.ustc.edu.cn/");
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = new EamsSession();
        try
        {
            foreach (var item in cookies)
            {
                var domain = item.Domain.TrimStart('.');
                if (!domain.Equals("jw.ustc.edu.cn", StringComparison.OrdinalIgnoreCase) && !domain.Equals("ustc.edu.cn", StringComparison.OrdinalIgnoreCase)) continue;
                candidate.Cookies.Add(item.ToSystemNetCookie());
            }
            await new ReadOnlyEamsClient(candidate.Client).ValidateAsync(cancellationToken);
            var old = Interlocked.Exchange(ref _session, candidate);
            old?.Dispose();
            return _snapshot = new(AuthenticationState.Authenticated, null, "教务身份已验证。", null);
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }

    public EamsSession GetAuthenticatedSession() => _session ?? throw new AuthenticationRequiredException();
    public Task<AuthenticationSnapshot> LoginAsync(string userName, string password, CancellationToken cancellationToken) => Task.FromResult(_snapshot = new(AuthenticationState.Failed, null, "请在教务窗口中完成登录。", null));
    public Task<AuthenticationSnapshot> RequestSmsCodeAsync(Guid challengeId, CancellationToken cancellationToken) => Task.FromResult(_snapshot = new(AuthenticationState.Failed, null, "请在教务窗口中请求验证码。", null));
    public Task<AuthenticationSnapshot> SubmitSmsCodeAsync(Guid challengeId, string code, CancellationToken cancellationToken) => Task.FromResult(_snapshot = new(AuthenticationState.Failed, null, "请在教务窗口中输入验证码。", null));
    public Task<string> ScreenshotChallengeAsync(Guid challengeId, CancellationToken cancellationToken) => throw new NotSupportedException("请在教务窗口完成验证。 ");
    public Task<AuthenticationSnapshot> SubmitCaptchaAsync(Guid challengeId, string code, CancellationToken cancellationToken) => Task.FromResult(_snapshot = new(AuthenticationState.Failed, null, "请在教务窗口完成验证。", null));
    public void Logout() { Interlocked.Exchange(ref _session, null)?.Dispose(); _snapshot = new(AuthenticationState.SignedOut, null, "已退出登录。", null); }
    public void Dispose() => Logout();
}
