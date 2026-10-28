using System.Security.Cryptography;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.Core;

/// <summary>
/// Windows only: the Mac's tool stays inside the app bundle, which Sparkle replaces without ending the
/// processes that run from it.
///
/// The copy of the command-line tool that Claude's entry and the PATH run, kept outside Velopack's update
/// folder: applying an update rewrites current\ and ends every process started from it, so a relay run from
/// there would end at each update and Claude would show the connector as failed. At each launch the app copies
/// the packaged tool to bin\ when the copy there differs. A running copy cannot be overwritten but can be
/// renamed, so the old one is renamed aside and deleted at a later launch, once nothing runs it. The app also
/// records its own executable there, for the copy to start it.
/// </summary>
public static class ControlToolCopy
{
    public const string BinDirName = "bin";
    public const string AsidePrefix = "connector-control.old-";
    public const string StagingSuffix = ".new";

    public static string BinDir(string dataDir) => Path.Combine(dataDir, BinDirName);

    public static string ToolPath(string dataDir) => Path.Combine(BinDir(dataDir), ControlEndpoint.ToolFileName);

    /// <summary>Brings the copy up to date with <paramref name="packaged"/> and records <paramref name="appExecutable"/>,
    /// when a packaged tool sits beside the app. True when it copied. A failure leaves the copy that is there
    /// serving; the next launch tries again.</summary>
    public static bool Refresh(string packaged, string dataDir, string appExecutable)
    {
        var bin = BinDir(dataDir);
        var target = ToolPath(dataDir);
        // A development run has no packaged tool beside it, and touches neither the copy nor the record.
        if (!File.Exists(packaged))
        {
            return false;
        }
        try
        {
            Directory.CreateDirectory(bin);
            DeleteLeftovers(bin);
            File.WriteAllText(Path.Combine(bin, ControlEndpoint.AppPathFileName), appExecutable);
            if (SameBytes(packaged, target))
            {
                return false;
            }
            // The new copy is made in full first: a copy that fails leaves the old one serving. Only then is the
            // old one renamed aside, and if the new one cannot take its place, the old one is put back.
            var staging = target + StagingSuffix;
            File.Copy(packaged, staging, overwrite: true);
            string? aside = null;
            if (File.Exists(target))
            {
                aside = AsidePath(bin);
                File.Move(target, aside);
            }
            try
            {
                File.Move(staging, target);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && aside is not null)
            {
                File.Move(aside, target);
                throw;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The copy and everything beside it, as the app is uninstalled. A copy still running cannot be
    /// deleted and stays; nothing will start it again.</summary>
    public static void Remove(string dataDir)
    {
        var bin = BinDir(dataDir);
        if (!Directory.Exists(bin))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(bin))
        {
            TryDelete(file);
        }
        try
        {
            Directory.Delete(bin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a copy still running keeps the folder
        }
    }

    /// <summary>Old copies an earlier launch renamed aside, and a copy left half made: deleted now unless
    /// something still runs them.</summary>
    internal static void DeleteLeftovers(string bin)
    {
        foreach (var file in Directory.EnumerateFiles(bin))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith(AsidePrefix, StringComparison.OrdinalIgnoreCase) || name.EndsWith(StagingSuffix, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(file);
            }
        }
    }

    /// <summary><c>connector-control.old-&lt;n&gt;.exe</c>, the first n not taken.</summary>
    internal static string AsidePath(string bin)
    {
        for (var n = 1; ; n++)
        {
            var aside = Path.Combine(bin, $"{AsidePrefix}{n}.exe");
            if (!File.Exists(aside))
            {
                return aside;
            }
        }
    }

    /// <summary>Whether the copy holds the packaged tool's bytes. The copy may be running, so it is opened as
    /// the loader leaves it: shared for reading, writing and deleting.</summary>
    internal static bool SameBytes(string packaged, string copy)
    {
        if (!File.Exists(copy) || new FileInfo(packaged).Length != new FileInfo(copy).Length)
        {
            return false;
        }
        using var one = new FileStream(packaged, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var other = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return SHA256.HashData(one).AsSpan().SequenceEqual(SHA256.HashData(other));
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // still running: a later launch, or nothing, deletes it
        }
    }
}
