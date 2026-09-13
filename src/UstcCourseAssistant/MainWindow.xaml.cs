using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace UstcCourseAssistant;

public partial class MainWindow : Window
{
    private readonly LocalStore _store = new();
    private readonly ObservableCollection<CourseTask> _tasks;
    private readonly ObservableCollection<string> _logs = [];
    private readonly DispatcherTimer _timer = new();
    private AppSettings _settings;
    private bool _loading = true;
    private bool _checking;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _store.Load();
        _tasks = new ObservableCollection<CourseTask>(_settings.Tasks);
        TasksGrid.ItemsSource = _tasks;
        LogBox.ItemsSource = _logs;
        LoadSettingsIntoUi();
        _timer.Tick += async (_, _) => await RunCheckAsync();
        Closing += (_, _) => SaveSettings();
        _loading = false;
        WriteLog("信息", "程序已启动。本机任务尚未开始运行。");
    }

    private void LoadSettingsIntoUi()
    {
        StudentIdBox.Text = _settings.StudentId;
        CustomIntervalBox.Text = _settings.IntervalSeconds.ToString();
        IntervalBox.SelectedIndex = _settings.IntervalSeconds switch { 5 => 0, 15 => 1, 60 => 2, _ => 3 };
        ScheduleBox.IsChecked = _settings.EnableSchedule;
        StartTimeBox.Text = _settings.StartAt.ToString("HH:mm");
        EndTimeBox.Text = _settings.EndAt.ToString("HH:mm");
        WeekdaysBox.IsChecked = _settings.WeekdaysOnly;
        StartupBox.IsChecked = _settings.RunAtStartup;
    }

    private void SettingsChanged(object sender, RoutedEventArgs e) => SaveSettings();
    private void SettingsChanged(object sender, TextChangedEventArgs e) => SaveSettings();

    private void SaveSettings()
    {
        if (_loading) return;
        _settings.StudentId = StudentIdBox.Text.Trim();
        _settings.IntervalSeconds = GetInterval();
        _settings.EnableSchedule = ScheduleBox.IsChecked == true;
        _settings.WeekdaysOnly = WeekdaysBox.IsChecked == true;
        _settings.RunAtStartup = StartupBox.IsChecked == true;
        if (TimeOnly.TryParse(StartTimeBox.Text, out var start)) _settings.StartAt = start;
        if (TimeOnly.TryParse(EndTimeBox.Text, out var end)) _settings.EndAt = end;
        _settings.Tasks = _tasks.OrderBy(t => t.Priority).ToList();
        _store.Save(_settings);
    }

    private int GetInterval() => IntervalBox.SelectedIndex switch
    {
        0 => 5, 1 => 15, 2 => 60,
        _ => int.TryParse(CustomIntervalBox.Text, out var seconds) ? Math.Clamp(seconds, 5, 3600) : 60
    };

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        _timer.Interval = TimeSpan.FromSeconds(GetInterval());
        _timer.Start();
        StateText.Text = $"监测中（每 {GetInterval()} 秒）";
        WriteLog("信息", "已开始监测。");
        await RunCheckAsync(selectWhenAvailable: true);
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        StateText.Text = "已暂停";
        WriteLog("信息", "监测已暂停。");
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e) => await RunCheckAsync(selectWhenAvailable: false);

    private async Task RunCheckAsync(bool selectWhenAvailable = true)
    {
        if (_checking || !IsInSchedule()) return;
        _checking = true;
        try
        {
            var engine = new SelectionEngine(new WebViewCourseSelectionAdapter(), WriteLog);
            await engine.CheckOnceAsync(_tasks, CancellationToken.None, selectWhenAvailable);
        }
        catch (Exception ex)
        {
            _timer.Stop();
            StateText.Text = "已暂停：需要处理";
            MessageBox.Show(ex.Message, "需要完成适配或处理异常", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _checking = false; TasksGrid.Items.Refresh(); SaveSettings(); }
    }

    private bool IsInSchedule()
    {
        if (!_settings.EnableSchedule) return true;
        var now = DateTime.Now;
        if (_settings.WeekdaysOnly && now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        var time = TimeOnly.FromDateTime(now);
        return _settings.StartAt <= _settings.EndAt
            ? time >= _settings.StartAt && time <= _settings.EndAt
            : time >= _settings.StartAt || time <= _settings.EndAt;
    }

    private void OpenLogin_Click(object sender, RoutedEventArgs e)
    {
        if (LoginSession.Window is { } window) { window.Show(); window.Activate(); }
        else new LoginWindow().Show();
        WriteLog("信息", "已打开内置教务登录窗口，请自行完成认证。");
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        _tasks.Add(new CourseTask { Priority = _tasks.Count == 0 ? 1 : _tasks.Max(t => t.Priority) + 1 });
        SaveSettings();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (TasksGrid.SelectedItem is CourseTask selected) _tasks.Remove(selected);
        RenumberPriorities(); SaveSettings();
    }

    private void Up_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => MoveSelected(1);
    private void MoveSelected(int direction)
    {
        if (TasksGrid.SelectedItem is not CourseTask selected) return;
        var index = _tasks.IndexOf(selected); var next = index + direction;
        if (next < 0 || next >= _tasks.Count) return;
        _tasks.Move(index, next); RenumberPriorities(); SaveSettings();
    }

    private void RenumberPriorities()
    {
        for (var i = 0; i < _tasks.Count; i++) _tasks[i].Priority = i + 1;
        TasksGrid.Items.Refresh();
    }

    private void WriteLog(AppLog log) => WriteLog(log.Level, log.Message);
    private void WriteLog(string level, string message)
    {
        var item = new AppLog(DateTimeOffset.Now, level, message);
        _store.AppendLog(item);
        _logs.Insert(0, $"{item.At:MM-dd HH:mm:ss} [{level}] {message}");
    }
}
