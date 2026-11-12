using System.IO;

namespace GameCult.Caching
{
    /// <summary>
    /// A write was attempted at a store path that is a link (a symbolic link to a file or a directory, a Windows junction or any
    /// other reparse-point link). The write is refused before the lock, the staging file or the replace, and again once the lock
    /// is held, so a link that exists when a writer takes the lock leaves the link and its target untouched. A link created by
    /// something that ignores the lock after that check, while the commit builds and writes the new image, is still replaced;
    /// that window cannot be closed without native no-follow opens. Configure the link's target as the store path to write to
    /// it; reads through a live link are unchanged.
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
