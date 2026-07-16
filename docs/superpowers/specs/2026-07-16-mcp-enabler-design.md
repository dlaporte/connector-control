# MCP Enabler — Design Spec

(Renamed to Connector Control; earlier working names Custom Connector Control and MCP Enabler.)

**Date:** 2026-07-16
**Status:** Approved pending user review

## Overview

MCP Enabler is a lightweight, native macOS menu bar app for managing the MCP servers
defined in Claude Desktop's `~/Library/Application Support/Claude/claude_desktop_config.json`.
It lets the user enable/disable each MCP with a toggle, edit an MCP's configuration,
and add/remove MCPs — while aggressively protecting the configuration with backups.

Claude Desktop is known to sometimes overwrite or wipe `claude_desktop_config.json`
(documented in anthropics/claude-code issues #32345, #56296, #37286, #59368, #34359).
The design therefore treats the tool's own master store — not Claude's file — as the
source of truth, and every write path is backed up.

## Goals

- Toggle MCPs on/off from the menu bar; changes applied by writing Claude's config.
- Edit any MCP's full configuration (form view + raw JSON view).
- Add MCPs via "Add Remote" and "Add Local" pre-populated templates; remove with confirmation.
- Never lose an MCP definition: master store + timestamped backups of both files.
- Preserve every non-`mcpServers` key in Claude's config untouched (by value).
- Zero runtime dependencies: a single native SwiftUI `.app`.

## Non-Goals

- Managing Claude Desktop Extensions, claude.ai connectors, or Claude Code MCPs.
- Editing the `preferences` or other non-MCP keys in Claude's config.
- Windows/Linux support.
- Code signing / notarization / distribution (built and run locally).

## Architecture

Two layers:

1. **`MCPEnablerCore`** (framework/module, no UI): config models, JSON round-tripping,
   master-store persistence, reconciliation, backup management, atomic file writes,
   remote-pattern detection, form-representability analysis. Fully unit-tested.
2. **App layer** (SwiftUI): `MenuBarExtra`-based status item + popover, edit sheet,
   add flows, alerts, restart-Claude action, config file watcher.

Target: macOS 14+. No sandbox (needs to read/write another app's Application Support
directory and quit/relaunch Claude). Xcode project, Swift 5.10+.

### Files owned by the tool

```
~/Library/Application Support/MCP Enabler/
├── mcps.json                                  # master store (source of truth)
└── backups/
    ├── claude_desktop_config.original.json    # first-run snapshot, kept forever
    ├── claude_desktop_config.<ISO-timestamp>.json
    └── mcps.<ISO-timestamp>.json
```

### Master store schema (`mcps.json`)

```json
{
  "version": 1,
  "mcps": {
    "scoutbook": {
      "enabled": true,
      "config": {
        "command": "npx",
        "args": ["-y", "mcp-remote", "https://…/mcp"]
      },
      "lastEditView": "form"
    }
  }
}
```

- `config` is the MCP's verbatim server entry as it appears (or would appear) under
  `mcpServers` in Claude's config. Arbitrary/unknown keys inside it are preserved as-is.
- `enabled` controls whether the entry is written to Claude's config on Apply.
- `lastEditView` ∈ `"form" | "json"` — per-MCP memory of the last-used edit view.

## Claude config interaction

**Read:** parse `claude_desktop_config.json` as a generic JSON object. Only the
`mcpServers` key is interpreted; everything else is opaque.

**Write (Apply):**
1. Back up the current file to `backups/claude_desktop_config.<timestamp>.json`.
2. Re-read the file fresh (never write from a stale in-memory copy — Claude may have
   changed other keys since we last read).
3. Replace only the `mcpServers` value with the enabled subset from the master store.
4. Serialize pretty-printed with sorted keys and write atomically
   (temp file in the same directory → `rename`).

All other top-level keys (`preferences`, `coworkUserFilesPath`, unknown future keys)
are preserved by value. Formatting/key order may be normalized by re-serialization;
Claude itself rewrites the file, so this is acceptable.

**If the file is missing:** recreate it containing just `mcpServers` (enabled subset).
**If the file is malformed JSON:** refuse to write, show the parse error, and offer
restore-from-backup.

## Reconciliation (launch, refresh, and file-change events)

The app watches Claude's config file (DispatchSource file monitor) and reconciles on
launch, on watcher events, and whenever the popover opens:

- **MCP present in Claude's file but unknown to the master store** → imported as
  enabled only when it's genuinely external: no baseline (fresh launch) or an
  entry differing from the baseline. When the file entry matches the baseline
  exactly, the store-side absence is a pending removal awaiting Apply and the
  entry is NOT re-imported.
- **MCP present in both but with a different config in Claude's file** → if the
  file entry still matches the baseline, the store's version is a pending edit
  awaiting Apply and is kept; Claude's file wins only when the file entry itself
  changed vs. the baseline (external hand-edit), or on a fresh launch where no
  baseline exists. When the file wins, the master store is updated (backup of
  `mcps.json` taken first).
- **MCP present in Claude's file but marked disabled in the master store** → if
  the file entry matches the last-known state of Claude's file (baseline), this
  is a pending disable awaiting Apply and is left disabled; it is marked enabled
  only when the entry differs from — or is newly absent from — the baseline
  (evidence it was re-added or changed externally). On a fresh launch there is
  no baseline and the disable intent is preserved.
- **MCP enabled in the master store but missing from Claude's file** → do NOT silently
  delete from the master store. Flag it in the UI: the menu bar icon gains a badge and
  the popover shows "Claude's config is missing N MCP(s) [Restore] [Mark disabled]".
  This is the recovery path for Claude wiping the file.

The master store is never modified without first writing `backups/mcps.<timestamp>.json`.

## Backups

- Before **every** write to Claude's config: timestamped copy in `backups/`.
- Before **every** write to `mcps.json`: same.
- First run only: `claude_desktop_config.original.json`, never pruned.
- Retention: newest 20 of each series kept; older ones pruned automatically.
- Popover menu includes **Backups ▸ Reveal in Finder** and **Backups ▸ Restore…**
  (pick a backup, confirm, restore → offer restart).

## UI

### Menu bar popover

- Status item icon (SF Symbol, e.g. `switch.2`). Click → popover.
- One row per MCP: toggle, name, kind badge (`remote`/`local`), chevron → opens edit sheet.
- Toggling marks state dirty; footer shows **Apply** (writes config) when dirty.
- After a successful Apply: inline prompt **"Restart Claude Desktop to pick up changes?
  [Restart Now] [Later]"**. Restart = graceful terminate of the running Claude app
  (`NSRunningApplication.terminate()`), wait for exit (with timeout), relaunch via
  `NSWorkspace` (`/Applications/Claude.app`). Never force-kill.
- Footer also has: **＋ Add ▸ (Remote… / Local…)**, **Backups ▸**, **Launch at login**
  toggle (SMAppService), **Quit**.
- If reconciliation flagged missing MCPs, a warning banner appears at the top of the
  popover (see Reconciliation).

### Edit sheet (per MCP)

A sheet/window opened from an MCP row. Segmented control at top: **[ Form | JSON ]**.
Opens in the view recorded in `lastEditView`; switching updates that memory on save.

**Form view** — fields adapt to the detected type:

- *Remote (mcp-remote pattern)*: **Name** + **Server URL** only, with a hint
  ("Runs via npx mcp-remote — managed for you"). Detection rule: `command == "npx"`
  and `args` is exactly `["-y", "mcp-remote", <url>]` or `["mcp-remote", <url>]`.
  Anything else renders as the generic/local form.
- *Local / generic*: **Name**, **Command**, **Arguments** (editable rows: add,
  remove, drag-reorder), **Environment variables** (key/value rows; values masked
  with a reveal toggle).
- **Additional fields** (read-only, collapsed): any keys inside the MCP's `config`
  that the form has no widget for (e.g. `type`, `url`, `headers`) are listed here
  verbatim — "N fields not editable here: … Switch to JSON to edit." They are
  preserved untouched through form edits and saves.

**JSON view** — a monospaced editor containing the MCP's `config` object. Live parse
validation: while invalid, the error shows inline, **Save is disabled**, and the
toggle to Form view is disabled.

**View toggling rules:**

- Form → JSON: always allowed and lossless (form state serializes to the exact JSON
  that would be saved).
- JSON → Form, all content representable (including preservable Additional fields):
  switches silently.
- JSON → Form, content *structurally unrepresentable* (e.g. `args` contains
  non-strings, `env` values are not strings, `command` is not a string): a warning
  dialog lists exactly what would be lost or altered — e.g. "`args[3]` (nested
  object)" — with **[Stay in JSON] (default)** and **[Switch Anyway]**. Switching
  requires that explicit acceptance.

