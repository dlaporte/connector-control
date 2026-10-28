import XCTest
import ConnectorControlCore
import ConnectorControlTestSupport
@testable import ConnectorControlState

/// Mirror: windows/tests/ConnectorControl.Core.Tests/State/ControlToolsSharingTests.cs
@MainActor
final class ControlToolsSharingTests: XCTestCase {
    private func rig(answer: Bool? = true) -> ControlRig {
        let rig = ControlRig()
        rig.confirmer.scripted = answer
        return rig
    }

    /// Nothing in `text` that a line would show as an escape: nothing that breaks a line, or
    /// reorders or hides text.
    private func assertPlain(_ text: String, file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertFalse(text.unicodeScalars.contains { ControlText.escape($0) != nil }, text, file: file, line: line)
    }

    /// A connector as the questions list it: its name in quotes, what becomes of it when that is
    /// said, then what it runs as the dialog shows it, indented under the name.
    private func listed(_ name: String, _ config: JSONValue?, _ detail: String? = nil) -> [String] {
        let quoted = "“" + ControlText.escapedName(name) + "”"
        return [detail.map { ControlTools.detail(quoted, $0) } ?? quoted]
            + (config.map { ControlMasking.codeLines($0, exposure: .dialog).map { "  " + $0 } } ?? [])
    }

    /// The Data team document's connectors, each with what it runs.
    private func dataTeamLines() -> [String] {
        let rendered = CollectionDocumentSamples.dataTeam.render()
        return ["dbt", "github", "ledger", "notion"].flatMap { listed($0, rendered.connectors[$0]?.config) }
    }

    /// A document's local connector running `command` with `args`.
    private func local(_ command: String, _ args: [String] = []) -> CollectionDocument.Connector {
        .init(launcher: .local(.init(command: command, args: args, platform: .mac)), env: [:], needs: [:], additional: [:])
    }

    /// A document whose one connector the masked view and the dialog show differently: an argument
    /// after a flag named for a secret, and an environment value.
    private let secretive = CollectionDocument(
        name: "Acme", author: nil, origin: "acme-origin", exported: "2026-10-01T00:00:00Z",
        connectors: ["acme": .init(launcher: .local(.init(command: "node", args: ["${COLLECTION_DIR}/server.js", "--api-key", "sk-live-abc123"], platform: .mac)),
                                   env: ["ACME_TOKEN": .value("literal-env-secret")], needs: [:], additional: [:])])

    // MARK: delete_collection

    func testDeletingACollectionAsksWhatTheWindowSaysAndSaysWhatBecomesActive() throws {
        let rig = rig(); defer { rig.dispose() }
        XCTAssertNil(rig.state.addEmptyCollection(named: "Work"))
        let window = rig.h.collectionsModel(rig.state).deleteInformative(for: "Default")
        let result = try XCTUnwrap(rig.call("delete_collection", ["collection": .string("Default")]))
        XCTAssertEqual(rig.confirmer.requests.last, ConfirmationRequest(
            title: ControlTools.deleteCollectionTitle("Claude", "Default"),
            message: [CollectionsModel.deleteConnectorsSentence(3), CollectionsModel.deleteCopiesSentence,
                      CollectionsModel.deleteNextActiveSentence("Work"), CollectionsModel.deleteCheckedInformative].joined(separator: " "),
            code: [], footer: ControlTools.takesEffectFooter))
        XCTAssertEqual(rig.confirmer.requests.last?.message, window, "the window's own Delete confirmation")
        XCTAssertEqual(result.text.components(separatedBy: "\n").first, ControlTools.deletedCollectionText("Default"))
        XCTAssertEqual(result.structured.value(at: JSONPointer(["activeCollection"])), .string("Work"))
        XCTAssertEqual(rig.state.activeCollection, "Work")
        XCTAssertNil(rig.state.store.collections["Default"])
    }

