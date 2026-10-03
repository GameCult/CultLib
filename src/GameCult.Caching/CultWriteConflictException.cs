using System;
using System.Collections.Generic;

namespace GameCult.Caching
{
    /// <summary>
    /// A write refused because the store it would land in holds a variant and has changed since its writer last read it. A variant
    /// resolves against other records, and a write never decodes a record it did not stage, so it cannot judge records another
    /// writer changed: it writes only while every record header in the store equals what the writer last read or wrote. Nothing is
    /// written. <see cref="RecordKeys"/> are the keys the write would have written or removed and <see cref="ChangedKeys"/> the keys
    /// whose durable header differs from what the writer last read, both ordinal-sorted. The cache has reloaded all of them as the
    /// store holds them and forgotten the staged changes at those keys, so a retry is judged against that. When reloading failed,
    /// nothing changed and <see cref="Exception.InnerException"/> is the cause.
    /// </summary>
    public sealed class CultWriteConflictException : InvalidOperationException
    {
        public CultWriteConflictException(string message, IReadOnlyList<string> recordKeys, IReadOnlyList<string> changedKeys, Exception? inner = null)
            : base(message, inner)
        {
            RecordKeys = recordKeys;
            ChangedKeys = changedKeys;
        }

        public IReadOnlyList<string> RecordKeys { get; }
        public IReadOnlyList<string> ChangedKeys { get; }
    }
}
