using System.Text;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.Cli;

/// <summary>
/// The tool's text, as UTF-8 bytes on the console or a pipe alike: the same bytes the Mac tool prints. On
/// a console the output code page is UTF-8 while the tool runs and put back after, so the curly quotes and
/// the ellipsis show as they are without changing the console for whatever runs next.
/// </summary>
internal sealed class ConsoleText : IDisposable
{
    private readonly Encoding? previous;
    private readonly Stream stdout = Console.OpenStandardOutput();
    private readonly Stream stderr = Console.OpenStandardError();

    public ConsoleText()
    {
        if (!Console.IsOutputRedirected || !Console.IsErrorRedirected)
        {
            previous = Console.OutputEncoding;
            Console.OutputEncoding = new UTF8Encoding(false);
        }
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
        if (previous is not null)
        {
            Console.OutputEncoding = previous;
        }
        stdout.Dispose();
        stderr.Dispose();
    }
}
