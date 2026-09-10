namespace ConnectorControl.Core;

/// <summary>What <see cref="AtomicFile.Write"/> achieved beyond the bytes: whether the file ended up owner-only (always true off Windows).</summary>
public readonly record struct AtomicWriteResult(bool Protected);

public static class AtomicFile
{
    /// <summary>
    /// Write-to-temp-then-rename in the target's own directory, so readers never see a partial
    /// file. Mirrors Swift AtomicFile.write: the temp file is created owner-only in the create
    /// call itself (never with the folder's inherited permissions, not even briefly), a
    /// directory this call has to create is owner-only too, and the rename carries the temp
    /// file's DACL onto the target — ReplaceFile would have kept the replaced file's.
    /// </summary>
    public static AtomicWriteResult Write(byte[] data, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Path has no parent directory.", nameof(path));
        // Throws IOException when a file sits where the directory should be —
        // before any temp file exists.
        OwnerOnlyAcl.CreateDirectoryProtected(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(fullPath)}.tmp-{Guid.NewGuid():N}");
        try
        {
            var isProtected = OwnerOnlyAcl.WriteNewProtectedFile(tmp, data);
            // A same-volume rename keeps the temp file's own DACL; MOVEFILE_REPLACE_EXISTING
            // makes it atomic over an existing target.
            File.Move(tmp, fullPath, overwrite: true);
            if (!isProtected)
            {
                // The create-time DACL was refused: one more try on the file now in place.
                isProtected = OwnerOnlyAcl.TryApply(fullPath);
            }
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
