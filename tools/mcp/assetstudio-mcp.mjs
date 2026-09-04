#!/usr/bin/env node
// AssetStudioCLI MCP server (zero-dependency, stdio transport).
//
// Exposes AssetStudioModCLI as MCP tools so an agent can run exports, inspect
// asset listings, and read the CLI's own log output. Communicates over
// newline-delimited JSON-RPC 2.0 on stdin/stdout (MCP stdio transport).
//
// Config via environment variables:
//   ASSETSTUDIO_CLI       Path to AssetStudioModCLI.exe or .dll. If it ends in
//                         .dll it is launched via `dotnet`. If unset, the first
//                         existing build output under AssetStudioCLI/bin is used.
//   ASSETSTUDIO_OUT       Default output folder for exports. If unset, a folder
//                         under the OS temp dir is used.
//   ASSETSTUDIO_TIMEOUT   Default per-command timeout in seconds (default 300).
//
// Nothing but protocol JSON is ever written to stdout; diagnostics go to stderr.

import { spawn } from "node:child_process";
import { existsSync, readdirSync, statSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, resolve, join, relative, sep } from "node:path";
import { tmpdir } from "node:os";

const __dirname = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(__dirname, "..", "..");

const DEFAULT_TIMEOUT = Number(process.env.ASSETSTUDIO_TIMEOUT) || 300;
const MAX_OUTPUT_CHARS = 60000;

// ---------------------------------------------------------------------------
// CLI resolution
// ---------------------------------------------------------------------------

function resolveCli() {
  if (process.env.ASSETSTUDIO_CLI) return process.env.ASSETSTUDIO_CLI;
  const candidates = [
    "AssetStudioCLI/bin/Release/net9.0/AssetStudioModCLI.exe",
    "AssetStudioCLI/bin/Release/net8.0/AssetStudioModCLI.exe",
    "AssetStudioCLI/bin/Release/net472/AssetStudioModCLI.exe",
    "AssetStudioCLI/bin/Release/net9.0/AssetStudioModCLI.dll",
    "AssetStudioCLI/bin/Release/net8.0/AssetStudioModCLI.dll",
  ];
  for (const c of candidates) {
    const p = join(repoRoot, ...c.split("/"));
    if (existsSync(p)) return p;
  }
  return null;
}

function defaultOutDir() {
  return process.env.ASSETSTUDIO_OUT || join(tmpdir(), "assetstudio-mcp-exports");
}

// Build the [command, args] pair, handling the .dll (run via dotnet) case.
function cliCommand(cliArgs) {
  const cli = resolveCli();
  if (!cli) {
    throw new Error(
      "AssetStudioModCLI not found. Build the CLI first (e.g. dotnet build " +
        "AssetStudioCLI -c Release -f net9.0) or set ASSETSTUDIO_CLI to its path."
    );
  }
  if (cli.toLowerCase().endsWith(".dll")) return ["dotnet", [cli, ...cliArgs]];
  return [cli, cliArgs];
}

