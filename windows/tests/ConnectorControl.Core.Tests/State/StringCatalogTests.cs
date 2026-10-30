using System.Text.Json.Nodes;
using ConnectorControl.Core;
using ConnectorControl.Core.Services;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;
using ConnectorControl.Core.Transport;
using JsonTextValue = System.Text.Json.Nodes.JsonValue;

namespace ConnectorControl.Core.Tests.State;

/// <summary>
/// Mirror: Tests/ConnectorControlStateTests/StringCatalogTests.swift
///
/// Guards every user-facing string this app and the Mac app are meant to carry
/// byte-for-byte, against the shared fixture both suites read:
/// Tests/Fixtures/strings.json. A wording change on either side that the other
/// side does not also make fails exactly one of the two suites.
/// </summary>
public class StringCatalogTests
{
    private enum ValueKind { Plain, Format }

    /// <summary>
    /// This fixture entry's Windows-side resolution, or null when the key is
    /// Mac-only (no "win" side, and not a bare shared value) - such a key
    /// must never appear in `actual` below.
    /// </summary>
    private static (ValueKind Kind, string Text)? WinValue(JsonNode? entry)
    {
        if (entry is JsonTextValue leaf && leaf.TryGetValue(out string? plain))
        {
            return (ValueKind.Plain, plain);
        }
        if (entry is not JsonObject obj)
        {
            return null;
        }
        if (obj.TryGetPropertyValue("format", out var sharedFormat)
            && sharedFormat is JsonTextValue sharedFormatLeaf && sharedFormatLeaf.TryGetValue(out string? sharedFormatText))
        {
            return (ValueKind.Format, sharedFormatText);
        }
        if (!obj.TryGetPropertyValue("win", out var win) || win is null)
        {
            return null;
        }
        if (win is JsonTextValue winLeaf && winLeaf.TryGetValue(out string? winPlain))
        {
            return (ValueKind.Plain, winPlain);
        }
        if (win is JsonObject winObj
            && winObj.TryGetPropertyValue("format", out var winFormat)
            && winFormat is JsonTextValue winFormatLeaf && winFormatLeaf.TryGetValue(out string? winFormatText))
        {
            return (ValueKind.Format, winFormatText);
        }
        return null;
    }

