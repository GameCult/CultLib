#nullable enable
using NUnit.Framework;

namespace GameCult.Networking.Tests
{
    // Q-J (docs/cultnet-selection-cut.md, section 2 "Numbers", 2026-09-22): the wire's canonical
    // decimal form - the door's grammar and the comparator that reads it. Pure string-in functions,
    // independent of the cache and the evaluator; CultDocumentSelectionSurfaceTests (GameCult.Caching.Tests)
    // pins the row side's rendering of the same form.
    public sealed class CultNetCanonicalNumberTests
    {
        [TestCase("0")]
        [TestCase("1")]
        [TestCase("-1")]
        [TestCase("10")]
        [TestCase("0.5")]
        [TestCase("-0.5")]
        [TestCase("9007199254740993")]
        [TestCase("123.456")]
        public void IsCanonicalAcceptsCanonicalSpellings(string value) =>
            Assert.That(CultNetCanonicalNumber.IsCanonical(value), Is.True);

        // The door's named mutants: a leading '+', a trailing fractional zero, a leading integer zero,
        // exponent notation, and negative zero are each refused, never normalised.
        [TestCase("+1")]
        [TestCase("1.0")]
        [TestCase("01")]
        [TestCase("1e3")]
        [TestCase("1E3")]
        [TestCase("-0")]
        [TestCase("")]
        [TestCase(" 1")]
        [TestCase("1.")]
        [TestCase("1..5")]
        public void IsCanonicalRefusesNonCanonicalSpellings(string value) =>
            Assert.That(CultNetCanonicalNumber.IsCanonical(value), Is.False);

        [Test]
        public void IsCanonicalRefusesNull() =>
            Assert.That(CultNetCanonicalNumber.IsCanonical(null), Is.False);

        // The comparator: sign first, then the integer part's length, then its digits, then the
        // fraction padded on the right - never a lexicographic compare of the whole string, which
        // would rank "10" below "9".
        [Test]
        public void CompareOrdersByMagnitudeNotLexicographically()
        {
            Assert.That(CultNetCanonicalNumber.Compare("10", "9"), Is.GreaterThan(0));
            Assert.That(CultNetCanonicalNumber.Compare("9", "10"), Is.LessThan(0));
        }

        [Test]
        public void CompareOrdersNegativeBelowPositive()
        {
            Assert.That(CultNetCanonicalNumber.Compare("-1", "1"), Is.LessThan(0));
            Assert.That(CultNetCanonicalNumber.Compare("1", "-1"), Is.GreaterThan(0));
            Assert.That(CultNetCanonicalNumber.Compare("-10", "-2"), Is.LessThan(0));
        }

        // Same leading digit, different length: "100" > "99" even though '1' < '9' as a bare character
        // comparison - this is what a comparator that skips the integer part's length gets wrong.
        [Test]
        public void CompareOrdersByIntegerPartLengthBeforeDigits()
        {
            Assert.That(CultNetCanonicalNumber.Compare("100", "99"), Is.GreaterThan(0));
            Assert.That(CultNetCanonicalNumber.Compare("99", "100"), Is.LessThan(0));
        }

        // "1.5" vs "1.45": padded to "50" vs "45" so 1.5 > 1.45. Comparing the raw, unpadded fraction
        // strings would get this wrong ("5" > "45" as characters is true by luck, "45" > "5" as strings
        // sorted by length first is not the rule either) - padding to equal length is the rule.
        [Test]
        public void CompareOrdersFractionsByPaddedDigitsNotRawLength()
        {
            Assert.That(CultNetCanonicalNumber.Compare("1.5", "1.45"), Is.GreaterThan(0));
            Assert.That(CultNetCanonicalNumber.Compare("1.45", "1.5"), Is.LessThan(0));
        }

        [Test]
        public void CompareTreatsEqualCanonicalStringsAsEqual()
        {
            Assert.That(CultNetCanonicalNumber.Compare("5", "5"), Is.EqualTo(0));
            Assert.That(CultNetCanonicalNumber.Compare("-0.5", "-0.5"), Is.EqualTo(0));
        }

        // The row-rendering mutant named in section 2 "Numbers": a long past 2^53 (where double loses
        // integer precision) must still compare correctly once rendered. This pins the comparator's
        // side of that guarantee; CultDocumentSelectionSurfaceTests pins the cache's rendering side.
        [Test]
        public void CompareDistinguishesALongPast2Pow53FromItsFloat64Rounding()
        {
            Assert.That(CultNetCanonicalNumber.Compare("9007199254740993", "9007199254740992"), Is.GreaterThan(0));
        }
    }
}
