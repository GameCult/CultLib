using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using R3;

namespace GameCult.Caching
{
    public enum CultCacheDocumentChangeKind
    {
        Added,

        Updated,

        Removed
    }

    public sealed class CultCacheDocumentChange<T> where T : class
    {
        public CultCacheDocumentChange(
            CultCacheDocumentChangeKind kind,
            CultRecordKey key,
            T? document,
            T? previousDocument,
            long sequence = 0)
        {
            Kind = kind;
            Key = key;
            Document = document;
            PreviousDocument = previousDocument;
            Sequence = sequence;
        }

        // Per-cache, in memory, assigned at admission. A consumer keeping a latest value ignores a lower one.
        public long Sequence { get; }

        public CultCacheDocumentChangeKind Kind { get; }

        public CultRecordKey Key { get; }

        public T? Document { get; }

        public T? PreviousDocument { get; }
    }
}
