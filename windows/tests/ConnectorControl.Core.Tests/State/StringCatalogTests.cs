using System.Text.Json.Nodes;
using ConnectorControl.Core;
using ConnectorControl.Core.Services;
using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;
using JsonTextValue = System.Text.Json.Nodes.JsonValue;

namespace ConnectorControl.Core.Tests.State;

/// <summary>
/// Guards every user-facing string this app and the Mac app are meant to carry
/// byte-for-byte, against the shared fixture both suites read:
/// Tests/Fixtures/strings.json. Tests/ConnectorControlStateTests/StringCatalogTests.swift
/// is this test's mirror; a wording change on either side that the other side
/// does not also make fails exactly one of the two suites.
/// </summary>
public class StringCatalogTests
{
    private enum ValueKind { Plain, Format }

    /// <summary>
    /// This fixture entry's Windows-side resolution, or null when the key is
    /// Mac-only (no "win" side, and not a bare shared value) — such a key
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

    /// <summary>Substitutes {0}, {1}, … with <paramref name="args"/>, in order — the same convention the Swift test's fixture resolution uses.</summary>
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
    /// list of keys — matching the convention the Swift test also follows.
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
        ["AppState.malformedConfigMessage"] = ["X"],
        ["AppState.needsValueCaution"] = ["X"],
        ["AppState.keptPathCarriedError"] = ["X", "Y"],
        ["AppState.pathMarkMovedError"] = ["X"],
        ["AppState.publishFolderCarriedError"] = ["X", "Y"],
        ["AppState.publishSlugTakenError"] = ["X"],
        ["AppState.restoreCollectionGoneError"] = ["X"],
        ["AppState.sourceUnreadableError"] = ["X", "Y"],
        ["ClaudeProcess.notAClaudePackageError"] = ["X"],
        ["ClaudePublisher.subjectNoOrganizationError"] = ["X", "Y"],
        ["ClaudeSignature.notFoundMessage"] = ["X"],
        ["CollectionsModel.deletePublishedFileQuestion"] = ["X"],
        ["CollectionsModel.localDetail"] = ["3"],
        ["CollectionsModel.publishedDetail"] = ["X"],
        ["CollectionsModel.removeCheckedMessage.many"] = ["3"],
        ["CollectionsModel.removeCheckedMessage.one"] = ["X"],
        ["CollectionsModel.selectedCount"] = ["3"],
        ["CollectionsModel.stopSyncingMessage"] = ["X"],
        ["CollectionsModel.syncedDetail"] = ["X", "Y"],
        ["ConfigService.corruptStoreNote"] = ["X"],
        ["ConfigService.invalidBackupError"] = ["X", "Y"],
        ["ConfigService.invalidBackupMcpServersError"] = ["X"],
        ["CopyModel.title"] = ["X"],
        ["EditTarget.editTitle"] = ["X"],
        ["EditorModel.additionalTitle"] = ["3", "a, b"],
        ["EditorModel.changedOutsideMessage"] = ["X"],
        ["EditorModel.cmdUnsafeError"] = ["X"],
        ["EditorModel.duplicateEnvError"] = ["X"],
        ["EditorModel.importedNote"] = ["X", "Y"],
        ["EditorModel.lockedFieldsNote"] = ["X"],
        ["EditorModel.propagateLabel"] = ["X", "Y"],
        ["EditorModel.propagateLabelMany"] = ["X", "Y"],
        ["EditorModel.publishedNote"] = ["X"],
        ["EditorModel.removedOutsideMessage"] = ["X"],
        ["FlyoutModel.settingsNotSavedCaution"] = ["X"],
        ["FieldName.argument"] = ["1"],
        ["FieldName.document"] = ["X"],
        ["FieldName.envValue"] = ["X"],
        ["FieldName.hint"] = ["X"],
        ["ImportModel.addModeTitle"] = ["X"],
        ["ImportModel.collisionPickerLabel"] = ["X"],
        ["ImportModel.importButton"] = ["3"],
        ["ImportModel.includeLabel"] = ["X"],
        ["ImportModel.skippedBadge"] = ["X"],
        ["ImportModel.sourceLine"] = ["X", "Y", "3"],
        ["MasterStore.duplicateCollectionNameError"] = ["X"],
        ["MasterStore.unknownCollectionError"] = ["X"],
        ["PopoverModel.locateButton"] = ["X"],
        ["PopoverModel.sourceTooltipFormat"] = ["X"],
        ["PublishModel.exportTitle"] = ["X"],
        ["PublishModel.folderLine"] = ["X"],
        ["PublishModel.footerLine"] = ["X", "Y"],
        ["PublishModel.title"] = ["X"],
        ["PublishModel.keptPathNote"] = ["X", "Y"],
        ["PublishModel.otherFolderNote"] = ["X", "Y", "Z"],
        ["PublishModel.publishFolderEditNote"] = ["X", "Y"],
        ["PublishModel.publishFolderNote"] = ["X", "Y"],
        ["PublishModel.unresolvedMarkNote"] = ["X", "Y"],
        ["PublishModel.warningLine"] = ["X", "Y"],
        // The parameter here is a RemoteField, not free text, so the fixed argument is the
        // label one of them renders as rather than the usual "X".
        ["RemotePattern.cmdUnsafeReason"] = ["Server URL"],
        ["RestoreModel.confirmMessage"] = ["X"],
        ["ReviewModel.title"] = ["X"],
        ["SettingsModel.keepCountLabel"] = ["3"],
        ["SettingsModel.loginItemFailureNote"] = ["X"],
        ["SettingsModel.versionText"] = ["X"],
        ["UpdateCoordinator.availableDetail"] = ["X", "Y"],
        ["UpdateCoordinator.upToDateDetail"] = ["X"],
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
        actual["AppState.lastLocalCollectionError"] = AppState.LastLocalCollectionError;
        actual["AppState.locateCaution"] = AppState.LocateCaution;
        actual["AppState.malformedConfigMessage"] = AppState.MalformedConfigMessage("X");
        actual["AppState.nameEmptyError"] = AppState.NameEmptyError;
        actual["AppState.needsValueCaution"] = AppState.NeedsValueCaution("X");
        actual["AppState.newCollectionTitle"] = AppState.NewCollectionTitle;
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
        actual["AppState.sourceUnreadableError"] = AppState.SourceUnreadableError("X", "Y");
        actual["AppState.storeChangedBody"] = AppState.StoreChangedBody;
        actual["AppState.targetMustBeLocalError"] = AppState.TargetMustBeLocalError;
        actual["AppState.unpublishedDirectoryCaution"] = AppState.UnpublishedDirectoryCaution;

