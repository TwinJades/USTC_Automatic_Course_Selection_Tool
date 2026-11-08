using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UstcCourseAssistant.Core.Models;

namespace UstcCourseAssistant.Route2;

public partial class MainWindow
{
    private bool _appointmentEditing;
    private async void ScheduledEditorPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindInput(source) && RequiresAppointmentDisarm())
        { e.Handled = true; await DisarmAppointmentForEditAsync(); }
    }
    private async void ScheduledEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Tab or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl) return;
        if (e.OriginalSource is DependencyObject source && FindInput(source) && RequiresAppointmentDisarm())
        { e.Handled = true; await DisarmAppointmentForEditAsync(); }
    }
    private static bool FindInput(DependencyObject? value)
    {
        while (value is not null)
        {
            if (value is TextBox or DatePicker or ComboBox or CheckBox) return true;
            value = System.Windows.Media.VisualTreeHelper.GetParent(value);
        }
        return false;
    }
    private bool RequiresAppointmentDisarm() => _controller.Snapshot.Appointment?.State is ScheduledSelectionState.Armed or ScheduledSelectionState.Queued or ScheduledSelectionState.Running;
    private async Task DisarmAppointmentForEditAsync()
    {
        if (_appointmentEditing) return;
        _appointmentEditing = true;
        try
        {
            var reply = await Send("appointment edit");
            ScheduledStatusText.Text = reply.Success ? "预约已暂停并转为草稿，请再次操作以编辑。" : reply.Message;
            if (reply.Success) { _loading = true; ScheduledAuthorizationCheckBox.IsChecked = false; _loading = false; }
        }
        finally { _appointmentEditing = false; }
    }
    private void AppointmentChanged() { if (!_loading) _appointmentDirty = true; }
    private void ScheduledTaskInput_TextChanged(object sender, TextChangedEventArgs e) { if (sender is TextBox { IsKeyboardFocusWithin: true }) AppointmentChanged(); }
    private void ScheduledTaskInput_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: AutomationTask task }) ScheduledTasksGrid.SelectedItem = task;
    }
    private void ScheduledEnabledCheckBox_Click(object sender, RoutedEventArgs e) => AppointmentChanged();
    private async void ScheduledTasksGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Keyboard.FocusedElement is TextBox || ScheduledTasksGrid.SelectedItem is not AutomationTask task) return;
        e.Handled = true;
        if (RequiresAppointmentDisarm())
        {
            await DisarmAppointmentForEditAsync();
            if (RequiresAppointmentDisarm()) return;
        }
        _appointmentTasks.Remove(task); AppointmentChanged();
    }
    private async Task<bool> PrepareAppointmentMutationAsync()
    {
        if (!RequiresAppointmentDisarm()) return true;
        await DisarmAppointmentForEditAsync();
        return !RequiresAppointmentDisarm();
    }
    private async void AddScheduledTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await PrepareAppointmentMutationAsync()) return;
        _appointmentTasks.Add(new AutomationTask { Priority = _appointmentTasks.Count == 0 ? 1 : _appointmentTasks.Max(x => x.Priority) + 1 }); AppointmentChanged();
    }
    private async void DeleteScheduledTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduledTasksGrid.SelectedItem is not AutomationTask task || !await PrepareAppointmentMutationAsync()) return;
        _appointmentTasks.Remove(task); AppointmentChanged();
    }
    private async void MoveScheduledTaskUpButton_Click(object sender, RoutedEventArgs e) { if (await PrepareAppointmentMutationAsync()) MoveAppointment(-1); }
    private async void MoveScheduledTaskDownButton_Click(object sender, RoutedEventArgs e) { if (await PrepareAppointmentMutationAsync()) MoveAppointment(1); }
    private void MoveAppointment(int direction)
    {
        if (ScheduledTasksGrid.SelectedItem is not AutomationTask task) return;
        int index = _appointmentTasks.IndexOf(task), next = index + direction;
        if (next < 0 || next >= _appointmentTasks.Count) return;
        _appointmentTasks.Move(index, next);
        for (int i = 0; i < _appointmentTasks.Count; i++) _appointmentTasks[i].Priority = i + 1;
        ScheduledTasksGrid.Items.Refresh(); AppointmentChanged();
    }
    private async Task<bool> SaveAppointmentAsync()
    {
        if (ScheduledDatePicker.SelectedDate is not DateTime date || !TimeOnly.TryParse(ScheduledTimeText.Text, out var time)) { ScheduledStatusText.Text = "请输入有效日期和时间。"; return false; }
        var existing = _controller.Snapshot.Appointment;
        if (existing is not null && existing.State != ScheduledSelectionState.Draft)
        {
            var edit = await Send("appointment edit"); if (!edit.Success) { ScheduledStatusText.Text = edit.Message; return false; }
        }
        var at = date.Date.Add(time.ToTimeSpan()).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var reply = await Send("appointment save", new { At = at, RunWhenMissed = ScheduledRunWhenMissedCheckBox.IsChecked == true, Completion = ScheduledCompletionSelector.SelectedIndex == 1 ? "monitor" : "stop", Tasks = _appointmentTasks.ToArray() });
        ScheduledStatusText.Text = reply.Message;
        if (reply.Success) _appointmentDirty = false;
        return reply.Success;
    }
    private async void SaveScheduledButton_Click(object sender, RoutedEventArgs e) => await SaveAppointmentAsync();
    private async void ArmScheduledButton_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduledAuthorizationCheckBox.IsChecked != true) { ScheduledStatusText.Text = "请明确授权预约真实操作。"; return; }
        if (_controller.Snapshot.Session is null) { ScheduledStatusText.Text = "请先登录并验证教务会话。"; return; }
        if (!await SaveAppointmentAsync()) return;
        if (MessageBox.Show(this, "确认授权此预约在指定时间执行一次真实选退课及所选完成方式？", "预约授权确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var turn = (TurnSelector.SelectedItem as TurnInfo)?.Id;
        var reply = turn.HasValue ? await Send("appointment arm", new { Authorize = true, TurnId = turn.Value }) : await Send("appointment arm", new { Authorize = true });
        ScheduledStatusText.Text = reply.Message;
    }
    private async void CancelScheduledButton_Click(object sender, RoutedEventArgs e) { var result = await Send("appointment cancel"); if (result.Success) _appointmentDirty = false; ScheduledStatusText.Text = result.Message; }
}
