namespace ConnectorControl.Core;

public static class AtomicFile
{
    /// <summary>
    /// Write-to-temp-then-replace in the target's own directory, so readers
    /// never see a partial file. Mirrors Swift AtomicFile.write.
    /// </summary>
    public static void Write(byte[] data, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Path has no parent directory.", nameof(path));
        // Throws IOException when a file sits where the directory should be —
        // before any temp file exists.
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(fullPath)}.tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(tmp, data);
            // Never leave secrets world-readable, even briefly: lock the temp file
            // down before it is moved into place.
            OwnerOnlyAcl.TryApply(tmp);
            if (File.Exists(fullPath))
            {
                File.Replace(tmp, fullPath, destinationBackupFileName: null);
            }
            else
            {
                try
                {
                    File.Move(tmp, fullPath);
                }
                catch (IOException) when (File.Exists(fullPath))
                {
                    File.Replace(tmp, fullPath, destinationBackupFileName: null);
                }
            }
            // ReplaceFile keeps the REPLACED file's DACL, not the temp file's, so a
            // pre-existing permissive file would stay permissive: re-apply to the result.
            OwnerOnlyAcl.TryApply(fullPath);
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