        // MARK: ClaudeConfigIO
        // Both messages are inline literals inside a throw, not exposed as callable
        // factories; pinned here to match ClaudeConfigIO.cs and cross-checked against
        // this project's own JSON-parsing test coverage.

        actual["ClaudeConfigIO.mcpServersNotObjectError"] = "mcpServers is not a JSON object";
        actual["ClaudeConfigIO.topLevelNotObjectError"] = "top level is not a JSON object";

        // MARK: ClaudeProcess / ClaudePublisher / ClaudeRestarter / ClaudeSignature
        // ClaudeProcess.cs lives in ConnectorControl.App, which this Core test project
        // cannot reference; its literals are pinned here by hand to match
        // windows/src/ConnectorControl.App/Services/ClaudeProcess.cs.

        actual["ClaudeProcess.notAClaudePackageError"] = "X is not a Claude Desktop package. " + ClaudePublisher.ChooseClaude;
        actual["ClaudeProcess.notInstalledMessage"] = "Claude Desktop was not found on this PC.";
        actual["ClaudeRestarter.didNotQuitMessage"] =
            "Claude didn’t quit (it may be showing a dialog). Quit it manually, then click Restart Claude again.";
        actual["ClaudeSignature.notFoundMessage"] = "Claude Desktop was not found at X.";
        // "Y" alone is not a parseable distinguished name, so SubjectProblem takes the
        // no-organization branch and echoes it back verbatim as the signer subject.
        actual["ClaudePublisher.subjectNoOrganizationError"] = ClaudePublisher.SubjectProblem("Y", "X")!;
        // "O=Y" parses with an organization ("y" normalized) that is not Anthropic's, so
        // SubjectProblem takes the not-Anthropic branch; the echoed subject is the raw "O=Y".
        actual["ClaudePublisher.subjectNotAnthropicError"] = ClaudePublisher.SubjectProblem("O=Y", "X")!;

