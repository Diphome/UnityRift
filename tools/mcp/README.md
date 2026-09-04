# AssetStudioCLI MCP server

A small [Model Context Protocol](https://modelcontextprotocol.io) server that
exposes `AssetStudioModCLI` as tools, so an AI agent (e.g. Claude Code) can run
exports, inspect asset listings, and read the CLI's log output directly.

It is a single zero-dependency Node script (`assetstudio-mcp.mjs`) speaking the
MCP **stdio** transport (newline-delimited JSON-RPC 2.0). No `npm install` needed.

## Requirements

- Node.js 18+ (developed against Node 24).
- A built `AssetStudioModCLI` (e.g. `AssetStudioCLI/bin/Release/net9.0/AssetStudioModCLI.exe`).
  Build it with `dotnet build AssetStudioCLI -c Release -f net9.0`.

## Tools

| Tool | Purpose |
|------|---------|
| `asset_help` | Print the full CLI help / option reference. |
| `asset_info` | Load asset file(s)/folder and list counts per asset type (`-m info`). |
| `asset_export` | Convert/export assets. Covers export / exportRaw / dump / extract / live2d / splitObjects / animator modes, grouping, formats, filters. |
| `asset_run` | Run the CLI with a verbatim argument list (escape hatch). |
| `list_output` | Recursively list files in an output folder with sizes. |

Every CLI-invoking tool returns the exact command line, the exit code, elapsed
time, and the combined stdout+stderr (ANSI stripped) — i.e. the CLI's own log.

## Configuration (environment variables)

| Variable | Default | Meaning |
|----------|---------|---------|
| `ASSETSTUDIO_CLI` | first existing `AssetStudioCLI/bin/Release/{net9.0,net8.0,net472}/AssetStudioModCLI.{exe,dll}` | Path to the CLI. A `.dll` is launched via `dotnet`. |
| `ASSETSTUDIO_OUT` | `<os-temp>/assetstudio-mcp-exports` | Default export output folder. |
| `ASSETSTUDIO_TIMEOUT` | `300` | Default per-command timeout (seconds). |

## Registration

Registered for this repo via `.mcp.json` at the project root. In Claude Code,
project-scoped MCP servers must be approved once (you'll be prompted on the next
session start after this file appears), and a **new session / reload** is needed
before the tools become callable — servers are discovered at startup.

To use it from another MCP client, point the client at:

```
node tools/mcp/assetstudio-mcp.mjs
```

## Notes

- The server spawns the CLI directly (no shell), so `asset_run` args are not
  subject to shell injection.
- Only protocol JSON is written to stdout; diagnostics go to stderr.
- The CLI itself only reads inputs and writes exports/dumps; it does not delete
  source assets.
