using Debug = System.Diagnostics.Debug;

namespace DotNext.Net.Cluster.Consensus.Raft.TransportServices;

internal interface IClientExchange : IExchange
{
    Task Task { get; }

    ClusterMemberId Sender { set; }
}

internal interface IClientExchange<T> : IClientExchange
{
    new Task<T> Task { get; }

    Task IClientExchange.Task => Task;
}

internal abstract class ClientExchange<T> : TaskCompletionSource<T>, IClientExchange<T>
{
    private protected ClusterMemberId sender;

    private protected ClientExchange()
        : base(TaskCreationOptions.RunContinuationsAsynchronously)
    {
    }

    ClusterMemberId IClientExchange.Sender
    {
        set => sender = value;
    }

    public abstract ValueTask<bool> ProcessInboundMessageAsync(PacketHeaders headers, ReadOnlyMemory<byte> payload, CancellationToken token);

    public abstract ValueTask<(PacketHeaders Headers, int BytesWritten, bool)> CreateOutboundMessageAsync(Memory<byte> payload, CancellationToken token);

    private protected virtual void OnException(Exception e)
    {
    }

    void IExchange.OnException(Exception e)
    {
        if (e is OperationCanceledException cancellation ? TrySetCanceled(cancellation.CancellationToken) : TrySetException(e))
            OnException(e);
    }

    private protected virtual void OnCanceled(CancellationToken token)
    {
    }

    void IExchange.OnCanceled(CancellationToken token)
    {
        if (TrySetCanceled(token))
            OnCanceled(token);
    }
}

internal abstract class ClientExchange : ClientExchange<Result<bool>>
{
    private protected readonly long currentTerm;

    private protected ClientExchange(long term) => currentTerm = term;

    public sealed override ValueTask<bool> ProcessInboundMessageAsync(PacketHeaders headers, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        Debug.Assert(headers.Control == FlowControl.Ack, "Unexpected response", $"Message type {headers.Type} control {headers.Control}");
        TrySetResult(IExchange.ReadResult(payload.Span));
        return new(false);
    }
}