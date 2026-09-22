import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// Guards every user-facing string this app and the Windows port are meant to
/// carry byte-for-byte, against the shared fixture both suites read:
/// `Tests/Fixtures/strings.json`. windows/tests/ConnectorControl.Core.Tests/State/StringCatalogTests.cs
/// is this test's mirror; a wording change on either side that the other
/// side does not also make fails exactly one of the two suites.
@MainActor
final class StringCatalogTests: XCTestCase {
    /// One fixture entry's Mac-side resolution: either fully resolved text,
    /// or a `{n}`-style template that still needs this test's fixed
    /// arguments substituted in.
    private enum MacValue {
        case plain(String)
        case format(String)
    }

    /// nil means the key is Windows-only (no `mac` side, and not a bare
    /// shared value) — such a key must never appear in `actual` below.
    private static func macValue(_ entry: JSONValue) -> MacValue? {
        switch entry {
        case .string(let s):
            return .plain(s)
        case .object(let obj):
            if case .string(let format)? = obj["format"] {
                return .format(format)
            }
            guard let mac = obj["mac"] else { return nil }
            switch mac {
            case .string(let s):
                return .plain(s)
            case .object(let macObj):
                if case .string(let format)? = macObj["format"] { return .format(format) }
                return nil
            default:
                return nil
            }
        default:
            return nil
        }
    }

    /// Substitutes `{0}`, `{1}`, … with `args`, in order — the same
    /// convention the C# test's fixture resolution uses.
    private static func expand(_ format: String, _ args: [String]) -> String {
        var result = format
        for (index, arg) in args.enumerated() {
            result = result.replacingOccurrences(of: "{\(index)}", with: arg)
        }
        return result
    }

    private static func loadFixture() throws -> [String: JSONValue] {
        let data = try Data(contentsOf: Fixtures.url("strings.json"))
        guard case .object(let dict) = try JSONValue.parse(data) else {
            XCTFail("Tests/Fixtures/strings.json: root is not an object")
            return [:]
        }
        return dict
    }

    /// Fixed arguments, by key, for every fixture entry whose Mac value is a
    /// `{n}` template: `"X"`/`"Y"` for string parameters, `"3"`/`"7"` for int
    /// parameters, and the joined-keys rendering for a factory that takes a
    /// list of keys — matching the convention the C# test also follows.
    private static let argsByKey: [String: [String]] = [
        "AppState.collectionLocateBanner": ["X"],
        "AppState.collectionPublishFailedBanner": ["X", "Y", "Z"],
        "AppState.collectionUpdateBanner": ["X", "Y"],
        "AppState.collectionUpdateNotificationBody": ["X", "Y"],
        "AppState.deleteCollectionMessage": ["X"],
        "AppState.duplicateNameError": ["X"],
        "AppState.enabledSubtitle": ["3", "7"],
        "AppState.malformedConfigMessage": ["X"],
        "AppState.needsValueCaution": ["X"],
        "AppState.keptPathCarriedError": ["X", "Y"],
        "AppState.pathMarkMovedError": ["X"],
        "AppState.publishFolderCarriedError": ["X", "Y"],
        "AppState.publishSlugTakenError": ["X"],
        "AppState.restoreCollectionGoneError": ["X"],
        "AppState.sourceUnreadableError": ["X", "Y"],
        "ClaudeSignature.notFoundMessage": ["X"],
        "ClaudeSignature.refusalMessage": ["X", "Y"],
        "ClaudeSignature.uninspectableMessage": ["X"],
        "CollectionsModel.deletePublishedFileQuestion": ["X"],
        "CollectionsModel.exportButton": ["3"],
        "CollectionsModel.localDetail": ["3"],
        "CollectionsModel.localType": ["X"],
        "CollectionsModel.publishedDetail": ["X"],
        "CollectionsModel.syncedDetail": ["X", "Y"],
        "ConfigService.corruptStoreNote": ["X"],
        "ConfigService.invalidBackupError": ["X", "Y"],
        "ConfigService.invalidBackupMcpServersError": ["X"],
        "ConnectorRow.editTooltip": ["X"],
        "EditTarget.editTitle": ["X"],
        "EditorModel.additionalTitle": ["3", "a, b"],
        "EditorModel.changedOutsideMessage": ["X"],
        "EditorModel.duplicateEnvError": ["X"],
        "EditorModel.importedNote": ["X", "Y"],
        "EditorModel.lockedFieldsNote": ["X"],
        "EditorModel.propagateLabel": ["X", "Y"],
        "EditorModel.publishedNote": ["X"],
        "EditorModel.removeMessage": ["X"],
        "EditorModel.removedOutsideMessage": ["X"],
        "ImportModel.addModeTitle": ["X"],
        "ImportModel.collisionPickerLabel": ["X"],
        "ImportModel.importButton": ["3"],
        "ImportModel.includeLabel": ["X"],
        "ImportModel.skippedBadge": ["X"],
        "ImportModel.sourceLine": ["X", "Y", "3"],
        "MasterStore.duplicateCollectionNameError": ["X"],
        "MasterStore.unknownCollectionError": ["X"],
        "PopoverModel.exportTitleFor": ["X"],
        "PopoverModel.locateButton": ["X"],
        "PopoverModel.sourceTooltipFormat": ["X"],
        "PublishModel.exportTitle": ["X"],
        "PublishModel.folderLine": ["X"],
        "PublishModel.footerLine": ["X", "Y"],
        "PublishModel.title": ["X"],
        "PublishModel.keptPathNote": ["X", "Y"],
        "PublishModel.otherFolderNote": ["X", "Y", "Z"],
        "PublishModel.publishFolderEditNote": ["X", "Y"],
        "PublishModel.publishFolderNote": ["X", "Y"],
        "PublishModel.unresolvedMarkNote": ["X", "Y"],
        "PublishModel.warningLine": ["X", "Y"],
        "RestoreModel.confirmMessage": ["X"],
        "ReviewModel.title": ["X"],
        "SettingsModel.keepCountLabel": ["3"],
        "SettingsModel.loginItemFailureNote": ["X"],
        "SettingsModel.versionText": ["X"],
        "ToolNote.shellOnlyText": ["X"],
    ]

