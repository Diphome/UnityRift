using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace UnityRift
{
    /// <summary>
    /// Reconstructs the on-the-wire layout of a Request/Response by reading the ordered sequence of
    /// serializer calls in a decompiled <c>Serialize</c>/<c>Deserialize</c> method. IL2CPP dummy DLLs
    /// have no method bodies, so this works on the Ghidra decompilation text (best after
    /// <see cref="Il2CppDecompCleaner"/> has symbolized <c>FUN_</c> calls to managed names): it walks
    /// the function top-to-bottom, extracts each Write/Read/Serialize primitive in order, tracks loop
    /// nesting (→ list/array elements) and flags an integer written/read just before a loop as a
    /// length/count prefix (the "read-ahead" a hand analyst always marks).
    ///
    /// Heuristic by nature — it reports the sequence the code executes, not a proven schema.
    /// </summary>
    public static class Il2CppWireLayout
    {
        // Serializer primitive: optional "Declaring$$" prefix (after symbolize), then a verb + type.
        // Serialization verbs only (Get/Put dropped: they false-match GetComponent/GetType/etc.).
        private static readonly Regex Prim = new Regex(
            @"(?:([A-Za-z_]\w*)\$\$)?\b(Write|Read|Serialize|Deserialize|Encode|Decode|Pack|Unpack)([A-Za-z0-9_]*)\s*\(",
            RegexOptions.Compiled);
        private static readonly Regex LoopHead = new Regex(@"\b(for|while)\s*\(|\bdo\b", RegexOptions.Compiled);
        private static readonly Regex FieldRef = new Regex(@"->\s*([A-Za-z_]\w*)|\b(field_[0-9A-Fa-f]+)\b|\+ (0x[0-9A-Fa-f]+)\)", RegexOptions.Compiled);

        private static readonly HashSet<string> IntTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Byte", "SByte", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Int", "UInt",
            "7BitEncodedInt", "VarInt", "Varint", "Length", "Count", "Size",
        };

        private sealed class Op
        {
            public int Order;
            public string Dir;      // write | read | nested
            public string Type;     // Int32 / String / … / ?
            public string Method;   // full leaf, e.g. WriteInt32
            public string Declaring;
            public bool InLoop;
            public string Target;   // field/offset the value came from/into, if visible
            public string Note;
        }

        public static JObject Analyze(string text, string label = null)
        {
            var lines = text.Replace("\r", "").Split('\n');
            var ops = new List<Op>();
            var loopStack = new List<bool>(); // one bool per open brace: is it a loop block?
            var order = 0;

            foreach (var raw in lines)
            {
                var line = raw;
                var lineIsLoop = LoopHead.IsMatch(line);

                // record primitives on this line (before adjusting brace depth, so same-line `for(){...}` counts)
                foreach (Match m in Prim.Matches(line))
                {
                    var verb = m.Groups[2].Value;
                    var typeTok = m.Groups[3].Value;
                    var dir = verb == "Write" || verb == "Encode" || verb == "Pack" ? "write"
                            : verb == "Read" || verb == "Decode" || verb == "Unpack" ? "read"
                            : "nested"; // Serialize/Deserialize
                    var type = string.IsNullOrEmpty(typeTok) ? (dir == "nested" ? "object" : "?") : typeTok;
                    var fieldM = FieldRef.Match(line);
                    var target = fieldM.Success ? (fieldM.Groups[1].Value + fieldM.Groups[2].Value + fieldM.Groups[3].Value) : null;
                    ops.Add(new Op
                    {
                        Order = ++order, Dir = dir, Type = type, Method = verb + typeTok,
                        Declaring = m.Groups[1].Success ? m.Groups[1].Value : null,
                        InLoop = loopStack.Any(b => b), Target = string.IsNullOrEmpty(target) ? null : target,
                    });
                }

                // adjust brace nesting for subsequent lines
                foreach (var ch in line)
                {
                    if (ch == '{') { loopStack.Add(lineIsLoop); lineIsLoop = false; }
                    else if (ch == '}' && loopStack.Count > 0) loopStack.RemoveAt(loopStack.Count - 1);
                }
            }

            // mark length/count prefixes: an int op just before the loop it sizes
            for (var i = 0; i < ops.Count - 1; i++)
                if (!ops[i].InLoop && ops[i + 1].InLoop && IntTypes.Contains(ops[i].Type))
                    ops[i].Note = "length/count prefix (read-ahead for the following list)";

            var writes = ops.Count(o => o.Dir == "write");
            var reads = ops.Count(o => o.Dir == "read");
            var direction = writes > reads * 2 ? "serialize (write)" : reads > writes * 2 ? "deserialize (read)" : "mixed";

            return new JObject
            {
                ["label"] = label,
                ["direction"] = direction,
                ["opCount"] = ops.Count,
                ["note"] = "Heuristic: the ordered sequence the code executes. Bare Write/Read (no type suffix) show '?' — confirm the type from the field or the decompiled arg.",
                ["layout"] = new JArray(ops.Select(o =>
                {
                    var j = new JObject
                    {
                        ["#"] = o.Order,
                        ["dir"] = o.Dir,
                        ["type"] = o.Type,
                    };
                    if (o.InLoop) j["list"] = true;
                    if (o.Target != null) j["target"] = o.Target;
                    if (o.Declaring != null) j["via"] = o.Declaring;
                    if (o.Note != null) j["note"] = o.Note;
                    return j;
                })),
            };
        }
    }
}
