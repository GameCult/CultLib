using System.IO;

namespace GameCult.Caching
{
    /// <summary>
    /// A write was attempted at a store path that is a link (a symbolic link to a file or a directory, a Windows junction or any
    /// other reparse-point link). The write is refused before the lock, the staging file or the replace, and again once the lock
    /// is held, so the link and what it points at are untouched. Configure the link's target as the store path to write to it;
    /// reads through a live link are unchanged.
    /// </summary>
    public sealed class CultStoreLinkedException : IOException
    {
        public CultStoreLinkedException(string path)
            : base($"CultCache store {path} is a link, and a write at it would replace the link; configure the link's target as the store path.")
        {
            Path = path;
        }

        /// <summary>The store path that is a link.</summary>
        public string Path { get; }
    }
}
