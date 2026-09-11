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
        ["AppState.deleteProfileMessage"] = ["X"],
        ["AppState.duplicateNameError"] = ["X"],
        ["AppState.enabledSubtitle"] = ["3", "7"],
        ["AppState.malformedConfigMessage"] = ["X"],
        ["ClaudeProcess.notAClaudePackageError"] = ["X"],
        ["ClaudePublisher.subjectNoOrganizationError"] = ["X", "Y"],
        ["ClaudeSignature.notFoundMessage"] = ["X"],
        ["ConfigService.corruptStoreNote"] = ["X"],
        ["ConfigService.invalidBackupError"] = ["X"],
        ["ConfigService.invalidBackupMcpServersError"] = ["X"],
        ["ConnectorRow.editTooltip"] = ["X"],
        ["EditTarget.editTitle"] = ["X"],
        ["EditorModel.additionalTitle"] = ["3", "a, b"],
        ["EditorModel.changedOutsideMessage"] = ["X"],
        ["EditorModel.cmdUnsafeError"] = ["X"],
        ["EditorModel.duplicateEnvError"] = ["X"],
        ["EditorModel.removeMessage"] = ["X"],
        ["EditorModel.removedOutsideMessage"] = ["X"],
        ["FlyoutModel.settingsNotSavedCaution"] = ["X"],
        ["MasterStore.duplicateProfileNameError"] = ["X"],
        ["MasterStore.unknownProfileError"] = ["X"],
        ["PopoverModel.deleteProfileTitle"] = ["X"],
        ["PopoverModel.profileChipText"] = ["X"],
        ["PopoverModel.renameProfileTitle"] = ["X"],
        ["RestoreModel.confirmMessage"] = ["X"],
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
        using var harness = new AppStateHarness(seedClaudeConfig: false);
        using var state = harness.Create();

        // MARK: AppState

        actual["AppState.chooseClaude"] = ClaudePublisher.ChooseClaude;
        actual["AppState.claudeConfigChangedBody"] = AppState.ClaudeConfigChangedBody;
        actual["AppState.claudeConfigRegeneratedBody"] = AppState.ClaudeConfigRegeneratedBody;
        actual["AppState.connectorListChangedBody.noRestart"] =
            AppState.ConnectorListChangedBody(new ServerDelta([], [], []), restartRequired: false);
        actual["AppState.connectorListChangedBody.restart"] =
            AppState.ConnectorListChangedBody(new ServerDelta([], [], []), restartRequired: true);
        actual["AppState.deleteButton"] = AppState.DeleteButton;
        actual["AppState.deleteProfileInformative"] = AppState.DeleteProfileInformative;
        actual["AppState.deleteProfileMessage"] = AppState.DeleteProfileMessage("X");
        actual["AppState.duplicateNameError"] = AppState.DuplicateNameError("X");
        actual["AppState.enabledSubtitle"] = AppState.EnabledSubtitle(3, 7);
        actual["AppState.malformedConfigMessage"] = AppState.MalformedConfigMessage("X");
        actual["AppState.nameEmptyError"] = AppState.NameEmptyError;
        actual["AppState.newProfileTitle"] = AppState.NewProfileTitle;
        actual["AppState.noConnectorsSubtitle"] = AppState.NoConnectorsSubtitle;
        actual["AppState.quitButton"] = AppState.QuitButton;
        actual["AppState.quitMessage"] = AppState.QuitMessage;
        actual["AppState.regenerationFailedBody"] = AppState.RegenerationFailedBody;
        actual["AppState.relaunchFailedMessage"] = AppState.RelaunchFailedMessage;
        actual["AppState.renameProfileTitle"] = AppState.RenameProfileTitle;
        actual["AppState.restartButton"] = AppState.RestartButton;
        actual["AppState.restartInformative"] = AppState.RestartInformative;
        actual["AppState.restartMessage"] = AppState.RestartMessage;
        actual["AppState.storeChangedBody"] = AppState.StoreChangedBody;

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

        // MARK: ConfigService
        // Every one of these is an inline literal built inside a throw or a notes list,
        // not a callable factory; pinned here to match ConfigService.cs and cross-checked
        // against ConfigServiceTests.cs's real-I/O assertions.

        actual["ConfigService.corruptStoreNote"] =
            "The MCP list file was unreadable; it was preserved as X and rebuilt from Claude's config.";
        actual["ConfigService.invalidBackupError"] = "backup X is not a valid config file";
        actual["ConfigService.invalidBackupMcpServersError"] = "backup X has an invalid mcpServers section";
        actual["ConfigService.malformedClaudeConfigNote"] =
            "Claude's config file is not valid JSON. Your MCP list is safe; use Backups ▸ Restore… to repair the file.";

        // MARK: ConnectorRow / Dialogs / AlertDialogs

        var row = new ConnectorRow(state, "X", true, null);
        actual["ConnectorRow.editTooltip"] = row.EditTooltip;
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
        actual["EditorModel.invalidURLError"] = EditorModel.InvalidUrlError;
        actual["EditorModel.jsonTip"] = EditorModel.JsonTip;
        actual["EditorModel.lossWarningPrefix"] = EditorModel.LossWarningPrefix;
        actual["EditorModel.notValidJSON"] = EditorModel.NotValidJson;
        actual["EditorModel.oauthSecretCaption"] = EditorModel.OAuthSecretCaption;
        actual["EditorModel.remoteFooter"] = EditorModel.RemoteFooter;
        actual["EditorModel.removeButton"] = EditorModel.RemoveButton;
        actual["EditorModel.removeInformative"] = EditorModel.RemoveInformative;
        actual["EditorModel.removeMessage"] = EditorModel.RemoveMessage("X");
        actual["EditorModel.removedOutsideDetail"] = EditorModel.RemovedOutsideDetail;
        actual["EditorModel.removedOutsideMessage"] = EditorModel.RemovedOutsideMessage("X");
        actual["EditorModel.saveAnywayButton"] = EditorModel.SaveAnywayButton;
        actual["EditorModel.stayInJSONButton"] = EditorModel.StayInJsonButton;
        actual["EditorModel.switchAnywayButton"] = EditorModel.SwitchAnywayButton;
        actual["EditorModel.urlHint"] = EditorModel.UrlHint;

        // MARK: FirstRunTip / FlyoutModel

        actual["FirstRunTip.body"] = FirstRunTip.Body;
        actual["FlyoutModel.settingsNotSavedCaution"] = FlyoutModel.SettingsNotSavedCaution("X");
        actual["FlyoutModel.storeNotPrivateCaution"] = FlyoutModel.StoreNotPrivateCaution;

        // MARK: MasterStore
        // Every message below is returned by a real mutation, not a bare constant —
        // each store is set up so that mutation fails for exactly the reason this key names.

        var nameEmptyStore = MasterStore.Empty();
        actual["MasterStore.nameEmptyError"] = nameEmptyStore.AddProfile("   ", false)!;
        var duplicateStore = new MasterStore(MasterStore.CurrentVersion, "X",
            [new KeyValuePair<string, Profile>("X", new Profile())]);
        actual["MasterStore.duplicateProfileNameError"] = duplicateStore.AddProfile("X", false)!;
        var deleteLastStore = MasterStore.Empty();
        actual["MasterStore.deleteLastProfileError"] = deleteLastStore.DeleteActiveProfile()!;
        var unknownProfileStore = MasterStore.Empty();
        actual["MasterStore.unknownProfileError"] = unknownProfileStore.SwitchProfile("X")!;

        // MARK: Notifications / PopoverModel (FlyoutModel on Windows)

        actual["Notifications.restartToastButton"] = Notifications.RestartToastButton;
        actual["Notifications.title"] = Notifications.Title;
        actual["PopoverModel.addTooltip"] = FlyoutModel.AddTooltip;
        actual["PopoverModel.deleteProfileTitle"] = FlyoutModel.DeleteProfileTitle("X");
        actual["PopoverModel.emptyText"] = FlyoutModel.EmptyText;
        actual["PopoverModel.newProfileTitle"] = FlyoutModel.NewProfileMenuItem;
        actual["PopoverModel.profileChipText"] = FlyoutModel.ProfileChipTextFor("X");
        actual["PopoverModel.quitTooltip"] = FlyoutModel.QuitTooltip;
        actual["PopoverModel.renameProfileTitle"] = FlyoutModel.RenameProfileTitle("X");
        actual["PopoverModel.restartTitle"] = FlyoutModel.RestartTitle;
        actual["PopoverModel.retryTitle"] = FlyoutModel.RetryTitle;
        actual["PopoverModel.settingsTooltip"] = FlyoutModel.SettingsTooltip;
        actual["PopoverModel.title"] = FlyoutModel.Title;

        // MARK: RemoteAuthKind

        actual["RemoteAuthKind.automatic.title"] = RemoteAuthKind.Automatic.Title();
        actual["RemoteAuthKind.bearer.title"] = RemoteAuthKind.Bearer.Title();
        actual["RemoteAuthKind.header.title"] = RemoteAuthKind.Header.Title();
        actual["RemoteAuthKind.oauthClient.title"] = RemoteAuthKind.OAuthClient.Title();

        // MARK: RestoreModel

        actual["RestoreModel.cancelTitle"] = RestoreModel.CancelTitle;
        actual["RestoreModel.caption"] = RestoreModel.Caption;
        actual["RestoreModel.confirmMessage"] = RestoreModel.ConfirmMessage("X");
        actual["RestoreModel.headline"] = RestoreModel.Headline;
        actual["RestoreModel.restoreButton"] = RestoreModel.RestoreButton;
        actual["RestoreModel.restoreTitle"] = RestoreModel.RestoreTitle;

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
        actual["SettingsModel.notifyCaption"] = SettingsModel.NotifyCaption;
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
}