    func testTheLastLocalCollectionIsRefusedBeforeAsking() throws {
        let rig = rig(); defer { rig.dispose() }
        try rig.h.subscribe(rig.state, to: CollectionDocumentSamples.dataTeam)
        XCTAssertEqual(rig.call("delete_collection", ["collection": .string("Default")]), .refusal(AppState.lastLocalCollectionError))
        XCTAssertEqual(rig.call("delete_collection", ["collection": .string("Nope")]), .refusal(MasterStore.noCollectionError("Nope")))
        XCTAssertTrue(rig.confirmer.requests.isEmpty)
        XCTAssertNotNil(rig.state.store.collections["Default"])
    }

    func testDeletingASubscribedCollectionLeavesItsFile() throws {
        let rig = rig(); defer { rig.dispose() }
        let url = try rig.h.subscribe(rig.state, to: CollectionDocumentSamples.dataTeam)
        _ = rig.call("delete_collection", ["collection": .string("Data team")])
        XCTAssertEqual(rig.confirmer.requests.last?.message,
                       [CollectionsModel.deleteConnectorsSentence(4), CollectionsModel.deleteCopiesSentence,
                        CollectionsModel.deleteSourceSentence, CollectionsModel.deleteCheckedInformative].joined(separator: " "))
        XCTAssertNil(rig.confirmer.requests.last?.footer, "nothing Claude runs changes")
        XCTAssertNil(rig.state.store.collections["Data team"])
        XCTAssertTrue(FileManager.default.fileExists(atPath: url.path))
    }

    /// The window asks whether a published collection's file goes too; nothing here asks twice, so
    /// the file stays, and the question says so.
    func testDeletingAPublishedCollectionLeavesItsFileAndSaysSo() throws {
        let rig = rig(); defer { rig.dispose() }
        XCTAssertNil(rig.state.addEmptyCollection(named: "Work"))
        XCTAssertNil(rig.state.upsert(name: "tool", entry: AppStateHarness.localConnector("node"), renamedFrom: nil, in: "Work"))
        let published = try rig.h.publish(rig.state, "Work")
        _ = rig.call("delete_collection", ["collection": .string("Work")])
        XCTAssertEqual(rig.confirmer.requests.last?.message,
                       [CollectionsModel.deleteConnectorsSentence(1), CollectionsModel.deleteCopiesSentence,
                        ControlTools.publishedFileStaysText, CollectionsModel.deleteCheckedInformative].joined(separator: " "))
        XCTAssertNil(rig.state.store.collections["Work"])
        XCTAssertFalse(rig.state.isPublished("Work"))
        XCTAssertTrue(FileManager.default.fileExists(atPath: published.path))
    }

    // MARK: import_document

    func testImportingCopiesConnectorsInSwitchedOffAndShowsWhatEachRuns() throws {
        let rig = rig(); defer { rig.dispose() }
        let url = try rig.h.writeDocument(CollectionDocumentSamples.dataTeam, named: "team.json")
        let result = try XCTUnwrap(rig.call("import_document", ["path": .string(url.path)]))
        XCTAssertEqual(rig.confirmer.requests.last, ConfirmationRequest(
            title: ControlTools.importTitle("Claude", "team.json"),
            message: ControlTools.importMessage(4, "Default"),
            code: dataTeamLines(),
            footer: ControlTools.fromFooter(url.path)))
        let code = try XCTUnwrap(rig.confirmer.requests.last?.code)
        XCTAssertEqual(Array(code.drop(while: { $0 != "“ledger”" }).prefix(4)),
                       ["“ledger”", "  command   node", "  args      ${CC_NEEDS:server_path}", "  type      stdio"],
                       "a connector's name, then what it runs, indented under it")
        XCTAssertEqual(result.text, ControlTools.importedText(4, "Default"))
        XCTAssertEqual(rig.state.store.mcps["dbt"]?.enabled, false)
        XCTAssertNil(try rig.h.claudeServers()["dbt"], "copies arrive off")
    }

