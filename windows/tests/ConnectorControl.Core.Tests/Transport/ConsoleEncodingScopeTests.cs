using System.Text;
using ConnectorControl.Core.Transport;

namespace ConnectorControl.Core.Tests.Transport;

/// <summary>Windows only: the Mac has no counterpart. The restore logic behind the tool's console code page,
/// through the seam a test can reach without a console.</summary>
public class ConsoleEncodingScopeTests
{
    private static readonly Encoding Original = Encoding.Latin1;

    [Fact]
    public void TheEncodingIsPutBackOnceHoweverOftenItIsAskedFor()
    {
        var current = Original;
        var sets = 0;
        var scope = new ConsoleEncodingScope(() => current, e => { current = e; sets++; });
        Assert.Equal("utf-8", current.WebName);
        scope.Restore();
        scope.Restore();
        scope.Dispose();
        Assert.Same(Original, current);
        Assert.Equal(2, sets);
    }

    [Fact]
    public void AnEncodingThatCannotBeSetIsNoReasonToFail()
    {
        var scope = new ConsoleEncodingScope(() => Original, _ => throw new IOException("no console"));
        scope.Dispose();
    }
}
