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
config as generated output, and backs up each file it manages — Claude's
config, the master list and collections.json — before writing to it, so a
wiped or mangled config is always one click from restored.

<p align="center">
  <img src="docs/screenshots/mac-popover.png" width="344" alt="The Connector Control popover on macOS: a collection chip and the active collection's connectors with on/off toggles.">
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
- **Automatic backups** — timestamped copies of Claude's config, the master
  list and collections.json, each taken before that file is written
  (configurable retention, plus a permanent first-run snapshot), with in-app
  restore.
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
complete list of connectors and enabled flags. The popover (Mac) or flyout
(Windows) runs the active one. A chip in its header — `<collection name> ▾`
— shows which collection that is and opens a menu of every collection, a
check on the active one, then **Manage Collections**. Choosing a collection
switches to it, which applies immediately, same as any other change, and
raises **Restart Required** just like a toggle would. The switches below the
chip turn the active collection's connectors on and off. That is all the
popover or flyout does: adding, editing, copying and sharing connectors
happen in the Collections window.

<p align="center">
  <img src="docs/screenshots/mac-chip-menu.png" width="344" alt="The collection chip's menu on macOS: the collections to switch between, a check on the active one and a chain on a subscribed one, then Manage Collections.">
</p>

A collection is one of three kinds.

**Local** collections are the ordinary kind, and everything in them is
editable. Every profile from an earlier version is now a local collection
with the same name and the same connectors. The last local collection can't
be deleted, so there is always somewhere to add a connector.

**Subscribed** collections are read-only mirrors of a collection document
somebody else publishes. You fill in the values the author left for you and
switch connectors on and off; nothing else can be edited, and nothing can be
added — the Collections window's **+** above the connector list is dimmed
with the tooltip "Additions go in a local collection." A chain glyph follows
the collection's name on the chip and in the Collections window's sidebar,
with the source file's path in its tooltip, and an amber dot joins it while
an update is waiting to be reviewed. In the chip's menu on a Mac the chain
is the row's icon and a waiting update reads "· update available" after the
name, because a macOS menu row draws one title and one image; the Windows
menu draws the chain and the dot. Every row of a subscribed collection
carries a lock.

**Published** collections are local collections that also write their
document to a folder whenever their content changes. They carry no glyph on
the chip or in the sidebar; the Collections window's header marks them
**Published**. Where the document goes is a fact about one machine, so only
the machine that publishes it says so: the Collections window's ⋯ menu offers
Show Published File, and the editor has a line at the top: "Published to
<folder> — saving updates the file your team reads. Secrets stay here."

**Manage Collections** opens the Collections window. Every control in it
sits on the thing it acts on. What this README calls a sheet — Import,
Copy, Publish, Export, Review — opens as a dialog on Windows. On a Mac,
Escape cancels any of these sheets, and the connector editor, as its
**Cancel** button does.

- **The sidebar** lists the collections under the heading **Collections**.
  Its **+** (tooltip "Add Collection") offers **New Collection**, which
  starts a collection as a copy of the active one's connectors and makes it
  the active one, then **Import**, "Adds copies you own", and **Subscribe**,
  "Stays in sync, read-only" (see Sharing a collection with a team).
  Selecting a collection only shows it; double-click it, or choose **Make
  Active** from its context menu, to switch to it. Double-clicking the
  collection that is already active does nothing. Renaming or deleting a
  collection other than the active one changes nothing Claude runs, so it
  leaves Claude's config alone and raises no **Restart Required**.
- **The header** carries the selected collection's name, its pills — a green
  **Active** on the collection Claude is running, then **Published** or
  **Subscribed**, never both — and a **⋯** (tooltip "More") holding
  everything done to the collection itself. Under the name, the header
  counts the collection's connectors — "5 connectors". On a subscribed
  collection whose source couldn't be read, the reason follows the count: "5
  connectors · data-team.json couldn’t be read: …". The menu lists only what
  applies: **Make Active** first, on a collection that is not active;
  **Rename**; **Duplicate**, or **Make Local Copy** on a subscribed
  collection; **Start Publishing**, or once it publishes **Publishing
  Settings** and **Stop Publishing**, with **Show Published File** on the
  machine that writes the file; on a subscribed collection **Refresh** and
  **Show Source File** once its file has been found, and **Stop Syncing**,
  which asks first; **Export All**, dimmed on a subscribed collection; and
  **Delete**, which asks first. Duplicate and Make Local Copy both copy the
  whole collection into a new local one, every connector switched off and
  marked with where it came from.
