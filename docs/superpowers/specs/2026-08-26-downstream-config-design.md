# Claude's Config Is Downstream

**Date:** 2026-08-26
**Status:** Approved

## Principle

Connector Control's store (`mcps.json`) is the single source of truth — it
changes only through the app's UI or through store-side sync. Claude's config
is build output: `render(store)` = every enabled connector's config. Any
divergence between the file and the render is resolved by regenerating the
file, never by adopting file changes into the store — with one exception,
ingestion of unknown entries (below).

This replaces the missing-connectors banner (Restore / Mark Disabled) and the
Reconciler's file-wins rules with the app's one familiar affordance: the
footer's **Restart Required** (or **Apply Failed — Retry**) button, identical
to a user-initiated toggle.

## Reconciler (Core)

`Reconciler.reconcile` keeps only the ingestion rule:

- **Unknown entry** in the file (name absent from the store) → imported as a
  new enabled connector, *unless* it exactly matches the baseline — that is a
  pending in-app deletion mid-apply, and re-importing it was a real
  resurrection bug once. `isExternalAddition` and its baseline guard survive
  unchanged.
- **Known entries:** no config adoption (`isExternalChange` deleted), no
  re-enable of disabled connectors (`isExternalReappearance` deleted), no
  reaction to removals. The store is never modified by the file except by
  ingestion.
- `ReconcileOutcome` loses `missingEnabled` — divergence is computed by the
  caller as `servers != store.enabledServers`.

New Core helper: `MasterStore.enabledServers: [String: JSONValue]` — the
render. Replaces the three hand-rolled
`mcps.filter(\.value.enabled).mapValues(\.config)` copies in
`ConfigService.apply`, `AppState.isDirty`, and `AppState.performApply`.

First-run bootstrap is unchanged: empty store + populated file → everything
ingested (nil baseline), no divergence afterward. Corrupt-store rebuild
(nil-baseline import) likewise. `storeAuthoritative` reloads use the file as
baseline, so nothing is ingested and the adopted store wins totally.

## AppState.reload

After reconcile, when `claudeServers` was readable:

1. `diverged = servers != store.enabledServers` (post-ingestion store).
2. `appliedServers = servers`, `hasLoadedOnce = true` (as today).
3. If `diverged` → `performApply()` — regenerates the file from the store and
   arms Restart Required / Apply Failed — Retry. Applies on *every* reload,
   including first launch (a config wiped while the app was off heals at
   launch) and `storeAuthoritative` reloads (an adopted synced store
   regenerates immediately; `adoptExternalStoreChange`'s own
   `if isDirty { performApply() }` becomes redundant and is removed, and
   `repointStore` gains the same regeneration for free).
4. Notification, at most one per reload:
   - regenerated externally-caused divergence (`diverged && wasLoaded &&
     !storeAuthoritative`) → "Claude's config was changed outside Connector
     Control — regenerated from your connector list. Restart Claude to pick
     it up."
   - else the existing `claudeConfigChangedExternally` /
     `storeChangedExternally` messages, unchanged.

No loops: apply leaves the file equal to the render, so the watcher-triggered
follow-up reload finds no divergence; store persists from ingestion trip the
store watcher into a `storeAuthoritative` reload that ingests nothing.

**Exception:** a malformed (unparseable) Claude config is still left alone
(note + Backups ▸ Restore…, as today) — regenerating would require reading
the file to preserve non-MCP keys, which is exactly what can't be done.

## Backups ▸ Restore (Claude series)

A restore is a deliberate user action that makes the snapshot the truth, so
`ConfigService.restoreClaudeConfig` adopts the snapshot INTO the store
(previously it leaned on file-wins reconciliation, which store-wins would
undo on the next reload): entries present in the snapshot are upserted
(config from the snapshot, enabled, `lastEditView` preserved for known
names); known entries absent from the snapshot are disabled — not deleted;
the store is persisted. This guarantees `enabledServers` equals the restored
servers, so no divergence follows and the restore sticks.

## Deletions

- `Reconciler.isExternalChange`, `isExternalReappearance`,
  `ReconcileOutcome.missingEnabled`, and the `missingEnabled` element of
  `loadAndReconcile`'s tuple.
- `AppState`: `@Published missingEnabled`, `restoreMissing()`,
  `markMissingDisabled()`, the `firedMissingNotification` logic.
- `PopoverView`: the yellow `missingBanner` and its row.
- Menu bar icon: `exclamationmark.triangle.fill` now signals
  `applyRetryNeeded` (the one persistent problem state left) instead of
  `missingEnabled`.

## Testing

Reconciler and ConfigService are pure Core — new semantics get tests first
(TDD): store wins on external edit, disabled entries stay disabled on re-add,
removals leave the store untouched, unknown entries ingest, the
pending-deletion guard holds, `enabledServers` renders the enabled subset.
`ConfigServiceTests` (first-load import, wipe recovery) updated to the new
tuple shape. AppState/PopoverView wiring verified by build + manual run.