    func testEveryStringMatchesTheSharedCatalog() throws {
        let fixture = try Self.loadFixture()
        var actual: [String: String] = [:]

        // MARK: AppState

        actual["AppState.authoredElsewhereCaution"] = AppState.authoredElsewhereCaution
        actual["AppState.chooseClaude"] = AppState.chooseClaude
        actual["AppState.claudeConfigChangedBody"] = AppState.claudeConfigChangedBody
        actual["AppState.claudeConfigRegeneratedBody"] = AppState.claudeConfigRegeneratedBody
        actual["AppState.collectionLocateBanner"] = AppState.collectionLocateBanner("X")
        actual["AppState.collectionPublishFailedBanner"] = AppState.collectionPublishFailedBanner("X", "Y", "Z")
        actual["AppState.collectionUpdateBanner"] = AppState.collectionUpdateBanner("X", "Y")
        actual["AppState.collectionUpdateNotificationBody"] = AppState.collectionUpdateNotificationBody("X", "Y")
        actual["AppState.collectionsNotSavedNote"] = AppState.collectionsNotSavedNote
        actual["AppState.connectorListChangedBody.noRestart"] =
            AppState.connectorListChangedBody(ServerDelta(), restartRequired: false)
        actual["AppState.connectorListChangedBody.restart"] =
            AppState.connectorListChangedBody(ServerDelta(), restartRequired: true)
        actual["AppState.defaultClaudeAppPath"] = AppState.defaultClaudeAppPath
        actual["AppState.deleteButton"] = AppState.deleteButton
        actual["AppState.deleteCollectionInformative"] = AppState.deleteCollectionInformative
        actual["AppState.deleteCollectionMessage"] = AppState.deleteCollectionMessage("X")
        actual["AppState.duplicateNameError"] = AppState.duplicateNameError("X")
        actual["AppState.enabledSubtitle"] = AppState.enabledSubtitle(enabled: 3, total: 7)
        actual["AppState.lastLocalCollectionError"] = AppState.lastLocalCollectionError
        actual["AppState.locateCaution"] = AppState.locateCaution
        actual["AppState.malformedConfigMessage"] = AppState.malformedConfigMessage(detail: "X")
        actual["AppState.nameEmptyError"] = AppState.nameEmptyError
        actual["AppState.needsValueCaution"] = AppState.needsValueCaution("X")
        actual["AppState.newCollectionTitle"] = AppState.newCollectionTitle
        actual["AppState.newerDocumentError"] = AppState.newerDocumentError
        actual["AppState.noConnectorsSubtitle"] = AppState.noConnectorsSubtitle
        actual["AppState.ownCollectionError"] = AppState.ownCollectionError
        actual["AppState.keptPathCarriedError"] = AppState.keptPathCarriedError("X", "Y")
        actual["AppState.pathMarkMovedError"] = AppState.pathMarkMovedError("X")
        actual["AppState.publishFolderCarriedError"] = AppState.publishFolderCarriedError("X", "Y")
        actual["AppState.publishIntoStoreError"] = AppState.publishIntoStoreError
        actual["AppState.publishSlugTakenError"] = AppState.publishSlugTakenError("X")
        actual["AppState.quitButton"] = AppState.quitButton
        actual["AppState.quitMessage"] = AppState.quitMessage
        actual["AppState.regenerationFailedBody"] = AppState.regenerationFailedBody
        actual["AppState.renameCollectionTitle"] = AppState.renameCollectionTitle
        actual["AppState.restartButton"] = AppState.restartButton
        actual["AppState.restartInformative"] = AppState.restartInformative
        actual["AppState.restartMessage"] = AppState.restartMessage
        actual["AppState.restoreCollectionGoneError"] = AppState.restoreCollectionGoneError("X")
        actual["AppState.sourceUnreadableError"] = AppState.sourceUnreadableError("X", "Y")
        actual["AppState.storeChangedBody"] = AppState.storeChangedBody
        actual["AppState.targetMustBeLocalError"] = AppState.targetMustBeLocalError
        actual["AppState.unpublishedDirectoryCaution"] = AppState.unpublishedDirectoryCaution

        // MARK: ClaudeConfigIO
        // Both messages are inline literals inside guard/throw statements, not
        // exposed as callable factories; pinned here to match ClaudeConfigIO.swift
        // and cross-checked against ClaudeConfigIOTests.swift's real-parse assertions.

        actual["ClaudeConfigIO.mcpServersNotObjectError"] = "mcpServers is not a JSON object"
        actual["ClaudeConfigIO.topLevelNotObjectError"] = "top level is not a JSON object"

        // MARK: ClaudeRestarter / ClaudeSignature
        // ClaudeRestarter.swift lives in the ConnectorControl app target (AppKit),
        // which this test target cannot import; its one user-facing literal is
        // pinned here by hand, matching Sources/ConnectorControl/ClaudeRestarter.swift.

        actual["ClaudeRestarter.didNotQuitMessage"] =
            "Claude didn\u{2019}t quit (it may be showing a dialog). Quit it manually, then click Restart Claude again."
        actual["ClaudeSignature.notFoundMessage"] = ClaudeSignature.notFoundMessage(path: "X")
        actual["ClaudeSignature.refusalMessage"] = ClaudeSignature.refusalMessage(name: "X", detail: "Y")
        actual["ClaudeSignature.requirementCompileFailure"] = ClaudeSignature.requirementCompileFailure
        actual["ClaudeSignature.uninspectableMessage"] = ClaudeSignature.uninspectableMessage(name: "X")

        // MARK: ConfigService
        // Every one of these is an inline literal built inside a throw or a notes
        // array, not a callable factory; pinned here to match ConfigService.swift
        // and cross-checked against ConfigServiceTests.swift's real-I/O assertions.

        actual["ConfigService.corruptStoreNote"] =
            "The MCP list file was unreadable; it was preserved as X and rebuilt from Claude's config."
        actual["ConfigService.invalidBackupError"] = "backup X is not a valid config file (Y)"
        actual["ConfigService.invalidBackupMcpServersError"] = "backup X has an invalid mcpServers section"
        actual["ConfigService.malformedClaudeConfigNote"] =
            "Claude's config file is not valid JSON. Your MCP list is safe; use Backups \u{25B8} Restore\u{2026} to repair the file."

        // MARK: CollectionsModel

        actual["CollectionsModel.activeSuffix"] = CollectionsModel.activeSuffix
        actual["CollectionsModel.deleteAction"] = CollectionsModel.deleteAction
        actual["CollectionsModel.deletePublishedFileQuestion"] = CollectionsModel.deletePublishedFileQuestion("X")
        actual["CollectionsModel.editTooltip"] = CollectionsModel.editTooltip
        actual["CollectionsModel.exportButton"] = CollectionsModel.exportButton(3)
        actual["CollectionsModel.importButton"] = CollectionsModel.importButton
        actual["CollectionsModel.keepFileButton"] = CollectionsModel.keepFileButton
        actual["CollectionsModel.localDetail"] = CollectionsModel.localDetail(3)
        actual["CollectionsModel.localType"] = CollectionsModel.localType("X")
        actual["CollectionsModel.lockedGlyphTooltip"] = CollectionsModel.lockedGlyphTooltip
        actual["CollectionsModel.makeActiveAction"] = CollectionsModel.makeActiveAction
        actual["CollectionsModel.makeLocalCopyButton"] = CollectionsModel.makeLocalCopyButton
        actual["CollectionsModel.newButton"] = CollectionsModel.newButton
        actual["CollectionsModel.publishButton"] = CollectionsModel.publishButton
        actual["CollectionsModel.publishedDetail"] = CollectionsModel.publishedDetail("X")
        actual["CollectionsModel.refreshButton"] = CollectionsModel.refreshButton
        actual["CollectionsModel.remoteType"] = CollectionsModel.remoteType
        actual["CollectionsModel.removeFileButton"] = CollectionsModel.removeFileButton
        actual["CollectionsModel.renameAction"] = CollectionsModel.renameAction
        actual["CollectionsModel.stopPublishingAction"] = CollectionsModel.stopPublishingAction
        actual["CollectionsModel.stopSyncingAction"] = CollectionsModel.stopSyncingAction
        actual["CollectionsModel.subscribeButton"] = CollectionsModel.subscribeButton
        actual["CollectionsModel.syncedDetail"] = CollectionsModel.syncedDetail("X", "Y")
        actual["CollectionsModel.unlocatedDetail"] = CollectionsModel.unlocatedDetail
        actual["CollectionsModel.updateAvailableStatus"] = CollectionsModel.updateAvailableStatus
        actual["CollectionsModel.upToDateStatus"] = CollectionsModel.upToDateStatus
        actual["CollectionsModel.windowTitle"] = CollectionsModel.windowTitle

        // MARK: ConnectorRow / Dialogs / AlertDialogs

        actual["ConnectorRow.editTooltip"] = ConnectorRow(name: "X", enabled: true, toolWarning: nil).editTooltip
        actual["Dialogs.cancelTitle"] = FakeDialogs.cancelTitle
        // AlertDialogs.swift lives in the ConnectorControl app target (AppKit),
        // which this test target cannot import; its OK button title is pinned
        // here by hand, matching Sources/ConnectorControl/AlertDialogs.swift.
        actual["AlertDialogs.okTitle"] = "OK"

        // MARK: EditTarget / EditorModel

        actual["EditTarget.addTitle"] = EditTarget.addTitle
        actual["EditTarget.editTitle"] = EditTarget.editTitle("X")
        actual["EditorModel.addArgumentTitle"] = EditorModel.addArgumentTitle
        actual["EditorModel.addVariableTitle"] = EditorModel.addVariableTitle
        actual["EditorModel.additionalTitle"] = EditorModel.additionalTitle(count: 3, keys: ["a", "b"])
        actual["EditorModel.automaticCaption"] = EditorModel.automaticCaption
        actual["EditorModel.bearerCaption"] = EditorModel.bearerCaption
        actual["EditorModel.bearerTokenError"] = EditorModel.bearerTokenError
        actual["EditorModel.changedOutsideDetail"] = EditorModel.changedOutsideDetail
        actual["EditorModel.changedOutsideMessage"] = EditorModel.changedOutsideMessage("X")
        actual["EditorModel.clientIDError"] = EditorModel.clientIDError
        actual["EditorModel.commandError"] = EditorModel.commandError
        actual["EditorModel.duplicateEnvError"] = EditorModel.duplicateEnvError("X")
        actual["EditorModel.envNamelessError"] = EditorModel.envNamelessError
        actual["EditorModel.headerNameError"] = EditorModel.headerNameError
        actual["EditorModel.headerValueError"] = EditorModel.headerValueError
        actual["EditorModel.importedNote"] = EditorModel.importedNote("X", "Y")
        actual["EditorModel.invalidURLError"] = EditorModel.invalidURLError
        actual["EditorModel.jsonTip"] = EditorModel.jsonTip
        actual["EditorModel.lockedFieldsNote"] = EditorModel.lockedFieldsNote("X")
        actual["EditorModel.lossWarningPrefix"] = EditorModel.lossWarningPrefix
        actual["EditorModel.makeLocalCopyButton"] = EditorModel.makeLocalCopyButton
        actual["EditorModel.needsPath"] = EditorModel.needsPath
        actual["EditorModel.needsValue"] = EditorModel.needsValue
        actual["EditorModel.notValidJSON"] = EditorModel.notValidJSON
        actual["EditorModel.oauthSecretCaption"] = EditorModel.oauthSecretCaption
        actual["EditorModel.propagateLabel"] = EditorModel.propagateLabel("X", "Y")
        actual["EditorModel.publishedNote"] = EditorModel.publishedNote("X")
        actual["EditorModel.remoteFooter"] = EditorModel.remoteFooter
        actual["EditorModel.removeButton"] = EditorModel.removeButton
        actual["EditorModel.removeInformative"] = EditorModel.removeInformative
        actual["EditorModel.removeMessage"] = EditorModel.removeMessage("X")
        actual["EditorModel.removedOutsideDetail"] = EditorModel.removedOutsideDetail
        actual["EditorModel.removedOutsideMessage"] = EditorModel.removedOutsideMessage("X")
        actual["EditorModel.saveAnywayButton"] = EditorModel.saveAnywayButton
        actual["EditorModel.stayInJSONButton"] = EditorModel.stayInJSONButton
        actual["EditorModel.switchAnywayButton"] = EditorModel.switchAnywayButton
        actual["EditorModel.urlHint"] = EditorModel.urlHint
        actual["EditorModel.whatCanIChange"] = EditorModel.whatCanIChange
        actual["EditorModel.whatCanIChangeAnswer"] = EditorModel.whatCanIChangeAnswer

        // MARK: ImportModel

        actual["ImportModel.addModeDetail"] = ImportModel.addModeDetail
        actual["ImportModel.addModeTitle"] = ImportModel.addModeTitle("X")
        actual["ImportModel.addTitle"] = ImportModel.addTitle
        actual["ImportModel.cancelButton"] = ImportModel.cancelButton
        actual["ImportModel.collisionPickerLabel"] = ImportModel.collisionPickerLabel("X")
        actual["ImportModel.importButton"] = ImportModel.importButton(3)
        actual["ImportModel.includeLabel"] = ImportModel.includeLabel("X")
        actual["ImportModel.keepBothTitle"] = ImportModel.keepBothTitle
        actual["ImportModel.newBadge"] = ImportModel.newBadge
        actual["ImportModel.presentBadge"] = ImportModel.presentBadge
        actual["ImportModel.replaceKeepsValues"] = ImportModel.replaceKeepsValues
        actual["ImportModel.replaceTitle"] = ImportModel.replaceTitle
        actual["ImportModel.skipTitle"] = ImportModel.skipTitle
        actual["ImportModel.skippedBadge"] = ImportModel.skippedBadge("X")
        actual["ImportModel.sourceLine"] = ImportModel.sourceLine("X", "Y", 3)
        actual["ImportModel.syncModeDetail"] = ImportModel.syncModeDetail
        actual["ImportModel.syncModeTitle"] = ImportModel.syncModeTitle
        actual["ImportModel.syncNameLabel"] = ImportModel.syncNameLabel
        actual["ImportModel.title"] = ImportModel.title
        actual["ImportModel.unknownAuthor"] = ImportModel.unknownAuthor

        // MARK: MasterStore
        // Every message below is returned by a real mutation, not a bare
        // constant — each store is set up so that mutation fails for exactly
        // the reason this key names.

        var nameEmptyStore = MasterStore.empty
        actual["MasterStore.nameEmptyError"] = nameEmptyStore.addCollection(named: "   ", copyingCurrent: false)
        var duplicateStore = MasterStore(activeCollection: "X", collections: ["X": Collection()])
        actual["MasterStore.duplicateCollectionNameError"] = duplicateStore.addCollection(named: "X", copyingCurrent: false)
        var deleteLastStore = MasterStore.empty
        actual["MasterStore.deleteLastCollectionError"] = deleteLastStore.deleteActiveCollection()
        var unknownCollectionStore = MasterStore.empty
        actual["MasterStore.unknownCollectionError"] = unknownCollectionStore.switchCollection(to: "X")

        // MARK: Notifications / PopoverModel

        actual["Notifications.restartToastButton"] = Notifications.restartToastButton
        actual["Notifications.title"] = Notifications.title
        actual["PopoverModel.addDisabledTooltip"] = PopoverModel.addDisabledTooltip
        actual["PopoverModel.addTooltip"] = PopoverModel.addTooltip
        actual["PopoverModel.chooseFolderButton"] = PopoverModel.chooseFolderButton
        actual["PopoverModel.emptyText"] = PopoverModel.emptyText
        actual["PopoverModel.exportTitleFor"] = PopoverModel.exportTitleFor("X")
        actual["PopoverModel.importTitle"] = PopoverModel.importTitle
        actual["PopoverModel.locateButton"] = PopoverModel.locateButton("X")
        actual["PopoverModel.manageTitle"] = PopoverModel.manageTitle
        actual["PopoverModel.pendingMenuMark"] = PopoverModel.pendingMenuMark
        actual["PopoverModel.quitTooltip"] = PopoverModel.quitTooltip
        actual["PopoverModel.restartTitle"] = PopoverModel.restartTitle
        actual["PopoverModel.retryTitle"] = PopoverModel.retryTitle
        actual["PopoverModel.reviewAndApplyButton"] = PopoverModel.reviewAndApplyButton
        actual["PopoverModel.settingsTooltip"] = PopoverModel.settingsTooltip
        actual["PopoverModel.sourceTooltipFormat"] = PopoverModel.sourceTooltipFormat("X")
        actual["PopoverModel.title"] = PopoverModel.title

        // MARK: PublishModel

        actual["PublishModel.cancelButton"] = PublishModel.cancelButton
        actual["PublishModel.chooseFolderButton"] = PublishModel.chooseFolderButton
        actual["PublishModel.envSectionTitle"] = PublishModel.envSectionTitle
        actual["PublishModel.exportButton"] = PublishModel.exportButton
        actual["PublishModel.exportTitle"] = PublishModel.exportTitle("X")
        actual["PublishModel.folderLine"] = PublishModel.folderLine("X")
        actual["PublishModel.footerLine"] = PublishModel.footerLine("X", "Y")
        actual["PublishModel.forgetMarkButton"] = PublishModel.forgetMarkButton
        actual["PublishModel.hintPlaceholder"] = PublishModel.hintPlaceholder
        actual["PublishModel.markPathLabel"] = PublishModel.markPathLabel
        actual["PublishModel.pathNamePlaceholder"] = PublishModel.pathNamePlaceholder
        actual["PublishModel.pathsSectionTitle"] = PublishModel.pathsSectionTitle
        actual["PublishModel.previewTitle"] = PublishModel.previewTitle
        actual["PublishModel.publishButton"] = PublishModel.publishButton
        actual["PublishModel.shareValueLabel"] = PublishModel.shareValueLabel
        actual["PublishModel.title"] = PublishModel.title("X")
        actual["PublishModel.keptPathNote"] = PublishModel.keptPathNote("X", "Y")
        actual["PublishModel.releaseValueButton"] = PublishModel.releaseValueButton
        actual["PublishModel.publishFolderNote"] = PublishModel.publishFolderNote("X", "Y")
        actual["PublishModel.publishFolderEditNote"] = PublishModel.publishFolderEditNote("X", "Y")
        actual["PublishModel.otherFolderNote"] = PublishModel.otherFolderNote("X", "Y", "Z")
        actual["PublishModel.useDirectoryTokenButton"] = PublishModel.useDirectoryTokenButton
        actual["PublishModel.unresolvedMarkNote"] = PublishModel.unresolvedMarkNote("X", "Y")
        actual["PublishModel.warningLine"] = PublishModel.warningLine("X", "Y")

        // MARK: RemoteAuthKind

        actual["RemoteAuthKind.automatic.title"] = RemoteAuthKind.automatic.title
        actual["RemoteAuthKind.bearer.title"] = RemoteAuthKind.bearer.title
        actual["RemoteAuthKind.header.title"] = RemoteAuthKind.header.title
        actual["RemoteAuthKind.oauthClient.title"] = RemoteAuthKind.oauthClient.title

        // MARK: RestoreModel

        actual["RestoreModel.cancelTitle"] = RestoreModel.cancelTitle
        actual["RestoreModel.caption"] = RestoreModel.caption
        actual["RestoreModel.confirmMessage"] = RestoreModel.confirmMessage(fileName: "X")
        actual["RestoreModel.headline"] = RestoreModel.headline
        actual["RestoreModel.restoreButton"] = RestoreModel.restoreButton
        actual["RestoreModel.restoreTitle"] = RestoreModel.restoreTitle

        // MARK: ReviewModel

        actual["ReviewModel.addedLabel"] = ReviewModel.addedLabel
        actual["ReviewModel.applyButton"] = ReviewModel.applyButton
        actual["ReviewModel.cancelButton"] = ReviewModel.cancelButton
        actual["ReviewModel.changedLabel"] = ReviewModel.changedLabel
        actual["ReviewModel.refreshButton"] = ReviewModel.refreshButton
        actual["ReviewModel.removedLabel"] = ReviewModel.removedLabel
        actual["ReviewModel.sourceMovedMessage"] = ReviewModel.sourceMovedMessage
        actual["ReviewModel.title"] = ReviewModel.title("X")

        // MARK: SettingsModel

        actual["SettingsModel.autoUpdateTitle"] = SettingsModel.autoUpdateTitle
        actual["SettingsModel.backupsCaption"] = SettingsModel.backupsCaption
        actual["SettingsModel.backupsHeader"] = SettingsModel.backupsHeader
        actual["SettingsModel.checkForUpdatesTitle"] = SettingsModel.checkForUpdatesTitle
        actual["SettingsModel.chooseTitle"] = SettingsModel.chooseTitle
        actual["SettingsModel.claudeAppHeader"] = SettingsModel.claudeAppHeader
        actual["SettingsModel.claudeAppRejectedTitle"] = SettingsModel.claudeAppRejectedTitle
        actual["SettingsModel.claudeTab"] = SettingsModel.claudeTab
        actual["SettingsModel.confirmQuitTitle"] = SettingsModel.confirmQuitTitle
        actual["SettingsModel.confirmRestartTitle"] = SettingsModel.confirmRestartTitle
        actual["SettingsModel.generalTab"] = SettingsModel.generalTab
        actual["SettingsModel.keepCountLabel"] = SettingsModel.keepCountLabel(3)
        actual["SettingsModel.launchAtLoginTitle"] = SettingsModel.launchAtLoginTitle
        actual["SettingsModel.loginItemApprovalNote"] = SettingsModel.loginItemApprovalNote
        actual["SettingsModel.loginItemFailureNote"] = SettingsModel.loginItemFailureNote("X")
        actual["SettingsModel.masterListHeader"] = SettingsModel.masterListHeader
        actual["SettingsModel.notifyCaption"] = SettingsModel.notifyCaption
        actual["SettingsModel.notifyTitle"] = SettingsModel.notifyTitle
        actual["SettingsModel.restoreTitle"] = SettingsModel.restoreTitle
        actual["SettingsModel.revealInFinderTitle"] = SettingsModel.revealInFinderTitle
        actual["SettingsModel.storageTab"] = SettingsModel.storageTab
        actual["SettingsModel.toolsCaption"] = SettingsModel.toolsCaption
        actual["SettingsModel.toolsHeader"] = SettingsModel.toolsHeader
        actual["SettingsModel.updatesHeader"] = SettingsModel.updatesHeader
        actual["SettingsModel.useDefaultTitle"] = SettingsModel.useDefaultTitle
        actual["SettingsModel.versionText"] = SettingsModel.versionText("X")

        // MARK: ToolFamily / ToolNote

        actual["ToolFamily.nodeJS.installCommand"] = ToolFamily.nodeJS.installCommand
        actual["ToolFamily.nodeJS.linkTitle"] = ToolFamily.nodeJS.linkTitle
        actual["ToolFamily.nodeJS.linkURL"] = ToolFamily.nodeJS.linkURL.absoluteString
        actual["ToolFamily.uv.installCommand"] = ToolFamily.uv.installCommand
        actual["ToolFamily.uv.linkTitle"] = ToolFamily.uv.linkTitle
        actual["ToolFamily.uv.linkURL"] = ToolFamily.uv.linkURL.absoluteString
        actual["ToolNote.checkingText"] = ToolNote.checkingText
        actual["ToolNote.foundText"] = ToolNote.foundText
        // ToolNote.missingText(_:) itself is internal (an implementation detail of
        // .make); .make's .text for .notFound is the same text, publicly reachable.
        actual["ToolNote.missingText"] = ToolNote.make(tool: .npx, status: .notFound)!.text
        actual["ToolNote.notFoundText"] = ToolNote.notFoundText
        actual["ToolNote.orRun"] = ToolNote.orRun
        actual["ToolNote.rowMissingText"] = ToolNote.rowMissingText(.npx)
        actual["ToolNote.rowShellOnlyText"] = ToolNote.rowShellOnlyText(.npx)
        actual["ToolNote.settingsCaption"] = ToolNote.settingsCaption
        actual["ToolNote.settingsHeader"] = ToolNote.settingsHeader
        actual["ToolNote.shellOnlyAdvice"] = ToolNote.shellOnlyAdvice
        actual["ToolNote.shellOnlyStatusText"] = ToolNote.shellOnlyStatusText
        actual["ToolNote.shellOnlyText"] = ToolNote.shellOnlyText(.npx, path: "X")

        // MARK: - Compare every key against the fixture's Mac side

        var macKeys: Set<String> = []
        for (key, entry) in fixture {
            guard let resolution = Self.macValue(entry) else { continue }
            macKeys.insert(key)
            let expected: String
            switch resolution {
            case .plain(let s):
                expected = s
            case .format(let format):
                expected = Self.expand(format, Self.argsByKey[key] ?? [])
            }
            XCTAssertEqual(actual[key], expected, key)
        }

        // An unpaired key — present in `actual` but missing (or Windows-only)
        // in the fixture, or vice versa — fails here on whichever side lacks it.
        XCTAssertEqual(Set(actual.keys), macKeys)
    }

