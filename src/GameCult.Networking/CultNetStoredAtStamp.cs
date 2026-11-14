#nullable enable
using System;

namespace GameCult.Networking
{
    // The one shape a peer's storedAt must have: RFC 3339 date-time, YYYY-MM-DDThh:mm:ss, an optional fraction of 1 to 9 digits,
    // then Z or +hh:mm / -hh:mm. Uppercase T and Z, ASCII digits, no whitespace, a real calendar date, seconds 00-59, years
    // 0001-9999 for both the local date and the UTC instant, offset hours 00-23 and minutes 00-59. Checked by shape and range, never by DateTimeOffset.TryParse, which is
    // culture-aware and takes far more than this (tests/vectors/stored-at-stamp, variants:ruling:raw-put-without-stamp-refused).
    internal static class CultNetStoredAtStamp
    {
        public static bool IsStamp(string? text)
        {
            if (text == null || text.Length < 20) return false;
            if (!Digits(text, 0, 4, out var year) || text[4] != '-' || !Digits(text, 5, 2, out var month) || text[7] != '-'
                || !Digits(text, 8, 2, out var day) || text[10] != 'T' || !Digits(text, 11, 2, out var hour) || text[13] != ':'
                || !Digits(text, 14, 2, out var minute) || text[16] != ':' || !Digits(text, 17, 2, out var second))
                return false;
            if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
                || hour > 23 || minute > 59 || second > 59)
                return false;

            var i = 19;
            if (text[i] == '.')
            {
                var start = ++i;
                while (i < text.Length && IsDigit(text[i])) i++;
                if (i - start is < 1 or > 9) return false;
            }

            if (i >= text.Length) return false;
            if (text[i] == 'Z') return i == text.Length - 1;
            if (!(text.Length - i == 6 && (text[i] == '+' || text[i] == '-')
                && Digits(text, i + 1, 2, out var offsetHour) && text[i + 3] == ':' && Digits(text, i + 4, 2, out var offsetMinute)
                && offsetHour <= 23 && offsetMinute <= 59))
                return false;
            // The UTC instant must lie in years 0001-9999 too: local minus offset may not leave DateTime's range.
            var offsetTicks = (offsetHour * 60L + offsetMinute) * TimeSpan.TicksPerMinute * (text[i] == '-' ? -1 : 1);
            var utcTicks = new DateTime(year, month, day, hour, minute, second).Ticks - offsetTicks;
            return utcTicks >= DateTime.MinValue.Ticks && utcTicks <= DateTime.MaxValue.Ticks;
        }

        private static bool IsDigit(char c) => c is >= '0' and <= '9';

        private static bool Digits(string text, int start, int count, out int value)
        {
            value = 0;
            for (var i = start; i < start + count; i++)
            {
                if (i >= text.Length || !IsDigit(text[i])) return false;
                value = value * 10 + (text[i] - '0');
            }

            return true;
        }
    }

    // A peer's raw put whose storedAt is no stamp: refused message_invalid, field document, nothing stored or minted.
    internal sealed class CultNetStoredAtStampException : Exception
    {
        public CultNetStoredAtStampException() : base("a raw put document must carry a storedAt stamp") { }
    }
}
