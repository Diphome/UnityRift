using UnityRift;
using UnityRiftCLI.Options;
using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Ansi = UnityRift.ColorConsole;

namespace UnityRiftCLI
{
    /// <summary>
    /// "-m dotnet": browse the game's managed assemblies. Lists assemblies/types, or dumps
    /// C#-like stubs (optionally with IL) for types matching --dotnet-type.
    /// </summary>
    internal static partial class Studio
    {
        public static void ShowDotNetClasses()
        {
            var folder = ResolveDotNetAssemblyFolder();
            if (folder == null)
            {
                Logger.Error("No .NET assemblies found. Pass the game's Managed folder (or a file inside the game folder) as input, or use --assembly-folder.");
                return;
            }
            if (!assemblyLoader.Loaded || assemblyLoader.LoadedPath == null)
            {
                var il2cpp = folder == il2cppFolder;
                assemblyLoader.Clear();
                assemblyLoader.Load(folder);
                assemblyLoader.IsIl2CppStubs = il2cpp;
            }
            Logger.Info($"Loaded {assemblyLoader.Modules.Count} assemblies from \"{assemblyLoader.LoadedPath.Color(Ansi.BrightCyan)}\""
                + (assemblyLoader.IsIl2CppStubs ? " (IL2CPP stubs: metadata only, no method bodies)" : ""));
            if (assemblyLoader.IsIl2CppStubs && CLIOptions.f_dotnetIL.Value)
                Logger.Warning("--dotnet-il has no effect on IL2CPP stubs (no method bodies are available).");

            var modules = assemblyLoader.Modules
                .Where(p => MatchesAssemblyFilter(p.Key))
                .OrderBy(p => AssemblyRank(p.Key))
                .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (modules.Count == 0)
            {
                Logger.Warning("No assembly matches the --dotnet-assembly filter.");
                return;
            }

            var typeFilters = CLIOptions.o_dotnetTypes.Value;
            if (typeFilters.Count == 0)
            {
                ListDotNetTypes(modules);
                if (CLIOptions.f_dotnetToFiles.Value)
                    ExportDotNetStubs(modules.Select(p => p.Value), null);
            }
            else
            {
                DumpDotNetTypes(modules, typeFilters);
            }

            if (CLIOptions.f_dotnetExportDll.Value)
            {
                var dir = Path.Combine(CLIOptions.o_outputFolder.Value, "DotNet", "Assemblies");
                Logger.Info($"Copying {modules.Count} assembly file(s)...");
                Progress.Reset();
                var result = DotNetExporter.ExportAssemblyFiles(modules.Select(p => p.Value), dir, (cur, total) => Progress.Report(cur, total), Logger.Warning);
                Logger.Info($"Copied {result.Files} assembly file(s) to \"{dir.Color(Ansi.BrightCyan)}\"" + (result.Failed > 0 ? $" ({result.Failed} failed)" : ""));
            }
        }

        private static string DotNetStubsFolder => Path.Combine(CLIOptions.o_outputFolder.Value, "DotNet");

        /// <summary>Writes .cs stubs for all types of <paramref name="modules"/> (or only those in <paramref name="onlyTypes"/>).</summary>
        private static void ExportDotNetStubs(IEnumerable<ModuleDefinition> modules, HashSet<TypeDefinition> onlyTypes)
        {
            var dir = DotNetStubsFolder;
            Logger.Info("Writing .NET type stubs...");
            Progress.Reset();
            var result = DotNetExporter.ExportStubs(modules, dir, CLIOptions.f_dotnetIL.Value,
                onlyTypes == null ? (Func<TypeDefinition, bool>)null : onlyTypes.Contains,
                (cur, total) => Progress.Report(cur, total), Logger.Warning);
            Logger.Info($"Wrote {result.Types} .cs stub file(s) to \"{dir.Color(Ansi.BrightCyan)}\"" + (result.Failed > 0 ? $" ({result.Failed} failed)" : ""));
        }

        private static string il2cppFolder;
        /// <summary>Copy of the CLI input paths taken before loading (AssetsManager clears the list it is given).</summary>
        internal static List<string> inputPathsSnapshot;
        private static List<string> InputPaths => inputPathsSnapshot ?? CLIOptions.inputPathList;

