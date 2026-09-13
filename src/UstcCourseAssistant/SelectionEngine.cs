namespace UstcCourseAssistant;

public sealed class SelectionEngine(ICourseSelectionAdapter adapter, Action<AppLog> writeLog)
{
    public async Task CheckOnceAsync(IEnumerable<CourseTask> tasks, CancellationToken token, bool selectWhenAvailable = true)
    {
        var selectedCodes = await adapter.GetSelectedCourseCodesAsync(token);
        foreach (var task in tasks.Where(t => t.Enabled).OrderBy(t => t.Priority))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (selectedCodes.Contains(task.CourseCode))
                {
                    task.Status = "已选中（已选课程页确认）";
                    task.Enabled = false;
                    writeLog(new(DateTimeOffset.Now, "成功", $"{task.CourseCode}：已选课程页确认已选中。"));
                    continue;
                }
                var probe = await adapter.ProbeAsync(task.CourseCode, token);
                if (probe is null)
                {
                    task.Status = "未找到课程或无法读取容量";
                    writeLog(new(DateTimeOffset.Now, "警告", $"{task.CourseCode}：未找到课程或无法读取容量。"));
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(probe.Name)) task.CourseName = probe.Name;
                if (probe.AlreadySelected)
                {
                    task.Status = "已选中（系统已确认）";
                    task.Enabled = false;
                    writeLog(new(DateTimeOffset.Now, "成功", $"{task.CourseCode}：系统显示已选中。"));
                    continue;
                }
                var seats = probe.RemainingSeats;
                if (seats is null)
                {
                    task.Status = "已找到课程，但无法读取容量";
                    writeLog(new(DateTimeOffset.Now, "警告", $"{task.CourseCode}：已找到课程，但无法读取容量。"));
                    continue;
                }
                if (seats <= 0)
                {
                    task.Status = "暂无余量";
                    writeLog(new(DateTimeOffset.Now, "信息", $"{task.CourseCode}：暂无余量。"));
                    continue;
                }
                if (!selectWhenAvailable)
                {
                    task.Status = $"可选（剩余 {seats}）";
                    writeLog(new(DateTimeOffset.Now, "信息", $"{task.CourseCode}：可选，剩余 {seats} 个名额；未执行选课。"));
                    continue;
                }

                task.Status = "正在处理冲突课";
                var dropped = new List<string>();
                foreach (var oldCode in task.ConflictingCourseCodes.Where(c => !string.IsNullOrWhiteSpace(c)))
                {
                    var result = await adapter.DropAsync(oldCode, token);
                    if (!result.Success) throw new InvalidOperationException($"退课 {oldCode} 失败：{result.Message}");
                    dropped.Add(oldCode);
                }

                var selected = await adapter.SelectAsync(task.CourseCode, token);
                if (selected.Success)
                {
                    task.Status = "已选中";
                    task.Enabled = false;
                    writeLog(new(DateTimeOffset.Now, "成功", $"{task.CourseCode}：选课成功。"));
                    continue;
                }

                task.Status = $"选课失败：{selected.Message}";
                if (selected.Message is "时间冲突" or "同课程代码只能选一门")
                {
                    task.Enabled = false;
                    task.Status = $"已停用：{selected.Message}";
                    writeLog(new(DateTimeOffset.Now, "警告", $"{task.CourseCode}：{selected.Message}，已自动停用该任务。"));
                    continue;
                }
                writeLog(new(DateTimeOffset.Now, "警告", $"{task.CourseCode}：选课失败，开始恢复旧课。原因：{selected.Message}"));
                foreach (var oldCode in dropped)
                {
                    var restored = await adapter.SelectAsync(oldCode, token);
                    if (!restored.Success)
                    {
                        task.Status = $"严重：恢复 {oldCode} 失败";
                        throw new InvalidOperationException($"恢复旧课 {oldCode} 失败，已停止任务。请立即手动核对选课结果。");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                task.Status = "已暂停：需要处理";
                writeLog(new(DateTimeOffset.Now, "错误", $"{task.CourseCode}：{ex.Message}"));
                throw;
            }
        }
    }
}
