import Foundation

enum Fixtures {
    static let realisticClaudeConfig = """
    {
      "mcpServers": {
        "scoutbook": {
          "command": "npx",
          "args": ["-y", "mcp-remote", "https://scoutbook.example.com/mcp"]
        },
        "aws-mcp": {
          "command": "npx",
          "args": ["-y", "mcp-remote", "https://aws-mcp.us-east-1.api.aws/mcp"]
        },
        "service-now": {
          "command": "npx",
          "args": ["-y", "mcp-remote", "https://snow.example.com/mcp"]
        }
      },
      "coworkUserFilesPath": "/Users/someone/Documents/Claude",
      "preferences": {
        "coworkScheduledTasksEnabled": true,
        "sidebarMode": "epitaxy",
        "bypassPermissionsGateByAccount": { "024145b7": true },
        "epitaxyPrefs": { "rowSplit": 0.5, "draftNonce": 0 }
      },
      "someFutureKey": [1, 2, {"nested": null}]
    }
    """
}
