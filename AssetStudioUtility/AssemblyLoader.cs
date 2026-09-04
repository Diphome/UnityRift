using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AssetStudio
{
    public class AssemblyLoader
    {
        public bool Loaded;
        /// <summary>Folder the assemblies were loaded from (null when nothing was loaded).</summary>
        public string LoadedPath;
        private Dictionary<string, ModuleDefinition> moduleDic = new Dictionary<string, ModuleDefinition>();

        /// <summary>All loaded modules, keyed by module file name (e.g. "Assembly-CSharp.dll").</summary>
        public IReadOnlyDictionary<string, ModuleDefinition> Modules => moduleDic;

        public void Load(string path)
        {
            var files = Directory.GetFiles(path, "*.dll");
            var resolver = new MyAssemblyResolver();
            resolver.AddSearchDirectory(path);
            var readerParameters = new ReaderParameters();
            readerParameters.AssemblyResolver = resolver;
            foreach (var file in files)
            {
                try
                {
                    var assembly = AssemblyDefinition.ReadAssembly(file, readerParameters);
                    resolver.Register(assembly);
                    moduleDic.Add(assembly.MainModule.Name, assembly.MainModule);
                }
                catch
                {
                    // ignored
                }
            }
            LoadedPath = path;
            Loaded = true;
        }

        /// <summary>
        /// Looks for a Unity "Managed" folder (the game's .NET assemblies) near the given
        /// asset file paths. Checks each ancestor directory for <c>Managed/*.dll</c> and for
        /// <c>*_Data/Managed/*.dll</c>. Returns null when none is found.
        /// </summary>
        public static string FindManagedFolder(IEnumerable<string> assetPaths)
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
                    var found = ProbeManaged(dir);
                    if (found != null)
                        return found;
                    dir = Path.GetDirectoryName(dir);
                }
            }
            return null;
        }

        private static string ProbeManaged(string dir)
        {
            try
            {
                if (Path.GetFileName(dir).Equals("Managed", StringComparison.OrdinalIgnoreCase) && HasDlls(dir))
                    return dir;
                var direct = Path.Combine(dir, "Managed");
                if (HasDlls(direct))
                    return direct;
                foreach (var data in Directory.EnumerateDirectories(dir, "*_Data"))
                {
                    var managed = Path.Combine(data, "Managed");
                    if (HasDlls(managed))
                        return managed;
                }
            }
            catch
            {
                // access denied etc.
            }
            return null;
        }

        private static bool HasDlls(string dir)
        {
            try
            {
                return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.dll").Any();
            }
            catch
            {
                return false;
            }
        }

        public TypeDefinition GetTypeDefinition(string assemblyName, string fullName)
        {
            if (!moduleDic.TryGetValue(assemblyName, out var module) && !assemblyName.EndsWith(".dll"))
            {
                assemblyName += ".dll";
                moduleDic.TryGetValue(assemblyName, out module);
            }
            if (module == null) 
                return null;

            var typeDef = module.GetType(fullName);
            if (typeDef == null && assemblyName == "UnityEngine.dll")
            {
                foreach (var pair in moduleDic)
                {
                    typeDef = pair.Value.GetType(fullName);
                    if (typeDef != null)
                    {
                        break;
                    }
                }
            }
            return typeDef;
        }

        public void Clear()
        {
            foreach (var pair in moduleDic)
            {
                pair.Value.Dispose();
            }
            moduleDic.Clear();
            LoadedPath = null;
            Loaded = false;
        }
    }
}
