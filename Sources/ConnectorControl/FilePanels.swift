import AppKit
import UniformTypeIdentifiers
import ConnectorControlState

/// The native panels the windows put up, built in one place so a filter or a prompt cannot drift
/// between copies. Each answers with what was chosen, or nil when the panel was cancelled. The
/// Windows mirror is its Pickers class.
@MainActor
enum FilePanels {
    /// One collection document, for Import, Subscribe and a collection asking to be pointed at
    /// its source again.
    static func chooseCollectionDocument() -> String? {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = false
        panel.canChooseFiles = true
        panel.allowsMultipleSelection = false
        panel.allowedContentTypes = [.json]
        guard panel.runModal() == .OK else { return nil }
        return panel.url?.path
    }

    /// One folder: the store's, or the one a collection publishes to.
    static func chooseFolder() -> String? {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        panel.prompt = SettingsModel.chooseTitle
        guard panel.runModal() == .OK else { return nil }
        return panel.url?.path
    }

    /// Where an exported collection document goes, offering `fileName` to start from.
    static func saveCollectionDocument(named fileName: String) -> String? {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = fileName
        panel.allowedContentTypes = [.json]
        guard panel.runModal() == .OK else { return nil }
        return panel.url?.path
    }
}
