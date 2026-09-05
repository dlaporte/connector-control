using System.Diagnostics;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests;

public class ToolProbeTests : IDisposable
{
    private const string PathExt = ".COM;.EXE;.BAT;.CMD";
    private readonly TempDir dir = new("toolprobe");

    public void Dispose() => dir.Dispose();

    private string Bin => dir.File("bin");

    private static Dictionary<string, string> Env(string path) => new(StringComparer.Ordinal)
    {
        ["PATH"] = path,
        ["PATHEXT"] = PathExt,
    };

    /// <summary>
    /// A stub launcher in <paramref name="sub"/>: a .cmd on Windows, an executable shell
    /// script elsewhere. Returns the path Resolve is expected to report.
    /// </summary>
    private string Stub(string name, string sub, string? windowsBody = null, string? unixBody = null)
    {
        var folder = dir.File(sub);
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
        {
            var script = Path.Combine(folder, name);
            File.WriteAllText(script, "#!/bin/sh\n" + (unixBody ?? "echo 10.9.2") + "\n");
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            return script;
        }
        var file = Path.Combine(folder, name + ".cmd");
        File.WriteAllText(file, windowsBody ?? "@echo 10.9.2\r\n");
        return file;
    }

    [Theory]
    [InlineData("v22.11.0\n", Tool.Node, "22.11.0")]
    [InlineData("10.9.2\r\n", Tool.Npx, "10.9.2")]
    [InlineData("uv 0.4.30 (Homebrew 2024-11-20)", Tool.Uv, "0.4.30")]
    [InlineData("uvx 0.4.30", Tool.Uvx, "0.4.30")]
    [InlineData("\n  \nv1.2.3 extra", Tool.Node, "1.2.3")]
    [InlineData("vanilla", Tool.Node, "vanilla")]   // a leading v is dropped only before a digit
    [InlineData("", Tool.Node, null)]
    [InlineData("\n  \n", Tool.Npx, null)]
    public void ParseVersionStripsTheNameAndALeadingV(string output, Tool tool, string? expected)
    {
        Assert.Equal(expected, ToolProbe.ParseVersion(output, tool));
    }

    [Fact]
    public void ResolveWithPathExtTriesEachExtensionInOrder()
    {
        Directory.CreateDirectory(Bin);
        File.WriteAllText(Path.Combine(Bin, "npx.cmd"), "");
        File.WriteAllText(Path.Combine(Bin, "node.exe"), "");
        File.WriteAllText(Path.Combine(Bin, "uv"), "");   // no extension: Windows would not run it
        var search = dir.File("missing") + Path.PathSeparator + Bin;
        Assert.Equal(Path.Combine(Bin, "npx.cmd"), ToolProbe.Resolve("npx", search, PathExt));
        Assert.Equal(Path.Combine(Bin, "node.exe"), ToolProbe.Resolve("node", search, PathExt));
        Assert.Null(ToolProbe.Resolve("uvx", search, PathExt));
        Assert.Null(ToolProbe.Resolve("uv", search, PathExt));
        Assert.Null(ToolProbe.Resolve("npx", "", PathExt));
    }

    [Fact]
    public void ResolveWithoutPathExtNeedsAnExecutableRegularFile()
    {
        Directory.CreateDirectory(Bin);
        var plain = Path.Combine(Bin, "node");
        File.WriteAllText(plain, "not executable");
        Directory.CreateDirectory(Path.Combine(Bin, "uvx"));   // a directory named like a tool
        var exe = Stub("npx", "bin");
        var search = dir.File("missing") + Path.PathSeparator + Bin;
        Assert.Null(ToolProbe.Resolve("uvx", search, null));   // a directory is not a tool, on either OS
        if (OperatingSystem.IsWindows())
        {
            // No execute bit on Windows: any regular file matches in extension-less mode,
            // and the stub is npx.cmd, invisible without PATHEXT.
            Assert.Equal(plain, ToolProbe.Resolve("node", search, null));
            Assert.Null(ToolProbe.Resolve("npx", search, null));
        }
        else
        {
            Assert.Null(ToolProbe.Resolve("node", search, null));   // no execute bit
            Assert.Equal(exe, ToolProbe.Resolve("npx", search, null));
        }
    }

    [Fact]
    public void ProbeFindsAStubAndReadsItsVersion()
    {
        var exe = Stub("npx", "bin");
        var probe = new ToolProbe(Env(Bin));
        var results = probe.Probe([Tool.Npx, Tool.Uvx]);
        Assert.Equal(new ToolStatus(exe, "10.9.2"), results[Tool.Npx]);
        Assert.True(results[Tool.Npx].Found);
        Assert.Equal(ToolStatus.NotFound, results[Tool.Uvx]);
        Assert.False(results[Tool.Uvx].Found);
        Assert.Equal(results[Tool.Npx], probe.Probe(Tool.Npx));
    }

    [Fact]
    public void ProbeReportsFoundWhenTheVersionCallHangs()
    {
        var exe = Stub("uv", "bin", windowsBody: "@ping -n 6 127.0.0.1 > nul\r\n@echo 0.4.30\r\n", unixBody: "exec sleep 5");
        var started = Stopwatch.StartNew();
        var status = new ToolProbe(Env(Bin), TimeSpan.FromMilliseconds(200)).Probe(Tool.Uv);
        Assert.Equal(new ToolStatus(exe, null), status);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3));   // abandoned, not waited for
    }

    [Fact]
    public void ProbeNeverThrowsOnGarbage()
    {
        Assert.Equal(ToolStatus.NotFound, new ToolProbe(new Dictionary<string, string>(StringComparer.Ordinal)).Probe(Tool.Node));
        var weird = "::" + dir.File("missing dir with spaces") + Path.PathSeparator + dir.File("nope");
        Assert.Equal(ToolStatus.NotFound, new ToolProbe(Env(weird)).Probe(Tool.Npx));
        // exits without printing: found, version unknown
        var silent = Stub("uvx", "bin", windowsBody: "@exit /b 3\r\n", unixBody: "exit 3");
        Assert.Equal(new ToolStatus(silent, null), new ToolProbe(Env(Bin)).Probe(Tool.Uvx));
    }
}
