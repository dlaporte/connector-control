# MCP Enabler

A lightweight macOS menu bar app to enable/disable/edit the MCP servers in
Claude Desktop's `claude_desktop_config.json` — with automatic backups of
everything it touches. See `docs/superpowers/specs/` for the full design.

## Build & install

    ./scripts/build-app.sh
    cp -R "build/MCP Enabler.app" /Applications/
    open "/Applications/MCP Enabler.app"

## Development

    swift test                      # unit tests (never touch real config)
    MCP_ENABLER_CLAUDE_CONFIG="$PWD/.sandbox/claude_desktop_config.json" \
    MCP_ENABLER_STORE_DIR="$PWD/.sandbox/store" \
    swift run MCPEnabler            # sandboxed dev run

## Data & backups

- Master MCP list: `~/Library/Application Support/MCP Enabler/mcps.json`
- Backups (last 20 per file + permanent first-run original):
  `~/Library/Application Support/MCP Enabler/backups/`
- Claude's config is rewritten only on Apply; every other key in it is preserved.
