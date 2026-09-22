using System.Globalization;

namespace ConnectorControl.Core;

/// <summary>
/// The one timestamp format a collection document carries: ISO 8601 UTC to the second, e.g.
/// "2026-09-21T14:02:11Z". Written through the invariant culture from UTC components, so a
/// machine set to a non-Gregorian calendar or an Arabic-numeral locale still writes the digits
/// every other machine reads.
///
/// Mirror: Sources/ConnectorControlCore/IsoTimestamp.swift
/// </summary>
public static class IsoTimestamp
{
    /// <summary>Named <c>String</c> to mirror Swift's <c>IsoTimestamp.string(from:)</c>.</summary>
    public static string String(DateTime value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