    /// <summary>Substitutes {0}, {1}, … with <paramref name="args"/>, in order - the same convention the Swift test's fixture resolution uses.</summary>
    private static string Expand(string format, params string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            format = format.Replace($"{{{i}}}", args[i], StringComparison.Ordinal);
        }
        return format;
    }

    /// <summary>
    /// Fixed arguments, by key, for every fixture entry whose Windows value is
    /// a {n} template: "X"/"Y" for string parameters, "3"/"7" for int
    /// parameters, and the joined-keys rendering for a factory that takes a
    /// list of keys - matching the convention the Swift test also follows.
    /// </summary>
    private static readonly Dictionary<string, string[]> ArgsByKey = new(StringComparer.Ordinal)
    {
        ["AppState.collectionLocateBanner"] = ["X"],
        ["AppState.collectionPublishFailedBanner"] = ["X", "Y", "Z"],
        ["AppState.collectionUpdateBanner"] = ["X", "Y"],
        ["AppState.collectionUpdateNotificationBody"] = ["X", "Y"],
        ["AppState.deleteCollectionMessage"] = ["X"],
        ["AppState.duplicateNameError"] = ["X"],
        ["AppState.enabledSubtitle"] = ["3", "7"],
        ["AppState.ingestedElsewhereSentence"] = ["X", "Y", "Z"],
        ["AppState.keptPathCarriedError"] = ["X", "Y"],
        ["AppState.malformedConfigMessage"] = ["X"],
        ["AppState.needsValueCaution"] = ["X"],
        ["AppState.newCredentialError"] = ["X"],
        ["AppState.pathMarkMovedError"] = ["X"],
        ["AppState.publishFolderCarriedError"] = ["X", "Y"],
        ["AppState.publishSlugTakenError"] = ["X"],
        ["AppState.restoreCollectionGoneError"] = ["X"],
        ["AppState.restoreSubscribedError"] = ["X"],
        ["AppState.sourceUnreadableError"] = ["X", "Y"],
        ["AppState.unreviewedConnectorError"] = ["X"],
        ["ClaudeProcess.notAClaudePackageError"] = ["X"],
        ["ClaudePublisher.subjectNoOrganizationError"] = ["X", "Y"],
        ["ClaudeSignature.notFoundMessage"] = ["X"],
        ["CollectionsModel.connectorTally.many"] = ["3"],
        ["CollectionsModel.connectorTally.one"] = ["1"],
        ["CollectionsModel.copiedNote.many"] = ["3", "X"],
        ["CollectionsModel.copiedNote.one"] = ["X"],
        ["CollectionsModel.deleteCheckedMessage.many"] = ["3"],
        ["CollectionsModel.deleteCheckedMessage.one"] = ["X"],
        ["CollectionsModel.deleteConnectorsSentence.many"] = ["3"],
        ["CollectionsModel.deleteConnectorsSentence.one"] = ["1"],
        ["CollectionsModel.deleteNextActiveSentence"] = ["X"],
        ["CollectionsModel.deletePublishedFileQuestion"] = ["X"],
        ["CollectionsModel.editLabel"] = ["X"],
        ["CollectionsModel.selectedCount"] = ["3"],
        ["CollectionsModel.stopSyncingMessage"] = ["X"],
        ["CommandLineToolText.installFailedError"] = ["X"],
        ["CommandLineToolText.installedInText"] = ["X"],
        ["CommandLineToolText.missingToolError"] = ["X"],
        ["CommandLineToolText.occupiedError"] = ["X"],
        ["CommandLineToolText.removeFailedError"] = ["X"],
        ["CommandLineToolText.scriptsCaption"] = ["X"],
        ["ConfigService.corruptStoreNote"] = ["X"],
        ["ConfigService.corruptStoreRestoredNote"] = ["X", "Y"],
        ["ConfigService.invalidBackupError"] = ["X", "Y"],
        ["ConfigService.invalidBackupMcpServersError"] = ["X"],
        ["ControlCaller.callingItselfText"] = ["X"],
        ["ControlCommandLine.missingValueError"] = ["X"],
        ["ControlCommandLine.unknownCommandError"] = ["X"],
        ["ControlCommandLine.unknownOptionError"] = ["X"],
        ["ControlCommandLine.unknownSubcommandError"] = ["X", "Y"],
        ["ControlCommandLine.usageError"] = ["X"],
        ["ControlCommandLine.versionLine"] = ["X"],
        ["ControlMasking.assignment"] = ["X", "Y"],
        ["ControlMasking.oauthClientText"] = ["X", "Y"],
        ["ControlMasking.remoteBridgeType"] = ["X"],
        ["ControlTools.addActiveMessage"] = ["X", "Y"],
        ["ControlTools.addMessage"] = ["X", "Y"],
        ["ControlTools.addTitle"] = ["X", "Y"],
        ["ControlTools.addedNotification"] = ["X", "Y", "Z"],
        ["ControlTools.addedText"] = ["X", "Y", "Z"],
        ["ControlTools.addsClause"] = ["X"],
        ["ControlTools.appliedUpdateNotification"] = ["X", "Y"],
        ["ControlTools.appliedUpdateText"] = ["X"],
        ["ControlTools.applyUpdateTitle"] = ["X", "Y"],
        ["ControlTools.arrivedOffText.many"] = ["3"],
        ["ControlTools.arrivedOffText.one"] = ["1"],
        ["ControlTools.changeActiveMessage"] = ["X"],
        ["ControlTools.changeMessage"] = ["X"],
        ["ControlTools.changeTitle"] = ["X", "Y"],
        ["ControlTools.changedNotification"] = ["X", "Y", "Z"],
        ["ControlTools.changedText"] = ["X", "Y"],
        ["ControlTools.changesClause"] = ["X"],
        ["ControlTools.connectorCountText.many"] = ["3", "7"],
        ["ControlTools.connectorCountText.one"] = ["1", "1"],
        ["ControlTools.createdText"] = ["X"],
        ["ControlTools.deleteActiveMessage"] = ["X"],
        ["ControlTools.deleteCollectionTitle"] = ["X", "Y"],
        ["ControlTools.deleteMessage"] = ["X"],
        ["ControlTools.deleteTitle"] = ["X", "Y"],
        ["ControlTools.deletedCollectionNotification"] = ["X", "Y"],
        ["ControlTools.deletedCollectionText"] = ["X"],
        ["ControlTools.deletedNotification"] = ["X", "Y", "Z"],
        ["ControlTools.deletedText"] = ["X", "Y"],
        ["ControlTools.deletesClause"] = ["X"],
        ["ControlTools.detail"] = ["X", "Y"],
        ["ControlTools.documentTooLargeError"] = ["X"],
        ["ControlTools.duplicateSubscribedError"] = ["X"],
        ["ControlTools.duplicatedText"] = ["X", "Y"],
        ["ControlTools.emptyCollectionText"] = ["X"],
        ["ControlTools.fromFooter"] = ["X"],
        ["ControlTools.importMessage.many"] = ["3", "X"],
        ["ControlTools.importMessage.one"] = ["1", "X"],
        ["ControlTools.importNewMessage.many"] = ["3", "X"],
        ["ControlTools.importNewMessage.one"] = ["1", "X"],
        ["ControlTools.importReplacingMessage.many"] = ["3", "X"],
        ["ControlTools.importReplacingMessage.one"] = ["1", "X"],
        ["ControlTools.importTitle"] = ["X", "Y"],
        ["ControlTools.importedNotification.many"] = ["X", "3", "Y"],
        ["ControlTools.importedNotification.one"] = ["X", "1", "Y"],
        ["ControlTools.importedReplacingText.many"] = ["3", "X"],
        ["ControlTools.importedReplacingText.one"] = ["1", "X"],
        ["ControlTools.importedText.many"] = ["3", "X"],
        ["ControlTools.importedText.one"] = ["1", "X"],
        ["ControlTools.installHint"] = ["X", "Y"],
        ["ControlTools.integrationSetText"] = ["X"],
        ["ControlTools.missingArgumentError"] = ["X"],
        ["ControlTools.needsNewerAppError"] = ["X"],
        ["ControlTools.needsValuesText"] = ["X"],
        ["ControlTools.noBackupError"] = ["X"],
        ["ControlTools.noConnectorError"] = ["X", "Y"],
        ["ControlTools.noUpdateError"] = ["X"],
        ["ControlTools.notAFileError"] = ["X"],
        ["ControlTools.notOneOfError"] = ["X", "Y"],
        ["ControlTools.notSubscribedError"] = ["X"],
        ["ControlTools.nothingToImportError"] = ["X"],
        ["ControlTools.pendingUpdateText"] = ["X", "Y"],
        ["ControlTools.renamedCollectionText"] = ["X", "Y"],
        ["ControlTools.renamedNotification"] = ["X", "Y", "Z"],
        ["ControlTools.renamedText"] = ["X", "Y"],
        ["ControlTools.restartTitle"] = ["X"],
        ["ControlTools.restoreActivatingText"] = ["X"],
        ["ControlTools.restoreIntoText"] = ["X"],
        ["ControlTools.restoreTitle"] = ["X", "Y"],
        ["ControlTools.restoredNotification"] = ["X", "Y"],
        ["ControlTools.restoredText"] = ["X"],
        ["ControlTools.savedNotAppliedText"] = ["X"],
        ["ControlTools.settingChangedText"] = ["X", "Y"],
        ["ControlTools.settingNotForClaudeError"] = ["X"],
        ["ControlTools.statusTitle"] = ["X"],
        ["ControlTools.subscribeMessage.many"] = ["X", "3"],
        ["ControlTools.subscribeMessage.one"] = ["X", "1"],
        ["ControlTools.subscribeTitle"] = ["X", "Y"],
        ["ControlTools.subscribedNotification"] = ["X", "Y", "Z"],
        ["ControlTools.subscribedAddError"] = ["X"],
        ["ControlTools.subscribedChangeError"] = ["X"],
        ["ControlTools.subscribedText"] = ["X", "Y"],
        ["ControlTools.switchValueError"] = ["X"],
        ["ControlTools.switchedNotification"] = ["X", "Y"],
        ["ControlTools.switchedText"] = ["X"],
        ["ControlTools.turnedOffNotification"] = ["X", "Y"],
        ["ControlTools.turnedOffText"] = ["X", "Y"],
        ["ControlTools.turnedOnNotification"] = ["X", "Y"],
        ["ControlTools.turnedOnText"] = ["X", "Y"],
        ["ControlTools.unchangedError"] = ["X"],
        ["ControlTools.unknownToolError"] = ["X"],
        ["ControlTools.upToDateText"] = ["X"],
        ["ControlTools.wrongTypeError"] = ["X", "Y"],
        ["CopyModel.title"] = ["X"],
        ["EditTarget.editTitle"] = ["X"],
        ["EditorModel.additionalTitle"] = ["3", "a, b"],
        // Called with the row's index, 2: the text counts from one.
        ["EditorModel.argumentLabel"] = ["3"],
        ["EditorModel.deleteArgumentRowLabel"] = ["3"],
        ["EditorModel.deleteVariableRowLabel"] = ["3"],
        ["EditorModel.hideVariableValueLabel"] = ["3"],
        ["EditorModel.showVariableValueLabel"] = ["3"],
        ["EditorModel.variableNameLabel"] = ["3"],
        ["EditorModel.variableValueLabel"] = ["3"],
        ["PublishModel.hintRowLabel"] = ["X", "Y"],
        ["PublishModel.markPathRowLabel"] = ["X", "Y"],
        ["PublishModel.pathNameRowLabel"] = ["X", "Y"],
        ["PublishModel.forgetMarkRowLabel"] = ["X", "Y"],
        ["PublishModel.releaseValueRowLabel"] = ["X", "Y"],
        ["PublishModel.useDirectoryTokenRowLabel"] = ["X", "Y"],
        ["PublishModel.shareValueRowLabel"] = ["X", "Y"],
        ["EditorModel.argumentNumber"] = ["3"],
        ["EditorModel.changedOutsideMessage"] = ["X"],
        ["EditorModel.cmdUnsafeError"] = ["X"],
        ["EditorModel.deletedOutsideMessage"] = ["X"],
        ["EditorModel.duplicateEnvError"] = ["X"],
        ["EditorModel.importedNote"] = ["X", "Y"],
        ["EditorModel.lockedFieldsNote"] = ["X"],
        ["EditorModel.propagateLabel"] = ["X", "Y"],
        ["EditorModel.propagateLabelMany"] = ["X", "Y"],
        ["EditorModel.publishedNote"] = ["X"],
        ["FieldName.argument"] = ["1"],
        ["FieldName.document"] = ["X"],
        ["FieldName.envValue"] = ["X"],
        ["FieldName.hint"] = ["X"],
        ["FlyoutModel.settingsNotSavedCaution"] = ["X"],
        ["ImportModel.addModeTitle"] = ["X"],
        ["ImportModel.collisionPickerLabel"] = ["X"],
        ["ImportModel.importButton"] = ["3"],
        ["ImportModel.includeLabel"] = ["X"],
        ["ImportModel.skippedBadge"] = ["X"],
        ["ImportModel.sourceLine"] = ["X", "Y", "3"],
        ["MasterStore.duplicateCollectionNameError"] = ["X"],
        ["MasterStore.unknownCollectionError"] = ["X"],
        ["PopoverModel.emptyText"] = ["X"],
        ["PopoverModel.locateButton"] = ["X"],
        ["PopoverModel.sourceTooltipFormat"] = ["X"],
        ["PublishModel.exportTitle"] = ["X"],
        ["PublishModel.folderLine"] = ["X"],
        ["PublishModel.footerLine"] = ["X", "Y"],
        ["PublishModel.keptPathNote"] = ["X", "Y"],
        ["PublishModel.otherFolderNote"] = ["X", "Y", "Z"],
        ["PublishModel.publishFolderEditNote"] = ["X", "Y"],
        ["PublishModel.publishFolderNote"] = ["X", "Y"],
        ["PublishModel.title"] = ["X"],
        ["PublishModel.unresolvedMarkNote"] = ["X", "Y"],
        ["PublishModel.warningLine"] = ["X", "Y"],
        // The parameter here is a RemoteField, not free text, so the fixed argument is the
        // label one of them renders as rather than the usual "X".
        ["RemotePattern.cmdUnsafeReason"] = ["Server URL"],
        ["RestoreModel.confirmMessage"] = ["X"],
        ["ReviewModel.title"] = ["X"],
        ["SettingsModel.keepCountHelpText"] = ["3"],
        ["SettingsModel.loginItemFailureNote"] = ["X"],
        ["SettingsModel.versionText"] = ["X"],
        ["ToolFooterLine.installText"] = ["X", "Y", "Z"],
        ["UpdateCoordinator.availableDetail"] = ["X", "Y"],
        ["UpdateCoordinator.upToDateDetail"] = ["X"],
        ["UpdateRefusals.insideTheUpdate"] = ["X"],
        ["UpdateRefusals.invalidCharacterEntry"] = ["X"],
        ["UpdateRefusals.notValidlySigned"] = ["X"],
        ["UpdateRefusals.olderVersion"] = ["X", "Y"],
        ["UpdateRefusals.previewForRelease"] = ["X", "Y"],
        ["UpdateRefusals.renamedEntry"] = ["X"],
        ["UpdateRefusals.signedByAnother"] = ["X", "Y"],
        ["UpdateRefusals.signedByUntrusted"] = ["X", "Y"],
        ["UpdateRefusals.sizeMismatch"] = ["X", "3", "7"],
        ["UpdateRefusals.stubNotAProgram"] = ["X"],
        ["UpdateRefusals.unknownInstalledVersion"] = ["X"],
        ["UpdateRefusals.unreadable"] = ["X"],
        ["UpdateRefusals.versionMismatch"] = ["X", "Y"],
    };

    [Fact]
    public void EveryStringMatchesTheSharedCatalog()
    {
        var json = File.ReadAllText(Fixtures.Path("strings.json"));
        var root = JsonNode.Parse(json)!.AsObject();

        var actual = new Dictionary<string, string>(StringComparer.Ordinal);

        // MARK: AppState

        actual["AppState.authoredElsewhereCaution"] = AppState.AuthoredElsewhereCaution;
        actual["AppState.chooseClaude"] = ClaudePublisher.ChooseClaude;
        actual["AppState.claudeConfigChangedBody"] = AppState.ClaudeConfigChangedBody;
        actual["AppState.claudeConfigRegeneratedBody"] = AppState.ClaudeConfigRegeneratedBody;
        actual["AppState.collectionDirCmdUnsafeCaution"] = AppState.CollectionDirCmdUnsafeCaution;
        actual["AppState.collectionLocateBanner"] = AppState.CollectionLocateBanner("X");
        actual["AppState.collectionPublishFailedBanner"] = AppState.CollectionPublishFailedBanner("X", "Y", "Z");
        actual["AppState.collectionUpdateBanner"] = AppState.CollectionUpdateBanner("X", "Y");
        actual["AppState.collectionUpdateNotificationBody"] = AppState.CollectionUpdateNotificationBody("X", "Y");
        actual["AppState.collectionsNotSavedNote"] = AppState.CollectionsNotSavedNote;
        actual["AppState.connectorListChangedBody.noRestart"] =
            AppState.ConnectorListChangedBody(new ServerDelta([], [], []), restartRequired: false);
        actual["AppState.connectorListChangedBody.restart"] =
            AppState.ConnectorListChangedBody(new ServerDelta([], [], []), restartRequired: true);
        actual["AppState.deleteButton"] = AppState.DeleteButton;
        actual["AppState.deleteCollectionMessage"] = AppState.DeleteCollectionMessage("X");
        actual["AppState.duplicateNameError"] = AppState.DuplicateNameError("X");
        actual["AppState.enabledSubtitle"] = AppState.EnabledSubtitle(3, 7);
        actual["AppState.ingestedElsewhereSentence"] = AppState.IngestedElsewhereSentence("X", "Y", "Z");
        actual["AppState.lastLocalCollectionError"] = AppState.LastLocalCollectionError;
        actual["AppState.locateCaution"] = AppState.LocateCaution;
        actual["AppState.malformedConfigMessage"] = AppState.MalformedConfigMessage("X");
        actual["AppState.nameEmptyError"] = AppState.NameEmptyError;
        actual["AppState.needsValueCaution"] = AppState.NeedsValueCaution("X");
        actual["AppState.newCollectionTitle"] = AppState.NewCollectionTitle;
        actual["AppState.newCredentialError"] = AppState.NewCredentialError("X");
        actual["AppState.newerDocumentError"] = AppState.NewerDocumentError;
        actual["AppState.noConnectorsSubtitle"] = AppState.NoConnectorsSubtitle;
        actual["AppState.ownCollectionError"] = AppState.OwnCollectionError;
        actual["AppState.keptPathCarriedError"] = AppState.KeptPathCarriedError("X", "Y");
        actual["AppState.pathMarkMovedError"] = AppState.PathMarkMovedError("X");
        actual["AppState.publishFolderCarriedError"] = AppState.PublishFolderCarriedError("X", "Y");
        actual["AppState.publishIntoStoreError"] = AppState.PublishIntoStoreError;
        actual["AppState.publishSlugTakenError"] = AppState.PublishSlugTakenError("X");
        actual["AppState.quitButton"] = AppState.QuitButton;
        actual["AppState.quitMessage"] = AppState.QuitMessage;
        actual["AppState.regenerationFailedBody"] = AppState.RegenerationFailedBody;
        actual["AppState.relaunchFailedMessage"] = AppState.RelaunchFailedMessage;
        actual["AppState.renameCollectionTitle"] = AppState.RenameCollectionTitle;
        actual["AppState.restartButton"] = AppState.RestartButton;
        actual["AppState.restartInformative"] = AppState.RestartInformative;
        actual["AppState.restartMessage"] = AppState.RestartMessage;
        actual["AppState.restoreCollectionGoneError"] = AppState.RestoreCollectionGoneError("X");
        actual["AppState.restoreSubscribedError"] = AppState.RestoreSubscribedError("X");
        actual["AppState.sourceUnreadableError"] = AppState.SourceUnreadableError("X", "Y");
        actual["AppState.storeChangedBody"] = AppState.StoreChangedBody;
        actual["AppState.targetMustBeLocalError"] = AppState.TargetMustBeLocalError;
        actual["AppState.unpublishedDirectoryCaution"] = AppState.UnpublishedDirectoryCaution;
        actual["AppState.unreviewedConnectorError"] = AppState.UnreviewedConnectorError("X");
        actual["AppVersion.developmentBuild"] = AppVersion.DevelopmentBuild;

        // MARK: BoundedRead

        actual["BoundedRead.notAFileError"] = BoundedRead.NotAFileError;
        actual["BoundedRead.tooLargeError"] = BoundedRead.TooLargeError;

        // MARK: ClaudeConfigIO
        // Both messages are inline literals inside a throw, not exposed as callable
        // factories; pinned here to match ClaudeConfigIO.cs and cross-checked against
        // this project's own JSON-parsing test coverage.

        actual["ClaudeConfigIO.mcpServersNotObjectError"] = "mcpServers is not a JSON object";
        actual["ClaudeConfigIO.notJSONError"] = ClaudeConfigIO.NotJsonError;
        actual["ClaudeConfigIO.repeatedKeyError"] = ClaudeConfigIO.RepeatedKeyError;
        actual["ClaudeConfigIO.topLevelNotObjectError"] = "top level is not a JSON object";

        // MARK: ClaudeIntegration

        actual["ClaudeIntegration.clashNote"] = ClaudeIntegration.ClashNote;
        actual["ClaudeIntegration.endpointUnavailableText"] = ClaudeIntegration.EndpointUnavailableText;
        actual["ClaudeIntegration.moveToApplicationsError"] = ClaudeIntegration.MoveToApplicationsError;
        actual["ClaudeIntegration.offerBody"] = ClaudeIntegration.OfferBody;
        actual["ClaudeIntegration.offerNotNowButton"] = ClaudeIntegration.OfferNotNowButton;
        actual["ClaudeIntegration.offerTitle"] = ClaudeIntegration.OfferTitle;
        actual["ClaudeIntegration.offerTurnOnButton"] = ClaudeIntegration.OfferTurnOnButton;
        actual["ClaudeIntegration.reservedNameError"] = ClaudeIntegration.ReservedNameError;

        // MARK: ClaudeProcess / ClaudePublisher / ClaudeRestarter / ClaudeSignature
        // ClaudeProcess.cs lives in ConnectorControl.App, which this Core test project
        // cannot reference; its literals are pinned here by hand to match
        // windows/src/ConnectorControl.App/Services/ClaudeProcess.cs.

        actual["ClaudeProcess.notAClaudePackageError"] = "X is not a Claude Desktop package. " + ClaudePublisher.ChooseClaude;
        actual["ClaudeProcess.notInstalledMessage"] = "Claude Desktop was not found on this PC.";
        actual["ClaudeRestarter.didNotQuitMessage"] = ClaudePublisher.DidNotQuitMessage;
        actual["ClaudeSignature.notFoundMessage"] = "Claude Desktop was not found at X.";
        // "Y" alone is not a parseable distinguished name, so SubjectProblem takes the
        // no-organization branch and echoes it back verbatim as the signer subject.
        actual["ClaudePublisher.subjectNoOrganizationError"] = ClaudePublisher.SubjectProblem("Y", "X")!;
        // "O=Y" parses with an organization ("y" normalized) that is not Anthropic's, so
        // SubjectProblem takes the not-Anthropic branch; the echoed subject is the raw "O=Y".
        actual["ClaudePublisher.subjectNotAnthropicError"] = ClaudePublisher.SubjectProblem("O=Y", "X")!;

        // MARK: CollectionDocument

        actual["CollectionDocument.credentialLiteralPhrase"] = CollectionDocument.CredentialLiteralPhrase;
        actual["CollectionDocument.notJSONError"] = CollectionDocument.NotJsonError;
        actual["CollectionDocument.credentialReferencePhrase"] = CollectionDocument.CredentialReferencePhrase;
        actual["CollectionDocument.credentialUserOnlyPhrase"] = CollectionDocument.CredentialUserOnlyPhrase;

        // MARK: CollectionsModel

        actual["CollectionsModel.activePill"] = CollectionsModel.ActivePill;
        actual["CollectionsModel.addCollectionTooltip"] = CollectionsModel.AddCollectionTooltip;
        actual["CollectionsModel.addConnectorDisabledTooltip"] = CollectionsModel.AddConnectorDisabledTooltip;
        actual["CollectionsModel.addConnectorTooltip"] = CollectionsModel.AddConnectorTooltip;
        actual["CollectionsModel.connectorTally.many"] = CollectionsModel.ConnectorTally(3);
        actual["CollectionsModel.connectorTally.one"] = CollectionsModel.ConnectorTally(1);
        actual["CollectionsModel.copiedNote.many"] = CollectionsModel.CopiedNote(3, "X");
        actual["CollectionsModel.copiedNote.one"] = CollectionsModel.CopiedNote(1, "X");
        actual["CollectionsModel.copyToButton"] = CollectionsModel.CopyToButton;
        actual["CollectionsModel.deleteAction"] = CollectionsModel.DeleteAction;
        actual["CollectionsModel.deleteConnectorsSentence.many"] = CollectionsModel.DeleteConnectorsSentence(3);
        actual["CollectionsModel.deleteConnectorsSentence.none"] = CollectionsModel.DeleteConnectorsSentence(0);
        actual["CollectionsModel.deleteConnectorsSentence.one"] = CollectionsModel.DeleteConnectorsSentence(1);
        actual["CollectionsModel.deleteCopiesSentence"] = CollectionsModel.DeleteCopiesSentence;
        actual["CollectionsModel.deleteNextActiveSentence"] = CollectionsModel.DeleteNextActiveSentence("X");
        actual["CollectionsModel.deletePublishedFileQuestion"] = CollectionsModel.DeletePublishedFileQuestion("X");
        actual["CollectionsModel.deleteSourceSentence"] = CollectionsModel.DeleteSourceSentence;
        actual["CollectionsModel.documentFilter"] = CollectionsModel.DocumentFilter;
        actual["CollectionsModel.duplicateAction"] = CollectionsModel.DuplicateAction;
        actual["CollectionsModel.editLabel"] = CollectionsModel.EditLabel("X");
        actual["CollectionsModel.exportAllAction"] = CollectionsModel.ExportAllAction;
        actual["CollectionsModel.exportCheckedButton"] = CollectionsModel.ExportCheckedButton;
        actual["CollectionsModel.importButton"] = CollectionsModel.ImportButton;
        actual["CollectionsModel.importSubtitle"] = CollectionsModel.ImportSubtitle;
        actual["CollectionsModel.keepFileButton"] = CollectionsModel.KeepFileButton;
        actual["CollectionsModel.lockedGlyphTooltip"] = CollectionsModel.LockedGlyphTooltip;
        actual["CollectionsModel.makeActiveAction"] = CollectionsModel.MakeActiveAction;
        actual["CollectionsModel.makeLocalCopyButton"] = CollectionsModel.MakeLocalCopyButton;
        actual["CollectionsModel.moreActionsLabel"] = CollectionsModel.MoreActionsLabel;
        actual["CollectionsModel.newButton"] = CollectionsModel.NewButton;
        actual["CollectionsModel.publishButton"] = CollectionsModel.PublishButton;
        actual["CollectionsModel.publishSettingsButton"] = CollectionsModel.PublishSettingsButton;
        actual["CollectionsModel.publishedPill"] = CollectionsModel.PublishedPill;
        actual["CollectionsModel.readOnlyNote"] = CollectionsModel.ReadOnlyNote;
        actual["CollectionsModel.refreshButton"] = CollectionsModel.RefreshButton;
        actual["ConnectorTarget.remoteType"] = ConnectorTarget.RemoteType;
        actual["CollectionsModel.deleteCheckedButton"] = CollectionsModel.DeleteCheckedButton;
        actual["CollectionsModel.deleteCheckedInformative"] = CollectionsModel.DeleteCheckedInformative;
        actual["CollectionsModel.deleteCheckedMessage.many"] = CollectionsModel.DeleteCheckedMessage(["X", "Y", "Z"]);
        actual["CollectionsModel.deleteCheckedMessage.one"] = CollectionsModel.DeleteCheckedMessage(["X"]);
        actual["CollectionsModel.deleteFileButton"] = CollectionsModel.DeleteFileButton;
        actual["CollectionsModel.renameAction"] = CollectionsModel.RenameAction;
        actual["CollectionsModel.selectedCount"] = CollectionsModel.SelectedCount(3);
        actual["CollectionsModel.showPublishedFileAction"] = CollectionsModel.ShowPublishedFileAction;
        actual["CollectionsModel.showSourceFileAction"] = CollectionsModel.ShowSourceFileAction;
        actual["CollectionsModel.stopPublishingAction"] = CollectionsModel.StopPublishingAction;
        actual["CollectionsModel.stopSyncingAction"] = CollectionsModel.StopSyncingAction;
        actual["CollectionsModel.stopSyncingInformative"] = CollectionsModel.StopSyncingInformative;
        actual["CollectionsModel.stopSyncingMessage"] = CollectionsModel.StopSyncingMessage("X");
        actual["CollectionsModel.subscribeButton"] = CollectionsModel.SubscribeButton;
        actual["CollectionsModel.subscribeSubtitle"] = CollectionsModel.SubscribeSubtitle;
        actual["CollectionsModel.subscribedPill"] = CollectionsModel.SubscribedPill;
        actual["CollectionsModel.updateAvailableStatus"] = CollectionsModel.UpdateAvailableStatus;
        actual["CollectionsModel.windowTitle"] = CollectionsModel.WindowTitle;

        // MARK: CommandLineToolText

        actual["CommandLineToolText.adminPrompt"] = CommandLineToolText.AdminPrompt;
        actual["CommandLineToolText.helpCommand"] = CommandLineToolText.HelpCommand;
        actual["CommandLineToolText.inPathText"] = CommandLineToolText.InPathText;
        actual["CommandLineToolText.installButton"] = CommandLineToolText.InstallButton;
        actual["CommandLineToolText.installFailedError"] = CommandLineToolText.InstallFailedError("X");
        actual["CommandLineToolText.installedInText"] = CommandLineToolText.InstalledInText("X");
        actual["CommandLineToolText.missingToolError"] = CommandLineToolText.MissingToolError("X");
        actual["CommandLineToolText.notInstalledText"] = CommandLineToolText.NotInstalledText;
        actual["CommandLineToolText.occupiedError"] = CommandLineToolText.OccupiedError("X");
        actual["CommandLineToolText.removeButton"] = CommandLineToolText.RemoveButton;
        actual["CommandLineToolText.removeFailedError"] = CommandLineToolText.RemoveFailedError("X");
        actual["CommandLineToolText.runHelpCaption"] = CommandLineToolText.RunHelpCaption;
        actual["CommandLineToolText.scriptsCaption"] = CommandLineToolText.ScriptsCaption("X");

        // MARK: ControlMasking / ControlText

        actual["ControlClient.droppedError"] = ControlClient.DroppedError;
        actual["ControlClient.elevatedError"] = ControlClient.ElevatedError;
        actual["ControlClient.notRunningError"] = ControlClient.NotRunningError;
        actual["ControlClient.startingText"] = ControlClient.StartingText;
        actual["ControlClient.unreachableError"] = ControlClient.UnreachableError;
        actual["ControlCommandLine.missingValueError"] = ControlCommandLine.MissingValueError("X");
        actual["ControlCommandLine.unknownCommandError"] = ControlCommandLine.UnknownCommandError("X");
        actual["ControlCommandLine.unknownOptionError"] = ControlCommandLine.UnknownOptionError("X");
        actual["ControlCommandLine.helpTakesNoArgumentsError"] = ControlCommandLine.HelpTakesNoArgumentsError;
        actual["ControlCommandLine.unknownSubcommandError"] = ControlCommandLine.UnknownSubcommandError("X", "Y");
        actual["ControlCommandLine.usageContinuation"] = ControlCommandLine.UsageContinuation;
        actual["ControlCommandLine.usageError"] = ControlCommandLine.UsageError("X");
        actual["ControlCommandLine.versionLine"] = ControlCommandLine.VersionLine("X");
        actual["ControlMasking.addedPrefix"] = ControlMasking.AddedPrefix;
        actual["ControlMasking.argsLabel"] = ControlMasking.ArgsLabel;
        actual["ControlMasking.assignment"] = ControlMasking.Assignment("X", "Y");
        actual["ControlMasking.commandLabel"] = ControlMasking.CommandLabel;
        actual["ControlMasking.envLabel"] = ControlMasking.EnvLabel;
        actual["ControlMasking.headersLabel"] = ControlMasking.HeadersLabel;
        actual["ControlMasking.mask"] = ControlMasking.Mask;
        actual["ControlMasking.nameLabel"] = ControlMasking.NameLabel;
        actual["ControlMasking.needsValueState"] = ControlMasking.NeedsValueState;
        actual["ControlMasking.oauthClientText"] = ControlMasking.OauthClientText("X", "Y");
        actual["ControlMasking.oauthLabel"] = ControlMasking.OauthLabel;
        actual["ControlMasking.otherLabel"] = ControlMasking.OtherLabel;
        actual["ControlMasking.remoteBridgeType"] = ControlMasking.RemoteBridgeType("X");
        actual["ControlMasking.removedPrefix"] = ControlMasking.RemovedPrefix;
        actual["ControlMasking.scopesLabel"] = ControlMasking.ScopesLabel;
        actual["ControlMasking.setState"] = ControlMasking.SetState;
        actual["ControlMasking.setText"] = ControlMasking.SetText;
        actual["ControlMasking.typeLabel"] = ControlMasking.TypeLabel;
        actual["ControlMasking.urlLabel"] = ControlMasking.UrlLabel;
        actual["ControlRelay.restartedError"] = ControlRelay.RestartedError;
        actual["ControlText.listAnd"] = ControlText.ListAnd;

        // MARK: ConfirmationDialog / TextRun

        actual["ConfirmationDialog.allowButton"] = ConfirmationDialog.AllowButton;
        actual["ConfirmationDialog.dontAllowButton"] = ConfirmationDialog.DontAllowButton;
        actual["TextRun.cutMark"] = TextRun.CutMark;

        // MARK: ControlCaller / ControlSettingKey / ControlTools

        actual["ControlCaller.callingItselfText"] = ControlCaller.CallingItselfText("X");
        actual["ControlCaller.claudeName"] = ControlCaller.ClaudeName;
        actual["ControlCaller.unknownClientName"] = ControlCaller.UnknownClientName;
        actual["ControlSettingKey.automaticUpdates"] = ControlSettingKey.AutomaticUpdates;
        actual["ControlSettingKey.backupsToKeep"] = ControlSettingKey.BackupsToKeep;
        actual["ControlSettingKey.claudeIntegration"] = ControlSettingKey.ClaudeIntegration;
        actual["ControlSettingKey.confirmClaudeChanges"] = ControlSettingKey.ConfirmClaudeChanges;
        actual["ControlSettingKey.confirmQuit"] = ControlSettingKey.ConfirmQuit;
        actual["ControlSettingKey.confirmRestart"] = ControlSettingKey.ConfirmRestart;
        actual["ControlSettingKey.launchAtLogin"] = ControlSettingKey.LaunchAtLogin;
        actual["ControlSettingKey.notifyExternalChanges"] = ControlSettingKey.NotifyExternalChanges;
        actual["ControlTools.activeCollectionLabel"] = ControlTools.ActiveCollectionLabel;
        actual["ControlTools.activeMark"] = ControlTools.ActiveMark;
        actual["ControlTools.addActiveMessage"] = ControlTools.AddActiveMessage("X", "Y");
        actual["ControlTools.addMessage"] = ControlTools.AddMessage("X", "Y");
        actual["ControlTools.addTitle"] = ControlTools.AddTitle("X", "Y");
        actual["ControlTools.addedNotification"] = ControlTools.AddedNotification("X", "Y", "Z");
        actual["ControlTools.addedText"] = ControlTools.AddedText("X", "Y", "Z");
        actual["ControlTools.addsClause"] = ControlTools.AddsClause("X");
        actual["ControlTools.appliedUpdateNotification"] = ControlTools.AppliedUpdateNotification("X", "Y");
        actual["ControlTools.appliedUpdateText"] = ControlTools.AppliedUpdateText("X");
        actual["ControlTools.applyUpdateTitle"] = ControlTools.ApplyUpdateTitle("X", "Y");
        actual["ControlTools.argumentsNotObjectError"] = ControlTools.ArgumentsNotObjectError;
        actual["ControlTools.arrivedOffText.many"] = ControlTools.ArrivedOffText(3);
        actual["ControlTools.arrivedOffText.one"] = ControlTools.ArrivedOffText(1);
        actual["ControlTools.booleanKind"] = ControlTools.BooleanKind;
        actual["ControlTools.changeActiveMessage"] = ControlTools.ChangeActiveMessage("X");
        actual["ControlTools.changeMessage"] = ControlTools.ChangeMessage("X");
        actual["ControlTools.changeTitle"] = ControlTools.ChangeTitle("X", "Y");
        actual["ControlTools.changedNotification"] = ControlTools.ChangedNotification("X", "Y", "Z");
        actual["ControlTools.changedText"] = ControlTools.ChangedText("X", "Y");
        actual["ControlTools.changedWhileAskingError"] = ControlTools.ChangedWhileAskingError;
        actual["ControlTools.changesClause"] = ControlTools.ChangesClause("X");
        actual["ControlTools.claudeGroup"] = ControlTools.ClaudeGroup;
        actual["ControlTools.claudeLabel"] = ControlTools.ClaudeLabel;
        actual["ControlTools.claudeNotRunning"] = ControlTools.ClaudeNotRunning;
        actual["ControlTools.claudeRunning"] = ControlTools.ClaudeRunning;
        actual["ControlTools.claudeRunningRestart"] = ControlTools.ClaudeRunningRestart;
        actual["ControlTools.clauseSeparator"] = ControlTools.ClauseSeparator;
        actual["ControlTools.collectionHeader"] = ControlTools.CollectionHeader;
        actual["ControlTools.collectionsGroup"] = ControlTools.CollectionsGroup;
        actual["ControlTools.configShapeError"] = ControlTools.ConfigShapeError;
        actual["ControlTools.connectorCountText.many"] = ControlTools.ConnectorCountText(3, 7);
        actual["ControlTools.connectorCountText.one"] = ControlTools.ConnectorCountText(1, 1);
        actual["ControlTools.connectorsGroup"] = ControlTools.ConnectorsGroup;
        actual["ControlTools.connectorsHeader"] = ControlTools.ConnectorsHeader;
        actual["ControlTools.createdText"] = ControlTools.CreatedText("X");
        actual["ControlTools.declinedError"] = ControlTools.DeclinedError;
        actual["ControlTools.deleteActiveMessage"] = ControlTools.DeleteActiveMessage("X");
        actual["ControlTools.deleteCollectionTitle"] = ControlTools.DeleteCollectionTitle("X", "Y");
        actual["ControlTools.deleteMessage"] = ControlTools.DeleteMessage("X");
        actual["ControlTools.deleteTitle"] = ControlTools.DeleteTitle("X", "Y");
        actual["ControlTools.deletedCollectionNotification"] = ControlTools.DeletedCollectionNotification("X", "Y");
        actual["ControlTools.deletedCollectionText"] = ControlTools.DeletedCollectionText("X");
        actual["ControlTools.deletedNotification"] = ControlTools.DeletedNotification("X", "Y", "Z");
        actual["ControlTools.deletedText"] = ControlTools.DeletedText("X", "Y");
        actual["ControlTools.deletesClause"] = ControlTools.DeletesClause("X");
        actual["ControlTools.detail"] = ControlTools.Detail("X", "Y");
        actual["ControlTools.documentTooLargeError"] = ControlTools.DocumentTooLargeError("X");
        actual["ControlTools.duplicateSubscribedError"] = ControlTools.DuplicateSubscribedError("X");
        actual["ControlTools.duplicatedText"] = ControlTools.DuplicatedText("X", "Y");
        actual["ControlTools.emptyCollectionText"] = ControlTools.EmptyCollectionText("X");
        actual["ControlTools.fromFooter"] = ControlTools.FromFooter("X");
        actual["ControlTools.helpOptions"] = ControlTools.HelpOptions;
        actual["ControlTools.helpUsage"] = ControlTools.HelpUsage;
        actual["ControlTools.idHeader"] = ControlTools.IdHeader;
        actual["ControlTools.importMessage.many"] = ControlTools.ImportMessage(3, "X");
        actual["ControlTools.importMessage.one"] = ControlTools.ImportMessage(1, "X");
        actual["ControlTools.importNewMessage.many"] = ControlTools.ImportNewMessage(3, "X");
        actual["ControlTools.importNewMessage.one"] = ControlTools.ImportNewMessage(1, "X");
        actual["ControlTools.importReplacingMessage.many"] = ControlTools.ImportReplacingMessage(3, "X");
        actual["ControlTools.importReplacingMessage.one"] = ControlTools.ImportReplacingMessage(1, "X");
        actual["ControlTools.importTitle"] = ControlTools.ImportTitle("X", "Y");
        actual["ControlTools.importedNotification.many"] = ControlTools.ImportedNotification("X", 3, "Y");
        actual["ControlTools.importedNotification.one"] = ControlTools.ImportedNotification("X", 1, "Y");
        actual["ControlTools.importedReplacingText.many"] = ControlTools.ImportedReplacingText(3, "X");
        actual["ControlTools.importedReplacingText.one"] = ControlTools.ImportedReplacingText(1, "X");
        actual["ControlTools.importedText.many"] = ControlTools.ImportedText(3, "X");
        actual["ControlTools.importedText.one"] = ControlTools.ImportedText(1, "X");
        actual["ControlTools.installHint"] = ControlTools.InstallHint("X", "Y");
        actual["ControlTools.integerKind"] = ControlTools.IntegerKind;
        actual["ControlTools.integrationLabel"] = ControlTools.IntegrationLabel;
        actual["ControlTools.integrationOffError"] = ControlTools.IntegrationOffError;
        actual["ControlTools.integrationOnConfirming"] = ControlTools.IntegrationOnConfirming;
        actual["ControlTools.integrationOnNotConfirming"] = ControlTools.IntegrationOnNotConfirming;
        actual["ControlTools.integrationSetText"] = ControlTools.IntegrationSetText("X");
        actual["ControlTools.intoOrNewCollectionError"] = ControlTools.IntoOrNewCollectionError;
        actual["ControlTools.keepCountError"] = ControlTools.KeepCountError;
        actual["ControlTools.kindHeader"] = ControlTools.KindHeader;
        actual["ControlTools.listKind"] = ControlTools.ListKind;
        actual["ControlTools.localKind"] = ControlTools.LocalKind;
        actual["ControlTools.masterListLabel"] = ControlTools.MasterListLabel;
        actual["ControlTools.missingArgumentError"] = ControlTools.MissingArgumentError("X");
        actual["ControlTools.nameHeader"] = ControlTools.NameHeader;
        actual["ControlTools.needsNewerAppError"] = ControlTools.NeedsNewerAppError("X");
        actual["ControlTools.needsValuesText"] = ControlTools.NeedsValuesText("X");
        actual["ControlTools.networkPathError"] = ControlTools.NetworkPathError;
        actual["ControlTools.noBackupError"] = ControlTools.NoBackupError("X");
        actual["ControlTools.noBackupsText"] = ControlTools.NoBackupsText;
        actual["ControlTools.noConnectorError"] = ControlTools.NoConnectorError("X", "Y");
        actual["ControlTools.noQuestionError"] = ControlTools.NoQuestionError;
        actual["ControlTools.noUpdateError"] = ControlTools.NoUpdateError("X");
        actual["ControlTools.notAFileError"] = ControlTools.NotAFileError("X");
        actual["ControlTools.notOneOfError"] = ControlTools.NotOneOfError("X", "Y");
        actual["ControlTools.notSubscribedError"] = ControlTools.NotSubscribedError("X");
        actual["ControlTools.nothingToChangeError"] = ControlTools.NothingToChangeError;
        actual["ControlTools.nothingToImportError"] = ControlTools.NothingToImportError("X");
        actual["ControlTools.objectKind"] = ControlTools.ObjectKind;
        actual["ControlTools.offWord"] = ControlTools.OffWord;
        actual["ControlTools.onWord"] = ControlTools.OnWord;
        actual["ControlTools.pendingUpdateText"] = ControlTools.PendingUpdateText("X", "Y");
        actual["ControlTools.problemLabel"] = ControlTools.ProblemLabel;
        actual["ControlTools.publishedFileStaysText"] = ControlTools.PublishedFileStaysText;
        actual["ControlTools.publishedKind"] = ControlTools.PublishedKind;
        actual["ControlTools.queuedText"] = ControlTools.QueuedText;
        actual["ControlTools.relaySummary"] = ControlTools.RelaySummary;
        actual["ControlTools.relayUsage"] = ControlTools.RelayUsage;
        actual["ControlTools.renamedCollectionText"] = ControlTools.RenamedCollectionText("X", "Y");
        actual["ControlTools.renamedNotification"] = ControlTools.RenamedNotification("X", "Y", "Z");
        actual["ControlTools.renamedText"] = ControlTools.RenamedText("X", "Y");
        actual["ControlTools.restartItselfTitle"] = ControlTools.RestartItselfTitle;
        actual["ControlTools.restartMessage"] = ControlTools.RestartMessage;
        actual["ControlTools.restartTitle"] = ControlTools.RestartTitle("X");
        actual["ControlTools.restartToApplyText"] = ControlTools.RestartToApplyText;
        actual["ControlTools.restartToUseText"] = ControlTools.RestartToUseText;
        actual["ControlTools.restartingText"] = ControlTools.RestartingText;
        actual["ControlTools.restoreActivatingText"] = ControlTools.RestoreActivatingText("X");
        actual["ControlTools.restoreIntoText"] = ControlTools.RestoreIntoText("X");
        actual["ControlTools.restoreTitle"] = ControlTools.RestoreTitle("X", "Y");
        actual["ControlTools.restoredNotification"] = ControlTools.RestoredNotification("X", "Y");
        actual["ControlTools.restoredText"] = ControlTools.RestoredText("X");
        actual["ControlTools.runsHeader"] = ControlTools.RunsHeader;
        actual["ControlTools.savedNotAppliedText"] = ControlTools.SavedNotAppliedText("X");
        actual["ControlTools.settingChangedText"] = ControlTools.SettingChangedText("X", "Y");
        actual["ControlTools.settingNotForClaudeError"] = ControlTools.SettingNotForClaudeError("X");
        actual["ControlTools.settingsGroup"] = ControlTools.SettingsGroup;
        actual["ControlTools.sharingGroup"] = ControlTools.SharingGroup;
        actual["ControlTools.statusGroup"] = ControlTools.StatusGroup;
        actual["ControlTools.statusTitle"] = ControlTools.StatusTitle("X");
        actual["ControlTools.subscribeMessage.many"] = ControlTools.SubscribeMessage("X", 3);
        actual["ControlTools.subscribeMessage.one"] = ControlTools.SubscribeMessage("X", 1);
        actual["ControlTools.subscribeTitle"] = ControlTools.SubscribeTitle("X", "Y");
        actual["ControlTools.subscribedKind"] = ControlTools.SubscribedKind;
        actual["ControlTools.subscribedNotification"] = ControlTools.SubscribedNotification("X", "Y", "Z");
        actual["ControlTools.subscribedAddError"] = ControlTools.SubscribedAddError("X");
        actual["ControlTools.subscribedChangeError"] = ControlTools.SubscribedChangeError("X");
        actual["ControlTools.subscribedText"] = ControlTools.SubscribedText("X", "Y");
        actual["ControlTools.switchValueError"] = ControlTools.SwitchValueError("X");
        actual["ControlTools.switchedNotification"] = ControlTools.SwitchedNotification("X", "Y");
        actual["ControlTools.switchedText"] = ControlTools.SwitchedText("X");
        actual["ControlTools.takesEffectFooter"] = ControlTools.TakesEffectFooter;
        actual["ControlTools.textKind"] = ControlTools.TextKind;
        actual["ControlTools.tooLongToShowError"] = ControlTools.TooLongToShowError;
        actual["ControlTools.turnedOffNotification"] = ControlTools.TurnedOffNotification("X", "Y");
        actual["ControlTools.turnedOffText"] = ControlTools.TurnedOffText("X", "Y");
        actual["ControlTools.turnedOnNotification"] = ControlTools.TurnedOnNotification("X", "Y");
        actual["ControlTools.turnedOnText"] = ControlTools.TurnedOnText("X", "Y");
        actual["ControlTools.unchangedError"] = ControlTools.UnchangedError("X");
        actual["ControlTools.unknownToolError"] = ControlTools.UnknownToolError("X");
        actual["ControlTools.upToDateText"] = ControlTools.UpToDateText("X");
        actual["ControlTools.updateAvailableKind"] = ControlTools.UpdateAvailableKind;
        actual["ControlTools.waitingText"] = ControlTools.WaitingText;
        actual["ControlTools.wrongTypeError"] = ControlTools.WrongTypeError("X", "Y");
        foreach (var tool in ControlTools.Catalog)
        {
            actual[$"ControlTools.{tool.Name}.description"] = tool.Description;
            actual[$"ControlTools.{tool.Name}.cliUsage"] = tool.CliUsage;
        }
        foreach (var (name, title) in ControlTools.Titles)
        {
            actual[$"ControlTools.titles.{name}"] = title;
        }

        // MARK: CopyModel

        actual["CopyModel.copyButton"] = CopyModel.CopyButton;
        actual["CopyModel.title"] = CopyModel.Title("X");

        // MARK: ConfigService
        // Every one of these is an inline literal built inside a throw or a notes list,
        // not a callable factory; pinned here to match ConfigService.cs and cross-checked
        // against ConfigServiceTests.cs's real-I/O assertions.

        actual["ConfigService.corruptStoreNote"] =
            "The MCP list file was unreadable; it was preserved as X and rebuilt from Claude’s config.";
        actual["ConfigService.corruptStoreRestoredNote"] =
            "The MCP list file was unreadable; it was preserved as X and restored from the backup of Y.";
        actual["ConfigService.invalidBackupError"] = "backup X is not a valid config file (Y)";
        actual["ConfigService.invalidBackupMcpServersError"] = "backup X has an invalid mcpServers section";
        actual["ConfigService.malformedClaudeConfigNote"] =
            "Claude’s config file is not valid JSON. Your MCP list is safe; use Backups ▸ Restore to repair the file.";

        // MARK: Dialogs / AlertDialogs

        actual["Dialogs.cancelTitle"] = Dialogs.CancelTitle;
        actual["AlertDialogs.okTitle"] = Dialogs.OkTitle;

        // MARK: EditTarget / EditorModel

        actual["EditTarget.addTitle"] = EditTarget.AddTitle;
        actual["EditTarget.editTitle"] = EditTarget.EditTitle("X");
        actual["EditorModel.addArgumentTitle"] = EditorModel.AddArgumentTitle;
        actual["EditorModel.addVariableTitle"] = EditorModel.AddVariableTitle;
        actual["EditorModel.additionalTitle"] = EditorModel.AdditionalTitleFor(3, ["a", "b"]);
        actual["EditorModel.argumentLabel"] = EditorModel.ArgumentLabel(2);
        actual["EditorModel.deleteArgumentRowLabel"] = EditorModel.DeleteArgumentRowLabel(2);
        actual["EditorModel.deleteVariableRowLabel"] = EditorModel.DeleteVariableRowLabel(2);
        actual["EditorModel.hideVariableValueLabel"] = EditorModel.HideVariableValueLabel(2);
        actual["EditorModel.showVariableValueLabel"] = EditorModel.ShowVariableValueLabel(2);
        actual["EditorModel.variableNameLabel"] = EditorModel.VariableNameLabel(2);
        actual["EditorModel.variableValueLabel"] = EditorModel.VariableValueLabel(2);
        actual["EditorModel.argumentNumber"] = EditorModel.ArgumentNumber(2);
        actual["EditorModel.argumentPrompt"] = EditorModel.ArgumentPrompt;
        actual["EditorModel.argumentsHeader"] = EditorModel.ArgumentsHeader;
        actual["EditorModel.authenticationHeader"] = EditorModel.AuthenticationHeader;
        actual["EditorModel.automaticCaption"] = EditorModel.AutomaticCaption;
        actual["EditorModel.bearerCaption"] = EditorModel.BearerCaption;
        actual["EditorModel.bearerTokenError"] = EditorModel.BearerTokenError;
        actual["EditorModel.cancelButton"] = EditorModel.CancelButton;
        actual["EditorModel.changedOutsideDetail"] = EditorModel.ChangedOutsideDetail;
        actual["EditorModel.changedOutsideMessage"] = EditorModel.ChangedOutsideMessage("X");
        actual["EditorModel.clientIDError"] = EditorModel.ClientIdError;
        actual["EditorModel.clientIDLabel"] = EditorModel.ClientIdLabel;
        actual["EditorModel.clientSecretLabel"] = EditorModel.ClientSecretLabel;
        actual["EditorModel.cmdPercentCaution"] = EditorModel.CmdPercentCaution;
        actual["EditorModel.cmdUnsafeError"] = EditorModel.CmdUnsafeError("X");
        actual["EditorModel.commandError"] = EditorModel.CommandError;
        actual["EditorModel.commandLabel"] = EditorModel.CommandLabel;
        actual["EditorModel.commandPrompt"] = EditorModel.CommandPrompt;
        actual["EditorModel.duplicateEnvError"] = EditorModel.DuplicateEnvError("X");
        actual["EditorModel.envNamelessError"] = EditorModel.EnvNamelessError;
        actual["EditorModel.environmentHeader"] = EditorModel.EnvironmentHeader;
        actual["EditorModel.formTab"] = EditorModel.FormTab;
        actual["EditorModel.headerNameError"] = EditorModel.HeaderNameError;
        actual["EditorModel.headerNameLabel"] = EditorModel.HeaderNameLabel;
        actual["EditorModel.headerNamePrompt"] = EditorModel.HeaderNamePrompt;
        actual["EditorModel.headerValueError"] = EditorModel.HeaderValueError;
        actual["EditorModel.headerValueLabel"] = EditorModel.HeaderValueLabel;
        actual["EditorModel.hideValueLabel"] = EditorModel.HideValueLabel;
        actual["EditorModel.importedNote"] = EditorModel.ImportedNote("X", "Y");
        actual["EditorModel.invalidURLError"] = EditorModel.InvalidUrlError;
        actual["EditorModel.jsonTab"] = EditorModel.JsonTab;
        actual["EditorModel.jsonTip"] = EditorModel.JsonTip;
        actual["EditorModel.localTypeTitle"] = EditorModel.LocalTypeTitle;
        actual["EditorModel.lockedFieldsNote"] = EditorModel.LockedFieldsNote("X");
        actual["EditorModel.lossWarningPrefix"] = EditorModel.LossWarningPrefix;
        actual["EditorModel.nameLabel"] = EditorModel.NameLabel;
        actual["EditorModel.namePrompt"] = EditorModel.NamePrompt;
        actual["EditorModel.needsPath"] = EditorModel.NeedsPath;
        actual["EditorModel.needsValue"] = EditorModel.NeedsValue;
        actual["EditorModel.notValidJSON"] = EditorModel.NotValidJson;
        actual["EditorModel.oauthSecretCaption"] = EditorModel.OAuthSecretCaption;
        actual["EditorModel.propagateLabel"] = EditorModel.PropagateLabel("X", "Y");
        actual["EditorModel.propagateLabelMany"] = EditorModel.PropagateLabelMany("X", "Y");
        actual["EditorModel.publishedNote"] = EditorModel.PublishedNote("X");
        actual["EditorModel.remoteFooter"] = EditorModel.RemoteFooter;
        actual["EditorModel.remoteTypeTitle"] = EditorModel.RemoteTypeTitle;
        actual["EditorModel.deleteArgumentLabel"] = EditorModel.DeleteArgumentLabel;
        actual["EditorModel.deleteVariableLabel"] = EditorModel.DeleteVariableLabel;
        actual["EditorModel.deletedOutsideDetail"] = EditorModel.DeletedOutsideDetail;
        actual["EditorModel.deletedOutsideMessage"] = EditorModel.DeletedOutsideMessage("X");
        actual["EditorModel.saveAnywayButton"] = EditorModel.SaveAnywayButton;
        actual["EditorModel.saveButton"] = EditorModel.SaveButton;
        actual["EditorModel.scopesLabel"] = EditorModel.ScopesLabel;
        actual["EditorModel.scopesPrompt"] = EditorModel.ScopesPrompt;
        actual["EditorModel.serverURLLabel"] = EditorModel.ServerUrlLabel;
        actual["EditorModel.serverURLPrompt"] = EditorModel.ServerUrlPrompt;
        actual["EditorModel.showValueLabel"] = EditorModel.ShowValueLabel;
        actual["EditorModel.stayInJSONButton"] = EditorModel.StayInJsonButton;
        actual["EditorModel.switchAnywayButton"] = EditorModel.SwitchAnywayButton;
        actual["EditorModel.tokenLabel"] = EditorModel.TokenLabel;
        actual["EditorModel.typeLabel"] = EditorModel.TypeLabel;
        actual["EditorModel.urlHint"] = EditorModel.UrlHint;
        actual["EditorModel.valueLabel"] = EditorModel.ValueLabel;
        actual["EditorModel.whatCanIChange"] = EditorModel.WhatCanIChange;
        actual["EditorModel.whatCanIChangeAnswer"] = EditorModel.WhatCanIChangeAnswer;

        // MARK: FieldName

        actual["FieldName.argument"] = FieldName.Argument(1);
        actual["FieldName.command"] = FieldName.Command;
        actual["FieldName.document"] = FieldName.Document("X");
        actual["FieldName.envValue"] = FieldName.EnvValue("X");
        actual["FieldName.hint"] = FieldName.Hint("X");

        // MARK: FirstRunTip / FlyoutModel

        actual["FirstRunTip.body"] = FirstRunTip.Body;
        actual["FlyoutModel.settingsNotSavedCaution"] = FlyoutModel.SettingsNotSavedCaution("X");
        actual["FlyoutModel.storeNotPrivateCaution"] = FlyoutModel.StoreNotPrivateCaution;
        actual["FlyoutModel.trayOpenTitle"] = FlyoutModel.TrayOpenTitle;

        // MARK: ImportModel

        actual["ImportModel.addModeDetail"] = ImportModel.AddModeDetail;
        actual["ImportModel.addModeTitle"] = ImportModel.AddModeTitle("X");
        actual["ImportModel.addTitle"] = ImportModel.AddTitle;
        actual["ImportModel.cancelButton"] = ImportModel.CancelButton;
        actual["ImportModel.collisionPickerLabel"] = ImportModel.CollisionPickerLabel("X");
        actual["ImportModel.importButton"] = ImportModel.ImportButton(3);
        actual["ImportModel.includeLabel"] = ImportModel.IncludeLabel("X");
        actual["ImportModel.keepBothTitle"] = ImportModel.KeepBothTitle;
        actual["ImportModel.newBadge"] = ImportModel.NewBadge;
        actual["ImportModel.presentBadge"] = ImportModel.PresentBadge;
        actual["ImportModel.replaceKeepsValues"] = ImportModel.ReplaceKeepsValues;
        actual["ImportModel.replaceTitle"] = ImportModel.ReplaceTitle;
        actual["ImportModel.skipTitle"] = ImportModel.SkipTitle;
        actual["ImportModel.skippedBadge"] = ImportModel.SkippedBadge("X");
        actual["ImportModel.sourceLine"] = ImportModel.SourceLine("X", "Y", 3);
        actual["ImportModel.syncModeDetail"] = ImportModel.SyncModeDetail;
        actual["ImportModel.syncModeTitle"] = ImportModel.SyncModeTitle;
        actual["ImportModel.syncNameLabel"] = ImportModel.SyncNameLabel;
        actual["ImportModel.title"] = ImportModel.Title;
        actual["ImportModel.unknownAuthor"] = ImportModel.UnknownAuthor;

        // MARK: MasterStore
        // Every message below is returned by a real mutation, not a bare constant -
        // each store is set up so that mutation fails for exactly the reason this key names.

        var nameEmptyStore = MasterStore.Empty();
        actual["MasterStore.nameEmptyError"] = nameEmptyStore.AddCollection("   ")!;
        var duplicateStore = new MasterStore(MasterStore.CurrentVersion, "X",
            [new KeyValuePair<string, Collection>("X", new Collection())]);
        actual["MasterStore.duplicateCollectionNameError"] = duplicateStore.AddCollection("X")!;
        var deleteLastStore = MasterStore.Empty();
        actual["MasterStore.deleteLastCollectionError"] = deleteLastStore.DeleteCollection(deleteLastStore.ActiveCollection)!;
        var unknownCollectionStore = MasterStore.Empty();
        actual["MasterStore.unknownCollectionError"] = unknownCollectionStore.SwitchCollection("X")!;

        // MARK: Notifications / PopoverModel (FlyoutModel on Windows)

        actual["Notifications.restartToastButton"] = Notifications.RestartToastButton;
        actual["Notifications.title"] = Notifications.Title;
        actual["PopoverModel.chooseFolderButton"] = FlyoutModel.ChooseFolderButton;
        actual["PopoverModel.emptyText"] = FlyoutModel.EmptyText("X");
        actual["PopoverModel.locateButton"] = FlyoutModel.LocateButton("X");
        actual["PopoverModel.manageTitle"] = FlyoutModel.ManageTitle;
        actual["PopoverModel.pendingMenuMark"] = FlyoutModel.PendingMenuMark;
        actual["PopoverModel.quitTooltip"] = FlyoutModel.QuitTooltip;
        actual["PopoverModel.restartTitle"] = FlyoutModel.RestartTitle;
        actual["PopoverModel.retryTitle"] = FlyoutModel.RetryTitle;
        actual["PopoverModel.reviewAndApplyButton"] = FlyoutModel.ReviewAndApplyButton;
        actual["PopoverModel.settingsTooltip"] = FlyoutModel.SettingsTooltip;
        actual["PopoverModel.sourceTooltipFormat"] = FlyoutModel.SourceTooltipFormat("X");
        actual["PopoverModel.title"] = FlyoutModel.Title;

        // MARK: PublishModel

        actual["PublishModel.cancelButton"] = PublishModel.CancelButton;
        actual["PublishModel.chooseFolderButton"] = PublishModel.ChooseFolderButton;
        actual["PublishModel.envSectionTitle"] = PublishModel.EnvSectionTitle;
        actual["PublishModel.exportButton"] = PublishModel.ExportButton;
        actual["PublishModel.exportTitle"] = PublishModel.ExportTitle("X");
        actual["PublishModel.folderLine"] = PublishModel.FolderLine("X");
        actual["PublishModel.footerLine"] = PublishModel.FooterLine("X", "Y");
        actual["PublishModel.forgetMarkButton"] = PublishModel.ForgetMarkButton;
        actual["PublishModel.hintPlaceholder"] = PublishModel.HintPlaceholder;
        actual["PublishModel.hintRowLabel"] = PublishModel.HintRowLabel("X", "Y");
        actual["PublishModel.markPathRowLabel"] = PublishModel.MarkPathRowLabel("X", "Y");
        actual["PublishModel.pathNameRowLabel"] = PublishModel.PathNameRowLabel("X", "Y");
        actual["PublishModel.forgetMarkRowLabel"] = PublishModel.ForgetMarkRowLabel("X", "Y");
        actual["PublishModel.releaseValueRowLabel"] = PublishModel.ReleaseValueRowLabel("X", "Y");
        actual["PublishModel.useDirectoryTokenRowLabel"] = PublishModel.UseDirectoryTokenRowLabel("X", "Y");
        actual["PublishModel.shareValueRowLabel"] = PublishModel.ShareValueRowLabel("X", "Y");
        actual["PublishModel.markPathLabel"] = PublishModel.MarkPathLabel;
        actual["PublishModel.noFolderError"] = PublishModel.NoFolderError;
        actual["PublishModel.pathNamePlaceholder"] = PublishModel.PathNamePlaceholder;
        actual["PublishModel.pathsSectionTitle"] = PublishModel.PathsSectionTitle;
        actual["PublishModel.previewTitle"] = PublishModel.PreviewTitle;
        actual["PublishModel.publishButton"] = PublishModel.PublishButton;
        actual["PublishModel.shareValueLabel"] = PublishModel.ShareValueLabel;
        actual["PublishModel.title"] = PublishModel.Title("X");
        actual["PublishModel.keptPathNote"] = PublishModel.KeptPathNote("X", "Y");
        actual["PublishModel.releaseValueButton"] = PublishModel.ReleaseValueButton;
        actual["PublishModel.publishFolderNote"] = PublishModel.PublishFolderNote("X", "Y");
        actual["PublishModel.publishFolderEditNote"] = PublishModel.PublishFolderEditNote("X", "Y");
        actual["PublishModel.otherFolderNote"] = PublishModel.OtherFolderNote("X", "Y", "Z");
        actual["PublishModel.useDirectoryTokenButton"] = PublishModel.UseDirectoryTokenButton;
        actual["PublishModel.unresolvedMarkNote"] = PublishModel.UnresolvedMarkNote("X", "Y");
        actual["PublishModel.warningLine"] = PublishModel.WarningLine("X", "Y");

        // MARK: RemoteAuthKind

        actual["RemoteAuthKind.automatic.title"] = RemoteAuthKind.Automatic.Title();
        actual["RemoteAuthKind.bearer.title"] = RemoteAuthKind.Bearer.Title();
        actual["RemoteAuthKind.header.title"] = RemoteAuthKind.Header.Title();
        actual["RemoteAuthKind.oauthClient.title"] = RemoteAuthKind.OAuthClient.Title();

        // MARK: RemotePattern
        // Windows-only: the Mac never writes the cmd /c launcher, so it never excludes a
        // connector for this reason.

        actual["RemotePattern.cmdUnsafeReason"] = RemotePattern.CmdUnsafeReason(RemoteField.Url);

        // MARK: RestoreModel

        actual["RestoreModel.cancelTitle"] = RestoreModel.CancelTitle;
        actual["RestoreModel.caption"] = RestoreModel.Caption;
        actual["RestoreModel.confirmMessage"] = RestoreModel.ConfirmMessage("X");
        actual["RestoreModel.headline"] = RestoreModel.Headline;
        actual["RestoreModel.restoreButton"] = RestoreModel.RestoreButton;
        actual["RestoreModel.restoreTitle"] = RestoreModel.RestoreTitle;

        // MARK: ReviewModel

        actual["ReviewModel.addedLabel"] = ReviewModel.AddedLabel;
        actual["ReviewModel.applyButton"] = ReviewModel.ApplyButton;
        actual["ReviewModel.cancelButton"] = ReviewModel.CancelButton;
        actual["ReviewModel.changedLabel"] = ReviewModel.ChangedLabel;
        actual["ReviewModel.refreshButton"] = ReviewModel.RefreshButton;
        actual["ReviewModel.deletedLabel"] = ReviewModel.DeletedLabel;
        actual["ReviewModel.sourceMovedMessage"] = ReviewModel.SourceMovedMessage;
        actual["ReviewModel.title"] = ReviewModel.Title("X");

        // MARK: SettingsModel

        actual["SettingsModel.autoUpdateTitle"] = SettingsModel.AutoUpdateTitle;
        actual["SettingsModel.backupsCaption"] = SettingsModel.BackupsCaption;
        actual["SettingsModel.backupsFooter"] = SettingsModel.BackupsFooter;
        actual["SettingsModel.backupsHeader"] = SettingsModel.BackupsHeader;
        actual["SettingsModel.backupsTitle"] = SettingsModel.BackupsTitle;
        actual["SettingsModel.checkForUpdatesTitle"] = SettingsModel.CheckForUpdatesTitle;
        actual["SettingsModel.checkingTitle"] = SettingsModel.CheckingTitle;
        actual["SettingsModel.chooseButtonTitle"] = SettingsModel.ChooseButtonTitle;
        actual["SettingsModel.chooseTitle"] = SettingsModel.ChooseTitle;
        actual["SettingsModel.claudeAppHeader"] = SettingsModel.ClaudeAppHeader;
        actual["SettingsModel.claudeAppRejectedTitle"] = SettingsModel.LaunchTargetRejectedTitle;
        actual["SettingsModel.claudeIntegrationCaption"] = SettingsModel.ClaudeIntegrationCaption;
        actual["SettingsModel.claudeIntegrationHeader"] = SettingsModel.ClaudeIntegrationHeader;
        actual["SettingsModel.claudeIntegrationTitle"] = SettingsModel.ClaudeIntegrationTitle;
        actual["SettingsModel.claudeTab"] = SettingsModel.ClaudeTab;
        actual["SettingsModel.commandLineToolHeader"] = SettingsModel.CommandLineToolHeader;
        actual["SettingsModel.confirmClaudeChangesOffCaption"] = SettingsModel.ConfirmClaudeChangesOffCaption;
        actual["SettingsModel.confirmClaudeChangesOnCaption"] = SettingsModel.ConfirmClaudeChangesOnCaption;
        actual["SettingsModel.confirmClaudeChangesTitle"] = SettingsModel.ConfirmClaudeChangesTitle;
        actual["SettingsModel.confirmQuitTitle"] = SettingsModel.ConfirmQuitTitle;
        actual["SettingsModel.confirmRestartTitle"] = SettingsModel.ConfirmRestartTitle;
        actual["SettingsModel.confirmationsHeader"] = SettingsModel.ConfirmationsHeader;
        actual["SettingsModel.configPathLabel"] = SettingsModel.ConfigPathLabel;
        actual["SettingsModel.fewerBackupsLabel"] = SettingsModel.FewerBackupsLabel;
        actual["SettingsModel.generalTab"] = SettingsModel.GeneralTab;
        // Inline literals inside InstallKindText's switch expression - not named constants.
        actual["SettingsModel.installKindLegacyText"] = "Legacy installer";
        actual["SettingsModel.installKindMsixText"] = "MSIX package";
        actual["SettingsModel.keepCountHelpText"] = SettingsModel.KeepCountHelpText(3);
        actual["SettingsModel.launchAtLoginTitle"] = SettingsModel.LaunchAtStartupTitle;
        actual["SettingsModel.launchTargetLabel"] = SettingsModel.LaunchTargetLabel;
        actual["SettingsModel.loginItemFailureNote"] = SettingsModel.StartupEntryFailureNote("X");
        actual["SettingsModel.masterListFooter"] = SettingsModel.MasterListFooter;
        actual["SettingsModel.masterListHeader"] = SettingsModel.MasterListHeader;
        actual["SettingsModel.moreBackupsLabel"] = SettingsModel.MoreBackupsLabel;
        actual["SettingsModel.notificationsHeader"] = SettingsModel.NotificationsHeader;
        actual["SettingsModel.notifyCaption"] = SettingsModel.NotifyCaption;
        actual["SettingsModel.notifyTitle"] = SettingsModel.NotifyTitle;
        actual["SettingsModel.previewUpdatesCaption"] = SettingsModel.PreviewUpdatesCaption;
        actual["SettingsModel.restoreButtonTitle"] = SettingsModel.RestoreButtonTitle;
        actual["SettingsModel.revealInFinderTitle"] = SettingsModel.ShowInExplorerTitle;
        actual["SettingsModel.startupHeader"] = SettingsModel.StartupHeader;
        actual["SettingsModel.storageTab"] = SettingsModel.StorageTab;
        actual["SettingsModel.updatesHeader"] = SettingsModel.UpdatesHeader;
        actual["SettingsModel.useDefaultTitle"] = SettingsModel.UseDefaultTitle;
        actual["SettingsModel.versionText"] = SettingsModel.VersionTextFor("X");
        actual["SettingsModel.windowTitle"] = SettingsModel.WindowTitle;

        // MARK: ToolFamily / ToolInfo / ToolFooterLine / ToolNote

        actual["ToolFamily.nodeJS.installCommand"] = ToolInfo.InstallCommand(ToolFamily.NodeJs);
        actual["ToolFamily.nodeJS.linkTitle"] = ToolInfo.LinkTitle(ToolFamily.NodeJs);
        actual["ToolFamily.nodeJS.linkURL"] = ToolInfo.LinkUrl(ToolFamily.NodeJs);
        actual["ToolFamily.nodeJS.toolNames"] = ToolInfo.ToolNames(ToolFamily.NodeJs);
        actual["ToolFamily.uv.installCommand"] = ToolInfo.InstallCommand(ToolFamily.Uv);
        actual["ToolFamily.uv.linkTitle"] = ToolInfo.LinkTitle(ToolFamily.Uv);
        actual["ToolFamily.uv.linkURL"] = ToolInfo.LinkUrl(ToolFamily.Uv);
        actual["ToolFamily.uv.toolNames"] = ToolInfo.ToolNames(ToolFamily.Uv);
        actual["ToolFooterLine.installText"] = ToolFooterLine.InstallText("X", "Y", "Z");
        actual["ToolNote.checkingText"] = ToolNote.CheckingText;
        actual["ToolNote.foundText"] = ToolNote.FoundText;
        actual["ToolNote.missingText"] = ToolNote.MissingText(Tool.Npx);
        actual["ToolNote.notFoundText"] = ToolNote.NotFoundText;
        actual["ToolNote.orRun"] = ToolNote.OrRun;
        actual["ToolNote.rowMissingText"] = ToolNote.RowMissingText(Tool.Npx);
        actual["ToolNote.settingsCaption"] = ToolNote.SettingsCaption;
        actual["ToolNote.settingsHeader"] = ToolNote.SettingsHeader;

        // MARK: UpdateCoordinator

        actual["UpdateCoordinator.availableDetail"] = UpdateCoordinator.AvailableDetail("X", "Y");
        actual["UpdateCoordinator.availableHeadline"] = UpdateCoordinator.AvailableHeadline;
        actual["UpdateCoordinator.checkFailedMessage"] = UpdateCoordinator.CheckFailedMessage;
        actual["UpdateCoordinator.dialogTitle"] = UpdateCoordinator.DialogTitle;
        actual["UpdateCoordinator.downloadingTitle"] = UpdateCoordinator.DownloadingTitle;
        actual["UpdateCoordinator.installButton"] = UpdateCoordinator.InstallButton;
        actual["UpdateCoordinator.installFailedMessage"] = UpdateCoordinator.InstallFailedMessage;
        actual["UpdateCoordinator.laterButton"] = UpdateCoordinator.LaterButton;
        actual["UpdateCoordinator.readyToastBody"] = UpdateCoordinator.ReadyToastBody;
        actual["UpdateCoordinator.updateRefusedMessage"] = UpdateCoordinator.UpdateRefusedMessage;
        actual["UpdateCoordinator.upToDateDetail"] = UpdateCoordinator.UpToDateDetail("X");
        actual["UpdateCoordinator.upToDateMessage"] = UpdateCoordinator.UpToDateMessage;

        // MARK: UpdateRefusals

        actual["UpdateRefusals.cannotValidateSelf"] = UpdateRefusals.CannotValidateSelf;
        actual["UpdateRefusals.insideTheUpdate"] = UpdateRefusals.InsideTheUpdate("X");
        actual["UpdateRefusals.installedUpdater"] = UpdateRefusals.InstalledUpdater;
        actual["UpdateRefusals.invalidCharacterEntry"] = UpdateRefusals.InvalidCharacterEntry("X");
        actual["UpdateRefusals.locationUnknown"] = UpdateRefusals.LocationUnknown;
        actual["UpdateRefusals.mainExecutableTwice"] = UpdateRefusals.MainExecutableTwice;
        actual["UpdateRefusals.noMainExecutable"] = UpdateRefusals.NoMainExecutable;
        actual["UpdateRefusals.noVersion"] = UpdateRefusals.NoVersion;
        actual["UpdateRefusals.notValidlySigned"] = UpdateRefusals.NotValidlySigned("X");
        actual["UpdateRefusals.olderVersion"] = UpdateRefusals.OlderVersion("X", "Y");
        actual["UpdateRefusals.previewForRelease"] = UpdateRefusals.PreviewForRelease("X", "Y");
        actual["UpdateRefusals.renamedEntry"] = UpdateRefusals.RenamedEntry("X");
        actual["UpdateRefusals.signedByAnother"] = UpdateRefusals.SignedByAnother("X", "Y");
        actual["UpdateRefusals.signedByUntrusted"] = UpdateRefusals.SignedByUntrusted("X", "Y");
        actual["UpdateRefusals.sizeMismatch"] = UpdateRefusals.SizeMismatch("X", 3, 7);
        actual["UpdateRefusals.stubNotAProgram"] = UpdateRefusals.StubNotAProgram("X");
        actual["UpdateRefusals.unknownInstalledVersion"] = UpdateRefusals.UnknownInstalledVersion("X");
        actual["UpdateRefusals.unreadable"] = UpdateRefusals.Unreadable("X");
        actual["UpdateRefusals.updaterKeptSentence"] = UpdateRefusals.UpdaterKeptSentence;
        actual["UpdateRefusals.updaterLeftSentence"] = UpdateRefusals.UpdaterLeftSentence;
        actual["UpdateRefusals.versionMismatch"] = UpdateRefusals.VersionMismatch("X", "Y");

        // MARK: UserPath

        actual["UserPath.notTextError"] = UserPath.NotTextError;

        // MARK: - Compare every key against the fixture's Windows side

        var winKeys = new List<string>();
        foreach (var (key, node) in root)
        {
            var resolved = WinValue(node);
            if (resolved is null)
            {
                continue;
            }
            winKeys.Add(key);
            var (kind, text) = resolved.Value;
            var expected = kind == ValueKind.Format
                ? Expand(text, ArgsByKey.TryGetValue(key, out var keyArgs) ? keyArgs : [])
                : text;
            Assert.True(actual.ContainsKey(key), $"{key}: missing from `actual` in this test");
            Assert.Equal(expected, actual[key]);
        }

        // An unpaired key - present in `actual` but missing (or Mac-only) in the
        // fixture, or vice versa - fails here on whichever side lacks it.
        var actualKeys = actual.Keys.Order(StringComparer.Ordinal).ToList();
        winKeys.Sort(StringComparer.Ordinal);
        Assert.Equal(winKeys, actualKeys);
    }

    /// <summary>
    /// The comparison above only ever looks at keys WinValue resolves - a fixture entry resolving
    /// on neither platform (both "mac" and "win" omitted, or an unrecognized value shape) is
    /// silently skipped here AND by the Swift mirror's equivalent loop, so such a mistake would be
    /// asserted nowhere. This pins the fixture-wide invariant directly: every root key must
    /// resolve on at least one platform.
    /// </summary>
    [Fact]
    public void EveryKeyResolvesOnAtLeastOnePlatform()
    {
        var json = File.ReadAllText(Fixtures.Path("strings.json"));
        var root = JsonNode.Parse(json)!.AsObject();

        foreach (var (key, node) in root)
        {
            bool resolves;
            if (node is JsonTextValue leaf && leaf.TryGetValue(out string? _))
            {
                resolves = true;   // a bare literal, shared verbatim by both platforms
            }
            else if (node is JsonObject obj)
            {
                resolves = (obj.TryGetPropertyValue("format", out var format) && format is JsonTextValue formatLeaf && formatLeaf.TryGetValue(out string? _))
                    || (obj.TryGetPropertyValue("mac", out var mac) && mac is not null)
                    || (obj.TryGetPropertyValue("win", out var win) && win is not null);
            }
            else
            {
                resolves = false;
            }
            Assert.True(resolves, $"{key} resolves on neither platform");
        }
    }
}
