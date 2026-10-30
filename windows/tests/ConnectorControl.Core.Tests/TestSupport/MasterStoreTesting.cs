namespace ConnectorControl.Core.Tests.TestSupport;

/// <summary>Mirror: Tests/ConnectorControlTestSupport/MasterStore+Testing.swift</summary>
public static class MasterStoreTesting
{
    extension(MasterStore)
    {
        /// <summary>Convenience used across tests: a single "Default" collection, always v2.</summary>
        public static MasterStore Single(IEnumerable<KeyValuePair<string, McpEntry>> mcps) =>
            new(MasterStore.CurrentVersion, MasterStore.DefaultCollectionName,
                [new KeyValuePair<string, Collection>(MasterStore.DefaultCollectionName, new Collection(mcps))]);
    }
}
