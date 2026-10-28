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
        var environment = PathContext.Live().Environment;
        var toolPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ControlEndpoint.ToolFileName);
        var version = ToolVersion();
        var pipe = ControlEndpoint.PipeName(environment, ControlEndpoint.CurrentUserSid());
        var invocation = ControlCommandLine.Parse(args, Environment.CurrentDirectory, File.ReadAllBytes);
        if (invocation is ControlInvocation.Relay)
        {
            // Claude's streams are pipes: bytes, never the console's code page.
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            return new ControlRelayRunner(Client(pipe, environment, toolPath, announce: null), version, input, output).Run();
        }
        using var console = new ConsoleText();
        var result = invocation switch
        {
            ControlInvocation.Help => new ControlOutput(ControlTools.HelpText(), null, ControlOutput.DoneExit),
            ControlInvocation.Version => new ControlOutput(ControlCommandLine.VersionLine(version), null, ControlOutput.DoneExit),
            ControlInvocation.Usage usage => ControlOutput.Usage(usage.Message),
            ControlInvocation.Call call => Run(call, Client(pipe, environment, toolPath, () => console.Error(ControlClient.StartingText)), version, console),
            _ => ControlOutput.Usage(ControlTools.HelpText()),
        };
        console.Write(result);
        return result.ExitCode;
    }

    private static ControlOutput Run(ControlInvocation.Call call, ControlClient client, string version, ConsoleText console)
    {
        IControlStream stream;
        try
        {
            stream = client.Connect();
        }
        catch (ControlClient.UnreachableException ex)
        {
            return ControlOutput.Unreachable(ControlClient.Message(ex.Reason));
        }
        try
        {
            return ControlCommandRunner.Run(call.Tool, call.Arguments, call.Json, version, stream, () => console.Error(ControlTools.QueuedText));
        }
        finally
        {
            stream.Close();
        }
    }

    private static ControlClient Client(string pipe, IReadOnlyDictionary<string, string> environment, string toolPath, Action? announce) =>
        new(() => ControlPipe.TryConnect(pipe, ConnectWait),
            ControlEndpoint.MayStartApp(environment) ? () => Launch(toolPath) : null,
            announce);

    /// <summary>Starts the app in the background: no flyout, and a second instance leaves without a word.</summary>
    private static bool Launch(string toolPath) =>
        ControlEndpoint.AppExecutable(toolPath, File.Exists, ReadText) is { } app
        && DetachedProcess.Start(app, ControlEndpoint.BackgroundArgument);

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

    /// <summary>The version the build stamped, without the source revision .NET adds after a "+". A build nobody
    /// stamped — Directory.Build.props' 0.0.0 — is a development build, as the Mac's unbundled tool says.</summary>
    private static string ToolVersion()
    {
        var stamped = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var version = ControlVersion.Semantic(stamped) ?? stamped;
        return version is "0.0.0" or "" ? AppVersion.DevelopmentBuild : version;
    }
}
