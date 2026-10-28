using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ConnectorControl.Core.Transport;

/// <summary>
/// Windows only: the Mac counterpart is Sources/ConnectorControlTransport/ControlSocket.swift.
///
/// The control layer's endpoint on Windows: a named pipe only this user can reach. Server and client
/// both use <see cref="PipeOptions.CurrentUserOnly"/> — the server's pipe admits only this user, and
/// the client connects only to a pipe this user owns, at this user's elevation — and the app's first
/// instance is created with <see cref="PipeOptions.FirstPipeInstance"/>, so it fails rather than join
/// a pipe something else made first under the same name: another account that takes the name first
/// cannot pose as the app, only keep it from serving, which the app reports. A client on another
/// computer, which the access list alone would admit, is closed unanswered. The pure rule is not
/// marked Windows-only, so it is tested on any platform.
/// </summary>
public static class ControlPipe
{
    /// <summary>What Windows answers <c>GetNamedPipeClientComputerName</c> for a client on this computer.</summary>
    public const int ErrorPipeLocal = 229;

    /// <summary>What each direction of a pipe holds before a write waits for the client to read. With none
    /// (the default), every write would wait for its reader, and a client paused with one line outstanding
    /// would be cut at the write deadline; the Mac's socket buffers absorb several lines first, so this is
    /// parity with that buffering.</summary>
    public const int BufferBytes = 65_536;

    /// <summary>One server instance. Asynchronous, so a read waiting on the pipe never holds up a write
    /// to it: Windows runs synchronous I/O on one pipe handle one operation at a time.</summary>
    [SupportedOSPlatform("windows")]
    public static NamedPipeServerStream CreateServer(string name, bool first) =>
        new(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            BufferBytes, BufferBytes);

    /// <summary>Whether a connected client is on this computer. .NET never asks Windows to refuse remote clients
    /// (<c>PIPE_REJECT_REMOTE_CLIENTS</c>), and the access list <see cref="PipeOptions.CurrentUserOnly"/> builds
    /// admits this user from any computer — nor can a stricter list be given alongside it, which .NET refuses — so
    /// the server asks after each connection. Windows answers a local client with the failure
    /// <see cref="ErrorPipeLocal"/>; only the SMB server sets a client computer name, and only for a remote
    /// connection, so a name is never taken as proof of being local — a remote client could claim this
    /// computer's — and every other answer is taken as remote.</summary>
    public static bool IsLocalClient(bool answered, int error) => !answered && error == ErrorPipeLocal;

    /// <summary>What Windows answers <c>GetNamedPipeClientComputerName</c> for a connected server instance:
    /// whether it succeeded and, when it did not, the error.</summary>
    [SupportedOSPlatform("windows")]
    internal static (bool Answered, int Error) ClientComputerAnswer(NamedPipeServerStream pipe)
    {
        var name = new StringBuilder(256);
        var answered = GetNamedPipeClientComputerName(pipe.SafePipeHandle, name, (uint)(name.Capacity * sizeof(char)));
        return (answered, answered ? 0 : Marshal.GetLastPInvokeError());
    }

    /// <summary><see cref="IsLocalClient(bool, int)"/> for a connected server instance.</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsLocalClient(NamedPipeServerStream pipe)
    {
        var (answered, error) = ClientComputerAnswer(pipe);
        return IsLocalClient(answered, error);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetNamedPipeClientComputerNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientComputerName(SafePipeHandle pipe, StringBuilder clientComputerName, uint clientComputerNameLength);

    /// <summary>Connects to the app's pipe, waiting up to <paramref name="timeout"/> for a free instance;
    /// null when nothing this user owns answers in time.</summary>
    [SupportedOSPlatform("windows")]
    public static IControlStream? TryConnect(string name, TimeSpan timeout)
    {
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            client.Connect((int)timeout.TotalMilliseconds);
            return new ControlPipeStream(client);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            client.Dispose();
            return null;
        }
    }
}

/// <summary>One connection to the pipe, as an <see cref="IControlStream"/>. Its reads and writes are
/// synchronous over an asynchronous handle, so one thread may read while another writes.</summary>
public sealed class ControlPipeStream(PipeStream pipe) : IControlStream
{
    private int closed;

    public byte[] Read(int maximum)
    {
        var buffer = new byte[maximum];
        try
        {
            var count = pipe.Read(buffer, 0, maximum);
            return count > 0 ? buffer[..count] : [];
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            return [];
        }
    }

    public bool Write(byte[] data)
    {
        try
        {
            pipe.Write(data, 0, data.Length);
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Disposing the pipe cancels a read waiting on it, which then returns empty.</summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref closed, 1) == 0)
        {
            pipe.Dispose();
        }
    }

    /// <summary>A pipe has no half-close. A write that failed on a broken pipe — ERROR_BROKEN_PIPE, ERROR_NO_DATA or
    /// ERROR_PIPE_NOT_CONNECTED, the failures .NET marks the pipe broken for — failed because the app's end has
    /// gone, and a read still gives what the app sent before it went and then the end: the pipe is left to be
    /// read. A write that failed any other way leaves the pipe connected, and a read that could wait for good,
    /// so the pipe is closed both ways and the reader ends now. The Mac's socket shuts its writing half.</summary>
    public void CloseWrite()
    {
        if (pipe.IsConnected)
        {
            Close();
        }
    }
}
