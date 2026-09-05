using System.Diagnostics;
using System.Runtime.Versioning;

namespace ConnectorControl.Core;

/// <summary>
/// Resolves the four tools the way Claude Desktop would — on the PATH this process was launched
/// with, honouring PATHEXT on Windows — and reads each one's <c>--version</c> best-effort
/// (spec §3.2). Never throws: every failure degrades to "not found" or "found, version unknown".
/// The Core tests run this on the Mac too, so the Unix branches are real, not dead code.
/// </summary>
public sealed class ToolProbe : IToolProbe
{
    public static readonly TimeSpan DefaultVersionTimeout = TimeSpan.FromSeconds(2);

    /// <summary>What Windows assumes when PATHEXT is unset.</summary>
    public const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    private readonly IReadOnlyDictionary<string, string> environment;
    private readonly TimeSpan versionTimeout;

    /// <param name="environment">The process environment (<c>PathContext.Live().Environment</c> in the app; case-insensitive keys on Windows).</param>
    public ToolProbe(IReadOnlyDictionary<string, string> environment, TimeSpan? versionTimeout = null)
    {
        this.environment = environment;
        this.versionTimeout = versionTimeout ?? DefaultVersionTimeout;
    }

    public ToolStatus Probe(Tool tool) => Probe([tool])[tool];

    public IReadOnlyDictionary<Tool, ToolStatus> Probe(IReadOnlyList<Tool> tools)
    {
        var results = new Dictionary<Tool, ToolStatus>();
        var searchPath = Get("PATH") ?? "";
        var pathExt = OperatingSystem.IsWindows() ? Get("PATHEXT") ?? DefaultPathExt : null;
        foreach (var tool in tools)
        {
            var path = Resolve(ToolInfo.Name(tool), searchPath, pathExt);
            results[tool] = path is null ? ToolStatus.NotFound : new ToolStatus(path, Version(path, tool));
        }
        return results;
    }

    private string? Get(string key) => environment.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// First match of <paramref name="name"/> along <paramref name="searchPath"/> (split on the
    /// OS path separator). With <paramref name="pathExt"/> — the Windows rule — each directory
    /// is tried with each extension in order (<c>npx.cmd</c> counts, a bare <c>npx</c> does not);
    /// without it — the Unix rule — a regular file with an execute bit is required.
    /// </summary>
    public static string? Resolve(string name, string searchPath, string? pathExt)
    {
        foreach (var raw in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            // Windows strips surrounding quotes from PATH entries ("C:\Program Files\nodejs");
            // Path.Combine would keep them and the directory would never match.
            var dir = raw.Trim('"');
            if (dir.Length == 0)
            {
                continue;
            }
            if (pathExt is null)
            {
                var candidate = Path.Combine(dir, name);
                if (IsExecutableFile(candidate))
                {
                    return candidate;
                }
                continue;
            }
            foreach (var ext in pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir, name + ext.ToLowerInvariant());
                if (Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// First non-blank line of <c>--version</c> output, with the tool's own name prefix
    /// (<c>uv 0.4.30 …</c>) and one leading <c>v</c> before a digit stripped; null when
    /// there is nothing usable.
    /// </summary>
    public static string? ParseVersion(string output, Tool tool)
    {
        var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is null)
        {
            return null;
        }
        var prefix = ToolInfo.Name(tool) + " ";
        if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            line = line[prefix.Length..];
        }
        var token = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null)
        {
            return null;
        }
        if (token.Length > 1 && (token[0] == 'v' || token[0] == 'V') && char.IsDigit(token[1]))
        {
            token = token[1..];
        }
        return token.Length == 0 ? null : token;
    }

    private static bool Exists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsExecutableFile(string path)
    {
        if (!Exists(path))
        {
            return false;
        }
        if (OperatingSystem.IsWindows())
        {
            return true;   // no execute bit on Windows; only tests reach this branch (Probe always passes PATHEXT there)
        }
        return HasExecuteBit(path);
    }

    [UnsupportedOSPlatform("windows")]
    private static bool HasExecuteBit(string path)
    {
        try
        {
            const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & anyExecute) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The parsed <c>--version</c>, or null when the call fails or overruns the timeout.</summary>
    private string? Version(string path, Tool tool)
    {
        try
        {
            using var process = Process.Start(StartInfo(path));
            if (process is null)
            {
                return null;
            }
            process.StandardInput.Close();   // a tool that reads stdin must see EOF, not a pipe that never closes
            var stdout = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();   // drain, or a chatty launcher could block on a full pipe
            if (!process.WaitForExit((int)versionTimeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException)
                {
                    // already gone, or not ours to kill
                }
                // Both read tasks are abandoned here and `using` disposes the streams underneath
                // them. Unobserved task exceptions are ignored in .NET 5+, so a faulted read on
                // this rare path cannot take the tray app down.
                return null;
            }
            return stdout.Wait(versionTimeout) ? ParseVersion(stdout.Result, tool) : null;
        }
        catch (Exception)
        {
            // Win32Exception (not runnable), IOException, InvalidOperationException,
            // AggregateException from Wait, PlatformNotSupportedException: the version is
            // decoration; "found" is the answer that matters.
            return null;
        }
    }

    /// <summary>
    /// A .cmd/.bat launcher needs the command interpreter (<c>cmd.exe /d /c "path" --version</c>);
    /// everything else runs directly. No console window, stdin not inherited.
    /// </summary>
    private static ProcessStartInfo StartInfo(string path)
    {
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        var ext = Path.GetExtension(path);
        if (OperatingSystem.IsWindows()
            && (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            info.FileName = "cmd.exe";
            info.ArgumentList.Add("/d");
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add(path);
            info.ArgumentList.Add("--version");
        }
        else
        {
            info.FileName = path;
            info.ArgumentList.Add("--version");
        }
        return info;
    }
}
