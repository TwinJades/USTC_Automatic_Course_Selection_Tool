using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using UstcCourseAssistant.Route2.Models;
using UstcCourseAssistant.Route2.Services;

namespace UstcCourseAssistant.Route2;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<TurnInfo> _turns = [];
    private readonly ObservableCollection<LessonInfo> _targetLessons = [];
    private readonly ObservableCollection<LessonInfo> _selectedLessons = [];
    private readonly ObservableCollection<AutomationTask> _automationTasks = [];
    private readonly ObservableCollection<LocalLogEntry> _activityEntries = [];
    private readonly ObservableCollection<LocalLogEntry> _keyActivityEntries = [];
    private readonly FlowDocument _allActivityDocument = new() { PagePadding = new Thickness(0) };
    private readonly FlowDocument _keyActivityDocument = new() { PagePadding = new Thickness(0) };
    private readonly Paragraph _allActivityParagraph = new() { Margin = new Thickness(0) };
    private readonly Paragraph _keyActivityParagraph = new() { Margin = new Thickness(0) };
    private readonly AutomationStore _automationStore = new();
    private readonly LocalActivityLog _localActivityLog = new();
    private readonly OperationJournalStore _operationJournalStore = new();
    private readonly ICredentialVault _credentialVault;
    private readonly DispatcherTimer _automationTimer = new();
    private readonly DispatcherTimer _automationTaskSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private DispatcherTimer? _automationArmHighlightTimer;
    private readonly RealOperationCoordinator _realOperations;
    private CancellationTokenSource? _validationCancellation;
    private SessionValidationResult? _sessionValidation;
    private LoginWindow? _loginWindow;
    private LessonInfo? _controlledLesson;
    private CourseMutationKind _precheckedMutationKind;
    private bool _controlledUiReady;
    private LocalPreferences _preferences = new(true);
    private AutomationSettings _automationSettings = new();
    private bool _preferencesReady;
    private bool _accountSecurityReady;
    private bool _hasSavedCredential;
    private bool _automationSettingsReady;
    private bool _startupOptionReady;
    private bool _automationRunning;
    private bool _automationCycleActive;
    private bool _interruptedInspectionCompleted;
    private bool _systemEventsSubscribed;
    private bool _networkWasUnavailable;
    private bool _readNetworkRecoveryPending;
    private bool _readNetworkRecoveryAttemptActive;
    private long _readRecoveryStudentId;
    private long _readRecoveryTurnId;
    private DateTimeOffset? _automationTickPlannedAt;
    private TimeSpan _automationTickPlannedDelay;
    private bool _isBusy;
    private bool _showAllActivity;
    private bool _shouldAutoShowOnboarding;
    private int _onboardingStepIndex;
    private readonly string? _configurationLoadWarning;

    public MainWindow()
    {
        var hadExistingInstallationData = LocalPreferences.HasExistingInstallationData();
        _credentialVault = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("USTC_ROUTE2_TEST_DATA_ROOT"))
            ? new WindowsCredentialVault()
            : new InMemoryCredentialVault();
        _realOperations = new RealOperationCoordinator(_operationJournalStore);
        InitializeComponent();
        _allActivityDocument.Blocks.Add(_allActivityParagraph);
        _keyActivityDocument.Blocks.Add(_keyActivityParagraph);
        TurnSelector.ItemsSource = _turns;
        TargetsGrid.ItemsSource = _targetLessons;
        SelectedLessonsGrid.ItemsSource = _selectedLessons;
        AutomationTasksGrid.ItemsSource = _automationTasks;
        _preferences = LocalPreferences.Load();
        if (_preferences.OnboardingCompleted is null)
        {
            _preferences = _preferences with { OnboardingCompleted = hadExistingInstallationData };
            _preferences.Save();
        }

        _shouldAutoShowOnboarding = _preferences.OnboardingCompleted == false;
        KeepLoginCheckBox.IsChecked = _preferences.KeepLoginState;
        InitializeAccountSecurityUi();
        _preferencesReady = true;
        var automationLoad = _automationStore.Load();
        _configurationLoadWarning = automationLoad.Warning;
        _automationSettings = automationLoad.Settings;
        foreach (var task in _automationSettings.Tasks.OrderBy(item => item.Priority))
        {
            _automationTasks.Add(task);
        }

        RenumberAutomationPriorities();
        LoadAutomationSettingsIntoUi();
        try
        {
            RunAtStartupCheckBox.IsChecked = StartupRegistrationService.IsEnabled();
            _automationSettings.RunAtWindowsStartup = RunAtStartupCheckBox.IsChecked == true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            RunAtStartupCheckBox.IsChecked = false;
        }

        _automationTimer.Tick += AutomationTimer_Tick;
        _automationTaskSaveTimer.Tick += (_, _) =>
        {
            _automationTaskSaveTimer.Stop();
            SaveAutomationSettings();
        };
        _automationSettingsReady = true;
        _startupOptionReady = true;
        _controlledUiReady = true;
        LoadLocalActivityHistory();
        if (automationLoad.Warning is { } configurationWarning)
        {
            AddActivity(configurationWarning, "配置恢复");
        }

        if (_realOperations.LoadWarning is { } operationWarning)
        {
            AddActivity(operationWarning, "安全");
        }

        RefreshInterruptedOperationBanner();
        SubscribeSystemSafetyEvents();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 主动实例化各操作页，避免把模板错误延迟到用户首次点击时才暴露。
        for (var index = 0; index < OperationTabs.Items.Count; index++)
        {
            OperationTabs.SelectedIndex = index;
            OperationTabs.UpdateLayout();
        }

        OperationTabs.SelectedIndex = 0;

        AddActivity("程序已启动；教务系统窗口尚未打开。", "信息", ActivityEventKind.ApplicationLifecycle);
        if (_realOperations.HasUnresolvedOperation)
        {
            SetStatus("检测到中断的真实操作；请登录后先读取实际状态。", StatusKind.Error);
        }
        else if (_configurationLoadWarning is { } warning)
        {
            SetStatus(warning, StatusKind.Warning);
            MessageBox.Show(
                warning + "\n\n请检查任务列表是否完整。程序不会自动开始监测。",
                "任务配置恢复",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        else
        {
            SetStatus("请点击“打开教务系统”登录或恢复会话。", StatusKind.Neutral);
        }

        SetBusy(false);
        if (_shouldAutoShowOnboarding)
        {
            Dispatcher.BeginInvoke(() => ShowOnboarding(), DispatcherPriority.ContextIdle);
        }
    }

    private LoginWindow EnsureLoginWindow()
    {
        if (_loginWindow is not null)
        {
            return _loginWindow;
        }

        _loginWindow = new LoginWindow();
        ConfigureLoginWindowAutoLogin();
        _loginWindow.BrowserReady += (_, _) =>
        {
            AddActivity("内置教务浏览器已就绪；等待用户完成认证。", "信息");
            SetStatus("教务浏览器已就绪；等待登录或自动识别。", StatusKind.Neutral);
            SetBusy(false);
        };
        _loginWindow.AuthenticatedPageReached += async (_, _) =>
        {
            await TryAutoValidateAsync();
        };
        _loginWindow.AutoLoginStatusChanged += (_, args) =>
        {
            var statusKind = args.Status switch
            {
                AutoLoginStatus.Navigating => StatusKind.Working,
                AutoLoginStatus.FormSubmitted => StatusKind.Working,
                AutoLoginStatus.VerificationRequired => StatusKind.Warning,
                AutoLoginStatus.Suppressed => StatusKind.Warning,
                AutoLoginStatus.Error => StatusKind.Error,
                _ => StatusKind.Neutral
            };
            SetStatus(args.Message, statusKind);
            AddActivity(args.Message, args.Status == AutoLoginStatus.FormSubmitted ? "自动登录" : "认证");
        };
        return _loginWindow;
    }

    private void OpenEamsButton_Click(object sender, RoutedEventArgs e)
    {
        var loginWindow = EnsureLoginWindow();
        if (loginWindow.IsBrowserReady)
        {
            loginWindow.ShowBrowserWindow();
        }
        else
        {
            loginWindow.Show();
        }

        AddActivity("已打开独立教务系统窗口。", "信息");
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loginWindow is null || !_loginWindow.IsBrowserReady)
        {
            SetStatus("内置浏览器仍在初始化。", StatusKind.Neutral);
            return;
        }

        _validationCancellation?.Cancel();
        _loginWindow.Logout();
        ResetSessionUi("已退出当前教务登录，请重新认证。");
        SetStatus(
            _preferences.AutoLoginEnabled && _hasSavedCredential
                ? "已退出教务登录；本次运行已抑制自动登录，取消并重新勾选后可解除。"
                : "已清除内置浏览器的登录会话。",
            _preferences.AutoLoginEnabled && _hasSavedCredential ? StatusKind.Warning : StatusKind.Success);
        AddActivity("用户主动退出教务登录；内置浏览器 Cookie 已清除。", "信息");
    }

    private void KeepLoginCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_preferencesReady)
        {
            return;
        }

        _preferences = _preferences with { KeepLoginState = KeepLoginCheckBox.IsChecked == true };
        _preferences.Save();
        AddActivity(
            _preferences.KeepLoginState
                ? "已启用：关闭程序后保留内置浏览器登录状态。"
                : "已禁用：关闭程序时清除内置浏览器登录状态。",
            "设置");
    }

    private void InitializeAccountSecurityUi()
    {
        SavedAccountTextBox.Text = _preferences.SavedAccount;
        try
        {
            _hasSavedCredential = _credentialVault.Exists();
            if (!_hasSavedCredential && _preferences.AutoLoginEnabled)
            {
                _preferences = _preferences with { AutoLoginEnabled = false };
                _preferences.Save();
            }

            AutoLoginCheckBox.IsChecked = _hasSavedCredential && _preferences.AutoLoginEnabled;
            CredentialStatusText.Text = _hasSavedCredential
                ? "Windows 凭据管理器中已保存密码；程序不会把密码显示或写入日志。"
                : "尚未在 Windows 凭据管理器中保存密码。";
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _hasSavedCredential = false;
            _preferences = _preferences with { AutoLoginEnabled = false };
            AutoLoginCheckBox.IsChecked = false;
            CredentialStatusText.Text = "无法读取 Windows 凭据管理器状态；自动登录保持关闭。";
        }

        _accountSecurityReady = true;
        UpdateAccountSecurityControls();
    }

    private void SaveCredentialButton_Click(object sender, RoutedEventArgs e)
    {
        var account = SavedAccountTextBox.Text.Trim();
        using var password = SavedPasswordBox.SecurePassword;
        if (account.Length == 0 || password.Length == 0)
        {
            CredentialStatusText.Text = "请输入账号和密码后再保存。";
            SetStatus("账号或密码为空，未保存任何凭据。", StatusKind.Error);
            return;
        }

        try
        {
            _credentialVault.Save(account, password);
            SavedPasswordBox.Clear();
            _hasSavedCredential = true;
            _preferences = _preferences with { SavedAccount = account };
            _preferences.Save();
            CredentialStatusText.Text = "密码已保存到当前 Windows 用户的凭据管理器；密码输入框已清空。";
            AddActivity("已更新 Windows 凭据管理器中的教务登录凭据；未记录账号或密码。", "安全");
            SetStatus("账号与密码已安全保存；自动登录是否启用由下方选项决定。", StatusKind.Success);
            UpdateAccountSecurityControls();
            ConfigureLoginWindowAutoLogin();
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            SavedPasswordBox.Clear();
            CredentialStatusText.Text = "凭据保存失败；密码输入框已清空，普通配置中没有写入密码。";
            SetStatus("无法保存到 Windows 凭据管理器。", StatusKind.Error);
            AddActivity("Windows 凭据管理器保存失败；未记录账号、密码或错误正文。", "安全错误");
        }
    }

    private void DeleteCredentialButton_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = MessageBox.Show(
            this,
            "确定删除 Windows 凭据管理器中保存的教务密码吗？账号文字可以继续保留。",
            "确认删除已保存密码",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _credentialVault.Delete();
            SavedPasswordBox.Clear();
            _hasSavedCredential = false;
            _preferences = _preferences with { AutoLoginEnabled = false };
            _preferences.Save();
            _accountSecurityReady = false;
            AutoLoginCheckBox.IsChecked = false;
            _accountSecurityReady = true;
            CredentialStatusText.Text = "已删除 Windows 凭据管理器中的密码；自动登录已关闭。";
            AddActivity("用户已删除保存的教务密码并关闭自动登录。", "安全");
            SetStatus("已删除保存的密码。", StatusKind.Success);
            UpdateAccountSecurityControls();
            ConfigureLoginWindowAutoLogin();
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or System.Security.SecurityException)
        {
            CredentialStatusText.Text = "无法删除 Windows 凭据管理器中的密码。";
            SetStatus("删除已保存密码失败。", StatusKind.Error);
        }
    }

    private void AutoLoginCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_accountSecurityReady)
        {
            return;
        }

        var enabled = AutoLoginCheckBox.IsChecked == true && _hasSavedCredential;
        _preferences = _preferences with { AutoLoginEnabled = enabled };
        _preferences.Save();
        CredentialStatusText.Text = enabled
            ? "自动登录已启用；遇到短信、验证码或新设备验证时会立即停止。"
            : _hasSavedCredential
                ? "密码仍保存在 Windows 凭据管理器中；自动登录已关闭。"
                : "尚未保存密码；自动登录保持关闭。";
        AddActivity(enabled ? "已启用安全的可选自动登录。" : "已关闭自动登录。", "设置");
        ConfigureLoginWindowAutoLogin(userChangedOption: true);
    }

    private void ConfigureLoginWindowAutoLogin(bool userChangedOption = false)
    {
        _loginWindow?.ConfigureAutoLogin(
            _preferences.AutoLoginEnabled && _hasSavedCredential,
            () => _preferences.AutoLoginEnabled ? _credentialVault.Read() : null,
            releaseUserSuppression: userChangedOption && _preferences.AutoLoginEnabled);
    }

    private void UpdateAccountSecurityControls()
    {
        var editable = !_isBusy && !_automationRunning;
        SavedAccountTextBox.IsEnabled = editable;
        SavedPasswordBox.IsEnabled = editable;
        SaveCredentialButton.IsEnabled = editable;
        DeleteCredentialButton.IsEnabled = editable && _hasSavedCredential;
        AutoLoginCheckBox.IsEnabled = editable && _hasSavedCredential;
    }

    private void RequestAuthenticationAfterSessionExpiry()
    {
        if (!_preferences.AutoLoginEnabled || !_hasSavedCredential)
        {
            return;
        }

        var loginWindow = EnsureLoginWindow();
        loginWindow.RequestAuthentication();
        AddActivity("认证会话失效；已打开正常登录页尝试一次安全登录，任务不会自动恢复。", "认证");
    }

    private void ResetSessionUi(string message)
    {
        _sessionValidation = null;
        _turns.Clear();
        _targetLessons.Clear();
        _selectedLessons.Clear();
        StudentIdText.Text = "未识别";
        NoTurnsText.Text = message;
        SnapshotSummaryText.Text = message;
        MissingTargetText.Text = string.Empty;
        if (_controlledUiReady)
        {
            InvalidateControlledPrecheck(message + " 请恢复会话后重新预检查。");
        }
    }

    private async void ValidateButton_Click(object sender, RoutedEventArgs e)
    {
        await ValidateSessionAsync(automatic: false);
    }

    private async Task<bool> ValidateSessionAsync(bool automatic)
    {
        if (_loginWindow is null || !_loginWindow.IsBrowserReady)
        {
            SetStatus("请等待内置浏览器初始化完成。", StatusKind.Neutral);
            return false;
        }

        if (_isBusy)
        {
            return _sessionValidation is not null;
        }

        _validationCancellation?.Cancel();
        _validationCancellation?.Dispose();
        _validationCancellation = new CancellationTokenSource();
        var cancellationToken = _validationCancellation.Token;

        SetBusy(true);
        SetStatus("正在将当前会话复制到内存并执行只读验证…", StatusKind.Working);
        AddActivity(
            automatic ? "检测到已登录页面，开始自动识别只读会话。" : "开始手动只读会话验证。",
            "只读",
            ActivityEventKind.Verification);
        _sessionValidation = null;
        _turns.Clear();
        _targetLessons.Clear();
        _selectedLessons.Clear();
        StudentIdText.Text = "正在验证…";
        NoTurnsText.Text = "正在读取…";
        SnapshotSummaryText.Text = "请等待会话验证完成。";

        try
        {
            using var bridge = await _loginWindow.CreateSessionBridgeAsync(cancellationToken);
            var client = new ReadOnlyEamsClient(bridge);
            var result = await client.ValidateAsync(cancellationToken);
            _sessionValidation = result;

            StudentIdText.Text = result.StudentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var turn in result.OpenTurns)
            {
                _turns.Add(turn);
            }

            NoTurnsText.Text = _turns.Count == 0 ? "当前没有开放的选课轮次。" : string.Empty;
            if (_turns.Count > 0)
            {
                TurnSelector.SelectedIndex = 0;
                SnapshotSummaryText.Text = "请选择目标教学班代码，然后读取课程与人数。";
            }
            else
            {
                SnapshotSummaryText.Text = "当前没有可用于影子验证的开放轮次。";
            }

            SetStatus("只读会话验证成功。", StatusKind.Success);
            AddActivity($"只读会话验证成功；读取到 {_turns.Count} 个开放轮次。", "成功", ActivityEventKind.Verification);
            return true;
        }
        catch (AuthenticationRequiredException)
        {
            _sessionValidation = null;
            StudentIdText.Text = "需要重新认证";
            NoTurnsText.Text = "请在教务系统窗口重新完成登录后再验证。";
            SetStatus("认证会话无效，请由你本人重新登录。", StatusKind.Error);
            AddActivity("认证会话无效，所有只读请求已停止。", "暂停", ActivityEventKind.Verification);
            RequestAuthenticationAfterSessionExpiry();
            return false;
        }
        catch (OperationCanceledException)
        {
            SetStatus("只读验证已取消。", StatusKind.Neutral);
            AddActivity("只读会话验证已取消。", "信息", ActivityEventKind.Verification);
            return false;
        }
        catch (JsonException)
        {
            _sessionValidation = null;
            StudentIdText.Text = "验证失败";
            NoTurnsText.Text = "接口返回格式与预期不一致。";
            SetStatus("接口格式可能已变化，未执行任何写入操作。", StatusKind.Error);
            AddActivity("接口返回格式发生变化；只读验证已停止。", "错误", ActivityEventKind.Verification);
            return false;
        }
        catch (Exception ex)
        {
            _sessionValidation = null;
            StudentIdText.Text = "验证失败";
            NoTurnsText.Text = "未读取到开放轮次。";
            SetStatus(ex.Message, StatusKind.Error);
            AddActivity(ex.Message, "错误", ActivityEventKind.Verification);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task TryAutoValidateAsync()
    {
        if (_sessionValidation is not null
            || _isBusy
            || _loginWindow is null
            || !_loginWindow.IsBrowserReady)
        {
            return;
        }

        await ValidateSessionAsync(automatic: true);
    }

    private async void ReadCoursesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loginWindow is null || !_loginWindow.IsBrowserReady)
        {
            SetStatus("请等待内置浏览器初始化完成。", StatusKind.Neutral);
            return;
        }

        if (_sessionValidation is null && !await ValidateSessionAsync(automatic: true))
        {
            SetStatus("尚未识别到有效会话，请在教务系统页面完成登录。", StatusKind.Error);
            return;
        }

        if (TurnSelector.SelectedItem is not TurnInfo turn)
        {
            SetStatus("当前没有可用的开放选课轮次。", StatusKind.Neutral);
            return;
        }

        var sessionValidation = _sessionValidation;
        if (sessionValidation is null)
        {
            SetStatus("会话状态发生变化，请重试。", StatusKind.Neutral);
            return;
        }

        var targetCodes = ParseTargetCodes(TargetCodesText.Text);
        _validationCancellation?.Cancel();
        _validationCancellation?.Dispose();
        _validationCancellation = new CancellationTokenSource();
        var cancellationToken = _validationCancellation.Token;

        SetBusy(true);
        SetStatus("正在读取课程列表并批量查询目标人数…", StatusKind.Working);
        AddActivity($"开始阶段 B 影子读取；目标教学班 {targetCodes.Count} 个。", "只读");
        SnapshotSummaryText.Text = "正在读取已选课程、可选课程和目标人数…";
        MissingTargetText.Text = string.Empty;
        _targetLessons.Clear();
        _selectedLessons.Clear();

        try
        {
            using var bridge = await _loginWindow.CreateSessionBridgeAsync(cancellationToken);
            var client = new ReadOnlyEamsClient(bridge);
            var snapshot = await client.GetShadowSnapshotAsync(
                sessionValidation.StudentId,
                turn.Id,
                targetCodes,
                cancellationToken);
            foreach (var lesson in snapshot.TargetLessons)
            {
                _targetLessons.Add(lesson);
            }

            foreach (var lesson in snapshot.SelectedLessons)
            {
                _selectedLessons.Add(lesson);
            }

            SnapshotSummaryText.Text =
                $"可选课程 {snapshot.AddableLessonCount} 门；当前已选 {snapshot.SelectedLessons.Count} 门；匹配目标 {snapshot.TargetLessons.Count} 门。";
            MissingTargetText.Text = snapshot.MissingTargetCodes.Count == 0
                ? string.Empty
                : "未在已选或可选课程中找到：" + string.Join("、", snapshot.MissingTargetCodes);
            SetStatus("阶段 B 只读影子读取成功。", StatusKind.Success);
            AddActivity(
                $"影子读取成功；已选 {snapshot.SelectedLessons.Count} 门，可选 {snapshot.AddableLessonCount} 门，匹配目标 {snapshot.TargetLessons.Count} 门。",
                "成功");
        }
        catch (AuthenticationRequiredException)
        {
            _sessionValidation = null;
            SnapshotSummaryText.Text = "认证会话已经失效，请重新登录并验证。";
            SetStatus("认证会话无效，影子读取已停止。", StatusKind.Error);
            AddActivity("影子读取时发现认证失效；所有请求已停止。", "暂停");
            RequestAuthenticationAfterSessionExpiry();
        }
        catch (OperationCanceledException)
        {
            SetStatus("影子读取已取消。", StatusKind.Neutral);
            AddActivity("阶段 B 影子读取已取消。", "信息");
        }
        catch (JsonException)
        {
            SnapshotSummaryText.Text = "接口返回格式与预期不一致，未执行任何写入操作。";
            SetStatus("课程接口格式可能已经变化。", StatusKind.Error);
            AddActivity("课程接口返回格式发生变化；影子读取已停止。", "错误");
        }
        catch (Exception ex)
        {
            SnapshotSummaryText.Text = ex.Message;
            SetStatus(ex.Message, StatusKind.Error);
            AddActivity(ex.Message, "错误");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void LoadAutomationSettingsIntoUi()
    {
        AutomationCustomIntervalText.Text = _automationSettings.IntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AutomationIntervalSelector.SelectedIndex = _automationSettings.IntervalSeconds switch
        {
            5 => 0,
            15 => 1,
            60 => 2,
            _ => 3
        };
        AutomationIntervalJitterCheckBox.IsChecked = _automationSettings.EnableIntervalJitter;
        AutomationScheduleCheckBox.IsChecked = _automationSettings.EnableSchedule;
        AutomationStartTimeText.Text = _automationSettings.StartAt.ToString("HH:mm");
        AutomationEndTimeText.Text = _automationSettings.EndAt.ToString("HH:mm");
    }

    private void AutomationSettings_Changed(object sender, RoutedEventArgs e)
    {
        if (_automationSettingsReady)
        {
            SaveAutomationSettings();
        }
    }

    private void AutomationSettings_Changed(object sender, TextChangedEventArgs e)
    {
        if (_automationSettingsReady)
        {
            SaveAutomationSettings();
        }
    }

    private void AutomationTaskInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_automationSettingsReady)
        {
            return;
        }

        _automationTaskSaveTimer.Stop();
        _automationTaskSaveTimer.Start();
    }

    private void AutomationTaskInput_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { DataContext: AutomationTask task })
        {
            return;
        }

        AutomationTasksGrid.SelectedItem = task;
        AutomationTasksGrid.CurrentItem = task;
        AutomationTasksGrid.ScrollIntoView(task);
    }

    private void AutomationTasksGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || FindVisualAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (AutomationTasksGrid.SelectedItem is AutomationTask selected)
        {
            DeleteAutomationTask(selected);
            e.Handled = true;
        }
    }

    private void AutomationEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: AutomationTask task })
        {
            return;
        }

        // The enable box remains a direct action, but the same click also establishes
        // the row targeted by delete and priority movement commands.
        task.Enabled = ((CheckBox)sender).IsChecked == true;
        AutomationTasksGrid.SelectedItem = task;
        AutomationTasksGrid.CurrentItem = task;
        AutomationTasksGrid.ScrollIntoView(task);
        SaveAutomationSettings();
    }

    private async void StartAutomationButton_Click(object sender, RoutedEventArgs e)
    {
        CommitAutomationEdits();
        if (!ValidateAutomationTasks(out var validationMessage))
        {
            SetStatus(validationMessage, StatusKind.Error);
            return;
        }

        if (!TryReadAutomationSchedule(out var scheduleError))
        {
            SetStatus(scheduleError, StatusKind.Error);
            return;
        }

        if (AutomationArmCheckBox.IsChecked != true)
        {
            SetStatus("请先勾选左下角黄色区域内的“我确认允许对启用任务执行真实选退课”。", StatusKind.Error);
            HighlightAutomationArmConfirmation();
            return;
        }

        if (_sessionValidation is null && !await ValidateSessionAsync(automatic: true))
        {
            SetStatus("请先打开教务系统并完成登录。", StatusKind.Error);
            return;
        }

        if (TurnSelector.SelectedItem is not TurnInfo turn)
        {
            SetStatus("当前没有开放的选课轮次。", StatusKind.Error);
            return;
        }

        var enabledCount = _automationTasks.Count(item => item.Enabled);
        var confirmation = MessageBox.Show(
            this,
            $"即将启动路线二自动监测。检测到余量时，程序会按优先级对 {enabledCount} 个启用任务执行真实选退课。{Environment.NewLine}{Environment.NewLine}" +
            $"当前轮次：{turn.DisplayText}{Environment.NewLine}" +
            "所有写操作严格串行且不会重试；目标课失败时会尝试恢复本轮已退旧课。是否确认开始？",
            "确认启动真实任务监测",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        InvalidateControlledPrecheck("任务监测即将启动；单门操作预检查已失效。若之后需要单门操作，请重新预检查。");
        SaveAutomationSettings();
        _automationRunning = true;
        AutomationStateText.Text = "监测中";
        AddActivity(
            _automationSettings.EnableIntervalJitter
                ? $"已启动路线二串行任务监测；基准周期 {GetAutomationInterval()} 秒，启用 ±1/3 随机抖动。"
                : $"已启动路线二串行任务监测；固定周期 {GetAutomationInterval()} 秒。",
            "监测",
            ActivityEventKind.MonitoringStarted);
        UpdateAutomationControls();
        await RunAutomationCycleAsync(executeWhenAvailable: true);
        if (_automationRunning)
        {
            ScheduleNextAutomationTick();
        }
    }

    private void HighlightAutomationArmConfirmation()
    {
        AutomationArmPanel.BringIntoView();
        AutomationArmCheckBox.Focus();
        AutomationArmPanel.BorderBrush = (Brush)FindResource("WarningBrush");
        AutomationArmPanel.BorderThickness = new Thickness(2);

        if (_automationArmHighlightTimer is null)
        {
            _automationArmHighlightTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.5)
            };
            _automationArmHighlightTimer.Tick += (_, _) =>
            {
                _automationArmHighlightTimer.Stop();
                AutomationArmPanel.BorderBrush = (Brush)FindResource("AutomationArmBorderBrush");
                AutomationArmPanel.BorderThickness = new Thickness(1);
            };
        }

        _automationArmHighlightTimer.Stop();
        _automationArmHighlightTimer.Start();
    }

    private async void AutomationTimer_Tick(object? sender, EventArgs e)
    {
        // DispatcherTimer 改为逐轮单次安排，确保每个周期都重新抽取抖动值。
        _automationTimer.Stop();
        var plannedAt = _automationTickPlannedAt;
        var plannedDelay = _automationTickPlannedDelay;
        ClearAutomationTimerPlan();
        if (plannedAt is not null
            && AutomationTiming.IsSignificantlyLate(plannedAt.Value, DateTimeOffset.Now, plannedDelay))
        {
            _readNetworkRecoveryPending = false;
            HandleExternalSafetyPause(
                "监测计时器明显晚于计划时间触发，可能经历了休眠、Modern Standby 或系统长时间停顿；已在发送任何请求前暂停，请重新验证会话后手动启动。",
                resetSession: true);
            return;
        }

        if (_readNetworkRecoveryPending)
        {
            await AttemptReadNetworkRecoveryAsync();
            return;
        }

        await RunAutomationCycleAsync(executeWhenAvailable: true);
        if (_automationRunning && !_readNetworkRecoveryPending)
        {
            ScheduleNextAutomationTick();
        }
    }

    private void PauseAutomationButton_Click(object sender, RoutedEventArgs e)
    {
        PauseAutomation("已由用户暂停。", "信息");
    }

    private async void CheckAutomationNowButton_Click(object sender, RoutedEventArgs e)
    {
        CommitAutomationEdits();
        if (!ValidateAutomationTasks(out var validationMessage))
        {
            SetStatus(validationMessage, StatusKind.Error);
            return;
        }

        SaveAutomationSettings();
        AddActivity("用户开始执行一次任务只读检查。", "只读", ActivityEventKind.ReadOnlyCheck);
        await RunAutomationCycleAsync(executeWhenAvailable: false);
        AddActivity("任务只读检查已结束；未提交选课或退课。", "只读", ActivityEventKind.ReadOnlyCheck);
    }

    private void AutomationArmCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_automationSettingsReady && _automationRunning)
        {
            PauseAutomation("真实操作确认已取消，监测已暂停。", "信息");
        }
    }

    private async Task RunAutomationCycleAsync(bool executeWhenAvailable)
    {
        if (_automationCycleActive || _isBusy || (executeWhenAvailable && !_automationRunning))
        {
            return;
        }

        if (executeWhenAvailable && !IsInAutomationSchedule())
        {
            var waitMessage = $"当前不在运行时段（{_automationSettings.StartAt:HH:mm}–{_automationSettings.EndAt:HH:mm}），监测等待中。";
            AutomationStateText.Text = "等待运行时段";
            SetStatus(waitMessage, StatusKind.Warning);
            return;
        }

        if (_loginWindow is null || !_loginWindow.IsBrowserReady)
        {
            if (executeWhenAvailable)
            {
                PauseAutomation("教务系统窗口尚未打开，监测已暂停。", "暂停");
            }
            else
            {
                SetStatus("请先打开教务系统。", StatusKind.Error);
            }

            return;
        }

        if (_sessionValidation is null && !await ValidateSessionAsync(automatic: true))
        {
            if (executeWhenAvailable)
            {
                PauseAutomation("会话验证失败，监测已暂停。", "暂停");
            }

            return;
        }

        if (_sessionValidation is not { } session || TurnSelector.SelectedItem is not TurnInfo turn)
        {
            if (executeWhenAvailable)
            {
                PauseAutomation("没有可用选课轮次，监测已暂停。", "暂停");
            }
            else
            {
                SetStatus("当前没有可用选课轮次。", StatusKind.Error);
            }

            return;
        }

        _validationCancellation?.Cancel();
        _validationCancellation?.Dispose();
        _validationCancellation = new CancellationTokenSource();
        var cancellationToken = _validationCancellation.Token;
        _automationCycleActive = true;
        AutomationStateText.Text = executeWhenAvailable ? "监测中" : "只读检查中";
        SetBusy(true);
        SetStatus(executeWhenAvailable ? "正在执行串行任务监测…" : "正在执行任务只读检查…", StatusKind.Working);
        AddActivity(executeWhenAvailable ? "开始一轮串行任务监测。" : "开始一轮任务只读检查。", executeWhenAvailable ? "监测" : "只读");

        try
        {
            using var bridge = await _loginWindow.CreateSessionBridgeAsync(cancellationToken);
            var readClient = new ReadOnlyEamsClient(bridge);
            var mutationClient = new ControlledEamsClient(bridge);
            using var operationLease = executeWhenAvailable
                ? await _realOperations.EnterAsync("任务监测", readClient, mutationClient, cancellationToken)
                : null;
            var engine = new Route2AutomationEngine(
                readClient,
                operationLease,
                session.StudentId,
                turn.Id,
                (level, message, kind) => AddActivity(message, level, kind));
            var result = await engine.RunOnceAsync(
                _automationTasks,
                executeWhenAvailable,
                cancellationToken);
            AutomationTasksGrid.Items.Refresh();
            SaveAutomationSettings();

            if (result.ShouldPause)
            {
                PauseAutomation(result.Summary, "暂停");
                SetStatus(result.Summary, StatusKind.Error);
            }
            else if (executeWhenAvailable && !_automationTasks.Any(item => item.Enabled))
            {
                PauseAutomation("所有启用任务均已完成，监测已自动停止。", "成功");
                SetStatus("所有任务已完成。", StatusKind.Success);
            }
            else
            {
                AutomationStateText.Text = executeWhenAvailable ? "监测中" : "只读检查完成";
                SetStatus(result.Summary, StatusKind.Success);
            }
        }
        catch (AuthenticationRequiredException)
        {
            ResetSessionUi("认证会话已经失效，请重新登录。");
            PauseAutomation("认证会话失效，监测已暂停。请人工核对当前已选课程。", "暂停");
            SetStatus("认证会话失效，任务引擎已停止。", StatusKind.Error);
            RequestAuthenticationAfterSessionExpiry();
        }
        catch (Exception ex) when (NetworkFailureClassifier.IsTransientReadFailure(
                                       ex,
                                       cancellationToken.IsCancellationRequested)
                                   && !_realOperations.HasUnresolvedOperation)
        {
            BeginReadNetworkRecoveryWait("纯读取网络请求超时；尚未开始任何真实写入，监测等待网络恢复。");
        }
        catch (OperationCanceledException)
        {
            if (_readNetworkRecoveryPending && !_realOperations.HasUnresolvedOperation)
            {
                SetStatus("纯读取请求已停止；未发送真实写入，正在等待网络恢复。", StatusKind.Warning);
            }
            else
            {
                PauseAutomation("任务等待被取消；结果可能不确定，请人工核对后再启动。", "暂停");
                SetStatus("任务结果可能不确定，禁止立即重启监测。", StatusKind.Error);
            }
        }
        catch (Exception ex)
        {
            PauseAutomation("任务引擎遇到异常，已暂停。请人工核对当前已选课程。", "错误");
            SetStatus(ex.Message, StatusKind.Error);
            AddActivity(ex.Message, "错误");
        }
        finally
        {
            _automationCycleActive = false;
            AutomationTasksGrid.Items.Refresh();
            SaveAutomationSettings();
            RefreshInterruptedOperationBanner();
            SetBusy(false);
        }
    }

    private void PauseAutomation(string message, string level)
    {
        var wasMonitoring = _automationRunning || _readNetworkRecoveryPending;
        _automationTimer.Stop();
        ClearAutomationTimerPlan();
        _readNetworkRecoveryPending = false;
        _automationRunning = false;
        AutomationArmCheckBox.IsChecked = false;
        AutomationStateText.Text = level == "成功" ? "已完成" : "已暂停";
        AddActivity(message, level, wasMonitoring ? ActivityEventKind.MonitoringStopped : ActivityEventKind.Detail);
        // 暂停会改变全局可交互状态，必须同时刷新顶部、课程影子、单门操作和任务页。
        SetBusy(_isBusy);
    }

    private void AddAutomationTaskButton_Click(object sender, RoutedEventArgs e)
    {
        var task = new AutomationTask { Priority = _automationTasks.Count + 1 };
        _automationTasks.Add(task);
        SaveAutomationSettings();
        Dispatcher.BeginInvoke(() =>
        {
            AutomationTasksGrid.SelectedItem = task;
            AutomationTasksGrid.CurrentItem = task;
            AutomationTasksGrid.ScrollIntoView(task);
            AutomationTasksGrid.UpdateLayout();
            if (AutomationTasksGrid.ItemContainerGenerator.ContainerFromItem(task) is DataGridRow row
                && FindVisualDescendant<TextBox>(row) is { } input)
            {
                input.Focus();
                Keyboard.Focus(input);
                input.SelectAll();
            }
        }, DispatcherPriority.Input);
    }

    private void DeleteAutomationTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (AutomationTasksGrid.SelectedItem is AutomationTask selected)
        {
            DeleteAutomationTask(selected);
        }
    }

    private void DeleteAutomationTask(AutomationTask task)
    {
        _automationTaskSaveTimer.Stop();
        _automationTasks.Remove(task);
        RenumberAutomationPriorities();
        SaveAutomationSettings();
    }

    private void MoveAutomationTaskUpButton_Click(object sender, RoutedEventArgs e) => MoveAutomationTask(-1);

    private void MoveAutomationTaskDownButton_Click(object sender, RoutedEventArgs e) => MoveAutomationTask(1);

    private void MoveAutomationTask(int direction)
    {
        if (AutomationTasksGrid.SelectedItem is not AutomationTask selected)
        {
            return;
        }

        var index = _automationTasks.IndexOf(selected);
        var next = index + direction;
        if (next < 0 || next >= _automationTasks.Count)
        {
            return;
        }

        _automationTasks.Move(index, next);
        RenumberAutomationPriorities();
        SaveAutomationSettings();
    }

    private void RenumberAutomationPriorities()
    {
        for (var index = 0; index < _automationTasks.Count; index++)
        {
            _automationTasks[index].Priority = index + 1;
        }

        AutomationTasksGrid?.Items.Refresh();
    }

    private bool ValidateAutomationTasks(out string message)
    {
        var enabled = _automationTasks.Where(item => item.Enabled).ToArray();
        if (enabled.Length == 0)
        {
            message = "请至少添加并启用一个任务。";
            return false;
        }

        foreach (var task in enabled)
        {
            var code = task.CourseCode.Trim();
            var parsed = ParseTargetCodes(code);
            if (parsed.Count != 1 || !parsed[0].Equals(code, StringComparison.OrdinalIgnoreCase))
            {
                message = $"任务“{code}”不是一个完整、唯一的教学班代码。";
                return false;
            }

            if (task.ConflictingCourseCodes.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                message = $"任务 {code} 的冲突旧课不能包含目标课自身。";
                return false;
            }
        }

        var duplicate = enabled
            .GroupBy(item => item.CourseCode.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            message = $"教学班 {duplicate.Key} 存在重复启用任务。";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private void CommitAutomationEdits()
    {
        _automationTaskSaveTimer.Stop();
        AutomationTasksGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        AutomationTasksGrid.CommitEdit(DataGridEditingUnit.Row, true);
        SaveAutomationSettings();
    }

    private int GetAutomationInterval() => AutomationIntervalSelector.SelectedIndex switch
    {
        0 => 5,
        1 => 15,
        2 => 60,
        _ => int.TryParse(AutomationCustomIntervalText.Text, out var seconds)
            ? Math.Clamp(seconds, 5, 3600)
            : 15
    };

    private void ScheduleNextAutomationTick()
    {
        var baseSeconds = GetAutomationInterval();
        var delay = AutomationTiming.CalculateNextDelay(
            baseSeconds,
            _automationSettings.EnableIntervalJitter,
            Random.Shared.NextDouble());
        ScheduleAutomationTimer(delay);

        if (IsInAutomationSchedule())
        {
            AutomationStateText.Text = "监测中";
        }
    }

    private void ScheduleReadNetworkRecoveryTick()
    {
        var seconds = Math.Clamp(GetAutomationInterval(), 15, 60);
        ScheduleAutomationTimer(TimeSpan.FromSeconds(seconds));
    }

    private void ScheduleAutomationTimer(TimeSpan delay)
    {
        _automationTickPlannedDelay = delay;
        _automationTickPlannedAt = DateTimeOffset.Now + delay;
        _automationTimer.Interval = delay;
        _automationTimer.Start();
    }

    private void ClearAutomationTimerPlan()
    {
        _automationTickPlannedAt = null;
        _automationTickPlannedDelay = TimeSpan.Zero;
    }

    private bool TryReadAutomationSchedule(out string error)
    {
        if (!TimeOnly.TryParse(AutomationStartTimeText.Text, out var start)
            || !TimeOnly.TryParse(AutomationEndTimeText.Text, out var end))
        {
            error = "运行时段必须使用 HH:mm 格式。";
            return false;
        }

        _automationSettings.StartAt = start;
        _automationSettings.EndAt = end;
        error = string.Empty;
        return true;
    }

    private bool IsInAutomationSchedule()
    {
        if (!_automationSettings.EnableSchedule)
        {
            return true;
        }

        var time = TimeOnly.FromDateTime(DateTime.Now);
        return _automationSettings.StartAt <= _automationSettings.EndAt
            ? time >= _automationSettings.StartAt && time <= _automationSettings.EndAt
            : time >= _automationSettings.StartAt || time <= _automationSettings.EndAt;
    }

    private void SaveAutomationSettings()
    {
        if (!_automationSettingsReady)
        {
            return;
        }

        _automationSettings.IntervalSeconds = GetAutomationInterval();
        _automationSettings.EnableIntervalJitter = AutomationIntervalJitterCheckBox.IsChecked == true;
        _automationSettings.EnableSchedule = AutomationScheduleCheckBox.IsChecked == true;
        _automationSettings.RunAtWindowsStartup = RunAtStartupCheckBox.IsChecked == true;
        if (TimeOnly.TryParse(AutomationStartTimeText.Text, out var start))
        {
            _automationSettings.StartAt = start;
        }

        if (TimeOnly.TryParse(AutomationEndTimeText.Text, out var end))
        {
            _automationSettings.EndAt = end;
        }

        _automationSettings.Tasks = _automationTasks.OrderBy(item => item.Priority).ToList();
        try
        {
            _automationStore.Save(_automationSettings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddActivity("任务配置保存失败；磁盘上的上一个完整版本仍保留。请检查磁盘空间或文件权限。", "配置错误");
            SetStatus("任务配置暂未保存；请检查磁盘空间或文件权限。", StatusKind.Error);
        }
    }

    private void RunAtStartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_startupOptionReady)
        {
            return;
        }

        var enabled = RunAtStartupCheckBox.IsChecked == true;
        try
        {
            StartupRegistrationService.SetEnabled(enabled);
            _automationSettings.RunAtWindowsStartup = enabled;
            SaveAutomationSettings();
            var message = enabled
                ? "已启用开机自动启动程序；启动后仍保持未监测状态。"
                : "已关闭开机自动启动程序。";
            AddActivity(message, "设置");
            SetStatus(message, StatusKind.Success);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            _startupOptionReady = false;
            RunAtStartupCheckBox.IsChecked = !enabled;
            _startupOptionReady = true;
            SetStatus("无法更新开机启动设置：" + ex.Message, StatusKind.Error);
            AddActivity("更新开机启动设置失败。", "错误");
        }
    }

    private async void InspectInterruptedOperationButton_Click(object sender, RoutedEventArgs e)
    {
        var journal = _realOperations.ActiveJournal;
        if (journal is null)
        {
            RefreshInterruptedOperationBanner();
            return;
        }

        if (_loginWindow is null || !_loginWindow.IsBrowserReady)
        {
            SetStatus("请先打开教务系统并完成登录，再读取中断操作实际状态。", StatusKind.Error);
            return;
        }

        if (_sessionValidation is null && !await ValidateSessionAsync(automatic: true))
        {
            return;
        }

        if (_sessionValidation is not { } session)
        {
            return;
        }

        if (journal.StudentId > 0 && journal.StudentId != session.StudentId)
        {
            InterruptedOperationText.Text =
                $"中断操作属于内部学生编号 {journal.StudentId}，当前登录编号为 {session.StudentId}。请切换到正确账户，保护未解除。";
            AcknowledgeInterruptedOperationButton.IsEnabled = false;
            SetStatus("当前登录账户与中断操作不一致。", StatusKind.Error);
            return;
        }

        var turnId = journal.TurnId > 0
            ? journal.TurnId
            : (TurnSelector.SelectedItem as TurnInfo)?.Id ?? 0;
        if (turnId <= 0)
        {
            SetStatus("无法确定中断操作的选课轮次。", StatusKind.Error);
            return;
        }

        _validationCancellation?.Cancel();
        _validationCancellation?.Dispose();
        _validationCancellation = new CancellationTokenSource();
        var cancellationToken = _validationCancellation.Token;
        SetBusy(true);
        SetStatus("正在只读核对中断操作涉及课程的实际状态…", StatusKind.Working);

        try
        {
            using var bridge = await _loginWindow.CreateSessionBridgeAsync(cancellationToken);
            var readClient = new ReadOnlyEamsClient(bridge);
            var codes = journal.RelatedCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var snapshot = await readClient.GetShadowSnapshotAsync(
                session.StudentId,
                turnId,
                codes,
                cancellationToken);
            var lines = new List<string>
            {
                $"检测到上次真实操作未正常结束：{journal.Owner}",
                $"记录阶段：{journal.Stage}",
                $"最后动作：{journal.CurrentAction} {journal.CurrentCourseCode}".TrimEnd(),
                $"记录时间：{journal.UpdatedAt:yyyy-MM-dd HH:mm:ss}",
                "以下内容来自本次只读查询："
            };

            if (codes.Length == 0)
            {
                lines.Add("事务记录未保留可识别课程代码；当前全部已选课程：");
                lines.AddRange(snapshot.SelectedLessons.Select(item => $"• {item.Code} {item.CourseName}"));
            }
            else
            {
                foreach (var code in codes)
                {
                    var selected = snapshot.SelectedLessons.FirstOrDefault(
                        item => item.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
                    var known = snapshot.TargetLessons.FirstOrDefault(
                        item => item.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
                    lines.Add(selected is not null
                        ? $"• {code}：已选（{selected.CourseName}）"
                        : known is not null
                            ? $"• {code}：未选（{known.CourseName}）"
                            : $"• {code}：当前轮次未找到，需在教务网页继续人工核对");
                }
            }

            lines.Add("程序不会自动继续或重复上次操作。请在教务网页处理完毕后，再点击右侧解除保护。");
            InterruptedOperationText.Text = string.Join(Environment.NewLine, lines);
            _interruptedInspectionCompleted = true;
            AcknowledgeInterruptedOperationButton.IsEnabled = true;
            SetStatus("中断操作实际状态已读取；等待你人工处理并确认。", StatusKind.Warning);
            AddActivity("已只读核对中断操作涉及课程；等待用户人工处理。", "安全");
        }
        catch (AuthenticationRequiredException)
        {
            ResetSessionUi("认证会话失效，请重新登录后继续核对中断操作。");
            SetStatus("认证会话失效，中断保护保持锁定。", StatusKind.Error);
            RequestAuthenticationAfterSessionExpiry();
        }
        catch (Exception ex)
        {
            SetStatus("读取中断操作实际状态失败：" + ex.Message, StatusKind.Error);
            AddActivity("读取中断操作实际状态失败；保护保持锁定。", "错误");
        }
        finally
        {
            SetBusy(false);
            RefreshInterruptedOperationBanner();
        }
    }

    private void AcknowledgeInterruptedOperationButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_interruptedInspectionCompleted || !_realOperations.HasUnresolvedOperation)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            "请确认：你已经根据刚才读取的状态，在教务系统网页中完成必要核对或处理。解除后程序不会补做上次操作，但允许新的真实操作。是否解除保护？",
            "确认解除中断操作保护",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _realOperations.AcknowledgeResolved("用户完成只读核对并明确解除保护。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("无法保存解除结果；保护仍保持锁定。请检查磁盘空间或文件权限。", StatusKind.Error);
            AddActivity("中断操作保护解除记录保存失败；保护保持锁定。", "配置错误");
            return;
        }
        _interruptedInspectionCompleted = false;
        AddActivity("用户已人工核对中断操作并解除真实操作保护。", "安全");
        SetStatus("中断操作保护已解除；新的真实操作仍需正常确认。", StatusKind.Success);
        RefreshInterruptedOperationBanner();
        SetBusy(false);
    }

    private void RefreshInterruptedOperationBanner()
    {
        var journal = _realOperations.ActiveJournal;
        InterruptedOperationBanner.Visibility = journal is null ? Visibility.Collapsed : Visibility.Visible;
        if (journal is null)
        {
            _interruptedInspectionCompleted = false;
            AcknowledgeInterruptedOperationButton.IsEnabled = false;
            return;
        }

        if (!_interruptedInspectionCompleted)
        {
            InterruptedOperationText.Text =
                $"检测到尚未核对的真实操作：{journal.Owner}；阶段：{journal.Stage}；最后动作：{journal.CurrentAction} {journal.CurrentCourseCode}。" +
                " 所有新的真实选退课已锁定，请登录后先读取实际状态。";
        }

        InspectInterruptedOperationButton.IsEnabled = !_isBusy && _loginWindow?.IsBrowserReady == true;
        AcknowledgeInterruptedOperationButton.IsEnabled = !_isBusy && _interruptedInspectionCompleted;
    }

    private void LoadLocalActivityHistory()
    {
        try
        {
            _activityEntries.Clear();
            _keyActivityEntries.Clear();
            ResetActivityDocuments();
            foreach (var entry in _localActivityLog.LoadRecent())
            {
                _activityEntries.Add(entry);
                AppendActivityLine(_allActivityParagraph, entry);
                if (entry.IsKeyEvent)
                {
                    _keyActivityEntries.Add(entry);
                    AppendActivityLine(_keyActivityParagraph, entry);
                }
            }

            RenderActivityLog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _activityEntries.Clear();
            _keyActivityEntries.Clear();
            ResetActivityDocuments();
            var entry = new LocalLogEntry(DateTimeOffset.Now, "警告", "无法读取本地活动日志。")
            {
                Kind = ActivityEventKind.Critical
            };
            _activityEntries.Add(entry);
            _keyActivityEntries.Add(entry);
            AppendActivityLine(_allActivityParagraph, entry);
            AppendActivityLine(_keyActivityParagraph, entry);
            RenderActivityLog();
        }
    }

    private void ImportantActivityButton_Click(object sender, RoutedEventArgs e)
    {
        _showAllActivity = false;
        RenderActivityLog();
    }

    private void AllActivityButton_Click(object sender, RoutedEventArgs e)
    {
        _showAllActivity = true;
        RenderActivityLog();
    }

    private void RenderActivityLog()
    {
        var entries = _showAllActivity
            ? _activityEntries
            : _keyActivityEntries;
        var document = _showAllActivity ? _allActivityDocument : _keyActivityDocument;
        if (!ReferenceEquals(ActivityLogViewer.Document, document))
        {
            ActivityLogViewer.Document = document;
        }

        ActivityEmptyText.Text = _showAllActivity ? "暂时没有活动记录。" : "暂时没有关键记录。";
        ActivityEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ImportantActivityButton.Background = _showAllActivity
            ? Brushes.White
            : (Brush)FindResource("AccentBrush");
        ImportantActivityButton.Foreground = _showAllActivity
            ? (Brush)FindResource("TextPrimaryBrush")
            : Brushes.White;
        AllActivityButton.Background = _showAllActivity
            ? (Brush)FindResource("AccentBrush")
            : Brushes.White;
        AllActivityButton.Foreground = _showAllActivity
            ? Brushes.White
            : (Brush)FindResource("TextPrimaryBrush");
        if (entries.Count > 0)
        {
            ScheduleActivityScrollToEnd(document);
        }
    }

    private void ScheduleActivityScrollToEnd(FlowDocument expectedDocument)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(ActivityLogViewer.Document, expectedDocument))
            {
                FindVisualDescendant<ScrollViewer>(ActivityLogViewer)?.ScrollToEnd();
            }
        }, DispatcherPriority.Background);
    }

    private void ResetActivityDocuments()
    {
        _allActivityParagraph.Inlines.Clear();
        _keyActivityParagraph.Inlines.Clear();
    }

    private void AppendActivityLine(Paragraph paragraph, LocalLogEntry entry)
    {
        var foreground = entry.Level switch
        {
            "错误" or "严重" or "安全错误" => (Brush)FindResource("DangerBrush"),
            "警告" or "暂停" or "安全暂停" => (Brush)FindResource("WarningBrush"),
            "成功" => (Brush)FindResource("SuccessBrush"),
            "受控写入" or "恢复" => (Brush)FindResource("AccentBrush"),
            _ => (Brush)FindResource("TextPrimaryBrush")
        };
        paragraph.Inlines.Add(new Run(entry.DisplayText)
        {
            Foreground = foreground,
            FontWeight = entry.IsEmphasized ? FontWeights.Bold : FontWeights.Normal
        });
        paragraph.Inlines.Add(new LineBreak());
    }

    private void SubscribeSystemSafetyEvents()
    {
        try
        {
            NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
            SystemEvents.PowerModeChanged += SystemPowerModeChanged;
            _systemEventsSubscribed = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.SecurityException)
        {
            AddActivity("无法订阅系统网络或电源事件；任务引擎仍会在请求异常时安全暂停。", "警告");
        }
    }

    private void BeginReadNetworkRecoveryWait(string message)
    {
        if (!_automationRunning || _realOperations.HasUnresolvedOperation)
        {
            return;
        }

        if (!_readNetworkRecoveryPending)
        {
            if (_sessionValidation is not { } session || TurnSelector.SelectedItem is not TurnInfo turn)
            {
                PauseAutomation("网络异常时无法保留已验证的账号和轮次；监测已暂停，请人工重新验证。", "暂停");
                SetStatus("无法安全进入网络恢复等待，必须人工重新验证。", StatusKind.Error);
                return;
            }

            _readRecoveryStudentId = session.StudentId;
            _readRecoveryTurnId = turn.Id;
            _readNetworkRecoveryPending = true;
            AddActivity(message, "网络等待");
        }

        _automationTimer.Stop();
        ClearAutomationTimerPlan();
        _validationCancellation?.Cancel();
        ResetSessionUi("纯读取网络请求已停止；等待网络恢复后执行一次只读会话验证。");
        AutomationStateText.Text = "等待网络恢复";
        SetStatus("网络暂不可用；尚未开始真实写入，监测正在等待恢复。", StatusKind.Warning);
        ScheduleReadNetworkRecoveryTick();
        SetBusy(_isBusy);
    }

    private async Task AttemptReadNetworkRecoveryAsync()
    {
        if (!_readNetworkRecoveryPending || _readNetworkRecoveryAttemptActive || !_automationRunning)
        {
            return;
        }

        if (_isBusy)
        {
            ScheduleReadNetworkRecoveryTick();
            return;
        }

        if (_loginWindow is null || !_loginWindow.IsBrowserReady)
        {
            _readNetworkRecoveryPending = false;
            PauseAutomation("网络恢复后无法访问教务系统窗口；监测保持暂停，请人工重新验证。", "暂停");
            SetStatus("无法执行网络恢复只读验证。", StatusKind.Error);
            return;
        }

        _readNetworkRecoveryAttemptActive = true;
        _validationCancellation?.Cancel();
        _validationCancellation?.Dispose();
        _validationCancellation = new CancellationTokenSource();
        var cancellationToken = _validationCancellation.Token;
        var recovered = false;
        var retryLater = false;
        SetBusy(true);
        SetStatus("网络可能已恢复，正在执行一次只读会话验证…", StatusKind.Working);
        AddActivity("网络恢复探测：开始一次只读会话验证；不会重放中断请求。", "只读");

        try
        {
            using var bridge = await _loginWindow.CreateSessionBridgeAsync(cancellationToken);
            var client = new ReadOnlyEamsClient(bridge);
            var result = await client.ValidateAsync(cancellationToken);
            if (result.StudentId != _readRecoveryStudentId
                || result.OpenTurns.All(turn => turn.Id != _readRecoveryTurnId))
            {
                _readNetworkRecoveryPending = false;
                PauseAutomation("网络恢复后的账号或选课轮次与中断前不一致；禁止自动继续。", "暂停");
                SetStatus("账号或轮次不一致，必须人工核对。", StatusKind.Error);
                return;
            }

            ApplyRecoveredSession(result, _readRecoveryTurnId);
            _readNetworkRecoveryPending = false;
            _networkWasUnavailable = false;
            AutomationStateText.Text = "监测中";
            SetStatus("网络恢复只读验证成功；将从下一轮继续监测，不重放中断请求。", StatusKind.Success);
            AddActivity("网络恢复只读验证成功；从下一轮继续监测，未重放任何中断请求。", "网络恢复");
            recovered = true;
        }
        catch (AuthenticationRequiredException)
        {
            _readNetworkRecoveryPending = false;
            ResetSessionUi("网络恢复后发现认证会话失效，请重新登录。");
            PauseAutomation("网络恢复后认证会话失效；监测保持暂停，不会自动恢复。", "暂停");
            SetStatus("认证会话失效，请人工重新登录。", StatusKind.Error);
            RequestAuthenticationAfterSessionExpiry();
        }
        catch (Exception ex) when (NetworkFailureClassifier.IsTransientReadFailure(
                                       ex,
                                       cancellationToken.IsCancellationRequested))
        {
            retryLater = true;
            AutomationStateText.Text = "等待网络恢复";
            SetStatus("网络仍不稳定；只读验证未完成，将继续等待。", StatusKind.Warning);
            AddActivity("网络恢复只读验证仍遇到超时；未发送真实写入，继续等待。", "网络等待");
        }
        catch (OperationCanceledException)
        {
            retryLater = _readNetworkRecoveryPending;
            if (retryLater)
            {
                SetStatus("网络恢复只读验证被网络变化中断；继续等待。", StatusKind.Warning);
            }
        }
        catch (Exception)
        {
            _readNetworkRecoveryPending = false;
            PauseAutomation("网络恢复只读验证遇到接口异常；监测保持暂停，请人工核对。", "暂停");
            SetStatus("只读验证失败，禁止自动继续。", StatusKind.Error);
        }
        finally
        {
            _readNetworkRecoveryAttemptActive = false;
            SetBusy(false);
            if (recovered && _automationRunning)
            {
                ScheduleNextAutomationTick();
            }
            else if (retryLater && _automationRunning && _readNetworkRecoveryPending)
            {
                ScheduleReadNetworkRecoveryTick();
            }
        }
    }

    private void ApplyRecoveredSession(SessionValidationResult result, long selectedTurnId)
    {
        _sessionValidation = result;
        _turns.Clear();
        foreach (var turn in result.OpenTurns)
        {
            _turns.Add(turn);
        }

        StudentIdText.Text = result.StudentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TurnSelector.SelectedItem = _turns.First(turn => turn.Id == selectedTurnId);
        NoTurnsText.Text = string.Empty;
        SnapshotSummaryText.Text = "网络恢复验证成功；等待下一轮监测。";
    }

    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            if (!e.IsAvailable)
            {
                _networkWasUnavailable = true;
                if (_automationRunning && !_realOperations.HasUnresolvedOperation)
                {
                    BeginReadNetworkRecoveryWait("检测到纯读取阶段网络断开；尚未开始真实写入，监测等待恢复。");
                }
                else
                {
                    HandleExternalSafetyPause("检测到网络断开；真实操作状态可能不确定，监测已暂停。", resetSession: true);
                }
            }
            else if (_readNetworkRecoveryPending)
            {
                _automationTimer.Stop();
                ClearAutomationTimerPlan();
                await AttemptReadNetworkRecoveryAsync();
            }
            else if (_networkWasUnavailable && _realOperations.HasEligibleInterruptedSwapRecovery)
            {
                _networkWasUnavailable = false;
                await AttemptInterruptedSwapRecoveryAfterNetworkAsync();
            }
            else if (_networkWasUnavailable)
            {
                _networkWasUnavailable = false;
                HandleExternalSafetyPause("网络已恢复；程序保持暂停，必须重新验证会话后手动启动。", resetSession: true);
            }
        });
    }

    private void SystemPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (e.Mode == PowerModes.Suspend)
            {
                HandleExternalSafetyPause("电脑即将休眠；监测已暂停。若真实操作正在进行，下次必须先核对实际状态。", resetSession: true);
            }
            else if (e.Mode == PowerModes.Resume)
            {
                HandleExternalSafetyPause("电脑已从休眠恢复；程序不会自动恢复监测，请重新验证会话。", resetSession: true);
            }
        });
    }

    private async Task AttemptInterruptedSwapRecoveryAfterNetworkAsync()
    {
        var journal = _realOperations.ActiveJournal;
        if (journal is null || !_realOperations.HasEligibleInterruptedSwapRecovery)
        {
            return;
        }

        if (_isBusy || _loginWindow is null || !_loginWindow.IsBrowserReady)
        {
            _realOperations.ProhibitAutomaticRecovery(
                "网络恢复时无法执行可靠的只读状态核对；禁止自动恢复，必须人工确认。");
            SetStatus("无法安全核对中断换课，自动恢复已禁止。", StatusKind.Error);
            RefreshInterruptedOperationBanner();
            return;
        }

        _validationCancellation?.Cancel();
        _validationCancellation?.Dispose();
        _validationCancellation = new CancellationTokenSource();
        var cancellationToken = _validationCancellation.Token;
        SetBusy(true);
        SetStatus("网络已恢复，正在严格只读核对中断换课状态…", StatusKind.Working);
        AddActivity("网络恢复后开始核对中断换课；目标课请求记录为从未发送。", "安全恢复");

        try
        {
            using var bridge = await _loginWindow.CreateSessionBridgeAsync(cancellationToken);
            var readClient = new ReadOnlyEamsClient(bridge);
            var validation = await readClient.ValidateAsync(cancellationToken);
            if (validation.StudentId != journal.StudentId
                || validation.OpenTurns.All(turn => turn.Id != journal.TurnId))
            {
                _realOperations.ProhibitAutomaticRecovery(
                    "网络恢复后的账号或选课轮次与中断记录不一致；禁止自动恢复。");
                SetStatus("账号或轮次不一致，旧课不会自动恢复。", StatusKind.Error);
                AddActivity("中断换课的账号或轮次不一致；已禁止自动恢复。", "安全");
                return;
            }

            ApplyRecoveredSession(validation, journal.TurnId);
            var mutationClient = new ControlledEamsClient(bridge);
            var result = await _realOperations.TryRestoreConfirmedDropsAfterNetworkAsync(
                validation.StudentId,
                journal.TurnId,
                readClient,
                mutationClient,
                cancellationToken);
            switch (result.Outcome)
            {
                case InterruptedRestoreOutcome.Restored:
                    SetStatus("旧课已执行一次串行恢复并完成只读复核；程序保持暂停，请人工确认。", StatusKind.Warning);
                    AddActivity("符合严格条件的旧课恢复已执行并复核；保持暂停，等待人工确认。", "安全恢复");
                    break;
                case InterruptedRestoreOutcome.Incomplete:
                    SetStatus("旧课恢复不完整；程序保持暂停，请立即人工处理。", StatusKind.Error);
                    AddActivity("一次旧课恢复不完整；禁止重试，等待人工处理。", "严重");
                    break;
                case InterruptedRestoreOutcome.ResultUnknown:
                    SetStatus("旧课恢复写入或复核结果不确定；禁止重试，必须人工核对。", StatusKind.Error);
                    AddActivity("旧课恢复结果不确定；已禁止自动重试。", "严重");
                    break;
                case InterruptedRestoreOutcome.AuthenticationRequired:
                    ResetSessionUi("恢复核对时认证失效，请重新登录后人工处理。");
                    SetStatus("认证失效，自动恢复已禁止。", StatusKind.Error);
                    RequestAuthenticationAfterSessionExpiry();
                    break;
                default:
                    SetStatus(result.Message, StatusKind.Error);
                    AddActivity("严格恢复条件不成立；未发送恢复请求，保持人工核对。", "安全");
                    break;
            }
        }
        catch (AuthenticationRequiredException)
        {
            _realOperations.ProhibitAutomaticRecovery(
                "网络恢复后的会话验证发现认证失效；禁止自动恢复，必须人工确认。");
            ResetSessionUi("认证失效，请重新登录后人工核对中断换课。");
            SetStatus("认证失效，旧课不会自动恢复。", StatusKind.Error);
            RequestAuthenticationAfterSessionExpiry();
        }
        catch (Exception)
        {
            _realOperations.ProhibitAutomaticRecovery(
                "网络恢复后的只读状态读取失败；禁止自动恢复或重试，必须人工确认。");
            SetStatus("中断换课状态读取失败；自动恢复已禁止。", StatusKind.Error);
            AddActivity("中断换课只读核对失败；未发送恢复请求，保持锁定。", "错误");
        }
        finally
        {
            SetBusy(false);
            RefreshInterruptedOperationBanner();
        }
    }

    private void HandleExternalSafetyPause(string message, bool resetSession)
    {
        _automationTimer.Stop();
        ClearAutomationTimerPlan();
        _readNetworkRecoveryPending = false;
        _automationRunning = false;
        AutomationArmCheckBox.IsChecked = false;
        _validationCancellation?.Cancel();
        _realOperations.MarkExternalInterruption(message);
        if (resetSession)
        {
            ResetSessionUi(message);
        }

        AutomationStateText.Text = "已暂停";
        SetStatus(message, StatusKind.Warning);
        AddActivity(message, "安全暂停");
        RefreshInterruptedOperationBanner();
        SetBusy(_isBusy);
    }

    private void ControlledInput_Changed(object sender, RoutedEventArgs e)
    {
        if (!_controlledUiReady)
        {
            return;
        }

        InvalidateControlledPrecheck("操作类型或教学班代码已变化，请重新执行只读预检查。");
    }

    private void ControlledConfirmCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_controlledUiReady)
        {
            UpdateControlledExecuteState();
        }
    }

    private async void ControlledPrecheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (_loginWindow is null || !_loginWindow.IsBrowserReady)
        {
            SetStatus("请先打开教务系统。", StatusKind.Neutral);
            return;
        }

        if (_sessionValidation is null && !await ValidateSessionAsync(automatic: true))
        {
            SetStatus("尚未识别到有效会话，请在教务系统窗口完成登录。", StatusKind.Error);
            return;
        }

        if (TurnSelector.SelectedItem is not TurnInfo turn || _sessionValidation is null)
        {
            SetStatus("当前没有可用于受控操作的开放轮次。", StatusKind.Neutral);
            return;
        }

        var codes = ParseTargetCodes(ControlledLessonCodeText.Text);
        if (codes.Count != 1 || !codes[0].Equals(ControlledLessonCodeText.Text.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            ControlledDetailsText.Text = "请输入且只输入一个完整教学班代码。";
            return;
        }

        var code = codes[0];
        var mutationKind = SelectedMutationKind;
        var session = _sessionValidation;
        _validationCancellation?.Cancel();
        _validationCancellation?.Dispose();
        _validationCancellation = new CancellationTokenSource();
        var cancellationToken = _validationCancellation.Token;

        SetBusy(true);
        InvalidateControlledPrecheck("正在执行只读预检查…");
        ControlledResultText.Text = "尚未执行真实操作。";
        AddActivity($"开始单门受控操作预检查：{code}。", "只读");

        try
        {
            using var bridge = await _loginWindow.CreateSessionBridgeAsync(cancellationToken);
            var readClient = new ReadOnlyEamsClient(bridge);
            var snapshot = await readClient.GetShadowSnapshotAsync(
                session.StudentId,
                turn.Id,
                [code],
                cancellationToken);
            var matches = snapshot.TargetLessons
                .Where(lesson => lesson.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1)
            {
                ControlledDetailsText.Text = matches.Length == 0
                    ? "没有在当前轮次的已选或可选课程中找到该教学班。"
                    : "找到多个同代码教学班，无法安全确定唯一目标，已停止。";
                AddActivity("单门预检查未能确定唯一教学班。", "暂停");
                return;
            }

            var lesson = matches[0];
            var currentlySelected = snapshot.SelectedLessons.Any(item => item.Id == lesson.Id);
            var actionAllowed = mutationKind == CourseMutationKind.Add
                ? !currentlySelected
                : currentlySelected;
            if (TurnSelector.SelectedItem is not TurnInfo currentTurn
                || currentTurn.Id != turn.Id
                || SelectedMutationKind != mutationKind
                || !ControlledLessonCodeText.Text.Trim().Equals(code, StringComparison.OrdinalIgnoreCase))
            {
                ControlledDetailsText.Text = "预检查期间输入或轮次发生变化；结果已丢弃，请重新预检查。";
                AddActivity("单门预检查结果因输入变化而丢弃。", "信息");
                return;
            }

            _controlledLesson = actionAllowed ? lesson : null;
            _precheckedMutationKind = mutationKind;
            ControlledDetailsText.Text =
                $"教学班：{lesson.Code}{Environment.NewLine}" +
                $"课程：{lesson.CourseName}{Environment.NewLine}" +
                $"教师：{lesson.Teachers}{Environment.NewLine}" +
                $"当前状态：{(currentlySelected ? "已选" : "未选")}{Environment.NewLine}" +
                $"人数/上限：{lesson.CapacityText}{Environment.NewLine}" +
                $"计划操作：{MutationDisplayName(mutationKind)}" +
                (actionAllowed
                    ? string.Empty
                    : Environment.NewLine + (mutationKind == CourseMutationKind.Add
                        ? "该教学班已经选中，禁止重复选课。"
                        : "该教学班当前未选中，禁止提交退课。"));
            ControlledConfirmCheckBox.IsChecked = false;
            UpdateControlledExecuteState();
            AddActivity(
                actionAllowed ? "单门受控操作预检查通过。" : "预检查发现当前状态不允许所选操作。",
                actionAllowed ? "成功" : "暂停");
        }
        catch (AuthenticationRequiredException)
        {
            ResetSessionUi("认证会话已经失效，请重新登录。");
            SetStatus("认证会话无效，预检查已停止。", StatusKind.Error);
            AddActivity("受控操作预检查时发现认证失效。", "暂停");
            RequestAuthenticationAfterSessionExpiry();
        }
        catch (OperationCanceledException)
        {
            ControlledDetailsText.Text = "只读预检查已取消。";
        }
        catch (Exception ex)
        {
            ControlledDetailsText.Text = ex.Message;
            SetStatus(ex.Message, StatusKind.Error);
            AddActivity(ex.Message, "错误");
        }
        finally
        {
            SetBusy(false);
            UpdateControlledExecuteState();
        }
    }

    private async void ExecuteControlledButton_Click(object sender, RoutedEventArgs e)
    {
        var lesson = _controlledLesson;
        var session = _sessionValidation;
        if (lesson is null
            || session is null
            || TurnSelector.SelectedItem is not TurnInfo turn
            || ControlledConfirmCheckBox.IsChecked != true
            || SelectedMutationKind != _precheckedMutationKind
            || !ControlledLessonCodeText.Text.Trim().Equals(lesson.Code, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("预检查或明确确认已经失效，请重新预检查。", StatusKind.Error);
            InvalidateControlledPrecheck("预检查状态无效，请重新执行只读预检查。");
            return;
        }

        var actionName = MutationDisplayName(_precheckedMutationKind);
        var confirmation = MessageBox.Show(
            this,
            $"即将对真实教务系统执行一次{actionName}：{Environment.NewLine}{lesson.Code}  {lesson.CourseName}{Environment.NewLine}{Environment.NewLine}是否确认继续？",
            "确认单次真实操作",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            AddActivity("用户取消了单门真实操作。", "信息");
            return;
        }

        var mutationKind = _precheckedMutationKind;
        _validationCancellation?.Cancel();
        _validationCancellation?.Dispose();
        _validationCancellation = new CancellationTokenSource();
        var cancellationToken = _validationCancellation.Token;

        SetBusy(true);
        ControlledConfirmCheckBox.IsChecked = false;
        ControlledResultText.Text = $"正在提交一次{actionName}并等待服务器结果…";
        var operationEventKind = mutationKind == CourseMutationKind.Add
            ? ActivityEventKind.CourseAdd
            : ActivityEventKind.CourseDrop;
        AddActivity($"提交单门真实{actionName}：{lesson.Code}。", "受控写入", operationEventKind);

        try
        {
            if (_loginWindow is null)
            {
                throw new InvalidOperationException("教务系统窗口尚未打开。");
            }

            using var bridge = await _loginWindow.CreateSessionBridgeAsync(cancellationToken);
            var readClient = new ReadOnlyEamsClient(bridge);
            var mutationClient = new ControlledEamsClient(bridge);
            using var operationLease = await _realOperations.EnterAsync(
                "单门受控操作",
                readClient,
                mutationClient,
                cancellationToken);
            operationLease.BeginWorkflow(
                "单门受控操作",
                session.StudentId,
                turn.Id,
                lesson.Code,
                []);
            var verified = await operationLease.ExecuteAndVerifyAsync(
                mutationKind,
                lesson,
                $"单门{actionName} {lesson.Code}",
                cancellationToken);
            if (verified.ExpectedStateReached)
            {
                operationLease.CompleteWorkflow("单门操作最终状态已读取且与计划一致，预检查已消费。");
            }
            else
            {
                operationLease.RequireManualReview("单门操作最终状态与计划不一致；不得重复提交，必须先读取实际状态并人工核对。");
            }

            ControlledResultText.Text =
                $"服务器与复核信息：{verified.Message}{Environment.NewLine}" +
                $"最终已选状态：{(verified.FinallySelected ? "已选" : "未选")}{Environment.NewLine}" +
                $"最终复核：{(verified.ExpectedStateReached ? "与计划操作一致" : "与计划操作不一致，预检查已消费")}";

            if (verified.ExpectedStateReached)
            {
                SetStatus($"单门真实{actionName}已完成并通过最终状态复核。", StatusKind.Success);
                AddActivity($"{lesson.Code}：单门真实{actionName}完成；最终状态复核一致。", "成功", operationEventKind);
            }
            else
            {
                SetStatus("服务器结果与最终已选状态不一致，禁止立即重复提交。", StatusKind.Error);
                AddActivity($"{lesson.Code}：受控{actionName}最终状态与计划不一致；已锁定重复提交。", "错误", operationEventKind);
            }

            _controlledLesson = null;
            ControlledDetailsText.Text += Environment.NewLine + "本次预检查已消费；如需再次操作，必须重新预检查。";
        }
        catch (AuthenticationRequiredException)
        {
            ResetSessionUi("认证会话已经失效，请重新登录。");
            ControlledResultText.Text = "认证会话失效，真实操作已停止。请先在教务系统窗口重新认证，再读取最终状态。";
            SetStatus("认证会话失效。", StatusKind.Error);
            AddActivity($"{lesson.Code}：真实{actionName}期间认证失效；已停止。", "暂停", operationEventKind);
            RequestAuthenticationAfterSessionExpiry();
        }
        catch (OperationCanceledException)
        {
            ControlledResultText.Text = "操作等待已取消。结果可能不确定；请先重新预检查最终状态，禁止立即重复提交。";
            AddActivity($"{lesson.Code}：真实{actionName}等待被取消；结果不确定。", "暂停", operationEventKind);
        }
        catch (Exception ex)
        {
            ControlledResultText.Text = ex.Message + Environment.NewLine + "结果可能不确定；请先重新预检查，禁止立即重复提交。";
            SetStatus(ex.Message, StatusKind.Error);
            AddActivity($"{lesson.Code}：真实{actionName}异常；{ex.Message}", "错误", operationEventKind);
        }
        finally
        {
            _controlledLesson = null;
            RefreshInterruptedOperationBanner();
            SetBusy(false);
            UpdateControlledExecuteState();
        }
    }

    private CourseMutationKind SelectedMutationKind =>
        ControlledActionSelector.SelectedIndex == 1 ? CourseMutationKind.Drop : CourseMutationKind.Add;

    private static string MutationDisplayName(CourseMutationKind kind) =>
        kind == CourseMutationKind.Add ? "选课" : "退课";

    private void InvalidateControlledPrecheck(string message)
    {
        _controlledLesson = null;
        ControlledConfirmCheckBox.IsChecked = false;
        ControlledDetailsText.Text = message;
        UpdateControlledExecuteState();
    }

    private void UpdateControlledExecuteState()
    {
        ExecuteControlledButton.IsEnabled = !_isBusy
                                            && !_automationRunning
                                            && !_realOperations.HasUnresolvedOperation
                                            && !_realOperations.IsOperationInProgress
                                            && _controlledLesson is not null
                                            && ControlledConfirmCheckBox.IsChecked == true;
    }

    private static IReadOnlyList<string> ParseTargetCodes(string text) =>
        text.Split(['\r', '\n', ',', '，', ';', '；', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private void AddActivity(string message, string level) =>
        AddActivity(message, level, ActivityEventKind.Detail);

    private void AddActivity(string message, string level, ActivityEventKind kind)
    {
        LocalLogEntry entry;
        try
        {
            entry = _localActivityLog.Append(level, message, kind);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            entry = new LocalLogEntry(DateTimeOffset.Now, LocalActivityLog.Sanitize(level), LocalActivityLog.Sanitize(message))
            {
                Kind = kind
            };
        }

        _activityEntries.Add(entry);
        AppendActivityLine(_allActivityParagraph, entry);
        if (entry.IsKeyEvent)
        {
            _keyActivityEntries.Add(entry);
            AppendActivityLine(_keyActivityParagraph, entry);
        }

        var visibleEntries = _showAllActivity ? _activityEntries : _keyActivityEntries;
        ActivityEmptyText.Visibility = visibleEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_showAllActivity || entry.IsKeyEvent)
        {
            ScheduleActivityScrollToEnd(_showAllActivity ? _allActivityDocument : _keyActivityDocument);
        }
    }

    private void OnboardingHelpButton_Click(object sender, RoutedEventArgs e) => ShowOnboarding();

    private void OnboardingSkipButton_Click(object sender, RoutedEventArgs e) => CompleteOnboarding();

    private void OnboardingPreviousButton_Click(object sender, RoutedEventArgs e)
    {
        if (_onboardingStepIndex > 0)
        {
            _onboardingStepIndex--;
            UpdateOnboardingStep();
        }
    }

    private void OnboardingNextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_onboardingStepIndex >= 5)
        {
            CompleteOnboarding();
            return;
        }

        _onboardingStepIndex++;
        UpdateOnboardingStep();
    }

    private void OnboardingOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (OnboardingOverlay.Visibility == Visibility.Visible)
        {
            Dispatcher.BeginInvoke(UpdateOnboardingStep, DispatcherPriority.Render);
        }
    }

    private void ShowOnboarding()
    {
        OperationTabs.SelectedIndex = 0;
        OperationTabs.UpdateLayout();
        _onboardingStepIndex = 0;
        OnboardingOverlay.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(UpdateOnboardingStep, DispatcherPriority.Render);
    }

    private void CompleteOnboarding()
    {
        OnboardingOverlay.Visibility = Visibility.Collapsed;
        _shouldAutoShowOnboarding = false;
        if (_preferences.OnboardingCompleted != true)
        {
            _preferences = _preferences with { OnboardingCompleted = true };
            _preferences.Save();
        }
    }

    private void UpdateOnboardingStep()
    {
        if (OnboardingOverlay.Visibility != Visibility.Visible
            || OnboardingOverlay.ActualWidth <= 0
            || OnboardingOverlay.ActualHeight <= 0)
        {
            return;
        }

        OperationTabs.SelectedIndex = 0;
        OperationTabs.UpdateLayout();
        AutomationTasksGrid.UpdateLayout();
        var (title, body, targets) = GetOnboardingStep(_onboardingStepIndex);
        OnboardingStepText.Text = $"第 {_onboardingStepIndex + 1} / 6 步";
        OnboardingTitleText.Text = title;
        OnboardingBodyText.Text = body;
        OnboardingPreviousButton.IsEnabled = _onboardingStepIndex > 0;
        OnboardingNextButton.Content = _onboardingStepIndex == 5 ? "完成" : "下一步";

        var targetBounds = GetCombinedBounds(targets);
        targetBounds.Inflate(7, 7);
        targetBounds.Intersect(new Rect(0, 0, OnboardingOverlay.ActualWidth, OnboardingOverlay.ActualHeight));
        PositionOnboardingMasks(targetBounds);

        OnboardingCallout.Measure(new Size(360, OnboardingOverlay.ActualHeight));
        var calloutHeight = OnboardingCallout.DesiredSize.Height;
        var calloutLeft = Math.Clamp(
            targetBounds.Left + targetBounds.Width / 2 - 180,
            14,
            Math.Max(14, OnboardingOverlay.ActualWidth - 374));
        var placeBelow = targetBounds.Bottom + 18 + calloutHeight <= OnboardingOverlay.ActualHeight - 12;
        var calloutTop = placeBelow
            ? targetBounds.Bottom + 18
            : targetBounds.Top - calloutHeight - 18;
        if (calloutTop < 12)
        {
            calloutTop = Math.Max(12, OnboardingOverlay.ActualHeight / 2 - calloutHeight / 2);
        }

        Canvas.SetLeft(OnboardingCallout, calloutLeft);
        Canvas.SetTop(OnboardingCallout, calloutTop);
        OnboardingArrowText.Text = placeBelow ? "↑" : "↓";
    }

    private (string Title, string Body, IReadOnlyList<FrameworkElement> Targets) GetOnboardingStep(int index)
    {
        var courseCodeHeader = FindVisualDescendant<DataGridColumnHeader>(
            AutomationTasksGrid,
            header => ReferenceEquals(header.Column, AutomationCourseCodeColumn));
        var enabledHeader = FindVisualDescendant<DataGridColumnHeader>(
            AutomationTasksGrid,
            header => ReferenceEquals(header.Column, AutomationTasksGrid.Columns[0]));
        return index switch
        {
            0 => (
                "打开教务系统",
                "日常使用时先在独立教务窗口完成登录。新手指引只作说明，不会替你打开网页或提交任何信息。",
                [OpenEamsButton]),
            1 => (
                "添加课程任务",
                "点击“添加课程”会新增一行，并自动把输入光标放进教学班代码框。",
                [AddAutomationTaskButton]),
            2 => (
                "填写教学班代码",
                "教学班代码单击即可直接输入，不需要先选中整行。请填写完整代码，例如 001683EX.01。",
                courseCodeHeader is null ? [AutomationTasksGrid] : [courseCodeHeader]),
            3 => (
                "启用需要监测的课程",
                "启用复选框一次点击即可切换。只有已经启用的任务才会参加检查和监测。",
                enabledHeader is null ? [AutomationTasksGrid] : [enabledHeader]),
            4 => (
                "先做只读检查",
                "“立即检查（不选课）”只核对课程、余量和冲突课，不会提交选课或退课。",
                [CheckAutomationNowButton]),
            _ => (
                "确认后再开始监测",
                "黄色区域允许真实选退课。核对无误后仍需由你本人勾选确认并点击“开始监测”；引导不会替你执行。",
                [AutomationArmPanel])
        };
    }

    private Rect GetCombinedBounds(IReadOnlyList<FrameworkElement> targets)
    {
        Rect? combined = null;
        foreach (var target in targets.Where(target => target.IsVisible && target.ActualWidth > 0 && target.ActualHeight > 0))
        {
            try
            {
                var bounds = target.TransformToVisual(OnboardingOverlay)
                    .TransformBounds(new Rect(new Size(target.ActualWidth, target.ActualHeight)));
                combined = combined is null ? bounds : Rect.Union(combined.Value, bounds);
            }
            catch (InvalidOperationException)
            {
            }
        }

        return combined ?? new Rect(
            OnboardingOverlay.ActualWidth / 2 - 120,
            OnboardingOverlay.ActualHeight / 2 - 30,
            240,
            60);
    }

    private void PositionOnboardingMasks(Rect target)
    {
        SetCanvasRect(OnboardingMaskTop, 0, 0, OnboardingOverlay.ActualWidth, target.Top);
        SetCanvasRect(OnboardingMaskLeft, 0, target.Top, target.Left, target.Height);
        SetCanvasRect(
            OnboardingMaskRight,
            target.Right,
            target.Top,
            Math.Max(0, OnboardingOverlay.ActualWidth - target.Right),
            target.Height);
        SetCanvasRect(
            OnboardingMaskBottom,
            0,
            target.Bottom,
            OnboardingOverlay.ActualWidth,
            Math.Max(0, OnboardingOverlay.ActualHeight - target.Bottom));
        SetCanvasRect(OnboardingSpotlightBorder, target.Left, target.Top, target.Width, target.Height);
    }

    private static void SetCanvasRect(FrameworkElement element, double left, double top, double width, double height)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }

    private static T? FindVisualDescendant<T>(DependencyObject root, Predicate<T>? predicate = null)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && (predicate is null || predicate(match)))
            {
                return match;
            }

            if (FindVisualDescendant(child, predicate) is T descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        for (var current = source; current is not null;)
        {
            if (current is T match)
            {
                return match;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        OpenEamsButton.IsEnabled = !isBusy;
        ValidateButton.IsEnabled = !isBusy && !_automationRunning && _loginWindow?.IsBrowserReady == true;
        LogoutButton.IsEnabled = !isBusy && !_automationRunning && _loginWindow?.IsBrowserReady == true;
        ReadCoursesButton.IsEnabled = !isBusy && !_automationRunning && _loginWindow?.IsBrowserReady == true;
        ControlledPrecheckButton.IsEnabled = !isBusy && !_automationRunning && _loginWindow?.IsBrowserReady == true;
        TurnSelector.IsEnabled = !isBusy && !_automationRunning;
        ControlledActionSelector.IsEnabled = !isBusy && !_automationRunning;
        ControlledLessonCodeText.IsEnabled = !isBusy && !_automationRunning;
        BusyIndicator.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        UpdateControlledExecuteState();
        UpdateAutomationControls();
        UpdateAccountSecurityControls();
        RefreshInterruptedOperationBanner();
    }

    private void UpdateAutomationControls()
    {
        var browserReady = _loginWindow?.IsBrowserReady == true;
        StartAutomationButton.IsEnabled = !_isBusy
                                          && !_automationRunning
                                          && browserReady
                                          && !_realOperations.HasUnresolvedOperation
                                          && !_realOperations.IsOperationInProgress;
        CheckAutomationNowButton.IsEnabled = !_isBusy && !_automationRunning && browserReady;
        PauseAutomationButton.IsEnabled = !_isBusy && _automationRunning;
        AutomationTasksGrid.IsEnabled = !_isBusy && !_automationRunning;
        AddAutomationTaskButton.IsEnabled = !_isBusy && !_automationRunning;
        DeleteAutomationTaskButton.IsEnabled = !_isBusy && !_automationRunning;
        MoveAutomationTaskUpButton.IsEnabled = !_isBusy && !_automationRunning;
        MoveAutomationTaskDownButton.IsEnabled = !_isBusy && !_automationRunning;
        AutomationIntervalSelector.IsEnabled = !_isBusy && !_automationRunning;
        AutomationCustomIntervalText.IsEnabled = !_isBusy && !_automationRunning;
        AutomationIntervalJitterCheckBox.IsEnabled = !_isBusy && !_automationRunning;
        AutomationScheduleCheckBox.IsEnabled = !_isBusy && !_automationRunning;
        AutomationStartTimeText.IsEnabled = !_isBusy && !_automationRunning;
        AutomationEndTimeText.IsEnabled = !_isBusy && !_automationRunning;
        RunAtStartupCheckBox.IsEnabled = !_isBusy && !_automationRunning;
        AutomationArmCheckBox.IsEnabled = !_isBusy;
    }

    private void SetStatus(string message, StatusKind kind)
    {
        StatusText.Text = message;
        StatusDot.Fill = kind switch
        {
            StatusKind.Success => (Brush)FindResource("SuccessBrush"),
            StatusKind.Error => (Brush)FindResource("DangerBrush"),
            StatusKind.Working => (Brush)FindResource("AccentBrush"),
            StatusKind.Warning => (Brush)FindResource("LoginStatusYellowBrush"),
            _ => (Brush)FindResource("TextMutedBrush")
        };
        StatusDot.Stroke = kind == StatusKind.Warning
            ? (Brush)FindResource("LoginStatusYellowBorderBrush")
            : Brushes.Transparent;
        StatusDot.StrokeThickness = kind == StatusKind.Warning ? 0.75 : 0;
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _automationTimer.Stop();
        _automationTaskSaveTimer.Stop();
        ClearAutomationTimerPlan();
        SaveAutomationSettings();
        if (_systemEventsSubscribed)
        {
            NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
            SystemEvents.PowerModeChanged -= SystemPowerModeChanged;
            _systemEventsSubscribed = false;
        }

        _validationCancellation?.Cancel();
        _validationCancellation?.Dispose();
        if (KeepLoginCheckBox.IsChecked != true)
        {
            _loginWindow?.ClearLoginState();
        }

        _loginWindow?.CloseForApplication();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_automationCycleActive || _realOperations.IsOperationInProgress)
        {
            e.Cancel = true;
            SetStatus("当前真实操作尚未完成；为避免结果不确定，暂时不能关闭程序。", StatusKind.Error);
            AddActivity("用户尝试在真实操作执行中关闭程序；已阻止关闭。", "安全");
            return;
        }

        if (_automationRunning)
        {
            var confirmation = MessageBox.Show(
                this,
                "任务监测仍在运行。关闭程序会停止后续监测，是否确认关闭？",
                "确认停止监测并关闭",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        if (_automationRunning)
        {
            AddActivity("用户关闭程序，正在运行的监测已结束。", "信息", ActivityEventKind.MonitoringStopped);
        }

        AddActivity("程序正常退出。", "信息", ActivityEventKind.ApplicationLifecycle);
        _automationTimer.Stop();
        ClearAutomationTimerPlan();
        _automationRunning = false;
        SaveAutomationSettings();
    }

    private enum StatusKind
    {
        Neutral,
        Working,
        Warning,
        Success,
        Error
    }
}