**Sheet footer:** **Remove…** (left, destructive, confirmation dialog; removed configs
remain recoverable via backups) — **Cancel / Save** (right). Save validates
(non-empty name, name unique, non-empty command or valid remote URL, JSON parses)
and writes the master store (with backup); Claude's config is written on Apply.

### Add flows

- **Add Remote…**: opens the edit sheet in Form view pre-populated with
  `command: "npx"`, `args: ["-y", "mcp-remote", ""]` — i.e. empty **Name** and
  **Server URL** fields ready to fill.
- **Add Local…**: opens the edit sheet in Form view pre-populated with
  `command: "npx"`, `args: ["-y", ""]`, empty env section — fields ready to point at
  a package or local binary/script instead.
- Both can flip to JSON view to paste a README snippet directly. Pasting an object of
  the form `{"mcpServers": {"name": {…}}}` is detected and unwrapped (name and config
  extracted) as a convenience.
- New MCPs default to enabled.

## Error handling

- All file writes are atomic (temp + rename), preceded by backups.
- Malformed Claude config → never overwritten blindly; parse error surfaced + restore offered.
- Claude app not found at relaunch → alert with the path tried.
- Master store corrupt/unreadable → rebuilt by importing Claude's current config;
  the corrupt file is preserved as `mcps.corrupt.<timestamp>.json`, and backups offered.
