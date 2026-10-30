# Changelog

Release notes for Connector Control on macOS and Windows. The section
matching the tagged version becomes the GitHub release notes and the
text both apps show in their update dialogs - the release build fails if
the section is missing. The top section also names the version a preview
build is cut from (a preview is versioned as that number followed by
-preview.N, and the preview build fails if that version has already been
released), so open the next version's section as soon as the previous
one ships. A freshly opened section starts with four sub-headings, in
this order: `### macOS`, `### Windows`, `### Both platforms`, `###
Release pipeline`; leave a sub-heading's bullets empty rather than
removing the sub-heading. The release build drops any sub-heading that
never got a bullet from the notes it ships, and fails if no sub-heading
has one.

## v1.5.1
- `connector-control collection` with no subcommand now lists the four it takes (new, rename, duplicate, delete), and a wrong one says it isn’t a collection command, instead of saying there is no command named “collection”. `connector-control help status` now says help takes no arguments.
- A collection with one connector now reads “1 connector, 1 on” in `connector-control status` and `collections` and in Claude’s answers, not “1 connectors”.

### macOS

- Restart Claude no longer says Claude “didn’t quit” when it actually did; Connector Control now reopens it.
- The JSON editor now supports undo (Cmd-Z) and no longer autocorrects or spell-checks what you type, so commands, arguments and header names stay as written.
- The menu bar icon now has a name for VoiceOver - “Connector Control”, or “Connector Control: Apply Failed - Retry” when an apply is waiting to be retried - and VoiceOver no longer reads the “▾” after the collection name in the popover.

### Windows

- Pressing Escape to close the editor’s Authentication drop-down now closes just the drop-down; before, it closed the whole editor and lost your unsaved changes. Escape in the flyout’s collection menu likewise closes only the menu. With nothing open, Escape still cancels the editor and dismisses the flyout.
- Check for Updates now reads “Checking…” and can’t be clicked while a check runs, instead of looking as if nothing happened. After Install and Relaunch, the update dialog stays open reading “Downloading…” until Connector Control relaunches, so the app no longer just vanishes; if the download fails, the dialog closes and says why.
- Narrator now names the flyout’s Restart Claude button and the collection switcher (with its waiting update), the Settings backup − and + buttons (which also dim at 5 and 100) and the Name field in New Collection, Rename and Duplicate. Each Use Default and Choose… button in Settings now says which row it belongs to.
- The Settings tabs now show their icons - a gear for General, a drive for Storage, and Claude’s own icon, or a generic app icon where Claude’s can’t be read - instead of an empty space before each name. Claude’s icon is now sharp at 150% and 200% scaling.
- The Publish and Export dialogs now fit on a short screen: when there isn’t room, the preview gets shorter so the Publish, Export and Cancel buttons stay visible.

### Both platforms

- Screen readers now name each row in the Publish sheet and the editor’s environment list: the share and path ticks and the hint boxes say which connector and variable they belong to, and each environment row’s name, value and delete button, and each argument’s delete button, carry the row’s number, instead of every row sounding the same. The Publish sheet’s Forget Mark, Release and Use ${COLLECTION_DIR} buttons now say which connector and path they act on.
- The Publish sheet no longer offers plain words such as `usr` or `tmp` as machine-specific paths. Which arguments it offers now depends only on how they’re written (a full path, `.` or `..`, or one that starts with `~`, `./` or `../`), not on how Connector Control happened to be started.
- Saving a connector with “Also apply this change to …” ticked now writes your connector list and takes a backup once, instead of once per collection. The in-between states no longer use up backup slots and push older history out.
- Commands that ask for something before they act now end in “…”, as Choose… and Restore… in Settings already did. New Collection, Rename, Duplicate, Make Local Copy, Import, Subscribe, Start Publishing, Publishing Settings, Export, Export All, Choose Folder, Locate and Review & Apply now read that way; commands that act at once, or only ask you to confirm, keep their plain names.
- When Claude or the command-line tool tries to add, change or delete a connector in a subscribed collection, the refusal now names the collection and says what to do instead (add the connector to a local collection, or make a local copy with Make Local Copy to change it), rather than repeating a window tooltip.
- Copy to now confirms a copy: the count under the collection’s name adds, for example, “Copied 2 connectors to “Work”” until you tick a row, run another command in the window or choose another collection. Before, the ticks just cleared and nothing said where the copies went.
- After you pick a Claude app in Settings ▸ Claude (on Windows, its launch target), the row now reads “Checking…” and Choose… is dimmed while its signature is verified, which can take a few seconds. Before, nothing changed on screen until the check finished, and a second click started a second check.
- Messages, tooltips and help text use a hyphen where they used an em-dash.
- When Claude or the command-line tool asks for a tool Connector Control doesn’t have, the answer now reads “There’s no tool named “…”.”, in the app’s own words and with the name shown safely, instead of a bare “Unknown tool: …”.

