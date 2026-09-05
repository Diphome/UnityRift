using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UnityRift
{
    /// <summary>An IL2CPP game located on disk: native binary + global-metadata.dat (+ data/player paths when known).</summary>
    public class Il2CppGame
    {
        public string BinaryPath;      // GameAssembly.dll / libil2cpp.so / ...
        public string MetadataPath;    // global-metadata.dat
        public string DataPath;        // <Game>_Data or Data (may be null)
        public string UnityPlayerPath; // UnityPlayer.dll (may be null)

        public override string ToString() => $"{BinaryPath} + {MetadataPath}";
    }

    /// <summary>
    /// IL2CPP support: finds the IL2CPP binary + metadata near loaded assets and turns them into
    /// "dummy" managed assemblies (type/member metadata, no method bodies) with Cpp2IL, cached on
    /// disk so the existing <see cref="AssemblyLoader"/> / Mono.Cecil pipeline can consume them.
    /// Only available on modern .NET (Cpp2IL does not target net472).
    /// </summary>
    public static class Il2CppAssemblyProvider
    {
#if NETFRAMEWORK
        public const bool IsSupported = false;
#else
        public const bool IsSupported = true;
#endif

        private static readonly string[] BinaryNames = { "GameAssembly.dll", "libil2cpp.so", "GameAssembly.so", "GameAssembly.dylib", "libil2cpp.dylib" };
        private static readonly object Il2CppLock = new object();

        /// <summary>Root of the dummy-DLL cache (one sub-folder per binary+metadata pair).</summary>
        public static string CacheRoot { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnityRift", "il2cpp");

        #region Detection

        /// <summary>
        /// Looks for an IL2CPP binary + global-metadata.dat near the given asset paths (walks up a
        /// few ancestor directories). Returns null when none is found.
        /// </summary>
        public static Il2CppGame Find(IEnumerable<string> assetPaths)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in assetPaths)
            {
                if (string.IsNullOrEmpty(p))
                    continue;
                string dir;
                try
                {
                    dir = Directory.Exists(p) ? Path.GetFullPath(p) : Path.GetDirectoryName(Path.GetFullPath(p));
                }
                catch
                {
                    continue;
                }
                var depth = 0;
                while (!string.IsNullOrEmpty(dir) && depth++ < 6)
                {
                    if (!seen.Add(dir))
                        break;
                    var game = Probe(dir);
                    if (game != null)
                        return game;
                    dir = Path.GetDirectoryName(dir);
                }
            }
            return null;
        }

        /// <summary>Builds an <see cref="Il2CppGame"/> from an explicitly chosen binary, locating the metadata next to it.</summary>
        public static Il2CppGame FromBinary(string binaryPath, string metadataPath = null)
        {
            if (!File.Exists(binaryPath))
                return null;
            var dir = Path.GetDirectoryName(binaryPath);
            var game = new Il2CppGame { BinaryPath = binaryPath };
            if (metadataPath != null && File.Exists(metadataPath))
                game.MetadataPath = metadataPath;
            else
            {
                var probe = Probe(dir);
                if (probe != null)
                {
                    game.MetadataPath = probe.MetadataPath;
                    game.DataPath = probe.DataPath;
                    game.UnityPlayerPath = probe.UnityPlayerPath;
                }
                else
                {
                    // Android layout: lib/<abi>/libil2cpp.so + assets/bin/Data/Managed/Metadata/global-metadata.dat
                    var candidates = new List<string>();
                    var cur = dir;
                    for (var i = 0; i < 4 && cur != null; i++)
                    {
                        candidates.Add(Path.Combine(cur, "global-metadata.dat"));
                        candidates.Add(Path.Combine(cur, "assets", "bin", "Data", "Managed", "Metadata", "global-metadata.dat"));
                        cur = Path.GetDirectoryName(cur);
                    }
                    game.MetadataPath = candidates.FirstOrDefault(File.Exists);
                }
            }
            if (game.MetadataPath == null)
                return null;
            FillPlayerAndData(game, dir);
            return game;
        }

        private static Il2CppGame Probe(string dir)
        {
            try
            {
                // Standalone layout: <root>/GameAssembly.dll + <root>/<Game>_Data|Data/il2cpp_data/Metadata/global-metadata.dat
                foreach (var name in BinaryNames)
                {
                    var bin = Path.Combine(dir, name);
                    if (!File.Exists(bin))
                        continue;
                    foreach (var data in DataDirs(dir))
                    {
                        var md = Path.Combine(data, "il2cpp_data", "Metadata", "global-metadata.dat");
                        if (File.Exists(md))
                        {
                            var game = new Il2CppGame { BinaryPath = bin, MetadataPath = md, DataPath = data };
                            FillPlayerAndData(game, dir);
                            return game;
                        }
                    }
                    // loose pair in one folder
                    var loose = Path.Combine(dir, "global-metadata.dat");
                    if (File.Exists(loose))
                        return new Il2CppGame { BinaryPath = bin, MetadataPath = loose };
                }
                // We may be inside <Game>_Data: look at the parent
                var parentBin = BinaryNames.Select(n => Path.Combine(Path.GetDirectoryName(dir) ?? "", n)).FirstOrDefault(File.Exists);
                var mdHere = Path.Combine(dir, "il2cpp_data", "Metadata", "global-metadata.dat");
                if (parentBin != null && File.Exists(mdHere))
                {
                    var game = new Il2CppGame { BinaryPath = parentBin, MetadataPath = mdHere, DataPath = dir };
                    FillPlayerAndData(game, Path.GetDirectoryName(dir));
                    return game;
                }
                // Android extracted APK: lib/<abi>/libil2cpp.so + assets/bin/Data/Managed/Metadata/global-metadata.dat
                var apkMd = Path.Combine(dir, "assets", "bin", "Data", "Managed", "Metadata", "global-metadata.dat");
                var libDir = Path.Combine(dir, "lib");
                if (File.Exists(apkMd) && Directory.Exists(libDir))
                {
                    var so = Directory.EnumerateDirectories(libDir)
                        .OrderBy(d => Path.GetFileName(d).StartsWith("arm64") ? 0 : 1)
                        .Select(d => Path.Combine(d, "libil2cpp.so"))
                        .FirstOrDefault(File.Exists);
                    if (so != null)
                        return new Il2CppGame { BinaryPath = so, MetadataPath = apkMd, DataPath = Path.Combine(dir, "assets", "bin", "Data") };
                }
            }
            catch
            {
                // access denied etc.
            }
            return null;
        }

        private static IEnumerable<string> DataDirs(string root)
        {
            var plain = Path.Combine(root, "Data");
            if (Directory.Exists(plain))
                yield return plain;
            IEnumerable<string> dataDirs;
            try { dataDirs = Directory.EnumerateDirectories(root, "*_Data").ToList(); }
            catch { yield break; }
            foreach (var d in dataDirs)
                yield return d;
        }

        private static void FillPlayerAndData(Il2CppGame game, string root)
        {
            if (game.UnityPlayerPath == null)
            {
                var player = Path.Combine(root, "UnityPlayer.dll");
                if (File.Exists(player))
                    game.UnityPlayerPath = player;
            }
            if (game.DataPath == null)
                game.DataPath = DataDirs(root).FirstOrDefault();
        }

        #endregion

        #region Cache

        /// <summary>Cache folder for this binary+metadata pair (exists and is complete when it contains the marker).</summary>
        public static string GetCacheFolder(Il2CppGame game)
        {
            var fb = new FileInfo(game.BinaryPath);
            var fm = new FileInfo(game.MetadataPath);
            var key = $"{fb.FullName}|{fb.Length}|{fb.LastWriteTimeUtc.Ticks}|{fm.FullName}|{fm.Length}|{fm.LastWriteTimeUtc.Ticks}";
            string hash;
            using (var sha = SHA1.Create())
                hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            var name = Path.GetFileNameWithoutExtension(Path.GetFileName(Path.GetDirectoryName(fb.FullName)) ?? "game");
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return Path.Combine(CacheRoot, $"{name}-{hash}");
        }

        public static bool IsCached(Il2CppGame game)
        {
            var folder = GetCacheFolder(game);
            return File.Exists(Path.Combine(folder, ".complete"))
                && Directory.EnumerateFiles(folder, "*.dll").Any()
                && File.Exists(Path.Combine(folder, "script.json"));
        }

        #endregion

        #region Ghidra package

        /// <summary>Folder of the cached package for the game, or null when not generated yet.</summary>
        public static string GetCachedFolder(Il2CppGame game) => IsCached(game) ? GetCacheFolder(game) : null;

        /// <summary>
        /// Copies the reverse-engineering package (script.json, stringliteral.json, il2cpp.h, il2cpp_ghidra.h,
        /// il2cpp_info.json and the ghidra/ scripts) from the cache folder to <paramref name="destFolder"/>.
        /// Returns the copied file paths.
        /// </summary>
        public static List<string> ExportGhidraPackage(string cacheFolder, string destFolder)
        {
            Directory.CreateDirectory(destFolder);
            var copied = new List<string>();
            foreach (var name in new[] { "script.json", "stringliteral.json", "il2cpp.h", "il2cpp_ghidra.h", "il2cpp_info.json" })
            {
                var src = Path.Combine(cacheFolder, name);
                if (!File.Exists(src)) continue;
                var dst = Path.Combine(destFolder, name);
                File.Copy(src, dst, true);
                copied.Add(dst);
            }
            var scripts = Path.Combine(cacheFolder, "ghidra");
            if (Directory.Exists(scripts))
            {
                var dstDir = Path.Combine(destFolder, "ghidra");
                Directory.CreateDirectory(dstDir);
                foreach (var f in Directory.GetFiles(scripts))
                {
                    var dst = Path.Combine(dstDir, Path.GetFileName(f));
                    File.Copy(f, dst, true);
                    copied.Add(dst);
                }
            }
            return copied;
        }

        /// <summary>
        /// Copies the generated dummy .NET assemblies (the *.dll files Cpp2IL produced) from the cache
        /// folder to <paramref name="destFolder"/>, so they can be opened in dnSpy / ILSpy / dotPeek
        /// (the same output Il2CppDumper writes to its DummyDll folder). Returns the copied file paths.
        /// </summary>
        public static List<string> ExportDummyDlls(string cacheFolder, string destFolder)
        {
            Directory.CreateDirectory(destFolder);
            var copied = new List<string>();
            foreach (var src in Directory.GetFiles(cacheFolder, "*.dll", SearchOption.AllDirectories))
            {
                var dst = Path.Combine(destFolder, Path.GetFileName(src));
                File.Copy(src, dst, true);
                copied.Add(dst);
            }
            return copied;
        }

        #endregion

        #region Generation

        /// <summary>
        /// Returns a folder of dummy DLLs for the game, generating it with Cpp2IL when not cached.
        /// <paramref name="unityVersion"/> is the version string from the loaded assets (e.g. "2021.3.16f1");
        /// when null it is determined from UnityPlayer.dll / the data folder.
        /// </summary>
        public static string GetOrGenerateAssemblies(Il2CppGame game, string unityVersion, Action<string> log = null)
        {
            var folder = GetCacheFolder(game);
            if (IsCached(game))
            {
                log?.Invoke($"Using cached IL2CPP dummy assemblies: {folder}");
                return folder;
            }
#if NETFRAMEWORK
            throw new NotSupportedException("IL2CPP support (Cpp2IL) requires the .NET 8+ build of UnityRift.");
#else
            lock (Il2CppLock)
            {
                if (IsCached(game))
                    return folder;
                Generate(game, unityVersion, folder, log);
                return folder;
            }
#endif
        }

#if !NETFRAMEWORK
        private static void Generate(Il2CppGame game, string unityVersion, string folder, Action<string> log)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            log?.Invoke($"Generating dummy assemblies from IL2CPP binary with Cpp2IL: {game.BinaryPath}");
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);

            Cpp2IL.Core.Logging.Logger.LogEvent onWarn = (msg, src) => log?.Invoke($"[Cpp2IL] {msg.TrimEnd()}");
            Cpp2IL.Core.Logging.Logger.WarningLog += onWarn;
            Cpp2IL.Core.Logging.Logger.ErrorLog += onWarn;
            try
            {
                Cpp2IL.Core.Cpp2IlApi.Init(Path.Combine(CacheRoot, "plugins"));

                AssetRipper.Primitives.UnityVersion version = default;
                if (!string.IsNullOrEmpty(unityVersion))
                {
                    try { version = AssetRipper.Primitives.UnityVersion.Parse(unityVersion); }
                    catch { version = default; }
                }
                if (version == default && (game.UnityPlayerPath != null || game.DataPath != null))
                {
                    try { version = Cpp2IL.Core.Cpp2IlApi.DetermineUnityVersion(game.UnityPlayerPath, game.DataPath); }
                    catch (Exception ex) { log?.Invoke($"[Cpp2IL] Could not determine Unity version: {ex.Message}"); }
                }
                if (version == default)
                    throw new InvalidOperationException("Unity version is required for IL2CPP processing but could not be determined. Load the game's assets first or specify the version.");
                log?.Invoke($"[Cpp2IL] Unity version {version}");

                Cpp2IL.Core.Cpp2IlApi.InitializeLibCpp2Il(game.BinaryPath, game.MetadataPath, version, false);
                var ctx = Cpp2IL.Core.Cpp2IlApi.CurrentAppContext;
                log?.Invoke($"[Cpp2IL] Parsed {ctx.Assemblies.Count} assemblies ({sw.ElapsedMilliseconds} ms)");

                var layers = new List<Cpp2IL.Core.Api.Cpp2IlProcessingLayer>
                {
                    new Cpp2IL.Core.ProcessingLayers.AttributeAnalysisProcessingLayer(),
                    new Cpp2IL.Core.ProcessingLayers.AttributeInjectorProcessingLayer(),
                };
                foreach (var l in layers) l.PreProcess(ctx, layers);
                foreach (var l in layers) l.Process(ctx, null);

                new Cpp2IL.Core.OutputFormats.AsmResolverDllOutputFormatDefault().DoOutput(ctx, folder);

                // Ghidra / reverse-engineering helpers (script.json, il2cpp.h, ...) while LibCpp2IL is still loaded.
                try
                {
                    var swG = System.Diagnostics.Stopwatch.StartNew();
                    var info = Il2CppGhidraExporter.Write(folder, game.BinaryPath, game.MetadataPath, version.ToString(), log);
                    log?.Invoke($"[il2cpp] Wrote script.json / il2cpp.h: {info.Methods} methods (+{info.GenericMethods} generic), {info.Strings} strings, {info.MetadataSymbols + info.MetadataMethods} metadata symbols, {info.Structs} structs, image base {info.ImageBase} ({swG.ElapsedMilliseconds} ms)");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[il2cpp] Ghidra helper generation failed (dummy DLLs are still usable): {ex}");
                }

                // Cpp2IL may nest the DLLs in a sub-folder; flatten so the loader sees one folder.
                var dlls = Directory.GetFiles(folder, "*.dll", SearchOption.AllDirectories);
                foreach (var dll in dlls)
                {
                    var target = Path.Combine(folder, Path.GetFileName(dll));
                    if (!string.Equals(dll, target, StringComparison.OrdinalIgnoreCase))
                        File.Move(dll, target);
                }
                File.WriteAllText(Path.Combine(folder, ".complete"), $"{game.BinaryPath}\n{game.MetadataPath}\n{version}\n{DateTime.UtcNow:o}\n");
                log?.Invoke($"Generated {dlls.Length} dummy assemblies in {sw.ElapsedMilliseconds} ms -> {folder}");
            }
            catch
            {
                try { Directory.Delete(folder, true); } catch { /* ignore */ }
                throw;
            }
            finally
            {
                Cpp2IL.Core.Logging.Logger.WarningLog -= onWarn;
                Cpp2IL.Core.Logging.Logger.ErrorLog -= onWarn;
                try { Cpp2IL.Core.Cpp2IlApi.ResetInternalState(); } catch { /* ignore */ }
                GC.Collect();
            }
        }
#endif

        #endregion
    }
}