    func testImportingIntoANewCollectionAndChoosingWhatAClashDoes() throws {
        let rig = rig(); defer { rig.dispose() }
        let url = try rig.h.writeDocument(CollectionDocumentSamples.dataTeam, named: "team.json")
        let rendered = CollectionDocumentSamples.dataTeam.render()
        XCTAssertEqual(rig.call("import_document", ["path": .string(url.path), "newCollection": .string("Team")])?.isError, false)
        XCTAssertEqual(rig.confirmer.requests.last?.message, ControlTools.importNewMessage(4, "Team"))
        XCTAssertEqual(rig.state.store.collections["Team"]?.mcps.count, 4)
        XCTAssertEqual(rig.call("import_document", ["path": .string(url.path), "into": .string("Team"), "onClash": .string("keep-both")])?.text,
                       ControlTools.importedText(4, "Team"))
        XCTAssertEqual(Array(rig.confirmer.requests.last?.code.prefix(1) ?? []), [ControlTools.detail("“dbt”", ImportModel.keepBothTitle)])
        XCTAssertNotNil(rig.state.store.collections["Team"]?.mcps["dbt 2"])
        XCTAssertEqual(rig.call("import_document", ["path": .string(url.path), "into": .string("Team"), "onClash": .string("replace")])?.text,
                       ControlTools.importedText(4, "Team"))
        XCTAssertEqual(rig.confirmer.requests.last?.code,
                       ["dbt", "github", "ledger", "notion"].flatMap { listed($0, rendered.connectors[$0]?.config, ImportModel.replaceTitle) })
        XCTAssertEqual(rig.state.store.collections["Team"]?.mcps.count, 8, "replaced in place")
        XCTAssertEqual(rig.call("import_document", ["path": .string(url.path), "into": .string("Team")]),
                       .refusal(ControlTools.nothingToImportError("team.json")), "every name clashes and clashes are skipped")
        XCTAssertEqual(rig.confirmer.requests.count, 3)
    }

    /// A clash nothing is said about is skipped, and the question says which; the rest come across.
    func testAClashIsSkippedUnlessOnClashSaysOtherwise() throws {
        let rig = rig(); defer { rig.dispose() }
        let url = try rig.h.writeDocument(CollectionDocumentSamples.dataTeam, named: "team.json")
        let rendered = CollectionDocumentSamples.dataTeam.render()
        XCTAssertNil(rig.state.addEmptyCollection(named: "Team"))
        XCTAssertNil(rig.state.upsert(name: "dbt", entry: AppStateHarness.localConnector("mine"), renamedFrom: nil, in: "Team"))
        XCTAssertEqual(rig.call("import_document", ["path": .string(url.path), "into": .string("Team")])?.text,
                       ControlTools.importedText(3, "Team"))
        XCTAssertEqual(rig.confirmer.requests.last?.message, ControlTools.importMessage(3, "Team"))
        XCTAssertEqual(rig.confirmer.requests.last?.code,
                       listed("dbt", nil, ImportModel.presentBadge)
                       + ["github", "ledger", "notion"].flatMap { listed($0, rendered.connectors[$0]?.config) })
        XCTAssertEqual(rig.state.store.collections["Team"]?.mcps["dbt"]?.config, AppStateHarness.localConnector("mine").config)
    }

    func testImportRefusals() throws {
        let rig = rig(); defer { rig.dispose() }
        let url = try rig.h.writeDocument(CollectionDocumentSamples.dataTeam, named: "team.json")
        try rig.h.subscribe(rig.state, to: CollectionDocumentSamples.dataTeam, at: "other.json")
        XCTAssertEqual(rig.call("import_document", ["path": .string(url.path), "into": .string("Data team")]),
                       .refusal(AppState.targetMustBeLocalError))
        XCTAssertEqual(rig.call("import_document", ["path": .string(url.path), "into": .string("Nope")]),
                       .refusal(MasterStore.noCollectionError("Nope")))
        XCTAssertEqual(rig.call("import_document", ["path": .string(url.path), "newCollection": .string("Default")])?.isError, true)
        XCTAssertEqual(rig.call("import_document", ["path": .string(rig.h.dir.file("missing.json").path)])?.isError, true)
        XCTAssertEqual(rig.call("import_document", ["path": .string("")]), .refusal(ControlTools.missingArgumentError("path")))
        XCTAssertEqual(rig.confirmer.requests, [])
        XCTAssertNil(rig.state.store.collections["Default"]?.mcps["dbt"])
    }