### Release pipeline

- A release now fails at its first step if its changelog section holds only the empty sub-headings, and drops any sub-heading with no bullets from the notes it publishes, so the GitHub release and both apps’ update dialogs never show bare headings.
- The Sparkle tools version used to sign the Mac update feed is now checked against the Sparkle the app links on every push that touches `Package.resolved`, so a Sparkle bump fails on its pull request instead of partway through a signed build.
- Every push now checks that no file contains an em-dash.

## v1.5.0

### macOS

- The command-line tool is inside the app, at `Contents/MacOS/connector-control`, and updates
  with it. Remove, beside Install, takes the /usr/local/bin link away again. A folder Install has
  to make there is one only an administrator can write to, whatever the machine's umask.
- Before you delete the app, turn off "Let Claude manage Connector Control" and Remove the
  command-line tool. Otherwise Claude keeps a connector-control connector whose tool is gone and
  shows it as failed, until you remove its entry from claude_desktop_config.json by hand. The
  Windows uninstaller does both for you.
- A preview install updates itself to each later preview and then to the final release, as on
  Windows; see Release pipeline for the one step to take by hand.
- Sparkle, the updater, is 2.10.1, up from 2.10.0.
- Claude's config, or a backup of it, is read as the Windows app reads it: one with a trailing
  comma, saved as UTF-16 or UTF-32, or nested more than 64 levels deep is reported as not valid
  JSON. Before, the Mac read it as if it were valid.

### Windows

