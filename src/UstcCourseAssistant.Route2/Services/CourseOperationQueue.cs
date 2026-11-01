namespace UstcCourseAssistant.Route2.Services;

/// <summary>
/// 串行执行完整课程操作。等待中的预约优先于普通操作，但不会中断已开始的操作。
/// </summary>
public sealed class CourseOperationQueue
{
    private readonly object _gate = new();
    private readonly SortedSet<WorkItem> _pending = new(WorkItemComparer.Instance);
    private long _nextSequence;
    private bool _consumerRunning;

    public Task<T> EnqueueAsync<T>(
        bool scheduled,
        DateTimeOffset dueAt,
        DateTimeOffset createdAt,
        Func<CancellationToken, Task<T>> execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execute);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        var item = new WorkItem<T>(this, scheduled, dueAt, createdAt, execute, cancellationToken);
        // 在锁外注册：若令牌已经取消，注册时会同步调用取消回调。
        item.CancellationRegistration = cancellationToken.UnsafeRegister(
            static state =>
            {
                var pending = (WorkItem)state!;
                pending.Owner.CancelPending(pending);
            },
            item);

        var startConsumer = false;
        var cancelledBeforeEnqueue = false;
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelledBeforeEnqueue = true;
            }
            else
            {
                item.Sequence = _nextSequence++;
                _pending.Add(item);
                item.Pending = true;
                if (!_consumerRunning)
                {
                    _consumerRunning = true;
                    startConsumer = true;
                }
            }
        }

        if (cancelledBeforeEnqueue)
        {
            item.CancellationRegistration.Dispose();
            item.Cancel();
        }
        else if (startConsumer)
        {
            _ = Task.Run(ConsumeAsync);
        }

        return item.Completion;
    }

    private void CancelPending(WorkItem item)
    {
        bool removed;
        lock (_gate)
        {
            removed = item.Pending && _pending.Remove(item);
            if (removed)
            {
                item.Pending = false;
            }
        }

        if (removed)
        {
            // 完成通知和取消注册释放均须在队列锁外执行。
            item.Cancel();
            item.CancellationRegistration.Dispose();
        }
    }

    private async Task ConsumeAsync()
    {
        while (true)
        {
            WorkItem item;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _consumerRunning = false;
                    return;
                }

                item = _pending.Min!;
                _pending.Remove(item);
                item.Pending = false;
            }

            // 出队后的取消由执行回调处理；即使令牌已取消，
            // 也必须等回调实际结束后才能处理下一项。
            item.CancellationRegistration.Dispose();
            await item.ExecuteAsync().ConfigureAwait(false);
        }
    }

    private abstract class WorkItem(
        CourseOperationQueue owner,
        bool scheduled,
        DateTimeOffset dueAt,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        public CourseOperationQueue Owner { get; } = owner;
        public bool Scheduled { get; } = scheduled;
        public DateTimeOffset DueAt { get; } = dueAt;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public long Sequence { get; set; }
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public CancellationTokenRegistration CancellationRegistration { get; set; }
        public bool Pending { get; set; }
        public abstract Task ExecuteAsync();
        public abstract void Cancel();
    }

    private sealed class WorkItem<T>(
        CourseOperationQueue owner,
        bool scheduled,
        DateTimeOffset dueAt,
        DateTimeOffset createdAt,
        Func<CancellationToken, Task<T>> execute,
        CancellationToken cancellationToken)
        : WorkItem(owner, scheduled, dueAt, createdAt, cancellationToken)
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Completion => _completion.Task;

        public override async Task ExecuteAsync()
        {
            try
            {
                CancellationToken.ThrowIfCancellationRequested();
                var result = await execute(CancellationToken).ConfigureAwait(false);
                _completion.TrySetResult(result);
            }
            catch (OperationCanceledException exception)
            {
                _completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }

        public override void Cancel() => _completion.TrySetCanceled(CancellationToken);
    }

    private sealed class WorkItemComparer : IComparer<WorkItem>
    {
        public static WorkItemComparer Instance { get; } = new();

        public int Compare(WorkItem? left, WorkItem? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            if (left.Scheduled != right.Scheduled)
            {
                return left.Scheduled ? -1 : 1;
            }

            if (left.Scheduled)
            {
                var dueOrder = left.DueAt.CompareTo(right.DueAt);
                if (dueOrder != 0)
                {
                    return dueOrder;
                }

                var createdOrder = left.CreatedAt.CompareTo(right.CreatedAt);
                if (createdOrder != 0)
                {
                    return createdOrder;
                }
            }

            return left.Sequence.CompareTo(right.Sequence);
        }
    }
}
