namespace ConnectorControl.Core;

/// <summary>
/// The two exceptions every best-effort file-system cleanup or probe in this app treats as
/// "the file system said no" rather than a programming error: a locked file, a missing
/// permission, a volume that refused. Centralizes the <c>catch (IOException) … catch
/// (UnauthorizedAccessException)</c> pair repeated across the codebase.
/// </summary>
public static class FileSystemErrors
{
    public static bool IsTransient(Exception ex) => ex is IOException or UnauthorizedAccessException;

    /// <summary>
    /// Best effort, like Swift's <c>try?</c>: removes <paramref name="path"/>, whether it is a
    /// file or a directory (recursively), and does nothing when it is already gone or the
    /// delete fails for a transient reason. Never throws.
    /// </summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            // best effort: the caller has already decided this isn't worth failing over
        }
    }
}