        // MARK: CollectionsModel

        actual["CollectionsModel.activePill"] = CollectionsModel.ActivePill;
        actual["CollectionsModel.activeSuffix"] = CollectionsModel.ActiveSuffix;
        actual["CollectionsModel.addCollectionTooltip"] = CollectionsModel.AddCollectionTooltip;
        actual["CollectionsModel.addConnectorDisabledTooltip"] = CollectionsModel.AddConnectorDisabledTooltip;
        actual["CollectionsModel.addConnectorTooltip"] = CollectionsModel.AddConnectorTooltip;
        actual["CollectionsModel.connectorsHeader"] = CollectionsModel.ConnectorsHeader;
        actual["CollectionsModel.copyToButton"] = CollectionsModel.CopyToButton;
        actual["CollectionsModel.deleteAction"] = CollectionsModel.DeleteAction;
        actual["CollectionsModel.deletePublishedFileQuestion"] = CollectionsModel.DeletePublishedFileQuestion("X");
        actual["CollectionsModel.duplicateAction"] = CollectionsModel.DuplicateAction;
        actual["CollectionsModel.editTooltip"] = CollectionsModel.EditTooltip;
        actual["CollectionsModel.exportAllAction"] = CollectionsModel.ExportAllAction;
        actual["CollectionsModel.exportCheckedButton"] = CollectionsModel.ExportCheckedButton;
        actual["CollectionsModel.importButton"] = CollectionsModel.ImportButton;
        actual["CollectionsModel.importSubtitle"] = CollectionsModel.ImportSubtitle;
        actual["CollectionsModel.keepFileButton"] = CollectionsModel.KeepFileButton;
        actual["CollectionsModel.localDetail"] = CollectionsModel.LocalDetail(3);
        actual["CollectionsModel.lockedGlyphTooltip"] = CollectionsModel.LockedGlyphTooltip;
        actual["CollectionsModel.makeActiveAction"] = CollectionsModel.MakeActiveAction;
        actual["CollectionsModel.makeLocalCopyButton"] = CollectionsModel.MakeLocalCopyButton;
        actual["CollectionsModel.moreActionsLabel"] = CollectionsModel.MoreActionsLabel;
        actual["CollectionsModel.newButton"] = CollectionsModel.NewButton;
        actual["CollectionsModel.publishButton"] = CollectionsModel.PublishButton;
        actual["CollectionsModel.publishSettingsButton"] = CollectionsModel.PublishSettingsButton;
        actual["CollectionsModel.publishedDetail"] = CollectionsModel.PublishedDetail("X");
        actual["CollectionsModel.publishedPill"] = CollectionsModel.PublishedPill;
        actual["CollectionsModel.readOnlyNote"] = CollectionsModel.ReadOnlyNote;
        actual["CollectionsModel.refreshButton"] = CollectionsModel.RefreshButton;
        actual["CollectionsModel.remoteType"] = CollectionsModel.RemoteType;
        actual["CollectionsModel.removeCheckedButton"] = CollectionsModel.RemoveCheckedButton;
        actual["CollectionsModel.removeCheckedInformative"] = CollectionsModel.RemoveCheckedInformative;
        actual["CollectionsModel.removeCheckedMessage.many"] = CollectionsModel.RemoveCheckedMessage(["X", "Y", "Z"]);
        actual["CollectionsModel.removeCheckedMessage.one"] = CollectionsModel.RemoveCheckedMessage(["X"]);
        actual["CollectionsModel.removeFileButton"] = CollectionsModel.RemoveFileButton;
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
        actual["CollectionsModel.syncedDetail"] = CollectionsModel.SyncedDetail("X", "Y");
        actual["CollectionsModel.unlocatedDetail"] = CollectionsModel.UnlocatedDetail;
        actual["CollectionsModel.updateAvailableStatus"] = CollectionsModel.UpdateAvailableStatus;
        actual["CollectionsModel.upToDateStatus"] = CollectionsModel.UpToDateStatus;
        actual["CollectionsModel.windowTitle"] = CollectionsModel.WindowTitle;

