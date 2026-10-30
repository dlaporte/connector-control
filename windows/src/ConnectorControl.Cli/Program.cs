using System.Reflection;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.Cli;

/// <summary>
/// Mirror: Sources/ConnectorControlCLI/ConnectorControlCLI.swift
///
/// connector-control, the command-line tool beside Connector Control and its copy in
/// %LOCALAPPDATA%\Connector Control\bin: commands are an MCP client of the running app's pipe, and mcp
/// is the relay Claude Desktop runs. Every word it prints comes from the shared model, so the Mac tool
/// prints the same.
/// </summary>
internal static class Program
{
    /// <summary>How long one attempt waits for a free instance of the app's pipe.</summary>
    private static readonly TimeSpan ConnectWait = TimeSpan.FromMilliseconds(200);

    private static int Main(string[] args)
    {
        using var console = new ConsoleText();
        try
        {
            return Execute(args, console);
        }
        catch (Exception)
        {
            // Platform idiom: whatever else goes wrong ends as the tool's one other failure does, in a line and
            // not .NET's stack trace, and with the console put back by the dispose below. The Mac tool has no
            // catch-all: Swift has no unhandled exceptions to catch, its errors are all handled where they arise,
            // and a fault it cannot handle is a trap. The trade-off: an unexpected error is reported as
            // unreachable (exit 3), not with its detail.
            console.UseUtf8();
            console.Write(ControlOutput.Unreachable(ControlClient.UnreachableError));
            return ControlOutput.UnreachableExit;
        }
    }

    private static int Execute(string[] args, ConsoleText console)
    {
        // The arguments first: help, --version and a usage error need nothing of this PC's — no environment, no
        // SID — so nothing about it can keep them from answering. Only a command that goes to the app looks for
        // its pipe.
        var invocation = ControlCommandLine.Parse(args, Environment.CurrentDirectory, File.ReadAllBytes);
        var version = ToolVersion();
        switch (invocation)
        {
            case ControlInvocation.Help:
                return Emit(console, new ControlOutput(ControlTools.HelpText(), null, ControlOutput.DoneExit));
            case ControlInvocation.Version:
                return Emit(console, new ControlOutput(ControlCommandLine.VersionLine(version), null, ControlOutput.DoneExit));
            case ControlInvocation.Usage usage:
                return Emit(console, ControlOutput.Usage(usage.Message));
            case ControlInvocation.Relay:
                // Claude's streams are pipes: bytes, never the console's code page. The relay's stdout is
                // Claude's protocol stream: nothing of ours goes on it.
                using (var input = Console.OpenStandardInput())
                using (var output = Console.OpenStandardOutput())
                {
                    return new ControlRelayRunner(Client(announce: null), version, input, output).Run();
                }
            case ControlInvocation.Call call:
                console.UseUtf8();
                return Emit(console, ControlCommandRunner.Run(call.Tool, call.Arguments, call.Json, version,
                    Client(() => console.Error(ControlClient.StartingText)), () => console.Error(ControlTools.QueuedText)));
            default:
                throw new System.Diagnostics.UnreachableException();
        }
    }

    /// <summary>The way to the running app, worked out only for a command that goes to it: its pipe, and whether
    /// this tool may start it. Run elevated, it starts nothing and says why (ControlClient).</summary>
    private static ControlClient Client(Action? announce)
    {
        var environment = PathContext.Live().Environment;
        var toolPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ControlEndpoint.ToolFileName);
        var pipe = ControlEndpoint.PipeName(environment, ControlEndpoint.CurrentUserSid());
        // Starting is allowed only for the tool that came with an app (AppExecutable finds no other), and
        // never while CONNECTOR_CONTROL_STORE_DIR or CONNECTOR_CONTROL_CLAUDE_CONFIG names a store or a config
        // the app it would start does not use.
        Func<bool>? launch = ControlEndpoint.MayStartApp(environment) ? () => Launch(toolPath, environment) : null;
        return new ControlClient(() => ControlPipe.TryConnect(pipe, ConnectWait), launch, announce, elevated: ControlEndpoint.IsElevated());
    }

    private static int Emit(ConsoleText console, ControlOutput output)
    {
        console.UseUtf8();
        console.Write(output);
        return output.ExitCode;
    }

    /// <summary>Starts the app in the background: no flyout, and a second instance leaves without a word. It gets the
    /// sign-in's environment, not this tool's (ControlEndpoint.AppEnvironment).</summary>
    private static bool Launch(string toolPath, IReadOnlyDictionary<string, string> environment) =>
        ControlEndpoint.AppExecutable(toolPath, File.Exists, ReadText) is { } app
        && DetachedProcess.Start(app, ControlEndpoint.BackgroundArgument, DetachedProcess.AppEnvironmentBlock(environment));

    private static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The version the build stamped, as the tool says it.</summary>
    private static string ToolVersion() =>
        ControlEndpoint.ToolVersion(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "");
}
