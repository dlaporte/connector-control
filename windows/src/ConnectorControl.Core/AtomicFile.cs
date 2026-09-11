namespace ConnectorControl.Core;

/// <summary>What <see cref="AtomicFile.Write"/> achieved beyond the bytes: whether the file ended up owner-only (always true off Windows).</summary>
public readonly record struct AtomicWriteResult(bool Protected);

/// <summary>
/// The one way this app writes a file. Unlike the Mac writer, which throws when it cannot make a file
/// private, this one reports the outcome (<see cref="AtomicWriteResult.Protected"/>): ACL support varies
/// by volume and share on Windows, and a save must not fail for it.
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Write-to-temp-then-rename in the target's own directory, so readers never see a partial
    /// file. Mirrors Swift AtomicFile.write: the temp file is created owner-only in the create
    /// call itself (never with the folder's inherited permissions, not even briefly), a
    /// directory this call has to create is owner-only too, and the rename carries the temp
    /// file's DACL onto the target — ReplaceFile would have kept the replaced file's. When the
    /// create-time DACL is refused, <see cref="OwnerOnlyAcl.WriteNewProtectedFile"/> has already
    /// retried on the temp file, in the same directory under the same identity, so the outcome
    /// it reports is final.
    /// </summary>
    public static AtomicWriteResult Write(byte[] data, string path)
    {
        var target = Path.GetFullPath(path);
        // A target that is itself a symlink (a config synced into a dotfiles repo, say) is
        // written through to the real file, not replaced by a plain file in the rename below —
        // mirrors the Mac writer's realpath resolution. ResolveLinkTarget throws rather than
        // returning null when the path (or a missing parent directory) doesn't exist yet, which
        // is the common first-run case, so it is only ever asked about a path already on disk;
        // one that is not there yet, or exists but is not a link, leaves target as is.
        if (File.Exists(target))
        {
            try
            {
                if (new FileInfo(target).ResolveLinkTarget(returnFinalTarget: true) is { FullName: var real })
                {
                    target = real;
                }
            }
            catch (IOException)
            {
                // the target vanished, or was replaced by something that is not a link, between
                // the exists check above and the resolve: write to the literal path instead of
                // failing the write. (DirectoryNotFoundException derives from IOException.)
            }
        }
        var dir = Path.GetDirectoryName(target)
            ?? throw new ArgumentException("Path has no parent directory.", nameof(path));
        // Throws IOException when a file sits where the directory should be —
        // before any temp file exists.
        OwnerOnlyAcl.CreateDirectoryProtected(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(target)}.tmp-{Guid.NewGuid().ToString("D").ToUpperInvariant()}");
        try
        {
            var isProtected = OwnerOnlyAcl.WriteNewProtectedFile(tmp, data);
            // A same-volume rename keeps the temp file's own DACL; MOVEFILE_REPLACE_EXISTING
            // makes it atomic over an existing target.
            File.Move(tmp, target, overwrite: true);
            return new AtomicWriteResult(isProtected);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