- **The connector list** has its **+** under the header's ⋯, beside the
  count (tooltip "Add Connector"). It opens the editor on a new connector in
  this collection. Each row has a tick — a lock on a subscribed collection —
  then the connector's name, a caution glyph when something needs your
  attention, and what the connector runs. Click a row anywhere but its tick
  to open its editor (read-only on a subscribed collection); a click on or
  near the tick ticks it. From the keyboard, the arrow keys move between
  rows, Return opens the editor and Space ticks. Names line up in a column
  as wide as the longest of them, up to a cap past which a long name is cut.
  There is no switch here and no right-click menu: whether a connector is on
  is the popover's or flyout's business, so to switch a connector in another
  collection, make that collection active first.
- **The selection bar** along the bottom is empty while nothing is ticked.
  Tick rows and it reads "2 selected", with **Copy to**, **Export** and,
  apart at the far end, **Remove**. The rows of a subscribed collection
  can't be ticked.

The column after the name says what the connector runs: a remote
connector's host, or a local one's program with its paths, URLs and package
names — `npx …/server-filesystem ~/Documents`. It is built to leave secrets
out. It shows the program's name, a URL as its scheme, host and port, a
file path with your home folder shortened to `~`, and a package's name. It
leaves out whatever follows a flag named like a secret (anything with token,
key, secret, pass, pwd, pw, auth, credential or bearer in it), any
`KEY=value` word, a URL's user name and password and its query string, long
random-looking strings, and everything it does not recognise, flags
included. It is a best-effort mask, not a guarantee: a secret shaped like a
path or a package name, or one following a flag that is not named like a
secret, still shows, as does one written into a real hostname, or a
hyphenated word in the place where npx or uvx names the server it runs.
Check the column before you share a screenshot of the window.

**Copy to** lists every other collection — a subscribed one is listed but
dimmed and marked "read-only", since its connectors are the author's — then
**New Collection**, which asks for a name and makes an empty local
collection to take the copies. Copies arrive switched off and record where
they came from. When the destination already holds a name you ticked, the
Copy sheet asks about each clash: **Replace**, **Keep both**, which lands the
copy as `<name> 2` and is the default, or **Skip**; the rest are listed as
"new · arrives off". Replacing a connector in the active collection applies
at once. **Export** opens the
Export sheet on the ticked rows. **Remove** asks first — "Remove
“<name>”?", or "Remove 3 connectors?" — adding "A copy remains in Backups.";
removing from the active collection applies at once.

<p align="center">
  <img src="docs/screenshots/mac-collections-window.png" width="620" alt="The Collections window on macOS: collections in the sidebar with a chain on the subscribed one; the selected collection's name with its pills and a more menu; its connectors with ticks, names, what each one runs and edit pencils; and the selection bar along the bottom.">
</p>

The master list file (mcps.json) is v2 (collection-aware); older v1 files,
from a build before 1.1, are simply rebuilt from Claude's current config the
same way any corrupted file is (see How it works). Beside it, a
collections.json records which collection is which kind, what each one
still needs, and each published collection's settings, including the text
of every path you marked. **If you sync mcps.json across machines, every
machine must run 1.1 or later** — an older app can't parse the v2 file and
will treat it as corrupt.

### Sharing a collection with a team

A **collection document** is a single JSON file describing a collection's
connectors. Publishing writes that file to a folder and keeps it current;
subscribing follows it. No network access is involved: git, OneDrive, Google
Drive, Dropbox or a file you hand over carry the document, exactly as they
carry the master list today.

#### Publishing

Select a local collection in the Collections window and choose **Start
Publishing** from its **⋯**. Point it at a folder in a repository or a synced drive — the master list's
own folder and the backups folder are refused — and the app writes
`<collection-name>.json` there, the name lowercased with every run of other
characters collapsed to a hyphen. The file name and the document's identity
are fixed the first time you publish and never re-derived, so renaming the
collection later does not rename the file.

The sheet decides what leaves the machine:

- **Environment values · stripped unless shared** lists every variable of
  every connector with a **share value** tick. Unticked, which is the
  default, the row takes your hint: only the variable's name and that hint
  travel, and each subscriber supplies the value. Ticked, the row shows the
  value that will travel, and the value goes into the document.
