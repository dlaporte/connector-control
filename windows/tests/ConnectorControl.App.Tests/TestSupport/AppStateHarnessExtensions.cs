using ConnectorControl.App.Services;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.App.Tests.TestSupport;

/// <summary>
/// The one extra thing App-level tests need from the shared harness: a full
/// PlatformServices bundle built from its fakes, for constructing windows
/// (EditorWindow, SettingsWindow, FlyoutWindow) rather than passing fakes one by
/// one. This lives here, not on AppStateHarness itself, because PlatformServices
/// is an App type — the shared harness also compiles into Core.Tests, which
/// cannot reference the App project.
/// </summary>
internal static class AppStateHarnessExtensions
{
    public static PlatformServices Services(this AppStateHarness h) =>
        new(h.Settings, new FakeClaudeInstall(), h.Claude, h.Notifier, new FakeAutostart(), new FakeUpdater());
}