- The command-line tool is copied to `%LOCALAPPDATA%\Connector Control\bin\` each time the app
  starts, and Claude's entry and your PATH use that copy, so an update never stops a relay Claude
  is running. Install adds that folder to the end of your user PATH and leaves every other entry
  exactly as written. It refuses while the copy is missing, and a PATH the registry holds as
  anything but text is left as it is.
- Uninstalling takes Connector Control's entry out of Claude's config, after a backup, deletes
  `bin\` and takes that folder out of your user PATH. It holds the app's single-instance lock
  meanwhile, so an app Claude starts during the uninstall can't put the entry back.
- An app the command-line tool or Claude's relay starts gets the PATH you get at sign-in, not the
  terminal's or Claude's, and none of their CONNECTOR_CONTROL_ variables, so it finds the same
  npx and uvx however it was started.
- A program that took the app's pipe name first, under another account, learns who connected to
  it and nothing more; it can't act as you.
- An update is installed only when the programs that must be Connector Control's own are signed
  by a certificate with the running app's whole subject, not merely its organization, and a
  certificate known to be revoked is refused. When the revocation servers can't be reached the
  update still installs, and crash.log says so once.
- An update package is accepted only as exactly the version the update feed names, preview label
  included, so a signed earlier preview can't be passed off as a later one or as the release.
- An update package the check can't finish, or one holding a file name Windows can't hold, is
  refused and deleted, and nothing the check didn't pass is applied when the app next starts.
  Before, such a package could be applied at the next launch. An update check cut short by
  quitting the app no longer leaves every later update refused: the app puts the installed updater
  back when it starts and before each check, and keeps its saved copy until that has worked.
- A release install takes only releases: a signed preview package is refused even if a feed
  offers it.
- The update dialog shows an image in the release notes as its text and loads nothing. Before, it
  fetched the image, from a network share too.
- The command interpreter and File Explorer are started by full path, from System32 and the
  Windows folder, so a cmd.exe or explorer.exe placed beside the app never runs in their place.
- The installer and the updater are Velopack 1.2.161, up from 1.2.158.

### Both platforms

- Claude integration, off until you turn it on: Connector Control can add a connector of its own,
  connector-control, to Claude's config, so Claude can list, switch and edit your connectors. The
  popover or flyout offers it once; the switch is in Settings ▸ Claude, where "Confirm changes Claude
  makes" is on by default.
- While that is on, Connector Control asks you before Claude adds, edits or deletes a connector,
  deletes a collection, imports, subscribes, applies an update or restores a backup, with Don't
  Allow the default; it always asks before Claude restarts itself. Turning a connector on or off and
  switching collections don't ask: a notification says what changed, as one does for every change
  Claude makes while confirmations are off.
- Only you can change the Claude integration, the confirmation, the notice of changes synced in
  from another machine, and automatic updates, in Settings or with the command-line tool; Claude
  can change the other settings.
- The connector-control command-line tool ships inside the app on both platforms, for scripts and
  MDM; `connector-control help` lists its commands. Settings ▸ Claude ▸ Command-Line Tool puts it in
  your PATH: on a Mac a link in /usr/local/bin, with an administrator prompt when that folder isn't
  yours; on Windows the tool's folder in your user PATH, which terminals opened afterwards see.
- If the active collection already holds a connector named connector-control, it keeps working,
  and the integration's switch is dimmed with "A connector named “connector-control” already
  exists. Rename it to turn this on." No other connector can take that name.
- Run the command-line tool as yourself. Under sudo, or in an elevated terminal on Windows, it
  starts nothing and says "Run connector-control without administrator rights: Connector Control
  runs as you, and an elevated copy can’t reach it." On Windows with the built-in Administrator
  account or UAC turned off, open Connector Control first, and the tool reaches it.
- A change Claude asked about is applied from exactly the file you were shown, even if the file
  changes in the moment after you click Allow; if it changed while the question was open, nothing
  is done and Claude is told to ask again.
- The Import and Restore sheets land the file you reviewed. Before, Import read the document again
  when you clicked it, and Restore read the backup only once you had confirmed, so a file
  rewritten in between landed unseen.
- A restore writes the backup as Connector Control read it, in the format every apply writes, and
  refuses a backup that lists the same name twice, which Connector Control and Claude would read
  differently. Before, it copied the backup byte for byte.
- A file that isn't valid JSON - Claude's config, a backup or a collection document - is reported
  in the same words on both platforms, and the message quotes nothing from the file.
- A setting changed with `connector-control settings set` shows at once in a Settings window that
  is already open.
- A collection file over 1 MB, a backup over 16 MB, or anything that isn't a regular file is
  refused as it is read, so a file swapped for a huge one can't stall the app.
- In the confirmation dialog each line of what would run is drawn on its own, clipped to its own
  height, so nothing in one line can draw over another, and it is selected and copied one line at a
  time. The block is one stop for Tab, and Up, Down, Page Up and Page Down move through its lines.
  Up to two accent marks per letter are drawn, so Thai, Vietnamese, pointed Hebrew and Arabic read
  as written; a third or later one, or one with no letter, shows as its code.
- A character that draws nothing shows as its code, so a name or variable that only gained one
  doesn't read as unchanged, and right-to-left letters in a line can't reorder the arguments
  around them.
- A name too long for a dialog keeps its start and its end around the "…", so long names that
  share a start still read apart. The name an MCP client gives itself is cut the same way, so it
  can't push what the client did out of a notification.
- Environment variables that name a program to run, such as GIT_ASKPASS, GIT_SSH_COMMAND,
  NODE_OPTIONS and LD_PRELOAD, always show in full in the dialog, though their names may look like
  a secret's, so you see what would run before you allow it. Claude still reads only that they are
  set.
- Claude never reads a secret packed into one argument: a whole shell line (`bash -c`, `cmd /c`),
  JSON passed as an argument, a URL password holding # ? or /, a header in any bridge's form, the
  token after Bearer, and text written against a `${CC_NEEDS:…}` placeholder all read as •••. A
  whole command line in one argument that holds a URL with a query reads as ••• in full.
- Claude's connection keeps working after Connector Control restarts, and its relay always ends
  once Claude closes it, whatever calls Claude cancelled on the way.
- Settings reads the same way everywhere: each row says what it is on the left, with a one-line note
  under it, and has its control on the right; every group has a heading, and every on/off setting
  is a switch. A missing tool's row stays one line, and how to install it is under the Tools box.

### Release pipeline

- Mac previews now update themselves, as Windows previews do: a preview build reads its own
  Sparkle feed (the `preview-feed` prerelease), which every published preview and every stable
  release replaces, never with an older build, so a preview install is offered each later
  preview and then the final release. Previews up to `preview-9` still read the stable feed;
  install the first preview after `preview-9` once by hand.
- The preview feed moves only to a build newer than every published preview and release, read
  from their own appcasts, and a served feed newer than all of them fails the run, so a planted
  feed can't stop preview updates while CI stays green.
- A preview's update dialog shows only that preview's changes; its release page adds how to
  install it, how to go back and the commits it holds.
- make-dmg.sh retries `hdiutil` when it reports the disk image "Resource busy".
- Stapling a notarized app or disk image is retried up to five times when Apple's ticket service
  fails, rather than failing the build.
- Every PowerShell script is parsed in CI, so one that doesn't parse fails there rather than on a
  Windows runner.
- The Windows install smoke test checks the command-line tool, the relay and the uninstall hook,
  uninstalling with the app still running.
- The MCP Inspector checks the relay against a headless test host on both platforms.
- The README's command list is tested against `connector-control help`'s own output.

## v1.4.0

### macOS

- Opening a connector whose editor is already open brings that editor forward even if the
  connector was switched on or off in the meantime, rather than opening a second editor for
  it, as on Windows.
- Escape cancels every sheet in the Collections window, and the connector editor, as their
  Cancel buttons do.
- A connector editor left open in 1.3 that macOS reopens after the upgrade closes itself,
  rather than stand empty: it cannot say which collection it was editing.
- In the Collections window, a click on a connector's tick or row no longer leaves the focus
  ring around its box. The ring shows only when the keyboard moves focus to a row (Tab into
  the list, or Up and Down), as the Windows row's focus rectangle does.
- The app updates itself with Sparkle 2.10.0, up from 2.9.6, and the release's update feed is
  signed with the Sparkle 2.10.0 tools to match.

### Windows

- The connector editor and the Collections window's dialogs open with the keyboard in their
  first text field, as on the Mac: the connector's name in the editor, the new collection's
  name in Subscribe.
- In the Collections window, Return on a collection in the sidebar makes it the active one,
  as a double-click does and as it already did on the Mac.
- Connectors arriving from a shared collection go through the same check as ones typed
  into the editor: a Server URL, header name, client ID or client secret containing
  `& | < > ^ "` or a space, or scopes containing any of those characters other than a
  space, is refused, because Claude Desktop hands the `cmd /c npx` launcher's arguments
  to cmd.exe unescaped. The Import sheet lists such a connector as skipped and names the
  field, and it stays out of the import and of later updates. A Mac writes bare `npx`,
  so the same document imports whole there.