    // MARK: subscribe

    func testSubscribingAsksThenFollowsTheFileReadOnlyWithEverythingOff() throws {
        let rig = rig(); defer { rig.dispose() }
        let url = try rig.h.writeDocument(CollectionDocumentSamples.dataTeam, named: "team.json")
        let result = try XCTUnwrap(rig.call("subscribe", ["path": .string(url.path)]))
        XCTAssertEqual(rig.confirmer.requests.last, ConfirmationRequest(
            title: ControlTools.subscribeTitle("Claude", "team.json"),
            message: ControlTools.subscribeMessage("Data team", 4),
            code: dataTeamLines(),
            footer: ControlTools.fromFooter(url.path)))
        XCTAssertEqual(result.text, ControlTools.subscribedText("Data team", "team.json") + "\n" + ControlTools.arrivedOffText(4))
        XCTAssertEqual(rig.state.kind(of: "Data team"), .synced)
        XCTAssertEqual(rig.state.store.collections["Data team"]?.mcps.values.filter(\.enabled).count, 0)
        XCTAssertEqual(rig.state.activeCollection, "Default")
    }

    /// A connector this platform will not carry is listed with the reason, as the Import sheet
    /// lists it, and does not count.
    func testAConnectorUnderTheReservedNameIsListedAsSkipped() throws {
        let rig = rig(); defer { rig.dispose() }
        var doc = CollectionDocumentSamples.dataTeam
        doc.connectors[ClaudeIntegration.entryName] = doc.connectors["github"]
        let url = try rig.h.writeDocument(doc, named: "team.json")
        _ = rig.call("subscribe", ["path": .string(url.path), "name": .string("Team")])
        XCTAssertEqual(rig.confirmer.requests.last?.message, ControlTools.subscribeMessage("Team", 4))
        let rendered = doc.render()
        XCTAssertEqual(rig.confirmer.requests.last?.code,
                       listed(ClaudeIntegration.entryName, nil, ImportModel.skippedBadge(ClaudeIntegration.reservedNameError))
                       + ["dbt", "github", "ledger", "notion"].flatMap { listed($0, rendered.connectors[$0]?.config) })
        XCTAssertNil(rig.state.store.collections["Team"]?.mcps[ClaudeIntegration.entryName])
    }

    // The Windows mirror also lists a remote connector the cmd /c launcher cannot carry as skipped,
    // with RemotePattern's reason; a Mac never writes that launcher, so there is no such test here.

    func testSubscribingToYourOwnPublishedCollectionOrATakenNameIsRefused() throws {
        let rig = rig(); defer { rig.dispose() }
        let published = try rig.h.publish(rig.state, "Default")
        XCTAssertTrue(FileManager.default.fileExists(atPath: published.path))
        XCTAssertEqual(rig.call("subscribe", ["path": .string(published.path)]), .refusal(AppState.ownCollectionError))
        let url = try rig.h.writeDocument(CollectionDocumentSamples.dataTeam, named: "team.json")
        XCTAssertEqual(rig.call("subscribe", ["path": .string(url.path), "name": .string("Default")])?.isError, true)
        XCTAssertEqual(rig.call("subscribe", ["path": .string("")]), .refusal(ControlTools.missingArgumentError("path")))
        XCTAssertTrue(rig.confirmer.requests.isEmpty)
    }

    // MARK: What the questions show

