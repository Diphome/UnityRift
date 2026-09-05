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
    /// IL2CPP (Cpp2IL dummy assemblies). The stub-generation helpers are shared with the scene exporter,
    /// which attaches the produced scripts to per-GameObject nodes.
    /// </summary>
    internal static partial class Studio
    {
        public static void ExportGodotScripts()
        {
            var outRoot = CLIOptions.o_outputFolder.Value;
            var scriptsDir = Path.Combine(outRoot, "scripts");
            var overwrite = CLIOptions.f_overwriteExisting.Value;

            EnsureScriptAssembliesLoaded();

            var mbs = new List<MonoBehaviour>();
            foreach (var item in parsedAssetsList)
                if (item.Asset is MonoBehaviour mb)
                    mbs.Add(mb);

            var map = BuildScriptStubs(mbs, scriptsDir, overwrite);
            if (map.Count == 0)
            {
                Logger.Warning($"No MonoBehaviour script stubs written ({mbs.Count} MonoBehaviour asset(s) scanned).");
                return;
            }
            Logger.Info($"Wrote {map.Count.ToString().Color(Ansi.BrightGreen)} Godot script stub(s) from {mbs.Count} MonoBehaviour(s) to \"{scriptsDir.Color(Ansi.BrightCyan)}\".");
            Logger.Info("Each .gd declares the serialized fields as @export vars; port the Unity logic into _ready()/_process() by hand.");
        }

        /// <summary>Loads managed (Mono) or Cpp2IL dummy (IL2CPP) assemblies for field resolution, once.</summary>
        internal static void EnsureScriptAssembliesLoaded()
        {
            if (assemblyLoader.Modules.Count > 0)
                return;
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

        /// <summary>
        /// Writes one GDScript stub per unique MonoBehaviour class into <paramref name="scriptsDir"/> and
        /// returns a map from class key (namespace.class, or bare class) to the stub's file base name
        /// (matching the on-disk <name>.gd and the GDScript class_name).
        /// </summary>
        internal static Dictionary<string, string> BuildScriptStubs(IEnumerable<MonoBehaviour> mbs, string scriptsDir, bool overwrite)
        {
            var classKeyToFile = new Dictionary<string, string>(StringComparer.Ordinal);
            var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var mb in mbs)
            {
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
                if (classKeyToFile.ContainsKey(classKey))
                    continue;

                OrderedDictionary fields = null;
                try
                {
                    fields = mb.ToType();
                    if (fields == null && assemblyLoader.Modules.Count > 0)
                        fields = mb.ToType(mb.ConvertToTypeTree(assemblyLoader));
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
                    if (overwrite || !File.Exists(path))
                    {
                        Directory.CreateDirectory(scriptsDir);
                        File.WriteAllText(path, result.Gd);
                    }
                    classKeyToFile[classKey] = baseName;
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to write stub for {classKey}: {ex.Message}");
                }
            }
            return classKeyToFile;
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
