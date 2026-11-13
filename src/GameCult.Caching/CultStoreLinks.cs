using System.IO;

namespace GameCult.Caching
{
    // The one decision on whether a store-owned file is a link, shared by every store kind that replaces files.
    internal static class CultStoreLinks
    {
        // A path with nothing at it reports no attributes at all (-1). Any reparse point counts as a link, whether or not its
        // target is there.
        internal static bool IsLink(string path)
        {
            var attributes = new FileInfo(path).Attributes;
            return (int)attributes != -1 && (attributes & FileAttributes.ReparsePoint) != 0;
        }

        internal static void Refuse(string path)
        {
            if (IsLink(path))
                throw new CultStoreLinkedException(path);
        }
    }
}