- A connector launched through the command shell that uses `${COLLECTION_DIR}` is
  cautioned when the folder that token stands for holds `& | < > ^ "` or a space, as an
  ordinary Windows folder name may: cmd.exe would read those as commands.
- The installer, the updater and the small launcher beside the app are built for the PC's
  own processor, so an Arm PC runs them natively instead of under x86 emulation. They
  come from Velopack 1.2.158, up from 1.2.0, and the app's update code moves with them.

### Both platforms

- New Collection, under the Collections window's sidebar +, makes an empty collection and
  leaves the active one as it was, so Claude's config is not touched. Before, it copied the
  active collection's connectors and switched to the copy. Add connectors with the list's +,
  then make the new collection active once it holds what you want.
- "Delete" is the one word for deleting: the selection bar's button and its question
  ("Delete “<name>”?", "Delete 3 connectors?"), the published-file question and its
  button, the editor's Delete argument and Delete variable, "was deleted outside this
  editor", the Review sheet's **Deleted** group and the "deletes …" in a change summary
  all say Delete where they said Remove.
- In "Also delete <file> from the folder?", asked by Stop Publishing and by Delete of a
  published collection, Return and Escape both answer Keep. Delete, shown as the
  destructive answer, needs a click; before, Return deleted the file, and on the Mac
  Escape did nothing.
- In the Collections window, clicking a connector's row opens its editor, and the
  pencil at the end of the row is gone, so what the connector runs has the room. A click
  on or beside a row's tick ticks it instead. The rows highlight under the pointer, and
  from the keyboard the arrow keys move between them, Return opens the editor and Space
  ticks.
- Profiles are now collections. Every profile you had is a collection with the same name
  and connectors, and the master list file is unchanged.
- Export writes a collection, or the connectors you tick, as a document. Import brings
  one in as copies into a collection of your own, and Subscribe keeps it as a read-only
  collection that follows the file. The Import sheet (a dialog on Windows, as every
  sheet named here is) says what will happen to each connector before anything does; a
  file it cannot read shows why, with no Import button.