    /// Import and subscribe show what each connector would run, argument as written and
    /// `${COLLECTION_DIR}` as the document writes it, with environment values masked, where the
    /// masked view would hide the argument; the answers carry none of it.
    func testTheQuestionsShowWhatWouldRunAndTheAnswersNoSecret() throws {
        let rig = rig(); defer { rig.dispose() }
        let url = try rig.h.writeDocument(secretive, named: "acme.json")
        let imported = try XCTUnwrap(rig.call("import_document", ["path": .string(url.path)]))
        let subscribed = try XCTUnwrap(rig.call("subscribe", ["path": .string(url.path)]))
        let config = try XCTUnwrap(secretive.render().connectors["acme"]?.config)
        XCTAssertEqual(rig.confirmer.requests.count, 2)
        XCTAssertNotEqual(ControlMasking.codeLines(config, exposure: .dialog), ControlMasking.codeLines(config), "the masked view hides it")
        for request in rig.confirmer.requests {
            XCTAssertEqual(request.code, listed("acme", config))
            let code = request.code.joined(separator: "\n")
            XCTAssertTrue(code.contains("${COLLECTION_DIR}/server.js --api-key sk-live-abc123"), code)
            XCTAssertTrue(code.contains("ACME_TOKEN"), code)
            XCTAssertFalse(code.contains("literal-env-secret"), code)
        }
        for result in [imported, subscribed] {
            XCTAssertFalse(result.isError)
            for secret in ["sk-live-abc123", "literal-env-secret"] {
                XCTAssertFalse(result.text.contains(secret), result.text)
                XCTAssertFalse(result.structured.compactLine().contains(secret), result.structured.compactLine())
            }
        }
    }

    // MARK: apply_update

    func testApplyingAnUpdateAsksWithWhatItChanges() throws {
        let rig = rig(); defer { rig.dispose() }
        let url = try rig.h.subscribe(rig.state, to: CollectionDocumentSamples.dataTeam)
        XCTAssertEqual(rig.call("apply_update", ["collection": .string("Data team")]), .refusal(ControlTools.noUpdateError("Data team")))
        let before = try XCTUnwrap(rig.state.store.collections["Data team"]?.mcps["dbt"]).config
        var doc = CollectionDocumentSamples.dataTeam
        doc.connectors["github"] = nil
        doc.connectors["dbt"] = local("npx", ["-y", "@dbt/mcp@2"])
        try rig.h.writeDocument(doc, at: url)
        rig.state.refreshSource(for: "Data team")
        let after = try XCTUnwrap(doc.render().connectors["dbt"]).config
        let result = try XCTUnwrap(rig.call("apply_update", ["collection": .string("Data team")]))
        XCTAssertEqual(rig.confirmer.requests.last, ConfirmationRequest(
            title: ControlTools.applyUpdateTitle("Claude", "Data team"),
            message: AppState.collectionUpdateBanner("Data team", "deletes github; changes dbt"),
            code: listed("github", nil, ReviewModel.deletedLabel) + listed("dbt", nil, ReviewModel.changedLabel)
                + ControlMasking.changeLines(from: before, to: after, exposure: .dialog).map { "  " + $0 },
            footer: nil))
        XCTAssertEqual(result.text, ControlTools.appliedUpdateText("Data team"))
        XCTAssertNil(rig.state.store.collections["Data team"]?.mcps["github"])
        XCTAssertEqual(rig.call("apply_update", ["collection": .string("Data team")]), .refusal(ControlTools.noUpdateError("Data team")))
        XCTAssertEqual(rig.call("apply_update", ["collection": .string("Default")]), .refusal(ControlTools.notSubscribedError("Default")))
        XCTAssertEqual(rig.call("apply_update", ["collection": .string("Nope")]), .refusal(MasterStore.noCollectionError("Nope")))
        XCTAssertEqual(rig.confirmer.requests.count, 1)
    }

    /// What the user allowed is what lands: a document that moves under the question asks a
    /// different one, so the Allow applies nothing.
    func testAnUpdateThatMovedWhileAskingIsNotApplied() throws {
        let rig = rig(answer: nil); defer { rig.dispose() }
        let url = try rig.h.subscribe(rig.state, to: CollectionDocumentSamples.dataTeam)
        var doc = CollectionDocumentSamples.dataTeam
        doc.connectors["dbt"] = local("npx", ["-y", "@dbt/mcp@2"])
        try rig.h.writeDocument(doc, at: url)
        rig.state.refreshSource(for: "Data team")
        let answer = rig.start("apply_update", ["collection": .string("Data team")])
        doc.connectors["dbt"] = local("npx", ["-y", "@dbt/mcp@3"])
        try rig.h.writeDocument(doc, at: url)
        rig.state.refreshSource(for: "Data team")
        rig.confirmer.answer(true)
        rig.h.drain()
        XCTAssertEqual(answer.result, .refusal(ControlTools.changedWhileAskingError))
        XCTAssertNotEqual(rig.state.store.collections["Data team"]?.mcps["dbt"]?.config, doc.render().connectors["dbt"]?.config)
    }