- **Machine-specific paths · found in arguments** lists every argument that
  looks like a path on this machine, each with a tick, a placeholder name
  and a hint. Unmarked, which is the default, it travels as written. Marked,
  it travels as a placeholder every subscriber fills in for themselves.
- **Document preview** is the document itself, exactly as it will be
  written. An argument or shared value that looks like a credential is
  listed under the preview, each line naming the connector it came from.
  Nothing is ever edited on your behalf; the preview is there so you see
  every byte before it leaves.

Once the collection publishes, the same menu's **Publishing Settings**
changes what is shared or re-marks a path: the sheet reopens on the folder and every tick the collection
publishes with, and pressing Publish there updates what is shared and
rewrites the document.

The bearer token, custom header value or OAuth client secret set in a
remote connector's Authentication fields always becomes a placeholder,
whatever you tick. A header typed straight into the arguments travels as
written, so check the preview for one; the warnings under it catch a value
that looks like a credential. Enabled flags never travel, so turning a
connector on or off never rewrites the document.

`${COLLECTION_DIR}` is the other way to keep a path out of a document. Write
it into a local server's command, arguments or environment values and each
subscriber's app expands it to the folder their copy of the document sits
in — so a team that keeps the document beside the server in one repository
shares one entry:

    "command": "node",
    "args": ["${COLLECTION_DIR}/ledger/dist/index.js"]

On the machine that publishes the collection, `${COLLECTION_DIR}` stands for
the publish folder, so a tool you keep beside the document runs from there
from the moment publishing starts. The published document still carries the
token as written, and each subscriber resolves it against their own copy of
the folder. In a local collection this machine does not publish — never
published, stopped, or published from your other machine — the token has no
folder: Claude gets it unexpanded, and the connector's row says
"${COLLECTION_DIR} has no folder until this collection is published from
this Mac." ("this PC" on Windows). Stop Publishing leaves the token in place
rather than writing the folder into those connectors.

The document is rewritten whenever what the collection runs changes, by the
machine that published it. **That machine has to be running for a change to
reach the team**: an edit made on your other machine travels through the
master list and is published the next time the publishing machine sees it. A
write that fails puts "Couldn’t publish …" on the banner with **Choose
Folder**, and in the popover or flyout **Stop Publishing** beside it (in the
window, Stop Publishing is in the **⋯**). The Publish sheet stays open on
a failure, and pressing Publish there, or in the sheet opened again later,
retries at once; otherwise the next change retries the write.

Export writes the same document once, wherever you choose, with the same
preview and warnings. The **⋯** menu's **Export All** writes the whole
selected collection; the selection bar's **Export** writes only the rows you
tick. A subscribed collection is the author's document already, so Export
All is dimmed on one and its rows can't be ticked.

#### Subscribing

**Subscribe**, under the sidebar's **+**, picks a document and opens the
Import sheet on **Keep as its own collection, in sync with this file**;
**Import** opens the same sheet on the other mode. Either way the sheet names the document and lists its
connectors before anything happens; a file it cannot read shows why
("<file> couldn’t be read: …"), with no Import button. A subscribed
collection arrives with every connector switched off and does not become
the active one.

What is the author's: names, commands, arguments, URLs and auth. They open
locked in the editor under a grey line reading "Synced from <name> ·
read-only", with a **What can I change?** link that spells out the rule.
What is yours: the values the document asks this machine for, and which
connectors are on. If the author changes or removes a connector while its
editor is open, **Save** refuses — "“<name>” changed outside this editor."
or "“<name>” was removed outside this editor." — rather than write the old
version back; reopen the editor to fill in your values again.

When the author changes the file the collection says so — "Data team changed
at its source: adds jira; removes confluence." — with **Review & Apply**.
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
folder the app finds it by itself; otherwise the collection shows "<name>’s
file isn’t on this Mac yet." — "this PC" on Windows — with
**Locate <file>**.

To take a subscribed collection somewhere you can edit it, choose **Make
Local Copy** from its **⋯**: it copies the whole collection into a new local
one, every connector switched off. **Stop Syncing** turns the collection
itself into an ordinary local one, keeping every connector, every value you
filled in and every switch; it asks first, and says as much. Deleting a
subscribed collection never touches the source file.

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
they live in your master list, they never go into a document, and they
survive every update from the source. A subscribed collection that uses
`${COLLECTION_DIR}` but has not found its file yet says "Locate the
collection file to resolve paths." on the rows that need it.

