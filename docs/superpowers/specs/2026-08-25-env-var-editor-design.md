# Env Var Editor: In-Place Rows Design

**Date:** 2026-08-25
**Status:** Approved

## Problem

In the edit sheet's Form view, environment variables are added through a
permanently visible two-field adder row (`EnvAdder`). Under
`.formStyle(.grouped)` those text fields render borderless, so the "NAME" and
"value" placeholders read as inert gray text — users don't realize they are
fields. The name field is also hard-fixed at 130 pt, too narrow for common
names like `GITHUB_PERSONAL_ACCESS_TOKEN` (the display column for saved
variables truncates at 130 pt too).

## Decision

Drop the adder row and match the Arguments section's pattern: a
`＋ Add variable` caption button that appends a row whose name and value are
edited in place.

## Design

All changes live in `Sources/ConnectorControl/EditSheetView.swift`. No Core
(`FormModel` / `FormMapper`) changes.

### Data model (UI-only)

A dictionary key can't back an editable name field — each keystroke would
re-key `form.env`, re-sort the `ForEach`, and drop focus. Instead the sheet
holds:

```swift
struct EnvRow: Identifiable, Equatable {
    let id = UUID()
    var name: String
    var value: String
}
@State private var envRows: [EnvRow]
```

- Initialized from `form.env` (sorted by name) in `init`.
- Rebuilt in `adoptForm` (JSON → Form switch).
- `form.env` is re-derived from the rows whenever they change
  (`.onChange(of: envRows)`): trim each name, drop empty-name rows, last
  duplicate wins in the dict. Save and the Form → JSON switch therefore see a
  correct dictionary with no other plumbing.

### UI

- Each row: monospaced name field (placeholder `NAME`, flexible width) +
  value field (placeholder `value`), keeping the existing eye-reveal and
  ✕-remove buttons.
- `EnvAdder` is deleted. Below the rows sits `＋ Add variable`, styled
  identically to `＋ Add argument`. Clicking appends an empty row and focuses
  its name field (`@FocusState` keyed by row id).

### Behavior

- **Reveal state** re-keys from name to row `UUID` (names are now mutable).
  New rows start revealed — the user is typing a fresh value, matching the
  old adder's plain TextField. Rows loaded from config start hidden, as
  today. `adoptForm` clears reveal state (row ids are regenerated anyway).
- **Duplicate names:** Save shows the existing inline validation error
  ("Duplicate environment variable name: X") instead of silently
  overwriting. A Form → JSON switch with duplicates still collapses
  last-wins (same semantics as the old adder's silent overwrite).
- **Empty-name rows** are dropped when the dict is derived — same as an
  unsubmitted adder row being lost today, except a typed name now persists
  in the UI until save.

## Verification

Existing tests are unaffected (no Core changes). Verify with a clean
`swift build` plus a manual run of the app.