        private static string ResolveDotNetAssemblyFolder()
        {
            if (!string.IsNullOrEmpty(CLIOptions.o_assemblyPath.Value))
                return CLIOptions.o_assemblyPath.Value;
            // Use the snapshot: AssetsManager clears CLIOptions.inputPathList during loading, so asset
            // modes (e.g. -m godotscripts) would otherwise search an empty list.
            var managed = AssemblyLoader.FindManagedFolder(InputPaths);
            if (managed != null)
                return managed;
            var il2cpp = ResolveIl2CppAssemblyFolder(CLIOptions.o_unityVersion.Value?.FullVersion);
            if (il2cpp != null)
                return il2cpp;
            // Fallback: an input folder that directly contains DLLs
            foreach (var input in CLIOptions.inputPathList)
            {
                try
                {
                    if (Directory.Exists(input) && Directory.EnumerateFiles(input, "*.dll").Any())
                        return input;
                }
                catch
                {
                    // ignored
                }
            }
            return null;
        }

        /// <summary>
        /// Finds the IL2CPP binary + metadata near the inputs and returns a folder of Cpp2IL-generated
        /// dummy assemblies (cached). Returns null when the game is not IL2CPP or processing fails.
        /// </summary>
        private static string ResolveIl2CppAssemblyFolder(string unityVersion)
        {
            var game = Il2CppAssemblyProvider.Find(InputPaths);
            if (game == null)
                return null;
            if (!Il2CppAssemblyProvider.IsSupported)
            {
                Logger.Warning($"IL2CPP game detected ({game.BinaryPath}) but IL2CPP support requires the .NET 8+ build of the CLI.");
                return null;
            }
            try
            {
                il2cppFolder = Il2CppAssemblyProvider.GetOrGenerateAssemblies(game, unityVersion, msg => Logger.Info(msg));
                return il2cppFolder;
            }
            catch (Exception ex)
            {
                Logger.Error($"IL2CPP processing failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>--il2cpp for asset modes: after assets are loaded, generate + load IL2CPP assemblies unless --assembly-folder was given.</summary>
        public static void LoadIl2CppAssembliesIfRequested()
        {
            if (!CLIOptions.f_il2cpp.Value || assemblyLoader.Modules.Count > 0)
                return;
            var version = assetsManager.AssetsFileList.Count > 0 ? assetsManager.AssetsFileList[0].version?.FullVersion : CLIOptions.o_unityVersion.Value?.FullVersion;
            var folder = ResolveIl2CppAssemblyFolder(version);
            if (folder == null)
            {
                Logger.Warning("--il2cpp: no IL2CPP binary/metadata found near the input path(s).");
                return;
            }
            assemblyLoader.Clear();
            assemblyLoader.Load(folder);
            assemblyLoader.IsIl2CppStubs = true;
            Logger.Info($"Loaded {assemblyLoader.Modules.Count} IL2CPP dummy assemblies from \"{folder.Color(Ansi.BrightCyan)}\"");
        }

        private static bool MatchesAssemblyFilter(string moduleName)
        {
            var filters = CLIOptions.o_dotnetAssemblies.Value;
            if (filters.Count == 0)
                return true;
            foreach (var f in filters)
            {
                var name = f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? f : f + ".dll";
                if (string.Equals(name, moduleName, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (moduleName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static int AssemblyRank(string name)
        {
            if (name.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase)) return 0;
            if (name.StartsWith("Unity", StringComparison.OrdinalIgnoreCase)) return 2;
            if (name.StartsWith("System", StringComparison.OrdinalIgnoreCase) || name.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase) || name.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Mono.", StringComparison.OrdinalIgnoreCase)) return 3;
            return 1;
        }

        private static string TypeKind(TypeDefinition t)
        {
            return t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" : DotNetTypeDumper.IsDelegate(t) ? "delegate" : "class";
        }

        private static void ListDotNetTypes(List<KeyValuePair<string, ModuleDefinition>> modules)
        {
            var sb = new StringBuilder();
            sb.AppendLine("======");
            sb.AppendLine("[Assemblies]");
            foreach (var pair in modules)
                sb.AppendLine($"# {pair.Key}: {pair.Value.Types.Count(t => t.Name != "<Module>")} types");

            // Without an explicit assembly filter, list types only for game code (not Unity/System)
            // to keep the output readable; the summary above shows what else is there.
            var listAll = CLIOptions.o_dotnetAssemblies.Value.Count > 0;
            var listed = modules.Where(p => listAll || AssemblyRank(p.Key) <= 1).ToList();
            foreach (var pair in listed)
            {
                sb.AppendLine();
                sb.AppendLine($"[{pair.Key}]");
                foreach (var t in pair.Value.Types.Where(t => t.Name != "<Module>").OrderBy(t => t.Namespace, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal))
                    AppendTypeLine(sb, t, "");
            }
            if (!listAll && listed.Count < modules.Count)
            {
                sb.AppendLine();
                sb.AppendLine($"# {modules.Count - listed.Count} Unity/System assemblies not listed; use --dotnet-assembly <name> to list them.");
            }
            sb.AppendLine("======");
            Logger.Default.Log(LoggerEvent.Info, sb.ToString(), ignoreLevel: true);
        }

        private static void AppendTypeLine(StringBuilder sb, TypeDefinition t, string indent)
        {
            sb.AppendLine($"{indent}{TypeKind(t),-9} {t.FullName}");
            foreach (var n in t.NestedTypes)
                AppendTypeLine(sb, n, indent + "  ");
        }

        private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
        {
            foreach (var t in module.Types)
            {
                if (t.Name == "<Module>")
                    continue;
                yield return t;
                foreach (var n in Nested(t))
                    yield return n;
            }
        }

        private static IEnumerable<TypeDefinition> Nested(TypeDefinition t)
        {
            foreach (var n in t.NestedTypes)
            {
                yield return n;
                foreach (var nn in Nested(n))
                    yield return nn;
            }
        }

        private static void DumpDotNetTypes(List<KeyValuePair<string, ModuleDefinition>> modules, List<string> filters)
        {
            var useRegex = CLIOptions.f_filterWithRegex.Value;
            var regexes = useRegex ? filters.Select(f => new Regex(f, RegexOptions.IgnoreCase)).ToList() : null;
            bool Matches(TypeDefinition t)
            {
                var full = t.FullName.Replace('/', '.');
                if (useRegex)
                    return regexes.Any(r => r.IsMatch(full));
                return filters.Any(f => string.Equals(full, f, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Name, f, StringComparison.OrdinalIgnoreCase))
                    || filters.Any(f => full.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            var matches = new List<TypeDefinition>();
            foreach (var pair in modules)
                matches.AddRange(AllTypes(pair.Value).Where(Matches));

            // Prefer exact matches when a filter names a type precisely.
            if (!useRegex)
            {
                var exact = matches.Where(t => filters.Any(f =>
                    string.Equals(t.FullName.Replace('/', '.'), f, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.Name, f, StringComparison.OrdinalIgnoreCase))).ToList();
                if (exact.Count > 0)
                    matches = exact;
            }

            if (matches.Count == 0)
            {
                Logger.Warning($"No .NET type matches \"{string.Join("\", \"", filters)}\".");
                return;
            }

            const int maxDump = 50;
            var withIL = CLIOptions.f_dotnetIL.Value;
            var toFiles = CLIOptions.f_dotnetToFiles.Value;
            var sb = new StringBuilder();
            sb.AppendLine($"====== {matches.Count} matching type(s)");
            var i = 0;
            foreach (var t in matches)
            {
                if (i++ >= maxDump)
                {
                    sb.AppendLine($"# ... {matches.Count - maxDump} more matching types not shown; narrow the --dotnet-type filter.");
                    break;
                }
                string text;
                try
                {
                    text = DotNetTypeDumper.DumpType(t, withIL);
                }
                catch (Exception ex)
                {
                    text = $"// {t.FullName}: failed to dump ({ex.Message})\r\n";
                }
                sb.AppendLine();
                sb.Append(text);
            }
            sb.AppendLine("======");
            Logger.Default.Log(LoggerEvent.Info, sb.ToString(), ignoreLevel: true);
            if (toFiles)
            {
                // Files are written per top-level type (nested matches are inlined in their
                // declaring type's file); all matches are exported, not just the displayed ones.
                var topLevel = new HashSet<TypeDefinition>();
                foreach (var t in matches)
                {
                    var top = t;
                    while (top.DeclaringType != null)
                        top = top.DeclaringType;
                    topLevel.Add(top);
                }
                ExportDotNetStubs(modules.Select(p => p.Value), topLevel);
            }
        }
    }
}
