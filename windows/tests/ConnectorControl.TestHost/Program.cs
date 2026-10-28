using System.Collections.Concurrent;
using ConnectorControl.Core;
using ConnectorControl.Core.Services;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.TestHost;

/// <summary>
/// Mirror: Sources/ConnectorControlTestHost/TestHost.swift
///
/// <c>ConnectorControl.TestHost &lt;sandbox&gt;</c>: the real control layer — AppState, ControlTools, the server and
/// its pipe — over a sandbox folder, with no window, no Claude and no updater, for the MCP Inspector check.
/// Every path comes from the sandbox argument and none from this process's environment, so it never reaches a
/// person's store or Claude's real config. The integration is on, confirmations are on, and every question is
/// declined: gated changes are refused.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]))
        {
            Console.Error.WriteLine("usage: ConnectorControl.TestHost <full sandbox folder>");
            return 2;
        }
        var sandbox = args[0];
        var store = Path.Combine(sandbox, "store");
        var claudeConfig = Path.Combine(sandbox, "claude_desktop_config.json");
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [AppPaths.StoreDirEnv] = store,
            [AppPaths.ClaudeConfigEnv] = claudeConfig,
        };
        Directory.CreateDirectory(store);
        if (!File.Exists(claudeConfig))
        {
            File.WriteAllText(claudeConfig, TestHostServices.SeedConfig);
        }
        var paths = new PathContext(environment, new KnownFolders(Path.Combine(sandbox, "Local"), Path.Combine(sandbox, "Roaming")), new RealPathProbe());
        var loop = new UiLoop();
        var host = new AppHost(loop.Post, loop.After, () => DateTime.UtcNow, action => Task.Run(action), Path.Combine(sandbox, "connector-control.exe"));
        loop.Post(() =>
        {
            var settings = new SettingsStore(Path.Combine(sandbox, "settings.json"))
            {
                ClaudeIntegrationEnabled = true,
                ConfirmClaudeChanges = true,
                ClaudeIntegrationOfferAnswered = true,
                NotifyExternalChanges = false,
                SweepVersion = PermissionsSweep.CurrentVersion,
            };
            var notifier = new TestHostNotifier();
            var dialogs = new TestHostDialogs();
            var state = new AppState(settings, new TestHostClaude(), notifier, dialogs, paths, host, new TestHostToolProbe());
            var updater = new TestHostUpdater();
            var model = new SettingsModel(state, settings, new TestHostAutostart(), new TestHostInstall(), updater,
                new UpdateCoordinator(updater, settings, notifier, dialogs, host));
            var server = new ControlServer(new ControlTools(state, model, new DecliningConfirmer(), host));
            var name = ControlEndpoint.PipeName(environment, ControlEndpoint.CurrentUserSid());
            var endpoint = ControlPipeServer.Start(name, server, host);
            loop.Keep(endpoint);
            // Ctrl+C stops it as a quit stops the app, and so does closing its console; killing the process,
            // as an update would, leaves nothing to clean up on Windows, where a pipe goes with its process.
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                loop.Post(() =>
                {
                    endpoint.Dispose();
                    server.CloseAll();
                    loop.Stop();
                });
            };
            Console.Out.WriteLine("ready " + name);
            Console.Out.Flush();
        });
        loop.Run();
        return 0;
    }

    /// <summary>The test host's UI thread: one thread running what is posted, in order, as the WPF dispatcher
    /// does in the app.</summary>
    private sealed class UiLoop
    {
        private readonly BlockingCollection<Action> work = [];
        private readonly List<object> kept = [];

        public void Post(Action action) => work.Add(action);

        public void After(TimeSpan delay, Action action) =>
            _ = Task.Delay(delay).ContinueWith(_ => work.Add(action), TaskScheduler.Default);

        /// <summary>What must live as long as the loop.</summary>
        public void Keep(object value) => kept.Add(value);

        public void Stop() => work.CompleteAdding();

        public void Run()
        {
            foreach (var action in work.GetConsumingEnumerable())
            {
                action();
            }
        }
    }
}
