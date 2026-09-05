using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UnityRift
{
    /// <summary>
    /// Exports the loaded .NET assemblies: C#-like stub files (one .cs per top-level type,
    /// nested types inlined) laid out as &lt;output&gt;/&lt;Assembly&gt;/&lt;Namespace&gt;/&lt;Type&gt;.cs,
    /// and/or a copy of the raw assembly files. Shared by the GUI and the CLI.
    /// </summary>
    public static class DotNetExporter
    {
        public class Result
        {
            public int Types;
            public int Files;
            public int Failed;
            public string OutputFolder;
        }

        /// <summary>Top-level types of a module, excluding the compiler's &lt;Module&gt; pseudo-type.</summary>
        public static IEnumerable<TypeDefinition> ExportableTypes(ModuleDefinition module)
        {
            return module.Types.Where(t => t.Name != "<Module>");
        }

        /// <summary>
        /// Writes C# stubs for every type of <paramref name="modules"/> that passes
        /// <paramref name="typeFilter"/> (null = all types).
        /// </summary>
        public static Result ExportStubs(IEnumerable<ModuleDefinition> modules, string outputFolder, bool withIL,
            Func<TypeDefinition, bool> typeFilter = null, Action<int, int> progress = null, Action<string> warn = null)
        {
            var result = new Result { OutputFolder = outputFolder };
            var work = new List<(ModuleDefinition module, TypeDefinition type)>();
            foreach (var module in modules)
            {
                foreach (var type in ExportableTypes(module))
                {
                    if (typeFilter == null || typeFilter(type))
                        work.Add((module, type));
                }
            }

            var done = 0;
            foreach (var (module, type) in work)
            {
                if (ExportTypeStub(module, type, outputFolder, withIL, warn))
                {
                    result.Types++;
                    result.Files++;
                }
                else
                {
                    result.Failed++;
                }
                progress?.Invoke(++done, work.Count);
            }
            return result;
        }

        /// <summary>Writes one type (with its nested types) to its stub file. Returns the file path or null.</summary>
        public static string ExportTypeStub(TypeDefinition type, string outputFolder, bool withIL, Action<string> warn = null)
        {
            return ExportTypeStub(type.Module, type, outputFolder, withIL, warn) ? StubFilePath(type, outputFolder) : null;
        }

        private static bool ExportTypeStub(ModuleDefinition module, TypeDefinition type, string outputFolder, bool withIL, Action<string> warn)
        {
            try
            {
                var path = StubFilePath(type, outputFolder);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, DotNetTypeDumper.DumpType(type, withIL));
                return true;
            }
            catch (Exception ex)
            {
                warn?.Invoke($"Failed to export {type.FullName} from {module.Name}: {ex.Message}");
                return false;
            }
        }

        public static string StubFilePath(TypeDefinition type, string outputFolder)
        {
            // Nested types are written inside their declaring type's file.
            while (type.DeclaringType != null)
                type = type.DeclaringType;
            var assembly = SafeName(Path.GetFileNameWithoutExtension(type.Module.Name));
            var dir = Path.Combine(outputFolder, assembly);
            if (!string.IsNullOrEmpty(type.Namespace))
            {
                foreach (var part in type.Namespace.Split('.'))
                    dir = Path.Combine(dir, SafeName(part));
            }
            return Path.Combine(dir, SafeName(type.Name) + ".cs");
        }

        /// <summary>Copies the assembly files themselves (e.g. IL2CPP dummy DLLs generated into a cache) to <paramref name="outputFolder"/>.</summary>
        public static Result ExportAssemblyFiles(IEnumerable<ModuleDefinition> modules, string outputFolder, Action<int, int> progress = null, Action<string> warn = null)
        {
            var result = new Result { OutputFolder = outputFolder };
            var list = modules.ToList();
            Directory.CreateDirectory(outputFolder);
            var done = 0;
            foreach (var module in list)
            {
                try
                {
                    var source = module.FileName;
                    if (string.IsNullOrEmpty(source) || !File.Exists(source))
                        throw new FileNotFoundException("assembly file not found", source ?? module.Name);
                    File.Copy(source, Path.Combine(outputFolder, Path.GetFileName(source)), true);
                    result.Files++;
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    warn?.Invoke($"Failed to copy {module.Name}: {ex.Message}");
                }
                progress?.Invoke(++done, list.Count);
            }
            return result;
        }

        private static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "_";
            var chars = name.ToCharArray();
            var invalid = Path.GetInvalidFileNameChars();
            for (var i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0)
                    chars[i] = '_';
            }
            var safe = new string(chars).TrimEnd('.', ' ');
            return safe.Length == 0 ? "_" : safe;
        }
    }
}
