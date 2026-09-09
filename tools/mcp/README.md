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
| `il2cpp_export` | Generate an Il2CppDumper-compatible Ghidra package (`script.json`, `il2cpp.h`, `ghidra.py`) from GameAssembly/libil2cpp (`-m il2cpp`). |
| `il2cpp_lookup` | Translate managed names ↔ RVAs/VAs while decompiling (`-m il2cpp --il2cpp-lookup`). |
| `il2cpp_strings` | Search IL2CPP string literals by text (`-m il2cpp --il2cpp-strings`). |
| `il2cpp_decode` | Decode a raw hex immediate into the float/double/int constant(s) it really is (`--il2cpp-decode`). |
| `il2cpp_data` | Resolve a `DAT_<addr>` literal-pool load to its constant by reading the binary (`--il2cpp-data`). |
| `il2cpp_clean` | Strip IL2CPP boilerplate from Ghidra pseudocode and annotate constants inline (`--il2cpp-clean`). |
| `il2cpp_suggest` | Suggest `Type$$`/`Type$$Method` symbols to decompile from keywords or a script file (`--il2cpp-suggest`). |
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

`il2cpp_export` writes the Ghidra helpers next to those stubs (`<output>/il2cpp/script.json`,
`il2cpp_ghidra.h`, and a `ghidra/` folder with `ghidra.py` / `ghidra_with_struct.py`). Import
the native binary into Ghidra, parse `il2cpp_ghidra.h`, then run the script and pick `script.json`.
`il2cpp_lookup` / `il2cpp_strings` translate names and addresses while you decompile
(`il2cpp_lookup` takes `use_fuzzy` for typo-tolerant name matching). To read the actual
game-logic numbers, `il2cpp_decode` turns a raw hex immediate into its float/double value
and `il2cpp_data` reads the constant behind a `DAT_<addr>` load from the binary;
`il2cpp_clean` makes a decompiled function readable (drops IL2CPP boilerplate, annotates
constants); and `il2cpp_suggest` maps a feature you're chasing ("parry", "adrenaline") to
the `Type$$` symbols worth decompiling.

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
