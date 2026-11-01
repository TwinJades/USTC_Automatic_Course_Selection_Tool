using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UstcCourseAssistant.Route2.Models;
using UstcCourseAssistant.Route2.Services;

namespace UstcCourseAssistant.Route2;

public partial class MainWindow
{
    private readonly ObservableCollection<AutomationTask> _scheduledTasks = [];
    private ScheduledSelection? _scheduledEditor;
    private bool _loadingScheduledEditor;
    private bool _scheduledEditorDirty;
    private bool _scheduledEditFailed;
    private string? _scheduledUiMessage;
    private string _scheduledDateInputText = string.Empty;
    private static readonly TimeZoneInfo ScheduledTimeZone = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");

    private void InitializeScheduledUi()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version
            ?? throw new InvalidOperationException("应用程序缺少版本信息。");
        VersionText.Text = $"V{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        ScheduledTasksGrid.ItemsSource = _scheduledTasks;
        ScheduledDatePicker.SelectedDateChanged += ScheduledDateChanged;
        ScheduledDatePicker.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(ScheduledDateInput_TextChanged));
        ScheduledDatePicker.DateValidationError += (_, args) =>
        {
            args.ThrowException = false;
            if (_loadingScheduledEditor) return;
            var unfinished = _scheduledDateInputText;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_scheduledDateInputText != unfinished) return;
                _loadingScheduledEditor = true;
                try { ScheduledDatePicker.Text = unfinished; }
                finally { _loadingScheduledEditor = false; }
            }));
        };
        ScheduledTimeText.TextChanged += ScheduledEditorChanged;
        ScheduledCompletionSelector.SelectionChanged += ScheduledEditorChanged;
        ScheduledRunWhenMissedCheckBox.Click += ScheduledEditorChanged;
        ScheduledAuthorizationCheckBox.Click += ScheduledEditorChanged;
        ScheduledTasksGrid.BeginningEdit += (_, args) => { if (!PrepareScheduledEdit()) args.Cancel = true; };
        ScheduledEditorPanel.PreviewMouseDown += ScheduledEditor_PreviewMouseDown;
        ScheduledEditorPanel.PreviewKeyDown += ScheduledEditor_PreviewKeyDown;
        ScheduledEditorPanel.PreviewTextInput += (_, args) =>
        {
            if (IsScheduledInput(args.OriginalSource as DependencyObject) && !PrepareScheduledEdit()) args.Handled = true;
        };
        DataObject.AddPastingHandler(ScheduledEditorPanel, (_, args) =>
        {
            if (!PrepareScheduledEdit()) args.CancelCommand();
        });
        _scheduledUiMessage = _scheduledService?.LoadWarning;
        RefreshScheduledUi();
    }

    private void RefreshScheduledUi()
    {
        var current = _scheduledService?.Current;
        if (_scheduledEditor is null || (!_scheduledEditFailed &&
            (!_scheduledEditorDirty || current is not null &&
                (current.Id != _scheduledEditor.Id || current.State != _scheduledEditor.State))))
            LoadScheduledEditor(current);
        UpdateScheduledSummary(current);
        UpdateScheduledControls();
    }

    private void LoadScheduledEditor(ScheduledSelection? appointment)
    {
        _loadingScheduledEditor = true;
        try
        {
            _scheduledEditor = appointment is null
                ? new ScheduledSelection { ExecuteAt = DateTimeOffset.Now.AddMinutes(5), CompletionAction = ScheduledCompletionAction.Stop }
                : ScheduledSelectionService.Copy(appointment);
            _scheduledTasks.Clear();
            var local = TimeZoneInfo.ConvertTime(_scheduledEditor.ExecuteAt, ScheduledTimeZone);
            ScheduledDatePicker.SelectedDate = local.Date;
            _scheduledDateInputText = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            ScheduledDatePicker.Text = _scheduledDateInputText;
            ScheduledTimeText.Text = local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            ScheduledCompletionSelector.SelectedIndex = (int)_scheduledEditor.CompletionAction;
            ScheduledRunWhenMissedCheckBox.IsChecked = _scheduledEditor.RunWhenMissed;
            ScheduledAuthorizationCheckBox.IsChecked = _scheduledEditor.RealOperationsAuthorized;
            foreach (var task in _scheduledEditor.Tasks) _scheduledTasks.Add(task);
            _scheduledEditorDirty = appointment is null;
        }
        finally { _loadingScheduledEditor = false; }
    }

    private void UpdateScheduledSummary(ScheduledSelection? current)
    {
        ScheduledSummaryText.Text = current is null
            ? "预约状态：草稿；尚未保存预约"
            : $"预约状态：{ScheduledStateText(current.State)}；已保存时间（北京时间）：{TimeZoneInfo.ConvertTime(current.ExecuteAt, ScheduledTimeZone):yyyy-MM-dd HH:mm:ss}";
        ScheduledStatusText.Text = _scheduledUiMessage ?? (_scheduledService is null
            ? "预约配置不可用，执行已禁用。"
            : !string.IsNullOrWhiteSpace(current?.Result) ? current.Result
            : "只执行一次；修改时间或课程后，需要重新授权并启用。到点使用左侧当前开放轮次。");
    }

    private static string ScheduledStateText(ScheduledSelectionState state) => state switch
    {
        ScheduledSelectionState.Draft => "草稿", ScheduledSelectionState.Armed => "已启用",
        ScheduledSelectionState.Queued => "等待执行", ScheduledSelectionState.Running => "执行中",
        ScheduledSelectionState.Completed => "已完成", ScheduledSelectionState.Failed => "失败",
        ScheduledSelectionState.Missed => "已错过", ScheduledSelectionState.Cancelled => "已取消",
        _ => "已中断"
    };

    private void ScheduledEditorChanged(object? sender, RoutedEventArgs e)
    {
        if (_loadingScheduledEditor || !PrepareScheduledEdit()) return;
        _scheduledEditorDirty = true;
        _scheduledUiMessage = null;
    }

    private void ScheduledDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingScheduledEditor || !PrepareScheduledEdit()) return;
        if (ScheduledDatePicker.SelectedDate is DateTime date)
        {
            _loadingScheduledEditor = true;
            _scheduledDateInputText = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            try { ScheduledDatePicker.Text = _scheduledDateInputText; }
            finally { _loadingScheduledEditor = false; }
        }
        _scheduledEditorDirty = true;
        _scheduledUiMessage = null;
    }

    private void ScheduledDateInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingScheduledEditor || e.OriginalSource is not TextBox { IsKeyboardFocusWithin: true } input) return;
        if (PrepareScheduledEdit())
        {
            _scheduledDateInputText = input.Text;
            _scheduledEditorDirty = true;
        }
    }

    private static bool IsScheduledInput(DependencyObject? source) =>
        FindVisualAncestor<TextBox>(source) is { IsReadOnly: false } ||
        FindVisualAncestor<DatePicker>(source) is not null ||
        FindVisualAncestor<ComboBox>(source) is not null ||
        FindVisualAncestor<CheckBox>(source) is not null;

    private void ScheduledEditor_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsScheduledInput(e.OriginalSource as DependencyObject)) return;
        if (!PrepareScheduledEdit()) e.Handled = true;
    }

    private void ScheduledEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsScheduledInput(e.OriginalSource as DependencyObject)) return;
        if (e.Key is Key.Tab or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && (e.Key is Key.C or Key.A)) return;
        if (!PrepareScheduledEdit()) e.Handled = true;
    }

    private bool PrepareScheduledEdit()
    {
        if (_loadingScheduledEditor) return true;
        if (_scheduledEditFailed || _scheduledService is null || _scheduledRunning || _updateInstalling || _updateHandshakePending) return false;
        if (_scheduledEditor is null) return false;
        var current = _scheduledService.Current;
        if (current?.State is ScheduledSelectionState.Queued or ScheduledSelectionState.Running) return false;
        if (_scheduledEditor.State == ScheduledSelectionState.Draft) return true;
        try
        {
            var previous = current is null ? null : ScheduledSelectionService.Copy(current);
            if (previous is not null && previous.State != ScheduledSelectionState.Armed)
                AddActivity($"上次预约结果（{ScheduledStateText(previous.State)}）：{previous.Result}", "预约", ActivityEventKind.ScheduledSelection);
            _scheduledService.BeginEdit(_scheduledEditor.Id);
            _scheduledEditor.State = ScheduledSelectionState.Draft;
            _scheduledEditor.RealOperationsAuthorized = false;
            _loadingScheduledEditor = true;
            try { ScheduledAuthorizationCheckBox.IsChecked = false; }
            finally { _loadingScheduledEditor = false; }
            _scheduledEditorDirty = true;
            _scheduledUiMessage = null;
            UpdateScheduledSummary(_scheduledService.Current);
            UpdateScheduledControls();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _scheduledEditFailed = true;
            _automaticWritesPaused = true;
            _scheduledUiMessage = $"预约编辑保存失败，已禁止执行：{ex.Message}";
            ScheduledStatusText.Text = _scheduledUiMessage;
            UpdateScheduledControls();
            AddActivity(_scheduledUiMessage, "错误", ActivityEventKind.ScheduledSelection);
            return false;
        }
    }

    private void UpdateScheduledControls()
    {
        if (ScheduledEditorPanel is null) return;
        var available = _scheduledService is not null && !_updateInstalling && !_updateHandshakePending && !_scheduledEditFailed;
        var state = _scheduledService?.Current?.State ?? ScheduledSelectionState.Draft;
        var editable = available && !_scheduledRunning && state is not (ScheduledSelectionState.Queued or ScheduledSelectionState.Running);
        ScheduledEditorPanel.IsEnabled = true;
        ScheduledDatePicker.IsEnabled = editable;
        ScheduledTimeText.IsEnabled = editable;
        ScheduledCompletionSelector.IsEnabled = editable;
        ScheduledRunWhenMissedCheckBox.IsEnabled = editable;
        ScheduledTasksGrid.IsEnabled = editable;
        ScheduledAuthorizationCheckBox.IsEnabled = editable;
        AddScheduledTaskButton.IsEnabled = editable;
        DeleteScheduledTaskButton.IsEnabled = editable;
        MoveScheduledTaskUpButton.IsEnabled = editable;
        MoveScheduledTaskDownButton.IsEnabled = editable;
        SaveScheduledButton.IsEnabled = editable;
        ArmScheduledButton.IsEnabled = editable && !HasScheduledWaiting;
        CancelScheduledButton.IsEnabled = available && !_scheduledRunning && _scheduledService?.Current is not null &&
            (state is ScheduledSelectionState.Draft or ScheduledSelectionState.Armed or ScheduledSelectionState.Queued);
    }

    private void ScheduledUiAction(Action action)
    {
        try { _scheduledUiMessage = null; action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _scheduledUiMessage = ex.Message;
            ScheduledStatusText.Text = ex.Message;
            AddActivity($"预约操作失败：{ex.Message}", "错误", ActivityEventKind.ScheduledSelection);
        }
    }

    private void CancelScheduledButton_Click(object sender, RoutedEventArgs e) => ScheduledUiAction(() =>
    {
        if (_scheduledService?.Current is not { } current || _scheduledRunning) return;
        _scheduledService.Cancel(current.Id);
        CancelScheduledWaiting(current.Id);
        _scheduledEditorDirty = false;
        RefreshScheduledUi();
    });

    private ScheduledSelection ReadScheduledEditor()
    {
        if (_scheduledEditor is null) throw new InvalidOperationException("预约编辑器不可用。");
        CommitCourseEdits(ScheduledTasksGrid);
        if (!DateTime.TryParseExact(_scheduledDateInputText.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
            !TimeOnly.TryParseExact(ScheduledTimeText.Text.Trim(), "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            throw new InvalidOperationException("请填写 yyyy-MM-dd 格式的日期和 HH:mm:ss 格式的北京时间。");
        var appointment = ScheduledSelectionService.Copy(_scheduledEditor);
        appointment.ExecuteAt = new DateTimeOffset(DateTime.SpecifyKind(date.Date + time.ToTimeSpan(), DateTimeKind.Unspecified), ScheduledTimeZone.BaseUtcOffset);
        appointment.CompletionAction = ScheduledCompletionSelector.SelectedIndex == 1 ? ScheduledCompletionAction.ImportToMonitoring : ScheduledCompletionAction.Stop;
        appointment.RunWhenMissed = ScheduledRunWhenMissedCheckBox.IsChecked == true;
        appointment.RealOperationsAuthorized = ScheduledAuthorizationCheckBox.IsChecked == true;
        appointment.Tasks = _scheduledTasks.Select(task => ScheduledSelectionService.CopyTask(task)).ToList();
        return appointment;
    }

    private void SaveScheduledButton_Click(object sender, RoutedEventArgs e) => ScheduledUiAction(() =>
    {
        if (!PrepareScheduledEdit() || _scheduledService is null) return;
        var appointment = ReadScheduledEditor();
        appointment.State = ScheduledSelectionState.Draft;
        appointment.RealOperationsAuthorized = false;
        _scheduledService.UpsertDraft(appointment);
        _scheduledEditorDirty = false;
        RefreshScheduledUi();
        _scheduledUiMessage = "草稿已保存；执行前请重新确认授权并启用预约。";
        ScheduledStatusText.Text = _scheduledUiMessage;
    });

    private async void ArmScheduledButton_Click(object sender, RoutedEventArgs e)
    {
        if (_scheduledService is null || _scheduledEditFailed || HasScheduledWaiting || _scheduledRunning) return;
        if (_sessionValidation is null && !await ValidateSessionAsync(true)) return;
        ScheduledUiAction(() =>
        {
            if (_sessionValidation is not { } session) throw new InvalidOperationException("请先验证当前账号的会话。");
            if (!PrepareScheduledEdit()) return;
            var appointment = ReadScheduledEditor();
            if (_realOperations.HasUnresolvedOperation) throw new InvalidOperationException("请先核对中断的真实操作。");
            if (!appointment.RealOperationsAuthorized) throw new InvalidOperationException("请先勾选真实选退课授权。");
            if (appointment.ExecuteAt <= DateTimeOffset.Now) throw new InvalidOperationException("预约执行时间必须晚于当前时间。");
            if (!ValidateCourseTasks(_scheduledTasks, out var error))
            {
                ScheduledTasksGrid.Items.Refresh();
                throw new InvalidOperationException(error);
            }
            var changedAccount = appointment.StudentId != 0 && appointment.StudentId != session.StudentId;
            appointment.StudentId = session.StudentId;
            var summary = $"北京时间：{TimeZoneInfo.ConvertTime(appointment.ExecuteAt, ScheduledTimeZone):yyyy-MM-dd HH:mm:ss}\n授权账号：{appointment.StudentId}" +
                (changedAccount ? "（将改用当前已验证账号，重新授权）" : string.Empty) +
                $"\n轮次：到点使用左侧当前开放轮次；未选择时使用第一个开放轮次，无开放轮次则失败\n错过后补执行：{(appointment.RunWhenMissed ? "是" : "否")}\n完成方式：{(appointment.CompletionAction == ScheduledCompletionAction.Stop ? "执行一轮后停止" : "剩余课程导入监测")}\n\n" +
                string.Join("\n", appointment.Tasks.Where(task => task.Enabled).Select(task => $"{task.Priority}. {task.CourseCode}；冲突旧课：{task.ConflictsText}")) +
                "\n\n到点将执行一次真实选退课，不再弹出确认。是否启用？";
            if (MessageBox.Show(this, summary, "确认启用预约", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _scheduledService.Arm(appointment, DateTimeOffset.Now);
            _automaticWritesPaused = false;
            _scheduledResumeScan = true;
            _scheduledTimer.Start();
            AddActivity($"预约已启用：{appointment.ExecuteAt:yyyy-MM-dd HH:mm:ss zzz}", "预约", ActivityEventKind.ScheduledSelection);
            _scheduledEditorDirty = false;
            RefreshScheduledUi();
        });
    }

    private void ScheduledTaskInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingScheduledEditor || sender is not TextBox { DataContext: AutomationTask task, IsKeyboardFocusWithin: true } || !_scheduledTasks.Contains(task)) return;
        if (PrepareScheduledEdit()) _scheduledEditorDirty = true;
    }

    private void ScheduledTaskInput_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox { DataContext: AutomationTask task }) return;
        ScheduledTasksGrid.SelectedItem = task;
        ScheduledTasksGrid.CurrentItem = task;
        ScheduledTasksGrid.ScrollIntoView(task);
    }

    private void ScheduledEnabledCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: AutomationTask task } checkBox || !PrepareScheduledEdit()) return;
        task.Enabled = checkBox.IsChecked == true;
        ScheduledTasksGrid.SelectedItem = task;
        ScheduledTasksGrid.CurrentItem = task;
        _scheduledEditorDirty = true;
    }

    private void ScheduledTasksGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || FindVisualAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null) return;
        DeleteScheduledTaskButton_Click(sender, e);
        e.Handled = true;
    }

    private void AddScheduledTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareScheduledEdit()) return;
        var task = new AutomationTask { Priority = _scheduledTasks.Select(item => item.Priority).DefaultIfEmpty().Max() + 1 };
        _scheduledTasks.Add(task);
        ScheduledTasksGrid.SelectedItem = task;
        ScheduledTasksGrid.CurrentItem = task;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_scheduledTasks.Contains(task) || !ReferenceEquals(ScheduledTasksGrid.SelectedItem, task)) return;
            ScheduledTasksGrid.ScrollIntoView(task);
            ScheduledTasksGrid.UpdateLayout();
            if (ScheduledTasksGrid.ItemContainerGenerator.ContainerFromItem(task) is DataGridRow row
                && FindVisualDescendant<TextBox>(row) is { } input)
            {
                input.Focus();
                Keyboard.Focus(input);
                input.SelectAll();
            }
        }, System.Windows.Threading.DispatcherPriority.Input);
        _scheduledEditorDirty = true;
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
        _scheduledEditorDirty = true;
        ScheduledTasksGrid.Items.Refresh();
    }
}
