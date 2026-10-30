namespace ConnectorControl.Core.Transport;

/// <summary>
/// Windows only: the Mac puts the tool on PATH with a link in /usr/local/bin.
///
/// The user's own environment as the registry keeps it: the seam through which the uninstall hook and the
/// command-line tool's install reach the user PATH, so tests never touch the real one.
/// </summary>
public interface IUserEnvironment
{
    /// <summary>The user PATH, unexpanded; null when there is none.</summary>
    string? ReadPath();

    /// <summary>Writes the user PATH, keeping the kind of value it was, and tells running programs it changed.</summary>
    void WritePath(string value);
}
