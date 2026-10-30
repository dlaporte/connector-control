namespace ConnectorControl.Core.Tests.TestSupport;

/// <summary>
/// The facts that open real pipes, run one at a time and after the parallel classes have finished. Their
/// deadlines (a write cut at one second and the close seen within a few) run on thread-pool timers and
/// continuations, so a pool the rest of the suite has starved can delay them by seconds; the Mac's tests run
/// serially and never meet this.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealEndpointsCollection
{
    public const string Name = "Real endpoints";
}
