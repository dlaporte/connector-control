using System.Text;

namespace ConnectorControl.Core.Tests.TestSupport;

/// <summary>
/// Whether JSON text holds a string anywhere, as written or as a JSON string spells it. The writers
/// on both platforms escape "/" as "\/" and "\" as "\\", so a plain search for a path in a written
/// document never finds one, and an absence check built on it can never fail. Every "the document
/// does not carry this" assertion goes through here.
///
/// Mirror: Tests/ConnectorControlTestSupport/JSONText.swift
/// </summary>
public static class JsonText
{
    public static bool Contains(string json, string text)
    {
        var escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        string[] forms =
        [
            text,
            text.Replace("/", "\\/", StringComparison.Ordinal),
            escaped,
            escaped.Replace("/", "\\/", StringComparison.Ordinal),
        ];
        return forms.Any(form => json.Contains(form, StringComparison.Ordinal));
    }

    public static bool Contains(byte[] json, string text) => Contains(Encoding.UTF8.GetString(json), text);

    /// <summary><see cref="Contains(string, string)"/> for the document written at <paramref name="path"/>.</summary>
    public static bool FileContains(string path, string text) => Contains(File.ReadAllBytes(path), text);
}
