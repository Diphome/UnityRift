using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityRift
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
            idx.LoadTypes();
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

        /// <summary>Name search over methods and metadata symbols (substring, or regex). Case-insensitive.
        /// With <paramref name="fuzzy"/>, also matches near-misses (typo-tolerant) and ranks by similarity.</summary>
        public JArray FindByName(string query, bool regex, int max = 50, bool fuzzy = false)
        {
            if (fuzzy && !regex)
                return FindFuzzy(query, max);

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

        private JArray FindFuzzy(string query, int max)
        {
            var q = query.ToLowerInvariant();
            var scored = new List<(double score, Method m)>();
            foreach (var m in Methods)
            {
                if (m.Name == null) continue;
                var name = m.Name.ToLowerInvariant();
                var contains = name.IndexOf(q, StringComparison.Ordinal) >= 0;
                var s = Ratio(q, Leaf(name));
                if (contains) s += 1.0;
                if (s >= 0.6) scored.Add((s, m));
            }
            return new JArray(scored.OrderByDescending(x => x.score).Take(max).Select(x =>
                new JObject { ["kind"] = "method", ["name"] = x.m.Name, ["rva"] = "0x" + x.m.Rva.ToString("X"), ["va"] = Va(x.m.Rva), ["signature"] = x.m.Signature, ["score"] = Math.Round(x.score, 3) }));
        }

        #region fuzzy suggestion

        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the","and","for","with","this","that","from","into","your","have","will","value","float","int","bool",
            "void","null","true","false","return","string","object","class","struct","public","private","static",
            "get","set","update","start","awake","ctor","field","backing","enum","using","namespace",
        };

        /// <summary>The type/method leaf name (drops namespace, keeps the identifier we compare against).</summary>
        private static string Leaf(string name)
        {
            var t = name.Split(new[] { "$$" }, StringSplitOptions.None)[0];
            var dot = t.LastIndexOf('.');
            return dot >= 0 ? t.Substring(dot + 1) : t;
        }

        private static string TypeOf(string name)
        {
            var i = name.IndexOf("$$", StringComparison.Ordinal);
            return i < 0 ? name : name.Substring(0, i);
        }

        /// <summary>difflib-style similarity ratio in [0,1] via normalised Levenshtein distance.</summary>
        public static double Ratio(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
            var n = a.Length; var m = b.Length;
            var d = new int[m + 1];
            for (var j = 0; j <= m; j++) d[j] = j;
            for (var i = 1; i <= n; i++)
            {
                var prev = d[0];
                d[0] = i;
                for (var j = 1; j <= m; j++)
                {
                    var tmp = d[j];
                    d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                    prev = tmp;
                }
            }
            var dist = d[m];
            var max = Math.Max(n, m);
            return max == 0 ? 1.0 : 1.0 - (double)dist / max;
        }

        /// <summary>Pulls domain tokens from arbitrary text (a script, notes, keywords): CamelCase parts and long identifiers.</summary>
        public static List<string> KeywordsFromText(string text)
        {
            var toks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(text, @"[A-Z][a-z]+(?:[A-Z][a-z]+)+"))
                foreach (Match p in Regex.Matches(m.Value, @"[A-Z][a-z]+"))
                    if (p.Value.Length >= 4) toks.Add(p.Value.ToLowerInvariant());
            foreach (Match m in Regex.Matches(text, @"[A-Za-z_]{4,}"))
            {
                var w = m.Value.Trim('_').ToLowerInvariant();
                if (w.Length >= 4 && !StopWords.Contains(w)) toks.Add(w);
            }
            return toks.ToList();
        }

        /// <summary>
        /// Given keywords (or tokens extracted from text), suggests IL2CPP types/methods worth
        /// decompiling — ranked candidate <c>Type$$</c> prefixes (and optionally <c>Type$$Method</c> hits),
        /// ready to paste into a names file or feed to Ghidra.
        /// </summary>
        public JObject Suggest(IEnumerable<string> keywords, int top = 8, bool methods = false, bool fuzzy = false)
        {
            var kws = keywords.Select(k => k.ToLowerInvariant()).Where(k => k.Length >= 4 && !StopWords.Contains(k)).Distinct().ToList();
            var typeScore = new Dictionary<string, double>(StringComparer.Ordinal);
            var methodScore = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var kw in kws)
            {
                var hits = new List<(double score, string name)>();
                foreach (var m in Methods)
                {
                    if (m.Name == null) continue;
                    var lname = m.Name.ToLowerInvariant();
                    var contains = lname.Contains(kw);
                    var r = Ratio(kw, Leaf(lname));
                    var score = contains ? 1.0 + r : r;
                    if (contains || (fuzzy && r >= 0.72)) hits.Add((score, m.Name));
                }
                hits.Sort((x, y) => y.score.CompareTo(x.score));
                foreach (var (score, name) in hits.Take(top * 4))
                {
                    var t = TypeOf(name);
                    typeScore[t] = Math.Max(typeScore.TryGetValue(t, out var s) ? s : 0, score);
                    if (methods) methodScore[name] = score;
                }
            }
            var types = typeScore.OrderByDescending(x => x.Value).Select(x => x.Key + "$$").ToList();
            var result = new JObject
            {
                ["keywords"] = new JArray(kws),
                ["typeCount"] = types.Count,
                ["types"] = new JArray(types),
            };
            if (methods)
                result["methods"] = new JArray(methodScore.OrderByDescending(x => x.Value).Take(top * 3).Select(x => x.Key));
            return result;
        }

        #endregion

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

        #region address -> name (for symbolizing decompilation)

        private Dictionary<ulong, string> _rvaToName;

        private Dictionary<ulong, string> RvaToName()
        {
            if (_rvaToName != null) return _rvaToName;
            var d = new Dictionary<ulong, string>();
            foreach (var m in Methods) if (!d.ContainsKey(m.Rva)) d[m.Rva] = m.Name;
            foreach (var s in Symbols) if (!d.ContainsKey(s.Rva)) d[s.Rva] = s.Name;
            _rvaToName = d;
            return d;
        }

        /// <summary>Managed name for a function/data address exactly at <paramref name="rva"/> (RVA), or null.</summary>
        public string NameForRva(ulong rva) => RvaToName().TryGetValue(rva, out var n) ? n : null;

        /// <summary>Managed name for a Ghidra address token that may be an RVA or a VA (image base applied), or null.</summary>
        public string NameForToken(ulong value)
        {
            var n = NameForRva(value);
            if (n != null) return n;
            if (ImageBase != 0 && value >= ImageBase) return NameForRva(value - ImageBase);
            return null;
        }

        #endregion

        #region type layouts + enums (loaded from il2cpp_types.json)

        public class FieldEntry { public string Name; public long Offset; public string Type; public bool Static; }
        public class TypeEntry
        {
            public bool IsEnum;
            public string Underlying;
            public List<FieldEntry> Fields = new List<FieldEntry>();
            public Dictionary<string, long> EnumValues = new Dictionary<string, long>();
        }

        public Dictionary<string, TypeEntry> Types { get; private set; }

        public bool HasTypes => Types != null && Types.Count > 0;

        public static readonly string TypesFileName = "il2cpp_types.json";

        private void LoadTypes()
        {
            var path = Path.Combine(Folder, TypesFileName);
            if (!File.Exists(path)) return;
            Types = new Dictionary<string, TypeEntry>(StringComparer.OrdinalIgnoreCase);
            var root = JObject.Parse(File.ReadAllText(path));
            var types = root["types"] as JObject;
            if (types == null) return;
            foreach (var p in types.Properties())
            {
                var o = (JObject)p.Value;
                var te = new TypeEntry
                {
                    IsEnum = o.Value<bool?>("enum") ?? false,
                    Underlying = o.Value<string>("underlying"),
                };
                foreach (var f in o["fields"] as JArray ?? new JArray())
                    te.Fields.Add(new FieldEntry
                    {
                        Name = f.Value<string>("name"),
                        Offset = f.Value<long?>("offset") ?? -1,
                        Type = f.Value<string>("type"),
                        Static = f.Value<bool?>("static") ?? false,
                    });
                var ev = o["enumValues"] as JObject;
                if (ev != null) foreach (var e in ev.Properties()) te.EnumValues[e.Name] = e.Value.Value<long>();
                Types[p.Name] = te;
            }
        }

        private TypeEntry FindType(string name)
        {
            if (Types == null) return null;
            if (Types.TryGetValue(name, out var t)) return t;
            // tolerate Type$$Method / trailing noise, and match on the leaf type name
            var q = name.Split(new[] { "$$" }, StringSplitOptions.None)[0];
            if (Types.TryGetValue(q, out t)) return t;
            return Types.FirstOrDefault(kv => kv.Key.EndsWith("." + q, StringComparison.OrdinalIgnoreCase) || kv.Key.Equals(q, StringComparison.OrdinalIgnoreCase)).Value;
        }

        /// <summary>Full field layout of a type, or the field(s) at/covering a byte offset when <paramref name="offset"/> is given.</summary>
        public JObject FieldLookup(string typeName, long? offset)
        {
            var result = new JObject { ["query"] = typeName };
            if (!HasTypes) { result["error"] = $"no {TypesFileName} in package (generate it with the dummy DLLs)"; return result; }
            var t = FindType(typeName);
            if (t == null) { result["error"] = "type not found"; return result; }
            IEnumerable<FieldEntry> fields = t.Fields;
            if (offset.HasValue)
            {
                var exact = t.Fields.Where(f => f.Offset == offset.Value).ToList();
                fields = exact.Count > 0 ? exact : t.Fields.Where(f => f.Offset >= 0 && f.Offset <= offset.Value).OrderByDescending(f => f.Offset).Take(1);
                result["offset"] = "0x" + offset.Value.ToString("X");
            }
            result["fields"] = new JArray(fields.Select(f => new JObject
            {
                ["name"] = f.Name, ["offset"] = "0x" + f.Offset.ToString("X"), ["type"] = f.Type, ["static"] = f.Static,
            }));
            return result;
        }

        /// <summary>All values of an enum type, or the name(s) for a specific value when <paramref name="value"/> is given.</summary>
        public JObject EnumLookup(string typeName, long? value)
        {
            var result = new JObject { ["query"] = typeName };
            if (!HasTypes) { result["error"] = $"no {TypesFileName} in package (generate it with the dummy DLLs)"; return result; }
            var t = FindType(typeName);
            if (t == null || !t.IsEnum) { result["error"] = t == null ? "type not found" : "not an enum"; return result; }
            result["underlying"] = t.Underlying;
            if (value.HasValue)
            {
                result["value"] = value.Value;
                result["names"] = new JArray(t.EnumValues.Where(kv => kv.Value == value.Value).Select(kv => kv.Key));
                // also decompose as flags if no exact hit
                if (((JArray)result["names"]).Count == 0 && value.Value != 0)
                {
                    var flags = t.EnumValues.Where(kv => kv.Value != 0 && (value.Value & kv.Value) == kv.Value).Select(kv => kv.Key).ToList();
                    if (flags.Count > 0) result["flags"] = new JArray(flags);
                }
            }
            else
            {
                result["values"] = new JObject(t.EnumValues.OrderBy(kv => kv.Value).Select(kv => new JProperty(kv.Key, kv.Value)));
            }
            return result;
        }

        #endregion

        /// <summary>Every method with its VA and C prototype, optionally filtered by a name regex — a batch "apply plan"
        /// for driving Ghidra (rename + set prototype) via its MCP or a script.</summary>
        public JArray ApplyPlan(string nameRegex, int max = 100000)
        {
            Regex re = string.IsNullOrEmpty(nameRegex) || nameRegex == "*" ? null : new Regex(nameRegex, RegexOptions.IgnoreCase);
            var arr = new JArray();
            foreach (var m in Methods)
            {
                if (re != null && !re.IsMatch(m.Name)) continue;
                if (arr.Count >= max) break;
                arr.Add(new JObject
                {
                    ["va"] = Va(m.Rva), ["rva"] = "0x" + m.Rva.ToString("X"),
                    ["name"] = m.Name, ["prototype"] = m.Signature,
                });
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
