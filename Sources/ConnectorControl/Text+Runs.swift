import SwiftUI
import ConnectorControlState

/// Mac only: the Windows counterpart is windows/src/ConnectorControl.App/Views/TextRuns.cs.
extension Text {
    /// A sentence with its set-apart pieces in the monospaced face, each as `TextRun.shownText` spells it, as
    /// the Windows views draw a TextRun.
    init(runs: [TextRun]) {
        self = runs.reduce(Text(verbatim: "")) { text, run in
            text + (run.isSetApart ? Text(verbatim: run.shownText).monospaced() : Text(verbatim: run.text))
        }
    }
}