const ANSI = /\x1b\[[0-9;]*m/g;

function clip(text) {
  if (text.length <= MAX_OUTPUT_CHARS) return text;
  return (
    text.slice(0, MAX_OUTPUT_CHARS) +
    `\n\n[...output truncated, ${text.length - MAX_OUTPUT_CHARS} more chars]`
  );
}

// Run the CLI, capturing combined stdout+stderr (that IS the CLI's log stream).
function runCli(cliArgs, timeoutSec) {
  return new Promise((resolvePromise) => {
    let cmd, args;
    try {
      [cmd, args] = cliCommand(cliArgs);
    } catch (e) {
      resolvePromise({ ok: false, error: e.message });
      return;
    }
    const started = Date.now();
    let child;
    try {
      child = spawn(cmd, args, { cwd: repoRoot, windowsHide: true });
    } catch (e) {
      resolvePromise({ ok: false, error: `Failed to launch CLI: ${e.message}` });
      return;
    }

    let out = "";
    const append = (buf) => {
      out += buf.toString();
      if (out.length > MAX_OUTPUT_CHARS * 4) out = out.slice(-MAX_OUTPUT_CHARS * 4);
    };
    child.stdout.on("data", append);
    child.stderr.on("data", append);

    const timer = setTimeout(() => {
      timedOut = true;
      child.kill("SIGKILL");
    }, timeoutSec * 1000);
    let timedOut = false;

    child.on("error", (e) => {
      clearTimeout(timer);
      resolvePromise({ ok: false, error: `CLI process error: ${e.message}` });
    });
    child.on("close", (code) => {
      clearTimeout(timer);
      resolvePromise({
        ok: true,
        exitCode: code,
        timedOut,
        elapsedMs: Date.now() - started,
        output: clip(out.replace(ANSI, "")),
        invocation: [cmd, ...args].join(" "),
      });
    });
  });
}

// ---------------------------------------------------------------------------
// Shared argument builders
// ---------------------------------------------------------------------------

function pushFilters(args, a) {
  if (a.filter_by_name) args.push("--filter-by-name", a.filter_by_name);
  if (a.filter_by_container) args.push("--filter-by-container", a.filter_by_container);
  if (a.filter_by_pathid) args.push("--filter-by-pathid", a.filter_by_pathid);
  if (a.filter_by_text) args.push("--filter-by-text", a.filter_by_text);
  if (a.filter_with_regex) args.push("--filter-with-regex");
}

function pushCommon(args, a) {
  if (a.asset_types) args.push("-t", a.asset_types);
  if (a.unity_version) args.push("--unity-version", a.unity_version);
  if (a.assembly_folder) args.push("--assembly-folder", a.assembly_folder);
  if (a.log_level) args.push("--log-level", a.log_level);
  if (a.load_all) args.push("--load-all");
  pushFilters(args, a);
}

function resultText(r) {
  if (!r.ok) return { content: [{ type: "text", text: `ERROR: ${r.error}` }], isError: true };
  const header =
    `$ ${r.invocation}\n` +
    `exit=${r.exitCode}${r.timedOut ? " (TIMED OUT, killed)" : ""} ` +
    `elapsed=${(r.elapsedMs / 1000).toFixed(1)}s\n` +
    "----- output -----\n";
  return {
    content: [{ type: "text", text: header + (r.output || "(no output)") }],
    isError: r.timedOut || r.exitCode !== 0,
  };
}

// ---------------------------------------------------------------------------
// Tool definitions
// ---------------------------------------------------------------------------

const filterProps = {
  filter_by_name: { type: "string", description: "Filter by asset name (comma/semicolon separated for multiple)." },
  filter_by_container: { type: "string", description: "Filter by container path." },
  filter_by_pathid: { type: "string", description: "Filter by PathID (comma separated)." },
  filter_by_text: { type: "string", description: "Filter by text in name or container." },
  filter_with_regex: { type: "boolean", description: "Treat filter text as a regular expression." },
};

const tools = [
  {
    name: "asset_help",
    description: "Show the full AssetStudioModCLI help / option reference.",
    inputSchema: { type: "object", properties: {} },
    handler: async () => resultText(await runCli(["--help"], 30)),
  },
  {
    name: "asset_info",
    description:
      "Load asset file(s)/folder and list how many exportable assets of each type are present " +
      "(CLI '-m info'). Use this to inspect a bundle before exporting.",
    inputSchema: {
      type: "object",
      properties: {
        input_path: { type: "string", description: "Path to an asset file or folder." },
        asset_types: { type: "string", description: "Asset type filter, e.g. 'tex2d,sprite,audio' or 'all'." },
        load_all: { type: "boolean", description: "Load assets of all types (like 'Display all assets')." },
        unity_version: { type: "string", description: "Override Unity version, e.g. '2017.4.39f1'." },
        assembly_folder: { type: "string", description: "Path to the assembly (Managed) folder." },
        log_level: { type: "string", enum: ["verbose", "debug", "info", "warning", "error"] },
        ...filterProps,
        timeout_sec: { type: "number", description: `Timeout in seconds (default ${DEFAULT_TIMEOUT}).` },
      },
      required: ["input_path"],
    },
    handler: async (a) => {
      const args = [a.input_path, "-m", "info"];
      pushCommon(args, a);
      return resultText(await runCli(args, a.timeout_sec || DEFAULT_TIMEOUT));
    },
  },
  {
    name: "asset_export",
    description:
      "Convert and export assets with AssetStudioModCLI. Covers export/exportRaw/dump/live2d/" +
      "splitObjects/animator modes. Returns the CLI log; use list_output afterwards to see files.",
    inputSchema: {
      type: "object",
      properties: {
        input_path: { type: "string", description: "Path to an asset file or folder." },
        output_path: { type: "string", description: `Output folder. Default: ${defaultOutDir()}` },
        mode: {
          type: "string",
          enum: ["export", "exportRaw", "dump", "extract", "live2d", "splitObjects", "animator"],
          description: "Working mode (default 'export').",
        },
        asset_types: { type: "string", description: "Asset type(s), e.g. 'mesh' or 'tex2d,sprite'." },
        group_option: {
          type: "string",
          enum: ["none", "type", "container", "containerFull", "fileName", "sceneHierarchy"],
        },
        filename_format: { type: "string", enum: ["assetName", "assetName_pathID", "pathID"] },
        image_format: { type: "string", enum: ["none", "jpg", "png", "bmp", "tga", "webp"] },
        audio_format: { type: "string", enum: ["none", "wav"] },
        overwrite: { type: "boolean", description: "Overwrite existing files." },
        unity_version: { type: "string" },
        assembly_folder: { type: "string" },
        max_export_tasks: { type: "integer", description: "Number of parallel export tasks." },
        log_level: { type: "string", enum: ["verbose", "debug", "info", "warning", "error"] },
        load_all: { type: "boolean" },
        extra_args: {
          type: "array",
          items: { type: "string" },
          description: "Extra raw CLI args appended verbatim (escape hatch for uncovered options).",
        },
        ...filterProps,
        timeout_sec: { type: "number", description: `Timeout in seconds (default ${DEFAULT_TIMEOUT}).` },
      },
      required: ["input_path"],
    },
    handler: async (a) => {
      const out = a.output_path || defaultOutDir();
      const args = [a.input_path, "-o", out];
      if (a.mode) args.push("-m", a.mode);
      if (a.group_option) args.push("-g", a.group_option);
      if (a.filename_format) args.push("-f", a.filename_format);
      if (a.image_format) args.push("--image-format", a.image_format);
      if (a.audio_format) args.push("--audio-format", a.audio_format);
      if (a.overwrite) args.push("-r");
      if (a.max_export_tasks) args.push("--max-export-tasks", String(a.max_export_tasks));
      pushCommon(args, a);
      if (Array.isArray(a.extra_args)) args.push(...a.extra_args);
      const r = await runCli(args, a.timeout_sec || DEFAULT_TIMEOUT);
      if (r.ok) r.output += `\n\n[output folder: ${out}]`;
      return resultText(r);
    },
  },
  {
    name: "asset_run",
    description:
      "Run AssetStudioModCLI with a verbatim argument list. Escape hatch for anything the typed " +
      "tools don't cover. Args are passed directly to the CLI (no shell).",
    inputSchema: {
      type: "object",
      properties: {
        args: { type: "array", items: { type: "string" }, description: "CLI arguments, in order." },
        timeout_sec: { type: "number", description: `Timeout in seconds (default ${DEFAULT_TIMEOUT}).` },
      },
      required: ["args"],
    },
    handler: async (a) => resultText(await runCli(a.args, a.timeout_sec || DEFAULT_TIMEOUT)),
  },
  {
    name: "list_output",
    description:
      "List files under an export/output folder (recursive) with sizes, so you can verify what an " +
      "export produced. Defaults to the MCP default output folder.",
    inputSchema: {
      type: "object",
      properties: {
        path: { type: "string", description: `Folder to list. Default: ${defaultOutDir()}` },
        max_entries: { type: "integer", description: "Max files to list (default 200)." },
      },
    },
    handler: async (a) => {
      const root = a.path || defaultOutDir();
      const limit = a.max_entries || 200;
      if (!existsSync(root)) {
        return { content: [{ type: "text", text: `Folder does not exist: ${root}` }] };
      }
      const rows = [];
      let total = 0;
      let truncated = false;
      const walk = (dir) => {
        if (rows.length >= limit) {
          truncated = true;
          return;
        }
        let entries;
        try {
          entries = readdirSync(dir, { withFileTypes: true });
        } catch {
          return;
        }
        for (const e of entries) {
          const full = join(dir, e.name);
          if (e.isDirectory()) {
            walk(full);
          } else {
            total++;
            if (rows.length < limit) {
              let size = 0;
              try {
                size = statSync(full).size;
              } catch {}
              rows.push(`${String(size).padStart(10)}  ${relative(root, full).split(sep).join("/")}`);
            } else {
              truncated = true;
            }
          }
        }
      };
      walk(root);
      const head = `${root}\n${total} file(s)${truncated ? ` (showing first ${limit})` : ""}\n`;
      return { content: [{ type: "text", text: head + (rows.join("\n") || "(empty)") }] };
    },
  },
];

const toolMap = new Map(tools.map((t) => [t.name, t]));

// ---------------------------------------------------------------------------
// JSON-RPC / MCP stdio plumbing
// ---------------------------------------------------------------------------

function send(msg) {
  process.stdout.write(JSON.stringify(msg) + "\n");
}

function reply(id, result) {
  send({ jsonrpc: "2.0", id, result });
}

function replyError(id, code, message) {
  send({ jsonrpc: "2.0", id, error: { code, message } });
}

async function handle(msg) {
  const { id, method, params } = msg;
  switch (method) {
    case "initialize":
      reply(id, {
        protocolVersion: params?.protocolVersion || "2025-06-18",
        capabilities: { tools: {} },
        serverInfo: { name: "assetstudio-cli", version: "0.1.0" },
      });
      return;
    case "notifications/initialized":
      return; // notification, no response
    case "ping":
      reply(id, {});
      return;
    case "tools/list":
      reply(id, {
        tools: tools.map((t) => ({
          name: t.name,
          description: t.description,
          inputSchema: t.inputSchema,
        })),
      });
      return;
    case "tools/call": {
      const tool = toolMap.get(params?.name);
      if (!tool) {
        replyError(id, -32602, `Unknown tool: ${params?.name}`);
        return;
      }
      try {
        const result = await tool.handler(params.arguments || {});
        reply(id, result);
      } catch (e) {
        reply(id, { content: [{ type: "text", text: `ERROR: ${e.message}` }], isError: true });
      }
      return;
    }
    default:
      if (id !== undefined) replyError(id, -32601, `Method not found: ${method}`);
  }
}

let buffer = "";
process.stdin.on("data", (chunk) => {
  buffer += chunk.toString();
  let nl;
  while ((nl = buffer.indexOf("\n")) >= 0) {
    const line = buffer.slice(0, nl).trim();
    buffer = buffer.slice(nl + 1);
    if (!line) continue;
    let msg;
    try {
      msg = JSON.parse(line);
    } catch {
      continue;
    }
    handle(msg).catch((e) => process.stderr.write(`handler error: ${e.stack}\n`));
  }
});
process.stdin.on("end", () => process.exit(0));

process.stderr.write(
  `assetstudio-cli MCP server ready. CLI=${resolveCli() || "NOT FOUND"} out=${defaultOutDir()}\n`
);
