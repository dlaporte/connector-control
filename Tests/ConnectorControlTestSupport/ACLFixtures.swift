import Foundation
import XCTest

/// Plants an allow-read ACE for group:everyone on `path` via /bin/chmod +a, optionally inheritable,
/// so a test can prove the code under test removed it. Fails the test if chmod does not exit 0.
public func grantEveryoneRead(at path: String, inheritable: Bool, file: StaticString = #filePath, line: UInt = #line) throws {
    let chmod = Process()
    chmod.executableURL = URL(fileURLWithPath: "/bin/chmod")
    let ace = inheritable ? "group:everyone allow read,file_inherit,directory_inherit" : "group:everyone allow read"
    chmod.arguments = ["+a", ace, path]
    try chmod.run()
    chmod.waitUntilExit()
    XCTAssertEqual(chmod.terminationStatus, 0, "chmod +a failed", file: file, line: line)
}

/// True when the object carries any ACL entry (inherited or explicit). Test-only:
/// production code strips ACLs but never needs to ask whether one remains.
public func hasACL(atPath path: String) -> Bool {
    guard let acl = acl_get_link_np(path, ACL_TYPE_EXTENDED) else { return false }
    acl_free(UnsafeMutableRawPointer(acl))
    return true
}
