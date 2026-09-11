import Foundation
import XCTest

/// Plants an allow-read ACE for group:everyone on `path` via /bin/chmod +a, optionally inheritable,
/// so a test can prove the code under test removed it. Fails the test if chmod does not exit 0.
func grantEveryoneRead(at path: String, inheritable: Bool, file: StaticString = #filePath, line: UInt = #line) throws {
    let chmod = Process()
    chmod.executableURL = URL(fileURLWithPath: "/bin/chmod")
    let ace = inheritable ? "group:everyone allow read,file_inherit,directory_inherit" : "group:everyone allow read"
    chmod.arguments = ["+a", ace, path]
    try chmod.run()
    chmod.waitUntilExit()
    XCTAssertEqual(chmod.terminationStatus, 0, "chmod +a failed", file: file, line: line)
}
