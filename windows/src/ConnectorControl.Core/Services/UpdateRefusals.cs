namespace ConnectorControl.Core.Services;

/// <summary>
/// Why the Windows updater refused an update, as the update dialog and the refusal toast say it. The checks are
/// the App's (UpdateVerifier, VelopackUpdater); the wording is here, in Core, so the shared string catalog pins
/// every word of it. Windows only: Sparkle checks the Mac's updates and words its own refusals.
/// </summary>
public static class UpdateRefusals
{
    /// <summary>Every refusal ends with this.</summary>
    public const string NotInstalledSuffix = "; the update was not installed.";

    /// <summary>The binary whose embedded product version must match the feed; every package carries it.</summary>
    public const string MainExecutable = "ConnectorControl.exe";

    /// <summary>The updater as a refusal names it when it is the one installed, not the package's.</summary>
    public const string InstalledUpdater = "The installed Update.exe";

    public const string CannotValidateSelf = "This app's own signature could not be validated, so an update cannot be checked against it" + NotInstalledSuffix;

    public const string LocationUnknown = "This app's own location is unknown, so an update cannot be checked" + NotInstalledSuffix;

    public const string MainExecutableTwice = $"The update package contains {MainExecutable} more than once{NotInstalledSuffix}";

    public const string NoMainExecutable = $"The update package does not contain {MainExecutable}{NotInstalledSuffix}";

    public const string NoVersion = $"{MainExecutable} inside the update carries no version{NotInstalledSuffix}";

    /// <summary>A file of the package, as a refusal names it.</summary>
    public static string InsideTheUpdate(string fileName) => $"{fileName} inside the update";

    /// <summary><paramref name="entryName"/> is already shown safely: a control character spelled out as \uXXXX.</summary>
    public static string InvalidCharacterEntry(string entryName) =>
        $"The update package contains an entry named \"{entryName}\", with a character no Windows file name can hold{NotInstalledSuffix}";

    public static string RenamedEntry(string entryName) =>
        $"The update package contains an entry named \"{entryName}\", which Windows would rename on extraction{NotInstalledSuffix}";

    public static string SizeMismatch(string entryName, long declared, long actual) =>
        $"The update package entry \"{entryName}\" declares {declared} bytes but holds {actual}{NotInstalledSuffix}";

    public static string NotValidlySigned(string file) => $"{file} is not validly signed{NotInstalledSuffix}";

    public static string SignedByAnother(string file, string subject) =>
        $"{file} is signed by \"{subject}\", not by this app's publisher{NotInstalledSuffix}";

    public static string SignedByUntrusted(string file, string subject) =>
        $"{file} is signed by \"{subject}\", not by this app's publisher or one it is built from{NotInstalledSuffix}";

    public static string VersionMismatch(string embedded, string feed) =>
        $"{MainExecutable} inside the update is version {embedded}, but the update feed says {feed}{NotInstalledSuffix}";

    public static string OlderVersion(string embedded, string running) =>
        $"{MainExecutable} inside the update is version {embedded}, older than the installed {running}{NotInstalledSuffix}";

    public static string PreviewForRelease(string embedded, string running) =>
        $"{MainExecutable} inside the update is the preview {embedded}, and the installed {running} takes releases only{NotInstalledSuffix}";

    public static string Unreadable(string reason) => $"The update package could not be read ({reason}){NotInstalledSuffix}";
}
