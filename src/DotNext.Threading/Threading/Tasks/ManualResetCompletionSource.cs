using System.Runtime.CompilerServices;
using static System.Threading.Timeout;
using Debug = System.Diagnostics.Debug;
using ValueTaskSourceOnCompletedFlags = System.Threading.Tasks.Sources.ValueTaskSourceOnCompletedFlags;

namespace DotNext.Threading.Tasks;

/// <summary>
/// Represents base class for producer of value task.
/// </summary>
public abstract class ManualResetCompletionSource : IThreadPoolWorkItem
{
    private static readonly ContextCallback ContinuationInvoker = InvokeContinuation;

    private readonly Action<object?> cancellationCallback;
    private readonly bool runContinuationsAsynchronously;
    private CancellationTokenRegistration tokenTracker, timeoutTracker;
    private CancellationTokenSource? timeoutSource;

    // task management
    private Action<object?>? continuation;
    private object? continuationState, capturedContext;
    private ExecutionContext? context;
    private protected short version;
    private volatile bool completed;

    private protected ManualResetCompletionSource(bool runContinuationsAsynchronously)
    {
        this.runContinuationsAsynchronously = runContinuationsAsynchronously;
        version = short.MinValue;

        // cached callback to avoid further allocations
        cancellationCallback = CancellationRequested;
    }

    private protected object SyncRoot => cancellationCallback;

    private void CancellationRequested(object? token)
    {
        Debug.Assert(token is short);
        CancellationRequested((short)token);
    }

    private void CancellationRequested(short token)
    {
        // due to concurrency, this method can be called after Reset or twice
        // that's why we need to skip the call if token doesn't match (call after Reset)
        // or completed flag is set (call twice with the same token)
        if (!completed)
        {
            lock (SyncRoot)
            {
                if (token == version && !completed)
                {
                    if (timeoutSource?.IsCancellationRequested ?? false)
                        CompleteAsTimedOut();
                    else
                        CompleteAsCanceled(tokenTracker.Token);
                }
            }
        }
    }

    private protected void StartTrackingCancellation(TimeSpan timeout, CancellationToken token)
    {
        // box current token once and only if needed
        object? tokenHolder = null;
        if (timeout > TimeSpan.Zero)
        {
            timeoutSource ??= new();
            tokenHolder = version;
            timeoutTracker = timeoutSource.Token.UnsafeRegister(cancellationCallback, tokenHolder);
            timeoutSource.CancelAfter(timeout);
        }

        if (token.CanBeCanceled)
        {
            tokenTracker = token.UnsafeRegister(cancellationCallback, tokenHolder ?? version);
        }
    }

    private protected abstract void CompleteAsTimedOut();

    private protected abstract void CompleteAsCanceled(CancellationToken token);

    private protected static object? CaptureContext()
    {
        var context = SynchronizationContext.Current;
        if (context is null || context.GetType() == typeof(SynchronizationContext))
        {
            var scheduler = TaskScheduler.Current;
            return ReferenceEquals(scheduler, TaskScheduler.Default) ? null : scheduler;
        }

        return context;
    }

    private protected void StopTrackingCancellation()
    {
        Debug.Assert(Monitor.IsEntered(SyncRoot));

        tokenTracker.Dispose();
        tokenTracker = default;

        timeoutTracker.Dispose();
        timeoutTracker = default;

        if (timeoutSource is not null && !TryReset(timeoutSource))
        {
            timeoutSource.Dispose();
            timeoutSource = null;
        }

        // TODO: Workaround for https://github.com/dotnet/runtime/issues/60182
        static bool TryReset(CancellationTokenSource source)
        {
            bool result;

            try
            {
                result = source.TryReset();
            }
            catch (ObjectDisposedException)
            {
                result = false;
            }

            return result;
        }
    }

    private static void InvokeContinuation(object? capturedContext, Action<object?> continuation, object? state, bool runAsynchronously)
    {
        switch (capturedContext)
        {
            case null:
                if (!runAsynchronously || !ThreadPool.UnsafeQueueUserWorkItem(continuation, state, false))
                    continuation(state);
                break;
            case SynchronizationContext context:
                context.Post(continuation.Invoke, state);
                break;
            case TaskScheduler scheduler:
                Task.Factory.StartNew(continuation, state, CancellationToken.None, TaskCreationOptions.DenyChildAttach, scheduler);
                break;
        }
    }

