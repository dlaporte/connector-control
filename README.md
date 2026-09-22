# Connector Control

Native menu bar (macOS) and system tray (Windows) apps for managing the
custom MCP connectors in Claude Desktop's configuration — with automatic
backups of every change they make.

Claude Desktop reads its MCP servers from claude_desktop_config.json
(`~/Library/Application Support/Claude/` on a Mac, `%APPDATA%\Claude\` on
Windows), a file you otherwise maintain by hand and that Claude itself has
been known to overwrite or wipe
([#32345](https://github.com/anthropics/claude-code/issues/32345),
[#56296](https://github.com/anthropics/claude-code/issues/56296),
[#37286](https://github.com/anthropics/claude-code/issues/37286)). Connector
Control keeps its own **master list** as the source of truth, treats Claude's
config as generated output, and backs up both files before every write — so a
wiped or mangled config is always one click from restored.

<p align="center">
  <img src="docs/screenshots/mac-popover.png" width="344" alt="The Connector Control popover on macOS: a collection chip, a list of connectors with on/off toggles, and an edit pencil on every row.">
</p>

<p align="center"><sub>Pending: equivalent Windows tray-flyout screenshots.</sub></p>

## Features

- **One-click enable/disable** — toggle any connector from the menu bar or
  tray; changes apply to Claude's config immediately, and a **Restart
  Required** button appears until Claude is running the new config (derived
  from Claude's actual process launch time, so it clears no matter how Claude
  restarts).
- **Full editor** — form view for the common cases (remote `mcp-remote`
  servers get a simple Name + URL form; local servers get command/args/env
  editors with secret masking), plus a raw JSON view with live validation and
  paste-a-README-snippet support. The two views stay in sync, and switching
  never silently loses fields the form can't represent.
- **Self-healing** — the app watches Claude's config; if connectors vanish
  from it (Claude update, cloud sync, crash), a banner offers one-click
  restore from the master list, and a notification fires even when the
  app's window is closed.
- **Automatic backups** — timestamped copies of both files before every
  write (configurable retention, plus a permanent first-run snapshot), with
  in-app restore.
- **Syncable** — point the master list at a folder synced by git, iCloud, or
  Dropbox and share one connector catalog across machines; backups always
  stay machine-local so they never pollute the synced folder.
- **Missing-tool warnings** — a connector that starts through npx, node,
  uvx or uv shows a caution glyph when that tool isn't installed where
  Claude Desktop can find it; the editor and Settings ▸ Claude ▸ Tools say
  what to install, with a download link and the brew or winget command.
- **Careful with secrets** — connector env vars can hold API tokens, so the
  master list and all backups are written owner-only (mode 600 on macOS, an
  owner-only ACL on Windows).

The editor's two views of the same connector:

<table>
  <tr>
    <td align="center"><img src="docs/screenshots/mac-editor-form.png" width="420" alt="Form view: Name, Server URL, and an Authentication type picker for a remote mcp-remote connector."><br><sub>Form view</sub></td>
    <td align="center"><img src="docs/screenshots/mac-editor-json.png" width="420" alt="JSON view of the same connector, with the paste-a-README-snippet tip along the bottom."><br><sub>JSON view</sub></td>
  </tr>
</table>

### Collections

Collections are full, independent connector snapshots — each has its own
complete list of connectors and enabled flags. A chip in the header of the
popover (Mac) or flyout (Windows) — `<collection name> ▾` — shows the active
collection and opens a menu to switch collections, to **Import…** or
**Export “<name>”…** a collection document, and to open **Manage
Collections…**. Switching applies immediately, same as any other change, and
raises **Restart Required** just like a toggle would. A new collection
starts as a copy of the active one's connectors.

<p align="center">
  <img src="docs/screenshots/mac-chip-menu.png" width="344" alt="The collection chip's menu on macOS: the collections to switch between, a check on the active one and a chain on a synced one, then Import…, Export…, and Manage Collections….">
</p>

A collection is one of three kinds.

**Local** collections are the ordinary kind, and everything in them is
editable. Every profile from an earlier version is now a local collection
with the same name and the same connectors. The last local collection can't
be deleted, so there is always somewhere to add a connector.

**Synced** collections are read-only mirrors of a collection document
somebody else publishes. You fill in the values the author left for you and
switch connectors on and off; nothing else can be edited, and nothing can be
added — the header's **+** is disabled with the tooltip "Additions go in a
local collection." A chain glyph follows the collection's name wherever it
appears, with the source file's path in its tooltip, and an amber dot joins
it while an update is waiting to be reviewed. Every row carries a lock.

**Published** collections are local collections that also write their
document to a folder whenever their content changes. Publishing is a fact
about one machine rather than about the collection, so a published
collection carries no glyph of its own; the Collections window's detail line
is where it says so.

**Manage Collections…** opens the Collections window: the collections on the
left, the selected one's connectors on the right, and a detail line over
them — "local · 5 connectors · active", "synced from
~/Acme/mcp/data-team.json · read-only · up to date", or "local · 4
connectors · publishes to ~/Acme/mcp from this Mac". The toolbar carries
**Import…**, **Subscribe…**, **Export <n>…** for the rows you tick,
**Publish…** and **New…**; with a synced collection selected, **Refresh**
and **Make Local Copy…** take the place of Export and Publish. **Rename…**,
**Delete…**, **Stop Publishing** and **Stop Syncing (keeps a local copy)**
sit under the rows. A switch can be flipped in any collection from here —
only the active collection reaches Claude, so a toggle elsewhere is saved
and nothing is restarted.

<p align="center">
  <img src="docs/screenshots/mac-collections-window.png" width="620" alt="The Collections window on macOS: collections in the left pane with a chain on the synced one, and the selected collection's connectors on the right with locks, a type column, toggles and edit pencils.">
</p>

The master list file (mcps.json) is v2 (collection-aware); older files from a
build that predates collections are simply rebuilt from Claude's current
config the same way any corrupted file is (see How it works). Beside it, a
collections.json records which collection is which kind and what each one
still needs. **If you sync mcps.json across machines, every machine must run
1.1 or later** — an older app can't parse the v2 file and will treat it as
corrupt.

### Sharing a collection with a team

A **collection document** is a single JSON file describing a collection's
connectors. Publishing writes that file to a folder and keeps it current;
subscribing follows it. No network access is involved: git, OneDrive, Google
Drive, Dropbox or a file you hand over carry the document, exactly as they
carry the master list today.

#### Publishing

Select a local collection in the Collections window and choose **Publish…**.
Point it at a folder in a repository or a synced drive — the master list's
own folder and the backups folder are refused — and the app writes
`<collection-name>.json` there, the name lowercased with every run of other
characters collapsed to a hyphen. The file name and the document's identity
are fixed the first time you publish and never re-derived, so renaming the
collection later does not rename the file.

The sheet decides what leaves the machine:

- **Environment values · stripped unless shared** lists every variable of
  every connector, its value in full, and a **share value** tick. Left
  unticked, which is the default, only the variable's name and the hint you
  write travel, and each subscriber supplies the value. Ticked, the value
  itself goes into the document.
- **Machine-specific paths · found in arguments** lists every argument that
  looks like a path on this machine, each with a tick, a placeholder name
  and a hint. Marked, it travels as a placeholder every subscriber fills in
  for themselves.
- **Document preview** is the document itself, exactly as it will be
  written. Anything in it that looks like a credential is listed under the
  preview, each line naming the connector it came from. Nothing is ever
  edited on your behalf; the preview is there so you see every byte before
  it leaves.

Remote connectors travel without their secrets whatever you tick: a bearer
token, a custom header's value and an OAuth client secret always become
placeholders. Enabled flags never travel, so turning a connector on or off
never rewrites the document.

`${COLLECTION_DIR}` is the other way to keep a path out of a document. Write
it into a local server's command, arguments or environment values and each
subscriber's app expands it to the folder their copy of the document sits
in — so a team that keeps the document beside the server in one repository
shares one entry:

    "command": "node",
    "args": ["${COLLECTION_DIR}/ledger/dist/index.js"]

The document is rewritten whenever what the collection runs changes, by the
machine that published it. **That machine has to be running for a change to
reach the team**: an edit made on your other machine travels through the
master list and is published the next time the publishing machine sees it. A
write that fails puts "Couldn’t publish …" on the banner with **Choose
Folder…** and **Stop Publishing** beside it, and pressing Publish again
retries the write there and then.

**Export “<name>”…** writes the same document once, wherever you choose,
with the same preview and warnings, and can carry just the rows you tick. A
synced collection is the author's document already, so it is never offered
for export.

#### Subscribing

**Subscribe…** picks a document and opens the Import sheet on **Keep as its
own collection, in sync with this file**; **Import…** opens the same sheet on
the other mode. Either way the sheet names the document, its author and its
connectors before anything happens. A subscribed collection arrives with
every connector switched off and does not become the active one.

What is the author's: names, commands, arguments, URLs and auth. They open
locked in the editor under a grey line reading "Synced from <name> ·
read-only", with a **What can I change?** link that spells out the rule.
What is yours: the values the document asks this machine for, and which
connectors are on.

When the author changes the file the collection says so — "Data team changed
at its source: adds jira; removes confluence." — with **Review & Apply…**.
The review sheet groups what would land under **Added**, **Removed** and
**Changed**, with the JSON either side of every change, and nothing lands
until you press **Apply**. Added connectors arrive switched off, and your
filled values follow their placeholder even if the author moved it to
another argument. If the collection is active, applying rewrites Claude's
config and raises **Restart Required**. **Refresh** re-reads the file on
demand. A half-written file, or one a sync client has not fetched yet, is
retried quietly before anything is reported.

Another machine picks the collection up from the master list, but where the
document sits is a per-machine fact. If it lies inside the master-list
folder the app finds it by itself; otherwise the collection shows "<name>'s
file isn’t on this Mac yet." — "this PC" on Windows — with
**Locate <file>…**.

To take part of a synced collection somewhere you can edit it, use **Make
Local Copy…**: from the Collections window it copies the whole collection
into a new local one, and from a connector's editor it copies that one
connector into a local collection you pick. **Stop Syncing (keeps a local
copy)** turns the collection itself into an ordinary local one, keeping
every connector, every value you filled in and every switch. Deleting a
synced collection never touches the source file.

#### Importing as copies

The Import sheet's other mode, **Add to a collection**, copies the
document's connectors into a local collection of your choosing and keeps no
link to the file afterwards. Each connector is listed as "new · arrives
off", "already present · skipped", or "skipped: <reason>". Where the name is
already taken you choose **Replace**, which "keeps your filled values",
**Keep both**, which lands the new one as `<name> 2`, or **Skip**. Every
copy arrives switched off, and its editor carries an italic line: "Imported
from “<name>” on <date>. Edits stay here."

#### Placeholders

Wherever the author stripped a value, the connector waits for yours. The row
shows a caution, the editor marks the field "needs your value" or "needs
your path", and the author's hint sits with it. Filled values are yours:
they live in your master list, they go nowhere, and they survive every
update from the source. A synced collection that uses `${COLLECTION_DIR}`
but has not found its file yet says "Locate the collection file to resolve
paths." on the rows that need it.

#### Trust

Every connector in a document is a command Claude runs. Anyone with write
access to the shared folder can change what Claude runs on every subscriber,
once those subscribers apply the change — the review step is the control.
Treat write access to a published collection's folder as you would treat
access to the machines that follow it.

#### Worth knowing

- **Stop Publishing** — and deleting a published collection — asks "Also
  remove <file> from the folder?", and keeping it is the default. Keep it,
  and publishing that collection into the same folder again is refused with
  "<file> already exists there and belongs to a different collection.": the
  leftover file carries the identity the collection had before, and the app
  never writes over a document it cannot vouch for. Delete the file from the
  folder, then publish again.
- A hint you write for an argument is remembered by the argument's position.
  Insert an argument above a marked one and the hint moves to its neighbour;
  reopen the Publish sheet to put it right.
- An export of part of a published collection still carries that
  collection's identity, so every app reading it treats it as the same
  collection — including yours, which refuses to subscribe to a document it
  publishes itself.
- Replacing an imported copy keeps a filled value only where the author left
  the placeholder in the same place. A copy is not a subscription and has
  nothing recording where the value used to be; a synced collection does,
  and follows the move.
- The document's name is only a default at import. Renaming a published
  collection never renames a subscriber's.

## Installation

### macOS

Requires macOS 14 (Sonoma) or later. The app is a universal binary
(Apple Silicon + Intel), Developer ID–signed and notarized by Apple, so it
runs without Gatekeeper warnings.

1. Download `ConnectorControl_<version>.dmg` from the
   [latest release](https://github.com/dlaporte/connector-control/releases/latest).
2. Open it and drag **Connector Control** to Applications.
3. Launch it — a plug icon appears in the menu bar. On first run it imports
   your existing connectors from Claude's config into the master list and
   takes a permanent snapshot of your original config.

There is no dock icon; the app lives entirely in the menu bar. Enable
**Launch at login** in Settings (⚙︎) if you want it always available. The
app checks for new releases on its own and offers each one in an update
window; Settings ▸ General ▸ Updates has a switch for installing them
automatically instead, and a **Check for Updates…** button.

#### Uninstalling

Quit the app, then remove:

    /Applications/Connector Control.app
    ~/Library/Application Support/Connector Control/   # master list + backups

Your claude_desktop_config.json keeps whatever connectors were enabled at
the time — the app leaves Claude's config valid on the way out.

### Windows

Requires Windows 10 version 1809 (build 17763) or later, or Windows 11, on
an x64 or Arm64 PC. The installer and the app are code-signed. While the
publisher is new to Microsoft's SmartScreen, Windows may still show
"Windows protected your PC" with the publisher named; choose **More info**,
then **Run anyway**. The warning goes away as the signature earns reputation.

1. Download `ConnectorControl-win-x64-Setup.exe` (Intel and AMD PCs) or
   `ConnectorControl-win-arm64-Setup.exe` (Arm PCs) from the
   [latest release](https://github.com/dlaporte/connector-control/releases/latest).
2. Run it. It installs for the current user — no administrator prompt —
   under `%LOCALAPPDATA%\ConnectorControl`, adds a Start menu entry (no
   desktop icon), and launches the app.
3. A plug icon appears in the system tray; open the `^` overflow if Windows
   tucked it away there. On first run the app imports your existing
   connectors from Claude's config into the master list; your first change
   takes the permanent snapshot of the original config.

Left-click the tray icon for the connector list; right-click it for
**Settings…** and **Quit**. Updates are offered, not installed silently: the
app checks GitHub for new releases and shows **Install and Relaunch** when
one is available (Settings ▸ General ▸ Updates has a switch to download and
install them automatically, and a **Check for Updates…** button). Turn on
**Launch at startup** there to have it always available.

#### Uninstalling

Settings ▸ Apps ▸ Installed apps ▸ Connector Control ▸ Uninstall removes
the program (`%LOCALAPPDATA%\ConnectorControl`). App data is left in place;
delete it yourself for a clean slate:

    %LOCALAPPDATA%\Connector Control\    # master list, backups, settings

Your claude_desktop_config.json keeps whatever connectors were enabled at
the time — the app leaves Claude's config valid on the way out.

## How it works

```
~/Library/Application Support/Connector Control/
├── mcps.json          ← master list: every connector + enabled flag (source of truth)
└── backups/           ← timestamped copies of both files, rotated; machine-local
    └── claude_desktop_config.original.json   ← first-run snapshot, never pruned

~/Library/Application Support/Claude/claude_desktop_config.json
                       ← generated output: only enabled connectors are written;
                         every other key in the file is preserved untouched
```

On Windows the same layout lives under `%LOCALAPPDATA%` (never the roaming
profile, so backups stay on the machine that made them):

```
%LOCALAPPDATA%\Connector Control\
├── mcps.json          ← master list (source of truth)
├── settings.json      ← app settings
└── backups\           ← timestamped copies of both files, rotated; machine-local
    └── claude_desktop_config.original.json   ← first snapshot, never pruned

%APPDATA%\Claude\claude_desktop_config.json   ← generated output, as above
```

Every change (toggle, edit, add, remove, restore) writes the master list and
regenerates the `mcpServers` section of Claude's config — atomically, after
backing both up. A reconciliation pass runs at launch, every time the popover or flyout
opens, and whenever either file changes on disk: connectors added outside the app
are imported, external edits are detected (and you're notified), and
connectors missing from Claude's config are flagged for restore rather than
ever being silently dropped. Claude only reads its config at startup, hence
the Restart Required flow.

On Windows, **Restart Claude** asks Claude Desktop to end its session cleanly
(the same request Windows sends at sign-out) and relaunches it from its Start
menu entry. Older builds of Claude Desktop kept a virtualized copy of the
config under `%LOCALAPPDATA%\Packages\Claude_…\LocalCache\Roaming\Claude\`;
the app manages that copy only when it exists, and Settings ▸ Claude lets
you point it at any file.

### Settings

<table>
  <tr>
    <td align="center"><img src="docs/screenshots/mac-settings-general.png" width="290" alt="Settings, General tab: launch at login, confirm before restarting Claude, confirm before quitting, notify about outside changes, and update options."><br><sub>General</sub></td>
    <td align="center"><img src="docs/screenshots/mac-settings-storage.png" width="290" alt="Settings, Storage tab: the master list location (here a OneDrive folder) and the backup retention count with Reveal in Finder and Restore buttons."><br><sub>Storage</sub></td>
    <td align="center"><img src="docs/screenshots/mac-settings-claude.png" width="290" alt="Settings, Claude tab: the Claude app path and a Tools table showing whether npx, node, uvx and uv are installed where Claude can find them."><br><sub>Claude</sub></td>
  </tr>
</table>

**General** covers launch-at-login, the confirmation prompts, outside-change
notifications, and updates. **Storage** is where the master list lives (point
it at a synced folder to share connectors across machines) and how many
backups to keep. **Claude** lets you choose which Claude app to restart and
shows whether the launchers connectors depend on are installed.

### Syncing across machines

Settings ▸ Storage ▸ **Master List Location** ▸ choose a folder inside your
synced location (a git repo, iCloud Drive, OneDrive, Dropbox). The app adopts an
mcps.json already there, or seeds the folder with your current list. Other
machines running Connector Control point at the same folder and pick up
changes live (the file is watched). Notes:

- The whole file syncs — including enabled/disabled state.
- Connector env vars (API keys!) sync too. Use a private repo, or keep
  secrets out of synced connectors.
- A change that arrives through the synced folder is written into Claude's
  config and announced by name — which connectors it added, removed or
  changed — whether or not Claude is running at the time. Every connector
  is a command Claude runs, so treat write access to the synced folder as
  you would treat access to the machines that follow it.
- Conflicts are your sync tool's department; local backups make any bad
  merge recoverable.
- A Mac and a PC can share one list, but connector commands are OS-specific:
  a Mac writes remote connectors as `npx mcp-remote …`, Windows writes them
  as `cmd /c npx mcp-remote …`, and local servers carry their own paths.
  Each app preserves the other platform's entries untouched — an entry may
  simply fail to start in Claude on the other OS until you edit it there.
  Because cmd.exe re-parses everything after `cmd /c`, the Windows editor
  refuses a URL, header name or OAuth client field containing `& | < > ^ "`
  or a space for such a connector; use the JSON view if you really need one.
- A shared **collection** document behaves better across platforms: it
  stores remote connectors in a neutral form, so a Mac author's remote
  connectors start on Windows and a Windows author's start on a Mac, each
  app writing its own launcher. Local servers travel as written and carry
  the platform they were authored on; on the other OS their row shows
  "authored on macOS" or "authored on Windows".
- The same `cmd /c` rule applies to a connector arriving from a document,
  not just to one typed into the editor: Windows skips a remote connector
  whose Server URL, header name or OAuth client field contains
  `& | < > ^ "` or a space. The Import sheet lists it as skipped with the
  field at fault, and every later update from that document leaves it out.
  The same document imports whole on a Mac, which writes bare `npx`.
- An app older than collections, sharing the same master list, has no idea a
  collection is synced and edits it as an ordinary one. This app then reads
  those edits as a pending update from the source, and applying reverts
  them; use Make Local Copy… or Stop Syncing first if you want to keep them.
- collections.json travels with mcps.json. The per-machine facts — where a
  source document sits, which folder a collection publishes to — live in a
  collections-local.json that stays out of the synced folder, as backups do.

## Building from source

### macOS

Requires Xcode 15.4+ (Swift 5.10). Command Line Tools alone can compile the
app but cannot run the test suite.

    git clone https://github.com/dlaporte/connector-control.git
    cd connector-control
    swift test                # the full suite, no network, never touches your real config
    ./scripts/build-app.sh    # → build/Connector Control.app (ad-hoc signed)
    cp -R "build/Connector Control.app" /Applications/

For development against a throwaway config instead of your real one:

    mkdir -p .sandbox/store
    cp "$HOME/Library/Application Support/Claude/claude_desktop_config.json" .sandbox/
    CONNECTOR_CONTROL_CLAUDE_CONFIG="$PWD/.sandbox/claude_desktop_config.json" \
    CONNECTOR_CONTROL_STORE_DIR="$PWD/.sandbox/store" \
    swift run ConnectorControl

### Windows

Requires the .NET SDK 10.0.400 or a later 10.0.x (`windows/global.json`).
The solution also builds — but the app cannot run — on a Mac or Linux with
the same SDK, which is how the shared Core tests run on both.

    dotnet test windows/ConnectorControl.slnx          # Core + app tests, no network
    dotnet run --project windows/src/ConnectorControl.App

The same `CONNECTOR_CONTROL_CLAUDE_CONFIG` and `CONNECTOR_CONTROL_STORE_DIR`
overrides point a development run at a throwaway config. Installers are
built by `windows/scripts/package.ps1` (Velopack, `vpk` at the version
pinned in `windows/.config/dotnet-tools.json`), which is also what the
in-app updater consumes.

Releases are produced by [`.github/workflows/release.yml`](.github/workflows/release.yml)
on version tags — both platforms from one tag, onto one GitHub release: the
universal Mac build, Developer ID signing with hardened runtime, Apple
notarization of both the app and the DMG, stapling, and the Sparkle
appcast; and the Windows installers for x64 and Arm64, code-signed with
Azure Artifact Signing and checked by a silent install on a Windows runner.

**Preview builds** (both apps): push a `preview-<n>` tag, or run Actions ▸ Preview ▸
Run workflow with a number (a dry run by default). A preview builds `<next>-preview.<n>`,
where `<next>` is the top `## vX.Y.Z` heading of CHANGELOG.md, signs and notarizes the
Mac app and signs the Windows installers exactly like a release, and publishes them as
one GitHub prerelease. Stable users are unaffected: a prerelease is never
`releases/latest`, so the Mac update feed does not change, and a Windows preview install
follows previews only. A `preview-dry-<n>` tag builds everything and publishes nothing.
The Mac job runs in the `signing` environment, whose deployment branch policy must allow
`preview-*` tags and any branch previews are cut from.

The release, preview and Windows CI workflows all call one Windows build definition,
[`windows-build.yml`](.github/workflows/windows-build.yml), and every
workflow's own YAML and shell/PowerShell scripts are linted by
[`infra-ci.yml`](.github/workflows/infra-ci.yml).

### Scripts

| Script | What it does | Who calls it |
| --- | --- | --- |
| `scripts/build-app.sh` | Assembles `build/Connector Control.app` from the SwiftPM build products, embedding Sparkle and the app icon. | `mac-ci.yml`, `release.yml` |
| `scripts/make-dmg.sh` | Packages the app bundle into a drag-to-Applications DMG. | `mac-ci.yml`, `release.yml` |
| `scripts/test-mac.sh` | Runs the Swift suite the way CI gates it (no test may skip or fail). | `mac-ci.yml`, `release.yml` |
| `scripts/generate-icon.swift` | Renders the app icon — macOS `.icns` or Windows `.ico`, chosen by the output extension. | `scripts/build-app.sh`; the `.ico` path is run by hand, on a Mac |
| `scripts/mac/import-signing-cert.sh` | Imports the Developer ID certificate into a throwaway CI keychain. | `release.yml` |
| `scripts/mac/notarize.sh` | Submits a binary or app bundle for Apple notarization and staples the ticket. | `release.yml` |
| `scripts/mac/make-appcast.sh` | Builds and EdDSA-signs the Sparkle appcast for one release. | `release.yml` |
| `scripts/release/changelog-section.sh` | Prints one version's CHANGELOG.md section. | `release.yml` |
| `scripts/release/preview-notes.sh` | Prints the release notes for a joint preview build. | `preview.yml` |
| `scripts/release/ensure-release.sh` | Creates a GitHub release, or reuses one a previous run already created. | `release.yml`, `preview.yml` |
| `scripts/release/upload-release-assets.sh` | Uploads one build's Velopack assets to an existing release. | `release.yml`, `preview.yml` |
| `scripts/release/verify-release.sh` | Verifies a release's draft/prerelease flags and asset set. | `release.yml`, `preview.yml` |
| `windows/scripts/package.ps1` | Publishes and Velopack-packs one Windows runtime. | `windows-build.yml` |
| `windows/scripts/smoke-test.ps1` | Installs a packed `Setup.exe` and proves the app starts, stays up, and (with `-SignatureOnly`) is signed. | `windows-build.yml` |
| `windows/tools/probe-claude.ps1` | Manual diagnostic for how Claude Desktop installs and is found on a PC. | run by hand, on Windows |

## Scope and caveats

- Manages the `mcpServers` section of Claude **Desktop**'s config only — not
  claude.ai web connectors, Claude Desktop extensions, or Claude Code's MCP
  configuration.
- Neither app is sandboxed: each needs to read and write Claude Desktop's
  config file and to quit and relaunch Claude.
- Restarting Claude interrupts any in-progress conversation; the app asks
  first by default (Settings ▸ General).
- Windows: Claude Desktop is found by its app package (`Claude_pzs8sxrjxfjjc`)
  or, for older installs, its program folder; if Claude does not come back
  after a restart the app says so rather than guessing.

## License

[MIT](LICENSE) — © 2026 David LaPorte
