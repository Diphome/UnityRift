#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Newtonsoft.Json;

namespace UnityRift
{
    /// <summary>
    /// Extracts per-type field offsets and enum value maps from the Cpp2IL dummy assemblies (via Mono.Cecil)
    /// into <c>il2cpp_types.json</c>, so <see cref="Il2CppSymbolIndex"/> can answer "what field is at offset
    /// 0x24 of Type X" and "what enum constant is 3 in Type Y" while reading a Ghidra decompilation.
    ///
    /// Cpp2IL annotates each field with <c>[FieldOffset(Offset = "0x..")]</c> and keeps enum constants, so the
    /// dummy DLLs carry the authoritative layout the raw il2cpp.h header only implies.
    /// </summary>
    public static class Il2CppTypesJson
    {
        /// <summary>Builds <c>il2cpp_types.json</c> in <paramref name="folder"/> from the dummy DLLs there (cached: skipped if present unless <paramref name="force"/>).</summary>
        public static string Build(string folder, bool force = false, Action<string> log = null)
        {
            var outPath = Path.Combine(folder, Il2CppSymbolIndex.TypesFileName);
            if (!force && File.Exists(outPath)) return outPath;

            var dlls = Directory.EnumerateFiles(folder, "*.dll", SearchOption.TopDirectoryOnly).ToList();
            if (dlls.Count == 0) { log?.Invoke($"[il2cpp] No dummy DLLs in {folder}; skipping {Il2CppSymbolIndex.TypesFileName}."); return null; }

            var types = new Dictionary<string, object>();
            var rp = new ReaderParameters { ReadSymbols = false, ReadingMode = ReadingMode.Deferred };
            var typeCount = 0;
            foreach (var dll in dlls)
            {
                AssemblyDefinition asm;
                try { asm = AssemblyDefinition.ReadAssembly(dll, rp); }
                catch (Exception ex) { log?.Invoke($"[il2cpp] {Path.GetFileName(dll)}: {ex.Message}"); continue; }
                using (asm)
                {
                    foreach (var module in asm.Modules)
                        foreach (var td in AllTypes(module.Types))
                        {
                            var entry = Convert(td);
                            if (entry != null) { types[NormalizeName(td)] = entry; typeCount++; }
                        }
                }
            }

            File.WriteAllText(outPath, JsonConvert.SerializeObject(new { types }, Formatting.Indented));
            log?.Invoke($"[il2cpp] Wrote {Il2CppSymbolIndex.TypesFileName} ({typeCount} types) to {folder}");
            return outPath;
        }

        private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> top)
        {
            foreach (var t in top)
            {
                yield return t;
                if (t.HasNestedTypes)
                    foreach (var n in AllTypes(t.NestedTypes))
                        yield return n;
            }
        }

        /// <summary>Cecil FullName uses '/' for nested types; script.json uses '.'. Also drop the module/assembly noise.</summary>
        private static string NormalizeName(TypeDefinition td) => td.FullName.Replace('/', '.');

        private static object Convert(TypeDefinition td)
        {
            if (td.IsEnum)
            {
                var values = new Dictionary<string, long>();
                foreach (var f in td.Fields)
                    if (f.HasConstant && f.Constant != null)
                        try { values[f.Name] = System.Convert.ToInt64(f.Constant, CultureInfo.InvariantCulture); } catch { /* skip */ }
                var underlying = td.Fields.FirstOrDefault(f => f.Name == "value__")?.FieldType.Name ?? "int";
                return new { @enum = true, underlying, enumValues = values };
            }

            var fields = new List<object>();
            foreach (var f in td.Fields)
            {
                if (f.IsLiteral) continue; // consts have no storage / offset
                var offset = FieldOffset(f);
                fields.Add(new { name = f.Name, offset, type = f.FieldType.Name, @static = f.IsStatic });
            }
            if (fields.Count == 0) return null;
            return new { @enum = false, fields };
        }

        private static long FieldOffset(FieldDefinition f)
        {
            foreach (var a in f.CustomAttributes)
            {
                if (a.AttributeType.Name != "FieldOffsetAttribute") continue;
                var raw = a.Fields.Concat(a.Properties).FirstOrDefault(n => n.Name == "Offset").Argument.Value as string
                          ?? (a.HasConstructorArguments ? a.ConstructorArguments[0].Value as string : null);
                if (raw != null && Il2CppConstantResolver.TryParseHex(raw, out var v)) return (long)v;
            }
            return -1;
        }
    }
}
#endif
