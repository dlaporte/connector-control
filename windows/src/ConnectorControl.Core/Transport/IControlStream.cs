namespace ConnectorControl.Core.Transport;

/// <summary>
/// Mirror: Sources/ConnectorControlTransport/ControlStream.swift
///
/// A client's connection to the app, as bytes: the command-line tool and the relay read and write
/// through it, over the Mac's socket or Windows' pipe.
/// </summary>
public interface IControlStream
{
    /// <summary>Up to <paramref name="maximum"/> bytes, waiting for at least one; empty at the end of the
    /// stream or on failure.</summary>
    byte[] Read(int maximum);

    /// <summary>Writes all of <paramref name="data"/>; false when the stream has failed.</summary>
    bool Write(byte[] data);

    /// <summary>Ends the stream both ways; a read waiting on it returns empty. Safe to call more than once.</summary>
    void Close();

    /// <summary>Ends only the writing half: the app reads the end of the stream and closes its side, while what it
    /// had already sent can still be read here. Safe to call more than once. Platform idiom: a pipe has no
    /// half-close, so here a pipe broken by the app's going is left to be read to its end, and one still
    /// connected is closed both ways.</summary>
    void CloseWrite();
}
