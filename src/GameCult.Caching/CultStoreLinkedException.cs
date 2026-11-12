using System.IO;

namespace GameCult.Caching
{
    /// <summary>
    /// A write was attempted at a single-file store path that is a link (a symbolic link to a file or a directory, a Windows
    /// junction or any other reparse-point link). The write is refused before the lock, the staging file or the replace, so the
    /// link and what it points at are untouched. Configure the link's target as the store path to write to it; reads through a
    /// live link are unchanged. <see cref="IOException.Message"/> names the path.
    /// </summary>
    public sealed class CultStoreLinkedException : IOException
    {
        public CultStoreLinkedException(string message) : base(message)
        {
        }
    }
}
