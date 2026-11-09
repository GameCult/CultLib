#!/usr/bin/env python3
"""Generate the Unicode 16.0 tables of the write_admitted messageId rule for the five CultNet runtimes.

The rule (contracts/cultnet/interop/write-receipt.md): a messageId is valid iff it holds a letter (Lu Ll Lt Lm Lo) or a number
(Nd Nl No) and no Default_Ignorable_Code_Point, all as of Unicode 16.0. The tables are embedded in each runtime so that no
platform's own Unicode version can change a result. Bumping the version is one coordinated change: run this script on the new
UCD files, commit the five regenerated files and the vectors that pin the new version, together.

  gen-unicode-messageid-tables.py DerivedGeneralCategory.txt DerivedCoreProperties.txt [--check]

The inputs are https://www.unicode.org/Public/16.0.0/ucd/extracted/DerivedGeneralCategory.txt and
https://www.unicode.org/Public/16.0.0/ucd/DerivedCoreProperties.txt. With --check nothing is written; the exit status is 1 if
any generated file differs from what these inputs produce (a stale or hand-edited table). Standard library only.
"""
import hashlib
import json
import re
import sys
from pathlib import Path

VERSION = "16.0.0"
ROOT = Path(__file__).resolve().parent.parent
LETTER_OR_NUMBER_CATEGORIES = {"Lu", "Ll", "Lt", "Lm", "Lo", "Nd", "Nl", "No"}

TARGETS = {
    "ts": ROOT / "packages/cultnet-ts/src/unicode-messageid-tables.ts",
    "py": ROOT / "packages/cultnet-py/src/cultnet_py/unicode_messageid_tables.py",
    "kt": ROOT / "packages/cultmesh-kotlin/src/main/kotlin/org/gamecult/cultmesh/UnicodeMessageIdTables.kt",
    "cs": ROOT / "src/GameCult.Networking/CultNetUnicodeMessageIdTables.cs",
    "rs": ROOT / "packages/cultnet-rs/src/unicode_messageid_tables.rs",
}
# The independent copy the runtimes' tests probe (first, last, and the code point either side of every range), so that deleting
# or narrowing any range of any embedded table fails a test.
RANGES_JSON = ROOT / "contracts/cultnet/interop/write-admitted-message-id-ranges.json"


def parse(path, wanted):
    """Ranges (low, high) of the lines `LOW[..HIGH] ; VALUE`, VALUE accepted by [wanted], merged and sorted."""
    points = []
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        m = re.match(r"([0-9A-F]+)(?:\.\.([0-9A-F]+))?\s*;\s*(\S+)", line)
        if m and wanted(m.group(3)):
            points.append((int(m.group(1), 16), int(m.group(2) or m.group(1), 16)))
    points.sort()
    merged = []
    for low, high in points:
        if merged and low <= merged[-1][1] + 1:
            merged[-1] = (merged[-1][0], max(merged[-1][1], high))
        else:
            merged.append((low, high))
    return merged


def flat(ranges):
    return [value for pair in ranges for value in pair]


def wrapped(items, indent, render, width=118):
    lines, current = [], indent
    for item in items:
        text = render(item) + ", "
        if len(current) + len(text) > width:
            lines.append(current.rstrip())
            current = indent
        current += text
    lines.append(current.rstrip())
    return "\n".join(lines)


def emit(target, letters, ignorable, header):
    num = lambda v: f"0x{v:X}"
    if target == "ts":
        body = lambda name, r: f"export const {name}: readonly number[] = [\n{wrapped(flat(r), '  ', num)}\n];\n"
        return f"// {header}\n\n// [low, high] pairs, sorted.\n{body('LETTER_OR_NUMBER', letters)}\n{body('DEFAULT_IGNORABLE', ignorable)}"
    if target == "py":
        body = lambda name, r: f"{name}: tuple[int, ...] = (\n{wrapped(flat(r), '    ', num)}\n)\n"
        return f"# {header}\n\n# (low, high) pairs, flattened and sorted.\n{body('LETTER_OR_NUMBER', letters)}\n{body('DEFAULT_IGNORABLE', ignorable)}"
    if target == "kt":
        body = lambda name, r: f"internal val {name}: IntArray = intArrayOf(\n{wrapped(flat(r), '    ', num)}\n)\n"
        return f"// {header}\n\npackage org.gamecult.cultmesh\n\n// (low, high) pairs, flattened and sorted.\n{body('LETTER_OR_NUMBER', letters)}\n{body('DEFAULT_IGNORABLE', ignorable)}"
    if target == "cs":
        body = lambda name, r: f"        public static readonly int[] {name} =\n        {{\n{wrapped(flat(r), '            ', num)}\n        }};\n"
        return f"// {header}\n\nnamespace GameCult.Networking\n{{\n    internal static class CultNetUnicodeMessageIdTables\n    {{\n        // (low, high) pairs, flattened and sorted.\n{body('LetterOrNumber', letters)}\n{body('DefaultIgnorable', ignorable)}    }}\n}}\n"
    body = lambda name, r: f"pub const {name}: &[(u32, u32)] = &[\n{wrapped(r, '    ', lambda p: f'({num(p[0])}, {num(p[1])})')}\n];\n"
    return f"// {header}\n\n// (low, high) pairs, sorted.\n{body('LETTER_OR_NUMBER', letters)}\n{body('DEFAULT_IGNORABLE', ignorable)}"


def main():
    args = [a for a in sys.argv[1:] if a != "--check"]
    check = "--check" in sys.argv[1:]
    if len(args) != 2:
        sys.exit(__doc__)
    gc, dcp = args
    for path, marker in ((gc, f"DerivedGeneralCategory-{VERSION}"), (dcp, f"DerivedCoreProperties-{VERSION}")):
        if marker not in Path(path).read_text(encoding="utf-8").splitlines()[0]:
            sys.exit(f"{path} is not {marker}.txt")
    letters = parse(gc, lambda v: v in LETTER_OR_NUMBER_CATEGORIES)
    ignorable = parse(dcp, lambda v: v == "Default_Ignorable_Code_Point")
    digest = hashlib.sha256(b"".join(Path(p).read_bytes() for p in (gc, dcp))).hexdigest()[:16]
    header = f"generated, Unicode {VERSION}, by scripts/gen-unicode-messageid-tables.py (inputs sha256 {digest}); do not edit"
    stale = []
    texts = {path: emit(target, letters, ignorable, header) for target, path in TARGETS.items()}
    texts[RANGES_JSON] = json.dumps(
        {"generated": header, "unicodeVersion": VERSION, "letterOrNumber": [list(r) for r in letters], "defaultIgnorable": [list(r) for r in ignorable]},
        separators=(",", ":"),
    ) + "\n"
    for path, text in texts.items():
        if check:
            if not path.exists() or path.read_text(encoding="utf-8") != text:
                stale.append(str(path.relative_to(ROOT)))
        else:
            path.write_text(text, encoding="utf-8")
    if stale:
        sys.exit("stale or hand-edited: " + ", ".join(stale))
    print(f"{'checked' if check else 'wrote'} {len(texts)} files: {len(letters)} letter-or-number ranges, {len(ignorable)} default-ignorable ranges")


if __name__ == "__main__":
    main()