- An imported name that is already taken can replace the old connector, keeping the
  secrets and paths you filled in, land beside it as `<name> 2`, or be left out. Copies
  arrive switched off, and the editor names the collection each came from and the day it
  arrived.
- A subscribed collection is read-only apart from your secrets, your paths and which
  connectors are on: its rows show a lock, the editor opens locked and says what you can
  change, and Add Connector is disabled. If the author changes or deletes a connector
  while its editor is open, Save refuses and says which, rather than write the old
  version back. Make Local Copy takes the whole collection into one of your own.
- A change at a subscribed collection's source arrives for review: Review & Apply shows the
  before and after of every connector, nothing lands until you apply it, and your filled
  values survive. Refresh re-reads at once, Locate finds the file on a new machine, and
  Stop Syncing makes the collection yours, after asking.
- A placeholder is a value a document asks each machine for; a connector waiting on one
  says "needs your value" or "needs your path", with the author's hint.
  `${COLLECTION_DIR}` is the document's folder: where a subscribed collection found it, or
  the publish folder on the publishing machine. In a local collection this machine does
  not publish, the connector's row says it has no folder.
- Start Publishing writes a local collection's document to a folder in a repository or
  synced drive and rewrites it whenever what the collection runs changes; turning a
  connector on or off never republishes. Publishing Settings changes what a published
  collection shares. Deleting a published collection, or Stop Publishing in the
  Collections window, asks whether to delete the document too; Keep is the default.
- The Publish sheet lists every environment value, which travels as a hint unless you
  tick share value, and every argument that looks like a path on this machine, which
  travels as written unless you tick it to become a placeholder. The token, header value
  or client secret in a remote connector's Authentication fields always travels as a
  placeholder, and a preview shows every byte before it leaves, flagging any argument or
  shared value that looks like a credential, a header or URL query or fragment parameter
  named like a secret or holding one, a URL parameter whose name is itself a token, a URL
  path segment that looks like a key, and a URL with a user name or password in it. A
  value that only refers to a credential kept elsewhere (`Bearer ${API_TOKEN}`,
  `Bearer $API_TOKEN` with the name in capitals, a placeholder) "refers to a credential",
  and a URL user with no password "names a user"; either one later given the credential
  itself waits for review. Each URL path segment and bare parameter is a line of its own,
  so a new one that looks like a key waits for review too.
  A header passed as an argument is read as a header, with or without a space after the
  colon (`Authorization:Bearer …`), so mcp-remote's `--header "Authorization:${AUTH}"`
  reads "refers to a credential".
- The sheet also lists every mark it could not place and every path this machine keeps
  back that the document would otherwise carry as written, a copy of a ticked path in any
  field included, all at once, each with its connector and field; Publish and Export wait
  until every one is ticked where it now sits, forgotten or released. The folder a
  collection publishes into never travels: Use `${COLLECTION_DIR}` answers it by rewriting
  the connector.
- A path marked on your other machine, in a collection published from there, is kept back
  from every collection this machine publishes, and stays kept back once that machine drops
  the mark, stops publishing the collection or deletes it, even when the change arrives
  while this machine is off. Release it in this machine's Publish sheet to let it travel.
