using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using UstcCourseAssistant.Route2.Models;
using UstcCourseAssistant.Route2.Services;

namespace UstcCourseAssistant.Route2;

public partial class MainWindow
{
    private readonly ObservableCollection<ScheduledSelection> _scheduledAppointments = [];
    private readonly ObservableCollection<AutomationTask> _scheduledTasks = [];
    private ScheduledSelection? _scheduledEditor;
    private bool _refreshingScheduledUi;
    private bool _loadingScheduledEditor;
    private static readonly TimeZoneInfo ScheduledTimeZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");

    private void InitializeScheduledUi()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(2, 2, 0);
        VersionText.Text = $"V{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        ScheduledAppointmentsGrid.ItemsSource = _scheduledAppointments;
        ScheduledTasksGrid.ItemsSource = _scheduledTasks;
        MonitoringTaskTurnSelector.ItemsSource = _turns;
        ScheduledDatePicker.SelectedDateChanged += ScheduledEditorChanged;
        ScheduledTimeText.TextChanged += ScheduledEditorChanged;
        ScheduledTurnSelector.SelectionChanged += ScheduledEditorChanged;
        ScheduledCompletionSelector.SelectionChanged += ScheduledEditorChanged;
        ScheduledRunWhenMissedCheckBox.Click += ScheduledEditorChanged;
        ScheduledAuthorizationCheckBox.Click += ScheduledEditorChanged;
        ScheduledTasksGrid.BeginningEdit += (_, args) => { if (!PrepareScheduledEdit()) args.Cancel = true; };
        RefreshScheduledUi();
    }

    private void RefreshScheduledUi(Guid? selectedId = null)
    {
        var preserveEditor = selectedId is null && _scheduledEditor is { State: ScheduledSelectionState.Draft } existing &&
            _scheduledService?.Appointments.Any(item => item.Id == existing.Id && item.State == existing.State) == true;
        selectedId ??= (ScheduledAppointmentsGrid.SelectedItem as ScheduledSelection)?.Id;
        _refreshingScheduledUi = true;
        try
        {
            _scheduledAppointments.Clear();
            if (_scheduledService is not null)
                foreach (var item in _scheduledService.Appointments.OrderBy(item => item.ExecuteAt).ThenBy(item => item.CreatedAt))
                    _scheduledAppointments.Add(item);
            ScheduledAppointmentsGrid.SelectedItem = _scheduledAppointments.FirstOrDefault(item => item.Id == selectedId);
        }
        finally { _refreshingScheduledUi = false; }
        if (preserveEditor) UpdateScheduledControls();
        else LoadScheduledEditor();
    }

    private void ScheduledAppointmentsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingScheduledUi) LoadScheduledEditor();
    }

    private void LoadScheduledEditor()
    {
        _loadingScheduledEditor = true;
        try { PopulateScheduledEditor(); }
        finally { _loadingScheduledEditor = false; }
    }

    private void ScheduledEditorChanged(object? sender, RoutedEventArgs e)
    {
        if (!_loadingScheduledEditor) PrepareScheduledEdit();
    }

    private bool PrepareScheduledEdit()
    {
        if (_loadingScheduledEditor || _scheduledEditor?.State != ScheduledSelectionState.Armed) return true;
        try
        {
            _scheduledService!.BeginEdit(_scheduledEditor.Id);
            _scheduledEditor.State = ScheduledSelectionState.Draft;
            RefreshScheduledUi();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ScheduledStatusText.Text = ex.Message;
            ScheduledEditorPanel.IsEnabled = false;
            AddActivity($"预约编辑保存失败：{ex.Message}", "错误", ActivityEventKind.ScheduledSelection);
            return false;
        }
    }

    private void PopulateScheduledEditor()
    {
        _scheduledEditor = ScheduledAppointmentsGrid.SelectedItem is ScheduledSelection selected
            ? ScheduledSelectionService.Copy(selected) : null;
        _scheduledTasks.Clear();
        if (_scheduledEditor is { } appointment)
        {
            var local = TimeZoneInfo.ConvertTime(appointment.ExecuteAt, ScheduledTimeZone);
            ScheduledDatePicker.SelectedDate = local.Date;
            ScheduledTimeText.Text = local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            var turns = new List<TurnInfo> { new(0, "执行时识别唯一开放轮次", "自动") };
            turns.AddRange(_turns);
            if (appointment.TurnId is long id && turns.All(turn => turn.Id != id))
                turns.Add(new TurnInfo(id, appointment.TurnName, "已保存轮次"));
            ScheduledTurnSelector.ItemsSource = turns;
            ScheduledTurnSelector.SelectedItem = turns.FirstOrDefault(turn => turn.Id == appointment.TurnId) ?? turns[0];
            ScheduledCompletionSelector.SelectedIndex = (int)appointment.CompletionAction;
            ScheduledRunWhenMissedCheckBox.IsChecked = appointment.RunWhenMissed;
            ScheduledAuthorizationCheckBox.IsChecked = appointment.RealOperationsAuthorized;
            foreach (var task in appointment.Tasks.OrderBy(task => task.Priority)) _scheduledTasks.Add(task);
            ScheduledStatusText.Text = string.IsNullOrWhiteSpace(appointment.Result)
                ? $"预约状态：{ScheduledStateText(appointment.State)}；账号：{appointment.StudentId}" : appointment.Result;
        }
        else ScheduledStatusText.Text = _scheduledService is null ? "预约配置不可用，执行已禁用。" : "请选择或新建预约";
        UpdateScheduledControls();
    }

    private static string ScheduledStateText(ScheduledSelectionState state) => state switch
    {
        ScheduledSelectionState.Draft => "草稿", ScheduledSelectionState.Armed => "已启用",
        ScheduledSelectionState.Queued => "等待执行", ScheduledSelectionState.Running => "执行中",
        ScheduledSelectionState.Completed => "已完成", ScheduledSelectionState.Failed => "失败",
        ScheduledSelectionState.Missed => "已错过", ScheduledSelectionState.Cancelled => "已取消",
        _ => "已中断"
    };

    private void UpdateScheduledControls()
    {
        if (ScheduledEditorPanel is null) return;
        var available = _scheduledService is not null && !_updateInstalling && !_updateHandshakePending;
        var state = _scheduledEditor?.State;
        var editable = available && state is ScheduledSelectionState.Draft or ScheduledSelectionState.Armed;
        ScheduledEditorPanel.IsEnabled = editable;
        NewScheduledButton.IsEnabled = available;
        CopyScheduledButton.IsEnabled = available && _scheduledEditor is not null && state != ScheduledSelectionState.Running;
        CancelScheduledButton.IsEnabled = available && state is ScheduledSelectionState.Draft or ScheduledSelectionState.Armed or ScheduledSelectionState.Queued;
        DeleteScheduledButton.IsEnabled = available && state is not null && state is not (ScheduledSelectionState.Armed or ScheduledSelectionState.Queued or ScheduledSelectionState.Running);
        MonitoringTaskTurnSelector.IsEnabled = !_automationRunning && !_realOperations.IsOperationInProgress && !_updateInstalling;
        AssignMonitoringTurnButton.IsEnabled = MonitoringTaskTurnSelector.IsEnabled;
    }

    private void ScheduledUiAction(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ScheduledStatusText.Text = ex.Message;
            AddActivity($"预约操作失败：{ex.Message}", "错误", ActivityEventKind.ScheduledSelection);
        }
    }

    private async void NewScheduledButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scheduledService is null) return;
        if (_sessionValidation is null && !await ValidateSessionAsync(true)) return;
        if (_sessionValidation is not { } session) return;
        ScheduledUiAction(() =>
        {
            var appointment = new ScheduledSelection { StudentId = session.StudentId,
                ExecuteAt = TimeZoneInfo.ConvertTime(DateTimeOffset.Now.AddMinutes(5), ScheduledTimeZone),
                TurnName = "执行时识别唯一开放轮次" };
            _scheduledService.UpsertDraft(appointment);
            RefreshScheduledUi(appointment.Id);
        });
    }

    private void CopyScheduledButton_Click(object sender, RoutedEventArgs e) => ScheduledUiAction(() =>
    {
        if (_scheduledService is null || _scheduledEditor is null) return;
        var copy = _scheduledService.CopyAsDraft(_scheduledEditor.Id);
        RefreshScheduledUi(copy.Id);
    });

    private void CancelScheduledButton_Click(object sender, RoutedEventArgs e) => ScheduledUiAction(() =>
    {
        if (_scheduledService is null || _scheduledEditor is null) return;
        _scheduledService.Cancel(_scheduledEditor.Id);
        CancelScheduledWaiting(_scheduledEditor.Id);
        RefreshScheduledUi(_scheduledEditor.Id);
    });

    private void DeleteScheduledButton_Click(object sender, RoutedEventArgs e) => ScheduledUiAction(() =>
    {
        if (_scheduledService is null || _scheduledEditor is null) return;
        _scheduledService.Delete(_scheduledEditor.Id);
        RefreshScheduledUi();
    });

    private ScheduledSelection ReadScheduledEditor()
    {
        if (_scheduledEditor is null) throw new InvalidOperationException("请选择预约。");
        CommitCourseEdits(ScheduledTasksGrid);
        if (ScheduledDatePicker.SelectedDate is not DateTime date ||
            !TimeOnly.TryParseExact(ScheduledTimeText.Text.Trim(), "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new InvalidOperationException("请填写有效日期和 HH:mm:ss 格式的北京时间。");
        if (!ValidateCourseTasks(_scheduledTasks, out var error))
        {
            ScheduledTasksGrid.Items.Refresh();
            throw new InvalidOperationException(error);
        }
        var appointment = ScheduledSelectionService.Copy(_scheduledEditor);
        appointment.ExecuteAt = new DateTimeOffset(DateTime.SpecifyKind(date.Date + time.ToTimeSpan(), DateTimeKind.Unspecified), ScheduledTimeZone.BaseUtcOffset);
        var turn = ScheduledTurnSelector.SelectedItem as TurnInfo;
        appointment.TurnId = turn?.Id is > 0 ? turn.Id : null;
        appointment.TurnName = appointment.TurnId is null ? "执行时识别唯一开放轮次" : $"{turn!.DisplayText}（{turn.Id}）";
        appointment.CompletionAction = ScheduledCompletionSelector.SelectedIndex == 1 ? ScheduledCompletionAction.ImportToMonitoring : ScheduledCompletionAction.Stop;
        appointment.RunWhenMissed = ScheduledRunWhenMissedCheckBox.IsChecked == true;
        appointment.RealOperationsAuthorized = ScheduledAuthorizationCheckBox.IsChecked == true;
        appointment.Tasks = _scheduledTasks.Select(task => ScheduledSelectionService.CopyTask(task)).ToList();
        return appointment;
    }

    private void SaveScheduledButton_Click(object sender, RoutedEventArgs e) => ScheduledUiAction(() =>
    {
        if (_scheduledService is null) return;
        var appointment = ReadScheduledEditor();
        if (_scheduledEditor!.State == ScheduledSelectionState.Armed) _scheduledService.BeginEdit(appointment.Id);
        appointment.State = ScheduledSelectionState.Draft;
        _scheduledService.UpsertDraft(appointment);
        RefreshScheduledUi(appointment.Id);
    });

    private async void ArmScheduledButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scheduledService is null) return;
        if (_sessionValidation is null && !await ValidateSessionAsync(true)) return;
        ScheduledUiAction(() =>
        {
            var appointment = ReadScheduledEditor();
            if (_sessionValidation?.StudentId != appointment.StudentId) throw new InvalidOperationException("当前账号与预约所属账号不一致。");
            if (_realOperations.HasUnresolvedOperation) throw new InvalidOperationException("请先核对中断的真实操作。");
            if (!appointment.RealOperationsAuthorized) throw new InvalidOperationException("请先勾选真实选退课授权。");
            if (appointment.ExecuteAt <= DateTimeOffset.Now) throw new InvalidOperationException("预约执行时间必须晚于当前时间。");
            if (!appointment.Tasks.Any(task => task.Enabled)) throw new InvalidOperationException("至少需要一门有效启用课程。");
            var summary = $"北京时间：{appointment.ExecuteAt:yyyy-MM-dd HH:mm:ss}\n账号：{appointment.StudentId}\n轮次：{appointment.TurnName}\n" +
                $"错过后补执行：{(appointment.RunWhenMissed ? "是" : "否")}\n完成方式：{(appointment.CompletionAction == ScheduledCompletionAction.Stop ? "停止" : "剩余课程导入并启动或继续监测")}\n\n" +
                string.Join("\n", appointment.Tasks.Where(task => task.Enabled).Select(task => $"{task.Priority}. {task.CourseCode}；冲突旧课：{task.ConflictsText}")) +
                "\n\n到点将执行一次真实选退课，不再弹出确认。是否启用？";
            if (MessageBox.Show(this, summary, "确认启用预约", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _scheduledService.Arm(appointment, DateTimeOffset.Now);
            _automaticWritesPaused = false;
            _scheduledResumeScan = true;
            _scheduledTimer.Start();
            AddActivity($"预约已启用：{appointment.ExecuteAt:yyyy-MM-dd HH:mm:ss zzz}", "预约", ActivityEventKind.ScheduledSelection);
            RefreshScheduledUi(appointment.Id);
        });
    }

    private void AddScheduledTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareScheduledEdit()) return;
        _scheduledTasks.Add(new AutomationTask { Priority = _scheduledTasks.Count + 1 });
        ScheduledTasksGrid.SelectedItem = _scheduledTasks.Last();
    }
    private void DeleteScheduledTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareScheduledEdit()) return;
        CommitCourseEdits(ScheduledTasksGrid);
        if (ScheduledTasksGrid.SelectedItem is AutomationTask task) _scheduledTasks.Remove(task);
        RenumberScheduledTasks();
    }
    private void MoveScheduledTaskUpButton_Click(object sender, RoutedEventArgs e) => MoveScheduledTask(-1);
    private void MoveScheduledTaskDownButton_Click(object sender, RoutedEventArgs e) => MoveScheduledTask(1);
    private void MoveScheduledTask(int direction)
    {
        if (!PrepareScheduledEdit()) return;
        CommitCourseEdits(ScheduledTasksGrid);
        if (ScheduledTasksGrid.SelectedItem is not AutomationTask selected) return;
        var index = _scheduledTasks.IndexOf(selected);
        var next = index + direction;
        if (next < 0 || next >= _scheduledTasks.Count) return;
        _scheduledTasks.Move(index, next);
        RenumberScheduledTasks();
    }
    private void RenumberScheduledTasks()
    {
        for (var i = 0; i < _scheduledTasks.Count; i++) _scheduledTasks[i].Priority = i + 1;
        ScheduledTasksGrid.Items.Refresh();
    }
    private void AssignMonitoringTurnButton_Click(object sender, RoutedEventArgs e)
    {
        if (_automationRunning || _realOperations.IsOperationInProgress || _updateInstalling) return;
        if (AutomationTasksGrid.SelectedItem is not AutomationTask task || MonitoringTaskTurnSelector.SelectedItem is not TurnInfo turn || _sessionValidation is null) return;
        CommitCourseEdits(AutomationTasksGrid);
        var previous = (task.StudentId, task.TurnId, task.TurnName);
        task.StudentId = _sessionValidation.StudentId;
        task.TurnId = turn.Id;
        task.TurnName = $"{turn.DisplayText}（{turn.Id}）";
        if (!ValidateCourseTasks(_automationTasks, out var error) || !SaveAutomationBeforeExecution())
        {
            (task.StudentId, task.TurnId, task.TurnName) = previous;
            if (!string.IsNullOrEmpty(error)) SetStatus(error, StatusKind.Error);
        }
        AutomationTasksGrid.Items.Refresh();
    }
}
