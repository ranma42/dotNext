﻿using System.Runtime.InteropServices;
using System.Text.Json;
using Debug = System.Diagnostics.Debug;

namespace DotNext.Net.Cluster.Consensus.Raft;

using Buffers;
using IO;

public partial class PersistentState
{
    /// <summary>
    /// Represents persistent log entry.
    /// </summary>
    /// <remarks>
    /// Use <see cref="TransformAsync"/> to decode the log entry.
    /// </remarks>
    [StructLayout(LayoutKind.Auto)]
    protected internal readonly struct LogEntry : IRaftLogEntry
    {
        // if null then the log entry payload is represented by buffer
        private readonly FileReader? content;
        private readonly LogEntryMetadata metadata;
        private readonly ReadOnlyMemory<byte> buffer;

        // if negative then it's a snapshot index because |snapshotIndex| > 0
        private readonly long index;

        // for regular log entry
        internal LogEntry(FileReader cachedContent, in LogEntryMetadata metadata, long index)
        {
            this.metadata = metadata;
            content = metadata.Length > 0L ? cachedContent : null;
            buffer = ReadOnlyMemory<byte>.Empty;
            this.index = index;
        }

        // for regular log entry cached in memory
        internal LogEntry(in MemoryOwner<byte> cachedContent, in LogEntryMetadata metadata, long index)
        {
            Debug.Assert(cachedContent.IsEmpty || cachedContent.Length == metadata.Length);
            this.metadata = metadata;
            content = null;
            buffer = cachedContent.Memory;
            this.index = index;
        }

        // for snapshot
        internal LogEntry(FileReader cachedContent, in SnapshotMetadata metadata)
        {
            Debug.Assert(metadata.Index > 0L);

            this.metadata = metadata.RecordMetadata;
            content = metadata.RecordMetadata.Length > 0L ? cachedContent : null;
            buffer = ReadOnlyMemory<byte>.Empty;
            index = -metadata.Index;
        }

        internal static LogEntry Initial => new();

        internal long? SnapshotIndex
        {
            get
            {
                var i = -index;
                return i > 0L ? i : null;
            }
        }

        internal bool IsBuffered => content is null;

        internal long Position => metadata.Offset;

        /// <summary>
        /// Gets the index of this log entry.
        /// </summary>
        public long Index => Math.Abs(index);

        /// <summary>
        /// Gets identifier of the command encapsulated by this log entry.
        /// </summary>
        public int? CommandId => metadata.Id;

        /// <summary>
        /// Gets a value indicating that this entry is a snapshot entry.
        /// </summary>
        public bool IsSnapshot => index < 0L;

        /// <summary>
        /// Gets length of the log entry content, in bytes.
        /// </summary>
        public long Length => metadata.Length;

        internal bool IsEmpty => Length == 0L;

        private static void Adjust(FileReader segment, in LogEntryMetadata metadata)
        {
            if (!segment.HasBufferedData || metadata.Offset < segment.FilePosition || metadata.Offset > segment.ReadPosition)
            {
                // attempt to read past or too far behind, clear the buffer
                segment.ClearBuffer();
                segment.FilePosition = metadata.Offset;
            }
            else
            {
                // the offset is in the buffered segment within the file, skip necessary bytes
                segment.Skip(metadata.Offset - segment.FilePosition);
            }

            segment.SetSegmentLength(metadata.Length);
        }

        /// <inheritdoc/>
        ValueTask IDataTransferObject.WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        {
            ValueTask result;

            if (content is not null)
            {
                Adjust(content, in metadata);
                result = new(content.CopyToAsync(writer, token));
            }
            else if (!buffer.IsEmpty)
            {
                result = writer.WriteAsync(buffer, null, token);
            }
            else
            {
                result = new();
            }

            return result;
        }

        /// <inheritdoc/>
        long? IDataTransferObject.Length => Length;

        /// <inheritdoc/>
        bool IDataTransferObject.IsReusable => content is null;

        /// <summary>
        /// Gets Raft term of this log entry.
        /// </summary>
        public long Term => metadata.Term;

        /// <summary>
        /// Gets timestamp of this log entry.
        /// </summary>
        public DateTimeOffset Timestamp => new(metadata.Timestamp, TimeSpan.Zero);

        /// <inheritdoc/>
        public ValueTask<TResult> TransformAsync<TResult, TTransformation>(TTransformation transformation, CancellationToken token)
            where TTransformation : notnull, IDataTransferObject.ITransformation<TResult>
        {
            ValueTask<TResult> result;

            if (content is not null)
            {
                Adjust(content, in metadata);
                result = transformation.TransformAsync(content, token);
            }
            else if (!buffer.IsEmpty)
            {
                result = transformation.TransformAsync(IAsyncBinaryReader.Create(buffer), token);
            }
            else
            {
                result = IDataTransferObject.Empty.TransformAsync<TResult, TTransformation>(transformation, token);
            }

            return result;
        }

        /// <summary>
        /// Attempts to obtain the payload of this log entry in the form of the memory block.
        /// </summary>
        /// <remarks>
        /// This method returns <see langword="false"/> if the log entry is not cached
        /// in the memory. Use <see cref="TransformAsync{TResult, TTransformation}(TTransformation, CancellationToken)"/>
        /// as a uniform way to deserialize this payload.
        /// </remarks>
        /// <param name="memory">The memory block representing the log entry payload.</param>
        /// <returns><see langword="true"/> if the log entry payload is available as a memory block; otherwise, <see langword="false"/>.</returns>
        public bool TryGetMemory(out ReadOnlyMemory<byte> memory)
        {
            if (content is null)
            {
                memory = buffer;
                return true;
            }

            if (IsEmpty)
            {
                memory = ReadOnlyMemory<byte>.Empty;
                return true;
            }

            memory = default;
            return false;
        }

        /// <summary>
        /// Gets reader that can be used to deserialize the content of this log entry.
        /// </summary>
        /// <returns>The binary reader providing access to the content of this log entry.</returns>
        public IAsyncBinaryReader GetReader()
        {
            IAsyncBinaryReader result;

            if (content is not null)
            {
                Adjust(content, in metadata);
                result = content;
            }
            else if (!buffer.IsEmpty)
            {
                result = IAsyncBinaryReader.Create(buffer);
            }
            else
            {
                result = IAsyncBinaryReader.Empty;
            }

            return result;
        }

        /// <summary>
        /// Deserializes JSON content represented by this log entry.
        /// </summary>
        /// <param name="typeLoader">
        /// The type loader responsible for resolving the type to be deserialized.
        /// If <see langword="null"/> then <see cref="Type.GetType(string, bool)"/> is used
        /// for type resolution.
        /// </param>
        /// <param name="options">Deserialization options.</param>
        /// <param name="token">The token that can be used to cancel the deserialization.</param>
        /// <returns>The deserialized object.</returns>
        /// <exception cref="TypeLoadException"><paramref name="typeLoader"/> unable to resolve the type.</exception>
        /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
        /// <seealso cref="CreateJsonLogEntry"/>
        public ValueTask<object?> DeserializeFromJsonAsync(Func<string, Type>? typeLoader = null, JsonSerializerOptions? options = null, CancellationToken token = default)
        {
            ValueTask<object?> result;

            if (content is not null)
            {
                Adjust(content, in metadata);
                result = DeserializeSlowAsync(typeLoader, options, token);
            }
            else if (!buffer.IsEmpty)
            {
                result = new(JsonLogEntry.Deserialize(IAsyncBinaryReader.Create(buffer), typeLoader, options));
            }
            else
            {
                result = new(default(object));
            }

            return result;
        }

        private async ValueTask<object?> DeserializeSlowAsync(Func<string, Type>? typeLoader, JsonSerializerOptions? options, CancellationToken token)
        {
            Debug.Assert(content is not null);

            using var buffer = MemoryAllocator.Allocate<byte>(metadata.Length.Truncate(), true);
            await content.ReadBlockAsync(buffer.Memory, token).ConfigureAwait(false);
            return JsonLogEntry.Deserialize(IAsyncBinaryReader.Create(buffer.Memory), typeLoader, options);
        }
    }

    /// <summary>
    /// Creates a log entry with JSON-serializable payload.
    /// </summary>
    /// <typeparam name="T">JSON-serializable type.</typeparam>
    /// <param name="content">JSON-serializable content of the log entry.</param>
    /// <param name="typeId">
    /// The type identifier required to recognize the correct type during deserialization.
    /// If <see langword="null"/> then <see cref="Type.AssemblyQualifiedName"/> of <typeparamref name="T"/> is used as type identifier.
    /// </param>
    /// <param name="options">Serialization options.</param>
    /// <returns>The log entry representing JSON-serializable content.</returns>
    /// <seealso cref="LogEntry.DeserializeFromJsonAsync"/>
    public JsonLogEntry<T> CreateJsonLogEntry<T>(T content, string? typeId = null, JsonSerializerOptions? options = null)
        => new(Term, content, typeId, options);
}