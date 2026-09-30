using System;

namespace GameCult.Caching
{
    /// <summary>
    /// A store file this runtime cannot read: not exactly one complete store (truncated, bytes after it, a value that is not a
    /// store, a missing or extra top-level slot), a format or record it does not know, or a body it cannot decode. Open, flush,
    /// commit and CultMesh's single-file reads all refuse a file with this one exception, and a refused file is left as it was.
    /// The underlying cause, where there is one, is the inner exception.
    /// </summary>
    public sealed class CultStoreUnreadableException : NotSupportedException
    {
        public CultStoreUnreadableException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }
}
