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

    /// <summary>
    /// Reactive POCO presentation for one CultCache-managed document.
    /// </summary>
    public sealed class CultManagedDocument<T> where T : class
    {
        private readonly Func<T?> _read;
        private readonly Func<T, Task> _commit;
        private readonly Observable<T> _watch;

        /// <summary>
        /// Creates a managed document presentation.
        /// </summary>
        public CultManagedDocument(
            CultRecordKey key,
            Func<T?> read,
            Func<T, Task> commit,
            Observable<T> watch)
        {
            Key = key;
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _commit = commit ?? throw new ArgumentNullException(nameof(commit));
            _watch = watch ?? throw new ArgumentNullException(nameof(watch));
        }

        /// <summary>
        /// Gets the document record key.
        /// </summary>
        public CultRecordKey Key { get; }

        /// <summary>
        /// Gets the current POCO presentation.
        /// </summary>
        public T? Value => _read();

        /// <summary>
        /// Watches committed values for this document.
        /// </summary>
        public Observable<T> Watch()
        {
            return _watch;
        }

        /// <summary>
        /// Commits the current POCO value through the owner-supplied commit path.
        /// </summary>
        public Task CommitAsync()
        {
            var value = Value ?? throw new InvalidOperationException($"Document '{Key.Value}' is not present.");
            return _commit(value);
        }

        /// <summary>
        /// Replaces the current value through the owner-supplied commit path.
        /// </summary>
        public Task ReplaceAsync(T value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            return _commit(value);
        }
    }
}
