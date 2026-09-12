# UnityRift MCP server

A small [Model Context Protocol](https://modelcontextprotocol.io) server that
exposes `UnityRiftCLI` as tools, so an AI agent (e.g. Claude Code) can run
exports, inspect asset listings, and read the CLI's log output directly.

It is a single zero-dependency Node script (`unityrift-mcp.mjs`) speaking the
MCP **stdio** transport (newline-delimited JSON-RPC 2.0). No `npm install` needed.

## Requirements

- Node.js 18+ (developed against Node 24).
- A built `UnityRiftCLI` (e.g. `UnityRiftCLI/bin/Release/net9.0/UnityRiftCLI.exe`).
  Build it with `dotnet build UnityRiftCLI -c Release -f net9.0`.

## Tools

| Tool | Purpose |
|------|---------|
| `asset_help` | Print the full CLI help / option reference. |
| `asset_info` | Load asset file(s)/folder and list counts per asset type (`-m info`). |
| `asset_export` | Convert/export assets. Covers export / exportRaw / dump / extract / live2d / splitObjects / animator modes, grouping, formats, filters. |
| `asset_dump` | Dump assets to text (`-m dump`). Best for inspecting fields, incl. type-tree-stripped builds via `typetree_db`. |
| `dotnet_list` | List the game's .NET assemblies and types (`-m dotnet`). Managed folder auto-detected from the game folder / an asset file. |
| `dotnet_type` | Dump .NET type(s) as C#-like class stubs, optionally with IL (`-m dotnet --dotnet-type`). Can also write `.cs` stub files. |
| `asset_run` | Run the CLI with a verbatim argument list (escape hatch). |
| `list_output` | Recursively list files in an output folder with sizes. |

`asset_info`, `asset_export`, and `asset_dump` accept `typetree_db` (path to a `.tpk`
type tree database) and `assembly_folder`. `typetree_db` lets stripped builds be
read/dumped; omit it to use the `classdata.tpk` bundled next to the CLI. Custom
MonoBehaviour fields additionally require `assembly_folder`.

`dotnet_list` / `dotnet_type` browse the game's managed code. Give them the game
folder, its `*_Data` folder, the `Managed` folder, or any asset file inside the
game; the `Managed` folder is located automatically (or pass `assembly_folder`).
**IL2CPP games** work too: when there is no `Managed` folder, the IL2CPP binary
(`GameAssembly.dll` / `libil2cpp.so`) and `global-metadata.dat` are processed with
[Cpp2IL](https://github.com/SamboyCoding/Cpp2IL) into metadata-only stub
assemblies (types, fields, signatures, RVAs; no method bodies). The result is
cached under `%LOCALAPPDATA%\UnityRift\il2cpp`; the first run takes
~10-60 s and a few GB of RAM. `asset_info` / `asset_export` / `asset_dump` accept
`il2cpp: true` to use those stubs for custom MonoBehaviour fields.

Native reverse-engineering of the compiled binary (the Ghidra/Il2CppDumper package,
decompilation helpers, Frida hooks, and protocol/wire-layout analysis) is **not** part of
this server — it lives in the separate **unityWyvern** project.

Every CLI-invoking tool returns the exact command line, the exit code, elapsed
time, and the combined stdout+stderr (ANSI stripped) — i.e. the CLI's own log.

## Configuration (environment variables)

| Variable | Default | Meaning |
|----------|---------|---------|
| `UNITYRIFT_CLI` | first existing `UnityRiftCLI/bin/Release/{net9.0,net8.0,net472}/UnityRiftCLI.{exe,dll}` | Path to the CLI. A `.dll` is launched via `dotnet`. |
| `UNITYRIFT_OUT` | `<os-temp>/unityrift-mcp-exports` | Default export output folder. |
| `UNITYRIFT_TIMEOUT` | `300` | Default per-command timeout (seconds). |

## Registration

Registered for this repo via `.mcp.json` at the project root. In Claude Code,
project-scoped MCP servers must be approved once (you'll be prompted on the next
session start after this file appears), and a **new session / reload** is needed
before the tools become callable — servers are discovered at startup.

To use it from another MCP client, point the client at:

```
node tools/mcp/unityrift-mcp.mjs
```

## Notes

- The server spawns the CLI directly (no shell), so `asset_run` args are not
  subject to shell injection.
- Only protocol JSON is written to stdout; diagnostics go to stderr.
- The CLI itself only reads inputs and writes exports/dumps; it does not delete
  source assets.
