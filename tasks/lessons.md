# Lessons

## 2026-07-17 — /resume debugging: two corrections in one incident

**What happened:** Claimed resume history was fixed after moving transcripts into the renamed project's folder. It wasn't — Claude Code's `/resume` also filters each transcript by fields inside it (`entrypoint` in {sdk-cli, sdk-ts, sdk-py}, `teamName`, `sessionKind` daemon, `isSidechain`, /loop marker). Separately, cleanup instructions I gave ("when you close that terminal: `mv …; rm -rf old-folder`") were run while the session was live and the only real interactive transcript (7MB, 88 prompts) was deleted; recovered from an APFS local snapshot.

**Rules:**
1. Never claim a fix for picker/UI behavior based on data-layout reasoning alone. Verify the actual filter logic (read the implementation) or have the user confirm before declaring success.
2. Never hand the user a `rm -rf` in prose, even with a precondition attached. Either perform the destructive step myself after verifying the precondition, or give only the non-destructive step and defer deletion.
3. When "history is missing," check what the user actually typed (`~/.claude/history.jsonl`) before assuming all transcript files are user sessions — most were SDK-spawned agent sessions that are hidden from /resume by design.
