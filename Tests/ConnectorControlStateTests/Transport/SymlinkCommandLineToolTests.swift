import XCTest
import ConnectorControlTestSupport
@testable import ConnectorControlState
@testable import ConnectorControlTransport

/// Mac only: the Windows counterpart is windows/tests/ConnectorControl.Core.Tests/Transport/UserPathCommandLineToolTests.cs.
@MainActor
final class SymlinkCommandLineToolTests: XCTestCase {
    final class Scripts: @unchecked Sendable {
        var ran: [[String]] = []
        var answer: (Int32, String) = (0, "")
    }

    private let tool = "/Applications/Connector Control.app/Contents/MacOS/connector-control"

    /// Runs background work and posts at once: everything here happens on the test's own thread.
    private func host(transient: Bool) -> AppHost {
        AppHost(marshal: { work in MainActor.assumeIsolated(work) }, delay: { _, work in MainActor.assumeIsolated(work) },
                now: { Date() }, background: { $0() }, controlToolPath: tool, controlToolIsTransient: transient)
    }

    private func make(_ dir: TempDir, _ scripts: Scripts, transient: Bool = false) -> SymlinkCommandLineTool {
        SymlinkCommandLineTool(toolPath: tool, linkPath: dir.file("bin/connector-control").path, host: host(transient: transient),
                               runScript: { arguments in
                                   scripts.ran.append(arguments)
                                   return scripts.answer
                               })
    }

    private func answer(_ act: (@escaping (String?) -> Void) -> Void) -> String?? {
        var said: String?? = .none
        act { said = .some($0) }
        return said
    }

    func testAFolderThePersonCanWriteGetsTheLinkWithNoPrompt() throws {
        let dir = TempDir(prefix: "cli-link"); defer { dir.dispose() }
        try FileManager.default.createDirectory(at: dir.file("bin"), withIntermediateDirectories: true)
        let scripts = Scripts()
        let installer = make(dir, scripts)
        XCTAssertFalse(installer.isInstalled)
        XCTAssertEqual(answer(installer.install), .some(nil))
        XCTAssertTrue(installer.isInstalled)
        XCTAssertEqual(try FileManager.default.destinationOfSymbolicLink(atPath: installer.linkPath), tool)
        XCTAssertEqual(answer(installer.remove), .some(nil))
        XCTAssertFalse(installer.isInstalled)
        XCTAssertFalse(FileManager.default.fileExists(atPath: installer.linkPath))
        XCTAssertEqual(scripts.ran, [], "no administrator prompt")
    }

    func testAFolderThePersonCannotWriteAsksForAnAdministrator() throws {
        let dir = TempDir(prefix: "cli-link"); defer { dir.dispose() }
        let bin = dir.file("bin")
        try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: bin.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: bin.path) }
        let scripts = Scripts()
        let installer = make(dir, scripts)
        XCTAssertEqual(answer(installer.install), .some(nil))
        let script = SymlinkCommandLineTool.installScript.flatMap { ["-e", $0] }
        XCTAssertEqual(scripts.ran, [script + [tool, installer.linkPath, bin.path, CommandLineToolText.adminPrompt]],
                       "the paths and the prompt go as arguments, never as script")
        scripts.answer = (1, "execution error: User canceled. (-128)\n")
        XCTAssertEqual(answer(installer.install), .some(nil), "Cancel is no error")
        scripts.answer = (1, "execution error: connector-control-occupied (3)\n")
        XCTAssertEqual(answer(installer.install), .some(CommandLineToolText.occupiedError(installer.linkPath)),
                       "a file that appeared there between the check and the password is left alone")
        scripts.answer = (1, "execution error: The operation couldn’t be completed. (1)\n")
        XCTAssertEqual(answer(installer.install),
                       .some(CommandLineToolText.installFailedError("execution error: The operation couldn’t be completed. (1)")))
    }

    /// Through an administrator, Remove hands the script the tool as well, and the script removes the
    /// link only while it still points there.
    func testRemovingThroughAnAdministratorTakesOnlyThisInstallsLink() throws {
        let dir = TempDir(prefix: "cli-link"); defer { dir.dispose() }
        let bin = dir.file("bin")
        try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
        let scripts = Scripts()
        let installer = make(dir, scripts)
        try FileManager.default.createSymbolicLink(atPath: installer.linkPath, withDestinationPath: tool)
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: bin.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: bin.path) }
        XCTAssertTrue(installer.isInstalled)
        XCTAssertEqual(answer(installer.remove), .some(nil))
        XCTAssertEqual(scripts.ran, [SymlinkCommandLineTool.removeScript.flatMap { ["-e", $0] }
                                        + [installer.linkPath, tool, CommandLineToolText.adminPrompt]])
    }

    func testAFileThatIsNotALinkIsNeverReplaced() throws {
        let dir = TempDir(prefix: "cli-link"); defer { dir.dispose() }
        try FileManager.default.createDirectory(at: dir.file("bin"), withIntermediateDirectories: true)
        let scripts = Scripts()
        let installer = make(dir, scripts)
        try Data("someone else's".utf8).write(to: URL(fileURLWithPath: installer.linkPath))
        XCTAssertEqual(answer(installer.install), .some(CommandLineToolText.occupiedError(installer.linkPath)))
        XCTAssertEqual(try Data(contentsOf: URL(fileURLWithPath: installer.linkPath)), Data("someone else's".utf8))
        XCTAssertEqual(answer(installer.remove), .some(nil), "a file that is not this install's link is not its to remove")
        XCTAssertTrue(FileManager.default.fileExists(atPath: installer.linkPath))
        XCTAssertEqual(scripts.ran, [])
    }

    /// A link to another copy of the app is not this install: Remove leaves it, and Install points it here.
    func testALinkToAnotherCopyIsNotThisInstallsToRemoveButInstallingRepointsIt() throws {
        let dir = TempDir(prefix: "cli-link"); defer { dir.dispose() }
        try FileManager.default.createDirectory(at: dir.file("bin"), withIntermediateDirectories: true)
        let installer = make(dir, Scripts())
        let other = "/Users/me/Downloads/Connector Control.app/Contents/MacOS/connector-control"
        try FileManager.default.createSymbolicLink(atPath: installer.linkPath, withDestinationPath: other)
        XCTAssertFalse(installer.isInstalled)
        XCTAssertEqual(answer(installer.remove), .some(nil))
        XCTAssertEqual(try FileManager.default.destinationOfSymbolicLink(atPath: installer.linkPath), other, "it survives Remove")
        XCTAssertEqual(answer(installer.install), .some(nil))
        XCTAssertTrue(installer.isInstalled)
    }

    /// An app run from where it will not stay would link a path that is about to be gone.
    func testAnAppRunFromWhereItWillNotStayCannotInstall() throws {
        let dir = TempDir(prefix: "cli-link"); defer { dir.dispose() }
        try FileManager.default.createDirectory(at: dir.file("bin"), withIntermediateDirectories: true)
        let scripts = Scripts()
        let installer = make(dir, scripts, transient: true)
        XCTAssertEqual(answer(installer.install), .some(ClaudeIntegration.moveToApplicationsError))
        XCTAssertFalse(FileManager.default.fileExists(atPath: installer.linkPath))
        XCTAssertEqual(scripts.ran, [])
    }
}
