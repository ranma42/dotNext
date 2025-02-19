namespace DotNext.Threading.Tasks;

internal abstract class LinkedValueTaskCompletionSource<T> : ValueTaskCompletionSource<T>
{
    private LinkedValueTaskCompletionSource<T>? previous, next;

    private protected LinkedValueTaskCompletionSource(bool runContinuationsAsynchronously = true)
        : base(runContinuationsAsynchronously)
    {
    }

    internal LinkedValueTaskCompletionSource<T>? Next => next;

    internal LinkedValueTaskCompletionSource<T>? Previous => previous;

    internal bool IsNotRoot => next is not null || previous is not null;

    internal void Append(LinkedValueTaskCompletionSource<T> node)
    {
        node.next = next;
        node.previous = this;
        next = node;
    }

    internal void Detach()
    {
        if (previous is not null)
            previous.next = next;
        if (next is not null)
            next.previous = previous;
        next = previous = null;
    }

    internal virtual LinkedValueTaskCompletionSource<T>? CleanupAndGotoNext()
    {
        var next = this.next;
        this.next = previous = null;
        return next;
    }
}