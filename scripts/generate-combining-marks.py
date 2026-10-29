#!/usr/bin/env python3
"""Writes the combining-mark table into both ControlText files.

The table lists every code point whose general category is Mn (nonspacing mark)
or Me (enclosing mark) in one pinned version of the Unicode Character Database,
as ranges of consecutive assigned code points. The Mac and Windows files get the
same ranges, so both platforms count marks alike whatever their own Unicode
tables say. Run it from the repository root after changing UNICODE_VERSION:

    python3 -I scripts/generate-combining-marks.py

It downloads UnicodeData.txt into a fresh temporary folder, rewrites the text
between the BEGIN and END lines in each file, and prints how many ranges it wrote.
"""
import os
import sys
import tempfile
import urllib.request

UNICODE_VERSION = "16.0.0"
SOURCE = f"https://www.unicode.org/Public/{UNICODE_VERSION}/ucd/UnicodeData.txt"
SWIFT = "Sources/ConnectorControlState/Control/ControlText.swift"
CSHARP = "windows/src/ConnectorControl.Core/State/Control/ControlText.cs"
BEGIN = "BEGIN generated combining marks"
END = "END generated combining marks"
PER_LINE = 5


def mark_ranges(path):
    """Mn and Me code points as (first, last) ranges of consecutive code points."""
    points = []
    with open(path, encoding="utf-8") as data:
        for line in data:
            fields = line.split(";")
            if fields[2] in ("Mn", "Me"):
                # No Mn or Me block is given as a <..., First>/<..., Last> pair.
                assert not fields[1].endswith(("First>", "Last>")), line
                points.append(int(fields[0], 16))
    ranges = []
    for point in sorted(points):
        if ranges and ranges[-1][1] == point - 1:
            ranges[-1][1] = point
        else:
            ranges.append([point, point])
    return ranges


def hexed(value):
    return f"0x{value:04X}"


def swift_table(ranges):
    lines = [f"    // {BEGIN}: Unicode {UNICODE_VERSION}, from scripts/generate-combining-marks.py. Do not edit by hand.",
             "    static let combiningMarks: [ClosedRange<UInt32>] = ["]
    for start in range(0, len(ranges), PER_LINE):
        row = ranges[start:start + PER_LINE]
        lines.append("        " + " ".join(f"{hexed(a)}...{hexed(b)}," for a, b in row))
    lines += ["    ]", f"    // {END}"]
    return lines


def csharp_table(ranges):
    lines = [f"    // {BEGIN}: Unicode {UNICODE_VERSION}, from scripts/generate-combining-marks.py. Do not edit by hand.",
             "    internal static readonly (int First, int Last)[] CombiningMarks =",
             "    ["]
    for start in range(0, len(ranges), PER_LINE):
        row = ranges[start:start + PER_LINE]
        lines.append("        " + " ".join(f"({hexed(a)}, {hexed(b)})," for a, b in row))
    lines += ["    ];", f"    // {END}"]
    return lines


def rewrite(path, table):
    with open(path, encoding="utf-8") as source:
        lines = source.read().split("\n")
    begin = next(i for i, line in enumerate(lines) if BEGIN in line)
    end = next(i for i, line in enumerate(lines) if END in line)
    lines[begin:end + 1] = table
    with open(path, "w", encoding="utf-8") as source:
        source.write("\n".join(lines))


def main():
    with tempfile.TemporaryDirectory() as folder:
        data = os.path.join(folder, "UnicodeData.txt")
        urllib.request.urlretrieve(SOURCE, data)
        ranges = mark_ranges(data)
    rewrite(SWIFT, swift_table(ranges))
    rewrite(CSHARP, csharp_table(ranges))
    print(f"{len(ranges)} ranges of Mn and Me code points, Unicode {UNICODE_VERSION}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
