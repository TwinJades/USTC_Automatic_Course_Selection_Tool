using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using UstcCourseAssistant.Core.Authentication;
using UstcCourseAssistant.Core.Models;
using UstcCourseAssistant.Core.Services;
using UstcCourseAssistant.Core.Update;
using UstcCourseAssistant.Route2.Services;

namespace UstcCourseAssistant.Route2;

public partial class MainWindow : Window
{
    private readonly WebViewAuthenticationService _authentication = new();
    private readonly CourseAssistantController _controller;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ObservableCollection<AutomationTask> _tasks = [];
    private readonly ObservableCollection<AutomationTask> _appointmentTasks = [];
    private readonly ObservableCollection<TurnInfo> _turns = [];
    private readonly ObservableCollection<LessonInfo> _targets = [], _selected = [];
    private readonly ICredentialVault _vault;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private LocalPreferences _preferences;
    private LoginWindow? _login;
    private DirectoryUpdateService? _updates;
    private UpdateWindow? _updateWindow;
    private AppRelease? _release;
    private Guid? _precheck;
    private bool _loading, _ready, _closing, _allowClose, _appointmentDirty, _allLogs, _importing;
    private Task? _controllerRun;
    private AuthenticationState _lastAuthenticationState = AuthenticationState.SignedOut;
    private int _guideStep;