- A path you marked stays kept back until you release it, even after you forget its mark
  (Forget Mark takes the mark off the record, not the path off this machine's list), or
  the argument or the connector that held it is deleted and the sheet is published again
  with no row for it: put back later, it is refused and listed in the sheet. Before, that
  Publish forgot it, so the path travelled as written once it came back.
- A Release belongs to the collection it was given in. A collection made later under a
  deleted one's name, or renamed onto it, no longer inherits it: its sheet lists the path
  and Release answers it there. Before, the path travelled in the new collection unlisted.
- A publish that fails says so on the collection banner, with Choose Folder and Stop
  Publishing, and pressing Publish again retries the write at once. The banner goes if the
  collection stops publishing, or is deleted, on your other machine. A path marked for
  others to supply survives added, deleted or reordered arguments, an in-place
  correction and a connector rename; if the app can no longer place it, or the document
  would carry a kept-back path or the publish folder in another connector, publishing
  stops rather than write it, the document already in the folder is left as it was, and
  the banner names the connector to open Publishing Settings for.
- A publish that happens on its own no longer sends a connector you haven't reviewed, or a
  new credential in one you have. A connector added to a published collection, copied or
  imported into it, or added on your other machine, and a connector edited so it now holds
  something that looks like a credential, in an argument, a shared value, a header or its
  URL, hold the publish with "“<connector>” hasn’t been reviewed for publishing." or
  "“<connector>” may now carry a credential." and Publishing Settings; the folder keeps
  its document until you press Publish in the sheet, and automatic publishing then
  resumes. Publish reviews only what the sheet showed, so a connector that arrives while
  the sheet is open waits until the sheet is opened again. Other edits, renames and
  deletions publish on their own as before. Before, all of it was published on the next
  save without a preview.
- The Collections window lists every collection beside its connectors, and every control
  in it sits on what it acts on. The sidebar's + offers New Collection, Import ("Adds
  copies you own") and Subscribe ("Stays in sync, read-only"). The header shows the
  collection's name, an Active, Published or Subscribed pill where one applies, and a ⋯
  menu. Under the name it counts the collection's connectors, "14 connectors", and adds
  why a subscribed collection's source could not be read when that is so. The ⋯ lists
  only what applies to that collection: Make Active, Rename, Duplicate or Make Local
  Copy, Start Publishing or Publishing Settings and Stop Publishing, Show Published File
  or Show Source File, Refresh, Stop Syncing, Export All and Delete. The + beside the
  count, under the ⋯, adds a connector. Double-clicking a collection in the sidebar makes
  it active; double-clicking the active one does nothing.
- A change that alters nothing Claude runs, such as renaming or deleting a collection
  other than the active one, leaves Claude's config alone and raises no Restart Required.
- Each row says what its connector runs: a remote connector's host (with its scheme for
  one Claude reaches by URL alone, rather than through mcp-remote), or a local one's
  program, paths, URLs and package names. The column leaves out the values of flags named
  for secrets, `KEY=value` words, a URL's user and query, random-looking strings and
  anything it does not recognise. It is a best-effort mask, not a guarantee.
- Ticking rows fills the empty bar at the foot of the window with a selection bar. Copy
  to copies the ticked connectors into another local collection, or into a new one it
  asks you to name; a subscribed collection is listed but cannot take copies. The copies
  arrive switched off and record where they came from, and a name the destination
  already holds can replace the one there, land beside it as `<name> 2`, which is the
  default, or be left out. Replacing a connector in the active collection applies at
  once. Export writes the ticked connectors as a document.
- Delete takes out any number of ticked connectors at once. It asks first, naming the
  connector or the count, and says a copy remains in Backups; the connector editor no
  longer has a Delete button of its own.
- Duplicate copies a whole local collection into a new one exactly as it stands, each
  connector's switch included, without marking anything as imported. The copy does not
  become active, so Claude's config is unchanged.
- The popover (Mac) and flyout (Windows) now only run the active collection: they switch
  collections and turn connectors on and off. Adding and editing connectors moved to the
  Collections window, and the chip's menu lists the collections, then Manage Collections.
- The collection chip carries a chain when the collection you are in comes from a shared
  document, and an amber dot when that document has changes waiting. Its menu marks
  every subscribed collection too: on Windows with the chain and the dot; on the Mac
  with the chain as the row's icon and "· update available" after the name, since a
  macOS menu row draws one image. A collection with news says so above the connector
  list, with the button that answers it.
- Saving a connector that another local collection holds an identical copy of offers, in
  one checkbox, to apply the same change there too.
- The editor numbers its argument rows from 1, so a message naming "argument 2" points at
  the row marked 2; a screen reader names each field "Argument 2" and so on.
- Restoring a backup of Claude's configuration puts it back into the collection it was
  taken from, and makes that collection active. A backup from an earlier version carries
  no record, and goes into the active collection.
- Connectors found in Claude's configuration at launch are taken into the store only
  while the active collection is the one that file was written from, so a relaunch after
  another machine switched collections no longer pours one collection's connectors into
  another. Profiles have had that flaw since 1.1.
- A connector added to Claude's configuration outside the app, by an installer or a hand
  edit, while a subscribed collection is active goes into a local collection: the one
  Claude's configuration was last applied from if that is local, else the first local one
  by name, else a new, empty one named "Default" ("Default 2" and so on if that is
  taken). Before, it went into the subscribed collection, where it could be neither
  edited nor deleted, and the next Review & Apply deleted it from Claude. The notification
  names where it went; at launch, when nothing is notified, the banner does. The same
  holds when the collections file can't be read for a moment while the app is running, as
  when a sync tool is halfway through writing it.
