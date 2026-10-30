using System.Text;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.Cli;

/// <summary>
/// Windows only: the Mac counterpart is the <c>write</c> and <c>printError</c> functions in
/// Sources/ConnectorControlCLI/ConnectorControlCLI.swift.
///
/// The tool's text, as UTF-8 bytes on the console or a pipe alike: the same bytes the Mac tool prints. On
/// a console the output code page is UTF-8 while the tool runs and put back after - also on Ctrl-C and on
/// an error, where a <c>using</c> alone would not run - so the curly quotes and the ellipsis show as they
/// are without changing the console for whatever runs next.
/// </summary>
internal sealed class ConsoleText : IDisposable
{
    private readonly Stream stdout = Console.OpenStandardOutput();
    private readonly Stream stderr = Console.OpenStandardError();
    private ConsoleEncodingScope? encoding;
    private volatile bool cancelled;

    /// <summary>Starts printing as UTF-8 on a console. The relay does not call this: Claude's streams are pipes.</summary>
    public void UseUtf8()
    {
        if (encoding is not null || (Console.IsOutputRedirected && Console.IsErrorRedirected))
        {
            return;
        }
        // Registered before the code page changes and removed after it is put back, so no Ctrl-C falls
        // between the two. The handler leaves termination to the usual exit; the code page is put back first.
        Console.CancelKeyPress += OnCancel;
        encoding = new ConsoleEncodingScope(() => Console.OutputEncoding, e => Console.OutputEncoding = e);
        if (cancelled)
        {
            // A Ctrl-C that came while the page was being changed found nothing to put back.
            encoding.Restore();
        }
    }

    private void OnCancel(object? sender, ConsoleCancelEventArgs e)
    {
        cancelled = true;
        encoding?.Restore();
    }

    public void Write(ControlOutput output)
    {
        if (output.Stdout is { } text)
        {
            Line(stdout, text);
        }
        if (output.Stderr is { } error)
        {
            Error(error);
        }
    }

    public void Error(string text) => Line(stderr, text);

    private static void Line(Stream stream, string text)
    {
        try
        {
            stream.Write(Encoding.UTF8.GetBytes(text + "\n"));
            stream.Flush();
        }
        catch (IOException)
        {
            // the reader has gone
        }
    }

    public void Dispose()
    {
        encoding?.Dispose();
        Console.CancelKeyPress -= OnCancel;
        stdout.Dispose();
        stderr.Dispose();
    }
}
