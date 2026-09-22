# Changelog

Release notes for Connector Control on macOS and Windows. The section
matching the tagged version becomes the GitHub release notes and the
text both apps show in their update dialogs — the release build fails if
the section is missing. The top section also names the version a preview
build is cut from (a preview is versioned as that number followed by
-preview.N, and the preview build fails if that version has already been
released), so open the next version's section as soon as the previous
one ships. A freshly opened section starts with four sub-headings, in
this order: `### macOS`, `### Windows`, `### Both platforms`, `###
Release pipeline`; leave a sub-heading's bullets empty rather than
removing the sub-heading, and drop any sub-heading that never got a
bullet once the section is about to ship.

## v1.4.0

### macOS

### Windows

- The check that refuses a Server URL, header name or OAuth client field containing
  `& | < > ^ "` or a space now covers connectors arriving from a shared collection, not
  only ones typed into the editor. Such a connector is listed in the Import sheet as
  skipped, with the field at fault, and is left out of the import and of every later
  update from that collection: Claude Desktop hands the `cmd /c npx` launcher's
  arguments to cmd.exe unescaped. A Mac writes bare `npx`, so the same document imports
  whole there.

### Both platforms

- Profiles are now collections. Every profile you had is a collection with the same name
  and the same connectors, and the master list file is unchanged.
- Collections can be shared. Export writes a collection as a document wherever you
  choose, and whoever receives that file imports it either as copies into a collection
  of their own or as a collection of its own that stays in sync with the file. The
  Import sheet lists every connector first — new, already present, or skipped and why —
  and nothing happens until you say so.
- Where an imported connector's name is already taken you choose what happens to it:
  replace it, which keeps the secrets and paths you had filled in; keep both, which
  lands the new one beside the old as `<name> 2`; or leave it alone. Imported connectors
  arrive switched off, and each one records the collection it came from, its author and
  the day it arrived.
- A synced collection follows its document. Its connectors are read-only — their names,
  commands, arguments, URLs and auth belong to the author — while what is yours stays
  yours: the values the document asks this machine for, the paths you point at, and
  which connectors you have turned on. When the author changes the file the collection
  says so, and Review & Apply shows the before and after of every connector it would
  add, remove or change; nothing lands until you apply it, and your filled values follow
  their placeholder wherever the author moved it. Refresh re-reads on demand, Locate…
  finds the file again on a new machine, and Stop Syncing keeps everything the
  collection holds and makes it yours. A file that is half-written, or that has not
  synced down yet, is retried quietly before anything is reported.
- A placeholder is a value a shared document asks each machine for instead of carrying
  it. A connector waiting on one says "needs your value" or "needs your path" until it
  is filled, with the author's hint beside the field, and `${COLLECTION_DIR}` in a
  synced collection resolves to the folder that collection's document sits in — so one
  shared list can point at each machine's own copy of a server.
- Publish… points a local collection at a folder in a repository or a synced drive and
  rewrites the document there whenever what the collection runs changes, so a team
  follows your list without anyone sending a file. The sheet lists every environment
  value and every argument that looks like a path on this machine: tick the ones that
  should travel as values, and the rest travel as a placeholder under a name and hint of
  your choosing. The document itself is underneath, so you see every byte before it
  leaves, with anything that looks like a credential called out; nothing is ever edited
  on your behalf. Turning a connector on or off never republishes.
- A publish that fails says so on the collection banner, with Choose Folder… and Stop
  Publishing beside it, and pressing Publish again retries the write there and then.
  Stop Publishing, and deleting a published collection, ask whether to remove the
  document from the shared folder as well; keeping it is the default answer.
- A Collections window lists every collection beside its connectors and marks which one
  is active, which are synced, which one this machine publishes, and whether a synced
  collection's file has been found. New, Rename, Delete, Import, Subscribe, Export,
  Publish, Refresh, Make Local Copy, Stop Syncing and Stop Publishing all live in its
  toolbar, which follows the selection. A connector's switch can be flipped in any
  collection from there; only the active collection reaches Claude, so a toggle
  elsewhere is saved and nothing is restarted.
