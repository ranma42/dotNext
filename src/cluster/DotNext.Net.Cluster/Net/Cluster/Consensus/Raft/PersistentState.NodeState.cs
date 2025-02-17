﻿using System.IO.MemoryMappedFiles;

namespace DotNext.Net.Cluster.Consensus.Raft;

using IO.Log;
using Threading;

public partial class PersistentState
{
    /*
        State file format:
        8 bytes = Term
        8 bytes = CommitIndex
        8 bytes = LastApplied
        8 bytes = LastIndex
        1 byte = presence of cluster member id
        sizeof(ClusterMemberId) = last vote
     */
    private sealed class NodeState : Disposable
    {
        internal static readonly Func<NodeState, long, bool> IsCommittedPredicate = IsCommitted;

        private const byte True = 1;
        private const byte False = 0;
        private const string FileName = "node.state";
        private const long Capacity = 128;
        private const long TermOffset = 0L;
        private const long CommitIndexOffset = TermOffset + sizeof(long);
        private const long LastAppliedOffset = CommitIndexOffset + sizeof(long);
        private const long LastIndexOffset = LastAppliedOffset + sizeof(long);
        private const long LastVotePresenceOffset = LastIndexOffset + sizeof(long);
        private const long LastVoteOffset = LastVotePresenceOffset + sizeof(byte);

        private readonly MemoryMappedFile mappedFile;
        private readonly MemoryMappedViewAccessor stateView;

        // boxed ClusterMemberId or null if there is not last vote stored
        private volatile object? votedFor;
        private long term, commitIndex, lastIndex, lastApplied;  // volatile

        internal NodeState(DirectoryInfo location)
        {
            mappedFile = MemoryMappedFile.CreateFromFile(Path.Combine(location.FullName, FileName), FileMode.OpenOrCreate, null, Capacity, MemoryMappedFileAccess.ReadWrite);
            stateView = mappedFile.CreateViewAccessor();
            term = stateView.ReadInt64(TermOffset);
            commitIndex = stateView.ReadInt64(CommitIndexOffset);
            lastIndex = stateView.ReadInt64(LastIndexOffset);
            lastApplied = stateView.ReadInt64(LastAppliedOffset);
            var hasLastVote = ValueTypeExtensions.ToBoolean(stateView.ReadByte(LastVotePresenceOffset));
            if (hasLastVote)
            {
                stateView.Read(LastVoteOffset, out ClusterMemberId votedFor);
                this.votedFor = votedFor;
            }
        }

        internal void Flush() => stateView.Flush();

        internal long CommitIndex
        {
            get => commitIndex.VolatileRead();
            set
            {
                stateView.Write(CommitIndexOffset, value);
                commitIndex.VolatileWrite(value);
            }
        }

        private static bool IsCommitted(NodeState state, long index) => index <= state.CommitIndex;

        internal long LastApplied
        {
            get => lastApplied.VolatileRead();
            set
            {
                stateView.Write(LastAppliedOffset, value);
                lastApplied.VolatileWrite(value);
            }
        }

        internal long LastIndex
        {
            get => lastIndex.VolatileRead();
            set
            {
                stateView.Write(LastIndexOffset, value);
                lastIndex.VolatileWrite(value);
            }
        }

        internal long TailIndex => LastIndex + 1L;

        internal long Term => term.VolatileRead();

        internal void UpdateTerm(long value, bool resetLastVote)
        {
            stateView.Write(TermOffset, value);
            if (resetLastVote)
            {
                votedFor = null;
                stateView.Write(LastVotePresenceOffset, False);
            }

            stateView.Flush();
            term.VolatileWrite(value);
        }

        internal long IncrementTerm()
        {
            var result = term.IncrementAndGet();
            stateView.Write(TermOffset, result);
            stateView.Flush();
            return result;
        }

        internal bool IsVotedFor(in ClusterMemberId? expected) => IPersistentState.IsVotedFor(votedFor, expected);

        internal void UpdateVotedFor(ClusterMemberId? member)
        {
            if (member.HasValue)
            {
                var id = member.GetValueOrDefault();
                votedFor = id;
                stateView.Write(LastVotePresenceOffset, True);
                stateView.Write(LastVoteOffset, ref id);
            }
            else
            {
                votedFor = null;
                stateView.Write(LastVotePresenceOffset, False);
            }

            stateView.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                stateView.Dispose();
                mappedFile.Dispose();
                votedFor = null;
            }

            base.Dispose(disposing);
        }
    }

    private readonly NodeState state;
    private long lastTerm;  // term of last committed entry

    /// <summary>
    /// Gets the index of the last committed log entry.
    /// </summary>
    public long LastCommittedEntryIndex => state.CommitIndex;

    /// <summary>
    /// Gets the index of the last uncommitted log entry.
    /// </summary>
    public long LastUncommittedEntryIndex => state.LastIndex;

    /// <summary>
    /// Gets the index of the last committed log entry applied to underlying state machine.
    /// </summary>
    public long LastAppliedEntryIndex => state.LastApplied;
}