        // MARK: CopyModel

        actual["CopyModel.copyButton"] = CopyModel.CopyButton;
        actual["CopyModel.title"] = CopyModel.Title("X");

        // MARK: ConfigService
        // Every one of these is an inline literal built inside a throw or a notes list,
        // not a callable factory; pinned here to match ConfigService.cs and cross-checked
        // against ConfigServiceTests.cs's real-I/O assertions.

        actual["ConfigService.corruptStoreNote"] =
            "The MCP list file was unreadable; it was preserved as X and rebuilt from Claude's config.";
        actual["ConfigService.invalidBackupError"] = "backup X is not a valid config file (Y)";
        actual["ConfigService.invalidBackupMcpServersError"] = "backup X has an invalid mcpServers section";
        actual["ConfigService.malformedClaudeConfigNote"] =
            "Claude's config file is not valid JSON. Your MCP list is safe; use Backups ▸ Restore to repair the file.";

        // MARK: Dialogs / AlertDialogs

        // IDialogs.Confirm's cancelTitle default parameter value — not a retrievable symbol.
        actual["Dialogs.cancelTitle"] = "Cancel";
        // WpfDialogs.cs / NamePromptDialog.xaml live in ConnectorControl.App, which this
        // Core test project cannot reference; hardcoded here to match their "OK" literal.
        actual["AlertDialogs.okTitle"] = "OK";

        // MARK: EditTarget / EditorModel

