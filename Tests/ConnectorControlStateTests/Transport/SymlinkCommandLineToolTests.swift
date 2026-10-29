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
        /// Runs while a script runs, before it answers: what the tool shows while the prompt is up.
        var whileRunning: (() -> Void)?
    }

    private let tool = "/Applications/Connector Control.app/Contents/MacOS/connector-control"

    /// Runs background work and posts at once: everything here happens on the test's own thread.
    private func host(transient: Bool) -> AppHost {
        AppHost(marshal: { work in MainActor.assumeIsolated(work) }, delay: { _, work in MainActor.assumeIsolated(work) },
                now: { Date() }, background: { $0() }, controlToolPath: tool, controlToolIsTransient: transient)
    }

    private func make(_ dir: TempDir, _ scripts: Scripts, transient: Bool = false, tool: String? = nil) -> SymlinkCommandLineTool {
        SymlinkCommandLineTool(toolPath: tool ?? self.tool, linkPath: dir.file("bin/connector-control").path, host: host(transient: transient),
                               runScript: { arguments in
                                   scripts.ran.append(arguments)
                                   scripts.whileRunning?()
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
        XCTAssertEqual(installer.installedLocation, installer.linkPath, "the link is where it is installed")
        XCTAssertEqual(answer(installer.install), .some(nil))
        XCTAssertFalse(installer.isBusy)
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
        var busy: [Bool] = []
        scripts.whileRunning = { MainActor.assumeIsolated { busy.append(installer.isBusy) } }
        XCTAssertEqual(answer(installer.install), .some(nil))
        XCTAssertEqual(busy, [true], "busy while the prompt is up")
        XCTAssertFalse(installer.isBusy)
        let script = SymlinkCommandLineTool.installScript.flatMap { ["-e", $0] }
        XCTAssertEqual(scripts.ran, [script + [tool, installer.linkPath, bin.path, CommandLineToolText.adminPrompt]],
                       "the paths and the prompt go as arguments, never as script")
        scripts.answer = (1, "0:332: execution error: User canceled. (-128)\n")
        XCTAssertEqual(answer(installer.install), .some(nil), "Cancel is no error")
        scripts.answer = (1, "7:332: execution error: connector-control-occupied (3)\n")
        XCTAssertEqual(answer(installer.install), .some(CommandLineToolText.occupiedError(installer.linkPath)),
                       "a file that appeared there between the check and the password is left alone")
        scripts.answer = (1, "7:332: execution error: ln: \(installer.linkPath): Permission denied (1)\n")
        XCTAssertEqual(answer(installer.install),
                       .some(CommandLineToolText.installFailedError("ln: \(installer.linkPath): Permission denied")),
                       "only the reason, not AppleScript's position or exit status")
        scripts.answer = (1, "7:332: execution error: the marker connector-control-occupied (3) came first, then more (1)\n")
        XCTAssertEqual(answer(installer.install),
                       .some(CommandLineToolText.installFailedError("the marker connector-control-occupied (3) came first, then more")),
                       "the marker counts only at the end")
        scripts.answer = (1, "7:332: execution error: not (-128) the end (1)\n")
        XCTAssertEqual(answer(installer.install),
                       .some(CommandLineToolText.installFailedError("not (-128) the end")), "nor does the cancel code")
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

    func testRemovingThroughAnAdministratorCancelsAndFailsInTheAppsWords() throws {
        let dir = TempDir(prefix: "cli-link"); defer { dir.dispose() }
        let bin = dir.file("bin")
        try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
        let scripts = Scripts()
        let installer = make(dir, scripts)
        try FileManager.default.createSymbolicLink(atPath: installer.linkPath, withDestinationPath: tool)
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: bin.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: bin.path) }
        scripts.answer = (1, "0:332: execution error: User canceled. (-128)\n")
        XCTAssertEqual(answer(installer.remove), .some(nil), "Cancel is no error")
        XCTAssertTrue(installer.isInstalled)
        scripts.answer = (1, "7:332: execution error: rm: Permission denied (1)\n")
        XCTAssertEqual(answer(installer.remove), .some(CommandLineToolText.removeFailedError("rm: Permission denied")))
    }

    /// The script and `isInstalled` judge the same text: a relative link that resolves to the tool is this
    /// install's, and the script is given the link's own text to compare.
    func testARelativeLinkToThisToolIsThisInstallsAndTheScriptGetsItsText() throws {
        let dir = TempDir(prefix: "cli-link"); defer { dir.dispose() }
        let bin = dir.file("bin")
        try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
        let scripts = Scripts()
        let installer = make(dir, scripts, tool: dir.file("app/connector-control").path)
        try FileManager.default.createSymbolicLink(atPath: installer.linkPath, withDestinationPath: "../app/connector-control")
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: bin.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: bin.path) }
        XCTAssertTrue(installer.isInstalled)
        XCTAssertEqual(answer(installer.remove), .some(nil))
        XCTAssertEqual(scripts.ran, [SymlinkCommandLineTool.removeScript.flatMap { ["-e", $0] }
                                        + [installer.linkPath, "../app/connector-control", CommandLineToolText.adminPrompt]])
    }

    /// No password for a change that is already made.
    func testInstallingWhatIsAlreadyInstalledAsksForNothing() throws {
        let dir = TempDir(prefix: "cli-link"); defer { dir.dispose() }
        let bin = dir.file("bin")
        try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
        let scripts = Scripts()
        let installer = make(dir, scripts)
        try FileManager.default.createSymbolicLink(atPath: installer.linkPath, withDestinationPath: tool)
        try FileManager.default.setAttributes([.posixPermissions: 0o500], ofItemAtPath: bin.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: bin.path) }
        XCTAssertEqual(answer(installer.install), .some(nil))
        XCTAssertEqual(scripts.ran, [])
    }

    /// Runs both scripts through the real `osascript` with the administrator clause taken out, on paths that
    /// would be script or shell if anything read them as such.
    func testTheScriptsRunWithoutAnAdministratorOnHostilePaths() throws {
        let dir = TempDir(prefix: "cli-link"); defer { dir.dispose() }
        let canary = dir.file("CANARY").path
        let hostile = "it's \"q\" $(touch \(canary)) `touch \(canary)` ; touch \(canary) \\back\nnew-line -lead $HOME %s & | > x"
        let toolFile = dir.file("App \(hostile)/-Contents/MacOS/connector-control")
        try FileManager.default.createDirectory(at: toolFile.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("#!/bin/sh\n".utf8).write(to: toolFile)
        let bin = dir.file("bin \(hostile)")   // not there yet: exercises mkdir -p
        let link = bin.appendingPathComponent("connector-control").path
        let tool = toolFile.path
        let prompt = CommandLineToolText.adminPrompt
        /// Only the shell text, through a plain `do shell script`: the production wrapper, with its administrator
        /// clause, is never handed to osascript here, and a script that mentions one never runs.
        func run(_ shell: String, _ arguments: [String]) throws -> (Int32, String) {
            let lines = ["on run argv", "do shell script " + shell, "end run"]
            if lines.contains(where: { $0.contains("administrator") }) {
                XCTFail("a script with an administrator clause must not run")
                throw CancellationError()
            }
            return SymlinkCommandLineTool.osascript(lines.flatMap { ["-e", $0] } + arguments)
        }
        // The production scripts are these shell texts and the privileges, nothing more.
        XCTAssertEqual(SymlinkCommandLineTool.installScript, ["on run argv", "do shell script \(SymlinkCommandLineTool.installShell)"
            + " with prompt (item 4 of argv) with administrator privileges", "end run"])
        XCTAssertEqual(SymlinkCommandLineTool.removeScript, ["on run argv", "do shell script \(SymlinkCommandLineTool.removeShell)"
            + " with prompt (item 3 of argv) with administrator privileges", "end run"])
        let fileManager = FileManager.default
        XCTAssertEqual(try run(SymlinkCommandLineTool.installShell, [tool, link, bin.path, prompt]).0, 0)
        XCTAssertEqual(try fileManager.destinationOfSymbolicLink(atPath: link), tool, "the link is made byte for byte")
        XCTAssertEqual(try run(SymlinkCommandLineTool.installShell, [tool, link, bin.path, prompt]).0, 0, "again, over the link")
        XCTAssertEqual(try fileManager.destinationOfSymbolicLink(atPath: link), tool)

        // Remove takes only a link that still names the tool.
        let other = tool + "-other"
        try fileManager.removeItem(atPath: link)
        try fileManager.createSymbolicLink(atPath: link, withDestinationPath: other)
        XCTAssertEqual(try run(SymlinkCommandLineTool.removeShell, [link, tool, prompt]).0, 0)
        XCTAssertEqual(try fileManager.destinationOfSymbolicLink(atPath: link), other, "another copy's link survives")
        try fileManager.removeItem(atPath: link)
        try fileManager.createSymbolicLink(atPath: link, withDestinationPath: tool)
        XCTAssertEqual(try run(SymlinkCommandLineTool.removeShell, [link, tool, prompt]).0, 0)
        XCTAssertFalse(fileManager.fileExists(atPath: link))
        XCTAssertNil(try? fileManager.destinationOfSymbolicLink(atPath: link))

        // A file that is not a link: install says so and leaves it, remove leaves it.
        try Data("mine".utf8).write(to: URL(fileURLWithPath: link))
        let occupied = try run(SymlinkCommandLineTool.installShell, [tool, link, bin.path, prompt])
        XCTAssertNotEqual(occupied.0, 0)
        XCTAssertTrue(SymlinkCommandLineTool.reason(from: occupied.1).hasSuffix(SymlinkCommandLineTool.occupiedMarker), occupied.1)
        XCTAssertEqual(try Data(contentsOf: URL(fileURLWithPath: link)), Data("mine".utf8))
        XCTAssertEqual(try run(SymlinkCommandLineTool.removeShell, [link, tool, prompt]).0, 0)
        XCTAssertEqual(try Data(contentsOf: URL(fileURLWithPath: link)), Data("mine".utf8))
        XCTAssertFalse(fileManager.fileExists(atPath: canary), "no path was ever run as a command")
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
