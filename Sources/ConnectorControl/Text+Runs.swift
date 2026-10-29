import SwiftUI
import ConnectorControlState

extension Text {
    /// A sentence with its set-apart pieces in the monospaced face, each as `TextRun.shownText` spells it, as
    /// the Windows views draw a TextRun.
    init(runs: [TextRun]) {
        self = runs.reduce(Text(verbatim: "")) { text, run in
            text + (run.isSetApart ? Text(verbatim: run.shownText).monospaced() : Text(verbatim: run.text))
        }
    }
}