    // MARK: restore_backup

    func testRestoringABackupByTheIdTheListGave() throws {
        let rig = rig(); defer { rig.dispose() }
        let id = try XCTUnwrap(rig.state.service.backups.backups(series: RestoreModel.series).first?.lastPathComponent)
        XCTAssertNil(rig.state.upsert(name: "extra", entry: MCPEntry(config: .object(["command": .string("x")])), renamedFrom: nil))
        rig.state.applyInteractively()
        let result = try XCTUnwrap(rig.call("restore_backup", ["id": .string(id)]))
        XCTAssertEqual(rig.confirmer.requests.last, ConfirmationRequest(
            title: ControlTools.restoreTitle("Claude", id),
            message: RestoreModel.caption + " " + ControlTools.restoreIntoText("Default"),
            code: ["“aws-mcp”", "“scoutbook”", "“service-now”"],
            footer: ControlTools.takesEffectFooter))
        XCTAssertEqual(result.text.components(separatedBy: "\n").first, ControlTools.restoredText(id))
        XCTAssertNil(try rig.h.claudeServers()["extra"], "Claude's config is the backup again")
        XCTAssertEqual(rig.call("restore_backup", ["id": .string("../../etc/passwd")]),
                       .refusal(ControlTools.noBackupError("../../etc/passwd")), "only an id the list gives")
        XCTAssertEqual(rig.confirmer.requests.count, 1)
    }

    /// A backup the Restore sheet would refuse is refused before anyone is asked, as is one that
    /// is not a config file.
    func testARestoreTheSheetWouldRefuseIsRefusedBeforeAsking() throws {
        let rig = rig(); defer { rig.dispose() }
        let broken = "claude_desktop_config.zz-broken.json"
        try Data("{".utf8).write(to: rig.h.backupsDir.appendingPathComponent(broken))
        XCTAssertEqual(rig.call("restore_backup", ["id": .string(broken)])?.isError, true)
        // A backup with no record of its collection goes into the active one, which is refused
        // while that one is subscribed.
        let backup = try XCTUnwrap(rig.state.service.backups.backups(series: RestoreModel.series).first)
        let unrecorded = "claude_desktop_config.zz-unrecorded.json"
        try FileManager.default.copyItem(at: backup, to: rig.h.backupsDir.appendingPathComponent(unrecorded))
        try rig.h.subscribe(rig.state, to: CollectionDocumentSamples.dataTeam)
        rig.state.switchCollection(to: "Data team")
        XCTAssertEqual(rig.call("restore_backup", ["id": .string(unrecorded)]), .refusal(AppState.restoreSubscribedError("Data team")))
        XCTAssertTrue(rig.confirmer.requests.isEmpty)
        XCTAssertEqual(rig.state.activeCollection, "Data team")
    }

    // MARK: restart_claude

    func testARestartFromClaudeAlwaysAsksAndFromTheCommandLineNever() {
        let rig = ControlRig(confirm: false); defer { rig.dispose() }
        rig.confirmer.scripted = false
        XCTAssertEqual(rig.call("restart_claude"), .refusal(ControlTools.declinedError), "asked although confirmations are off")
        XCTAssertEqual(rig.confirmer.requests.last, ConfirmationRequest(
            title: ControlTools.restartItselfTitle, message: ControlTools.restartMessage, code: [], footer: nil))
        XCTAssertEqual(rig.h.claude.restartCalls, 0)
        XCTAssertEqual(rig.call("restart_claude", as: .cli)?.text, ControlTools.restartingText)
        XCTAssertEqual(rig.h.claude.restartCalls, 1)
        XCTAssertEqual(rig.confirmer.requests.count, 1)
        rig.confirmer.scripted = true
        _ = rig.call("restart_claude", as: .relay(clientName: "Cursor"))
        XCTAssertEqual(rig.confirmer.requests.last?.title, ControlTools.restartTitle("Cursor"))
        XCTAssertEqual(rig.h.claude.restartCalls, 2)
        XCTAssertTrue(rig.h.notifier.sent.isEmpty, "a restart is asked about, never announced")
    }

