using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AssetStudio
{
    /// <summary>
    /// Fast queries over a generated IL2CPP package (script.json / stringliteral.json / il2cpp_info.json):
    /// address → managed method / string / metadata symbol, and name → addresses. Meant for an agent
    /// working alongside a Ghidra session (translate what Ghidra shows into managed names and back).
    /// Works on any .NET (plain JSON, no Cpp2IL needed).
    /// </summary>
    public class Il2CppSymbolIndex
    {
        public class Method { public ulong Rva; public string Name; public string Signature; }
        public class Symbol { public ulong Rva; public string Name; public string Kind; public string Signature; public ulong MethodRva; }
        public class Str { public ulong Rva; public string Value; }

        public string Folder { get; private set; }
        public ulong ImageBase { get; private set; }
        public int PointerSize { get; private set; } = 8;
        public JObject Info { get; private set; }
        public List<Method> Methods { get; } = new List<Method>();     // sorted by Rva
        public List<Symbol> Symbols { get; } = new List<Symbol>();     // metadata + metadata methods, sorted by Rva
        public List<Str> Strings { get; } = new List<Str>();           // sorted by Rva
        public ulong[] FunctionStarts { get; private set; } = Array.Empty<ulong>();

        public static bool Exists(string folder) => File.Exists(Path.Combine(folder, "script.json"));

        public static Il2CppSymbolIndex Load(string folder)
        {
            var idx = new Il2CppSymbolIndex { Folder = folder };
            var infoPath = Path.Combine(folder, "il2cpp_info.json");
            if (File.Exists(infoPath))
            {
                idx.Info = JObject.Parse(File.ReadAllText(infoPath));
                var ib = idx.Info.Value<string>("ImageBase");
                if (!string.IsNullOrEmpty(ib)) idx.ImageBase = ParseHex(ib);
                idx.PointerSize = idx.Info.Value<int?>("PointerSize") ?? 8;
            }
            var json = JObject.Parse(File.ReadAllText(Path.Combine(folder, "script.json")));
            foreach (var m in json["ScriptMethod"] ?? new JArray())
                idx.Methods.Add(new Method { Rva = m.Value<ulong>("Address"), Name = m.Value<string>("Name"), Signature = m.Value<string>("Signature") });
            foreach (var m in json["ScriptMetadata"] ?? new JArray())
                idx.Symbols.Add(new Symbol { Rva = m.Value<ulong>("Address"), Name = m.Value<string>("Name"), Kind = "metadata", Signature = m.Value<string>("Signature") });
            foreach (var m in json["ScriptMetadataMethod"] ?? new JArray())
                idx.Symbols.Add(new Symbol { Rva = m.Value<ulong>("Address"), Name = m.Value<string>("Name"), Kind = "methodinfo", MethodRva = m.Value<ulong?>("MethodAddress") ?? 0 });
            foreach (var s in json["ScriptString"] ?? new JArray())
                idx.Strings.Add(new Str { Rva = s.Value<ulong>("Address"), Value = s.Value<string>("Value") });
            idx.FunctionStarts = (json["Addresses"] as JArray)?.Select(a => a.Value<ulong>()).OrderBy(x => x).ToArray() ?? Array.Empty<ulong>();
            idx.Methods.Sort((a, b) => a.Rva.CompareTo(b.Rva));
            idx.Symbols.Sort((a, b) => a.Rva.CompareTo(b.Rva));
            idx.Strings.Sort((a, b) => a.Rva.CompareTo(b.Rva));
            return idx;
        }

        public static ulong ParseHex(string s)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return ulong.Parse(s, System.Globalization.NumberStyles.HexNumber);
        }

        /// <summary>Interprets an address string: "0x..." (RVA, or VA if ≥ image base), "rva:0x..", "va:0x..", "off:0x.." (file offset unsupported → null).</summary>
        public bool TryParseAddress(string text, out ulong rva, out string how)
        {
            rva = 0; how = null;
            var t = text.Trim();
            var explicitVa = false;
            var explicitRva = false;
            if (t.StartsWith("va:", StringComparison.OrdinalIgnoreCase)) { explicitVa = true; t = t.Substring(3); }
            else if (t.StartsWith("rva:", StringComparison.OrdinalIgnoreCase)) { explicitRva = true; t = t.Substring(4); }
            if (!(t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(t, "^[0-9A-Fa-f]{5,}$")))
                return false;
            ulong v;
            try { v = ParseHex(t); } catch { return false; }
            if (explicitVa || (!explicitRva && ImageBase != 0 && v >= ImageBase))
            {
                if (ImageBase == 0) return false;
                rva = v - ImageBase; how = $"VA 0x{v:X} - image base 0x{ImageBase:X}";
            }
            else
            {
                rva = v; how = "RVA";
            }
            return true;
        }

        public string Va(ulong rva) => ImageBase != 0 ? "0x" + (ImageBase + rva).ToString("X") : null;

        /// <summary>Everything known at/around an RVA: containing method (+offset), symbol, string, next function start.</summary>
        public JObject DescribeAddress(ulong rva)
        {
            var o = new JObject { ["rva"] = "0x" + rva.ToString("X"), ["va"] = Va(rva) };
            var mi = FloorIndex(Methods.Select(m => m.Rva).ToList(), rva);
            if (mi >= 0)
            {
                var m = Methods[mi];
                var nextStart = NextFunctionStart(m.Rva);
                var inside = rva == m.Rva || nextStart == 0 || rva < nextStart;
                var jm = new JObject
                {
                    ["name"] = m.Name, ["rva"] = "0x" + m.Rva.ToString("X"), ["va"] = Va(m.Rva),
                    ["offset"] = "+0x" + (rva - m.Rva).ToString("X"), ["signature"] = m.Signature,
                };
                if (nextStart != 0) jm["nextFunctionRva"] = "0x" + nextStart.ToString("X");
                o[inside ? "method" : "previousMethod"] = jm;
                // other methods sharing the same address (identical bodies folded by the linker)
                var same = Methods.Where(x => x.Rva == m.Rva && x != m).Select(x => x.Name).Take(20).ToList();
                if (same.Count > 0) jm["aliases"] = new JArray(same);
            }
            var s = Strings.FirstOrDefault(x => x.Rva == rva);
            if (s != null) o["string"] = s.Value;
            var sym = Symbols.Where(x => x.Rva == rva).ToList();
            if (sym.Count > 0)
                o["symbols"] = new JArray(sym.Select(x => new JObject { ["name"] = x.Name, ["kind"] = x.Kind, ["signature"] = x.Signature, ["methodRva"] = x.MethodRva != 0 ? "0x" + x.MethodRva.ToString("X") : null }));
            return o;
        }

        private ulong NextFunctionStart(ulong rva)
        {
            var i = FloorIndex(FunctionStarts.ToList(), rva);
            return i >= 0 && i + 1 < FunctionStarts.Length ? FunctionStarts[i + 1] : 0;
        }

        private static int FloorIndex(List<ulong> sorted, ulong value)
        {
            var lo = 0; var hi = sorted.Count - 1; var ans = -1;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (sorted[mid] <= value) { ans = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return ans;
        }

        /// <summary>Name search over methods and metadata symbols (substring, or regex). Case-insensitive.</summary>
        public JArray FindByName(string query, bool regex, int max = 50)
        {
            Func<string, bool> match;
            if (regex)
            {
                var re = new Regex(query, RegexOptions.IgnoreCase);
                match = s => s != null && re.IsMatch(s);
            }
            else
            {
                match = s => s != null && s.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            var arr = new JArray();
            // exact "$$" name or "Type.Method" form first
            var q = query.Replace("::", "$$").Replace(".", "$$");
            var exact = Methods.Where(m => string.Equals(m.Name, query, StringComparison.OrdinalIgnoreCase) || string.Equals(m.Name, q, StringComparison.OrdinalIgnoreCase)).ToList();
            var rest = Methods.Where(m => !exact.Contains(m) && match(m.Name));
            foreach (var m in exact.Concat(rest))
            {
                if (arr.Count >= max) break;
                arr.Add(new JObject { ["kind"] = "method", ["name"] = m.Name, ["rva"] = "0x" + m.Rva.ToString("X"), ["va"] = Va(m.Rva), ["signature"] = m.Signature });
            }
            foreach (var s in Symbols.Where(x => match(x.Name)))
            {
                if (arr.Count >= max) break;
                arr.Add(new JObject { ["kind"] = s.Kind, ["name"] = s.Name, ["rva"] = "0x" + s.Rva.ToString("X"), ["va"] = Va(s.Rva), ["signature"] = s.Signature, ["methodRva"] = s.MethodRva != 0 ? "0x" + s.MethodRva.ToString("X") : null });
            }
            return arr;
        }

        public JArray FindStrings(string query, bool regex, int max = 50)
        {
            Func<string, bool> match;
            if (regex)
            {
                var re = new Regex(query, RegexOptions.IgnoreCase);
                match = s => s != null && re.IsMatch(s);
            }
            else
            {
                match = s => s != null && s.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            var arr = new JArray();
            foreach (var s in Strings.Where(x => match(x.Value)))
            {
                if (arr.Count >= max) break;
                arr.Add(new JObject { ["value"] = s.Value, ["rva"] = "0x" + s.Rva.ToString("X"), ["va"] = Va(s.Rva) });
            }
            return arr;
        }

        public JObject Summary()
        {
            return new JObject
            {
                ["folder"] = Folder,
                ["imageBase"] = ImageBase != 0 ? "0x" + ImageBase.ToString("X") : null,
                ["pointerSize"] = PointerSize,
                ["methods"] = Methods.Count,
                ["strings"] = Strings.Count,
                ["symbols"] = Symbols.Count,
                ["functionStarts"] = FunctionStarts.Length,
                ["info"] = Info,
            };
        }
    }
}
