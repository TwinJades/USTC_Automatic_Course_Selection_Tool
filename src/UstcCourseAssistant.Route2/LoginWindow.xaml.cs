using Microsoft.Web.WebView2.Core;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using UstcCourseAssistant.Route2.Services;

namespace UstcCourseAssistant.Route2;

public partial class LoginWindow : Window
{
    private static readonly Uri LoginUri = new("https://jw.ustc.edu.cn/login");

    private bool _initialized;
    private bool _allowClose;
    private bool _autoLoginEnabled;
    private bool _autoLoginSubmitted;
    private bool _entryActivationAttempted;
    private bool _autoLoginSuppressed;
    private bool _manualRequirementReported;
    private string? _autoLoginSuppressionMessage;
    private CancellationTokenSource? _autoLoginProbeCancellation;
    private Func<StoredCredential?>? _credentialProvider;

    public LoginWindow()
    {
        InitializeComponent();
        Loaded += LoginWindow_Loaded;
        Closing += LoginWindow_Closing;
    }

    public event EventHandler? BrowserReady;
    public event EventHandler? AuthenticatedPageReached;
    public event EventHandler<AutoLoginStatusEventArgs>? AutoLoginStatusChanged;

    public bool IsBrowserReady => Browser.CoreWebView2 is not null;

