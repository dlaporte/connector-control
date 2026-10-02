using ConnectorControl.Core.State;

namespace ConnectorControl.Core.Tests.TestSupport;

/// <summary>
/// Edit targets for tests. The collection defaults to the one every harness starts with, and
/// active; production always names the collection the window shows.
///
/// Mirror: Tests/ConnectorControlStateTests/TestSupport/EditTarget+Testing.swift
/// </summary>
internal static class TestTargets
{
    public const string Collection = "Default";

    /// <summary>
    /// A brand-new connector opened from a template config (e.g. the Local-form default).
    /// Production code only ever opens a genuinely blank target (<c>NewRemote</c>) or an existing one.
    /// </summary>
    public static EditTarget New(JsonValue template, string collection = Collection) =>
        new(Guid.NewGuid().ToString(), "", new McpEntry(template), IsNew: true, Collection: collection);

    public static EditTarget Existing(string name, McpEntry entry, string collection = Collection) =>
        EditTarget.Existing(name, entry, collection);

    public static EditTarget NewRemote(RemoteLaunchStyle style, string collection = Collection) =>
        EditTarget.NewRemote(style, collection);
}
