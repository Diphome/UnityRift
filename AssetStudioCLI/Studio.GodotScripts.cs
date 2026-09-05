using AssetStudio;
using AssetStudioCLI.Options;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using static AssetStudioCLI.Exporter;
using Ansi = AssetStudio.ColorConsole;

namespace AssetStudioCLI
{
    /// <summary>
    /// "-m godotscripts": for each unique MonoBehaviour script class, generate a Godot 4 GDScript stub
    /// (class name + serialized fields as @export vars). Works for both Mono (managed assemblies) and
    /// IL2CPP (Cpp2IL dummy assemblies) games - the field type tree is resolved from whichever assemblies
    /// are available. The Unity logic is not translated; the stub is scaffolding to port by hand.
    /// </summary>
    internal static partial class Studio
    {
        public static void ExportGodotScripts()
        {
            // Ensure assemblies are loaded so stripped MonoBehaviour type trees can be reconstructed.
            // (--il2cpp is already handled by LoadIl2CppAssembliesIfRequested; here we cover Mono and the
            // no-flag IL2CPP fallback.)
            if (assemblyLoader.Modules.Count == 0)
            {
                try
                {
                    var folder = ResolveDotNetAssemblyFolder();
                    if (folder != null)
                    {
                        assemblyLoader.Load(folder);
                        Logger.Info($"Loaded {assemblyLoader.Modules.Count} assemblies from \"{folder.Color(Ansi.BrightCyan)}\" for field resolution.");
                    }
                    else
                    {
                        Logger.Warning("No managed/IL2CPP assemblies found. Only MonoBehaviours with an embedded type tree will expose their fields.");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Assembly resolution failed: {ex.Message}");
                }
            }

            var outRoot = CLIOptions.o_outputFolder.Value;
            var scriptsDir = Path.Combine(outRoot, "scripts");
            var overwrite = CLIOptions.f_overwriteExisting.Value;

            // One stub per unique script class (many instances share a class).
            var seenClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var exported = 0;
            var total = 0;

            foreach (var item in parsedAssetsList)
            {
                if (!(item.Asset is MonoBehaviour mb))
                    continue;
                total++;

                string className = null, ns = null, asm = null;
                if (mb.m_Script.TryGet(out var ms, mb.assetsFile))
                {
                    className = ms.m_ClassName;
                    ns = ms.m_Namespace;
                    asm = ms.m_AssemblyName;
                }
                if (string.IsNullOrEmpty(className))
                    className = string.IsNullOrEmpty(mb.m_Name) ? "UnityScript" : mb.m_Name;

                var classKey = (string.IsNullOrEmpty(ns) ? "" : ns + ".") + className;
                if (!seenClasses.Add(classKey))
                    continue; // already produced a stub for this class

                OrderedDictionary fields = null;
                try
                {
                    fields = mb.ToType();
                    if (fields == null && assemblyLoader.Modules.Count > 0)
                    {
                        var tt = mb.ConvertToTypeTree(assemblyLoader);
                        fields = mb.ToType(tt);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Field read failed for {classKey}: {ex.Message}");
                }

                try
                {
                    var result = GodotScriptStubExporter.Export(className, ns, asm, fields);
                    var baseName = UniqueScriptName(result.ClassName, fileNames);
                    var path = Path.Combine(scriptsDir, baseName + ".gd");
                    if (!overwrite && File.Exists(path))
                        continue;
                    Directory.CreateDirectory(scriptsDir);
                    File.WriteAllText(path, result.Gd);
                    exported++;
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to write stub for {classKey}: {ex.Message}");
                }
            }

            if (exported == 0)
            {
                Logger.Warning($"No MonoBehaviour script stubs written ({total} MonoBehaviour asset(s) scanned).");
                return;
            }

            Logger.Info($"Wrote {exported.ToString().Color(Ansi.BrightGreen)} Godot script stub(s) from {total} MonoBehaviour(s) to \"{scriptsDir.Color(Ansi.BrightCyan)}\".");
            Logger.Info("Each .gd declares the serialized fields as @export vars; port the Unity logic into _ready()/_process() by hand.");
        }

        private static string UniqueScriptName(string baseName, HashSet<string> used)
        {
            var name = string.IsNullOrEmpty(baseName) ? "UnityScript" : baseName;
            var candidate = name;
            var n = 1;
            while (!used.Add(candidate))
                candidate = $"{name}_{n++}";
            return candidate;
        }
    }
}