    private void InvokeContinuationCore()
    {
        context = null;

        var continuation = this.continuation;
        this.continuation = null;

        var continuationState = this.continuationState;
        this.continuationState = null;

        var capturedContext = this.capturedContext;
        this.capturedContext = null;

        if (continuation is not null)
            InvokeContinuation(capturedContext, continuation, continuationState, runContinuationsAsynchronously);
    }

    private static void InvokeContinuation(object? source)
    {
        Debug.Assert(source is ManualResetCompletionSource);

        Unsafe.As<ManualResetCompletionSource>(source).InvokeContinuationCore();
    }

    private protected void InvokeContinuation()
    {
        if (context is null)
            InvokeContinuationCore();
        else
            ExecutionContext.Run(context, ContinuationInvoker, this);
    }

    private protected virtual void ResetCore()
    {
        Debug.Assert(Monitor.IsEntered(SyncRoot));

        version += 1;
        completed = false;
        context = null;
        continuation = null;
        continuationState = capturedContext = null;
    }

    /// <summary>
    /// Attempts to reset state of this object for reuse.
    /// </summary>
    /// <remarks>
    /// This methods acts as a barried for completion.
    /// It means that calling of this method guarantees that the task
    /// cannot be completed by previously linked timeout or cancellation token.
    /// </remarks>
    /// <returns>The version of the incompleted task.</returns>
    public short Reset()
    {
        short result;

        lock (SyncRoot)
        {
            StopTrackingCancellation();
            ResetCore();
            result = version;
        }

        return result;
    }

    /// <summary>
    /// Invokes when this source is ready to reuse.
    /// </summary>
    protected virtual void AfterConsumed()
    {
    }

    /// <inheritdoc />
    void IThreadPoolWorkItem.Execute() => AfterConsumed();

    private protected void QueueAfterConsumed()
    {
        if (!ThreadPool.UnsafeQueueUserWorkItem(this, true))
            AfterConsumed();
    }

    private void OnCompleted(object? capturedContext, Action<object?> continuation, object? state, short token, bool flowExecutionContext)
    {
        // fast path - monitor lock is not needed
        if (token != version)
            goto invalid_token;

        if (completed)
            goto execute_inplace;

        lock (SyncRoot)
        {
            // avoid running continuation inside of the lock
            if (token != version)
                goto invalid_token;

            if (completed)
                goto execute_inplace;

            this.continuation = continuation;
            continuationState = state;
            this.capturedContext = capturedContext;
            context = flowExecutionContext ? ExecutionContext.Capture() : null;
            goto exit;
        }

    execute_inplace:
        InvokeContinuation(capturedContext, continuation, state, runContinuationsAsynchronously);

    exit:
        return;
    invalid_token:
        throw new InvalidOperationException();
    }

    private protected void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
    {
        var capturedContext = (flags & ValueTaskSourceOnCompletedFlags.UseSchedulingContext) == 0 ? null : CaptureContext();
        OnCompleted(capturedContext, continuation, state, token, (flags & ValueTaskSourceOnCompletedFlags.FlowExecutionContext) != 0);
    }

    /// <summary>
    /// Attempts to complete the task unsuccessfully.
    /// </summary>
    /// <param name="e">The exception to be returned to the consumer.</param>
    /// <returns><see langword="true"/> if the result is completed successfully; <see langword="false"/> if the task has been canceled or timed out.</returns>
    public abstract bool TrySetException(Exception e);

    /// <summary>
    /// Attempts to complete the task unsuccessfully.
    /// </summary>
    /// <param name="token">The canceled token.</param>
    /// <returns><see langword="true"/> if the result is completed successfully; <see langword="false"/> if the task has been canceled or timed out.</returns>
    public abstract bool TrySetCanceled(CancellationToken token);

    /// <summary>
    /// Gets a value indicating that the source is in signaled state.
    /// </summary>
    public bool IsCompleted
    {
        get => completed;
        private protected set => completed = value;
    }

    private void PrepareTaskCore(TimeSpan timeout, CancellationToken token)
    {
        Debug.Assert(Monitor.IsEntered(SyncRoot));

        if (timeout == TimeSpan.Zero)
        {
            CompleteAsTimedOut();
            goto exit;
        }

        if (token.IsCancellationRequested)
        {
            CompleteAsCanceled(token);
            goto exit;
        }

        StartTrackingCancellation(timeout, token);

    exit:
        return;
    }

    private protected void PrepareTask(TimeSpan timeout, CancellationToken token)
    {
        if (timeout < TimeSpan.Zero && timeout != InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        if (!IsCompleted)
        {
            lock (SyncRoot)
            {
                if (!IsCompleted)
                    PrepareTaskCore(timeout, token);
            }
        }
    }
}