using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static System.Threading.Timeout;

namespace DotNext.Threading;

using Tasks;
using Tasks.Pooling;
using Timestamp = Diagnostics.Timestamp;

/// <summary>
/// Provides a framework for implementing asynchronous locks and related synchronization primitives that rely on first-in-first-out (FIFO) wait queues.
/// </summary>
public class QueuedSynchronizer : Disposable
{
    private protected abstract class WaitNode : LinkedValueTaskCompletionSource<bool>
    {
        private Timestamp createdAt;

        private protected override void ResetCore()
        {
            LockDurationCounter = null;
            base.ResetCore();
        }

        internal Action<double>? LockDurationCounter
        {
            private get;
            set;
        }

        internal bool ThrowOnTimeout
        {
            private get;
            set;
        }

        internal void ResetAge() => createdAt = Timestamp.Current;

        protected sealed override Result<bool> OnTimeout() => ThrowOnTimeout ? base.OnTimeout() : false;

        private protected void ReportLockDuration()
            => LockDurationCounter?.Invoke(createdAt.Elapsed.TotalMilliseconds);
    }

    private protected sealed class DefaultWaitNode : WaitNode, IPooledManualResetCompletionSource<DefaultWaitNode>
    {
        private Action<DefaultWaitNode>? consumedCallback;

        protected sealed override void AfterConsumed()
        {
            ReportLockDuration();
            consumedCallback?.Invoke(this);
        }

        private protected override void ResetCore()
        {
            consumedCallback = null;
            base.ResetCore();
        }

        Action<DefaultWaitNode>? IPooledManualResetCompletionSource<DefaultWaitNode>.OnConsumed
        {
            set => consumedCallback = value;
        }
    }

    private protected interface ILockManager
    {
        bool IsLockAllowed { get; }

        void AcquireLock();
    }

    private protected interface ILockManager<in TNode> : ILockManager
        where TNode : WaitNode
    {
        void InitializeNode(TNode node);
    }

    private readonly Action<double>? contentionCounter, lockDurationCounter;
    private readonly TaskCompletionSource disposeTask;
    private protected LinkedValueTaskCompletionSource<bool>? first;
    private LinkedValueTaskCompletionSource<bool>? last;

