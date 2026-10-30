import Combine
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/TestSupport/FakeCommandLineTool.cs
///
/// The command-line tool's seam: on PATH or not as the test says, its link where the Mac's would be
/// (nil, as on Windows, when a test says so), and Install and Remove held until `finish(installed:problem:)`,
/// busy meanwhile, as an administrator prompt or a PATH broadcast holds them. Each start and end is
/// announced on `changes`, and the last failure kept until the next try, as the real seams do.
@MainActor
final class FakeCommandLineTool: CommandLineTool {
    var toolPath = "/Applications/Connector Control.app/Contents/MacOS/connector-control"
    var installedLocation: String? = "/usr/local/bin/connector-control"
    var isInstalled = false
    private(set) var isBusy = false
    private(set) var lastProblem: String?
    private(set) var installs = 0
    private(set) var removals = 0
    private var pending: ((String?) -> Void)?
    private let changed = PassthroughSubject<Void, Never>()

    var changes: AnyPublisher<Void, Never> { changed.eraseToAnyPublisher() }

    func install(completion: @escaping (String?) -> Void) {
        installs += 1
        start(completion)
    }

    func remove(completion: @escaping (String?) -> Void) {
        removals += 1
        start(completion)
    }

    private func start(_ completion: @escaping (String?) -> Void) {
        isBusy = true
        lastProblem = nil
        pending = completion
        changed.send()
    }

    /// Ends the operation in flight: the tool is then on PATH or not, and the operation answers
    /// `problem`, nil for done or cancelled.
    func finish(installed: Bool, problem: String? = nil) {
        isInstalled = installed
        isBusy = false
        lastProblem = problem
        changed.send()
        let done = pending
        pending = nil
        done?(problem)
    }
}
