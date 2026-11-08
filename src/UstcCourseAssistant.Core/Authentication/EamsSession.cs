using System.Net;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("UstcCourseAssistant.CoreChecks")]

namespace UstcCourseAssistant.Core.Authentication;

public sealed class EamsSession : IDisposable
{
    private static long _nextGeneration;
    public long Generation { get; } = Interlocked.Increment(ref _nextGeneration);
    public CookieContainer Cookies { get; } = new();
    public HttpClient Client { get; }

    public EamsSession()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = Cookies,
            UseCookies = true
        };
        Client = new HttpClient(handler) { BaseAddress = new Uri("https://jw.ustc.edu.cn/"), Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1024 * 1024 };
        Client.DefaultRequestHeaders.UserAgent.ParseAdd("USTC-Course-Assistant/2.2.1");
        Client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9");
    }

    public void Dispose() => Client.Dispose();
}
