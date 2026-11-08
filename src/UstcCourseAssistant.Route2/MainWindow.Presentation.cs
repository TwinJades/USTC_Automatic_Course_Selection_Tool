using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using UstcCourseAssistant.Core.Services;

namespace UstcCourseAssistant.Route2;

public partial class MainWindow
{
    private readonly FlowDocument _allActivityDocument = new() { PagePadding = new Thickness(0) };
    private readonly FlowDocument _keyActivityDocument = new() { PagePadding = new Thickness(0) };
    private readonly Paragraph _allActivityParagraph = new() { Margin = new Thickness(0) };
    private readonly Paragraph _keyActivityParagraph = new() { Margin = new Thickness(0) };
    private List<LocalLogEntry> _cachedActivity = [];

    private async Task RefreshLogsAsync()
    {
        var reply = await Send("logs", new { Count = 1000 });
        if (reply.Data is not { } data) return;
        var entries = data.Deserialize<List<LocalLogEntry>>() ?? [];
        if (!_cachedActivity.SequenceEqual(entries))
        {
            _cachedActivity = entries;
            if (_allActivityDocument.Blocks.Count == 0) _allActivityDocument.Blocks.Add(_allActivityParagraph);
            if (_keyActivityDocument.Blocks.Count == 0) _keyActivityDocument.Blocks.Add(_keyActivityParagraph);
            _allActivityParagraph.Inlines.Clear();
            _keyActivityParagraph.Inlines.Clear();
            foreach (var entry in entries)
            {
                AppendActivity(_allActivityParagraph, entry);
                if (entry.IsKeyEvent) AppendActivity(_keyActivityParagraph, entry);
            }
        }
        ShowActivity();
    }
    private static void AppendActivity(Paragraph paragraph, LocalLogEntry entry)
    {
        if (paragraph.Inlines.Count != 0) paragraph.Inlines.Add(new LineBreak());
        var run = new Run(entry.DisplayText);
        if (entry.IsEmphasized) run.FontWeight = FontWeights.Bold;
        paragraph.Inlines.Add(run);
    }
    private void ShowActivity()
    {
        if (_allActivityDocument.Blocks.Count == 0) _allActivityDocument.Blocks.Add(_allActivityParagraph);
        if (_keyActivityDocument.Blocks.Count == 0) _keyActivityDocument.Blocks.Add(_keyActivityParagraph);
        ActivityLogViewer.Document = _allLogs ? _allActivityDocument : _keyActivityDocument;
        ActivityEmptyText.Visibility = (_allLogs ? _cachedActivity.Count == 0 : !_cachedActivity.Any(x => x.IsKeyEvent)) ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void ImportantActivityButton_Click(object sender, RoutedEventArgs e) { _allLogs = false; ShowActivity(); await RefreshLogsAsync(); }
    private async void AllActivityButton_Click(object sender, RoutedEventArgs e) { _allLogs = true; ShowActivity(); await RefreshLogsAsync(); }
    private void ShowGuide()
    {
        OperationTabs.SelectedIndex = 0;
        _guideStep = 0; OnboardingOverlay.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(new Action(RenderGuide), System.Windows.Threading.DispatcherPriority.Render);
    }
    private void RenderGuide()
    {
        if (OnboardingOverlay.Visibility != Visibility.Visible || OnboardingOverlay.ActualWidth <= 0 || OnboardingOverlay.ActualHeight <= 0) return;
        OperationTabs.SelectedIndex = 0; OperationTabs.UpdateLayout(); AutomationTasksGrid.UpdateLayout();
        var codeHeader = FindGuideVisual<DataGridColumnHeader>(AutomationTasksGrid, header => ReferenceEquals(header.Column, AutomationCourseCodeColumn));
        var enabledHeader = FindGuideVisual<DataGridColumnHeader>(AutomationTasksGrid, header => ReferenceEquals(header.Column, AutomationTasksGrid.Columns[0]));
        var (title, body, target) = _guideStep switch
        {
            0 => ("打开教务系统", "日常使用时先在独立教务窗口完成登录。新手指引只作说明，不会替你打开网页或提交任何信息。", (FrameworkElement)OpenEamsButton),
            1 => ("添加课程任务", "点击“添加课程”，输入完整教学班代码后确认，即可新增任务。", (FrameworkElement)AddAutomationTaskButton),
            2 => ("填写教学班代码", "教学班代码单击即可直接输入。请填写完整代码，例如 001683EX.01。", (FrameworkElement?)codeHeader ?? AutomationTasksGrid),
            3 => ("启用需要监测的课程", "启用复选框一次点击即可切换。只有已经启用的任务才会参加检查和监测。", (FrameworkElement?)enabledHeader ?? AutomationTasksGrid),
            4 => ("先做只读检查", "“立即检查（不选课）”只核对课程、余量和冲突课，不会提交选课或退课。", (FrameworkElement)CheckAutomationNowButton),
            _ => ("确认后再开始监测", "黄色区域允许真实选退课。核对无误后仍需由你本人勾选确认、点击“开始监测”并二次确认；引导不会替你执行。", (FrameworkElement)AutomationArmPanel)
        };
        OnboardingStepText.Text = $"第 {_guideStep + 1} / 6 步"; OnboardingTitleText.Text = title; OnboardingBodyText.Text = body;
        OnboardingPreviousButton.IsEnabled = _guideStep > 0; OnboardingNextButton.Content = _guideStep == 5 ? "完成" : "下一步";
        Rect bounds;
        try { bounds = target.TransformToVisual(OnboardingOverlay).TransformBounds(new Rect(new Size(target.ActualWidth, target.ActualHeight))); }
        catch (InvalidOperationException) { bounds = new Rect(OnboardingOverlay.ActualWidth / 2 - 120, OnboardingOverlay.ActualHeight / 2 - 30, 240, 60); }
        bounds.Inflate(7, 7); bounds.Intersect(new Rect(0, 0, OnboardingOverlay.ActualWidth, OnboardingOverlay.ActualHeight));
        if (bounds.IsEmpty) bounds = new Rect(0, 0, 0, 0);
        SetGuideRect(OnboardingMaskTop, 0, 0, OnboardingOverlay.ActualWidth, bounds.Top);
        SetGuideRect(OnboardingMaskLeft, 0, bounds.Top, bounds.Left, bounds.Height);
        SetGuideRect(OnboardingMaskRight, bounds.Right, bounds.Top, OnboardingOverlay.ActualWidth - bounds.Right, bounds.Height);
        SetGuideRect(OnboardingMaskBottom, 0, bounds.Bottom, OnboardingOverlay.ActualWidth, OnboardingOverlay.ActualHeight - bounds.Bottom);
        SetGuideRect(OnboardingSpotlightBorder, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        OnboardingCallout.Measure(new Size(360, OnboardingOverlay.ActualHeight));
        var height = OnboardingCallout.DesiredSize.Height;
        var left = Math.Clamp(bounds.Left + bounds.Width / 2 - 180, 14, Math.Max(14, OnboardingOverlay.ActualWidth - 374));
        var below = bounds.Bottom + 18 + height <= OnboardingOverlay.ActualHeight - 12;
        var top = below ? bounds.Bottom + 18 : bounds.Top - height - 18;
        if (top < 12) top = Math.Max(12, OnboardingOverlay.ActualHeight / 2 - height / 2);
        Canvas.SetLeft(OnboardingCallout, left); Canvas.SetTop(OnboardingCallout, top); OnboardingArrowText.Text = below ? "↑" : "↓";
    }
    private static void SetGuideRect(FrameworkElement element, double left, double top, double width, double height)
    {
        Canvas.SetLeft(element, left); Canvas.SetTop(element, top); element.Width = Math.Max(0, width); element.Height = Math.Max(0, height);
    }
    private static T? FindGuideVisual<T>(DependencyObject root, Predicate<T> predicate) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && predicate(match)) return match;
            if (FindGuideVisual(child, predicate) is { } nested) return nested;
        }
        return null;
    }
    private void HideGuide() { OnboardingOverlay.Visibility = Visibility.Collapsed; _preferences = _preferences with { OnboardingCompleted = true }; _preferences.Save(); }
    private void OnboardingHelpButton_Click(object sender, RoutedEventArgs e) => ShowGuide();
    private void OnboardingSkipButton_Click(object sender, RoutedEventArgs e) => HideGuide();
    private void OnboardingPreviousButton_Click(object sender, RoutedEventArgs e) { if (_guideStep > 0) { _guideStep--; RenderGuide(); } }
    private void OnboardingNextButton_Click(object sender, RoutedEventArgs e) { if (_guideStep == 5) HideGuide(); else { _guideStep++; RenderGuide(); } }
    private void OnboardingOverlay_SizeChanged(object sender, SizeChangedEventArgs e) { if (OnboardingOverlay.Visibility == Visibility.Visible) RenderGuide(); }
}
