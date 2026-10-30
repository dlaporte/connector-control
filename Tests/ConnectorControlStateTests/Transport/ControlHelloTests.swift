import XCTest
import ConnectorControlCore
@testable import ConnectorControlTransport

/// Mirror: windows/tests/ConnectorControl.Core.Tests/Transport/ControlHelloTests.cs
final class ControlHelloTests: XCTestCase {
    private func parse(_ text: String) -> ControlHello { ControlHello.parse(Data(text.utf8)) }

    func testOnlyAWellFormedCommandLineHelloIsTheCommandLine() {
        XCTAssertEqual(parse(#"{"client":"cli","version":"1.5.0"}"#), ControlHello(isCLI: true, version: "1.5.0"))
        XCTAssertEqual(parse(#"{"version":"1.5.0","client":"cli","pid":7}"#), ControlHello(isCLI: true, version: "1.5.0"),
                       "other keys are ignored, so a later tool can say more")
        XCTAssertEqual(parse(#"{"client":"relay","version":"1.5.0"}"#), ControlHello(isCLI: false, version: "1.5.0"))
    }

    func testAnythingElseIsTheRelay() {
        let notCLI = [
            #"{"client":"cli"}"#, #"{"client":"cli","version":""}"#, #"{"client":"cli","version":1}"#,
            #"{"client":"CLI","version":"1.5.0"}"#, #"{"client":" cli","version":"1.5.0"}"#,
            #"["cli","1.5.0"]"#, #""cli""#, "", "cli", #"{"client":"cli","version":"1.5.0""#,
            #"{"jsonrpc":"2.0","id":0,"method":"initialize","params":{}}"#,
        ]
        for text in notCLI { XCTAssertFalse(parse(text).isCLI, text) }
        XCTAssertFalse(ControlHello.parse(Data([0x7B, 0xFF, 0x7D])).isCLI, "not UTF-8")
        XCTAssertFalse(parse(String(repeating: "[", count: 100_000)).isCLI, "nested past the limit, never parsed")
        XCTAssertEqual(parse(#"{"client":"CLI","version":"1.5.0"}"#).version, "1.5.0",
                       "a relay's version still counts when a tool is missing")
        XCTAssertEqual(ControlHello.unknown, ControlHello(isCLI: false, version: nil))
    }

    /// A hello with a trailing comma is not JSON, so it is the relay, as on Windows.
    func testAHelloWithATrailingCommaIsTheRelay() {
        XCTAssertFalse(parse(#"{"client":"cli","version":"1.5.0",}"#).isCLI)
        XCTAssertNil(parse(#"{"client":"cli","version":"1.5.0",}"#).version)
    }

    func testAHelloThatIsNotStrictUTF8IsTheRelayEvenWhenItsJSONIsOtherwiseACommandLineHello() {
        // A lossy decode would turn the stray byte into U+FFFD and still read a "cli" hello.
        var bytes = Data(#"{"client":"cli","version":"1.5.0","x":""#.utf8)
        bytes.append(0xFF)
        bytes.append(contentsOf: Data(#""}"#.utf8))
        XCTAssertEqual(ControlHello.parse(bytes), ControlHello.unknown)
    }

    func testAnEscapedLoneSurrogateIsTheRelay() {
        XCTAssertEqual(parse(#"{"client":"cli","version":"\ud800"}"#), ControlHello.unknown)
        XCTAssertEqual(parse(#"{"client":"cli","version":"1.5.0","\udc00":1}"#), ControlHello.unknown)
    }

    func testTheHelloLineIsCompactKeySortedJSON() {
        XCTAssertEqual(ControlHello.line(client: ControlHello.cliClient, version: "1.5.0"), #"{"client":"cli","version":"1.5.0"}"#)
        XCTAssertEqual(ControlHello.line(client: ControlHello.relayClient, version: "1.5.0"), #"{"client":"relay","version":"1.5.0"}"#)
    }
}
