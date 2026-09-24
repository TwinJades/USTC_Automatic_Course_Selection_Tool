using UstcCourseAssistant.Route2.Models;
using UstcCourseAssistant.Route2.Services;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Text.Json;
using IOPath = System.IO.Path;

Environment.SetEnvironmentVariable(
    "USTC_ROUTE2_TEST_DATA_ROOT",
    IOPath.Combine(IOPath.GetTempPath(), "ustc-route2-ui-check-" + Guid.NewGuid().ToString("N")));

try
{
    var skipUi = args.Contains("--skip-ui", StringComparer.OrdinalIgnoreCase);
    if (skipUi)
    {
        Console.WriteLine("[跳过] 界面状态回归（CI 托管环境不提供稳定的 WPF 布局与性能条件）");
    }
    else
    {
        RunCheck("界面状态回归", RunUiStateRegressionChecks);
    }

    RunCheck("自动登录脚本安全", CheckAutoLoginScriptSafety);
    RunCheck("偏好设置升级兼容", CheckPreferenceUpgradeCompatibility);
    RunCheck("检查间隔抖动边界", CheckIntervalJitterBounds);
    RunCheck("定时延迟与网络分类", CheckTimerDelayAndNetworkClassification);
    RunCheck("原子配置恢复", CheckAtomicConfigurationRecovery);
    RunCheck("日志脱敏与保留期", CheckLogSanitizationAndRetention);
    await RunCheckAsync("中断操作保护", CheckInterruptedOperationProtectionAsync);
    await RunCheckAsync("严格换课恢复", CheckStrictInterruptedSwapRecoveryAsync);
    RunCheck("损坏事务记录恢复", CheckCorruptJournalRecovery);
    await RunCheckAsync("无余量时禁止写入", NoSeatNeverWritesAsync);
    await RunCheckAsync("成功换课严格串行", SuccessfulSwapIsStrictlySerialAsync);
    await RunCheckAsync("目标失败时恢复旧课", FailedTargetRestoresOldCourseAsync);
    await RunCheckAsync("重复目标禁止写入", DuplicateTargetNeverWritesAsync);
    await RunCheckAsync("高优先级成功后禁用冲突任务", HigherPrioritySelectionDisablesConflictingLowerTaskAsync);
    Console.WriteLine("路线二任务引擎离线场景检查全部通过。");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}

static void RunCheck(string name, Action check)
{
    Console.WriteLine($"[检查] {name}");
    check();
}

static async Task RunCheckAsync(string name, Func<Task> check)
{
    Console.WriteLine($"[检查] {name}");
    await check();
}

