namespace ConnectorControl.Core;

/// <summary>
/// Mirror: Sources/ConnectorControlCore/McpLineFramer.swift
///
/// One message as the transport read it: a line's bytes without their newline, or word that a line
/// ran past the limit - whose bytes were dropped as they came, so a client cannot make the reader
/// hold more than the limit. The Mac mirror's enum carries the bytes; here each frame is a record,
/// and a <see cref="Line"/> compares its array by reference, as arrays do, so tests compare
/// <c>Bytes</c> with <c>SequenceEqual</c>.
/// </summary>
public abstract record McpFrame
{
    private McpFrame()
    {
    }

    public sealed record Line(byte[] Bytes) : McpFrame;

    public sealed record TooLarge : McpFrame;
}

/// <summary>
/// Splits a byte stream into newline-delimited messages, holding at most <see cref="Limit"/> bytes of
/// a line. A line longer than that is reported once, as <see cref="McpFrame.TooLarge"/>, and skipped
/// up to its newline; the stream carries on after it. Platform idiom: a class, where the Mac mirror is
/// a struct its one owner mutates in place.
/// </summary>
public sealed class McpLineFramer(int limit = McpSession.MaxMessageBytes)
{
    private readonly MemoryStream buffer = new();
    /// <summary>Inside a line already reported as too large.</summary>
    private bool skipping;

    public int Limit { get; } = limit;

    public IReadOnlyList<McpFrame> Append(ReadOnlySpan<byte> chunk)
    {
        var frames = new List<McpFrame>();
        var rest = chunk;
        int newline;
        while ((newline = rest.IndexOf((byte)'\n')) >= 0)
        {
            var piece = rest[..newline];
            rest = rest[(newline + 1)..];
            if (skipping)
            {
                skipping = false;
            }
            else if (buffer.Length + piece.Length > Limit)
            {
                frames.Add(new McpFrame.TooLarge());
            }
            else
            {
                buffer.Write(piece);
                frames.Add(new McpFrame.Line(buffer.ToArray()));
            }
            buffer.SetLength(0);
        }
        if (!skipping)
        {
            if (buffer.Length + rest.Length > Limit)
            {
                buffer.SetLength(0);
                skipping = true;
                frames.Add(new McpFrame.TooLarge());
            }
            else
            {
                buffer.Write(rest);
            }
        }
        return frames;
    }

    /// <summary>The end of the stream: a last line without its newline is still a line.</summary>
    public IReadOnlyList<McpFrame> Finish()
    {
        IReadOnlyList<McpFrame> frames = skipping || buffer.Length == 0 ? [] : [new McpFrame.Line(buffer.ToArray())];
        buffer.SetLength(0);
        skipping = false;
        return frames;
    }
}