- The collection chip carries a chain when the collection you are in comes from a shared
  document, and an amber dot when that document has changes waiting for you. Its menu
  switches collections, marks the synced ones the same way, and offers Import…, Export
  and Manage Collections…. A collection with news says so above the connector list, with
  the one button that answers it.
- Connectors in a synced collection show a lock in the list and open locked in the
  editor, where only the values the document asks this machine for can be typed into and
  "What can I change?" spells out the rule. Add Connector is disabled there, saying
  additions go in a local collection, and Make Local Copy… takes the connectors you
  pick, or a whole collection, into a collection of your own, where they are yours to
  change.
- The editor says where a connector came from in one line above the fields: synced from
  a collection, published to a folder, or imported from another collection on a date. An
  ordinary connector in an ordinary collection says nothing and takes no room.
- Saving a connector that another local collection holds an identical copy of offers, in
  one checkbox, to apply the same change there too.
- A local server authored on the other platform is marked on its row, since a command
  written for a Mac rarely runs on a PC unchanged; remote connectors cross either way.
- A change that cannot be written to the collection list now says plainly that it is not
  on disk rather than looking saved, a list another machine changed is never quietly
  reverted, and a change made while a sync tool is writing that file no longer replaces
  it with an empty one.
- A folder the app watches — the master list's, or the one a synced collection's
  document sits in — that is deleted and recreated, or replaced wholesale by a sync
  client, is picked up again instead of leaving the app watching a folder that is no
  longer there.

### Release pipeline

- Preview builds now ship both apps: a `preview-<n>` tag, or Actions ▸ Preview ▸ Run workflow, publishes a signed, notarized Mac DMG beside the signed Windows installers as one GitHub prerelease versioned `1.4.0-preview.<n>`. Stable users see nothing: a prerelease is never `releases/latest`, so the Mac update feed is untouched. A Windows preview install updates itself to later previews and to the final release; a Mac preview is offered the final release when it ships.

## v1.3.3

### macOS

- The one-time permissions repair no longer records itself done when every repair
  failed; it retries at the next launch. It is tracked by a version number now, so an
  upgraded install runs it once more.
- The Restore… sheet reports a failure to list backups instead of showing an empty list.
- Repointing the master list to a folder that cannot be written keeps the current
  location and reports the failure instead of switching to an empty list.
- Deleting the folder that holds Claude's config or the connector list no longer
  leaves the file watcher stuck; it recovers when the folder reappears.
- An empty `CONNECTOR_CONTROL_CLAUDE_CONFIG` or `CONNECTOR_CONTROL_STORE_DIR`
  variable, or an empty stored master-list path, now counts as unset.
- The migration from the pre-1.0 app names is gone; no released build ever needed it.

### Windows

- Updates are offered, not installed silently, matching the Mac; the switch under
  Settings ▸ General ▸ Updates turns automatic installation back on.
- A Claude config reached through a symlink is written through to the real file
  instead of being replaced by a plain copy.
- The editor clears a bearer token, header or OAuth client secret when a JSON edit
  switches the auth type, instead of keeping it behind the new one.
- The Settings window no longer stalls while the Claude tab's icon loads or while a
  chosen launch target is verified.
- Previews of a connector's extra fields no longer show escaped slashes.
- Repointing the master list to a folder that refuses the write keeps the current
  location and reports the failure.
- A failed settings save shows in the flyout banner instead of passing silently.
- The one-time owner-only permissions repair is tracked by a version number; an
  upgraded install runs it once more.
- The tray icon is now the same plug as the Mac's menu bar icon, prongs to the right
  and cable to the left; it used to stand upright.
- A JSON object with a duplicate key now keeps the first value, matching the Mac; it
  used to keep the last.
- A declined update is no longer offered again by the background check; a manual
  check still offers it.

### Both platforms

- A stale backup that cannot be deleted no longer fails the save; it is retried at
  the next rotation.
- The messages for a Claude app that is not found, or not signed by Anthropic, end
  with the same sentence on both platforms.
- The error for an unparseable backup names the parser's complaint.

### Release pipeline

- The Windows build no longer downloads the previous release or builds a delta
  package; no client has applied one since v1.3.2.
