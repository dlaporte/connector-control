using System.Text;

namespace ConnectorControl.Core.Transport;

/// <summary>
/// Windows only: the Mac has no counterpart - its terminals take UTF-8 as they are.
///
/// The console's output encoding set to UTF-8 and put back, once, by whichever comes first of the tool
/// finishing, Ctrl-C and an error: the restore is idempotent and may be called from the thread that handles
/// Ctrl-C. The encoding is reached through two functions so a test needs no console.
/// </summary>
public sealed class ConsoleEncodingScope : IDisposable
{
    private readonly Action<Encoding> set;
    private Encoding? previous;

    public ConsoleEncodingScope(Func<Encoding> get, Action<Encoding> set)
    {
        this.set = set;
        try
        {
            var before = get();
            set(new UTF8Encoding(false));
            previous = before;
        }
        catch (Exception ex) when (FileSystemErrors.IsTransient(ex))
        {
            // no console to change: the bytes are UTF-8 regardless
        }
    }

    /// <summary>Puts the encoding back, if it was changed and not yet put back.</summary>
    public void Restore()
    {
        if (Interlocked.Exchange(ref previous, null) is not { } before)
        {
            return;
        }
        try
        {
            set(before);
        }
        catch (Exception ex) when (FileSystemErrors.IsTransient(ex))
        {
            // the console has gone
        }
    }

    public void Dispose() => Restore();
}
