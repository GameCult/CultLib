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
    }
}
