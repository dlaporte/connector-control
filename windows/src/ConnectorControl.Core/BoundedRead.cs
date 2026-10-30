using Microsoft.Win32.SafeHandles;

namespace ConnectorControl.Core;

/// <summary>
/// Why a bounded read refused a file it opened. Platform idiom: an <see cref="IOException"/>, so the I/O
/// catches already there take it; the Mac mirror throws <c>BoundedReadError</c>, which a caller matches
/// by case.
/// </summary>
public sealed class BoundedReadException(string message) : IOException(message);

/// <summary>
/// Reads a file someone else may be changing, in one go and at most so many bytes. A size checked
/// beforehand says nothing about what a later read finds: the file can be swapped in between for a
/// far larger one, or for something that is not a file at all. So the file is opened first, and
/// what the open handle is, and how much it holds, decide the read: a regular file of at most
/// <c>limit</c> bytes, and never more than <c>limit + 1</c> read to find out.
///
/// Mirror: Sources/ConnectorControlCore/BoundedRead.swift
/// </summary>
public static class BoundedRead
{
    public const string NotAFileError = "It isn’t a file.";
    public const string TooLargeError = "The file is too large.";

    /// <summary>How much more a read takes at a time once the file outgrows what it said it held.</summary>
    private const int ChunkBytes = 64 * 1024;

    public static byte[] Data(string path, int limit)
    {
        // Platform idiom: a handle, asked what it is and how long; the Mac mirror opens without waiting,
        // so a FIFO cannot hold the read open, and asks the descriptor's fstat. Opening a folder throws
        // an access error here, which is said as the Mac says it.
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (UnauthorizedAccessException) when (Directory.Exists(path))
        {
            throw new BoundedReadException(NotAFileError);
        }
        using var opened = handle;
        if ((File.GetAttributes(handle) & (FileAttributes.Directory | FileAttributes.Device)) != 0)
        {
            throw new BoundedReadException(NotAFileError);
        }
        long length;
        try
        {
            length = RandomAccess.GetLength(handle);
        }
        catch (NotSupportedException)
        {
            // A pipe, or anything else with no length to ask.
            throw new BoundedReadException(NotAFileError);
        }
        if (length > limit)
        {
            throw new BoundedReadException(TooLargeError);
        }
        return ReadUpTo(handle, length, limit);
    }

    /// <summary>One byte past the limit is enough to know the file grew past it. The buffer starts at what the
    /// handle said it holds and grows in chunks only for a file that grows while it is read.</summary>
    private static byte[] ReadUpTo(SafeFileHandle handle, long length, int limit)
    {
        var buffer = new byte[(int)Math.Min(length, limit) + 1];
        var count = 0;
        while (count <= limit)
        {
            if (count == buffer.Length)
            {
                Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length + ChunkBytes, (long)limit + 1));
            }
            var read = RandomAccess.Read(handle, buffer.AsSpan(count), count);
            if (read == 0)
            {
                break;
            }
            count += read;
        }
        if (count > limit)
        {
            throw new BoundedReadException(TooLargeError);
        }
        return buffer[..count];
    }
}
