import Foundation

/// Mirror: windows/src/ConnectorControl.Core/Transport/IControlStream.cs
///
/// A client's connection to the app, as bytes: the command-line tool and the relay read and write
/// through it, over the Mac's socket or Windows' pipe.
public protocol ControlStream: AnyObject, Sendable {
    /// Up to `maximum` bytes, waiting for at least one; empty at the end of the stream or on failure.
    func read(maximum: Int) -> Data
    /// Writes all of `data`; false when the stream has failed.
    func write(_ data: Data) -> Bool
    /// Ends the stream both ways; a read waiting on it returns empty. Safe to call more than once.
    func close()
    /// Ends only the writing half: the app reads the end of the stream and closes its side, while what it
    /// had already sent can still be read here. Safe to call more than once. Platform idiom: Windows has
    /// no half-close for a pipe, so there this does nothing.
    func closeWrite()
}