    /// The comparison above only ever looks at keys `macValue` resolves — a fixture entry
    /// resolving on neither platform (both `mac` and `win` omitted, or an unrecognized value
    /// shape) is silently skipped here AND by the C# mirror's equivalent loop, so such a mistake
    /// would be asserted nowhere. This pins the fixture-wide invariant directly: every root key
    /// must resolve on at least one platform.
    func testEveryKeyResolvesOnAtLeastOnePlatform() throws {
        let fixture = try Self.loadFixture()
        for (key, entry) in fixture {
            let resolves: Bool
            switch entry {
            case .string:
                resolves = true   // a bare literal, shared verbatim by both platforms
            case .object(let obj):
                if case .string? = obj["format"] {
                    resolves = true   // a shared template, shared verbatim by both platforms
                } else {
                    resolves = Self.isResolved(obj["mac"]) || Self.isResolved(obj["win"])
                }
            default:
                resolves = false
            }
            XCTAssertTrue(resolves, "\(key) resolves on neither platform")
        }
    }

    /// True when `value` is present and not JSON `null`. A `"mac"`/`"win"`
    /// key explicitly set to `null` is present but unresolved and must not
    /// count as resolving — matching StringCatalogTests.cs's `mac is not
    /// null` check on `JsonNode`, where a JSON null parses to a null node
    /// (and every other JSON type — string, nested `format` object, etc. —
    /// parses to a non-null one).
    private static func isResolved(_ value: JSONValue?) -> Bool {
        guard let value else { return false }
        if case .null = value { return false }
        return true
    }

    /// The fixture has no `null` platform value today, so the rule above is
    /// pinned here rather than by the fixture walk.
    func testANullPlatformValueDoesNotResolve() {
        XCTAssertFalse(Self.isResolved(nil))
        XCTAssertFalse(Self.isResolved(.null))
        XCTAssertTrue(Self.isResolved(.string("Quit")))
        XCTAssertTrue(Self.isResolved(.object(["format": .string("{0} left")])))
    }
}
