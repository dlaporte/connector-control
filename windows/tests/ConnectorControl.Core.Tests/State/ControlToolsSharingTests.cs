using ConnectorControl.Core.State;
using ConnectorControl.Core.Tests.TestSupport;

namespace ConnectorControl.Core.Tests.State;

/// <summary>Mirror: Tests/ConnectorControlStateTests/ControlToolsSharingTests.swift</summary>
public class ControlToolsSharingTests
{
    private static JsonValue Args(params (string Key, JsonValue Value)[] arguments) => JsonValue.Object(arguments);

    private static JsonValue Text(string value) => JsonValue.String(value);

    private static ControlRig Rig(bool? answer = true)
    {
        var rig = new ControlRig();
        rig.Confirmer.Scripted = answer;
        return rig;
    }

    /// <summary>Runs everything the host delayed, as time passing would: a restart due, and the tick a
    /// question answered at once leaves behind, which finds its call answered and does nothing.</summary>
    private static void RunDelays(ControlRig rig)
    {
        while (rig.H.Delays.Pending.Count > 0)
        {
            rig.H.Delays.RunNext();
        }
        rig.H.Drain();
    }

    /// <summary>Nothing in <paramref name="text"/> that a line would show as an escape: nothing that breaks a
    /// line, or reorders or hides text.</summary>
    private static void AssertPlain(string text) => Assert.DoesNotContain(text, c => ControlText.Escape(c) is not null);

    /// <summary>A connector as the questions list it: its name in quotes, what becomes of it when that is
    /// said, then what it runs as the dialog shows it, indented under the name.</summary>
    private static List<string> Listed(string name, JsonValue? config, string? detail = null)
    {
        var quoted = "“" + ControlText.EscapedName(name) + "”";
        var lines = new List<string> { detail is null ? quoted : ControlTools.Detail(quoted, detail) };
        if (config is not null)
        {
            lines.AddRange(ControlMasking.CodeLines(config, ControlMasking.Exposure.Dialog).Select(line => "  " + line));
        }
        return lines;
    }

    /// <summary>The Data team document's connectors, each with what it runs.</summary>
    private static List<string> DataTeamLines()
    {
        var rendered = CollectionDocumentSamples.DataTeam.Render();
        return new[] { "dbt", "github", "ledger", "notion" }.SelectMany(name => Listed(name, rendered.Connectors[name].Config)).ToList();
    }

    /// <summary>A document's local connector running <paramref name="command"/> with <paramref name="args"/>.</summary>
    private static CollectionDocument.Connector Local(string command, params string[] args) =>
        new(new CollectionDocument.Launcher.Local(command, args, CollectionPlatform.Mac),
            new Dictionary<string, CollectionDocument.EnvValue>(), new Dictionary<string, string?>(), new Dictionary<string, JsonValue>());

    /// <summary><paramref name="d"/> with <paramref name="name"/> set to <paramref name="connector"/>, or
    /// taken out when it is null: the Mac mirror sets the document's connectors in place.</summary>
    private static CollectionDocument With(CollectionDocument d, string name, CollectionDocument.Connector? connector)
    {
        var connectors = new Dictionary<string, CollectionDocument.Connector>(d.Connectors, StringComparer.Ordinal);
        if (connector is null)
        {
            connectors.Remove(name);
        }
        else
        {
            connectors[name] = connector;
        }
        return new CollectionDocument(d.Name, d.Author, d.Origin, d.Exported, connectors);
    }

    /// <summary>A document whose one connector the masked view and the dialog show differently: an argument
    /// after a flag named for a secret, and an environment value.</summary>
    private static readonly CollectionDocument Secretive = new(
        "Acme", null, "acme-origin", "2026-10-01T00:00:00Z",
        new Dictionary<string, CollectionDocument.Connector>
        {
            ["acme"] = new(
                new CollectionDocument.Launcher.Local("node", ["${COLLECTION_DIR}/server.js", "--api-key", "sk-live-abc123"], CollectionPlatform.Mac),
                new Dictionary<string, CollectionDocument.EnvValue> { ["ACME_TOKEN"] = new CollectionDocument.EnvValue.Value("literal-env-secret") },
                new Dictionary<string, string?>(), new Dictionary<string, JsonValue>()),
        });

    /// <summary>A document whose one connector holds <paramref name="value"/> where the dialog shows only
    /// that it is set.</summary>
    private static CollectionDocument Masked(string value) => new(
        "Acme", null, "acme-origin", "2026-10-01T00:00:00Z",
        new Dictionary<string, CollectionDocument.Connector>
        {
            ["acme"] = new(new CollectionDocument.Launcher.Local("node", [], CollectionPlatform.Mac),
                new Dictionary<string, CollectionDocument.EnvValue> { ["ACME_TOKEN"] = new CollectionDocument.EnvValue.Value(value) },
                new Dictionary<string, string?>(), new Dictionary<string, JsonValue>()),
        });

    // MARK: delete_collection