    /// The gate already asked, so the app's own "confirm before restarting" question does not ask
    /// a second time; the command line's typed command is its confirmation.
    func testTheAppsOwnRestartQuestionDoesNotAskAgain() {
        let rig = rig(); defer { rig.dispose() }
        rig.h.settings.confirmBeforeRestart = true
        rig.h.dialogs.nextConfirm = false
        _ = rig.call("restart_claude")
        _ = rig.call("restart_claude", as: .cli)
        XCTAssertEqual(rig.h.claude.restartCalls, 2)
        XCTAssertEqual(rig.h.dialogs.confirms, [])
    }

    // MARK: Names

    /// Every name these questions and answers carry is chosen by someone else: the asker by the
    /// client, a collection's by Claude or a document's author, a file's by whoever wrote it, a
    /// connector's by the author or Claude. Each shows as an escape what would break a line, reorder
    /// or hide text, or close its quotes.
    func testNamesCannotForgeTheseQuestionsOrTheirAnswers() throws {
        let rig = rig(); defer { rig.dispose() }
        let caller = ControlCaller.relay(clientName: "Cursor\n\u{202E}d"), shownAsker = #"Cursor\n\u{202E}d"#
        let collection = "Work\n\u{202E}z", shownCollection = #"Work\n\u{202E}z"#
        let connector = "a\nb\u{202E}c", shownConnector = #"a\nb\u{202E}c"#
        // Neither platform takes a line break in a file name; both take these.
        let file = "t\u{202E}\u{201D}.json", shownFile = #"t\u{202E}\u{201D}.json"#
        let team = "Team\n\u{202E}y", shownTeam = #"Team\n\u{202E}y"#
        let config = AppStateHarness.localConnector("node").config
        var doc = CollectionDocument(name: team, author: nil, origin: "hostile-origin", exported: "2026-10-01T00:00:00Z",
                                     connectors: [connector: local("node"), "keep": local("node", ["k"])])
        let url = try rig.h.writeDocument(doc, named: file)
        XCTAssertNil(rig.state.addEmptyCollection(named: collection))
        XCTAssertNil(rig.state.upsert(name: connector, entry: MCPEntry(config: config), renamedFrom: nil))
        rig.state.applyInteractively()
        XCTAssertNil(rig.state.upsert(name: "later", entry: MCPEntry(config: config), renamedFrom: nil))
        rig.state.applyInteractively()
        let backup = try XCTUnwrap(rig.state.service.backups.backups(series: RestoreModel.series).first)
        let id = "claude_desktop_config.\u{202E}\u{201D}.json", shownID = #"claude_desktop_config.\u{202E}\u{201D}.json"#
        try FileManager.default.copyItem(at: backup, to: rig.h.backupsDir.appendingPathComponent(id))

        var results = [rig.call("import_document", ["path": .string(url.path), "into": .string(collection)], as: caller)]
        results.append(rig.call("subscribe", ["path": .string(url.path)], as: caller))
        doc.connectors[connector] = nil
        try rig.h.writeDocument(doc, at: url)
        rig.state.refreshSource(for: team)
        results.append(rig.call("apply_update", ["collection": .string(team)], as: caller))
        results.append(rig.call("delete_collection", ["collection": .string(collection)], as: caller))
        results.append(rig.call("restore_backup", ["id": .string(id)], as: caller))
        results.append(rig.call("restart_claude", as: caller))

        let requests = rig.confirmer.requests
        XCTAssertEqual(requests.map(\.title), [
            ControlTools.importTitle(shownAsker, shownFile), ControlTools.subscribeTitle(shownAsker, shownFile),
            ControlTools.applyUpdateTitle(shownAsker, shownTeam), ControlTools.deleteCollectionTitle(shownAsker, shownCollection),
            ControlTools.restoreTitle(shownAsker, shownID), ControlTools.restartTitle(shownAsker),
        ])
        XCTAssertEqual(requests.prefix(3).map(\.message), [
            ControlTools.importMessage(2, shownCollection), ControlTools.subscribeMessage(shownTeam, 2),
            AppState.collectionUpdateBanner(shownTeam, "deletes " + shownConnector),
        ])
        XCTAssertEqual(requests.first?.code.first, "“" + shownConnector + "”")
        XCTAssertEqual(requests.dropFirst(2).first?.code, ["“" + shownConnector + "” · " + ReviewModel.deletedLabel])
        XCTAssertEqual(requests.first?.footer, ControlTools.fromFooter(ControlText.escaped(url.path)))
        XCTAssertTrue(requests.dropFirst(4).first?.code.contains("“" + shownConnector + "”") == true, "the backup's connectors")
        XCTAssertEqual(results.prefix(4).map { $0?.text.components(separatedBy: "\n").first }, [
            ControlTools.importedText(2, shownCollection), ControlTools.subscribedText(shownTeam, shownFile),
            ControlTools.appliedUpdateText(shownTeam), ControlTools.deletedCollectionText(shownCollection),
        ])
        XCTAssertEqual(results.last??.text, ControlTools.restartingText)
        for request in requests {
            for text in [request.title, request.message, request.footer ?? ""] + request.code { assertPlain(text) }
        }
        for result in results {
            XCTAssertEqual(result?.isError, false, result?.text ?? "")
            for line in (result?.text ?? "").split(separator: "\n") { assertPlain(String(line)) }
        }
    }

