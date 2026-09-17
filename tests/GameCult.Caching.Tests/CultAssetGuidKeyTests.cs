#nullable enable
using NUnit.Framework;

namespace GameCult.Caching.Tests
{
    public class CultAssetGuidKeyTests
    {
        [Test]
        public void BareGuidParsesWithNoSubAsset()
        {
            Assert.That(CultAssetGuidKey.TryParse("0123456789abcdef0123456789abcdef", out var guid, out var sub), Is.True);
            Assert.That(guid, Is.EqualTo("0123456789abcdef0123456789abcdef"));
            Assert.That(sub, Is.Null);
        }

        [Test]
        public void GuidWithSubAssetNameParses()
        {
            Assert.That(CultAssetGuidKey.TryParse("abc123[Icon_Small]", out var guid, out var sub), Is.True);
            Assert.That(guid, Is.EqualTo("abc123"));
            Assert.That(sub, Is.EqualTo("Icon_Small"));
        }

        [Test]
        public void SubAssetNameMayContainSpaces()
        {
            Assert.That(CultAssetGuidKey.TryParse("abc123[Icon Small Two]", out var guid, out var sub), Is.True);
            Assert.That(guid, Is.EqualTo("abc123"));
            Assert.That(sub, Is.EqualTo("Icon Small Two"));
        }

        [Test]
        public void SubAssetNameMayContainBrackets()
        {
            Assert.That(CultAssetGuidKey.TryParse("abc123[Icon[2]]", out var guid, out var sub), Is.True);
            Assert.That(guid, Is.EqualTo("abc123"));
            Assert.That(sub, Is.EqualTo("Icon[2]"));
        }

        [TestCase("")]
        [TestCase("[name]")]
        [TestCase("abc123[")]
        [TestCase("abc123[name")]
        [TestCase("abc123]")]
        [TestCase("abc123[]")]
        public void MalformedInputDoesNotParse(string key)
        {
            var ok = CultAssetGuidKey.TryParse(key, out var guid, out var sub);
            if (key == "abc123]")
            {
                // No '[' at all: treated as a literal bare key, not a bracket form.
                Assert.That(ok, Is.True);
                Assert.That(guid, Is.EqualTo("abc123]"));
                Assert.That(sub, Is.Null);
                return;
            }

            Assert.That(ok, Is.False);
        }

        [Test]
        public void FormatRoundTripsBareGuid()
        {
            Assert.That(CultAssetGuidKey.Format("abc123", null), Is.EqualTo("abc123"));
            Assert.That(CultAssetGuidKey.Format("abc123", string.Empty), Is.EqualTo("abc123"));
        }

        [Test]
        public void FormatRoundTripsSubAsset()
        {
            Assert.That(CultAssetGuidKey.Format("abc123", "Icon_Small"), Is.EqualTo("abc123[Icon_Small]"));
        }

        [Test]
        public void SubAssetNameWithLeadingAndTrailingSpacesRoundTrips()
        {
            // A name.Trim() mutant would strip the spaces on the way out; Addressables sub-asset
            // names are taken verbatim, so the exact bytes between the brackets must survive.
            Assert.That(CultAssetGuidKey.TryParse("abc123[ Icon Small ]", out var guid, out var sub), Is.True);
            Assert.That(guid, Is.EqualTo("abc123"));
            Assert.That(sub, Is.EqualTo(" Icon Small "));
        }

        [Test]
        public void FormatThenTryParseRoundTripsBareGuid()
        {
            var key = CultAssetGuidKey.Format("abc123", null);
            Assert.That(CultAssetGuidKey.TryParse(key, out var guid, out var sub), Is.True);
            Assert.That(guid, Is.EqualTo("abc123"));
            Assert.That(sub, Is.Null);
        }

        [Test]
        public void FormatThenTryParseRoundTripsSubAsset()
        {
            var key = CultAssetGuidKey.Format("abc123", "Icon[2]");
            Assert.That(CultAssetGuidKey.TryParse(key, out var guid, out var sub), Is.True);
            Assert.That(guid, Is.EqualTo("abc123"));
            Assert.That(sub, Is.EqualTo("Icon[2]"));
        }

        [Test]
        public void FormatOfNullOrEmptyGuidReturnsEmptyString()
        {
            Assert.That(CultAssetGuidKey.Format(null!, null), Is.EqualTo(string.Empty));
            Assert.That(CultAssetGuidKey.Format(string.Empty, null), Is.EqualTo(string.Empty));
            Assert.That(CultAssetGuidKey.Format(null!, "Icon"), Is.EqualTo("[Icon]"));
        }

        [Test]
        public void BareGuidPreservesCase()
        {
            // No lowercasing: a mutant that normalizes case would still pass every other test here
            // since the fixtures above are already lowercase.
            Assert.That(CultAssetGuidKey.TryParse("ABC123DEF456", out var guid, out _), Is.True);
            Assert.That(guid, Is.EqualTo("ABC123DEF456"));
        }

        [Test]
        public void FormatOfSubAssetNameContainingBracketsMatchesAddressablesSplit()
        {
            // Documents the split ruled for Addressables-shaped keys: "g[Icon[2]]" parses back to
            // name "Icon[2]", i.e. Format takes everything up to the FIRST '[' as the guid and
            // everything between it and the LAST ']' as the name, brackets included.
            var key = CultAssetGuidKey.Format("g", "Icon[2]");
            Assert.That(key, Is.EqualTo("g[Icon[2]]"));
            Assert.That(CultAssetGuidKey.TryParse(key, out var guid, out var sub), Is.True);
            Assert.That(guid, Is.EqualTo("g"));
            Assert.That(sub, Is.EqualTo("Icon[2]"));
        }
    }
}
