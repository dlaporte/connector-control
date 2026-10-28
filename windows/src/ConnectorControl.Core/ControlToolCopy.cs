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
    /// when a packaged tool sits beside the app. True when it copied; false also when there was nothing to do.
    /// A failure leaves the copy that is there serving, is said to <paramref name="failed"/> — the app's log —
    /// and is tried again at the next launch.
    /// <para>
    /// Two refreshes can run at once: the app's single-instance mutex is per logon session, so one person
    /// signed in twice — at the console and over Remote Desktop — runs two apps against the one bin\. Their
    /// steps can interleave: one's sweep of leftovers can delete the other's staging copy, or the old copy the
    /// other has just renamed aside. The worst that leaves is a failed refresh, a stale copy or none until the
    /// next launch, whose refresh recovers it.
    /// </para></summary>
    public static bool Refresh(string packaged, string dataDir, string appExecutable, Action<string>? failed = null) =>
        Refresh(packaged, dataDir, appExecutable, failed, File.Move);

    /// <summary><paramref name="move"/> renames the copies, as <see cref="File.Move(string, string)"/> does: a test's
    /// way to a move that fails after the old copy is aside.</summary>
    internal static bool Refresh(string packaged, string dataDir, string appExecutable, Action<string>? failed, Action<string, string> move)
    {
        // A development run has no packaged tool beside it, and touches neither the copy nor the record.
        if (!File.Exists(packaged))
        {
            return false;
        }
        var bin = BinDir(dataDir);
        var copied = Copy(packaged, bin, failed, move);
        // Its own step, after the copy: a record that cannot be written never stops the copy.
        Record(bin, appExecutable, failed);
        return copied;
    }

    private static bool Copy(string packaged, string bin, Action<string>? failed, Action<string, string> move)
    {
        var target = Path.Combine(bin, ControlEndpoint.ToolFileName);
        try
        {
            Directory.CreateDirectory(bin);
            DeleteLeftovers(bin);
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
                move(target, aside);
            }
            try
            {
                move(staging, target);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && aside is not null)
            {
                try
                {
                    move(aside, target);
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                {
                    // Both are said, the cause first. The old copy stays aside, and the next launch's sweep and
                    // refresh place a new one.
                    throw new IOException($"{ex.Message} The old copy could not be put back either: {rollback.Message}", ex);
                }
                throw;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed?.Invoke($"The command-line tool could not be copied to {target}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The app's executable, for the copy to start it: written only when it differs, and then whole —
    /// a new record is written beside the old one and moved into place, so the tool never reads half of one.</summary>
    private static void Record(string bin, string appExecutable, Action<string>? failed)
    {
        var record = Path.Combine(bin, ControlEndpoint.AppPathFileName);
        try
        {
            if (File.Exists(record) && File.ReadAllText(record) == appExecutable)
            {
                return;
            }
            var staging = record + StagingSuffix;
            File.WriteAllText(staging, appExecutable);
            File.Move(staging, record, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed?.Invoke($"The app's path could not be recorded in {record}: {ex.Message}");
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

    /// <summary>Old copies an earlier launch renamed aside, and a copy or record left half made: deleted now
    /// unless something still runs them.</summary>
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
