import Foundation

/// The one timestamp format a collection document carries: ISO 8601 UTC to the second, e.g.
/// "2026-09-21T14:02:11Z". Written by hand from UTC components rather than through a locale-aware
/// formatter, so a machine set to a non-Gregorian calendar or an Arabic-numeral locale still
/// writes the digits every other machine reads.
///
/// Mirror: windows/src/ConnectorControl.Core/IsoTimestamp.cs
public enum IsoTimestamp {
    public static func string(from date: Date) -> String {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let c = calendar.dateComponents([.year, .month, .day, .hour, .minute, .second], from: date)
        return String(format: "%04d-%02d-%02dT%02d:%02d:%02dZ",
                      c.year ?? 0, c.month ?? 0, c.day ?? 0, c.hour ?? 0, c.minute ?? 0, c.second ?? 0)
    }

    /// The calendar date here, "2026-09-21": what an imported copy records as the day it
    /// arrived. Local rather than UTC, because that date is read by the person who did the
    /// importing and has to be the day they lived through — and built from the same hand-written
    /// digits, so a machine on a non-Gregorian calendar still writes the ones everyone reads.
    public static func localDate(from date: Date) -> String {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone.current
        let c = calendar.dateComponents([.year, .month, .day], from: date)
        return String(format: "%04d-%02d-%02d", c.year ?? 0, c.month ?? 0, c.day ?? 0)
    }
}