    private async void LoginWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            var profile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "USTC Course Assistant Route2",
                "browser-profile");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
            await Browser.EnsureCoreWebView2Async(environment);

            Browser.CoreWebView2.NavigationStarting += (_, _) =>
            {
                CancelAutoLoginProbe();
                BrowserStatusText.Text = "页面加载中…";
            };
            Browser.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                BrowserStatusText.Text = args.IsSuccess
                    ? DescribeBrowserState()
                    : "页面加载失败，请检查网络。";
                if (args.IsSuccess && IsLikelyAuthenticatedPage())
                {
                    CancelAutoLoginProbe();
                    _autoLoginSubmitted = false;
                    _entryActivationAttempted = false;
                    _manualRequirementReported = false;
                    AuthenticatedPageReached?.Invoke(this, EventArgs.Empty);
                }
                else if (args.IsSuccess)
                {
                    StartAutoLoginProbe();
                }
            };
            Browser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                Browser.CoreWebView2.Navigate(args.Uri);
            };

            BrowserReady?.Invoke(this, EventArgs.Empty);
            BeginAuthenticationFlow(clearSuppression: false);
            Browser.CoreWebView2.Navigate(LoginUri.AbsoluteUri);
        }
        catch (Exception ex)
        {
            BrowserStatusText.Text = "无法初始化内置浏览器：" + ex.Message;
        }
    }

    public void ShowBrowserWindow()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        StartAutoLoginProbe();
    }

    public void Logout()
    {
        CancelAutoLoginProbe();
        _autoLoginSuppressed = true;
        _autoLoginSuppressionMessage = "你已主动退出教务登录；本次运行已暂停自动登录。取消并重新勾选自动登录后可解除。";
        _autoLoginSubmitted = false;
        _entryActivationAttempted = false;
        ReportManualRequirement(_autoLoginSuppressionMessage, AutoLoginStatus.Suppressed);
        if (Browser.CoreWebView2 is null)
        {
            return;
        }

        Browser.CoreWebView2.CookieManager.DeleteAllCookies();
        ShowAndNavigate(LoginUri);
    }

    public void ConfigureAutoLogin(
        bool enabled,
        Func<StoredCredential?>? credentialProvider,
        bool releaseUserSuppression = false)
    {
        _autoLoginEnabled = enabled;
        _credentialProvider = credentialProvider;
        if (!enabled)
        {
            CancelAutoLoginProbe();
            _autoLoginSuppressed = true;
            _autoLoginSuppressionMessage = "自动登录未启用。";
        }
        else
        {
            if (_autoLoginSuppressed && !releaseUserSuppression)
            {
                if (_autoLoginSuppressionMessage is { } reason)
                {
                    ReportManualRequirement(reason, AutoLoginStatus.Suppressed);
                }

                return;
            }

            BeginAuthenticationFlow(clearSuppression: releaseUserSuppression);
            StartAutoLoginProbe();
        }
    }

    public void RequestAuthentication()
    {
        if (!_autoLoginSuppressed)
        {
            BeginAuthenticationFlow(clearSuppression: false);
        }
        else if (_autoLoginEnabled && _autoLoginSuppressionMessage is { } reason)
        {
            ReportManualRequirement(reason, AutoLoginStatus.Suppressed);
        }

        ShowAndNavigate(LoginUri);
    }

    public void ClearLoginState()
    {
        Browser.CoreWebView2?.CookieManager.DeleteAllCookies();
    }

    public async Task<AuthenticatedSessionBridge> CreateSessionBridgeAsync(
        CancellationToken cancellationToken = default)
    {
        if (Browser.CoreWebView2 is null)
        {
            throw new InvalidOperationException("教务浏览器仍在初始化。");
        }

        var currentPage = Uri.TryCreate(Browser.Source?.AbsoluteUri, UriKind.Absolute, out var page)
            ? page
            : null;
        return await AuthenticatedSessionBridge.CreateAsync(
            Browser.CoreWebView2,
            currentPage,
            cancellationToken);
    }

    public void CloseForApplication()
    {
        _allowClose = true;
        Close();
    }

    private void ShowAndNavigate(Uri target)
    {
        ShowBrowserWindow();
        if (Browser.CoreWebView2 is null)
        {
            BrowserStatusText.Text = "内置浏览器仍在初始化…";
            return;
        }

        Browser.CoreWebView2.Navigate(target.AbsoluteUri);
    }

    private string DescribeBrowserState()
    {
        if (IsLikelyAuthenticatedPage())
        {
            return "已进入教务系统；程序将自动识别只读会话。";
        }

        if (_autoLoginEnabled && _autoLoginSuppressed && _autoLoginSuppressionMessage is { } reason)
        {
            return reason;
        }

        return _autoLoginEnabled
            ? "正在识别教务登录入口或统一身份认证页面…"
            : "请在此窗口中自行完成认证。";
    }

    private bool IsLikelyAuthenticatedPage()
    {
        return Uri.TryCreate(Browser.CoreWebView2?.Source, UriKind.Absolute, out var uri)
               && uri.Host.Equals("jw.ustc.edu.cn", StringComparison.OrdinalIgnoreCase)
               && !uri.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase);
    }

    private void BeginAuthenticationFlow(bool clearSuppression)
    {
        CancelAutoLoginProbe();
        _autoLoginSubmitted = false;
        _entryActivationAttempted = false;
        _manualRequirementReported = false;
        if (clearSuppression)
        {
            _autoLoginSuppressed = false;
            _autoLoginSuppressionMessage = null;
        }
    }

    private void StartAutoLoginProbe()
    {
        if (!_autoLoginEnabled || _autoLoginSuppressed || Browser.CoreWebView2 is null || IsLikelyAuthenticatedPage())
        {
            return;
        }

        CancelAutoLoginProbe();
        _autoLoginProbeCancellation = new CancellationTokenSource();
        _ = RunAutoLoginProbeAsync(_autoLoginProbeCancellation.Token);
    }

    private async Task RunAutoLoginProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 1; attempt <= AutoLoginNavigationPolicy.MaximumPageProbeAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Browser.CoreWebView2 is null
                    || !Uri.TryCreate(Browser.CoreWebView2.Source, UriKind.Absolute, out var currentUri))
                {
                    return;
                }

                if (AutoLoginNavigationPolicy.IsTeachingLoginEntry(currentUri))
                {
                    if (!_entryActivationAttempted)
                    {
                        var entryJson = await Browser.CoreWebView2.ExecuteScriptAsync(
                            LoginFormAutomation.BuildTeachingLoginEntryScript());
                        var entryResult = LoginFormAutomation.ParseResult(entryJson);
                        if (entryResult == "entry-clicked")
                        {
                            _entryActivationAttempted = true;
                            ReportProgress("已找到教务登录入口，正在前往统一身份认证页。", AutoLoginStatus.Navigating);
                        }
                    }
                }
                else if (AutoLoginNavigationPolicy.IsIdentityProvider(currentUri))
                {
                    var shouldStop = await TryIdentityProviderLoginOnceAsync();
                    if (shouldStop)
                    {
                        return;
                    }
                }
                else
                {
                    ReportManualRequirement(
                        "当前认证页面不在允许的 USTC 教务登录入口或统一身份认证域名内；自动登录已停止。",
                        AutoLoginStatus.Error);
                    return;
                }

                await Task.Delay(AutoLoginNavigationPolicy.PageProbeDelay, cancellationToken);
            }

            ReportManualRequirement(
                "登录页面在有限等待时间内仍未准备好；自动登录已停止，请由你本人继续。",
                AutoLoginStatus.ManualLoginRequired);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or JsonException or Win32Exception)
        {
            ReportManualRequirement("自动登录已停止；请由你本人继续登录。", AutoLoginStatus.Error);
        }
    }

    private async Task<bool> TryIdentityProviderLoginOnceAsync()
    {
        var classificationJson = await Browser.CoreWebView2.ExecuteScriptAsync(
            LoginFormAutomation.BuildClassificationScript());
        var classification = LoginFormAutomation.ParseResult(classificationJson);
        if (classification == "verification")
        {
            StopForManualVerification();
            return true;
        }

        if (classification != "login")
        {
            return false;
        }

        if (_autoLoginSubmitted)
        {
            ReportManualRequirement("本次登录流程已经提交过一次；不会重复提交，请由你本人继续。", AutoLoginStatus.ManualLoginRequired);
            return true;
        }

        var credential = _credentialProvider?.Invoke();
        if (credential is null || string.IsNullOrWhiteSpace(credential.UserName) || credential.Password.Length == 0)
        {
            ReportManualRequirement("没有可用的 Windows 保存凭据；请由你本人登录。", AutoLoginStatus.ManualLoginRequired);
            return true;
        }

        // 在执行脚本前即消费本流程唯一的提交机会。即使脚本调用异常，也不得猜测失败后再次提交。
        _autoLoginSubmitted = true;
        var submissionJson = await Browser.CoreWebView2.ExecuteScriptAsync(
            LoginFormAutomation.BuildSubmissionScript(credential.UserName, credential.Password));
        var submission = LoginFormAutomation.ParseResult(submissionJson);
        if (submission == "verification")
        {
            StopForManualVerification();
            return true;
        }

        if (submission is "submitted" or "filled")
        {
            var message = submission == "submitted"
                ? "已使用 Windows 保存的凭据提交正常登录表单；等待校方响应。"
                : "已填写正常登录表单，但无法安全自动提交；请由你本人继续。";
            BrowserStatusText.Text = message;
            AutoLoginStatusChanged?.Invoke(
                this,
                new AutoLoginStatusEventArgs(
                    submission == "submitted" ? AutoLoginStatus.FormSubmitted : AutoLoginStatus.ManualLoginRequired,
                    message));
            return true;
        }

        ReportManualRequirement(
            "登录表单在提交前发生变化；本次流程不会再次尝试，请由你本人继续。",
            AutoLoginStatus.ManualLoginRequired);
        return true;
    }

    private void StopForManualVerification()
    {
        _autoLoginSuppressed = true;
        _autoLoginSuppressionMessage = "检测到短信、验证码、OTP、新设备或二次认证；自动登录已停止，请由你本人完成。";
        CancelAutoLoginProbe();
        ReportManualRequirement(
            _autoLoginSuppressionMessage,
            AutoLoginStatus.VerificationRequired);
    }

    private void ReportProgress(string message, AutoLoginStatus status)
    {
        BrowserStatusText.Text = message;
        AutoLoginStatusChanged?.Invoke(this, new AutoLoginStatusEventArgs(status, message));
    }

    private void ReportManualRequirement(string message, AutoLoginStatus status)
    {
        BrowserStatusText.Text = message;
        if (_manualRequirementReported && status == AutoLoginStatus.ManualLoginRequired)
        {
            return;
        }

        _manualRequirementReported = true;
        AutoLoginStatusChanged?.Invoke(this, new AutoLoginStatusEventArgs(status, message));
    }

    private void CancelAutoLoginProbe()
    {
        var cancellation = _autoLoginProbeCancellation;
        _autoLoginProbeCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void LoginWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            CancelAutoLoginProbe();
            Browser.Dispose();
            return;
        }

        e.Cancel = true;
        Hide();
    }
}

public enum AutoLoginStatus
{
    Navigating,
    FormSubmitted,
    VerificationRequired,
    ManualLoginRequired,
    Suppressed,
    Error
}

public sealed record AutoLoginStatusEventArgs(AutoLoginStatus Status, string Message);