static void RunUiStateRegressionChecks()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        UstcCourseAssistant.Route2.App? app = null;
        UstcCourseAssistant.Route2.MainWindow? window = null;
        UstcCourseAssistant.Route2.LoginWindow? loginWindow = null;
        try
        {
            app = new UstcCourseAssistant.Route2.App();
            app.InitializeComponent();
            window = new UstcCourseAssistant.Route2.MainWindow();
            loginWindow = new UstcCourseAssistant.Route2.LoginWindow();
            var windowType = window.GetType();
            var runningField = windowType.GetField("_automationRunning", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var sessionField = windowType.GetField("_sessionValidation", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var turnsField = windowType.GetField("_turns", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var recoveryPendingField = windowType.GetField("_readNetworkRecoveryPending", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var plannedAtField = windowType.GetField("_automationTickPlannedAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var plannedDelayField = windowType.GetField("_automationTickPlannedDelay", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var settingsField = windowType.GetField("_automationSettings", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var tasksField = windowType.GetField("_automationTasks", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var shouldAutoShowOnboardingField = windowType.GetField("_shouldAutoShowOnboarding", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var setBusy = windowType.GetMethod("SetBusy", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var pause = windowType.GetMethod("PauseAutomation", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var runCycle = windowType.GetMethod("RunAutomationCycleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var beginReadNetworkWait = windowType.GetMethod("BeginReadNetworkRecoveryWait", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var timerTick = windowType.GetMethod("AutomationTimer_Tick", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var enabledClick = windowType.GetMethod("AutomationEnabledCheckBox_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var deleteTask = windowType.GetMethod("DeleteAutomationTaskButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var moveTask = windowType.GetMethod("MoveAutomationTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var saveCredential = windowType.GetMethod("SaveCredentialButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var showOnboarding = windowType.GetMethod("ShowOnboarding", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var updateOnboarding = windowType.GetMethod("UpdateOnboardingStep", BindingFlags.Instance | BindingFlags.NonPublic)!;

            Assert(!(bool)runningField.GetValue(window)!, "程序启动后不得自动开始监测");
            Assert(window.Icon is not null && loginWindow.Icon is not null, "主窗口或教务窗口没有加载统一图标");
            Assert(window.MinWidth >= 1040 && window.MinHeight >= 700, "主窗口最小尺寸约束发生变化");

            var operationTabs = (TabControl)window.FindName("OperationTabs");
            var topHeaders = operationTabs.Items.Cast<TabItem>().Select(item => item.Header?.ToString()).ToArray();
            Assert(topHeaders.SequenceEqual(["任务监测", "活动记录", "调试工具", "账号与安全"]), "最终主导航结构不正确");
            var advancedTabs = (TabControl)window.FindName("AdvancedToolsTabs");
            var advancedHeaders = advancedTabs.Items.Cast<TabItem>().Select(item => item.Header?.ToString()).ToArray();
            Assert(advancedHeaders.SequenceEqual(["课程影子", "单门受控操作"]), "调试工具子页面结构不正确");

            var version = (TextBox)window.FindName("VersionText");
            var unofficial = (TextBox)window.FindName("UnofficialBadgeText");
            var title = (TextBox)window.FindName("MainTitleText");
            Assert(title.Text == "USTC 选课助手", "顶部主标题不正确");
            Assert(version.Text == "2.1.0", "顶部没有显示 2.1.0 版本号");
            Assert(unofficial.Text == "学生自制工具", "学生自制工具标签缺失");
            Assert(window.FindName("HeaderAppIcon") is null, "蓝色横幅仍然含有大 Logo");

            var topBar = (Border)window.FindName("TopTeachingSystemBar");
            var mainColumns = (Grid)window.FindName("MainContentColumns");
            var sessionPanel = (Border)window.FindName("SessionInfoPanel");
            var functionPanel = (Border)window.FindName("FunctionPagesPanel");
            Assert(System.Windows.Controls.Grid.GetRow(topBar) == 1, "教务系统操作区没有恢复到顶部操作条");
            Assert(mainColumns.ColumnDefinitions.Count == 3
                   && System.Windows.Controls.Grid.GetColumn(sessionPanel) == 0
                   && System.Windows.Controls.Grid.GetColumn(functionPanel) == 2,
                "会话信息与功能页面没有恢复为左、右两栏");
            var armPanel = (Border)window.FindName("AutomationArmPanel");
            var armCheckBox = (CheckBox)window.FindName("AutomationArmCheckBox");
            var controlPanel = (StackPanel)window.FindName("AutomationControlPanel");
            var controlOrder = controlPanel.Children.Cast<System.Windows.FrameworkElement>().Select(item => item.Name).ToArray();
            Assert(controlOrder.SequenceEqual([
                    "StartAutomationButton",
                    "PauseAutomationButton",
                    "CheckAutomationNowButton",
                    "AutomationStateText"
                ]),
                "任务控制区不是开始、暂停、立即检查、任务状态的顺序");
            foreach (var name in new[]
                     {
                         "StatusText",
                         "StudentIdText",
                         "NoTurnsText",
                         "AutomationStateText",
                         "SnapshotSummaryText",
                         "MissingTargetText",
                         "ControlledDetailsText",
                         "ControlledResultText"
                     })
            {
                var text = (TextBox)window.FindName(name);
                Assert(text.IsReadOnly, $"文字控件 {name} 不能直接选择复制");
                Assert(!text.IsReadOnlyCaretVisible, $"只读文字控件 {name} 仍会显示插入光标");
            }
            var activityLog = (FlowDocumentScrollViewer)window.FindName("ActivityLogViewer");
            Assert(activityLog.IsSelectionEnabled && activityLog.Cursor == System.Windows.Input.Cursors.Arrow,
                "活动记录必须可跨行选择复制且不能显示插入光标");
            Assert(!title.IsReadOnlyCaretVisible && title.Cursor == System.Windows.Input.Cursors.Arrow,
                "可复制标题仍表现得像可编辑输入框");

            var automationGrid = (DataGrid)window.FindName("AutomationTasksGrid");
            Assert(automationGrid.ClipboardCopyMode == DataGridClipboardCopyMode.IncludeHeader,
                "任务表不再支持 Ctrl+C 复制");
            Assert(automationGrid.HeadersVisibility == DataGridHeadersVisibility.All
                   && automationGrid.RowHeaderWidth is >= 22 and <= 26,
                "任务表左侧行选择区域不是约 22～26 像素");
            Assert(automationGrid.SelectionUnit == DataGridSelectionUnit.FullRow
                   && automationGrid.SelectionMode == DataGridSelectionMode.Single,
                "任务表没有恢复单选整行模式");
            Assert(!automationGrid.CanUserDeleteRows,
                "任务表仍允许 DataGrid 默认 Delete 逻辑绕过焦点分流");
            Assert(automationGrid.Columns[0] is DataGridTemplateColumn,
                "启用列没有使用可一次点击切换的复选框模板");
            Assert(automationGrid.Columns[1] is DataGridTemplateColumn
                   && automationGrid.Columns[4] is DataGridTemplateColumn,
                "教学班代码或冲突旧课没有改成单击即输入的模板列");
            Assert(automationGrid.RowHeight is >= 32 and <= 35 && Math.Abs(automationGrid.RowHeight - 34) < 0.1,
                "任务行高没有调整为 34 个逻辑像素");
            foreach (var column in automationGrid.Columns.OfType<DataGridTextColumn>())
            {
                Assert(HasSetter(column.ElementStyle, System.Windows.FrameworkElement.VerticalAlignmentProperty,
                        System.Windows.VerticalAlignment.Center),
                    $"任务表文字列“{column.Header}”没有明确垂直居中");
            }
            foreach (var columnIndex in new[] { 1, 4 })
            {
                var directInput = (TextBox)((DataGridTemplateColumn)automationGrid.Columns[columnIndex]).CellTemplate.LoadContent();
                Assert(directInput.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.UpdateSourceTrigger
                       == System.Windows.Data.UpdateSourceTrigger.PropertyChanged,
                    $"任务表第 {columnIndex} 列没有即时更新输入内容");
                directInput.IsEnabled = false;
                directInput.ApplyTemplate();
                var inputBorder = (Border?)directInput.Template.FindName("InputBorder", directInput);
                Assert(inputBorder?.Background is SolidColorBrush background && background.Color.A == 0
                       && inputBorder.BorderThickness == new System.Windows.Thickness(0),
                    $"任务表第 {columnIndex} 列在监测锁定时仍显示整块禁用输入框");
            }
            Assert(((DataGrid)window.FindName("TargetsGrid")).ClipboardCopyMode == DataGridClipboardCopyMode.IncludeHeader,
                "课程影子表不再支持 Ctrl+C 复制");
            Assert(((TextBox)loginWindow.FindName("BrowserStatusText")).IsReadOnly,
                "教务窗口状态文字不能选择复制");
            var loginWindowType = loginWindow.GetType();
            var suppressionField = loginWindowType.GetField("_autoLoginSuppressed", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var suppressionMessageField = loginWindowType.GetField("_autoLoginSuppressionMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
            loginWindow.ConfigureAutoLogin(true, () => null);
            loginWindow.Logout();
            Assert((bool)suppressionField.GetValue(loginWindow)!
                   && suppressionMessageField.GetValue(loginWindow)?.ToString()?.Contains("主动退出", StringComparison.Ordinal) == true,
                "主动退出后没有保留明确的自动登录抑制原因");
            loginWindow.ConfigureAutoLogin(true, () => null);
            Assert((bool)suppressionField.GetValue(loginWindow)!,
                "更新凭据或重复配置不应悄悄解除主动退出抑制");
            loginWindow.ConfigureAutoLogin(false, null);
            loginWindow.ConfigureAutoLogin(true, () => null, releaseUserSuppression: true);
            Assert(!(bool)suppressionField.GetValue(loginWindow)!,
                "取消并重新启用自动登录后没有解除主动退出抑制");
            Assert(window.FindName("SavedAccountTextBox") is TextBox
                   && window.FindName("SavedPasswordBox") is PasswordBox
                   && window.FindName("AutoLoginCheckBox") is CheckBox
                   && window.FindName("SaveCredentialButton") is Button
                   && window.FindName("DeleteCredentialButton") is Button,
                "账号与安全页缺少必要控件");
            Assert(((TextBox)window.FindName("CredentialStatusText")).IsReadOnly,
                "凭据状态文字不能选择复制");
            Assert(((CheckBox)window.FindName("AutoLoginCheckBox")).IsChecked != true,
                "自动登录必须默认关闭");

            window.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = -20000;
            window.ShowInTaskbar = false;
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            window.Show();
            window.UpdateLayout();
            Assert((bool)shouldAutoShowOnboardingField.GetValue(window)!,
                "全新用户没有被标记为需要首次展示新手指引");
            showOnboarding.Invoke(window, null);
            window.UpdateLayout();
            updateOnboarding.Invoke(window, null);
            var onboardingOverlay = (Canvas)window.FindName("OnboardingOverlay");
            var onboardingHelp = (Button)window.FindName("OnboardingHelpButton");
            Assert(onboardingHelp.ToolTip?.ToString() == "查看新手指引",
                "左下角问号的悬停文字不正确");
            Assert(onboardingOverlay.Visibility == System.Windows.Visibility.Visible,
                "全新用户首次启动时没有显示新手指引");
            Assert(((TextBox)window.FindName("OnboardingStepText")).Text == "第 1 / 6 步",
                "新手指引不是六步或没有从第一步开始");
            ((Button)window.FindName("OnboardingSkipButton")).RaiseEvent(
                new System.Windows.RoutedEventArgs(Button.ClickEvent));
            Assert(onboardingOverlay.Visibility == System.Windows.Visibility.Collapsed,
                "跳过后新手指引没有关闭");
            onboardingHelp.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            Assert(onboardingOverlay.Visibility == System.Windows.Visibility.Visible,
                "左下角问号不能重新打开新手指引");
            ((Button)window.FindName("OnboardingSkipButton")).RaiseEvent(
                new System.Windows.RoutedEventArgs(Button.ClickEvent));
            Assert(window.ActualWidth >= window.MinWidth && window.ActualHeight >= window.MinHeight,
                "主窗口无法按最小尺寸完成布局");
            Assert(sessionPanel.ActualWidth >= 280 && functionPanel.ActualWidth >= 600,
                "最小窗口下左侧会话栏或右侧功能页可用空间不足");
            Assert(topBar.ActualHeight < 80, "教务系统操作区不再是紧凑顶部操作条");
            Assert(sessionPanel.IsAncestorOf(armPanel) && armPanel.IsAncestorOf(armCheckBox),
                "黄色真实操作确认区没有整体移动到左侧会话栏");
            var armTexts = VisualDescendants<TextBox>(armPanel).ToArray();
            Assert(armTexts.Length >= 2 && armTexts.All(text => text.IsReadOnly),
                "黄色确认区的安全提示文字不能直接选择复制");
            Assert(armTexts.Any(text => text.Text.StartsWith("检测到余量后", StringComparison.Ordinal))
                   && armTexts.All(text => !text.Text.Contains("监测检测到", StringComparison.Ordinal)),
                "黄色确认区仍含有重复用词");
            var armBottom = armPanel.TranslatePoint(new System.Windows.Point(0, armPanel.ActualHeight), sessionPanel).Y;
            Assert(armBottom <= sessionPanel.ActualHeight + 0.5,
                $"最小窗口下左侧黄色确认区被截断：底部 {armBottom:F1}，会话栏 {sessionPanel.ActualHeight:F1}");
            var armCheckBottom = armCheckBox.TranslatePoint(
                new System.Windows.Point(0, armCheckBox.ActualHeight), sessionPanel).Y;
            Assert(armCheckBox.ActualHeight >= 14 && armCheckBottom <= sessionPanel.ActualHeight - 4,
                $"最小窗口下黄色确认复选框未完整显示：高度 {armCheckBox.ActualHeight:F1}，底部 {armCheckBottom:F1}，会话栏 {sessionPanel.ActualHeight:F1}");

            var intervalSelector = (ComboBox)window.FindName("AutomationIntervalSelector");
            foreach (var name in new[] { "AutomationCustomIntervalText", "AutomationStartTimeText", "AutomationEndTimeText" })
            {
                var input = (TextBox)window.FindName(name);
                Assert(Math.Abs(input.ActualHeight - intervalSelector.ActualHeight) <= 0.5,
                    $"输入框 {name} 与检查间隔下拉框高度不一致");
                Assert(input.VerticalContentAlignment == System.Windows.VerticalAlignment.Center
                       && input.Padding.Top == input.Padding.Bottom,
                    $"输入框 {name} 的文字没有垂直居中");
                Assert(input.ActualHeight >= input.FontSize + 12,
                    $"输入框 {name} 的文字可能被截断");
            }
            var taskColumnWidths = automationGrid.Columns.Select(column => column.ActualWidth).ToArray();
            Assert(taskColumnWidths.All(width => width >= 35),
                "最小窗口下任务表存在被截断的列：" + string.Join(", ", taskColumnWidths.Select(width => width.ToString("F1"))));

            window.Width = 1240;
            window.Height = 800;
            window.UpdateLayout();
            var tasks = (IList<AutomationTask>)tasksField.GetValue(window)!;
            tasks.Clear();
            var firstTask = new AutomationTask { CourseCode = "001111.01", CourseName = "第一门", Priority = 1 };
            var secondTask = new AutomationTask { CourseCode = "002222.01", CourseName = "第二门", Priority = 2, Enabled = false };
            var thirdTask = new AutomationTask { CourseCode = "003333.01", CourseName = "第三门", Priority = 3 };
            tasks.Add(firstTask);
            tasks.Add(secondTask);
            tasks.Add(thirdTask);
            automationGrid.SelectedItem = secondTask;
            automationGrid.ScrollIntoView(secondTask);
            automationGrid.UpdateLayout();
            var selectedRow = (DataGridRow?)automationGrid.ItemContainerGenerator.ContainerFromItem(secondTask);
            Assert(selectedRow?.IsSelected == true, "普通任务行没有形成清楚的整行选择状态");
            Assert(selectedRow is not null && Math.Abs(selectedRow.ActualHeight - 34) <= 0.5,
                "任务表实际行高不是 34 个逻辑像素");

            var enabledColumn = (DataGridTemplateColumn)automationGrid.Columns[0];
            var enabledBox = (CheckBox)enabledColumn.CellTemplate.LoadContent();
            Assert(enabledBox.HorizontalAlignment == System.Windows.HorizontalAlignment.Center
                   && enabledBox.VerticalAlignment == System.Windows.VerticalAlignment.Center,
                "启用复选框没有保持水平、垂直居中");
            enabledBox.DataContext = secondTask;
            enabledBox.IsChecked = true;
            enabledBox.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateSource();
            enabledClick.Invoke(window, [enabledBox, new System.Windows.RoutedEventArgs()]);
            Assert(secondTask.Enabled && ReferenceEquals(automationGrid.SelectedItem, secondTask),
                "启用复选框没有同时切换状态并选中所在行");

            moveTask.Invoke(window, [-1]);
            Assert(ReferenceEquals(tasks[0], secondTask) && secondTask.Priority == 1,
                "优先级上移没有作用于当前整行选中任务");
            moveTask.Invoke(window, [1]);
            Assert(ReferenceEquals(tasks[1], secondTask) && secondTask.Priority == 2,
                "优先级下移没有作用于当前整行选中任务");
            deleteTask.Invoke(window, [window, new System.Windows.RoutedEventArgs()]);
            Assert(!tasks.Contains(secondTask) && tasks.Count == 2,
                "删除选中项没有作用于当前整行选中任务");
            automationGrid.SelectedItem = firstTask;

            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            window.UpdateLayout();
            operationTabs.SelectedIndex = 1;
            window.UpdateLayout();
            Assert(((FlowDocumentScrollViewer)window.FindName("ActivityLogViewer")).ActualHeight >= 100,
                "活动记录页在最小窗口下没有可用空间");
            var addActivity = windowType.GetMethod(
                "AddActivity",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                [typeof(string), typeof(string), typeof(ActivityEventKind)],
                null)!;
            addActivity.Invoke(window, ["底层诊断明细", "信息", ActivityEventKind.Detail]);
            addActivity.Invoke(window, ["TEST.01：选课成功并通过最终状态复核。", "成功", ActivityEventKind.CourseAdd]);
            var importantText = new TextRange(activityLog.Document.ContentStart, activityLog.Document.ContentEnd).Text;
            Assert(importantText.Contains("TEST.01", StringComparison.Ordinal)
                   && !importantText.Contains("底层诊断明细", StringComparison.Ordinal),
                "关键记录没有正确筛选真实写入和普通诊断");
            Assert(activityLog.Document.Blocks.OfType<Paragraph>()
                .SelectMany(paragraph => paragraph.Inlines.OfType<Run>())
                .Any(run => run.Text.Contains("TEST.01", StringComparison.Ordinal) && run.FontWeight == System.Windows.FontWeights.Bold),
                "真实选退课记录没有加粗");
            ((Button)window.FindName("AllActivityButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            var allText = new TextRange(activityLog.Document.ContentStart, activityLog.Document.ContentEnd).Text;
            Assert(allText.Contains("TEST.01", StringComparison.Ordinal)
                   && allText.Contains("底层诊断明细", StringComparison.Ordinal),
                "全部记录没有保留普通诊断信息");
            Assert(activityLog.IsSelectionEnabled
                   && allText.Contains(Environment.NewLine, StringComparison.Ordinal),
                "活动记录不能跨越多行连续选择复制");
            ((Button)window.FindName("ImportantActivityButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            var allActivityEntries = (ObservableCollection<LocalLogEntry>)windowType
                .GetField("_activityEntries", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;
            var allActivityParagraph = (Paragraph)windowType
                .GetField("_allActivityParagraph", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;
            for (var index = 0; index < 20000; index++)
            {
                var entry = new LocalLogEntry(DateTimeOffset.Now, "信息", $"性能测试明细 {index}");
                allActivityEntries.Add(entry);
                allActivityParagraph.Inlines.Add(new Run(entry.DisplayText));
                allActivityParagraph.Inlines.Add(new LineBreak());
            }

            var switchTimer = System.Diagnostics.Stopwatch.StartNew();
            ((Button)window.FindName("AllActivityButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            switchTimer.Stop();
            Assert(switchTimer.Elapsed < TimeSpan.FromSeconds(1),
                $"两万条活动记录切换到全部记录仍然阻塞过久：{switchTimer.Elapsed.TotalMilliseconds:F0} ms");
            switchTimer.Restart();
            ((Button)window.FindName("ImportantActivityButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            switchTimer.Stop();
            Assert(switchTimer.Elapsed < TimeSpan.FromSeconds(1),
                $"两万条活动记录切回关键记录仍然阻塞过久：{switchTimer.Elapsed.TotalMilliseconds:F0} ms");
            switchTimer.Restart();
            ((Button)window.FindName("AllActivityButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            ((Button)window.FindName("ImportantActivityButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            switchTimer.Stop();
            Assert(switchTimer.Elapsed < TimeSpan.FromSeconds(1.5),
                $"缓存后的关键/全部往返切换仍然阻塞过久：{switchTimer.Elapsed.TotalMilliseconds:F0} ms");
            operationTabs.SelectedIndex = 2;
            advancedTabs.SelectedIndex = 0;
            window.UpdateLayout();
            Assert(VisualDescendants<TextBox>(operationTabs).Any(text => text.Text == "调试工具"),
                "调试工具页面内部标题没有同步改名");
            var shadowColumnWidths = ((DataGrid)window.FindName("TargetsGrid")).Columns.Select(column => column.ActualWidth).ToArray();
            Assert(shadowColumnWidths.All(width => width >= 35),
                "最小窗口下课程影子表存在被截断的列：" + string.Join(", ", shadowColumnWidths.Select(width => width.ToString("F1"))));
            advancedTabs.SelectedIndex = 1;
            window.UpdateLayout();
            Assert(((TextBox)window.FindName("ControlledDetailsText")).ActualWidth >= 300,
                "最小窗口下单门操作结果区被截断");
            operationTabs.SelectedIndex = 3;
            window.UpdateLayout();
            var accountTextBox = (TextBox)window.FindName("SavedAccountTextBox");
            var passwordBox = (PasswordBox)window.FindName("SavedPasswordBox");
            var autoLoginCheckBox = (CheckBox)window.FindName("AutoLoginCheckBox");
            Assert(accountTextBox.ActualWidth >= 250 && passwordBox.ActualWidth >= 250,
                "最小窗口下账号或密码输入框被截断");
            const string offlineSecret = "offline-secret-7A!";
            accountTextBox.Text = "offline-user";
            passwordBox.Password = offlineSecret;
            saveCredential.Invoke(window, [window, new System.Windows.RoutedEventArgs()]);
            Assert(passwordBox.Password.Length == 0, "保存凭据后密码输入框没有清空");
            Assert(autoLoginCheckBox.IsEnabled && autoLoginCheckBox.IsChecked != true,
                "保存密码后不应自动开启自动登录");
            autoLoginCheckBox.IsChecked = true;
            var testRoot = Environment.GetEnvironmentVariable("USTC_ROUTE2_TEST_DATA_ROOT")!;
            var persistedText = string.Join("\n", Directory.EnumerateFiles(testRoot, "*", SearchOption.AllDirectories)
                .Select(File.ReadAllText));
            Assert(!persistedText.Contains(offlineSecret, StringComparison.Ordinal),
                "密码被写入了普通配置或日志文件");
            Assert(persistedText.Contains("offline-user", StringComparison.Ordinal)
                   && persistedText.Contains("AutoLoginEnabled", StringComparison.Ordinal),
                "账号或自动登录偏好没有保存到普通配置");
            var activityPath = IOPath.Combine(testRoot, "activity.jsonl");
            Assert(!File.Exists(activityPath)
                   || !File.ReadAllText(activityPath).Contains("offline-user", StringComparison.Ordinal),
                "活动日志记录了普通配置中允许保存的账号");
            operationTabs.SelectedIndex = 0;
            window.UpdateLayout();

            if (Environment.GetEnvironmentVariable("USTC_ROUTE2_UI_PREVIEW") is { Length: > 0 } previewPath)
            {
                RenderPreview(previewPath, 1240, 800);
                automationGrid.IsEnabled = false;
                RenderPreview(Suffixed(previewPath, "monitoring-locked"), 1240, 800);
                automationGrid.IsEnabled = true;
                operationTabs.SelectedIndex = 1;
                RenderPreview(Suffixed(previewPath, "activity"), 1240, 800);
                operationTabs.SelectedIndex = 2;
                advancedTabs.SelectedIndex = 0;
                RenderPreview(Suffixed(previewPath, "shadow"), 1240, 800);
                advancedTabs.SelectedIndex = 1;
                RenderPreview(Suffixed(previewPath, "controlled"), 1240, 800);
                operationTabs.SelectedIndex = 3;
                RenderPreview(Suffixed(previewPath, "account-security"), 1240, 800);
                operationTabs.SelectedIndex = 0;
                RenderPreview(Suffixed(previewPath, "minimum"), window.MinWidth, window.MinHeight);
                showOnboarding.Invoke(window, null);
                window.Width = 1240;
                window.Height = 800;
                window.UpdateLayout();
                updateOnboarding.Invoke(window, null);
                RenderPreview(Suffixed(previewPath, "onboarding"), 1240, 800);
                ((Button)window.FindName("OnboardingSkipButton")).RaiseEvent(
                    new System.Windows.RoutedEventArgs(Button.ClickEvent));

                void RenderPreview(string path, double width, double height)
                {
                    window.Width = width;
                    window.Height = height;
                    window.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(
                        Math.Max(1, (int)Math.Ceiling(window.ActualWidth)),
                        Math.Max(1, (int)Math.Ceiling(window.ActualHeight)),
                        96,
                        96,
                        PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    Directory.CreateDirectory(IOPath.GetDirectoryName(path)!);
                    using var stream = File.Create(path);
                    encoder.Save(stream);
                }

                static string Suffixed(string path, string suffix) =>
                    IOPath.Combine(
                        IOPath.GetDirectoryName(path)!,
                    IOPath.GetFileNameWithoutExtension(path) + "-" + suffix + IOPath.GetExtension(path));
            }

            window.Hide();

            runningField.SetValue(window, true);
            setBusy.Invoke(window, [false]);
            Assert(!Control("TurnSelector").IsEnabled, "监测时轮次输入应锁定");
            Assert(!Control("ControlledLessonCodeText").IsEnabled, "监测时单门课程输入应锁定");

            pause.Invoke(window, ["测试暂停", "测试"]);
            foreach (var name in new[]
                     {
                         "TurnSelector",
                         "ControlledActionSelector",
                         "ControlledLessonCodeText",
                         "AutomationTasksGrid",
                         "AutomationIntervalSelector",
                         "AutomationCustomIntervalText",
                         "AutomationIntervalJitterCheckBox",
                         "AutomationScheduleCheckBox",
                         "AutomationStartTimeText",
                         "AutomationEndTimeText",
                         "RunAtStartupCheckBox",
                         "SavedAccountTextBox",
                         "SavedPasswordBox",
                         "SaveCredentialButton"
                     })
            {
                Assert(Control(name).IsEnabled, $"暂停后控件 {name} 没有恢复");
            }

            var recoveryTurn = new TurnInfo(200, "恢复测试轮次", "测试学期");
            var turns = (System.Collections.ObjectModel.ObservableCollection<TurnInfo>)turnsField.GetValue(window)!;
            turns.Clear();
            turns.Add(recoveryTurn);
            ((ComboBox)window.FindName("TurnSelector")).SelectedItem = recoveryTurn;
            sessionField.SetValue(window, new SessionValidationResult(100, [recoveryTurn]));
            runningField.SetValue(window, true);
            beginReadNetworkWait.Invoke(window, ["离线模拟纯读取网络超时"]);
            Assert((bool)recoveryPendingField.GetValue(window)!
                   && (bool)runningField.GetValue(window)!
                   && ((TextBox)window.FindName("AutomationStateText")).Text == "等待网络恢复"
                   && plannedAtField.GetValue(window) is DateTimeOffset,
                "纯读取网络超时没有进入等待恢复状态或错误停止了监测意图");
            pause.Invoke(window, ["结束网络等待离线测试", "测试"]);

            runningField.SetValue(window, true);
            plannedAtField.SetValue(window, (DateTimeOffset?)(DateTimeOffset.Now - TimeSpan.FromHours(1)));
            plannedDelayField.SetValue(window, TimeSpan.FromSeconds(15));
            timerTick.Invoke(window, [null, EventArgs.Empty]);
            Assert(!(bool)runningField.GetValue(window)!
                   && ((TextBox)window.FindName("StatusText")).Text.Contains("明显晚于计划时间", StringComparison.Ordinal),
                "计时器长时间迟到没有在任何请求前暂停");

            var outsideTime = TimeOnly.FromDateTime(DateTime.Now.AddMinutes(5));
            settingsField.SetValue(window, new AutomationSettings
            {
                EnableSchedule = true,
                StartAt = outsideTime,
                EndAt = outsideTime
            });
            runningField.SetValue(window, true);
            var cycle = (Task)runCycle.Invoke(window, [true])!;
            cycle.GetAwaiter().GetResult();
            var status = (TextBox)window.FindName("StatusText");
            var dot = (Ellipse)window.FindName("StatusDot");
            Assert(status.Text.Contains("不在运行时段", StringComparison.Ordinal), "运行时段外未显示等待状态");
            Assert(((TextBox)window.FindName("AutomationStateText")).Text == "等待运行时段",
                "运行时段外任务控制区没有显示简短的等待状态");
            var yellowBrush = (SolidColorBrush)window.FindResource("LoginStatusYellowBrush");
            var yellowBorder = (SolidColorBrush)window.FindResource("LoginStatusYellowBorderBrush");
            Assert(yellowBrush.Color == (Color)ColorConverter.ConvertFromString("#FFFF00"), "登录状态黄色资源不是 #FFFF00");
            Assert(dot.Fill is SolidColorBrush brush && brush.Color == yellowBrush.Color,
                "运行时段外登录状态圆点没有使用 #FFFF00");
            Assert(dot.Stroke is SolidColorBrush stroke && stroke.Color == yellowBorder.Color && dot.StrokeThickness > 0,
                "黄色登录状态圆点缺少细深黄色边框");

            runningField.SetValue(window, false);
            return;

            Control Control(string name) => (Control)window.FindName(name);
        }
        catch (Exception ex)
        {
            failure = ex is TargetInvocationException { InnerException: not null } invocation
                ? invocation.InnerException
                : ex;
        }
        finally
        {
            loginWindow?.CloseForApplication();
            window?.Close();
            app?.Shutdown();
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null)
    {
        throw new InvalidOperationException("界面状态回归检查失败。", failure);
    }
}

static void CheckAutoLoginScriptSafety()
{
    var entry = LoginFormAutomation.BuildTeachingLoginEntryScript();
    var classification = LoginFormAutomation.BuildClassificationScript();
    var submission = LoginFormAutomation.BuildSubmissionScript("user\"name", "p'ass\\word");
    Assert(entry.Contains("id.ustc.edu.cn", StringComparison.Ordinal)
           && entry.Contains("统一身份认证", StringComparison.Ordinal)
           && entry.Contains("target.click()", StringComparison.Ordinal),
        "教务登录入口脚本不能识别并进入统一身份认证");
    Assert(!entry.Contains("password", StringComparison.OrdinalIgnoreCase)
           && !entry.Contains("username", StringComparison.OrdinalIgnoreCase),
        "教务登录入口脚本不应接触账号密码字段");
    Assert(classification.Contains("verificationInput", StringComparison.Ordinal)
           && classification.Contains("新设备", StringComparison.Ordinal),
        "登录页识别脚本没有覆盖验证页面");
    Assert(submission.IndexOf("verificationInput", StringComparison.Ordinal)
           < submission.IndexOf("setValue(accountInput", StringComparison.Ordinal),
        "自动填写脚本没有在写入账号密码前检查验证字段");
    Assert(submission.Contains("user\\u0022name", StringComparison.Ordinal)
           && submission.Contains("p\\u0027ass\\\\word", StringComparison.Ordinal)
           && !submission.Contains("user\"name", StringComparison.Ordinal),
        "账号密码没有安全编码进登录脚本");
    Assert(LoginFormAutomation.ParseResult("\"verification\"") == "verification"
           && LoginFormAutomation.ParseResult("not-json") == "other",
        "登录页脚本结果解析不符合安全默认值");
    Assert(AutoLoginNavigationPolicy.MaximumPageProbeAttempts is > 1 and <= 20
           && AutoLoginNavigationPolicy.PageProbeDelay >= TimeSpan.FromMilliseconds(200),
        "异步登录页探测不是有限、节制的重试");
    Assert(AutoLoginNavigationPolicy.IsTeachingLoginEntry(new Uri("https://jw.ustc.edu.cn/login?service=test"))
           && !AutoLoginNavigationPolicy.IsTeachingLoginEntry(new Uri("http://jw.ustc.edu.cn/login"))
           && !AutoLoginNavigationPolicy.IsTeachingLoginEntry(new Uri("https://jw.ustc.edu.cn/other"))
           && AutoLoginNavigationPolicy.IsIdentityProvider(new Uri("https://id.ustc.edu.cn/cas/login"))
           && !AutoLoginNavigationPolicy.IsIdentityProvider(new Uri("https://id.ustc.edu.cn.evil.test/login")),
        "自动登录允许域名或入口路径边界不正确");
}

static void CheckPreferenceUpgradeCompatibility()
{
    var testRoot = Environment.GetEnvironmentVariable("USTC_ROUTE2_TEST_DATA_ROOT")!;
    var preferencePath = IOPath.Combine(testRoot, "preferences.json");
    Directory.CreateDirectory(testRoot);
    File.WriteAllText(preferencePath, "{\"KeepLoginState\":false}");

    var migrated = LocalPreferences.Load();
    Assert(!migrated.KeepLoginState
           && migrated.SavedAccount.Length == 0
           && !migrated.AutoLoginEnabled
           && migrated.OnboardingCompleted is null,
        "阶段 D 旧偏好配置不能安全迁移到发布候选版");
    Assert(LocalPreferences.HasExistingInstallationData(),
        "已有偏好文件没有被识别为旧用户数据");

    var current = new LocalPreferences(false, "upgrade-user", true, true);
    current.Save();
    var reloaded = LocalPreferences.Load();
    var json = File.ReadAllText(preferencePath);
    Assert(reloaded == current, "阶段 E 偏好配置重新加载后发生变化");
    Assert(json.Contains("upgrade-user", StringComparison.Ordinal)
           && !json.Contains("Password", StringComparison.OrdinalIgnoreCase),
        "普通偏好配置缺少账号或出现密码字段");
}

static bool HasSetter(System.Windows.Style? style, System.Windows.DependencyProperty property, object expected) =>
    style?.Setters.OfType<System.Windows.Setter>().Any(setter =>
        setter.Property == property && Equals(setter.Value, expected)) == true;

static IEnumerable<T> VisualDescendants<T>(System.Windows.DependencyObject root)
    where T : System.Windows.DependencyObject
{
    for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
    {
        var child = VisualTreeHelper.GetChild(root, index);
        if (child is T match)
        {
            yield return match;
        }

        foreach (var descendant in VisualDescendants<T>(child))
        {
            yield return descendant;
        }
    }
}

static void CheckIntervalJitterBounds()
{
    var fixedDelay = AutomationTiming.CalculateNextDelay(15, false, 0.37);
    Assert(Math.Abs(fixedDelay.TotalSeconds - 15) < 0.001, "关闭抖动后周期不应变化");

    var minimum = AutomationTiming.CalculateNextDelay(15, true, 0);
    var midpoint = AutomationTiming.CalculateNextDelay(15, true, 0.5);
    var maximum = AutomationTiming.CalculateNextDelay(15, true, 1);
    Assert(Math.Abs(minimum.TotalSeconds - 10) < 0.001, "抖动下界不是基准周期的 2/3");
    Assert(Math.Abs(midpoint.TotalSeconds - 15) < 0.001, "抖动中点不等于基准周期");
    Assert(Math.Abs(maximum.TotalSeconds - 20) < 0.001, "抖动上界不是基准周期的 4/3");

    for (var index = 0; index <= 100; index++)
    {
        var sample = AutomationTiming.CalculateNextDelay(5, true, index / 100d).TotalSeconds;
        Assert(sample >= 5d * 2d / 3d - 0.001 && sample <= 5d * 4d / 3d + 0.001, "抖动结果超出 ±1/3 范围");
    }
}

static void CheckTimerDelayAndNetworkClassification()
{
    var delay = TimeSpan.FromSeconds(15);
    var plannedAt = new DateTimeOffset(2026, 8, 29, 0, 25, 5, TimeSpan.FromHours(8));
    Assert(!AutomationTiming.IsSignificantlyLate(plannedAt, plannedAt.AddSeconds(30), delay),
        "计时器在容忍边界上不应误判为休眠");
    Assert(AutomationTiming.IsSignificantlyLate(plannedAt, plannedAt.AddSeconds(30.001), delay),
        "计时器明显迟到没有在任何请求前触发保护");
    Assert(AutomationTiming.CalculateLateTolerance(TimeSpan.FromMinutes(10)) == TimeSpan.FromMinutes(2),
        "长周期的迟到容忍没有限制在两分钟内");
    Assert(!AutomationTiming.IsSignificantlyLate(plannedAt, plannedAt.AddSeconds(-1), delay),
        "系统时间向前校正不应误判为计时器迟到");

    Assert(NetworkFailureClassifier.IsTransientReadFailure(new HttpRequestException("offline"), false),
        "纯读取 HttpRequestException 没有分类为可等待网络异常");
    Assert(NetworkFailureClassifier.IsTransientReadFailure(
            new TaskCanceledException("timeout", new TimeoutException()),
            false),
        "HttpClient 超时没有分类为可等待网络异常");
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    Assert(!NetworkFailureClassifier.IsTransientReadFailure(
            new OperationCanceledException(canceled.Token),
            cancellationRequested: true),
        "用户或安全策略主动取消被误当作纯读取网络超时");
    Assert(!NetworkFailureClassifier.IsTransientReadFailure(new JsonException("changed"), false)
           && !NetworkFailureClassifier.IsTransientReadFailure(new InvalidOperationException("HTTP 500"), false),
        "接口格式或服务器错误不应自动等待并恢复监测");
}

static void CheckAtomicConfigurationRecovery()
{
    var root = IOPath.Combine(IOPath.GetTempPath(), "ustc-route2-config-check-" + Guid.NewGuid().ToString("N"));
    var path = IOPath.Combine(root, "automation-settings.json");
    var store = new AutomationStore(path);
    store.Save(new AutomationSettings { Tasks = [CreateTask("FIRST.01")] });
    store.Save(new AutomationSettings { Tasks = [CreateTask("SECOND.01")] });
    File.WriteAllText(path, "{损坏的配置");
    var recovered = store.Load();
    Assert(recovered.Warning is not null, "配置损坏时没有给出恢复提示");
    Assert(recovered.Settings.Tasks.Count == 1, "配置损坏后任务全部丢失");
}

static void CheckLogSanitizationAndRetention()
{
    var root = IOPath.Combine(IOPath.GetTempPath(), "ustc-route2-log-check-" + Guid.NewGuid().ToString("N"));
    var path = IOPath.Combine(root, "activity.jsonl");
    Directory.CreateDirectory(root);
    var expired = new LocalLogEntry(DateTimeOffset.Now.AddDays(-15), "旧日志", "password=old-secret");
    var recent = new LocalLogEntry(DateTimeOffset.Now.AddDays(-1), "近期日志", "保留此条");
    File.WriteAllLines(path, [JsonSerializer.Serialize(expired), JsonSerializer.Serialize(recent)]);
    File.WriteAllText(path + ".bak", JsonSerializer.Serialize(expired));
    File.WriteAllText(path + ".tmp-interrupted", JsonSerializer.Serialize(expired));
    var log = new LocalActivityLog(path);
    var retainedBeforeAppend = log.LoadRecent();
    Assert(retainedBeforeAppend.Count == 1 && retainedBeforeAppend[0].Message == "保留此条", "日志没有按最近 14 天清理");
    Assert(!File.ReadAllText(path + ".bak").Contains("old-secret", StringComparison.Ordinal), "日志备份仍保留超过 14 天的敏感内容");
    Assert(!File.ReadAllText(path + ".tmp-interrupted").Contains("old-secret", StringComparison.Ordinal), "中断日志临时文件绕过了 14 天清理");
    log.Append(
        "测试",
        "password=secret cookie=abcdef token=very-secret-value https://example.test/a?ticket=123",
        ActivityEventKind.CourseAdd);
    var entries = log.LoadRecent();
    Assert(entries.Count == 2, "脱敏日志没有正常保存");
    var message = entries[^1].Message;
    Assert(!message.Contains("secret", StringComparison.OrdinalIgnoreCase), "日志泄露了密码或令牌");
    Assert(!message.Contains("ticket=123", StringComparison.OrdinalIgnoreCase), "日志泄露了 URL 参数");
    Assert(entries[0].Kind == ActivityEventKind.Detail
           && entries[^1].Kind == ActivityEventKind.CourseAdd
           && entries[^1].IsKeyEvent
           && entries[^1].IsEmphasized,
        "旧日志兼容或关键写入分类不正确");
}

static async Task CheckInterruptedOperationProtectionAsync()
{
    var root = IOPath.Combine(IOPath.GetTempPath(), "ustc-route2-journal-check-" + Guid.NewGuid().ToString("N"));
    var store = new OperationJournalStore(IOPath.Combine(root, "operation-journal.json"));
    var coordinator = new RealOperationCoordinator(store);
    var fake = new FakeSession(selectedIds: []);
    fake.AddLesson(2, "NEW.01", selectedCount: 5, limit: 10);

    using (var lease = await coordinator.EnterAsync("离线中断测试", fake, fake, CancellationToken.None))
    {
        var duplicateBlocked = false;
        try
        {
            using var duplicate = await coordinator.EnterAsync("重复功能", fake, fake, CancellationToken.None);
        }
        catch (RealOperationBusyException)
        {
            duplicateBlocked = true;
        }

        Assert(duplicateBlocked, "同一进程内两个功能仍可同时进入真实操作");
        lease.BeginWorkflow("离线中断测试", 100, 200, "NEW.01", []);
        var result = await lease.ExecuteAndVerifyAsync(
            CourseMutationKind.Add,
            fake.Lesson(2),
            "选目标课 NEW.01",
            CancellationToken.None);
        Assert(result.ExpectedStateReached, "离线中断测试的状态复核失败");
        // 故意不调用 CompleteWorkflow，模拟进程在整个换课流程收尾前中断。
    }

    var restarted = new RealOperationCoordinator(store);
    Assert(restarted.HasUnresolvedOperation, "重启后没有识别未完成操作流程");
    var blocked = false;
    try
    {
        using var unexpected = await restarted.EnterAsync("不应允许", fake, fake, CancellationToken.None);
    }
    catch (UnresolvedOperationException)
    {
        blocked = true;
    }

    Assert(blocked, "存在中断流程时仍允许新的真实操作");
    restarted.AcknowledgeResolved("离线测试人工核对完成");
    Assert(!restarted.HasUnresolvedOperation, "人工核对后保护没有解除");

    fake.SelectedIds.Remove(2);
    fake.FailAddIds.Add(2);
    using (var lease = await restarted.EnterAsync("结果不一致测试", fake, fake, CancellationToken.None))
    {
        lease.BeginWorkflow("结果不一致测试", 100, 200, "NEW.01", []);
        var mismatch = await lease.ExecuteAndVerifyAsync(
            CourseMutationKind.Add,
            fake.Lesson(2),
            "选目标课 NEW.01",
            CancellationToken.None);
        Assert(!mismatch.ExpectedStateReached, "模拟结果不一致没有生效");
        lease.RequireManualReview("离线测试：最终状态与计划不一致");
    }

    Assert(restarted.HasUnresolvedOperation, "最终状态与计划不一致后没有锁定新的真实操作");
    restarted.AcknowledgeResolved("离线测试人工核对不一致结果");

    using (var lease = await restarted.EnterAsync("取消保护测试", fake, fake, CancellationToken.None))
    {
        lease.BeginWorkflow("取消保护测试", 100, 200, "NEW.01", []);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await lease.ExecuteAndVerifyAsync(
                CourseMutationKind.Add,
                fake.Lesson(2),
                "选目标课 NEW.01",
                canceled.Token);
            throw new InvalidOperationException("取消测试没有抛出 OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
        }
    }

    Assert(restarted.HasUnresolvedOperation, "真实操作取消后没有保留结果不确定保护");
    Assert(restarted.ActiveJournal?.CurrentRequestMayHaveBeenSent == true,
        "真实写入取消后没有记录请求可能已发送");
    restarted.AcknowledgeResolved("离线测试人工核对取消结果");

    fake.IncompleteMutationIds.Add(2);
    using (var lease = await restarted.EnterAsync("服务器结果不确定测试", fake, fake, CancellationToken.None))
    {
        lease.BeginWorkflow("服务器结果不确定测试", 100, 200, "NEW.01", []);
        try
        {
            await lease.ExecuteAndVerifyAsync(
                CourseMutationKind.Add,
                fake.Lesson(2),
                "选目标课 NEW.01",
                CancellationToken.None);
            throw new InvalidOperationException("服务器未给出最终结果时没有抛出结果不确定保护");
        }
        catch (WriteResultUncertainException)
        {
        }
    }

    Assert(restarted.HasUnresolvedOperation
           && restarted.ActiveJournal?.CurrentRequestMayHaveBeenSent == true,
        "服务器未给出最终写入结果后没有保持锁定");
    restarted.AcknowledgeResolved("离线测试人工核对服务器不确定结果");
}

static async Task CheckStrictInterruptedSwapRecoveryAsync()
{
    var (coordinator, fake) = await CreateEligibleInterruptedSwapAsync("success");
    Assert(coordinator.HasEligibleInterruptedSwapRecovery,
        "旧课退课已确认且目标请求未发送时没有形成严格恢复资格");
    var restored = await coordinator.TryRestoreConfirmedDropsAfterNetworkAsync(
        100,
        200,
        fake,
        fake,
        CancellationToken.None);
    Assert(restored.Outcome == InterruptedRestoreOutcome.Restored,
        "严格条件成立时没有执行一次旧课恢复");
    Assert(fake.Operations.SequenceEqual(["Drop:OLD.01", "Add:OLD.01"])
           && fake.SelectedIds.SetEquals([1L]),
        "旧课恢复请求不是严格串行一次或最终状态不正确");
    Assert(coordinator.HasUnresolvedOperation
           && coordinator.ActiveJournal is { AutomaticRecoveryEvaluationStarted: true, AutomaticRestoreAttempted: true },
        "自动恢复结束后没有继续保持人工核对锁");
    var repeated = await coordinator.TryRestoreConfirmedDropsAfterNetworkAsync(
        100,
        200,
        fake,
        fake,
        CancellationToken.None);
    Assert(repeated.Outcome == InterruptedRestoreOutcome.NotEligible && fake.Operations.Count == 2,
        "同一个中断流程重复发送了旧课恢复请求");

    var (targetStartedCoordinator, targetStartedFake) = await CreateEligibleInterruptedSwapAsync(
        "target-started",
        markTargetStarted: true);
    Assert(!targetStartedCoordinator.HasEligibleInterruptedSwapRecovery,
        "目标课请求已进入可能发送阶段后仍允许自动恢复");
    var targetStartedResult = await targetStartedCoordinator.TryRestoreConfirmedDropsAfterNetworkAsync(
        100,
        200,
        targetStartedFake,
        targetStartedFake,
        CancellationToken.None);
    Assert(targetStartedResult.Outcome == InterruptedRestoreOutcome.NotEligible
           && targetStartedFake.Operations.SequenceEqual(["Drop:OLD.01"]),
        "无法证明目标请求未发送时仍提交了恢复请求");

    var (identityCoordinator, identityFake) = await CreateEligibleInterruptedSwapAsync("identity");
    var identityMismatch = await identityCoordinator.TryRestoreConfirmedDropsAfterNetworkAsync(
        999,
        200,
        identityFake,
        identityFake,
        CancellationToken.None);
    Assert(identityMismatch.Outcome == InterruptedRestoreOutcome.IdentityMismatch
           && identityFake.Operations.SequenceEqual(["Drop:OLD.01"])
           && !identityCoordinator.HasEligibleInterruptedSwapRecovery,
        "账号不一致时没有永久禁止自动恢复");

    var (targetSelectedCoordinator, targetSelectedFake) = await CreateEligibleInterruptedSwapAsync("target-selected");
    targetSelectedFake.SelectedIds.Add(2);
    var targetSelected = await targetSelectedCoordinator.TryRestoreConfirmedDropsAfterNetworkAsync(
        100,
        200,
        targetSelectedFake,
        targetSelectedFake,
        CancellationToken.None);
    Assert(targetSelected.Outcome == InterruptedRestoreOutcome.StateNotEligible
           && targetSelectedFake.Operations.SequenceEqual(["Drop:OLD.01"]),
        "目标课已经选中时仍错误恢复了旧课");

    var (readFailedCoordinator, readFailedFake) = await CreateEligibleInterruptedSwapAsync("read-failed");
    readFailedFake.ReadFailure = new HttpRequestException("offline read");
    var readFailed = await readFailedCoordinator.TryRestoreConfirmedDropsAfterNetworkAsync(
        100,
        200,
        readFailedFake,
        readFailedFake,
        CancellationToken.None);
    Assert(readFailed.Outcome == InterruptedRestoreOutcome.StateReadFailed
           && readFailedFake.Operations.SequenceEqual(["Drop:OLD.01"])
           && !readFailedCoordinator.HasEligibleInterruptedSwapRecovery,
        "状态读取失败后仍保留自动恢复或重试资格");

    var (writeUnknownCoordinator, writeUnknownFake) = await CreateEligibleInterruptedSwapAsync("write-unknown");
    writeUnknownFake.MutationFailure = new HttpRequestException("unknown write");
    var writeUnknown = await writeUnknownCoordinator.TryRestoreConfirmedDropsAfterNetworkAsync(
        100,
        200,
        writeUnknownFake,
        writeUnknownFake,
        CancellationToken.None);
    Assert(writeUnknown.Outcome == InterruptedRestoreOutcome.ResultUnknown
           && writeUnknownFake.Operations.SequenceEqual(["Drop:OLD.01", "Add:OLD.01"])
           && writeUnknownCoordinator.ActiveJournal?.CurrentRequestMayHaveBeenSent == true,
        "恢复写入结果不确定时没有保持可能已发送标记");
    var unknownRepeated = await writeUnknownCoordinator.TryRestoreConfirmedDropsAfterNetworkAsync(
        100,
        200,
        writeUnknownFake,
        writeUnknownFake,
        CancellationToken.None);
    Assert(unknownRepeated.Outcome == InterruptedRestoreOutcome.NotEligible
           && writeUnknownFake.Operations.Count == 2,
        "结果不确定的恢复请求被自动重试");
}

static async Task<(RealOperationCoordinator Coordinator, FakeSession Fake)> CreateEligibleInterruptedSwapAsync(
    string suffix,
    bool markTargetStarted = false)
{
    var root = IOPath.Combine(IOPath.GetTempPath(), "ustc-route2-strict-recovery-" + suffix + "-" + Guid.NewGuid().ToString("N"));
    var coordinator = new RealOperationCoordinator(
        new OperationJournalStore(IOPath.Combine(root, "operation-journal.json")));
    var fake = new FakeSession(selectedIds: [1]);
    fake.AddLesson(1, "OLD.01", selectedCount: 5, limit: 10);
    fake.AddLesson(2, "NEW.01", selectedCount: 5, limit: 10);
    using (var lease = await coordinator.EnterAsync("严格恢复测试", fake, fake, CancellationToken.None))
    {
        lease.BeginWorkflow("严格恢复测试", 100, 200, "NEW.01", ["OLD.01"]);
        var drop = await lease.ExecuteAndVerifyAsync(
            CourseMutationKind.Drop,
            fake.Lesson(1),
            "退旧课 OLD.01",
            CancellationToken.None);
        Assert(drop.ExpectedStateReached, "严格恢复测试未确认旧课退课成功");
        lease.RecordConfirmedDropBeforeTarget(fake.Lesson(1));
        if (markTargetStarted)
        {
            lease.MarkTargetRequestWillStart();
        }
    }

    return (coordinator, fake);
}

static void CheckCorruptJournalRecovery()
{
    var root = IOPath.Combine(IOPath.GetTempPath(), "ustc-route2-journal-recovery-" + Guid.NewGuid().ToString("N"));
    var path = IOPath.Combine(root, "operation-journal.json");
    var store = new OperationJournalStore(path);
    var journal = new OperationJournal
    {
        Owner = "换课恢复测试",
        TargetCode = "NEW.01",
        CurrentCourseCode = "OLD.01",
        Stage = "退旧课请求即将提交"
    };
    store.Save(journal);
    journal.Stage = "退旧课最终状态已读取";
    store.Save(journal);
    File.WriteAllText(path, "{损坏的操作记录");

    var recovered = new OperationJournalStore(path).Load();
    Assert(recovered.ActiveJournal is { Active: true }, "操作记录损坏后没有保持安全锁");
    Assert(recovered.Warning?.Contains("安全副本", StringComparison.Ordinal) == true, "操作记录损坏后没有明确恢复提示");
    Assert(recovered.ActiveJournal!.TargetCode == "NEW.01", "操作记录损坏后没有恢复涉及课程");
}

static async Task NoSeatNeverWritesAsync()
{
    var fake = new FakeSession(selectedIds: []);
    fake.AddLesson(2, "NEW.01", selectedCount: 10, limit: 10);
    var task = CreateTask("NEW.01");
    var result = await Engine(fake).RunOnceAsync([task], true, CancellationToken.None);
    Assert(!result.ShouldPause, "无余量不应暂停整个监测");
    Assert(fake.Operations.Count == 0, "无余量不得发送写请求");
    Assert(task.Status == "暂无余量", "无余量状态不正确");
}

static async Task SuccessfulSwapIsStrictlySerialAsync()
{
    var fake = new FakeSession(selectedIds: [1]);
    fake.AddLesson(1, "OLD.01", selectedCount: 5, limit: 10);
    fake.AddLesson(2, "NEW.01", selectedCount: 5, limit: 10);
    var task = CreateTask("NEW.01", "OLD.01");
    var result = await Engine(fake).RunOnceAsync([task], true, CancellationToken.None);
    Assert(!result.ShouldPause, "成功换课不应暂停监测");
    Assert(fake.Operations.SequenceEqual(["Drop:OLD.01", "Add:NEW.01"]), "换课写入顺序错误");
    Assert(fake.SelectedIds.SetEquals([2L]), "成功换课后的最终状态错误");
    Assert(!task.Enabled, "成功任务应停用");
}

static async Task FailedTargetRestoresOldCourseAsync()
{
    var fake = new FakeSession(selectedIds: [1]);
    fake.AddLesson(1, "OLD.01", selectedCount: 5, limit: 10);
    fake.AddLesson(2, "NEW.01", selectedCount: 5, limit: 10);
    fake.FailAddIds.Add(2);
    var task = CreateTask("NEW.01", "OLD.01");
    var result = await Engine(fake).RunOnceAsync([task], true, CancellationToken.None);
    Assert(result.ShouldPause, "目标失败后必须暂停监测");
    Assert(fake.Operations.SequenceEqual(["Drop:OLD.01", "Add:NEW.01", "Add:OLD.01"]), "失败恢复顺序错误");
    Assert(fake.SelectedIds.SetEquals([1L]), "目标失败后旧课没有恢复");
    Assert(!task.Enabled, "失败任务必须停用以防下一轮重复操作");
}

static async Task DuplicateTargetNeverWritesAsync()
{
    var fake = new FakeSession(selectedIds: []);
    fake.AddLesson(2, "NEW.01", selectedCount: 5, limit: 10);
    fake.AddLesson(3, "NEW.01", selectedCount: 5, limit: 10);
    var task = CreateTask("NEW.01");
    var result = await Engine(fake).RunOnceAsync([task], true, CancellationToken.None);
    Assert(fake.Operations.Count == 0, "同代码多班不得发送写请求");
    Assert(result.ShouldPause && !task.Enabled, "同代码多班必须停用任务并暂停监测");
}

static async Task HigherPrioritySelectionDisablesConflictingLowerTaskAsync()
{
    var fake = new FakeSession(selectedIds: [2]);
    fake.AddLesson(2, "HIGH.01", selectedCount: 5, limit: 10);
    fake.AddLesson(3, "LOW.01", selectedCount: 5, limit: 10);
    var high = CreateTask("HIGH.01");
    high.Priority = 1;
    var low = CreateTask("LOW.01", "HIGH.01");
    low.Priority = 2;

    var result = await Engine(fake).RunOnceAsync([low, high], true, CancellationToken.None);
    Assert(!result.ShouldPause, "已选高优先级课程不应导致异常暂停");
    Assert(!high.Enabled && !low.Enabled, "高优先级课程已选后没有停用冲突低优先级任务");
    Assert(fake.Operations.Count == 0, "仅处理优先级冲突时不应发送写请求");
}

static AutomationTask CreateTask(string code, params string[] conflicts) => new()
{
    CourseCode = code,
    ConflictingCourseCodes = [.. conflicts]
};

static Route2AutomationEngine Engine(FakeSession fake) => new(fake, new FakeProtectedExecutor(fake), 100, 200, (_, _, _) => { });

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class FakeSession(IEnumerable<long> selectedIds) : IAutomationReadClient, IAutomationMutationClient
{
    private readonly Dictionary<long, LessonInfo> _lessons = [];
    public HashSet<long> SelectedIds { get; } = [.. selectedIds];
    public HashSet<long> FailAddIds { get; } = [];
    public HashSet<long> IncompleteMutationIds { get; } = [];
    public List<string> Operations { get; } = [];
    public Exception? ReadFailure { get; set; }
    public Exception? MutationFailure { get; set; }

    public void AddLesson(long id, string code, int selectedCount, int limit)
    {
        _lessons[id] = new LessonInfo(id, code, code.Split('.')[0], code, limit, "测试教师", selectedCount);
    }

    public LessonInfo Lesson(long id) => _lessons[id];

    public Task<ShadowSnapshot> GetShadowSnapshotAsync(
        long studentId,
        long turnId,
        IReadOnlyCollection<string> targetCodes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ReadFailure is { } readFailure)
        {
            throw readFailure;
        }

        var selected = _lessons.Values.Where(item => SelectedIds.Contains(item.Id)).ToArray();
        var targets = _lessons.Values
            .Where(item => targetCodes.Contains(item.Code, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var missing = targetCodes
            .Where(code => targets.All(item => !item.Code.Equals(code, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return Task.FromResult(new ShadowSnapshot(selected, targets, missing, _lessons.Count - selected.Length));
    }

    public Task<ControlledOperationResponse> ExecuteOnceAsync(
        CourseMutationKind kind,
        long studentId,
        long turnId,
        long lessonId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lesson = _lessons[lessonId];
        Operations.Add($"{kind}:{lesson.Code}");
        if (MutationFailure is { } mutationFailure)
        {
            throw mutationFailure;
        }

        if (IncompleteMutationIds.Contains(lessonId))
        {
            return Task.FromResult(new ControlledOperationResponse(kind, false, false, "模拟服务器未完成"));
        }

        var success = kind switch
        {
            CourseMutationKind.Add when FailAddIds.Contains(lessonId) => false,
            CourseMutationKind.Add => SelectedIds.Add(lessonId),
            CourseMutationKind.Drop => SelectedIds.Remove(lessonId),
            _ => false
        };
        return Task.FromResult(new ControlledOperationResponse(kind, true, success, success ? "成功" : "模拟失败"));
    }
}

sealed class FakeProtectedExecutor(FakeSession fake) : IProtectedMutationExecutor
{
    private bool _workflowStarted;

    public void BeginWorkflow(string owner, long studentId, long turnId, string targetCode, IEnumerable<string> relatedCodes)
    {
        if (_workflowStarted)
        {
            throw new InvalidOperationException("模拟保护器检测到并行流程");
        }

        _workflowStarted = true;
    }

    public async Task<VerifiedMutationResult> ExecuteAndVerifyAsync(
        CourseMutationKind kind,
        LessonInfo lesson,
        string stepName,
        CancellationToken cancellationToken)
    {
        var response = await fake.ExecuteOnceAsync(kind, 100, 200, lesson.Id, cancellationToken);
        var snapshot = await fake.GetShadowSnapshotAsync(100, 200, [lesson.Code], cancellationToken);
        var selected = snapshot.SelectedLessons.Any(item => item.Id == lesson.Id);
        var expected = kind == CourseMutationKind.Add;
        return new VerifiedMutationResult(selected == expected, selected, response.Message);
    }

    public void RecordConfirmedDropBeforeTarget(LessonInfo lesson)
    {
    }

    public void MarkTargetRequestWillStart()
    {
    }

    public void CompleteWorkflow(string note) => _workflowStarted = false;

    public void RequireManualReview(string note) => _workflowStarted = false;
}
