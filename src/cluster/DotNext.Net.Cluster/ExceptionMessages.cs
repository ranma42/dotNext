using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Resources;

namespace DotNext
{
    [SuppressMessage("Globalization", "CA1304", Justification = "This is culture-specific resource strings")]
    [SuppressMessage("Globalization", "CA1305", Justification = "This is culture-specific resource strings")]
    [ExcludeFromCodeCoverage]
    internal static class ExceptionMessages
    {
        private static readonly ResourceManager Resources = new ResourceManager("DotNext.ExceptionMessages", Assembly.GetExecutingAssembly());

        internal static string NoConsensus => Resources.GetString("NoConsensus");

        internal static string CannotRemoveLocalNode => Resources.GetString("CannotRemoveLocalNode");

        internal static string ReplicationRejected => Resources.GetString("ReplicationRejected");

        internal static string EntrySetIsEmpty => Resources.GetString("EntrySetIsEmpty");

        internal static string LocalNodeNotLeader => Resources.GetString("LocalNodeNotLeader");

        internal static string InvalidEntryIndex(long index) => string.Format(Resources.GetString("InvalidEntryIndex"), index);

        internal static string InvalidAppendIndex => Resources.GetString("InvalidAppendIndex");

        internal static string SnapshotDetected => Resources.GetString("SnapshotDetected");

        internal static string RangeTooBig => Resources.GetString("RangeTooBig");

        internal static string UnexpectedError => Resources.GetString("UnexpectedError");

        internal static string NoAvailableReadSessions => Resources.GetString("NoAvailableReadSessions");
    
        internal static string LockNameTooLong => Resources.GetString("LockNameTooLong");
    
        internal static string LockConflict => Resources.GetString("LockConflict");

        internal static string LockNameIsEmpty => Resources.GetString("LockNameIsEmpty");

        internal static string InvalidLockToken => Resources.GetString("InvalidLockToken");
    
        internal static string UnsupportedAddressFamily => Resources.GetString("UnsupportedAddressFamily");

        internal static string UnresolvedLocalMember => Resources.GetString("UnresolvedLocalMember");
    
        internal static string ChangesRejected => Resources.GetString("ChangesRejected");
    }
}