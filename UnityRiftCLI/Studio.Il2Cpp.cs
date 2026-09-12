using UnityRift;
using UnityRiftCLI.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ansi = UnityRift.ColorConsole;

namespace UnityRiftCLI
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
            Logger.Error("IL2CPP support requires the .NET 8+ build of UnityRiftCLI.");
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

            // Field offsets + enum maps from the dummy DLLs (cached), for --il2cpp-field / --il2cpp-enum.
            try { Il2CppTypesJson.Build(folder, log: msg => Logger.Info(msg)); }
            catch (Exception ex) { Logger.Warning($"Could not build {Il2CppSymbolIndex.TypesFileName}: {ex.Message}"); }

            var dest = Path.Combine(CLIOptions.o_outputFolder.Value, "il2cpp");
            var copied = Il2CppAssemblyProvider.ExportGhidraPackage(folder, dest);
            Logger.Info($"Wrote {copied.Count} Ghidra helper file(s) to \"{dest.Color(Ansi.BrightCyan)}\"");
            Logger.Info("Ghidra: import GameAssembly.dll / libil2cpp.so, Parse C Source il2cpp_ghidra.h, then Script Manager → add the 'ghidra' folder and run ghidra.py (or ghidra_with_struct.py).");

            if (CLIOptions.f_il2cppDummyDll.Value)
            {
                var dllDest = Path.Combine(CLIOptions.o_outputFolder.Value, "DummyDll");
                var dlls = Il2CppAssemblyProvider.ExportDummyDlls(folder, dllDest);
                Logger.Info($"Exported {dlls.Count} dummy assemblies to \"{dllDest.Color(Ansi.BrightCyan)}\" (open in dnSpy / ILSpy / dotPeek).");
            }

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
            var decodes = CLIOptions.o_il2cppDecode.Value;
            var datas = CLIOptions.o_il2cppData.Value;
            var cleans = CLIOptions.o_il2cppClean.Value;
            var suggests = CLIOptions.o_il2cppSuggest.Value;
            var fields = CLIOptions.o_il2cppField.Value;
            var enums = CLIOptions.o_il2cppEnum.Value;
            var fridas = CLIOptions.o_il2cppFrida.Value;
            var applyPlans = CLIOptions.o_il2cppApplyPlan.Value;
            var maps = CLIOptions.o_il2cppMap.Value;
            var wireLayouts = CLIOptions.o_il2cppWireLayout.Value;
            var regex = CLIOptions.f_filterWithRegex.Value;
            var fuzzy = CLIOptions.f_il2cppFuzzy.Value;
            if (lookups.Count == 0 && strings.Count == 0 && decodes.Count == 0 &&
                datas.Count == 0 && cleans.Count == 0 && suggests.Count == 0 &&
                fields.Count == 0 && enums.Count == 0 && fridas.Count == 0 && applyPlans.Count == 0 &&
                maps.Count == 0 && wireLayouts.Count == 0)
                return;

            // The game binary backs DAT_ literal-pool reads (data/clean); opened lazily.
            BinaryImage image = null;
            var imageTried = false;
            BinaryImage GetImage()
            {
                if (!imageTried)
                {
                    imageTried = true;
                    image = BinaryImage.TryOpen(game.BinaryPath);
                    if (image == null)
                        Logger.Warning($"Could not open IL2CPP binary for literal-pool reads: {game.BinaryPath}");
                }
                return image;
            }

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
                        item["result"] = idx.FindByName(q, regex, fuzzy: fuzzy);
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
            if (decodes.Count > 0)
            {
                var arr = new JArray();
                foreach (var q in decodes)
                {
                    var item = new JObject { ["value"] = q };
                    if (Il2CppConstantResolver.TryParseHex(q, out var v))
                        item["decoded"] = Il2CppConstantResolver.DecodeImmediate(v) ?? "(not a plausible float/double/int)";
                    else
                        item["error"] = "not a hex value";
                    arr.Add(item);
                }
                root["decode"] = arr;
            }
            if (datas.Count > 0)
            {
                var arr = new JArray();
                var img = GetImage();
                foreach (var q in datas)
                {
                    var item = new JObject { ["va"] = q };
                    if (!Il2CppConstantResolver.TryParseHex(q, out var va)) item["error"] = "not a hex address";
                    else if (img == null) item["error"] = "binary not available";
                    else item["decoded"] = img.ResolveData(va) ?? "(no plausible constant at this address)";
                    arr.Add(item);
                }
                root["data"] = arr;
            }
            if (cleans.Count > 0)
            {
                var strip = !CLIOptions.f_il2cppCleanRaw.Value;
                var img = GetImage();
                foreach (var pathArg in cleans)
                {
                    var files = new List<string>();
                    if (Directory.Exists(pathArg)) files.AddRange(Directory.GetFiles(pathArg, "*.c"));
                    else if (File.Exists(pathArg)) files.Add(pathArg);
                    else { Logger.Warning($"--il2cpp-clean: path not found: {pathArg}"); continue; }
                    foreach (var f in files)
                    {
                        var cleaned = Il2CppDecompCleaner.Clean(File.ReadAllText(f), strip, floats: true, img: img, index: idx);
                        Logger.Default.Log(LoggerEvent.Info, $"========= {Path.GetFileName(f)}\n{cleaned}", ignoreLevel: true);
                    }
                }
            }
            if (fields.Count > 0)
            {
                var arr = new JArray();
                foreach (var q in fields)
                {
                    var (type, off) = SplitTypeAndValue(q);
                    arr.Add(idx.FieldLookup(type, off));
                }
                root["field"] = arr;
            }
            if (enums.Count > 0)
            {
                var arr = new JArray();
                foreach (var q in enums)
                {
                    var (type, val) = SplitTypeAndValue(q);
                    arr.Add(idx.EnumLookup(type, val));
                }
                root["enum"] = arr;
            }
            if (applyPlans.Count > 0)
            {
                var arr = new JArray();
                foreach (var q in applyPlans)
                    arr.Add(new JObject { ["filter"] = q, ["plan"] = idx.ApplyPlan(q) });
                root["applyPlan"] = arr;
            }
            if (fridas.Count > 0)
            {
                var hooks = new List<Il2CppFridaGenerator.Hook>();
                foreach (var q in fridas)
                {
                    if (idx.TryParseAddress(q, out var rva, out _))
                    {
                        var m = idx.Methods.FirstOrDefault(x => x.Rva == rva);
                        if (m != null) hooks.Add(new Il2CppFridaGenerator.Hook { Name = m.Name, Rva = m.Rva, Signature = m.Signature, Thumb = m.Thumb });
                    }
                    else
                    {
                        foreach (var hit in idx.FindByName(q, regex, max: 25, fuzzy: fuzzy))
                        {
                            if (hit.Value<string>("kind") != "method") continue;
                            var mrva = hit.Value<string>("rva");
                            if (mrva == null) continue;
                            hooks.Add(new Il2CppFridaGenerator.Hook
                            {
                                Name = hit.Value<string>("name"),
                                Rva = Il2CppSymbolIndex.ParseHex(mrva),
                                Signature = hit.Value<string>("signature"),
                                Thumb = hit.Value<bool?>("thumb") ?? false,
                            });
                        }
                    }
                }
                if (hooks.Count > 0)
                {
                    var module = Path.GetFileName(game.BinaryPath);
                    var js = Il2CppFridaGenerator.Generate(module, hooks);
                    var jsPath = Path.Combine(dest, "hooks.js");
                    File.WriteAllText(jsPath, js);
                    Logger.Info($"Wrote {hooks.Count} Frida hook(s) to \"{jsPath.Color(Ansi.BrightCyan)}\"");
                    Logger.Default.Log(LoggerEvent.Info, js, ignoreLevel: true);
                }
                else
                {
                    Logger.Warning("--il2cpp-frida: no methods matched.");
                }
            }
            if (maps.Count > 0)
            {
                var arr = new JArray();
                foreach (var q in maps)
                    arr.Add(idx.Map(q));
                root["map"] = arr;
            }
            if (wireLayouts.Count > 0)
            {
                var img = GetImage();
                foreach (var pathArg in wireLayouts)
                {
                    var files = new List<string>();
                    if (Directory.Exists(pathArg)) files.AddRange(Directory.GetFiles(pathArg, "*.c"));
                    else if (File.Exists(pathArg)) files.Add(pathArg);
                    else { Logger.Warning($"--il2cpp-wire-layout: path not found: {pathArg}"); continue; }
                    var arr = new JArray();
                    foreach (var f in files)
                    {
                        // symbolize + annotate first so serializer calls carry managed names
                        var cleaned = Il2CppDecompCleaner.Clean(File.ReadAllText(f), strip: true, floats: true, img: img, index: idx);
                        arr.Add(Il2CppWireLayout.Analyze(cleaned, Path.GetFileName(f)));
                    }
                    root["wireLayout"] = arr;
                }
            }
            if (suggests.Count > 0)
            {
                // A single arg that is a file path -> extract keywords from its text; otherwise treat as keywords.
                var keywords = new List<string>();
                foreach (var s in suggests)
                {
                    if (File.Exists(s)) keywords.AddRange(Il2CppSymbolIndex.KeywordsFromText(File.ReadAllText(s)));
                    else keywords.Add(s);
                }
                root["suggest"] = idx.Suggest(keywords, methods: true, fuzzy: fuzzy);
            }
            if (root.HasValues)
                Logger.Default.Log(LoggerEvent.Info, root.ToString(Formatting.Indented), ignoreLevel: true);
#endif
        }

#if !NETFRAMEWORK
        /// <summary>Splits a "Type@value" query into the type name and an optional numeric value (hex if 0x-prefixed, else decimal).</summary>
        private static (string type, long? value) SplitTypeAndValue(string q)
        {
            var at = q.LastIndexOf('@');
            if (at < 0) return (q.Trim(), null);
            var type = q.Substring(0, at).Trim();
            var v = q.Substring(at + 1).Trim();
            if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return (type, Il2CppConstantResolver.TryParseHex(v, out var hv) ? (long)hv : (long?)null);
            if (long.TryParse(v, out var dv)) return (type, dv);
            return (type, Il2CppConstantResolver.TryParseHex(v, out var hv2) ? (long)hv2 : (long?)null);
        }
#endif
    }
}
