import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState
@testable import ConnectorControlTransport

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/StringCatalogTests.cs
///
/// Guards every user-facing string this app and the Windows port are meant to
/// carry byte-for-byte, against the shared fixture both suites read:
/// `Tests/Fixtures/strings.json`. A wording change on either side that the
/// other side does not also make fails exactly one of the two suites.
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
        "AppState.ingestedElsewhereSentence": ["X", "Y", "Z"],
        "AppState.keptPathCarriedError": ["X", "Y"],
        "AppState.malformedConfigMessage": ["X"],
        "AppState.needsValueCaution": ["X"],
        "AppState.newCredentialError": ["X"],
        "AppState.pathMarkMovedError": ["X"],
        "AppState.publishFolderCarriedError": ["X", "Y"],
        "AppState.publishSlugTakenError": ["X"],
        "AppState.restoreCollectionGoneError": ["X"],
        "AppState.restoreSubscribedError": ["X"],
        "AppState.sourceUnreadableError": ["X", "Y"],
        "AppState.unreviewedConnectorError": ["X"],
        "ClaudeSignature.notFoundMessage": ["X"],
        "ClaudeSignature.refusalMessage": ["X", "Y"],
        "ClaudeSignature.uninspectableMessage": ["X"],
        "CollectionsModel.connectorTally.many": ["3"],
        "CollectionsModel.connectorTally.one": ["1"],
        "CollectionsModel.deleteCheckedMessage.many": ["3"],
        "CollectionsModel.deleteCheckedMessage.one": ["X"],
        "CollectionsModel.deleteConnectorsSentence.many": ["3"],
        "CollectionsModel.deleteConnectorsSentence.one": ["1"],
        "CollectionsModel.deleteNextActiveSentence": ["X"],
        "CollectionsModel.deletePublishedFileQuestion": ["X"],
        "CollectionsModel.editLabel": ["X"],
        "CollectionsModel.selectedCount": ["3"],
        "CollectionsModel.stopSyncingMessage": ["X"],
        "CommandLineToolText.installFailedError": ["X"],
        "CommandLineToolText.installedInText": ["X"],
        "CommandLineToolText.missingToolError": ["X"],
        "CommandLineToolText.occupiedError": ["X"],
        "CommandLineToolText.removeFailedError": ["X"],
        "CommandLineToolText.scriptsCaption": ["X"],
        "ConfigService.corruptStoreNote": ["X"],
        "ConfigService.corruptStoreRestoredNote": ["X", "Y"],
        "ConfigService.invalidBackupError": ["X", "Y"],
        "ConfigService.invalidBackupMcpServersError": ["X"],
        "ControlCaller.callingItselfText": ["X"],
        "ControlCommandLine.missingValueError": ["X"],
        "ControlCommandLine.unknownCommandError": ["X"],
        "ControlCommandLine.unknownOptionError": ["X"],
        "ControlCommandLine.usageError": ["X"],
        "ControlCommandLine.versionLine": ["X"],
        "ControlMasking.assignment": ["X", "Y"],
        "ControlMasking.oauthClientText": ["X", "Y"],
        "ControlMasking.remoteBridgeType": ["X"],
        "ControlTools.addActiveMessage": ["X", "Y"],
        "ControlTools.addMessage": ["X", "Y"],
        "ControlTools.addTitle": ["X", "Y"],
        "ControlTools.addedNotification": ["X", "Y", "Z"],
        "ControlTools.addedText": ["X", "Y", "Z"],
        "ControlTools.addsClause": ["X"],
        "ControlTools.appliedUpdateNotification": ["X", "Y"],
        "ControlTools.appliedUpdateText": ["X"],
        "ControlTools.applyUpdateTitle": ["X", "Y"],
        "ControlTools.arrivedOffText.many": ["3"],
        "ControlTools.arrivedOffText.one": ["1"],
        "ControlTools.changeActiveMessage": ["X"],
        "ControlTools.changeMessage": ["X"],
        "ControlTools.changeTitle": ["X", "Y"],
        "ControlTools.changedNotification": ["X", "Y", "Z"],
        "ControlTools.changedText": ["X", "Y"],
        "ControlTools.changesClause": ["X"],
        "ControlTools.connectorCountText": ["3", "7"],
        "ControlTools.createdText": ["X"],
        "ControlTools.deleteActiveMessage": ["X"],
        "ControlTools.deleteCollectionTitle": ["X", "Y"],
        "ControlTools.deleteMessage": ["X"],
        "ControlTools.deleteTitle": ["X", "Y"],
        "ControlTools.deletedCollectionNotification": ["X", "Y"],
        "ControlTools.deletedCollectionText": ["X"],
        "ControlTools.deletedNotification": ["X", "Y", "Z"],
        "ControlTools.deletedText": ["X", "Y"],
        "ControlTools.deletesClause": ["X"],
        "ControlTools.detail": ["X", "Y"],
        "ControlTools.documentTooLargeError": ["X"],
        "ControlTools.duplicateSubscribedError": ["X"],
        "ControlTools.duplicatedText": ["X", "Y"],
        "ControlTools.emptyCollectionText": ["X"],
        "ControlTools.fromFooter": ["X"],
        "ControlTools.importMessage.many": ["3", "X"],
        "ControlTools.importMessage.one": ["1", "X"],
        "ControlTools.importNewMessage.many": ["3", "X"],
        "ControlTools.importNewMessage.one": ["1", "X"],
        "ControlTools.importReplacingMessage.many": ["3", "X"],
        "ControlTools.importReplacingMessage.one": ["1", "X"],
        "ControlTools.importTitle": ["X", "Y"],
        "ControlTools.importedNotification.many": ["X", "3", "Y"],
        "ControlTools.importedNotification.one": ["X", "1", "Y"],
        "ControlTools.importedReplacingText.many": ["3", "X"],
        "ControlTools.importedReplacingText.one": ["1", "X"],
        "ControlTools.importedText.many": ["3", "X"],
        "ControlTools.importedText.one": ["1", "X"],
        "ControlTools.installHint": ["X", "Y"],
        "ControlTools.integrationSetText": ["X"],
        "ControlTools.missingArgumentError": ["X"],
        "ControlTools.needsNewerAppError": ["X"],
        "ControlTools.needsValuesText": ["X"],
        "ControlTools.noBackupError": ["X"],
        "ControlTools.noConnectorError": ["X", "Y"],
        "ControlTools.noUpdateError": ["X"],
        "ControlTools.notAFileError": ["X"],
        "ControlTools.notOneOfError": ["X", "Y"],
        "ControlTools.notSubscribedError": ["X"],
        "ControlTools.nothingToImportError": ["X"],
        "ControlTools.pendingUpdateText": ["X", "Y"],
        "ControlTools.renamedCollectionText": ["X", "Y"],
        "ControlTools.renamedNotification": ["X", "Y", "Z"],
        "ControlTools.renamedText": ["X", "Y"],
        "ControlTools.restartTitle": ["X"],
        "ControlTools.restoreActivatingText": ["X"],
        "ControlTools.restoreIntoText": ["X"],
        "ControlTools.restoreTitle": ["X", "Y"],
        "ControlTools.restoredNotification": ["X", "Y"],
        "ControlTools.restoredText": ["X"],
        "ControlTools.savedNotAppliedText": ["X"],
        "ControlTools.settingChangedText": ["X", "Y"],
        "ControlTools.settingNotForClaudeError": ["X"],
        "ControlTools.statusTitle": ["X"],
        "ControlTools.subscribeMessage.many": ["X", "3"],
        "ControlTools.subscribeMessage.one": ["X", "1"],
        "ControlTools.subscribeTitle": ["X", "Y"],
        "ControlTools.subscribedNotification": ["X", "Y", "Z"],
        "ControlTools.subscribedText": ["X", "Y"],
        "ControlTools.switchValueError": ["X"],
        "ControlTools.switchedNotification": ["X", "Y"],
        "ControlTools.switchedText": ["X"],
        "ControlTools.turnedOffNotification": ["X", "Y"],
        "ControlTools.turnedOffText": ["X", "Y"],
        "ControlTools.turnedOnNotification": ["X", "Y"],
        "ControlTools.turnedOnText": ["X", "Y"],
        "ControlTools.unchangedError": ["X"],
        "ControlTools.unknownToolError": ["X"],
        "ControlTools.upToDateText": ["X"],
        "ControlTools.wrongTypeError": ["X", "Y"],
        "CopyModel.title": ["X"],
        "EditTarget.editTitle": ["X"],
        "EditorModel.additionalTitle": ["3", "a, b"],
        // Called with the row's index, 2: the text counts from one.
        "EditorModel.argumentLabel": ["3"],
        "EditorModel.argumentNumber": ["3"],
        "EditorModel.changedOutsideMessage": ["X"],
        "EditorModel.deletedOutsideMessage": ["X"],
        "EditorModel.duplicateEnvError": ["X"],
        "EditorModel.importedNote": ["X", "Y"],
        "EditorModel.lockedFieldsNote": ["X"],
        "EditorModel.propagateLabel": ["X", "Y"],
        "EditorModel.propagateLabelMany": ["X", "Y"],
        "EditorModel.publishedNote": ["X"],
        "FieldName.argument": ["1"],
        "FieldName.document": ["X"],
        "FieldName.envValue": ["X"],
        "FieldName.hint": ["X"],
        "ImportModel.addModeTitle": ["X"],
        "ImportModel.collisionPickerLabel": ["X"],
        "ImportModel.importButton": ["3"],
        "ImportModel.includeLabel": ["X"],
        "ImportModel.skippedBadge": ["X"],
        "ImportModel.sourceLine": ["X", "Y", "3"],
        "MasterStore.duplicateCollectionNameError": ["X"],
        "MasterStore.unknownCollectionError": ["X"],
        "PopoverModel.emptyText": ["X"],
        "PopoverModel.locateButton": ["X"],
        "PopoverModel.sourceTooltipFormat": ["X"],
        "PublishModel.exportTitle": ["X"],
        "PublishModel.folderLine": ["X"],
        "PublishModel.footerLine": ["X", "Y"],
        "PublishModel.keptPathNote": ["X", "Y"],
        "PublishModel.otherFolderNote": ["X", "Y", "Z"],
        "PublishModel.publishFolderEditNote": ["X", "Y"],
        "PublishModel.publishFolderNote": ["X", "Y"],
        "PublishModel.title": ["X"],
        "PublishModel.unresolvedMarkNote": ["X", "Y"],
        "PublishModel.warningLine": ["X", "Y"],
        "RestoreModel.confirmMessage": ["X"],
        "ReviewModel.title": ["X"],
        "SettingsModel.loginItemFailureNote": ["X"],
        "SettingsModel.versionText": ["X"],
        "ToolFooterLine.installText": ["X", "Y", "Z"],
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
        actual["AppState.deleteCollectionMessage"] = AppState.deleteCollectionMessage("X")
        actual["AppState.duplicateNameError"] = AppState.duplicateNameError("X")
        actual["AppState.enabledSubtitle"] = AppState.enabledSubtitle(enabled: 3, total: 7)
        actual["AppState.ingestedElsewhereSentence"] = AppState.ingestedElsewhereSentence("X", "Y", "Z")
        actual["AppState.lastLocalCollectionError"] = AppState.lastLocalCollectionError
        actual["AppState.locateCaution"] = AppState.locateCaution
        actual["AppState.malformedConfigMessage"] = AppState.malformedConfigMessage(detail: "X")
        actual["AppState.nameEmptyError"] = AppState.nameEmptyError
        actual["AppState.needsValueCaution"] = AppState.needsValueCaution("X")
        actual["AppState.newCollectionTitle"] = AppState.newCollectionTitle
        actual["AppState.newCredentialError"] = AppState.newCredentialError("X")
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
        actual["AppState.restoreSubscribedError"] = AppState.restoreSubscribedError("X")
        actual["AppState.sourceUnreadableError"] = AppState.sourceUnreadableError("X", "Y")
        actual["AppState.storeChangedBody"] = AppState.storeChangedBody
        actual["AppState.targetMustBeLocalError"] = AppState.targetMustBeLocalError
        actual["AppState.unpublishedDirectoryCaution"] = AppState.unpublishedDirectoryCaution
        actual["AppState.unreviewedConnectorError"] = AppState.unreviewedConnectorError("X")
        actual["AppVersion.developmentBuild"] = AppVersion.developmentBuild

        // MARK: BoundedRead

        actual["BoundedRead.notAFileError"] = BoundedRead.notAFileError
        actual["BoundedRead.tooLargeError"] = BoundedRead.tooLargeError

        // MARK: ClaudeConfigIO
        // Both messages are inline literals inside guard/throw statements, not
        // exposed as callable factories; pinned here to match ClaudeConfigIO.swift
        // and cross-checked against ClaudeConfigIOTests.swift's real-parse assertions.

        actual["ClaudeConfigIO.mcpServersNotObjectError"] = "mcpServers is not a JSON object"
        actual["ClaudeConfigIO.repeatedKeyError"] = ClaudeConfigIO.repeatedKeyError
        actual["ClaudeConfigIO.topLevelNotObjectError"] = "top level is not a JSON object"

        // MARK: ClaudeIntegration

        actual["ClaudeIntegration.clashNote"] = ClaudeIntegration.clashNote
        actual["ClaudeIntegration.endpointUnavailableText"] = ClaudeIntegration.endpointUnavailableText
        actual["ClaudeIntegration.moveToApplicationsError"] = ClaudeIntegration.moveToApplicationsError
        actual["ClaudeIntegration.offerBody"] = ClaudeIntegration.offerBody
        actual["ClaudeIntegration.offerNotNowButton"] = ClaudeIntegration.offerNotNowButton
        actual["ClaudeIntegration.offerTitle"] = ClaudeIntegration.offerTitle
        actual["ClaudeIntegration.offerTurnOnButton"] = ClaudeIntegration.offerTurnOnButton
        actual["ClaudeIntegration.reservedNameError"] = ClaudeIntegration.reservedNameError

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
            "The MCP list file was unreadable; it was preserved as X and rebuilt from Claude’s config."
        actual["ConfigService.corruptStoreRestoredNote"] =
            "The MCP list file was unreadable; it was preserved as X and restored from the backup of Y."
        actual["ConfigService.invalidBackupError"] = "backup X is not a valid config file (Y)"
        actual["ConfigService.invalidBackupMcpServersError"] = "backup X has an invalid mcpServers section"
        actual["ConfigService.malformedClaudeConfigNote"] =
            "Claude’s config file is not valid JSON. Your MCP list is safe; use Backups \u{25B8} Restore to repair the file."

        // MARK: CollectionDocument

        actual["CollectionDocument.credentialLiteralPhrase"] = CollectionDocument.credentialLiteralPhrase
        actual["CollectionDocument.credentialReferencePhrase"] = CollectionDocument.credentialReferencePhrase
        actual["CollectionDocument.credentialUserOnlyPhrase"] = CollectionDocument.credentialUserOnlyPhrase

        // MARK: CollectionsModel

        actual["CollectionsModel.activePill"] = CollectionsModel.activePill
        actual["CollectionsModel.addCollectionTooltip"] = CollectionsModel.addCollectionTooltip
        actual["CollectionsModel.addConnectorDisabledTooltip"] = CollectionsModel.addConnectorDisabledTooltip
        actual["CollectionsModel.addConnectorTooltip"] = CollectionsModel.addConnectorTooltip
        actual["CollectionsModel.connectorTally.many"] = CollectionsModel.connectorTally(3)
        actual["CollectionsModel.connectorTally.one"] = CollectionsModel.connectorTally(1)
        actual["CollectionsModel.copyToButton"] = CollectionsModel.copyToButton
        actual["CollectionsModel.deleteAction"] = CollectionsModel.deleteAction
        actual["CollectionsModel.deleteConnectorsSentence.many"] = CollectionsModel.deleteConnectorsSentence(3)
        actual["CollectionsModel.deleteConnectorsSentence.none"] = CollectionsModel.deleteConnectorsSentence(0)
        actual["CollectionsModel.deleteConnectorsSentence.one"] = CollectionsModel.deleteConnectorsSentence(1)
        actual["CollectionsModel.deleteCopiesSentence"] = CollectionsModel.deleteCopiesSentence
        actual["CollectionsModel.deleteNextActiveSentence"] = CollectionsModel.deleteNextActiveSentence("X")
        actual["CollectionsModel.deletePublishedFileQuestion"] = CollectionsModel.deletePublishedFileQuestion("X")
        actual["CollectionsModel.deleteSourceSentence"] = CollectionsModel.deleteSourceSentence
        actual["CollectionsModel.duplicateAction"] = CollectionsModel.duplicateAction
        actual["CollectionsModel.editLabel"] = CollectionsModel.editLabel(for: "X")
        actual["CollectionsModel.exportAllAction"] = CollectionsModel.exportAllAction
        actual["CollectionsModel.exportCheckedButton"] = CollectionsModel.exportCheckedButton
        actual["CollectionsModel.importButton"] = CollectionsModel.importButton
        actual["CollectionsModel.importSubtitle"] = CollectionsModel.importSubtitle
        actual["CollectionsModel.keepFileButton"] = CollectionsModel.keepFileButton
        actual["CollectionsModel.lockedGlyphTooltip"] = CollectionsModel.lockedGlyphTooltip
        actual["CollectionsModel.makeActiveAction"] = CollectionsModel.makeActiveAction
        actual["CollectionsModel.makeLocalCopyButton"] = CollectionsModel.makeLocalCopyButton
        actual["CollectionsModel.moreActionsLabel"] = CollectionsModel.moreActionsLabel
        actual["CollectionsModel.newButton"] = CollectionsModel.newButton
        actual["CollectionsModel.publishButton"] = CollectionsModel.publishButton
        actual["CollectionsModel.publishSettingsButton"] = CollectionsModel.publishSettingsButton
        actual["CollectionsModel.publishedPill"] = CollectionsModel.publishedPill
        actual["CollectionsModel.readOnlyNote"] = CollectionsModel.readOnlyNote
        actual["CollectionsModel.refreshButton"] = CollectionsModel.refreshButton
        actual["CollectionsModel.remoteType"] = CollectionsModel.remoteType
        actual["CollectionsModel.deleteCheckedButton"] = CollectionsModel.deleteCheckedButton
        actual["CollectionsModel.deleteCheckedInformative"] = CollectionsModel.deleteCheckedInformative
        actual["CollectionsModel.deleteCheckedMessage.many"] = CollectionsModel.deleteCheckedMessage(["X", "Y", "Z"])
        actual["CollectionsModel.deleteCheckedMessage.one"] = CollectionsModel.deleteCheckedMessage(["X"])
        actual["CollectionsModel.deleteFileButton"] = CollectionsModel.deleteFileButton
        actual["CollectionsModel.renameAction"] = CollectionsModel.renameAction
        actual["CollectionsModel.selectedCount"] = CollectionsModel.selectedCount(3)
        actual["CollectionsModel.showPublishedFileAction"] = CollectionsModel.showPublishedFileAction
        actual["CollectionsModel.showSourceFileAction"] = CollectionsModel.showSourceFileAction
        actual["CollectionsModel.stopPublishingAction"] = CollectionsModel.stopPublishingAction
        actual["CollectionsModel.stopSyncingAction"] = CollectionsModel.stopSyncingAction
        actual["CollectionsModel.stopSyncingInformative"] = CollectionsModel.stopSyncingInformative
        actual["CollectionsModel.stopSyncingMessage"] = CollectionsModel.stopSyncingMessage("X")
        actual["CollectionsModel.subscribeButton"] = CollectionsModel.subscribeButton
        actual["CollectionsModel.subscribeSubtitle"] = CollectionsModel.subscribeSubtitle
        actual["CollectionsModel.subscribedPill"] = CollectionsModel.subscribedPill
        actual["CollectionsModel.updateAvailableStatus"] = CollectionsModel.updateAvailableStatus
        actual["CollectionsModel.windowTitle"] = CollectionsModel.windowTitle

        // MARK: CommandLineToolText

        actual["CommandLineToolText.adminPrompt"] = CommandLineToolText.adminPrompt
        actual["CommandLineToolText.helpCommand"] = CommandLineToolText.helpCommand
        actual["CommandLineToolText.inPathText"] = CommandLineToolText.inPathText
        actual["CommandLineToolText.installButton"] = CommandLineToolText.installButton
        actual["CommandLineToolText.installFailedError"] = CommandLineToolText.installFailedError("X")
        actual["CommandLineToolText.installedInText"] = CommandLineToolText.installedInText("X")
        actual["CommandLineToolText.missingToolError"] = CommandLineToolText.missingToolError("X")
        actual["CommandLineToolText.notInstalledText"] = CommandLineToolText.notInstalledText
        actual["CommandLineToolText.occupiedError"] = CommandLineToolText.occupiedError("X")
        actual["CommandLineToolText.removeButton"] = CommandLineToolText.removeButton
        actual["CommandLineToolText.removeFailedError"] = CommandLineToolText.removeFailedError("X")
        actual["CommandLineToolText.runHelpCaption"] = CommandLineToolText.runHelpCaption
        actual["CommandLineToolText.scriptsCaption"] = CommandLineToolText.scriptsCaption("X")

        // MARK: ControlMasking / ControlText

        actual["ControlClient.droppedError"] = ControlClient.droppedError
        actual["ControlClient.elevatedError"] = ControlClient.elevatedError
        actual["ControlClient.notRunningError"] = ControlClient.notRunningError
        actual["ControlClient.startingText"] = ControlClient.startingText
        actual["ControlClient.unreachableError"] = ControlClient.unreachableError
        actual["ControlCommandLine.missingValueError"] = ControlCommandLine.missingValueError("X")
        actual["ControlCommandLine.unknownCommandError"] = ControlCommandLine.unknownCommandError("X")
        actual["ControlCommandLine.unknownOptionError"] = ControlCommandLine.unknownOptionError("X")
        actual["ControlCommandLine.usageError"] = ControlCommandLine.usageError("X")
        actual["ControlCommandLine.versionLine"] = ControlCommandLine.versionLine("X")
        actual["ControlMasking.addedPrefix"] = ControlMasking.addedPrefix
        actual["ControlMasking.argsLabel"] = ControlMasking.argsLabel
        actual["ControlMasking.assignment"] = ControlMasking.assignment("X", "Y")
        actual["ControlMasking.commandLabel"] = ControlMasking.commandLabel
        actual["ControlMasking.envLabel"] = ControlMasking.envLabel
        actual["ControlMasking.headersLabel"] = ControlMasking.headersLabel
        actual["ControlMasking.mask"] = ControlMasking.mask
        actual["ControlMasking.nameLabel"] = ControlMasking.nameLabel
        actual["ControlMasking.needsValueState"] = ControlMasking.needsValueState
        actual["ControlMasking.oauthClientText"] = ControlMasking.oauthClientText("X", "Y")
        actual["ControlMasking.oauthLabel"] = ControlMasking.oauthLabel
        actual["ControlMasking.otherLabel"] = ControlMasking.otherLabel
        actual["ControlMasking.remoteBridgeType"] = ControlMasking.remoteBridgeType("X")
        actual["ControlMasking.removedPrefix"] = ControlMasking.removedPrefix
        actual["ControlMasking.scopesLabel"] = ControlMasking.scopesLabel
        actual["ControlMasking.setState"] = ControlMasking.setState
        actual["ControlMasking.setText"] = ControlMasking.setText
        actual["ControlMasking.typeLabel"] = ControlMasking.typeLabel
        actual["ControlMasking.urlLabel"] = ControlMasking.urlLabel
        actual["ControlRelay.restartedError"] = ControlRelay.restartedError
        actual["ControlText.listAnd"] = ControlText.listAnd

        // MARK: ConfirmationDialog / TextRun

        actual["ConfirmationDialog.allowButton"] = ConfirmationDialog.allowButton
        actual["ConfirmationDialog.dontAllowButton"] = ConfirmationDialog.dontAllowButton
        actual["TextRun.cutMark"] = TextRun.cutMark

        // MARK: ControlCaller / ControlSettingKey / ControlTools

        actual["ControlCaller.callingItselfText"] = ControlCaller.callingItselfText("X")
        actual["ControlCaller.claudeName"] = ControlCaller.claudeName
        actual["ControlCaller.unknownClientName"] = ControlCaller.unknownClientName
        actual["ControlSettingKey.automaticUpdates"] = ControlSettingKey.automaticUpdates
        actual["ControlSettingKey.backupsToKeep"] = ControlSettingKey.backupsToKeep
        actual["ControlSettingKey.claudeIntegration"] = ControlSettingKey.claudeIntegration
        actual["ControlSettingKey.confirmClaudeChanges"] = ControlSettingKey.confirmClaudeChanges
        actual["ControlSettingKey.confirmQuit"] = ControlSettingKey.confirmQuit
        actual["ControlSettingKey.confirmRestart"] = ControlSettingKey.confirmRestart
        actual["ControlSettingKey.launchAtLogin"] = ControlSettingKey.launchAtLogin
        actual["ControlSettingKey.notifyExternalChanges"] = ControlSettingKey.notifyExternalChanges
        actual["ControlTools.activeCollectionLabel"] = ControlTools.activeCollectionLabel
        actual["ControlTools.activeMark"] = ControlTools.activeMark
        actual["ControlTools.addActiveMessage"] = ControlTools.addActiveMessage("X", "Y")
        actual["ControlTools.addMessage"] = ControlTools.addMessage("X", "Y")
        actual["ControlTools.addTitle"] = ControlTools.addTitle("X", "Y")
        actual["ControlTools.addedNotification"] = ControlTools.addedNotification("X", "Y", "Z")
        actual["ControlTools.addedText"] = ControlTools.addedText("X", "Y", "Z")
        actual["ControlTools.addsClause"] = ControlTools.addsClause("X")
        actual["ControlTools.appliedUpdateNotification"] = ControlTools.appliedUpdateNotification("X", "Y")
        actual["ControlTools.appliedUpdateText"] = ControlTools.appliedUpdateText("X")
        actual["ControlTools.applyUpdateTitle"] = ControlTools.applyUpdateTitle("X", "Y")
        actual["ControlTools.argumentsNotObjectError"] = ControlTools.argumentsNotObjectError
        actual["ControlTools.arrivedOffText.many"] = ControlTools.arrivedOffText(3)
        actual["ControlTools.arrivedOffText.one"] = ControlTools.arrivedOffText(1)
        actual["ControlTools.booleanKind"] = ControlTools.booleanKind
        actual["ControlTools.changeActiveMessage"] = ControlTools.changeActiveMessage("X")
        actual["ControlTools.changeMessage"] = ControlTools.changeMessage("X")
        actual["ControlTools.changeTitle"] = ControlTools.changeTitle("X", "Y")
        actual["ControlTools.changedNotification"] = ControlTools.changedNotification("X", "Y", "Z")
        actual["ControlTools.changedText"] = ControlTools.changedText("X", "Y")
        actual["ControlTools.changedWhileAskingError"] = ControlTools.changedWhileAskingError
        actual["ControlTools.changesClause"] = ControlTools.changesClause("X")
        actual["ControlTools.claudeGroup"] = ControlTools.claudeGroup
        actual["ControlTools.claudeLabel"] = ControlTools.claudeLabel
        actual["ControlTools.claudeNotRunning"] = ControlTools.claudeNotRunning
        actual["ControlTools.claudeRunning"] = ControlTools.claudeRunning
        actual["ControlTools.claudeRunningRestart"] = ControlTools.claudeRunningRestart
        actual["ControlTools.clauseSeparator"] = ControlTools.clauseSeparator
        actual["ControlTools.collectionHeader"] = ControlTools.collectionHeader
        actual["ControlTools.collectionsGroup"] = ControlTools.collectionsGroup
        actual["ControlTools.configShapeError"] = ControlTools.configShapeError
        actual["ControlTools.connectorCountText"] = ControlTools.connectorCountText(3, 7)
        actual["ControlTools.connectorsGroup"] = ControlTools.connectorsGroup
        actual["ControlTools.connectorsHeader"] = ControlTools.connectorsHeader
        actual["ControlTools.createdText"] = ControlTools.createdText("X")
        actual["ControlTools.declinedError"] = ControlTools.declinedError
        actual["ControlTools.deleteActiveMessage"] = ControlTools.deleteActiveMessage("X")
        actual["ControlTools.deleteCollectionTitle"] = ControlTools.deleteCollectionTitle("X", "Y")
        actual["ControlTools.deleteMessage"] = ControlTools.deleteMessage("X")
        actual["ControlTools.deleteTitle"] = ControlTools.deleteTitle("X", "Y")
        actual["ControlTools.deletedCollectionNotification"] = ControlTools.deletedCollectionNotification("X", "Y")
        actual["ControlTools.deletedCollectionText"] = ControlTools.deletedCollectionText("X")
        actual["ControlTools.deletedNotification"] = ControlTools.deletedNotification("X", "Y", "Z")
        actual["ControlTools.deletedText"] = ControlTools.deletedText("X", "Y")
        actual["ControlTools.deletesClause"] = ControlTools.deletesClause("X")
        actual["ControlTools.detail"] = ControlTools.detail("X", "Y")
        actual["ControlTools.documentTooLargeError"] = ControlTools.documentTooLargeError("X")
        actual["ControlTools.duplicateSubscribedError"] = ControlTools.duplicateSubscribedError("X")
        actual["ControlTools.duplicatedText"] = ControlTools.duplicatedText("X", "Y")
        actual["ControlTools.emptyCollectionText"] = ControlTools.emptyCollectionText("X")
        actual["ControlTools.fromFooter"] = ControlTools.fromFooter("X")
        actual["ControlTools.helpOptions"] = ControlTools.helpOptions
        actual["ControlTools.helpUsage"] = ControlTools.helpUsage
        actual["ControlTools.idHeader"] = ControlTools.idHeader
        actual["ControlTools.importMessage.many"] = ControlTools.importMessage(3, "X")
        actual["ControlTools.importMessage.one"] = ControlTools.importMessage(1, "X")
        actual["ControlTools.importNewMessage.many"] = ControlTools.importNewMessage(3, "X")
        actual["ControlTools.importNewMessage.one"] = ControlTools.importNewMessage(1, "X")
        actual["ControlTools.importReplacingMessage.many"] = ControlTools.importReplacingMessage(3, "X")
        actual["ControlTools.importReplacingMessage.one"] = ControlTools.importReplacingMessage(1, "X")
        actual["ControlTools.importTitle"] = ControlTools.importTitle("X", "Y")
        actual["ControlTools.importedNotification.many"] = ControlTools.importedNotification("X", 3, "Y")
        actual["ControlTools.importedNotification.one"] = ControlTools.importedNotification("X", 1, "Y")
        actual["ControlTools.importedReplacingText.many"] = ControlTools.importedReplacingText(3, "X")
        actual["ControlTools.importedReplacingText.one"] = ControlTools.importedReplacingText(1, "X")
        actual["ControlTools.importedText.many"] = ControlTools.importedText(3, "X")
        actual["ControlTools.importedText.one"] = ControlTools.importedText(1, "X")
        actual["ControlTools.installHint"] = ControlTools.installHint("X", "Y")
        actual["ControlTools.integerKind"] = ControlTools.integerKind
        actual["ControlTools.integrationLabel"] = ControlTools.integrationLabel
        actual["ControlTools.integrationOffError"] = ControlTools.integrationOffError
        actual["ControlTools.integrationOnConfirming"] = ControlTools.integrationOnConfirming
        actual["ControlTools.integrationOnNotConfirming"] = ControlTools.integrationOnNotConfirming
        actual["ControlTools.integrationSetText"] = ControlTools.integrationSetText("X")
        actual["ControlTools.intoOrNewCollectionError"] = ControlTools.intoOrNewCollectionError
        actual["ControlTools.keepCountError"] = ControlTools.keepCountError
        actual["ControlTools.kindHeader"] = ControlTools.kindHeader
        actual["ControlTools.listKind"] = ControlTools.listKind
        actual["ControlTools.localKind"] = ControlTools.localKind
        actual["ControlTools.masterListLabel"] = ControlTools.masterListLabel
        actual["ControlTools.missingArgumentError"] = ControlTools.missingArgumentError("X")
        actual["ControlTools.nameHeader"] = ControlTools.nameHeader
        actual["ControlTools.needsNewerAppError"] = ControlTools.needsNewerAppError("X")
        actual["ControlTools.needsValuesText"] = ControlTools.needsValuesText("X")
        actual["ControlTools.noBackupError"] = ControlTools.noBackupError("X")
        actual["ControlTools.noBackupsText"] = ControlTools.noBackupsText
        actual["ControlTools.noConnectorError"] = ControlTools.noConnectorError("X", "Y")
        actual["ControlTools.noQuestionError"] = ControlTools.noQuestionError
        actual["ControlTools.noUpdateError"] = ControlTools.noUpdateError("X")
        actual["ControlTools.notAFileError"] = ControlTools.notAFileError("X")
        actual["ControlTools.notOneOfError"] = ControlTools.notOneOfError("X", "Y")
        actual["ControlTools.notSubscribedError"] = ControlTools.notSubscribedError("X")
        actual["ControlTools.nothingToChangeError"] = ControlTools.nothingToChangeError
        actual["ControlTools.nothingToImportError"] = ControlTools.nothingToImportError("X")
        actual["ControlTools.objectKind"] = ControlTools.objectKind
        actual["ControlTools.offWord"] = ControlTools.offWord
        actual["ControlTools.onWord"] = ControlTools.onWord
        actual["ControlTools.pendingUpdateText"] = ControlTools.pendingUpdateText("X", "Y")
        actual["ControlTools.problemLabel"] = ControlTools.problemLabel
        actual["ControlTools.publishedFileStaysText"] = ControlTools.publishedFileStaysText
        actual["ControlTools.publishedKind"] = ControlTools.publishedKind
        actual["ControlTools.queuedText"] = ControlTools.queuedText
        actual["ControlTools.relaySummary"] = ControlTools.relaySummary
        actual["ControlTools.relayUsage"] = ControlTools.relayUsage
        actual["ControlTools.renamedCollectionText"] = ControlTools.renamedCollectionText("X", "Y")
        actual["ControlTools.renamedNotification"] = ControlTools.renamedNotification("X", "Y", "Z")
        actual["ControlTools.renamedText"] = ControlTools.renamedText("X", "Y")
        actual["ControlTools.restartItselfTitle"] = ControlTools.restartItselfTitle
        actual["ControlTools.restartMessage"] = ControlTools.restartMessage
        actual["ControlTools.restartTitle"] = ControlTools.restartTitle("X")
        actual["ControlTools.restartToApplyText"] = ControlTools.restartToApplyText
        actual["ControlTools.restartToUseText"] = ControlTools.restartToUseText
        actual["ControlTools.restartingText"] = ControlTools.restartingText
        actual["ControlTools.restoreActivatingText"] = ControlTools.restoreActivatingText("X")
        actual["ControlTools.restoreIntoText"] = ControlTools.restoreIntoText("X")
        actual["ControlTools.restoreTitle"] = ControlTools.restoreTitle("X", "Y")
        actual["ControlTools.restoredNotification"] = ControlTools.restoredNotification("X", "Y")
        actual["ControlTools.restoredText"] = ControlTools.restoredText("X")
        actual["ControlTools.runsHeader"] = ControlTools.runsHeader
        actual["ControlTools.savedNotAppliedText"] = ControlTools.savedNotAppliedText("X")
        actual["ControlTools.settingChangedText"] = ControlTools.settingChangedText("X", "Y")
        actual["ControlTools.settingNotForClaudeError"] = ControlTools.settingNotForClaudeError("X")
        actual["ControlTools.settingsGroup"] = ControlTools.settingsGroup
        actual["ControlTools.sharingGroup"] = ControlTools.sharingGroup
        actual["ControlTools.statusGroup"] = ControlTools.statusGroup
        actual["ControlTools.statusTitle"] = ControlTools.statusTitle("X")
        actual["ControlTools.subscribeMessage.many"] = ControlTools.subscribeMessage("X", 3)
        actual["ControlTools.subscribeMessage.one"] = ControlTools.subscribeMessage("X", 1)
        actual["ControlTools.subscribeTitle"] = ControlTools.subscribeTitle("X", "Y")
        actual["ControlTools.subscribedKind"] = ControlTools.subscribedKind
        actual["ControlTools.subscribedNotification"] = ControlTools.subscribedNotification("X", "Y", "Z")
        actual["ControlTools.subscribedText"] = ControlTools.subscribedText("X", "Y")
        actual["ControlTools.switchValueError"] = ControlTools.switchValueError("X")
        actual["ControlTools.switchedNotification"] = ControlTools.switchedNotification("X", "Y")
        actual["ControlTools.switchedText"] = ControlTools.switchedText("X")
        actual["ControlTools.takesEffectFooter"] = ControlTools.takesEffectFooter
        actual["ControlTools.textKind"] = ControlTools.textKind
        actual["ControlTools.tooLongToShowError"] = ControlTools.tooLongToShowError
        actual["ControlTools.turnedOffNotification"] = ControlTools.turnedOffNotification("X", "Y")
        actual["ControlTools.turnedOffText"] = ControlTools.turnedOffText("X", "Y")
        actual["ControlTools.turnedOnNotification"] = ControlTools.turnedOnNotification("X", "Y")
        actual["ControlTools.turnedOnText"] = ControlTools.turnedOnText("X", "Y")
        actual["ControlTools.unchangedError"] = ControlTools.unchangedError("X")
        actual["ControlTools.unknownToolError"] = ControlTools.unknownToolError("X")
        actual["ControlTools.upToDateText"] = ControlTools.upToDateText("X")
        actual["ControlTools.updateAvailableKind"] = ControlTools.updateAvailableKind
        actual["ControlTools.waitingText"] = ControlTools.waitingText
        actual["ControlTools.wrongTypeError"] = ControlTools.wrongTypeError("X", "Y")
        for tool in ControlTools.catalog {
            actual["ControlTools.\(tool.name).description"] = tool.description
            actual["ControlTools.\(tool.name).cliUsage"] = tool.cliUsage
        }
        for (name, title) in ControlTools.titles {
            actual["ControlTools.titles.\(name)"] = title
        }

        // MARK: CopyModel

        actual["CopyModel.copyButton"] = CopyModel.copyButton
        actual["CopyModel.title"] = CopyModel.title("X")

        // MARK: Dialogs / AlertDialogs

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
        actual["EditorModel.argumentLabel"] = EditorModel.argumentLabel(2)
        actual["EditorModel.argumentNumber"] = EditorModel.argumentNumber(2)
        actual["EditorModel.argumentPrompt"] = EditorModel.argumentPrompt
        actual["EditorModel.argumentsHeader"] = EditorModel.argumentsHeader
        actual["EditorModel.authenticationHeader"] = EditorModel.authenticationHeader
        actual["EditorModel.automaticCaption"] = EditorModel.automaticCaption
        actual["EditorModel.bearerCaption"] = EditorModel.bearerCaption
        actual["EditorModel.bearerTokenError"] = EditorModel.bearerTokenError
        actual["EditorModel.cancelButton"] = EditorModel.cancelButton
        actual["EditorModel.changedOutsideDetail"] = EditorModel.changedOutsideDetail
        actual["EditorModel.changedOutsideMessage"] = EditorModel.changedOutsideMessage("X")
        actual["EditorModel.clientIDError"] = EditorModel.clientIDError
        actual["EditorModel.clientIDLabel"] = EditorModel.clientIDLabel
        actual["EditorModel.clientSecretLabel"] = EditorModel.clientSecretLabel
        actual["EditorModel.commandError"] = EditorModel.commandError
        actual["EditorModel.commandLabel"] = EditorModel.commandLabel
        actual["EditorModel.commandPrompt"] = EditorModel.commandPrompt
        actual["EditorModel.duplicateEnvError"] = EditorModel.duplicateEnvError("X")
        actual["EditorModel.envNamelessError"] = EditorModel.envNamelessError
        actual["EditorModel.environmentHeader"] = EditorModel.environmentHeader
        actual["EditorModel.formTab"] = EditorModel.formTab
        actual["EditorModel.headerNameError"] = EditorModel.headerNameError
        actual["EditorModel.headerNameLabel"] = EditorModel.headerNameLabel
        actual["EditorModel.headerNamePrompt"] = EditorModel.headerNamePrompt
        actual["EditorModel.headerValueError"] = EditorModel.headerValueError
        actual["EditorModel.headerValueLabel"] = EditorModel.headerValueLabel
        actual["EditorModel.hideValueLabel"] = EditorModel.hideValueLabel
        actual["EditorModel.importedNote"] = EditorModel.importedNote("X", "Y")
        actual["EditorModel.invalidURLError"] = EditorModel.invalidURLError
        actual["EditorModel.jsonTab"] = EditorModel.jsonTab
        actual["EditorModel.jsonTip"] = EditorModel.jsonTip
        actual["EditorModel.localTypeTitle"] = EditorModel.localTypeTitle
        actual["EditorModel.lockedFieldsNote"] = EditorModel.lockedFieldsNote("X")
        actual["EditorModel.lossWarningPrefix"] = EditorModel.lossWarningPrefix
        actual["EditorModel.nameLabel"] = EditorModel.nameLabel
        actual["EditorModel.namePrompt"] = EditorModel.namePrompt
        actual["EditorModel.needsPath"] = EditorModel.needsPath
        actual["EditorModel.needsValue"] = EditorModel.needsValue
        actual["EditorModel.noTargetMessage"] = EditorModel.noTargetMessage
        actual["EditorModel.notValidJSON"] = EditorModel.notValidJSON
        actual["EditorModel.oauthSecretCaption"] = EditorModel.oauthSecretCaption
        actual["EditorModel.propagateLabel"] = EditorModel.propagateLabel("X", "Y")
        actual["EditorModel.propagateLabelMany"] = EditorModel.propagateLabelMany("X", "Y")
        actual["EditorModel.publishedNote"] = EditorModel.publishedNote("X")
        actual["EditorModel.remoteFooter"] = EditorModel.remoteFooter
        actual["EditorModel.remoteTypeTitle"] = EditorModel.remoteTypeTitle
        actual["EditorModel.deleteArgumentLabel"] = EditorModel.deleteArgumentLabel
        actual["EditorModel.deleteVariableLabel"] = EditorModel.deleteVariableLabel
        actual["EditorModel.deletedOutsideDetail"] = EditorModel.deletedOutsideDetail
        actual["EditorModel.deletedOutsideMessage"] = EditorModel.deletedOutsideMessage("X")
        actual["EditorModel.saveAnywayButton"] = EditorModel.saveAnywayButton
        actual["EditorModel.saveButton"] = EditorModel.saveButton
        actual["EditorModel.scopesLabel"] = EditorModel.scopesLabel
        actual["EditorModel.scopesPrompt"] = EditorModel.scopesPrompt
        actual["EditorModel.serverURLLabel"] = EditorModel.serverURLLabel
        actual["EditorModel.serverURLPrompt"] = EditorModel.serverURLPrompt
        actual["EditorModel.showValueLabel"] = EditorModel.showValueLabel
        actual["EditorModel.stayInJSONButton"] = EditorModel.stayInJSONButton
        actual["EditorModel.switchAnywayButton"] = EditorModel.switchAnywayButton
        actual["EditorModel.tokenLabel"] = EditorModel.tokenLabel
        actual["EditorModel.typeLabel"] = EditorModel.typeLabel
        actual["EditorModel.urlHint"] = EditorModel.urlHint
        actual["EditorModel.valueLabel"] = EditorModel.valueLabel
        actual["EditorModel.viewPickerLabel"] = EditorModel.viewPickerLabel
        actual["EditorModel.whatCanIChange"] = EditorModel.whatCanIChange
        actual["EditorModel.whatCanIChangeAnswer"] = EditorModel.whatCanIChangeAnswer
        actual["EditorModel.windowGroupTitle"] = EditorModel.windowGroupTitle

        // MARK: FieldName

        actual["FieldName.argument"] = FieldName.argument(1)
        actual["FieldName.command"] = FieldName.command
        actual["FieldName.document"] = FieldName.document("X")
        actual["FieldName.envValue"] = FieldName.envValue("X")
        actual["FieldName.hint"] = FieldName.hint("X")

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
        actual["MasterStore.nameEmptyError"] = nameEmptyStore.addCollection(named: "   ")
        var duplicateStore = MasterStore(activeCollection: "X", collections: ["X": Collection()])
        actual["MasterStore.duplicateCollectionNameError"] = duplicateStore.addCollection(named: "X")
        var deleteLastStore = MasterStore.empty
        actual["MasterStore.deleteLastCollectionError"] = deleteLastStore.deleteCollection(named: deleteLastStore.activeCollection)
        var unknownCollectionStore = MasterStore.empty
        actual["MasterStore.unknownCollectionError"] = unknownCollectionStore.switchCollection(to: "X")

        // MARK: Notifications / PopoverModel

        actual["Notifications.restartToastButton"] = Notifications.restartToastButton
        actual["Notifications.title"] = Notifications.title
        actual["PopoverModel.chooseFolderButton"] = PopoverModel.chooseFolderButton
        actual["PopoverModel.emptyText"] = PopoverModel.emptyText("X")
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
        actual["PublishModel.noFolderError"] = PublishModel.noFolderError
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
        actual["ReviewModel.deletedLabel"] = ReviewModel.deletedLabel
        actual["ReviewModel.sourceMovedMessage"] = ReviewModel.sourceMovedMessage
        actual["ReviewModel.title"] = ReviewModel.title("X")

        // MARK: SettingsModel

        actual["SettingsModel.autoUpdateTitle"] = SettingsModel.autoUpdateTitle
        actual["SettingsModel.backupsCaption"] = SettingsModel.backupsCaption
        actual["SettingsModel.backupsFooter"] = SettingsModel.backupsFooter
        actual["SettingsModel.backupsHeader"] = SettingsModel.backupsHeader
        actual["SettingsModel.backupsTitle"] = SettingsModel.backupsTitle
        actual["SettingsModel.checkForUpdatesTitle"] = SettingsModel.checkForUpdatesTitle
        actual["SettingsModel.chooseButtonTitle"] = SettingsModel.chooseButtonTitle
        actual["SettingsModel.chooseTitle"] = SettingsModel.chooseTitle
        actual["SettingsModel.claudeAppHeader"] = SettingsModel.claudeAppHeader
        actual["SettingsModel.claudeAppRejectedTitle"] = SettingsModel.claudeAppRejectedTitle
        actual["SettingsModel.claudeIntegrationCaption"] = SettingsModel.claudeIntegrationCaption
        actual["SettingsModel.claudeIntegrationHeader"] = SettingsModel.claudeIntegrationHeader
        actual["SettingsModel.claudeIntegrationTitle"] = SettingsModel.claudeIntegrationTitle
        actual["SettingsModel.claudeTab"] = SettingsModel.claudeTab
        actual["SettingsModel.commandLineToolHeader"] = SettingsModel.commandLineToolHeader
        actual["SettingsModel.confirmClaudeChangesOffCaption"] = SettingsModel.confirmClaudeChangesOffCaption
        actual["SettingsModel.confirmClaudeChangesOnCaption"] = SettingsModel.confirmClaudeChangesOnCaption
        actual["SettingsModel.confirmClaudeChangesTitle"] = SettingsModel.confirmClaudeChangesTitle
        actual["SettingsModel.confirmQuitTitle"] = SettingsModel.confirmQuitTitle
        actual["SettingsModel.confirmRestartTitle"] = SettingsModel.confirmRestartTitle
        actual["SettingsModel.confirmationsHeader"] = SettingsModel.confirmationsHeader
        actual["SettingsModel.generalTab"] = SettingsModel.generalTab
        actual["SettingsModel.launchAtLoginTitle"] = SettingsModel.launchAtLoginTitle
        actual["SettingsModel.loginItemApprovalNote"] = SettingsModel.loginItemApprovalNote
        actual["SettingsModel.loginItemFailureNote"] = SettingsModel.loginItemFailureNote("X")
        actual["SettingsModel.masterListFooter"] = SettingsModel.masterListFooter
        actual["SettingsModel.masterListHeader"] = SettingsModel.masterListHeader
        actual["SettingsModel.notificationsHeader"] = SettingsModel.notificationsHeader
        actual["SettingsModel.notifyCaption"] = SettingsModel.notifyCaption
        actual["SettingsModel.notifyTitle"] = SettingsModel.notifyTitle
        actual["SettingsModel.previewUpdatesCaption"] = SettingsModel.previewUpdatesCaption
        actual["SettingsModel.restoreButtonTitle"] = SettingsModel.restoreButtonTitle
        actual["SettingsModel.revealInFinderTitle"] = SettingsModel.revealInFinderTitle
        actual["SettingsModel.startupHeader"] = SettingsModel.startupHeader
        actual["SettingsModel.storageTab"] = SettingsModel.storageTab
        actual["SettingsModel.toolsCaption"] = SettingsModel.toolsCaption
        actual["SettingsModel.toolsHeader"] = SettingsModel.toolsHeader
        actual["SettingsModel.updatesHeader"] = SettingsModel.updatesHeader
        actual["SettingsModel.useDefaultTitle"] = SettingsModel.useDefaultTitle
        actual["SettingsModel.versionText"] = SettingsModel.versionText("X")

        // MARK: ToolFamily / ToolFooterLine / ToolNote

        actual["ToolFamily.nodeJS.installCommand"] = ToolFamily.nodeJS.installCommand
        actual["ToolFamily.nodeJS.linkTitle"] = ToolFamily.nodeJS.linkTitle
        actual["ToolFamily.nodeJS.linkURL"] = ToolFamily.nodeJS.linkURL.absoluteString
        actual["ToolFamily.nodeJS.toolNames"] = ToolFamily.nodeJS.toolNames
        actual["ToolFamily.uv.installCommand"] = ToolFamily.uv.installCommand
        actual["ToolFamily.uv.linkTitle"] = ToolFamily.uv.linkTitle
        actual["ToolFamily.uv.linkURL"] = ToolFamily.uv.linkURL.absoluteString
        actual["ToolFamily.uv.toolNames"] = ToolFamily.uv.toolNames
        actual["ToolFooterLine.installText"] = ToolFooterLine.installText("X", "Y", "Z")
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
    /// pinned here rather than by the fixture walk. Swift-only: the C# walk
    /// inlines `is not null`, which JsonNode already gives this meaning, so it
    /// has no helper of its own to pin.
    func testANullPlatformValueDoesNotResolve() {
        XCTAssertFalse(Self.isResolved(nil))
        XCTAssertFalse(Self.isResolved(.null))
        XCTAssertTrue(Self.isResolved(.string("Quit")))
        XCTAssertTrue(Self.isResolved(.object(["format": .string("{0} left")])))
    }
}
