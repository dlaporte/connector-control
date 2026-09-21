import CryptoKit
import Foundation

/// Content identity for a collection document, used to tell "the file changed" from "the file
/// was rewritten with the same bytes".
public enum ContentHash {
    public static func sha256(_ data: Data) -> String {
        "sha256:" + SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
    }
}
