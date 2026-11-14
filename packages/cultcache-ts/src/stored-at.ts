// The one shape a `storedAt` stamp has (tests/vectors/stored-at-stamp/README.md; reference `parse_stored_at` in cultcache-py): an
// RFC 3339 date-time `YYYY-MM-DDThh:mm:ss`, an optional fraction of 1 to 9 digits, and `Z` or `+hh:mm` / `-hh:mm` (hours 00-23,
// minutes 00-59). A real calendar date, seconds 00-59, and an instant within years 0001-9999. Checked here, not left to
// `Date.parse`, which accepts shapes this one does not and whose accepted set differs by engine.
const STAMP = /^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.[0-9]{1,9})?(?:Z|([+-])([01][0-9]|2[0-3]):([0-5][0-9]))$/;

const MS_PER_MINUTE = 60_000;

/** True when `text` is a string that is a stamp. Anything else (missing, empty, wrong type, other shapes) is not. */
export function isStoredAtStamp(text: unknown): text is string {
  if (typeof text !== "string") return false;
  const shape = STAMP.exec(text);
  if (shape === null) return false;
  const [year, month, day, hour, minute, second] = shape.slice(1, 7).map(Number) as [number, number, number, number, number, number];
  const leap = (year % 4 === 0 && year % 100 !== 0) || year % 400 === 0;
  const daysInMonth = [31, leap ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31][month - 1];
  if (year < 1 || month < 1 || month > 12 || day < 1 || day > (daysInMonth as number) || hour > 23 || minute > 59 || second > 59) {
    return false;
  }
  // The UTC instant must stay within years 0001-9999, so an offset can push a boundary date out of range.
  const local = new Date(0);
  local.setUTCFullYear(year, month - 1, day);
  local.setUTCHours(hour, minute, second, 0);
  const offsetMinutes = shape[7] === undefined ? 0 : (shape[7] === "-" ? -1 : 1) * (Number(shape[8]) * 60 + Number(shape[9]));
  const utcMs = local.getTime() - offsetMinutes * MS_PER_MINUTE;
  const first = new Date(0);
  first.setUTCFullYear(1, 0, 1);
  first.setUTCHours(0, 0, 0, 0);
  const last = new Date(0);
  last.setUTCFullYear(9999, 11, 31);
  last.setUTCHours(23, 59, 59, 999);
  return utcMs >= first.getTime() && utcMs <= last.getTime();
}