#### Trust

Every connector in a document is a command Claude runs. Anyone with write
access to the shared folder can change what Claude runs on every subscriber,
once those subscribers apply the change — the review step is the control.
A connector that runs a program kept in the shared folder through
`${COLLECTION_DIR}` is the exception: a change to that program reaches every
subscriber the next time Claude starts it, with nothing to review, because
the review covers the document and not the files it points at. Treat write
access to a published collection's folder as you would treat access to the
machines that follow it, including the one that publishes it.

#### Worth knowing

- **Stop Publishing** — and deleting a published collection — asks "Also
  remove <file> from the folder?", and keeping it is the default. Stop
  Publishing from the failed-write banner, or while the last write is
  failing, keeps the file without asking. Once the file is kept, publishing
  that collection into the same folder again is refused with "<file>
  already exists there and belongs to a different collection.": the
  leftover file carries the identity the collection had before, and the app
  never writes over a document it cannot vouch for. Delete the file from the
  folder, then publish again.
- A path you mark in the Publish sheet is remembered together with the text it
  had, so it stays a placeholder when you add, remove or reorder arguments
  around it, correct it in place in the editor's form, or rename the
  connector. If it changes somewhere the app cannot follow — the JSON view,
  a hand edit, or an older version of the app on any machine — and the app
  can no longer tell which argument it is, the app stops publishing that
  collection rather than send the path as written. The banner gives the
  reason, "A path marked in “<connector>” has moved. Open Publishing
  Settings to mark it again." Publishing stops the same way, each with a banner of its own,
  when the document would carry a path this machine keeps back, or this
  machine's publish folder, in some other connector. However it stops, the
  document already in the folder is left exactly as it was, and subscribers
  receive none of that collection's other changes until the entry is
  answered.
- Reopen **Publishing Settings** and the sheet lists every mark it could not place,
  and every path this machine keeps back that turns up elsewhere in the
  document, each with its connector and the field it sits in. Publish and
  Export stay unavailable until every entry is answered, and each kind of
  entry takes its own answer. A lost mark: tick the path where it now sits,
  or **Forget Mark**. A kept-back path: tick it where it sits, or
  **Release** it to let it travel as written once you have read the
  preview. This machine's publish folder: **Use ${COLLECTION_DIR}** alone,
  which rewrites that connector to the token. A folder is never released,
  because a document that would carry it is never written.
- A differently spelled version of a marked path is a different path to the
  app — another case, `~` in place of your home folder, a trailing slash.
  It is not recognised as the one you marked, so it travels as written;
  read the preview.
- Restoring a backup of Claude's configuration puts it back into the
  collection it was taken from and makes that collection active. A backup
  whose collection is gone is refused: "This backup was taken from
  “<name>”, which no longer exists. Nothing was restored. Create a
  collection named “<name>” again, and this backup goes back into it." Only
  this version records the collection, so a backup from an earlier one —
  which is every backup you already have, and the first-run original —
  goes into the active collection instead.
- An export of part of a published collection still carries that
  collection's identity, so your own app refuses to subscribe to it, as it
  refuses the published document itself.
- Replacing an imported copy keeps a filled value only where the author left
  the placeholder in the same place. A copy is not a subscription and has
  nothing recording where the value used to be; a subscribed collection
  does, and follows the move.
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
automatically instead, and a **Check for Updates** button.

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
**Open**, **Settings** and **Quit Connector Control**. Updates are offered, not installed silently: the
app checks GitHub for new releases and shows **Install and Relaunch** when
one is available (Settings ▸ General ▸ Updates has a switch to download and
install them automatically, and a **Check for Updates** button). Turn on
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
├── collections.json   ← collection kinds, needs and publish settings; moves with mcps.json
├── collections-local.json
│                      ← this machine's document and publish folders; never synced
└── backups/           ← timestamped copies of Claude's config, mcps.json and
                         collections.json, rotated; machine-local
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
├── collections.json   ← collection kinds, needs and publish settings; moves with mcps.json
├── collections-local.json
│                      ← this machine's document and publish folders; never synced
├── settings.json      ← app settings
└── backups\           ← timestamped copies of Claude's config, mcps.json and
                         collections.json, rotated; machine-local
    └── claude_desktop_config.original.json   ← first snapshot, never pruned