        actual["EditTarget.addTitle"] = EditTarget.AddTitle;
        actual["EditTarget.editTitle"] = EditTarget.EditTitle("X");
        actual["EditorModel.addArgumentTitle"] = EditorModel.AddArgumentTitle;
        actual["EditorModel.addVariableTitle"] = EditorModel.AddVariableTitle;
        actual["EditorModel.additionalTitle"] = EditorModel.AdditionalTitleFor(3, ["a", "b"]);
        actual["EditorModel.automaticCaption"] = EditorModel.AutomaticCaption;
        actual["EditorModel.bearerCaption"] = EditorModel.BearerCaption;
        actual["EditorModel.bearerTokenError"] = EditorModel.BearerTokenError;
        actual["EditorModel.changedOutsideDetail"] = EditorModel.ChangedOutsideDetail;
        actual["EditorModel.changedOutsideMessage"] = EditorModel.ChangedOutsideMessage("X");
        actual["EditorModel.clientIDError"] = EditorModel.ClientIdError;
        actual["EditorModel.cmdPercentCaution"] = EditorModel.CmdPercentCaution;
        actual["EditorModel.cmdUnsafeError"] = EditorModel.CmdUnsafeError("X");
        actual["EditorModel.commandError"] = EditorModel.CommandError;
        actual["EditorModel.duplicateEnvError"] = EditorModel.DuplicateEnvError("X");
        actual["EditorModel.envNamelessError"] = EditorModel.EnvNamelessError;
        actual["EditorModel.headerNameError"] = EditorModel.HeaderNameError;
        actual["EditorModel.headerValueError"] = EditorModel.HeaderValueError;
        actual["EditorModel.importedNote"] = EditorModel.ImportedNote("X", "Y");
        actual["EditorModel.invalidURLError"] = EditorModel.InvalidUrlError;
        actual["EditorModel.jsonTip"] = EditorModel.JsonTip;
        actual["EditorModel.lockedFieldsNote"] = EditorModel.LockedFieldsNote("X");
        actual["EditorModel.lossWarningPrefix"] = EditorModel.LossWarningPrefix;
        actual["EditorModel.needsPath"] = EditorModel.NeedsPath;
        actual["EditorModel.needsValue"] = EditorModel.NeedsValue;
        actual["EditorModel.notValidJSON"] = EditorModel.NotValidJson;
        actual["EditorModel.oauthSecretCaption"] = EditorModel.OAuthSecretCaption;
        actual["EditorModel.propagateLabel"] = EditorModel.PropagateLabel("X", "Y");
        actual["EditorModel.propagateLabelMany"] = EditorModel.PropagateLabelMany("X", "Y");
        actual["EditorModel.publishedNote"] = EditorModel.PublishedNote("X");
        actual["EditorModel.remoteFooter"] = EditorModel.RemoteFooter;
        actual["EditorModel.removedOutsideDetail"] = EditorModel.RemovedOutsideDetail;
        actual["EditorModel.removedOutsideMessage"] = EditorModel.RemovedOutsideMessage("X");
        actual["EditorModel.saveAnywayButton"] = EditorModel.SaveAnywayButton;
        actual["EditorModel.stayInJSONButton"] = EditorModel.StayInJsonButton;
        actual["EditorModel.switchAnywayButton"] = EditorModel.SwitchAnywayButton;
        actual["EditorModel.urlHint"] = EditorModel.UrlHint;
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
        // Every message below is returned by a real mutation, not a bare constant —
        // each store is set up so that mutation fails for exactly the reason this key names.

        var nameEmptyStore = MasterStore.Empty();
        actual["MasterStore.nameEmptyError"] = nameEmptyStore.AddCollection("   ", false)!;
        var duplicateStore = new MasterStore(MasterStore.CurrentVersion, "X",
            [new KeyValuePair<string, Collection>("X", new Collection())]);
        actual["MasterStore.duplicateCollectionNameError"] = duplicateStore.AddCollection("X", false)!;
        var deleteLastStore = MasterStore.Empty();
        actual["MasterStore.deleteLastCollectionError"] = deleteLastStore.DeleteCollection(deleteLastStore.ActiveCollection)!;
        var unknownCollectionStore = MasterStore.Empty();
        actual["MasterStore.unknownCollectionError"] = unknownCollectionStore.SwitchCollection("X")!;

        // MARK: Notifications / PopoverModel (FlyoutModel on Windows)

        actual["Notifications.restartToastButton"] = Notifications.RestartToastButton;
        actual["Notifications.title"] = Notifications.Title;
        actual["PopoverModel.chooseFolderButton"] = FlyoutModel.ChooseFolderButton;
        actual["PopoverModel.emptyText"] = FlyoutModel.EmptyText;
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
        actual["ReviewModel.removedLabel"] = ReviewModel.RemovedLabel;
        actual["ReviewModel.sourceMovedMessage"] = ReviewModel.SourceMovedMessage;
        actual["ReviewModel.title"] = ReviewModel.Title("X");

        // MARK: SettingsModel