    [Fact]
    public void DeletingACollectionAsksWhatTheWindowSaysAndSaysWhatBecomesActive()
    {
        using var rig = Rig();
        Assert.Null(rig.State.AddEmptyCollection("Work"));
        Assert.Null(rig.State.Upsert("tool", AppStateHarness.LocalConnector("node"), null, "Work"));
        var window = rig.H.CollectionsModel(rig.State).DeleteInformative("Default");
        var result = rig.Call("delete_collection", Args(("collection", Text("Default"))));
        Assert.NotNull(result);
        Assert.Equal(new ConfirmationRequest(
            ControlTools.DeleteCollectionTitle("Claude", "Default"),
            string.Join(" ", CollectionsModel.DeleteConnectorsSentence(3), CollectionsModel.DeleteCopiesSentence,
                CollectionsModel.DeleteNextActiveSentence("Work"), CollectionsModel.DeleteCheckedInformative),
            [], ControlTools.TakesEffectFooter), rig.Confirmer.Requests[^1]);
        // The window's own Delete confirmation.
        Assert.Equal(window, rig.Confirmer.Requests[^1].Message);
        Assert.Equal(ControlTools.DeletedCollectionText("Default"), result.Text.Split('\n')[0]);
        Assert.Equal(JsonValue.String("Work"), result.Structured.ValueAt(new JsonPointer(["activeCollection"])));
        Assert.Equal("Work", rig.State.ActiveCollection);
        Assert.False(rig.State.Store.Collections.ContainsKey("Default"));
        // Claude's config follows the collection that became active.
        Assert.Equal([ClaudeIntegration.EntryName, "tool"], rig.H.ClaudeServers().Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheLastLocalCollectionIsRefusedBeforeAsking()
    {
        using var rig = Rig();
        rig.H.Subscribe(rig.State, CollectionDocumentSamples.DataTeam);
        Assert.Equal(ControlResult.Refusal(AppState.LastLocalCollectionError), rig.Call("delete_collection", Args(("collection", Text("Default")))));
        Assert.Equal(ControlResult.Refusal(MasterStore.NoCollectionError("Nope")), rig.Call("delete_collection", Args(("collection", Text("Nope")))));
        Assert.Empty(rig.Confirmer.Requests);
        Assert.True(rig.State.Store.Collections.ContainsKey("Default"));
    }

    [Fact]
    public void DeletingASubscribedCollectionLeavesItsFile()
    {
        using var rig = Rig();
        var path = rig.H.Subscribe(rig.State, CollectionDocumentSamples.DataTeam);
        rig.Call("delete_collection", Args(("collection", Text("Data team"))));
        Assert.Equal(string.Join(" ", CollectionsModel.DeleteConnectorsSentence(4), CollectionsModel.DeleteCopiesSentence,
                CollectionsModel.DeleteSourceSentence, CollectionsModel.DeleteCheckedInformative),
            rig.Confirmer.Requests[^1].Message);
        // Nothing Claude runs changes.
        Assert.Null(rig.Confirmer.Requests[^1].Footer);
        Assert.False(rig.State.Store.Collections.ContainsKey("Data team"));
        Assert.True(File.Exists(path));
    }

    /// <summary>The window asks whether a published collection's file goes too; nothing here asks twice, so
    /// the file stays, and the question says so.</summary>
    [Fact]
    public void DeletingAPublishedCollectionLeavesItsFileAndSaysSo()
    {
        using var rig = Rig();
        Assert.Null(rig.State.AddEmptyCollection("Work"));
        Assert.Null(rig.State.Upsert("tool", AppStateHarness.LocalConnector("node"), null, "Work"));
        var published = rig.H.Publish(rig.State, "Work");
        rig.Call("delete_collection", Args(("collection", Text("Work"))));
        Assert.Equal(string.Join(" ", CollectionsModel.DeleteConnectorsSentence(1), CollectionsModel.DeleteCopiesSentence,
                ControlTools.PublishedFileStaysText, CollectionsModel.DeleteCheckedInformative),
            rig.Confirmer.Requests[^1].Message);
        Assert.False(rig.State.Store.Collections.ContainsKey("Work"));
        Assert.False(rig.State.IsPublished("Work"));
        Assert.True(File.Exists(published));
    }

    // MARK: import_document

    [Fact]
    public void ImportingCopiesConnectorsInSwitchedOffAndShowsWhatEachRuns()
    {
        using var rig = Rig();
        var path = rig.H.WriteDocument(CollectionDocumentSamples.DataTeam, "team.json");
        var result = rig.Call("import_document", Args(("path", Text(path))));
        Assert.NotNull(result);
        Assert.Equal(new ConfirmationRequest(
            ControlTools.ImportTitle("Claude", "team.json"),
            ControlTools.ImportMessage(4, "Default"),
            DataTeamLines(),
            ControlTools.FromFooter(path)), rig.Confirmer.Requests[^1]);
        // A connector's name, then what it runs, indented under it.
        Assert.Equal(["“ledger”", "  command   node", "  args      ${CC_NEEDS:server_path}", "  type      stdio"],
            rig.Confirmer.Requests[^1].Code.SkipWhile(line => line != "“ledger”").Take(4));
        Assert.Equal(ControlTools.ImportedText(4, "Default"), result.Text);
        Assert.False(rig.State.Store.Mcps["dbt"].Enabled);
        // Copies arrive off.
        Assert.False(rig.H.ClaudeServers().ContainsKey("dbt"));
    }

    [Fact]
    public void ImportingIntoANewCollectionAndChoosingWhatAClashDoes()
    {
        using var rig = Rig();
        var path = rig.H.WriteDocument(CollectionDocumentSamples.DataTeam, "team.json");
        var rendered = CollectionDocumentSamples.DataTeam.Render();
        Assert.False(rig.Call("import_document", Args(("path", Text(path)), ("newCollection", Text("Team"))))?.IsError);
        Assert.Equal(ControlTools.ImportNewMessage(4, "Team"), rig.Confirmer.Requests[^1].Message);
        Assert.Equal(4, rig.State.Store.Collections["Team"].Mcps.Count);
        Assert.Equal(ControlTools.ImportedText(4, "Team"),
            rig.Call("import_document", Args(("path", Text(path)), ("into", Text("Team")), ("onClash", Text("keep-both"))))?.Text);
        Assert.Equal([ControlTools.Detail("“dbt”", ImportModel.KeepBothTitle)], rig.Confirmer.Requests[^1].Code.Take(1));
        Assert.True(rig.State.Store.Collections["Team"].Mcps.ContainsKey("dbt 2"));
        Assert.Equal(ControlTools.ImportedText(4, "Team"),
            rig.Call("import_document", Args(("path", Text(path)), ("into", Text("Team")), ("onClash", Text("replace"))))?.Text);
        Assert.Equal(new[] { "dbt", "github", "ledger", "notion" }.SelectMany(name => Listed(name, rendered.Connectors[name].Config, ImportModel.ReplaceTitle)),
            rig.Confirmer.Requests[^1].Code);
        // Replaced in place.
        Assert.Equal(8, rig.State.Store.Collections["Team"].Mcps.Count);
        // Every name clashes and clashes are skipped.
        Assert.Equal(ControlResult.Refusal(ControlTools.NothingToImportError("team.json")),
            rig.Call("import_document", Args(("path", Text(path)), ("into", Text("Team")))));
        Assert.Equal(3, rig.Confirmer.Requests.Count);
    }

    /// <summary>A clash nothing is said about is skipped, and the question says which; the rest come across.</summary>
    [Fact]
    public void AClashIsSkippedUnlessOnClashSaysOtherwise()
    {
        using var rig = Rig();
        var path = rig.H.WriteDocument(CollectionDocumentSamples.DataTeam, "team.json");
        var rendered = CollectionDocumentSamples.DataTeam.Render();
        Assert.Null(rig.State.AddEmptyCollection("Team"));
        Assert.Null(rig.State.Upsert("dbt", AppStateHarness.LocalConnector("mine"), null, "Team"));
        Assert.Equal(ControlTools.ImportedText(3, "Team"), rig.Call("import_document", Args(("path", Text(path)), ("into", Text("Team"))))?.Text);
        Assert.Equal(ControlTools.ImportMessage(3, "Team"), rig.Confirmer.Requests[^1].Message);
        Assert.Equal(Listed("dbt", null, ImportModel.PresentBadge)
                .Concat(new[] { "github", "ledger", "notion" }.SelectMany(name => Listed(name, rendered.Connectors[name].Config))),
            rig.Confirmer.Requests[^1].Code);
        Assert.Equal(AppStateHarness.LocalConnector("mine").Config, rig.State.Store.Collections["Team"].Mcps["dbt"].Config);
    }

    [Fact]
    public void ImportRefusals()
    {
        using var rig = Rig();
        var path = rig.H.WriteDocument(CollectionDocumentSamples.DataTeam, "team.json");
        rig.H.Subscribe(rig.State, CollectionDocumentSamples.DataTeam, "other.json");
        Assert.Equal(ControlResult.Refusal(AppState.TargetMustBeLocalError),
            rig.Call("import_document", Args(("path", Text(path)), ("into", Text("Data team")))));
        Assert.Equal(ControlResult.Refusal(MasterStore.NoCollectionError("Nope")),
            rig.Call("import_document", Args(("path", Text(path)), ("into", Text("Nope")))));
        Assert.True(rig.Call("import_document", Args(("path", Text(path)), ("newCollection", Text("Default"))))?.IsError);
        Assert.True(rig.Call("import_document", Args(("path", Text(rig.H.Dir.File("missing.json")))))?.IsError);
        Assert.Equal(ControlResult.Refusal(ControlTools.MissingArgumentError("path")), rig.Call("import_document", Args(("path", Text("")))));
        // One place or the other, as the command line's --into | --new.
        Assert.Equal(ControlResult.Refusal(ControlTools.IntoOrNewCollectionError),
            rig.Call("import_document", Args(("path", Text(path)), ("into", Text("Default")), ("newCollection", Text("Team")))));
        Assert.False(rig.State.Store.Collections.ContainsKey("Team"));
        Assert.Empty(rig.Confirmer.Requests);
        Assert.False(rig.State.Store.Collections["Default"].Mcps.ContainsKey("dbt"));
    }

    // MARK: subscribe

    [Fact]
    public void SubscribingAsksThenFollowsTheFileReadOnlyWithEverythingOff()
    {
        using var rig = Rig();
        var path = rig.H.WriteDocument(CollectionDocumentSamples.DataTeam, "team.json");
        var result = rig.Call("subscribe", Args(("path", Text(path))));
        Assert.NotNull(result);
        Assert.Equal(new ConfirmationRequest(
            ControlTools.SubscribeTitle("Claude", "team.json"),
            ControlTools.SubscribeMessage("Data team", 4),
            DataTeamLines(),
            ControlTools.FromFooter(path)), rig.Confirmer.Requests[^1]);
        Assert.Equal(ControlTools.SubscribedText("Data team", "team.json") + "\n" + ControlTools.ArrivedOffText(4), result.Text);
        Assert.Equal(CollectionKind.Synced, rig.State.KindOf("Data team"));
        Assert.DoesNotContain(rig.State.Store.Collections["Data team"].Mcps.Values, entry => entry.Enabled);
        Assert.Equal("Default", rig.State.ActiveCollection);
    }

    /// <summary>A connector this platform will not carry is listed with the reason, as the Import sheet
    /// lists it, and does not count.</summary>
    [Fact]
    public void AConnectorUnderTheReservedNameIsListedAsSkipped()
    {
        using var rig = Rig();
        var doc = CollectionDocumentSamples.WithReservedConnector(CollectionDocumentSamples.DataTeam);
        var path = rig.H.WriteDocument(doc, "team.json");
        rig.Call("subscribe", Args(("path", Text(path)), ("name", Text("Team"))));
        Assert.Equal(ControlTools.SubscribeMessage("Team", 4), rig.Confirmer.Requests[^1].Message);
        var rendered = doc.Render();
        Assert.Equal(Listed(ClaudeIntegration.EntryName, null, ImportModel.SkippedBadge(ClaudeIntegration.ReservedNameError))
                .Concat(new[] { "dbt", "github", "ledger", "notion" }.SelectMany(name => Listed(name, rendered.Connectors[name].Config))),
            rig.Confirmer.Requests[^1].Code);
        Assert.False(rig.State.Store.Collections["Team"].Mcps.ContainsKey(ClaudeIntegration.EntryName));
        Assert.Equal(ControlTools.ImportedText(4, "Default"), rig.Call("import_document", Args(("path", Text(path))))?.Text);
        Assert.Equal(Listed(ClaudeIntegration.EntryName, null, ImportModel.SkippedBadge(ClaudeIntegration.ReservedNameError))[0],
            rig.Confirmer.Requests[^1].Code[0]);
        Assert.False(rig.State.Store.Collections["Default"].Mcps.ContainsKey(ClaudeIntegration.EntryName));
    }

    /// <summary>This mirror only: a remote connector the cmd /c launcher cannot carry is listed as skipped,
    /// with RemotePattern's reason. The Mac never writes that launcher, so its mirror has no such test; the
    /// rule is the same on every machine, so the test runs everywhere.</summary>
    [Fact]
    public void AConnectorTheCmdLauncherCannotCarryIsListedAsSkipped()
    {
        using var rig = Rig();
        var unsafeRemote = new CollectionDocument.Connector(
            new CollectionDocument.Launcher.Remote("https://x.example/mcp?a=1&b=2", CollectionDocument.Auth.Auto, "mcp-remote", []),
            new Dictionary<string, CollectionDocument.EnvValue>(), new Dictionary<string, string?>(), new Dictionary<string, JsonValue>());
        var path = rig.H.WriteDocument(With(CollectionDocumentSamples.DataTeam, "amp", unsafeRemote), "team.json");
        rig.Call("import_document", Args(("path", Text(path))));
        Assert.Equal(ControlTools.ImportMessage(4, "Default"), rig.Confirmer.Requests[^1].Message);
        Assert.Equal(Listed("amp", null, ImportModel.SkippedBadge(RemotePattern.CmdUnsafeReason(RemoteField.Url))),
            rig.Confirmer.Requests[^1].Code.Take(1));
        Assert.False(rig.State.Store.Mcps.ContainsKey("amp"));
    }

    [Fact]
    public void SubscribingToYourOwnPublishedCollectionOrATakenNameIsRefused()
    {
        using var rig = Rig();
        var published = rig.H.Publish(rig.State, "Default");
        Assert.True(File.Exists(published));
        Assert.Equal(ControlResult.Refusal(AppState.OwnCollectionError), rig.Call("subscribe", Args(("path", Text(published)))));
        var path = rig.H.WriteDocument(CollectionDocumentSamples.DataTeam, "team.json");
        Assert.True(rig.Call("subscribe", Args(("path", Text(path)), ("name", Text("Default"))))?.IsError);
        Assert.Equal(ControlResult.Refusal(ControlTools.MissingArgumentError("path")), rig.Call("subscribe", Args(("path", Text("")))));
        Assert.Empty(rig.Confirmer.Requests);
    }

    // MARK: What the questions show

    /// <summary>Import and subscribe show what each connector would run, argument as written and
    /// <c>${COLLECTION_DIR}</c> as the document writes it, with environment values masked, where the
    /// masked view would hide the argument; the answers carry none of it.</summary>
    [Fact]
    public void TheQuestionsShowWhatWouldRunAndTheAnswersNoSecret()
    {
        using var rig = Rig();
        var path = rig.H.WriteDocument(Secretive, "acme.json");
        var imported = rig.Call("import_document", Args(("path", Text(path))));
        var subscribed = rig.Call("subscribe", Args(("path", Text(path))));
        var config = Secretive.Render().Connectors["acme"].Config;
        Assert.Equal(2, rig.Confirmer.Requests.Count);
        // The masked view hides it.
        Assert.NotEqual(ControlMasking.CodeLines(config), ControlMasking.CodeLines(config, ControlMasking.Exposure.Dialog));
        foreach (var request in rig.Confirmer.Requests)
        {
            Assert.Equal(Listed("acme", config), request.Code);
            var code = string.Join("\n", request.Code);
            Assert.Contains("${COLLECTION_DIR}/server.js --api-key sk-live-abc123", code);
            Assert.Contains("ACME_TOKEN", code);
            Assert.DoesNotContain("literal-env-secret", code);
        }
        foreach (var result in new[] { imported, subscribed })
        {
            Assert.NotNull(result);
            Assert.False(result.IsError);
            foreach (var secret in new[] { "sk-live-abc123", "literal-env-secret" })
            {
                Assert.DoesNotContain(secret, result.Text);
                Assert.DoesNotContain(secret, result.Structured.CompactLine());
            }
        }
    }

    // MARK: apply_update

    [Fact]
    public void ApplyingAnUpdateAsksWithWhatItChanges()
    {
        using var rig = Rig();
        var path = rig.H.Subscribe(rig.State, CollectionDocumentSamples.DataTeam);
        Assert.Equal(ControlResult.Refusal(ControlTools.NoUpdateError("Data team")), rig.Call("apply_update", Args(("collection", Text("Data team")))));
        var before = rig.State.Store.Collections["Data team"].Mcps["dbt"].Config;
        var doc = With(With(CollectionDocumentSamples.DataTeam, "github", null), "dbt", Local("npx", "-y", "@dbt/mcp@2"));
        AppStateHarness.WriteDocumentAt(doc, path);
        rig.State.RefreshSource("Data team");
        var after = doc.Render().Connectors["dbt"].Config;
        var result = rig.Call("apply_update", Args(("collection", Text("Data team"))));
        Assert.NotNull(result);
        Assert.Equal(new ConfirmationRequest(
            ControlTools.ApplyUpdateTitle("Claude", "Data team"),
            ControlTools.PendingUpdateText("Data team", ControlTools.DeletesClause("“github”") + ControlTools.ClauseSeparator
                + ControlTools.ChangesClause("“dbt”")),
            [.. Listed("github", null, ReviewModel.DeletedLabel), .. Listed("dbt", null, ReviewModel.ChangedLabel),
             .. ControlMasking.ChangeLines(before, after, ControlMasking.Exposure.Dialog).Select(line => "  " + line)],
            null), rig.Confirmer.Requests[^1]);
        Assert.Equal(ControlTools.AppliedUpdateText("Data team"), result.Text);
        Assert.False(rig.State.Store.Collections["Data team"].Mcps.ContainsKey("github"));
        Assert.Equal(ControlResult.Refusal(ControlTools.NoUpdateError("Data team")), rig.Call("apply_update", Args(("collection", Text("Data team")))));
        Assert.Equal(ControlResult.Refusal(ControlTools.NotSubscribedError("Default")), rig.Call("apply_update", Args(("collection", Text("Default")))));
        Assert.Equal(ControlResult.Refusal(MasterStore.NoCollectionError("Nope")), rig.Call("apply_update", Args(("collection", Text("Nope")))));
        Assert.Single(rig.Confirmer.Requests);
    }

    /// <summary>What the user allowed is what lands: a document that moves under the question asks a
    /// different one, so the Allow applies nothing.</summary>
    [Fact]
    public void AnUpdateThatMovedWhileAskingIsNotApplied()
    {
        using var rig = Rig(answer: null);
        var path = rig.H.Subscribe(rig.State, CollectionDocumentSamples.DataTeam);
        var original = rig.State.Store.Collections["Data team"].Mcps["dbt"];
        AppStateHarness.WriteDocumentAt(With(CollectionDocumentSamples.DataTeam, "dbt", Local("npx", "-y", "@dbt/mcp@2")), path);
        rig.State.RefreshSource("Data team");
        var answer = rig.Start("apply_update", Args(("collection", Text("Data team"))));
        var moved = With(CollectionDocumentSamples.DataTeam, "dbt", Local("npx", "-y", "@dbt/mcp@3"));
        AppStateHarness.WriteDocumentAt(moved, path);
        rig.State.RefreshSource("Data team");
        rig.Confirmer.Answer(true);
        rig.H.Drain();
        Assert.Equal(ControlResult.Refusal(ControlTools.ChangedWhileAskingError), answer.Result);
        // Nothing applied.
        Assert.Equal(original, rig.State.Store.Collections["Data team"].Mcps["dbt"]);
    }

    /// <summary>A name cannot invent a clause of the update's sentence: every name is quoted.</summary>
    [Fact]
    public void AConnectorsNameCannotInventAClauseOfTheUpdate()
    {
        using var rig = Rig();
        var doc = With(CollectionDocumentSamples.DataTeam, "x; deletes notion", CollectionDocumentSamples.DataTeam.Connectors["github"]);
        var path = rig.H.Subscribe(rig.State, doc);
        AppStateHarness.WriteDocumentAt(With(doc, "x; deletes notion", null), path);
        rig.State.RefreshSource("Data team");
        var sentence = ControlTools.PendingUpdateText("Data team", ControlTools.DeletesClause("“x; deletes notion”"));
        Assert.Equal(sentence, rig.Call("get_pending_update", Args(("collection", Text("Data team"))))?.Text);
        rig.Call("apply_update", Args(("collection", Text("Data team"))));
        Assert.Equal(sentence, rig.Confirmer.Requests[^1].Message);
    }

    // MARK: What the user allowed

    /// <summary>The question cannot show a value the dialog masks, so it alone cannot tell the user's Allow
    /// from one given to a different value: a document that changed only there while the dialog was open
    /// lands nothing.</summary>
    [Fact]
    public void AnImportWhoseDocumentChangedOnlyWhereTheDialogMasksLandsNothing()
    {
        using var rig = Rig(answer: null);
        var path = rig.H.WriteDocument(Masked("one"), "acme.json");
        var answer = rig.Start("import_document", Args(("path", Text(path))));
        var asked = rig.Confirmer.Requests[^1];
        AppStateHarness.WriteDocumentAt(Masked("two"), path);
        rig.Confirmer.Answer(true);
        rig.H.Drain();
        Assert.Equal(ControlResult.Refusal(ControlTools.ChangedWhileAskingError), answer.Result);
        // Nothing applied.
        Assert.False(rig.State.Store.Mcps.ContainsKey("acme"));
        rig.Start("import_document", Args(("path", Text(path))));
        // The question itself reads the same.
        Assert.Equal(asked, rig.Confirmer.Requests[^1]);
    }

    [Fact]
    public void ASubscriptionWhoseDocumentChangedOnlyWhereTheDialogMasksLandsNothing()
    {
        using var rig = Rig(answer: null);
        var path = rig.H.WriteDocument(Masked("one"), "acme.json");
        var answer = rig.Start("subscribe", Args(("path", Text(path))));
        AppStateHarness.WriteDocumentAt(Masked("two"), path);
        rig.Confirmer.Answer(true);
        rig.H.Drain();
        Assert.Equal(ControlResult.Refusal(ControlTools.ChangedWhileAskingError), answer.Result);
        // Nothing applied.
        Assert.False(rig.State.Store.Collections.ContainsKey("Acme"));
    }

    [Fact]
    public void AnUpdateWhoseDocumentChangedOnlyWhereTheDialogMasksLandsNothing()
    {
        using var rig = Rig(answer: null);
        var path = rig.H.Subscribe(rig.State, Masked("one"));
        var original = rig.State.Store.Collections["Acme"].Mcps["acme"];
        AppStateHarness.WriteDocumentAt(Masked("two"), path);
        rig.State.RefreshSource("Acme");
        var answer = rig.Start("apply_update", Args(("collection", Text("Acme"))));
        AppStateHarness.WriteDocumentAt(Masked("three"), path);
        rig.State.RefreshSource("Acme");
        rig.Confirmer.Answer(true);
        rig.H.Drain();
        Assert.Equal(ControlResult.Refusal(ControlTools.ChangedWhileAskingError), answer.Result);
        // Nothing applied.
        Assert.Equal(original, rig.State.Store.Collections["Acme"].Mcps["acme"]);
    }

    [Fact]
    public void ARestoreWhoseBackupChangedWhileAskingLandsNothing()
    {
        using var rig = Rig(answer: null);
        const string id = "claude_desktop_config.zz-masked.json";
        var backup = Path.Combine(rig.H.BackupsDir, id);
        const string config = """{"mcpServers": {"acme": {"command": "node", "env": {"ACME_TOKEN": "%@"}}}}""";
        File.WriteAllText(backup, config.Replace("%@", "one", StringComparison.Ordinal));
        var before = rig.H.ClaudeServers();
        var answer = rig.Start("restore_backup", Args(("id", Text(id))));
        File.WriteAllText(backup, config.Replace("%@", "two", StringComparison.Ordinal));
        rig.Confirmer.Answer(true);
        rig.H.Drain();
        Assert.Equal(ControlResult.Refusal(ControlTools.ChangedWhileAskingError), answer.Result);
        // Nothing applied.
        Assert.Equal(before, rig.H.ClaudeServers());
    }

    /// <summary>Don't Allow, for every one of these, leaves everything as it was.</summary>
    [Fact]
    public void ADeclinedChangeLeavesEverythingAsItWas()
    {
        using var rig = Rig(answer: false);
        Assert.Null(rig.State.AddEmptyCollection("Work"));
        var subscribed = rig.H.Subscribe(rig.State, CollectionDocumentSamples.DataTeam);
        AppStateHarness.WriteDocumentAt(With(CollectionDocumentSamples.DataTeam, "github", null), subscribed);
        rig.State.RefreshSource("Data team");
        var path = rig.H.WriteDocument(Masked("one"), "acme.json");
        var id = Path.GetFileName(rig.State.Service.Backups.Backups(RestoreModel.Series)[0]);
        var store = rig.H.StoreOnDisk();
        var claude = rig.H.ClaudeServers();
        (string Name, JsonValue Arguments)[] calls =
        [
            ("delete_collection", Args(("collection", Text("Work")))),
            ("import_document", Args(("path", Text(path)))),
            ("subscribe", Args(("path", Text(path)))),
            ("apply_update", Args(("collection", Text("Data team")))),
            ("restore_backup", Args(("id", Text(id)))),
        ];
        foreach (var (name, arguments) in calls)
        {
            Assert.Equal(ControlResult.Refusal(ControlTools.DeclinedError), rig.Call(name, arguments));
        }
        Assert.Equal(calls.Length, rig.Confirmer.Requests.Count);
        Assert.Equal(store, rig.H.StoreOnDisk());
        Assert.Equal(claude, rig.H.ClaudeServers());
        Assert.True(rig.State.PendingUpdates.ContainsKey("Data team"));
    }

    // MARK: restore_backup

    [Fact]
    public void RestoringABackupByTheIdTheListGave()
    {
        using var rig = Rig();
        var id = Path.GetFileName(rig.State.Service.Backups.Backups(RestoreModel.Series)[0]);
        Assert.Null(rig.State.Upsert("extra", new McpEntry(JsonValue.Object(("command", JsonValue.String("x")))), null));
        rig.State.ApplyInteractively();
        var result = rig.Call("restore_backup", Args(("id", Text(id))));
        Assert.NotNull(result);
        Assert.Equal(new ConfirmationRequest(
            ControlTools.RestoreTitle("Claude", id),
            RestoreModel.Caption + " " + ControlTools.RestoreIntoText("Default"),
            ["“aws-mcp”", "“scoutbook”", "“service-now”"],
            ControlTools.TakesEffectFooter), rig.Confirmer.Requests[^1]);
        Assert.Equal(ControlTools.RestoredText(id), result.Text.Split('\n')[0]);
        // Claude's config is the backup again.
        Assert.False(rig.H.ClaudeServers().ContainsKey("extra"));
        // Only an id the list gives.
        Assert.Equal(ControlResult.Refusal(ControlTools.NoBackupError("../../etc/passwd")),
            rig.Call("restore_backup", Args(("id", Text("../../etc/passwd")))));
        Assert.Single(rig.Confirmer.Requests);
    }

    /// <summary>A backup the Restore sheet would refuse is refused before anyone is asked, as is one that
    /// is not a config file.</summary>
    [Fact]
    public void ARestoreTheSheetWouldRefuseIsRefusedBeforeAsking()
    {
        using var rig = Rig();
        const string broken = "claude_desktop_config.zz-broken.json";
        File.WriteAllText(Path.Combine(rig.H.BackupsDir, broken), "{");
        var unreadable = rig.Call("restore_backup", Args(("id", Text(broken))));
        Assert.NotNull(unreadable);
        Assert.True(unreadable.IsError);
        // Listed, but not a config file.
        Assert.NotEqual(ControlResult.Refusal(ControlTools.NoBackupError(broken)), unreadable);
        // A backup with no record of its collection goes into the active one, which is refused while
        // that one is subscribed.
        var backup = rig.State.Service.Backups.Backups(RestoreModel.Series)[0];
        const string unrecorded = "claude_desktop_config.zz-unrecorded.json";
        File.Copy(backup, Path.Combine(rig.H.BackupsDir, unrecorded));
        rig.H.Subscribe(rig.State, CollectionDocumentSamples.DataTeam);
        rig.State.SwitchCollection("Data team");
        Assert.Equal(ControlResult.Refusal(AppState.RestoreSubscribedError("Data team")),
            rig.Call("restore_backup", Args(("id", Text(unrecorded)))));
        Assert.Empty(rig.Confirmer.Requests);
        Assert.Equal("Data team", rig.State.ActiveCollection);
    }

    /// <summary>A backup taken while another collection was active goes back into that one, which becomes
    /// the active collection again; the question says so.</summary>
    [Fact]
    public void RestoringABackupOfAnotherCollectionMakesThatOneActive()
    {
        using var rig = Rig();
        Assert.Null(rig.State.CreateActiveCopy("Work"));
        Assert.Null(rig.State.Upsert("one", AppStateHarness.LocalConnector("node"), null));
        rig.State.ApplyInteractively();
        Assert.Null(rig.State.Upsert("two", AppStateHarness.LocalConnector("node"), null));
        rig.State.ApplyInteractively();
        rig.State.SwitchCollection("Default");
        var backup = rig.State.Service.Backups.Backups(RestoreModel.Series)
            .First(path => BackupCollections.CollectionOf(path, rig.H.BackupsDir) == "Work");
        var result = rig.Call("restore_backup", Args(("id", Text(Path.GetFileName(backup)))));
        Assert.NotNull(result);
        Assert.Equal(RestoreModel.Caption + " " + ControlTools.RestoreActivatingText("Work"), rig.Confirmer.Requests[^1].Message);
        Assert.False(result.IsError, result.Text);
        Assert.Equal("Work", rig.State.ActiveCollection);
        Assert.Equal(JsonValue.String("Work"), result.Structured.ValueAt(new JsonPointer(["activeCollection"])));
    }

    // MARK: restart_claude

    [Fact]
    public void ARestartFromClaudeAlwaysAsksAndFromTheCommandLineNever()
    {
        using var rig = new ControlRig(confirm: false);
        rig.Confirmer.Scripted = false;
        // Asked although confirmations are off.
        Assert.Equal(ControlResult.Refusal(ControlTools.DeclinedError), rig.Call("restart_claude"));
        Assert.Equal(new ConfirmationRequest(ControlTools.RestartItselfTitle, ControlTools.RestartMessage, [], null),
            rig.Confirmer.Requests[^1]);
        // A declined restart leaves nothing for later.
        Assert.DoesNotContain(TimeSpan.FromSeconds(ControlTools.RestartDelay), rig.H.Delays.Pending.Select(pending => pending.Delay));
        RunDelays(rig);
        Assert.Equal(0, rig.H.Claude.RestartCalls);
        Assert.Equal(ControlTools.RestartingText, rig.Call("restart_claude", caller: new ControlCaller.Cli())?.Text);
        RunDelays(rig);
        Assert.Equal(1, rig.H.Claude.RestartCalls);
        Assert.Single(rig.Confirmer.Requests);
        rig.Confirmer.Scripted = true;
        rig.Call("restart_claude", caller: new ControlCaller.Relay("Cursor"));
        Assert.Equal(ControlTools.RestartTitle("Cursor"), rig.Confirmer.Requests[^1].Title);
        RunDelays(rig);
        Assert.Equal(2, rig.H.Claude.RestartCalls);
        // A restart is asked about, never announced.
        Assert.Empty(rig.H.Notifier.Sent);
    }

    /// <summary>The gate already asked, so the app's own "confirm before restarting" question does not ask
    /// a second time; the command line's typed command is its confirmation.</summary>
    [Fact]
    public void TheAppsOwnRestartQuestionDoesNotAskAgain()
    {
        using var rig = Rig();
        rig.H.Settings.ConfirmBeforeRestart = true;
        rig.H.Dialogs.NextConfirm = false;
        rig.Call("restart_claude");
        rig.Call("restart_claude", caller: new ControlCaller.Cli());
        RunDelays(rig);
        Assert.Equal(2, rig.H.Claude.RestartCalls);
        Assert.Empty(rig.H.Dialogs.Confirms);
    }

    /// <summary>Claude hears its answer before it quits: the restart waits <c>RestartDelay</c>, and one due
    /// after the app has quit never happens.</summary>
    [Fact]
    public void TheAnswerComesBeforeTheRestart()
    {
        using var rig = Rig();
        Assert.Equal(ControlTools.RestartingText, rig.Call("restart_claude")?.Text);
        // Answered, not yet restarted.
        Assert.Equal(0, rig.H.Claude.RestartCalls);
        // The wait's first tick, set before the question was put, then the restart.
        Assert.Equal([TimeSpan.FromSeconds(ControlTools.ProgressInterval), TimeSpan.FromSeconds(ControlTools.RestartDelay)],
            rig.H.Delays.Pending.Select(pending => pending.Delay));
        rig.H.Delays.RunNext();
        rig.H.Drain();
        // The tick finds the call answered and does nothing.
        Assert.Equal(0, rig.H.Claude.RestartCalls);
        rig.H.Delays.RunNext();
        rig.H.Drain();
        Assert.Equal(1, rig.H.Claude.RestartCalls);
        rig.Call("restart_claude");
        rig.State.Dispose();
        while (rig.H.Delays.Pending.Count > 0)
        {
            rig.H.Delays.RunNext();
        }
        // The app quit before the restart was due.
        Assert.Equal(1, rig.H.Claude.RestartCalls);
    }

    // MARK: Names

    /// <summary>Every name these questions and answers carry is chosen by someone else: the asker by the
    /// client, a collection's by Claude or a document's author, a file's by whoever wrote it, a
    /// connector's by the author or Claude. Each shows as an escape what would break a line, reorder
    /// or hide text, or close its quotes.</summary>
    [Fact]
    public void NamesCannotForgeTheseQuestionsOrTheirAnswers()
    {
        using var rig = Rig();
        var caller = new ControlCaller.Relay("Cursor\n\u202Ed");
        const string shownAsker = """Cursor\n\u{202E}d""";
        const string collection = "Work\n\u202Ez", shownCollection = """Work\n\u{202E}z""";
        const string connector = "a\nb\u202Ec", shownConnector = """a\nb\u{202E}c""";
        // Neither platform takes a line break in a file name; both take these.
        const string file = "t\u202E\u201D.json", shownFile = """t\u{202E}\u{201D}.json""";
        const string team = "Team\n\u202Ey", shownTeam = """Team\n\u{202E}y""";
        var config = AppStateHarness.LocalConnector("node").Config;
        var doc = new CollectionDocument(team, null, "hostile-origin", "2026-10-01T00:00:00Z",
            new Dictionary<string, CollectionDocument.Connector> { [connector] = Local("node"), ["keep"] = Local("node", "k") });
        var path = rig.H.WriteDocument(doc, file);
        Assert.Null(rig.State.AddEmptyCollection(collection));
        Assert.Null(rig.State.Upsert(connector, new McpEntry(config), null));
        rig.State.ApplyInteractively();
        Assert.Null(rig.State.Upsert("later", new McpEntry(config), null));
        rig.State.ApplyInteractively();
        var backup = rig.State.Service.Backups.Backups(RestoreModel.Series)[0];
        const string id = "claude_desktop_config.\u202E\u201D.json", shownId = """claude_desktop_config.\u{202E}\u{201D}.json""";
        File.Copy(backup, Path.Combine(rig.H.BackupsDir, id));

        var results = new List<ControlResult?>
        {
            rig.Call("import_document", Args(("path", Text(path)), ("into", Text(collection))), caller),
            rig.Call("subscribe", Args(("path", Text(path))), caller),
        };
        AppStateHarness.WriteDocumentAt(With(doc, connector, null), path);
        rig.State.RefreshSource(team);
        results.Add(rig.Call("apply_update", Args(("collection", Text(team))), caller));
        results.Add(rig.Call("delete_collection", Args(("collection", Text(collection))), caller));
        results.Add(rig.Call("restore_backup", Args(("id", Text(id))), caller));
        results.Add(rig.Call("restart_claude", caller: caller));
        rig.H.Drain();

        var requests = rig.Confirmer.Requests;
        Assert.Equal([
            ControlTools.ImportTitle(shownAsker, shownFile), ControlTools.SubscribeTitle(shownAsker, shownFile),
            ControlTools.ApplyUpdateTitle(shownAsker, shownTeam), ControlTools.DeleteCollectionTitle(shownAsker, shownCollection),
            ControlTools.RestoreTitle(shownAsker, shownId), ControlTools.RestartTitle(shownAsker),
        ], requests.Select(request => request.Title));
        Assert.Equal([
            ControlTools.ImportMessage(2, shownCollection), ControlTools.SubscribeMessage(shownTeam, 2),
            ControlTools.PendingUpdateText(shownTeam, ControlTools.DeletesClause("“" + shownConnector + "”")),
        ], requests.Take(3).Select(request => request.Message));
        Assert.Equal("“" + shownConnector + "”", requests[0].Code[0]);
        Assert.Equal(["“" + shownConnector + "” · " + ReviewModel.DeletedLabel], requests[2].Code);
        Assert.Equal(ControlTools.FromFooter(ControlText.Escaped(path)), requests[0].Footer);
        // The backup's connectors.
        Assert.Contains("“" + shownConnector + "”", requests[4].Code);
        Assert.Equal([
            ControlTools.ImportedText(2, shownCollection), ControlTools.SubscribedText(shownTeam, shownFile),
            ControlTools.AppliedUpdateText(shownTeam), ControlTools.DeletedCollectionText(shownCollection),
        ], results.Take(4).Select(result => result?.Text.Split('\n')[0]));
        Assert.Equal(ControlTools.RestartingText, results[^1]?.Text);
        foreach (var request in requests)
        {
            foreach (var text in new[] { request.Title, request.Message, request.Footer ?? "" }.Concat(request.Code))
            {
                AssertPlain(text);
            }
        }
        foreach (var result in results)
        {
            Assert.False(result?.IsError, result?.Text);
            foreach (var line in (result?.Text ?? "").Split('\n'))
            {
                AssertPlain(line);
            }
        }
    }

    /// <summary>A refusal in the app's own words still shows a name someone else chose as an escape, with the
    /// sentence's own quotes as they are.</summary>
    [Fact]
    public void NamesInsideTheAppsOwnSentencesAreEscaped()
    {
        using var rig = Rig();
        const string collection = "Work\n\u202Ez", shownCollection = """Work\n\u{202E}z""";
        const string file = "t\u202E\u201D.json", shownFile = """t\u{202E}\u{201D}.json""";
        Assert.Null(rig.State.AddEmptyCollection(collection));
        var taken = rig.State.Store.Clone().AddCollection(collection);
        Assert.NotNull(taken);
        var shownTaken = taken.Replace(collection, shownCollection, StringComparison.Ordinal);
        var path = rig.H.WriteDocument(CollectionDocumentSamples.DataTeam, "team.json");
        Assert.Equal(ControlResult.Refusal(shownTaken), rig.Call("import_document", Args(("path", Text(path)), ("newCollection", Text(collection)))));
        Assert.Equal(ControlResult.Refusal(shownTaken), rig.Call("subscribe", Args(("path", Text(path)), ("name", Text(collection)))));
        var broken = rig.H.Dir.File(file);
        File.WriteAllText(broken, "{");
        var source = rig.H.Subscribe(rig.State, CollectionDocumentSamples.DataTeam, "source/" + file, "Team");
        File.WriteAllText(source, "{");
        rig.State.RefreshSource("Team");
        foreach (var refusal in new[]
                 {
                     rig.Call("import_document", Args(("path", Text(broken)))), rig.Call("subscribe", Args(("path", Text(broken)))),
                     rig.Call("apply_update", Args(("collection", Text("Team")))),
                 })
        {
            Assert.NotNull(refusal);
            Assert.True(refusal.IsError);
            Assert.Contains(shownFile, refusal.Text);
            AssertPlain(refusal.Text);
        }
        Assert.Empty(rig.Confirmer.Requests);
        // As a change refused as it was made says it.
        Assert.Equal(ControlResult.Refusal("""No collection named “a\nb”."""),
            ControlTools.Refused("No collection named “a\nb”.", ["a\nb"]).Result);
    }

    /// <summary>The announcements, for changes nobody was asked about, name everything as the questions do.</summary>
    [Fact]
    public void AnAnnouncementShowsNamesAsEscapes()
    {
        using var rig = new ControlRig(confirm: false);
        var caller = new ControlCaller.Relay("Cursor\n\u202Ed");
        const string shownAsker = """Cursor\n\u{202E}d""";
        const string collection = "Work\n\u202Ez", shownCollection = """Work\n\u{202E}z""";
        const string file = "t\u202E\u201D.json", shownFile = """t\u{202E}\u{201D}.json""";
        const string team = "Team\n\u202Ey", shownTeam = """Team\n\u{202E}y""";
        var doc = new CollectionDocument(team, null, "hostile-origin", "2026-10-01T00:00:00Z",
            new Dictionary<string, CollectionDocument.Connector> { ["one"] = Local("node") });
        var path = rig.H.WriteDocument(doc, file);
        var id = Path.GetFileName(rig.State.Service.Backups.Backups(RestoreModel.Series)[0]);
        Assert.Null(rig.State.AddEmptyCollection(collection));
        rig.Call("import_document", Args(("path", Text(path)), ("into", Text(collection))), caller);
        rig.Call("subscribe", Args(("path", Text(path))), caller);
        Assert.Equal([
            ControlTools.ImportedNotification(shownAsker, 1, shownCollection),
            ControlTools.SubscribedNotification(shownAsker, shownFile, shownTeam),
        ], rig.H.Notifier.Sent.Select(sent => sent.Body));
        AppStateHarness.WriteDocumentAt(With(doc, "two", Local("node")), path);
        rig.State.RefreshSource(team);
        // The update's own notice, which is the app's, not an announcement.
        rig.H.Notifier.Sent.Clear();
        rig.Call("apply_update", Args(("collection", Text(team))), caller);
        rig.Call("delete_collection", Args(("collection", Text(collection))), caller);
        rig.Call("restore_backup", Args(("id", Text(id))), caller);
        Assert.Equal([
            ControlTools.AppliedUpdateNotification(shownAsker, shownTeam),
            ControlTools.DeletedCollectionNotification(shownAsker, shownCollection),
            ControlTools.RestoredNotification(shownAsker, id),
        ], rig.H.Notifier.Sent.Select(sent => sent.Body));
        foreach (var body in rig.H.Notifier.Sent.Select(sent => sent.Body))
        {
            AssertPlain(body);
        }
        Assert.Empty(rig.Confirmer.Requests);
    }
}
