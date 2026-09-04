using AssetStudio;
using AssetStudioCLI.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using Ansi = AssetStudio.ColorConsole;

namespace AssetStudioCLI
{
    /// <summary>
    /// "-m il2cpp": generate the Il2CppDumper-compatible Ghidra package (script.json, il2cpp.h,
    /// bundled ghidra.py / ghidra_with_struct.py) from GameAssembly.dll / libil2cpp.so +
    /// global-metadata.dat, copy it to the output folder, and optionally look up names/addresses.
    /// </summary>
    internal static partial class Studio
    {
        public static void ExportIl2CppGhidraPackage()
        {
#if NETFRAMEWORK
            Logger.Error("IL2CPP support requires the .NET 8+ build of AssetStudioModCLI.");
#else
            var game = Il2CppAssemblyProvider.Find(CLIOptions.inputPathList);
            if (game == null)
            {
                foreach (var input in CLIOptions.inputPathList)
                {
                    if (File.Exists(input))
                    {
                        game = Il2CppAssemblyProvider.FromBinary(input);
                        if (game != null) break;
                    }
                }
            }
            if (game == null)
            {
                Logger.Error("No IL2CPP binary/metadata found. Pass the game folder, GameAssembly.dll / libil2cpp.so, or a file inside the game folder.");
                return;
            }

            Logger.Info($"IL2CPP binary: {game.BinaryPath.Color(Ansi.BrightCyan)}");
            Logger.Info($"Metadata:      {game.MetadataPath.Color(Ansi.BrightCyan)}");

            string folder;
            try
            {
                folder = Il2CppAssemblyProvider.GetOrGenerateAssemblies(game, CLIOptions.o_unityVersion.Value?.FullVersion, msg => Logger.Info(msg));
            }
            catch (Exception ex)
            {
                Logger.Error($"IL2CPP processing failed: {ex.Message}");
                return;
            }

            if (!Il2CppSymbolIndex.Exists(folder))
            {
                Logger.Error($"Ghidra package was not generated (no script.json in {folder}). Dummy assemblies may still be usable with -m dotnet.");
                return;
            }

            var dest = Path.Combine(CLIOptions.o_outputFolder.Value, "il2cpp");
            var copied = Il2CppAssemblyProvider.ExportGhidraPackage(folder, dest);
            Logger.Info($"Wrote {copied.Count} Ghidra helper file(s) to \"{dest.Color(Ansi.BrightCyan)}\"");
            Logger.Info("Ghidra: import GameAssembly.dll / libil2cpp.so, Parse C Source il2cpp_ghidra.h, then Script Manager → add the 'ghidra' folder and run ghidra.py (or ghidra_with_struct.py).");

            Il2CppSymbolIndex idx;
            try
            {
                idx = Il2CppSymbolIndex.Load(folder);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to load script.json: {ex.Message}");
                return;
            }

            var summary = idx.Summary();
            Logger.Default.Log(LoggerEvent.Info, summary.ToString(Formatting.Indented), ignoreLevel: true);

            var lookups = CLIOptions.o_il2cppLookup.Value;
            var strings = CLIOptions.o_il2cppStrings.Value;
            var regex = CLIOptions.f_filterWithRegex.Value;
            if (lookups.Count == 0 && strings.Count == 0)
                return;

            var root = new JObject();
            if (lookups.Count > 0)
            {
                var arr = new JArray();
                foreach (var q in lookups)
                {
                    var item = new JObject { ["query"] = q };
                    if (idx.TryParseAddress(q, out var rva, out var how))
                    {
                        item["how"] = how;
                        item["result"] = idx.DescribeAddress(rva);
                    }
                    else
                    {
                        item["result"] = idx.FindByName(q, regex);
                    }
                    arr.Add(item);
                }
                root["lookup"] = arr;
            }
            if (strings.Count > 0)
            {
                var arr = new JArray();
                foreach (var q in strings)
                    arr.Add(new JObject { ["query"] = q, ["result"] = idx.FindStrings(q, regex) });
                root["strings"] = arr;
            }
            Logger.Default.Log(LoggerEvent.Info, root.ToString(Formatting.Indented), ignoreLevel: true);
#endif
        }
    }
}