    private protected QueuedSynchronizer()
    {
        disposeTask = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    private protected void RemoveAndDrainWaitQueue(LinkedValueTaskCompletionSource<bool> node)
    {
        if (RemoveNodeCore(node))
            DrainWaitQueue();
    }

    private protected bool IsDisposeRequested
    {
        get;
        private set;
    }

    /// <summary>
    /// Sets counter for lock contention.
    /// </summary>
    public IncrementingEventCounter LockContentionCounter
    {
        init => contentionCounter = (value ?? throw new ArgumentNullException(nameof(value))).Increment;
    }

    /// <summary>
    /// Sets counter of lock duration, in milliseconds.
    /// </summary>
    public EventCounter LockDurationCounter
    {
        init => lockDurationCounter = (value ?? throw new ArgumentNullException(nameof(value))).WriteMetric;
    }

    private bool RemoveNodeCore(LinkedValueTaskCompletionSource<bool> node)
    {
        bool isFirst;

        if (isFirst = ReferenceEquals(first, node))
            first = node.Next;

        if (ReferenceEquals(last, node))
            last = node.Previous;

        node.Detach();
        return isFirst;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    private protected bool RemoveNode(LinkedValueTaskCompletionSource<bool> node) => RemoveNodeCore(node);

    private protected virtual void DrainWaitQueue() => Debug.Assert(Monitor.IsEntered(this));

    private TNode EnqueueNode<TNode, TLockManager>(ValueTaskPool<TNode> pool, ref TLockManager manager, bool throwOnTimeout)
        where TNode : WaitNode, IPooledManualResetCompletionSource<TNode>, new()
        where TLockManager : struct, ILockManager<TNode>
    {
        Debug.Assert(Monitor.IsEntered(this));

        var node = pool.Get();
        manager.InitializeNode(node);
        node.ThrowOnTimeout = throwOnTimeout;
        node.LockDurationCounter = lockDurationCounter;
        node.ResetAge();

        if (last is null)
        {
            first = last = node;
        }
        else
        {
            last.Append(node);
            last = node;
        }

        contentionCounter?.Invoke(1L);
        return node;
    }

    private protected bool TryAcquire<TLockManager>(ref TLockManager manager)
        where TLockManager : struct, ILockManager
    {
        Debug.Assert(Monitor.IsEntered(this));

        bool result;

        if (result = manager.IsLockAllowed)
        {
            for (LinkedValueTaskCompletionSource<bool>? current = first, next; current is not null; current = next)
            {
                next = current.Next;

                if (current.IsCompleted)
                {
                    RemoveNodeCore(current);
                }
                else
                {
                    result = false;
                    goto exit;
                }
            }

            manager.AcquireLock();
        }

    exit:
        return result;
    }

    private protected ValueTask WaitWithTimeoutAsync<TNode, TLockManager>(ref TLockManager manager, ValueTaskPool<TNode> pool, TimeSpan timeout, CancellationToken token)
        where TNode : WaitNode, IPooledManualResetCompletionSource<TNode>, new()
        where TLockManager : struct, ILockManager<TNode>
    {
        Debug.Assert(Monitor.IsEntered(this));

        if (IsDisposed || IsDisposeRequested)
            return new(DisposedTask);

        if (timeout < TimeSpan.Zero && timeout != InfiniteTimeSpan)
            return ValueTask.FromException(new ArgumentOutOfRangeException(nameof(timeout)));

        if (token.IsCancellationRequested)
            return ValueTask.FromCanceled(token);

        if (TryAcquire(ref manager))
            return ValueTask.CompletedTask;

        if (timeout == TimeSpan.Zero)
            return ValueTask.FromException(new TimeoutException());

        return EnqueueNode(pool, ref manager, true).As<ISupplier<TimeSpan, CancellationToken, ValueTask>>().Invoke(timeout, token);
    }

    private protected ValueTask<bool> WaitNoTimeoutAsync<TNode, TManager>(ref TManager manager, ValueTaskPool<TNode> pool, TimeSpan timeout, CancellationToken token)
        where TNode : WaitNode, IPooledManualResetCompletionSource<TNode>, new()
        where TManager : struct, ILockManager<TNode>
    {
        Debug.Assert(Monitor.IsEntered(this));

        if (IsDisposed || IsDisposeRequested)
            return new(GetDisposedTask<bool>());

        if (timeout < TimeSpan.Zero && timeout != InfiniteTimeSpan)
            return ValueTask.FromException<bool>(new ArgumentOutOfRangeException(nameof(timeout)));

        if (token.IsCancellationRequested)
            return ValueTask.FromCanceled<bool>(token);

        if (TryAcquire(ref manager))
            return new(true);

        if (timeout == TimeSpan.Zero)
            return new(false);    // if timeout is zero fail fast

        return EnqueueNode(pool, ref manager, false).CreateTask(timeout, token);
    }

    /// <summary>
    /// Cancels all suspended callers.
    /// </summary>
    /// <param name="token">The canceled token.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="token"/> is not in canceled state.</exception>
    /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
    public void CancelSuspendedCallers(CancellationToken token)
    {
        ThrowIfDisposed();

        if (!token.IsCancellationRequested)
            throw new ArgumentOutOfRangeException(nameof(token));

        for (LinkedValueTaskCompletionSource<bool>? current = DetachWaitQueue(), next; current is not null; current = next)
        {
            next = current.CleanupAndGotoNext();
            current.TrySetCanceled(token);
        }
    }

    private protected static long ResumeSuspendedCallers(LinkedValueTaskCompletionSource<bool>? queueHead)
    {
        var count = 0L;

        for (LinkedValueTaskCompletionSource<bool>? next; queueHead is not null; queueHead = next)
        {
            next = queueHead.CleanupAndGotoNext();

            if (queueHead.TrySetResult(true))
                count += 1L;
        }

        return count;
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    private protected LinkedValueTaskCompletionSource<bool>? DetachWaitQueue()
    {
        var queueHead = first;
        first = last = null;

        return queueHead;
    }

    private void NotifyObjectDisposed()
    {
        var e = new ObjectDisposedException(GetType().Name);

        for (LinkedValueTaskCompletionSource<bool>? current = DetachWaitQueue(), next; current is not null; current = next)
        {
            next = current.CleanupAndGotoNext();
            current.TrySetException(e);
        }
    }

    /// <summary>
    /// Releases all resources associated with exclusive lock.
    /// </summary>
    /// <remarks>
    /// This method is not thread-safe and may not be used concurrently with other members of this instance.
    /// </remarks>
    /// <param name="disposing">Indicates whether the <see cref="Dispose(bool)"/> has been called directly or from finalizer.</param>
    protected override void Dispose(bool disposing)
    {
        IsDisposeRequested = true;

        if (disposing)
        {
            NotifyObjectDisposed();
            disposeTask.TrySetResult();
        }

        base.Dispose(disposing);
    }

    private protected virtual bool IsReadyToDispose => true;

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.Synchronized)]
    protected override ValueTask DisposeAsyncCore()
    {
        IsDisposeRequested = true;

        if (IsReadyToDispose)
        {
            Dispose(true);
            return ValueTask.CompletedTask;
        }

        return new(disposeTask.Task);
    }

    /// <summary>
    /// Disposes this synchronization primitive gracefully.
    /// </summary>
    /// <returns>The task representing asynchronous result.</returns>
    public new ValueTask DisposeAsync() => base.DisposeAsync();
}