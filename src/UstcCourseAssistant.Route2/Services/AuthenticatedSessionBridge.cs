using Microsoft.Web.WebView2.Core;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace UstcCourseAssistant.Route2.Services;

/// <summary>
/// 将 WebView2 当前会话所需的教务 Cookie 复制到仅存在于内存中的 HttpClient。
/// Cookie 值不会向调用方暴露，也不会写入配置或日志。
/// </summary>
public sealed class AuthenticatedSessionBridge : IDisposable
{
    private static readonly Uri EamsRoot = new("https://jw.ustc.edu.cn/");
    private readonly HttpClientHandler _handler;

    private AuthenticatedSessionBridge(HttpClientHandler handler, HttpClient client)
    {
        _handler = handler;
        Client = client;
    }

    internal HttpClient Client { get; }

    public static async Task<AuthenticatedSessionBridge> CreateAsync(
        CoreWebView2 webView,
        Uri? currentPage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(webView);
        cancellationToken.ThrowIfCancellationRequested();

        var cookies = await webView.CookieManager.GetCookiesAsync(EamsRoot.AbsoluteUri);
        cancellationToken.ThrowIfCancellationRequested();

        var container = new CookieContainer();
        var copiedCount = 0;
        foreach (var webCookie in cookies)
        {
            if (!IsAllowedCookieDomain(webCookie.Domain))
            {
                continue;
            }

            try
            {
                container.Add(EamsRoot, webCookie.ToSystemNetCookie());
                copiedCount++;
            }
            catch (CookieException)
            {
                // 忽略浏览器可接受但 System.Net 无法表示的非关键 Cookie；绝不记录其内容。
            }
        }

        if (copiedCount == 0)
        {
            throw new AuthenticationRequiredException();
        }

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            CookieContainer = container,
            UseCookies = true
        };

        var client = new HttpClient(handler)
        {
            BaseAddress = EamsRoot,
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", webView.Settings.UserAgent);

        var safeReferer = currentPage is not null && IsEamsHost(currentPage)
            ? currentPage
            : new Uri(EamsRoot, "for-std/course-select");
        client.DefaultRequestHeaders.Referrer = safeReferer;

        return new AuthenticatedSessionBridge(handler, client);
    }

    internal static bool IsEamsHost(Uri uri) =>
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals(EamsRoot.Host, StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedCookieDomain(string domain)
    {
        var normalized = domain.Trim().TrimStart('.');
        return normalized.Equals("jw.ustc.edu.cn", StringComparison.OrdinalIgnoreCase)
               || normalized.Equals("ustc.edu.cn", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        Client.Dispose();
        _handler.Dispose();
    }
}
