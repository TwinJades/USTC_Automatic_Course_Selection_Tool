using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Windows;

namespace UstcCourseAssistant;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitializeBrowserAsync();
        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "USTC Course Assistant", "browser-profile");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            await Browser.EnsureCoreWebView2Async(environment);
            LoginSession.Window = this;
            Browser.CoreWebView2.NavigationCompleted += (_, e) => { if (e.IsSuccess) UpdateSession(); };
            Browser.CoreWebView2.SourceChanged += (_, _) => UpdateSession();
            Browser.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                // 教务系统将选课页以新窗口打开；接管并导航到同一内置会话，避免丢失认证状态。
                e.Handled = true;
                Browser.CoreWebView2.Navigate(e.Uri);
            };
            Browser.CoreWebView2.Navigate("https://jw.ustc.edu.cn/login");
            StatusText.Text = "请在页面中完成登录。短信验证必须由你本人输入。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "无法初始化登录窗口：" + ex.Message;
        }
    }

    public Task<string> ExecuteAsync(string script) => Browser.CoreWebView2.ExecuteScriptAsync(script);

    private void UpdateSession()
    {
        var source = Browser.CoreWebView2?.Source;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var address)) return;
        LoginSession.LastPage = address;
        var loggedIn = address.Host.Equals("jw.ustc.edu.cn", StringComparison.OrdinalIgnoreCase)
                       && address.AbsolutePath.StartsWith("/for-std/", StringComparison.OrdinalIgnoreCase);
        if (loggedIn)
        {
            LoginSession.IsAuthenticated = true;
            StatusText.Text = "已检测到选课页面，认证会话已就绪。请保持此窗口打开或最小化。";
        }
    }
}