    /// The announcements, for changes nobody was asked about, name everything as the questions do.
    func testAnAnnouncementShowsNamesAsEscapes() throws {
        let rig = ControlRig(confirm: false); defer { rig.dispose() }
        let caller = ControlCaller.relay(clientName: "Cursor\n\u{202E}d"), shownAsker = #"Cursor\n\u{202E}d"#
        let collection = "Work\n\u{202E}z", shownCollection = #"Work\n\u{202E}z"#
        let file = "t\u{202E}\u{201D}.json", shownFile = #"t\u{202E}\u{201D}.json"#
        let team = "Team\n\u{202E}y", shownTeam = #"Team\n\u{202E}y"#
        var doc = CollectionDocument(name: team, author: nil, origin: "hostile-origin", exported: "2026-10-01T00:00:00Z",
                                     connectors: ["one": local("node")])
        let url = try rig.h.writeDocument(doc, named: file)
        let id = try XCTUnwrap(rig.state.service.backups.backups(series: RestoreModel.series).first?.lastPathComponent)
        XCTAssertNil(rig.state.addEmptyCollection(named: collection))
        _ = rig.call("import_document", ["path": .string(url.path), "into": .string(collection)], as: caller)
        _ = rig.call("subscribe", ["path": .string(url.path)], as: caller)
        XCTAssertEqual(rig.h.notifier.sent.map(\.body), [
            ControlTools.importedNotification(shownAsker, 1, shownCollection),
            ControlTools.subscribedNotification(shownAsker, shownFile, shownTeam),
        ])
        doc.connectors["two"] = doc.connectors["one"]
        try rig.h.writeDocument(doc, at: url)
        rig.state.refreshSource(for: team)
        // The update's own notice, which is the app's, not an announcement.
        rig.h.notifier.clearSent()
        _ = rig.call("apply_update", ["collection": .string(team)], as: caller)
        _ = rig.call("delete_collection", ["collection": .string(collection)], as: caller)
        _ = rig.call("restore_backup", ["id": .string(id)], as: caller)
        XCTAssertEqual(rig.h.notifier.sent.map(\.body), [
            ControlTools.appliedUpdateNotification(shownAsker, shownTeam),
            ControlTools.deletedCollectionNotification(shownAsker, shownCollection),
            ControlTools.restoredNotification(shownAsker, id),
        ])
        for body in rig.h.notifier.sent.map(\.body) { assertPlain(body) }
        XCTAssertTrue(rig.confirmer.requests.isEmpty)
    }
}