- The smoke test asks the installed app to verify its own package with the code the
  updater runs, instead of re-implementing the publisher policy in PowerShell.
- The release and preview workflows share one secrets gate and one set of publish
  scripts; a new lint workflow checks every workflow and the scripts they call on push;
  the Mac CI build also produces the DMG. vpk is pinned in a tool manifest that
  Dependabot tracks.

## v1.3.2

Hardening from a security review of the app and its release pipeline.

- macOS: updates are offered, not installed silently. The app still checks
  for new releases on its own; installing one is now a click in the update
  window. Settings ▸ General ▸ **Automatically download and install
  updates** turns silent installs back on.
- Both platforms: a connector-list change that arrives through a synced
  master list is always announced once it has been written into Claude's
  config, whether or not Claude is running, and the notification names the
  connectors it added, removed or changed. Before, a change that landed
  while Claude was closed was applied with no notification at all.
- Both platforms: **Restart Claude** now checks that the app it is about to
  launch really is Claude Desktop signed by Anthropic (a code-signature
  check on macOS, an Authenticode check on Windows) before quitting the
  running Claude, and Settings ▸ Claude refuses a chosen app or program
  that fails it, saying why.
- macOS: every file the app writes is private from the instant it is
  created (the mode is set by the create call, not applied afterwards), and
  backups of a config file are snapshots of its bytes, so a Claude config
  symlinked into a dotfiles folder is backed up, restored and written
  through correctly instead of the link itself being copied or replaced.
- Both platforms: the one-time permissions repair at launch now touches only
  the app's own files in the master-list folder — mcps.json and any
  corrupt-file copies beside it — and never the folder's other contents.
  On Windows it also refuses to run on a drive root or a shell folder such
  as Documents or OneDrive, and no longer records itself as done when every
  step failed. Earlier builds rewrote the permissions of everything under a
  chosen master-list folder.
- Both platforms: the OAuth **Client Secret** field in the connector editor
  says that mcp-remote receives this value on its command line, where other
  programs running as you can read it, unlike the token and header fields.
- Windows: the app manifest declares that it runs as the signed-in user,
  which keeps Windows from ever treating it as an installer that wants
  elevation.
- Windows: the connector editor refuses a Server URL, header name or OAuth
  client field that contains `& | < > ^ "` or a space when the connector
  uses the `cmd /c npx` launcher, and says why: Claude Desktop hands that
  launcher's arguments to cmd.exe unescaped, so a pasted URL such as
  `https://host/mcp&<command>` would run the command. A URL with more than
  one `%` shows a caution instead. Connectors written as bare `npx` — the
  Mac's shape — are unaffected, because Claude escapes npx's arguments
  itself.
- Windows: every file the app writes is private from the instant it is
  created — the permissions travel with the create call rather than being
  applied afterwards — the folders it creates are private too, and
  replacing an existing file carries the new file's permissions instead of
  keeping the old file's. If the master-list folder refuses the permission
  change, the flyout says so instead of staying silent.
- Windows: **Restart Claude** checks the signer's organization rather than
  whether the word "Anthropic" appears anywhere in the certificate, and a
  Store-app launch target set by hand in settings.json must name a Claude
  Desktop package.
- Both platforms: editing a remote connector keeps its `mcp-remote@<version>`
  pin. Earlier builds rewrote the pinned package as plain `mcp-remote` on
  every save from the form view, silently dropping a version the user had
  chosen on purpose.
- macOS: files and folders the app creates carry no inherited ACL entries. A
  folder shared with an inheritable read entry (a Finder-shared folder, a
  `chmod +a … file_inherit` folder) used to hand that entry to every new
  master list, config and backup, and mode 600 does not override an ACL.
  Temp files are now born in the app's own private folder and renamed into
  place whenever the target is on the same volume, and a one-time pass
  strips inherited entries from files written by earlier builds.