- Duplicate MCP name on save → validation error, Save blocked.

## Testing

`MCPEnablerCore` unit tests (XCTest) with fixtures, including a copy of the real
config shape (three mcp-remote servers + `preferences` + unknown keys):

- Round-trip: read config → toggle subset → write → all non-`mcpServers` keys
  preserved by value; disabled MCPs absent; enabled present verbatim.
- Reconciliation: external add, external edit, external wipe → correct master-store
  updates and flags; no silent deletions.
- Remote-pattern detection: positive and negative cases (extra args, different
  command, missing URL).
- Representability analysis: unknown keys → Additional fields; structural violations
  → correct lossy-change list.
- Backup rotation: 20 kept + original never pruned.
- Atomicity: write failure leaves the original file intact.

UI layer is kept thin; manual verification checklist for popover/sheet/restart flows.

## Settings

Five user-configurable settings, stored in `UserDefaults`:

| Setting | Key | Default |
|---|---|---|
| Master list location | `masterStoreDir` | absent (Application Support default) |
| After Apply behavior | `restartBehavior` | `"ask"` (`"ask"` \| `"auto"` \| `"never"`) |
| Claude app location | `claudeAppPath` | `/Applications/Claude.app` |
| Backup retention | `backupKeepCount` | `20` |
| Notify on external changes | `notifyExternalChanges` | `true` |

Notes:

- When the master list location is repointed to a custom (e.g. synced/cloud)
  directory, backups always stay machine-local — they are never redirected there,
  so a synced folder isn't filled with rotating backup files. Repointing seeds the
  new location from the current store if it has no `mcps.json` yet.
- `mcps.json` is watched for changes just like Claude's config file, so edits made
  by an external sync tool (e.g. a `git pull` or Dropbox update landing on the
  store file) are picked up and reconciled automatically.
- The `MCP_ENABLER_STORE_DIR` / `MCP_ENABLER_CLAUDE_CONFIG` environment overrides
  (used for sandboxed dev runs) always take precedence over the corresponding user
  settings.

## Build & run

Swift Package (`Package.swift`) with two targets — `MCPEnablerCore` (library, unit
tests) and `MCPEnabler` (executable app) — buildable and testable from the CLI with
`swift build` / `swift test`. A `scripts/build-app.sh` script assembles
`build/MCP Enabler.app` (Info.plist with `LSUIElement` so no Dock icon, ad-hoc
codesign); user copies it to `/Applications`. During development the app runs via
`swift run` against a sandboxed config path (env-var override) so the real Claude
config is never touched by tests or dev runs. Launch-at-login optional via in-app
toggle (requires running from the built .app bundle).
