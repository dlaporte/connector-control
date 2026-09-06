using ConnectorControl.Core.Services;

namespace ConnectorControl.Core.Tests.TestSupport;

public sealed class FakeClaudeInstall : IClaudeInstall
{
    public ClaudeInstallInfo Info { get; set; } = new(ClaudeInstallKind.Msix, "Claude_pzs8sxrjxfjjc", "Claude_pzs8sxrjxfjjc!Claude", "claude");

    public ClaudeInstallInfo Detect() => Info;
}
