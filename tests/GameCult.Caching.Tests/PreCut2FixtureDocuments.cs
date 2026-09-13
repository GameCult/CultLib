using GameCult.Caching;
using MessagePack;

namespace GameCult.Caching.Tests
{
    [CultDocument("fixtures.precut2.item", "fixtures.precut2.item.v1")]
    [MessagePackObject(AllowPrivate = true)]
    internal sealed class PreCut2FixtureItem
    {
        [Key(0)]
        [CultName]
        public string Name = string.Empty;

        [Key(1)]
        public int Count;
    }

    [CultDocument("fixtures.precut2.note", "fixtures.precut2.note.v1")]
    [MessagePackObject(AllowPrivate = true)]
    internal sealed class PreCut2FixtureNote
    {
        [Key(0)]
        public string Text = string.Empty;
    }
}