- Windows: a downloaded update is installed only if every program and
  library inside it is validly signed — the app's own files and the updater
  by the same publisher as the running app, the .NET runtime and the
  libraries the app is built from by that publisher or by Microsoft or the
  .NET Foundation — and only if the app inside it carries the version the
  update feed advertises, so an older signed release cannot be replayed as
  new. Programs are recognized by their content, not their file name. A
  package that fails these checks is discarded, the previous updater is put
  back, and a notification says so, even for a background update. Updates
  now always download the full package rather than a delta, so nothing from
  the feed is processed before it is checked. Before, the feed's checksum was
  the only thing standing between a GitHub release and code running on your
  PC.
- macOS: Sparkle now verifies an update's signature before unpacking it and
  requires the update feed itself to be signed.
- Release pipeline: the Sparkle tools that sign the Mac update feed are
  verified against a pinned checksum before use; the Windows build receives
  only the six signing secrets it needs rather than every repository
  secret; a re-run never replaces an asset already published under a
  version; every GitHub Action is pinned to a commit, with Dependabot
  keeping the pins current, including the Windows NuGet packages and the
  Swift package.

## v1.3.1

- macOS: every file the app writes — Claude's config and the master list —
  now ends up readable by you alone, even when it already existed with
  wider permissions (a config Claude Desktop created, a master list checked
  out of a repo). Earlier builds set the private mode on the file they
  wrote and then let the replace restore the old, wider mode. The folders
  the app creates for the master list and backups are private from the
  start too, not only after the next launch's repair pass.
- macOS: the Restore Claude config sheet no longer keeps showing a
  previous attempt's error once you start another restore, and a restore
  that fails on an unreadable backup now says what was wrong with the file
  instead of showing a generic error code.
- Both platforms: when the master list was unreadable and Claude's config
  was malformed at the same time, the banner now reports both, including
  the Backups ▸ Restore… way out; before, only the first note was shown.

## v1.3.0

- Windows: Connector Control now runs on Windows. The new app lives in the
  system tray and brings the same connector list, editor, profiles, backups,
  self-healing and syncing to Windows 10 (build 17763 and later) and
  Windows 11, on x64 and Arm64 PCs. Install it from
  ConnectorControl-win-x64-Setup.exe (or the win-arm64 one) on the release
  page; it keeps itself up to date from then on. Both apps read and write
  the same mcps.json, so a master list synced between a Mac and a PC
  serves both — local-server commands stay OS-specific, see the README.
- Both platforms: missing-tool warnings for a connector that starts through npx, node,
  uvx or uv now show a caution glyph in the connector list when that
  tool isn't installed where Claude Desktop can find it. The editor explains
  what to install, with a download link and the brew (or winget)
  command, and Settings ▸ Claude ▸ Tools lists all four tools with their
  versions. On a Mac, a tool that only your shell can see (an nvm install,
  say) is flagged too, since Claude Desktop launches connectors with its own
  PATH.
- macOS: the Settings window is taller so the Claude tab fits without
  scrolling.

## v1.2.3

Reliability release: fixes from a full code review of 1.1.4–1.2.2.

- Environment variable editing no longer silently alters data: a value
  without a name blocks saving instead of vanishing, variable names are
  preserved exactly as written (no more surprise whitespace trimming), and
  switching to the JSON view enforces the same duplicate-name validation as
  Save.
- Notifications are truthful and complete: no "config regenerated" banner
  when the write actually failed (a failure now gets its own banner, once),
  a synced change that can't be applied is announced instead of silent, and
  banners now show even while Connector Control is the active app.
- Update feed hardening: version numbers (not build counters) order updates,
  and prerelease tags can no longer reach the auto-update feed.
- Deleting or breaking the connector-list file mid-session can no longer
  wipe Claude's config — the app restores its own list instead.
- Backup ordering is immune to daylight-saving clock rollbacks (timestamps
  are now UTC).

## v1.2.2

- Release notes now appear in the update dialog: the changelog is embedded
  into the update feed, so **Check for Updates…** shows what changed before
  you install.

## v1.2.1

- Settings cleanup: the About tab is gone — the current version now lives in
  Settings ▸ General ▸ Updates — and the settings window is taller so the
  General tab no longer needs a scrollbar.

## v1.2.0

- Built-in auto-update (Sparkle): updates download and install automatically,
  configurable in Settings ▸ General ▸ Updates. This release must be
  installed manually; later versions arrive on their own.