        actual["SettingsModel.autoUpdateTitle"] = SettingsModel.AutoUpdateTitle;
        actual["SettingsModel.backupsCaption"] = SettingsModel.BackupsCaption;
        actual["SettingsModel.backupsHeader"] = SettingsModel.BackupsHeader;
        actual["SettingsModel.checkForUpdatesTitle"] = SettingsModel.CheckForUpdatesTitle;
        actual["SettingsModel.chooseTitle"] = SettingsModel.ChooseTitle;
        actual["SettingsModel.claudeAppHeader"] = SettingsModel.ClaudeAppHeader;
        actual["SettingsModel.claudeAppRejectedTitle"] = SettingsModel.LaunchTargetRejectedTitle;
        actual["SettingsModel.claudeTab"] = SettingsModel.ClaudeTab;
        actual["SettingsModel.confirmQuitTitle"] = SettingsModel.ConfirmQuitTitle;
        actual["SettingsModel.confirmRestartTitle"] = SettingsModel.ConfirmRestartTitle;
        actual["SettingsModel.configPathLabel"] = SettingsModel.ConfigPathLabel;
        actual["SettingsModel.generalTab"] = SettingsModel.GeneralTab;
        // Inline literals inside InstallKindText's switch expression — not named constants.
        actual["SettingsModel.installKindLegacyText"] = "Legacy installer";
        actual["SettingsModel.installKindMsixText"] = "MSIX package";
        actual["SettingsModel.keepCountLabel"] = SettingsModel.KeepCountLabelFor(3);
        actual["SettingsModel.launchAtLoginTitle"] = SettingsModel.LaunchAtStartupTitle;
        actual["SettingsModel.launchTargetLabel"] = SettingsModel.LaunchTargetLabel;
        actual["SettingsModel.loginItemFailureNote"] = SettingsModel.StartupEntryFailureNote("X");
        actual["SettingsModel.masterListHeader"] = SettingsModel.MasterListHeader;
        actual["SettingsModel.notifyTitle"] = SettingsModel.NotifyTitle;
        actual["SettingsModel.revealInFinderTitle"] = SettingsModel.ShowInExplorerTitle;
        actual["SettingsModel.storageTab"] = SettingsModel.StorageTab;
        actual["SettingsModel.updatesHeader"] = SettingsModel.UpdatesHeader;
        actual["SettingsModel.useDefaultTitle"] = SettingsModel.UseDefaultTitle;
        actual["SettingsModel.versionText"] = SettingsModel.VersionTextFor("X");

        // MARK: ToolFamily / ToolInfo / ToolNote

        actual["ToolFamily.nodeJS.installCommand"] = ToolInfo.InstallCommand(ToolFamily.NodeJs);
        actual["ToolFamily.nodeJS.linkTitle"] = ToolInfo.LinkTitle(ToolFamily.NodeJs);
        actual["ToolFamily.nodeJS.linkURL"] = ToolInfo.LinkUrl(ToolFamily.NodeJs);
        actual["ToolFamily.uv.installCommand"] = ToolInfo.InstallCommand(ToolFamily.Uv);
        actual["ToolFamily.uv.linkTitle"] = ToolInfo.LinkTitle(ToolFamily.Uv);
        actual["ToolFamily.uv.linkURL"] = ToolInfo.LinkUrl(ToolFamily.Uv);
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
        actual["UpdateCoordinator.installButton"] = UpdateCoordinator.InstallButton;
        actual["UpdateCoordinator.laterButton"] = UpdateCoordinator.LaterButton;
        actual["UpdateCoordinator.readyToastBody"] = UpdateCoordinator.ReadyToastBody;
        actual["UpdateCoordinator.updateRefusedMessage"] = UpdateCoordinator.UpdateRefusedMessage;
        actual["UpdateCoordinator.upToDateDetail"] = UpdateCoordinator.UpToDateDetail("X");
        actual["UpdateCoordinator.upToDateMessage"] = UpdateCoordinator.UpToDateMessage;

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

        // An unpaired key — present in `actual` but missing (or Mac-only) in the
        // fixture, or vice versa — fails here on whichever side lacks it.
        var actualKeys = actual.Keys.Order(StringComparer.Ordinal).ToList();
        winKeys.Sort(StringComparer.Ordinal);
        Assert.Equal(winKeys, actualKeys);
    }

    /// <summary>
    /// The comparison above only ever looks at keys WinValue resolves — a fixture entry resolving
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
