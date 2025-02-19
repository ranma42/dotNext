using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Debug = System.Diagnostics.Debug;

namespace DotNext.Net.Cluster.Consensus.Raft.Tcp;

using Buffers;
using TransportServices;
using static IO.StreamExtensions;

internal abstract class TcpTransport : Disposable, INetworkTransport
{
    private const int PacketPrologueSize = PacketHeaders.Size + sizeof(int);

    private sealed class TcpStream : NetworkStream
    {
        internal TcpStream(Socket socket, bool owns)
            : base(socket, owns)
        {
        }

        internal bool Connected => Socket.Connected;

        internal EndPoint? RemoteEndPoint => Socket.RemoteEndPoint;
    }

    private protected class PacketStream : Disposable
    {
        private readonly TcpStream transport;

        // actual stream for Network I/O
        // can be of type TcpStream or SslStream
        private readonly Stream networkStream;
        private protected readonly SslStream? ssl;

        internal PacketStream(Socket socket, bool owns, bool useSsl)
        {
            transport = new TcpStream(socket, owns);
            if (useSsl)
            {
                ssl = new SslStream(transport, true);
                networkStream = ssl;
            }
            else
            {
                ssl = null;
                networkStream = transport;
            }
        }

        internal bool Connected => transport.Connected;

        private protected EndPoint? RemoteEndPoint => transport.RemoteEndPoint;

        private protected ValueTask WritePacket(PacketHeaders headers, Memory<byte> buffer, int count, CancellationToken token)
        {
            var writer = new SpanWriter<byte>(buffer.Span);

            // write headers
            headers.Format(ref writer);
            writer.WriteInt32(count, true);
            Debug.Assert(writer.WrittenCount == PacketPrologueSize);

            // transmit packet to the remote endpoint
            return networkStream.WriteAsync(buffer.Slice(0, writer.WrittenCount + count), token);
        }

        private static void ReadPrologue(ReadOnlyMemory<byte> prologue, out PacketHeaders headers, out int count)
        {
            var reader = new SpanReader<byte>(prologue.Span);

            headers = new PacketHeaders(ref reader);
            count = reader.ReadInt32(true);
            Debug.Assert(reader.ConsumedCount == PacketPrologueSize);
        }

        private protected async ValueTask<(PacketHeaders Headers, ReadOnlyMemory<byte> Payload)> ReadPacket(Memory<byte> buffer, CancellationToken token)
        {
            // read headers and number of bytes
            await networkStream.ReadBlockAsync(buffer.Slice(0, PacketPrologueSize), token).ConfigureAwait(false);
            ReadPrologue(buffer, out var headers, out var count);
            buffer = buffer.Slice(0, count);
            await networkStream.ReadBlockAsync(buffer, token).ConfigureAwait(false);
            return (headers, buffer);
        }

        internal void Close(int timeout) => transport.Close(timeout);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ssl?.Dispose();
                transport.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private const int MinTransmissionBlockSize = 300;

    internal readonly IPEndPoint Address;
    private protected readonly ILogger logger;
    private readonly MemoryAllocator<byte> allocator;
    private int transmissionBlockSize;

    private protected TcpTransport(IPEndPoint address, MemoryAllocator<byte> allocator, ILoggerFactory loggerFactory)
    {
        Address = address;
        logger = loggerFactory.CreateLogger(GetType());
        this.allocator = allocator;
        transmissionBlockSize = MinTransmissionBlockSize;
        LingerOption = new LingerOption(false, 0);
        Ttl = 64;
    }

    private protected static void ConfigureSocket(Socket socket, LingerOption linger, byte ttl)
    {
        socket.NoDelay = true;
        socket.Ttl = ttl;
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Linger, linger);
    }

    internal static int ValidateTranmissionBlockSize(int value)
        => value >= MinTransmissionBlockSize ? value : throw new ArgumentOutOfRangeException(nameof(value));

    internal int TransmissionBlockSize
    {
        get => transmissionBlockSize;
        set => transmissionBlockSize = ValidateTranmissionBlockSize(value);
    }

    internal LingerOption LingerOption
    {
        get;
        set;
    }

    internal byte Ttl
    {
        get;
        set;
    }

    IPEndPoint INetworkTransport.Address => Address;

    private protected MemoryOwner<byte> AllocTransmissionBlock()
        => allocator(transmissionBlockSize);

    private protected static Memory<byte> AdjustToPayload(Memory<byte> packet)
        => packet.Slice(PacketPrologueSize);
}