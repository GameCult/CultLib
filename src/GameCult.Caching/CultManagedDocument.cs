using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using R3;

namespace GameCult.Caching
{
    /// <summary>
    /// Describes how a CultCache-managed document changed.
    /// </summary>
    public enum CultCacheDocumentChangeKind
    {
        /// <summary>
        /// A document was added.
        /// </summary>
        Added,

        /// <summary>
        /// A document was updated.
        /// </summary>
        Updated,

        /// <summary>
        /// A document was removed.
        /// </summary>
        Removed
    }

    /// <summary>
    /// One reactive CultCache document change.
    /// </summary>
    public sealed class CultCacheDocumentChange<T> where T : class
    {
        /// <summary>
        /// Creates a document change.
        /// </summary>
        public CultCacheDocumentChange(
            CultCacheDocumentChangeKind kind,
            CultRecordKey key,
            T? document,
            T? previousDocument)
        {
            Kind = kind;
            Key = key;
            Document = document;
            PreviousDocument = previousDocument;
        }

        /// <summary>
        /// Gets the change kind.
        /// </summary>
        public CultCacheDocumentChangeKind Kind { get; }

        /// <summary>
        /// Gets the document record key.
        /// </summary>
        public CultRecordKey Key { get; }

        /// <summary>
        /// Gets the current POCO presentation, when present.
        /// </summary>
        public T? Document { get; }

        /// <summary>
        /// Gets the previous POCO presentation, when present.
        /// </summary>
        public T? PreviousDocument { get; }
    }
}