    public MainWindow()
    {
        _vault = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("USTC_ROUTE2_TEST_DATA_ROOT")) ? new WindowsCredentialVault() : new InMemoryCredentialVault();
        var hadData = LocalPreferences.HasExistingInstallationData();
        _preferences = LocalPreferences.Load();
        if (_preferences.OnboardingCompleted is null) { _preferences = _preferences with { OnboardingCompleted = hadData }; _preferences.Save(); }
        _controller = new CourseAssistantController(AppDataPaths.Root, _authentication, false);
        _loading = true;
        InitializeComponent();
        ScheduledDatePicker.SelectedDateChanged += (_, _) => AppointmentChanged();
        ScheduledTimeText.TextChanged += (_, _) => AppointmentChanged();
        ScheduledCompletionSelector.SelectionChanged += (_, _) => AppointmentChanged();
        ScheduledEditorPanel.PreviewMouseDown += ScheduledEditorPreviewMouseDown;
        ScheduledEditorPanel.PreviewKeyDown += ScheduledEditorPreviewKeyDown;
        ScheduledRunWhenMissedCheckBox.Click += (_, _) => AppointmentChanged();
        AutomationTasksGrid.ItemsSource = _tasks; ScheduledTasksGrid.ItemsSource = _appointmentTasks;
        TurnSelector.ItemsSource = _turns; TargetsGrid.ItemsSource = _targets; SelectedLessonsGrid.ItemsSource = _selected;
        _loading = true;
        KeepLoginCheckBox.IsChecked = _preferences.KeepLoginState;
        SavedAccountTextBox.Text = _preferences.SavedAccount;
        AutoLoginCheckBox.IsChecked = _preferences.AutoLoginEnabled;
        try { RunAtStartupCheckBox.IsChecked = StartupRegistrationService.IsEnabled(); CredentialStatusText.Text = _vault.Exists() ? "已保存 Windows 系统凭据。" : "尚未保存系统凭据。"; } catch (Exception ex) { CredentialStatusText.Text = ex.Message; }
        var version = typeof(MainWindow).Assembly.GetName().Version!;
        VersionText.Text = $"V{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        _loading = false;
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); await SaveSettingsAsync(); };
        _controller.Changed += ControllerChanged;
        Render(_controller.Snapshot, true);
        _ready = true;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _controllerRun = RunControllerAsync();
        try
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("USTC_ROUTE2_TEST_DATA_ROOT")))
            { _updates = new DirectoryUpdateService(Directory.GetParent(AppDataPaths.Root)!.FullName, "win-x64"); await CheckUpdateAsync(); }
            if (_closing) return;
            await RefreshLogsAsync();
            if (_preferences.AutoLoginEnabled && !_closing) OpenEamsButton_Click(this, new RoutedEventArgs());
            if (_preferences.OnboardingCompleted == false && !_closing) ShowGuide();
        }
        catch (Exception ex) { if (!_closing) StatusText.Text = LocalActivityLog.Sanitize(ex.Message); }
    }
    private async Task RunControllerAsync() { try { await _controller.RunAsync(_lifetime.Token); } catch (OperationCanceledException) { } catch (Exception ex) { await Dispatcher.InvokeAsync(() => StatusText.Text = LocalActivityLog.Sanitize(ex.Message)); } }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true; IsEnabled = false;
        try
        {
            _saveTimer.Stop();
            await _controller.ExecuteAsync(Request("service prepare-stop"), CancellationToken.None);
            _lifetime.Cancel(); if (_controllerRun is not null) await _controllerRun; await _controller.DisposeAsync();
            if (!_preferences.KeepLoginState) _login?.ClearLoginState();
            _login?.CloseForApplication(); _authentication.Dispose(); _updates?.Dispose();
            _allowClose = true; Close();
        }
        catch (Exception ex) { StatusText.Text = LocalActivityLog.Sanitize(ex.Message); IsEnabled = true; _closing = false; }
    }
    private static ControlRequest Request(string command, object? args = null) => new(Guid.NewGuid(), command, JsonSerializer.SerializeToElement(args ?? new { }));
    private async Task<ControlReply> Send(string command, object? args = null)
    {
        try { var reply = await _controller.ExecuteAsync(Request(command, args), _lifetime.Token); if (!reply.Success) StatusText.Text = reply.Message; return reply; }
        catch (Exception ex) { StatusText.Text = LocalActivityLog.Sanitize(ex.Message); return new(Guid.Empty, false, "error", StatusText.Text, null); }
    }
    private void ControllerChanged(AssistantSnapshot snapshot) => Dispatcher.BeginInvoke(new Action(() => { if (!_closing) Render(_controller.Snapshot, false); }));
    private void Render(AssistantSnapshot snapshot, bool initial)
    {
        if (_ready && snapshot.Authentication.State == AuthenticationState.Failed && _lastAuthenticationState == AuthenticationState.Authenticated && _preferences.AutoLoginEnabled && _login is not null)
            _login.RequestAuthentication();
        _lastAuthenticationState = snapshot.Authentication.State;
        StatusText.Text = snapshot.Status; AutomationStateText.Text = snapshot.MonitoringRunning ? "监测中" : snapshot.Status;
        StudentIdText.Text = snapshot.Session?.StudentId.ToString(CultureInfo.InvariantCulture) ?? "正在等待登录…";
        var turnId = (TurnSelector.SelectedItem as TurnInfo)?.Id;
        if (!_turns.SequenceEqual(snapshot.Session?.OpenTurns ?? []))
        {
            _turns.Clear(); foreach (var turn in snapshot.Session?.OpenTurns ?? []) _turns.Add(turn);
            TurnSelector.SelectedItem = _turns.FirstOrDefault(x => x.Id == turnId) ?? _turns.FirstOrDefault();
        }
        NoTurnsText.Text = _turns.Count == 0 ? "暂无开放轮次。" : $"开放轮次：{_turns.Count}";
        if (snapshot.PendingOperation is not null || snapshot.SafetyPaused || snapshot.MonitoringRunning || snapshot.Authentication.State != AuthenticationState.Authenticated)
        { _precheck = null; ControlledConfirmCheckBox.IsChecked = false; ExecuteControlledButton.IsEnabled = false; }
        InterruptedOperationBanner.Visibility = snapshot.PendingOperation is not null || snapshot.SafetyPaused ? Visibility.Visible : Visibility.Collapsed;
        if (snapshot.MonitoringRunning || initial || !AutomationTasksGrid.IsKeyboardFocusWithin)
        {
            var selectedId = (AutomationTasksGrid.SelectedItem as AutomationTask)?.Id;
            _loading = true; _tasks.Clear(); foreach (var task in snapshot.Monitoring.Tasks.OrderBy(t => t.Priority)) _tasks.Add(task);
            AutomationTasksGrid.SelectedItem = _tasks.FirstOrDefault(t => t.Id == selectedId);
            if (initial) { var settings = snapshot.Monitoring; AutomationIntervalSelector.SelectedIndex = settings.IntervalSeconds switch { 5 => 0, 15 => 1, 60 => 2, _ => 3 }; AutomationCustomIntervalText.Text = settings.IntervalSeconds.ToString(); AutomationIntervalJitterCheckBox.IsChecked = settings.EnableIntervalJitter; AutomationScheduleCheckBox.IsChecked = settings.EnableSchedule; AutomationStartTimeText.Text = settings.StartAt.ToString("HH:mm"); AutomationEndTimeText.Text = settings.EndAt.ToString("HH:mm"); }
            _loading = false;
        }
        else
        {
            var ordered = snapshot.Monitoring.Tasks.OrderBy(t => t.Priority).ToArray();
            if (ordered.Length == _tasks.Count && ordered.All(t => _tasks.Any(current => current.Id == t.Id)))
            {
                var changed = false;
                for (int i = 0; i < ordered.Length; i++)
                {
                    var current = _tasks.First(t => t.Id == ordered[i].Id);
                    if (current.Priority != ordered[i].Priority) { current.Priority = ordered[i].Priority; changed = true; }
                    var index = _tasks.IndexOf(current);
                    if (index != i) { _tasks.Move(index, i); changed = true; }
                }
                if (changed) AutomationTasksGrid.Items.Refresh();
            }
        }
        if (initial || !_appointmentDirty && !ScheduledEditorPanel.IsKeyboardFocusWithin) LoadAppointment(snapshot.Appointment);
        AutomationTasksGrid.IsEnabled = !snapshot.MonitoringRunning;
        TurnSelector.IsEnabled = !snapshot.MonitoringRunning;
        AddAutomationTaskButton.IsEnabled = DeleteAutomationTaskButton.IsEnabled = MoveAutomationTaskUpButton.IsEnabled = MoveAutomationTaskDownButton.IsEnabled = !snapshot.MonitoringRunning;
        AutomationIntervalSelector.IsEnabled = AutomationCustomIntervalText.IsEnabled = AutomationIntervalJitterCheckBox.IsEnabled = AutomationScheduleCheckBox.IsEnabled = AutomationStartTimeText.IsEnabled = AutomationEndTimeText.IsEnabled = !snapshot.MonitoringRunning;
        StartAutomationButton.IsEnabled = !snapshot.MonitoringRunning; PauseAutomationButton.IsEnabled = snapshot.MonitoringRunning;
    }
    private void LoadAppointment(ScheduledSelection? appointment)
    {
        _loading = true;
        var at = appointment?.ExecuteAt ?? BeijingClock.Now().AddMinutes(5);
        at = TimeZoneInfo.ConvertTime(at, BeijingClock.TimeZone);
        ScheduledDatePicker.SelectedDate = at.Date; ScheduledTimeText.Text = at.ToString("HH:mm:ss");
        ScheduledCompletionSelector.SelectedIndex = (int)(appointment?.CompletionAction ?? ScheduledCompletionAction.Stop);
        ScheduledRunWhenMissedCheckBox.IsChecked = appointment?.RunWhenMissed ?? false;
        ScheduledAuthorizationCheckBox.IsChecked = appointment?.RealOperationsAuthorized ?? false;
        _appointmentTasks.Clear(); foreach (var task in appointment?.Tasks ?? []) _appointmentTasks.Add(task);
        ScheduledSummaryText.Text = appointment is null ? "尚未保存预约。" : $"预约：{appointment.State}，北京时间 {at:yyyy-MM-dd HH:mm:ss}";
        ScheduledStatusText.Text = appointment?.Result ?? "修改后需重新授权。";
        _loading = false;
    }
    private async void OpenEamsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_login is null)
        {
            _login = new LoginWindow { Owner = this };
            _login.ConfigureAutoLogin(_preferences.AutoLoginEnabled, () => _vault.Read());
            _login.AuthenticatedPageReached += async (_, _) => await ImportBrowserAsync();
        }
        _login.ShowBrowserWindow();
        await Task.CompletedTask;
    }
    private async Task ImportBrowserAsync()
    {
        if (_login is null || _importing) return;
        _importing = true;
        try { await _controller.ExecuteExternalAuthenticationAsync(token => Dispatcher.InvokeAsync(async () => await _login.ImportSessionAsync(_authentication, token)).Task.Unwrap(), _lifetime.Token); }
        catch (Exception ex) { StatusText.Text = LocalActivityLog.Sanitize(ex.Message); }
        finally { _importing = false; }
    }
    private async void ValidateButton_Click(object sender, RoutedEventArgs e) => await ImportBrowserAsync();
    private async void LogoutButton_Click(object sender, RoutedEventArgs e) { var result = await Send("auth logout"); if (result.Success) _login?.Logout(); }
    private void KeepLoginCheckBox_Changed(object sender, RoutedEventArgs e) { if (!_ready || _loading) return; _preferences = _preferences with { KeepLoginState = KeepLoginCheckBox.IsChecked == true }; _preferences.Save(); }
    private void AutoLoginCheckBox_Changed(object sender, RoutedEventArgs e) { if (!_ready || _loading) return; _preferences = _preferences with { AutoLoginEnabled = AutoLoginCheckBox.IsChecked == true }; _preferences.Save(); _login?.ConfigureAutoLogin(_preferences.AutoLoginEnabled, () => _vault.Read(), true); }
    private void SaveCredentialButton_Click(object sender, RoutedEventArgs e)
    {
        try { _vault.Save(SavedAccountTextBox.Text, SavedPasswordBox.SecurePassword); _preferences = _preferences with { SavedAccount = SavedAccountTextBox.Text.Trim() }; _preferences.Save(); SavedPasswordBox.Clear(); CredentialStatusText.Text = "已保存到 Windows 凭据管理器。"; }
        catch (Exception ex) { CredentialStatusText.Text = LocalActivityLog.Sanitize(ex.Message); }
    }
    private void DeleteCredentialButton_Click(object sender, RoutedEventArgs e) { try { _vault.Delete(); CredentialStatusText.Text = "已删除系统凭据。"; } catch (Exception ex) { CredentialStatusText.Text = ex.Message; } }
    private void RunAtStartupCheckBox_Changed(object sender, RoutedEventArgs e) { if (!_ready || _loading) return; try { StartupRegistrationService.SetEnabled(RunAtStartupCheckBox.IsChecked == true); } catch (Exception ex) { StatusText.Text = ex.Message; } }
    private void AutomationSettings_Changed(object sender, RoutedEventArgs e) { if (_ready && !_loading) { _saveTimer.Stop(); _saveTimer.Start(); } }
    private void AutomationSettings_Changed(object sender, TextChangedEventArgs e) => AutomationSettings_Changed(sender, (RoutedEventArgs)e);
    private void AutomationSettings_Changed(object sender, SelectionChangedEventArgs e) => AutomationSettings_Changed(sender, (RoutedEventArgs)e);
    private async Task<bool> SaveSettingsAsync()
    {
        if (!int.TryParse(AutomationIntervalSelector.SelectedIndex switch { 0 => "5", 1 => "15", 2 => "60", _ => AutomationCustomIntervalText.Text }, out var interval)) { StatusText.Text = "间隔无效。"; return false; }
        var reply = await Send("settings set", new { Interval = interval, Jitter = AutomationIntervalJitterCheckBox.IsChecked == true, Schedule = AutomationScheduleCheckBox.IsChecked == true, Start = AutomationStartTimeText.Text, End = AutomationEndTimeText.Text });
        return reply.Success;
    }
    private async void StartAutomationButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await SaveSettingsAsync()) return; if (TurnSelector.SelectedItem is not TurnInfo turn) { StatusText.Text = "请选择开放轮次。"; return; }
        if (AutomationArmCheckBox.IsChecked != true) { StatusText.Text = "请明确授权真实选退课。"; return; }
        if (_controller.Snapshot.Session is null) { StatusText.Text = "请先登录并验证教务会话。"; return; }
        if (MessageBox.Show(this, $"确认在轮次 {turn.DisplayText} 持续监测并对启用任务执行真实选退课？", "监测授权确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Send("monitor start", new { TurnId = turn.Id, Authorize = true });
    }
    private async void PauseAutomationButton_Click(object sender, RoutedEventArgs e) => await Send("monitor stop");
    private async void AutomationArmCheckBox_Unchecked(object sender, RoutedEventArgs e) { if (_ready && !_loading && _controller.Snapshot.MonitoringRunning) await Send("monitor stop"); }
    private async void CheckAutomationNowButton_Click(object sender, RoutedEventArgs e) { TargetCodesText.Text = string.Join("\n", _tasks.Where(x => x.Enabled).Select(x => x.CourseCode)); await ReadCoursesAsync(); }
    private async void AddAutomationTaskButton_Click(object sender, RoutedEventArgs e)
    {
        var code = Prompt("输入教学班代码："); if (string.IsNullOrWhiteSpace(code)) return;
        await Send("tasks add", new { Code = code.Trim(), Priority = _tasks.Count == 0 ? 1 : _tasks.Max(x => x.Priority) + 1 });
    }
    private async void DeleteAutomationTaskButton_Click(object sender, RoutedEventArgs e) { if (AutomationTasksGrid.SelectedItem is AutomationTask task) await Send("tasks remove", new { task.Id }); }
    private async void MoveAutomationTaskUpButton_Click(object sender, RoutedEventArgs e) => await MoveTaskAsync(-1);
    private async void MoveAutomationTaskDownButton_Click(object sender, RoutedEventArgs e) => await MoveTaskAsync(1);
    private async Task MoveTaskAsync(int direction)
    {
        if (AutomationTasksGrid.SelectedItem is not AutomationTask task) return;
        var ordered = _controller.Snapshot.Monitoring.Tasks.OrderBy(x => x.Priority).ToList(); var index = ordered.FindIndex(x => x.Id == task.Id); var next = index + direction;
        if (index < 0 || next < 0 || next >= ordered.Count) return;
        await Send("tasks reorder", new { task.Id, OtherId = ordered[next].Id });
    }
    private async void AutomationEnabledCheckBox_Click(object sender, RoutedEventArgs e) { if (sender is CheckBox { DataContext: AutomationTask task }) await Send(task.Enabled ? "tasks enable" : "tasks disable", new { task.Id }); }
    private async void AutomationTaskInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _loading || sender is not TextBox { IsKeyboardFocusWithin: true, DataContext: AutomationTask task } input) return;
        var attempted = input.Text;
        var property = input.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path?.Path;
        object arguments = property switch
        {
            nameof(AutomationTask.CourseCode) => new { task.Id, Code = attempted },
            nameof(AutomationTask.ConflictsText) => new { task.Id, Conflict = attempted.Split([',', '，', ';', '；', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() },
            _ => throw new InvalidOperationException("无法识别任务输入字段。")
        };
        var reply = await Send("tasks edit", arguments);
        if (reply.Success || input.Text != attempted) return;
        var saved = _controller.Snapshot.Monitoring.Tasks.FirstOrDefault(x => x.Id == task.Id);
        if (saved is null) return;
        _loading = true;
        try
        {
            task.CourseCode = saved.CourseCode;
            task.ConflictingCourseCodes = saved.ConflictingCourseCodes.ToList();
            input.SetCurrentValue(TextBox.TextProperty, property == nameof(AutomationTask.CourseCode) ? saved.CourseCode : saved.ConflictsText);
            input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }
        finally { _loading = false; }
    }
    private void AutomationTaskInput_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: AutomationTask task }) AutomationTasksGrid.SelectedItem = task;
    }
    private async void AutomationTasksGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Keyboard.FocusedElement is TextBox || AutomationTasksGrid.SelectedItem is not AutomationTask task) return;
        e.Handled = true;
        await Send("tasks remove", new { task.Id });
    }
    private async void ReadCoursesButton_Click(object sender, RoutedEventArgs e) => await ReadCoursesAsync();
    private async Task ReadCoursesAsync()
    {
        if (TurnSelector.SelectedItem is not TurnInfo turn) { StatusText.Text = "请选择开放轮次。"; return; }
        _targets.Clear(); _selected.Clear(); var codes = TargetCodesText.Text.Split([',', '，', ';', '；', '\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (codes.Length == 0) { var selected = await Send("courses selected", new { TurnId = turn.Id }); if (selected.Data is { } data) foreach (var lesson in data.Deserialize<List<LessonInfo>>() ?? []) _selected.Add(lesson); return; }
        var missing = new List<string>();
        foreach (var code in codes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var reply = await Send("courses check", new { TurnId = turn.Id, Code = code }); if (!reply.Success || reply.Data is not { } data) continue;
            var shadow = data.Deserialize<ShadowSnapshot>(); if (shadow is null) continue;
            foreach (var lesson in shadow.TargetLessons) if (!_targets.Any(x => x.Id == lesson.Id)) _targets.Add(lesson);
            if (_selected.Count == 0) foreach (var lesson in shadow.SelectedLessons) _selected.Add(lesson);
            missing.AddRange(shadow.MissingTargetCodes);
        }
        MissingTargetText.Text = missing.Count == 0 ? "" : "未找到：" + string.Join(", ", missing); SnapshotSummaryText.Text = $"目标 {_targets.Count} 门；已选 {_selected.Count} 门。";
    }
    private void ControlledInput_Changed(object sender, RoutedEventArgs e) { _precheck = null; if (ControlledConfirmCheckBox is not null) ControlledConfirmCheckBox.IsChecked = false; if (ExecuteControlledButton is not null) ExecuteControlledButton.IsEnabled = false; }
    private void ControlledInput_Changed(object sender, TextChangedEventArgs e) => ControlledInput_Changed(sender, (RoutedEventArgs)e);
    private void ControlledInput_Changed(object sender, SelectionChangedEventArgs e) => ControlledInput_Changed(sender, (RoutedEventArgs)e);
    private void ControlledConfirmCheckBox_Changed(object sender, RoutedEventArgs e) { if (ExecuteControlledButton is not null) ExecuteControlledButton.IsEnabled = _precheck.HasValue && ControlledConfirmCheckBox.IsChecked == true; }
    private async void ControlledPrecheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (TurnSelector.SelectedItem is not TurnInfo turn) { StatusText.Text = "请选择轮次。"; return; }
        var reply = await Send("operations precheck", new { TurnId = turn.Id, Code = ControlledLessonCodeText.Text.Trim(), Action = ControlledActionSelector.SelectedIndex == 0 ? "add" : "drop" });
        if (reply.Data is { } data && reply.Success) { _precheck = data.GetProperty("PrecheckId").GetGuid(); ControlledDetailsText.Text = data.ToString(); } else ControlledDetailsText.Text = reply.Message;
    }
    private async void ExecuteControlledButton_Click(object sender, RoutedEventArgs e)
    {
        if (_precheck is not Guid id || ControlledConfirmCheckBox.IsChecked != true) return;
        if (MessageBox.Show(this, $"确认对 {ControlledLessonCodeText.Text.Trim()} 执行一次真实{(ControlledActionSelector.SelectedIndex == 0 ? "选课" : "退课")}？", "真实操作确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _precheck = null; ExecuteControlledButton.IsEnabled = false;
        var reply = await Send("operations execute", new { PrecheckId = id, Authorize = true }); ControlledConfirmCheckBox.IsChecked = false; ControlledResultText.Text = reply.Data?.ToString() ?? reply.Message;
    }
    private async void InspectInterruptedOperationButton_Click(object sender, RoutedEventArgs e) { var reply = await Send("operations verify"); if (reply.Success) { InterruptedOperationText.Text = reply.Data?.ToString() ?? reply.Message; AcknowledgeInterruptedOperationButton.IsEnabled = true; } }
    private async void AcknowledgeInterruptedOperationButton_Click(object sender, RoutedEventArgs e) { if (MessageBox.Show(this, "已人工核对教务实际状态，确认解除保护？", "安全确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; var reply = await Send("operations acknowledge", new { Authorize = true }); if (reply.Success) AcknowledgeInterruptedOperationButton.IsEnabled = false; }
    private static string? Prompt(string label)
    {
        var input = new TextBox { Margin = new Thickness(12) }; var ok = new Button { Content = "确定", IsDefault = true, Margin = new Thickness(12) };
        var panel = new StackPanel(); panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(12, 12, 12, 0) }); panel.Children.Add(input); panel.Children.Add(ok);
        var window = new Window { Title = "USTC 选课助手", Width = 380, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = panel }; ok.Click += (_, _) => window.DialogResult = true;
        return window.ShowDialog() == true ? input.Text : null;
    }
}