%APPDATA%\Claude\claude_desktop_config.json   ← generated output, as above
```

Every change (toggle, edit, add, remove, restore) writes the master list and
regenerates the `mcpServers` section of Claude's config — atomically, after
backing both up. Backups are named by the millisecond they were taken; two
taken in the same one are numbered, and still list, restore and prune
newest first. A reconciliation pass runs at launch, every time the popover or flyout
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
  refuses a URL, header name, OAuth client ID or client secret containing
  `& | < > ^ "` or a space, and OAuth scopes containing any of those but a
  space, for such a connector; use the JSON view if you really need one.
- A shared **collection** document behaves better across platforms: it
  stores remote connectors in a neutral form, so a Mac author's remote
  connectors start on Windows and a Windows author's start on a Mac, each
  app writing its own launcher. Local servers travel as written and carry
  the platform they were authored on; in a subscribed collection on the
  other OS their row shows "authored on macOS" or "authored on Windows".
- The same `cmd /c` rule applies to a connector arriving from a document,
  not just to one typed into the editor: Windows skips a remote connector
  whose Server URL, header name or OAuth client ID contains `& | < > ^ "` or
  a space, or whose OAuth scopes contain any of those but a space. The
  Import sheet lists it as skipped with the field at fault, and every later
  update from that document leaves it out. The same document imports whole
  on a Mac, which writes bare `npx`.
- The same goes for the folder `${COLLECTION_DIR}` stands for. On Windows a
  connector launched through `cmd /c` that uses the token shows a caution
  when that folder holds `& | < > ^ "` or a space: "The folder
  ${COLLECTION_DIR} stands for must not contain…". Folder names with spaces
  are ordinary on Windows, so a collection meant for PCs is best published
  into a folder without one.
- An app older than collections, sharing the same master list, has no idea a
  collection is subscribed and edits it as an ordinary one. This app then reads
  those edits as a pending update from the source, and applying reverts
  them; use Make Local Copy or Stop Syncing first if you want to keep them.
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
`releases/latest`, so the Mac update feed does not change. A Windows preview install
updates itself to later previews and to the final release; a Mac preview is offered the
final release when it ships, and each new preview is downloaded by hand. A `preview-dry-<n>` tag builds everything and publishes nothing.
The Mac job runs in the `signing` environment, whose deployment branch policy must allow
`preview-*` tags and any branch previews are cut from.

The release, preview and Windows CI workflows all call one Windows build definition,
[`windows-build.yml`](.github/workflows/windows-build.yml), and every
workflow's own YAML and shell/PowerShell scripts are linted by
[`infra-ci.yml`](.github/workflows/infra-ci.yml).

### Scripts

| Script | What it does | Who calls it |
| --- | --- | --- |
| `scripts/build-app.sh` | Assembles `build/Connector Control.app` from the SwiftPM build products, embedding Sparkle and the app icon. | `mac-ci.yml`, `preview.yml`, `release.yml` |
| `scripts/make-dmg.sh` | Packages the app bundle into a drag-to-Applications DMG. | `mac-ci.yml`, `preview.yml`, `release.yml` |
| `scripts/test-mac.sh` | Runs the Swift suite the way CI gates it (no test may skip or fail). | `mac-ci.yml`, `preview.yml`, `release.yml` |
| `scripts/generate-icon.swift` | Renders the app icon — macOS `.icns` or Windows `.ico`, chosen by the output extension. | `scripts/build-app.sh`; the `.ico` path is run by hand, on a Mac |
| `scripts/mac/import-signing-cert.sh` | Imports the Developer ID certificate into a throwaway CI keychain. | `preview.yml`, `release.yml` |
| `scripts/mac/notarize.sh` | Submits a binary or app bundle for Apple notarization and staples the ticket. | `preview.yml`, `release.yml` |
| `scripts/mac/make-appcast.sh` | Builds and EdDSA-signs the Sparkle appcast for one release. | `release.yml` |
| `scripts/release/changelog-section.sh` | Prints one version's CHANGELOG.md section. | `release.yml`, `scripts/release/preview-notes.sh` |
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