- When the active collection has no connectors, the popover (Mac) and flyout (Windows) say
  "No connectors in “<collection>”." with a Manage Collections button beneath, which opens
  the Collections window on that collection. Before, they said "No connectors configured
  yet." and offered nothing.
- The Backups caption in Settings ▸ Storage names the three files backed up before every
  change: Claude’s config, the master list and collections.json. It said "Both config
  files".
- A backup that records no collection, such as the first-run original or one from an
  earlier version, is refused while a subscribed collection is active, before the
  confirmation: "“<name>” is subscribed, so its connectors are the author’s. Make a local
  collection active, then restore." Before, it was restored into the subscribed collection,
  where its connectors could be neither edited nor deleted.
- Deleting the active collection makes the first local collection with connectors active,
  by name, and falls back to the first remaining collection by name only when there is
  none. Before, the first remaining collection by name took over even when it was
  subscribed or empty, which emptied Claude's config.
- Delete Collection asks "Delete Collection “<name>”?" and says what goes with it: its
  connectors ("Its 3 connectors are deleted with it.", or "It has no connectors."), but not
  their copies in other collections, nor a subscribed collection's source file; the
  collection that becomes active, when it is the active one; and, when it held connectors,
  that a copy remains in Backups. Before, it said only "Its connector list is removed;
  backups keep prior states."
- A master list that can't be read is restored from its newest backup that can be, so
  every collection, subscription and published setting survives. It is still kept aside
  as `mcps.corrupt.<time>.json`, and the banner says "…and restored from the backup of
  <date and time>." Only when no backup can be read is it rebuilt from Claude's config, as
  before, which kept only the connectors Claude was running.
- Import's "Add to a collection" starts on the collection selected in the Collections
  window when that one is local, rather than always on the active collection, and its
  target picker ends with New Collection, which asks for a name and imports the copies
  into a new, empty local collection that does not become active. An empty name is refused,
  as Copy to ▸ New Collection refuses it, and the collection is made only when the import
  lands: a document that can no longer be read by then makes nothing, and the sheet says
  why.
- In a subscribed collection, a local server authored on the other platform is marked on
  its row; remote connectors cross either way.
- A folder the app watches - Claude's config folder, the master list's, or a subscribed
  collection's - that is deleted and recreated, or replaced wholesale by a sync client,
  is picked up again.
- Two backups taken in the same millisecond list, restore and prune newest first;
  before, the newer one could list below the older and be pruned in its place.
- A collections file with nothing in it, which a launch writes when nothing is
  published or subscribed, is read as empty. Before, it was taken for an unreadable
  file from the second launch on, and Publish and Subscribe refused with "Collections
  could not be saved: the collections file is unreadable."

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
  the app's own files in the master-list folder - mcps.json and any
  corrupt-file copies beside it - and never the folder's other contents.
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
  one `%` shows a caution instead. Connectors written as bare `npx` - the
  Mac's shape - are unaffected, because Claude escapes npx's arguments
  itself.
- Windows: every file the app writes is private from the instant it is
  created - the permissions travel with the create call rather than being
  applied afterwards - the folders it creates are private too, and
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
  library inside it is validly signed - the app's own files and the updater
  by the same publisher as the running app, the .NET runtime and the
  libraries the app is built from by that publisher or by Microsoft or the
  .NET Foundation - and only if the app inside it carries the version the
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

- macOS: every file the app writes - Claude's config and the master list -
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
  serves both - local-server commands stay OS-specific, see the README.
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
  wipe Claude's config - the app restores its own list instead.
- Backup ordering is immune to daylight-saving clock rollbacks (timestamps
  are now UTC).

## v1.2.2

- Release notes now appear in the update dialog: the changelog is embedded
  into the update feed, so **Check for Updates…** shows what changed before
  you install.

## v1.2.1

- Settings cleanup: the About tab is gone - the current version now lives in
  Settings ▸ General ▸ Updates - and the settings window is taller so the
  General tab no longer needs a scrollbar.

## v1.2.0

- Built-in auto-update (Sparkle): updates download and install automatically,
  configurable in Settings ▸ General ▸ Updates. This release must be
  installed manually; later versions arrive on their own.
