using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityRift
{
    /// <summary>
    /// Makes Ghidra IL2CPP pseudocode readable: strips the boilerplate that IL2CPP emits into every
    /// function (class-init guards, metadata-init thunks, ctor scaffolding, empty declarations) by
    /// SHAPE rather than by literal DAT_/FUN_ addresses, so it survives a rebased/rebuilt binary, and
    /// annotates hidden float/double/int constants in place via <see cref="Il2CppConstantResolver"/>.
    ///
    /// Framework-agnostic text transform (no Cpp2IL dependency). Mirrors the standalone clean_dec.py
    /// helper, minus its project-specific namespace shortening.
    /// </summary>
    public static class Il2CppDecompCleaner
    {
        // Structural noise, matched by SHAPE (not by specific addresses).
        private static readonly string[] StructSkip =
        {
            // IL2CPP class-init guard triplet:  if ((DAT_x & 1) == 0) { thunk(0x..); DAT_x = 1; }
            @"\(DAT_[0-9a-fx]+ & 1\) == 0",
            @"^\s*DAT_[0-9a-fx]+ = 1;\s*$",
            @"^\s*_?thunk_FUN_[0-9a-f]+\(0x[0-9a-f]+\);\s*$",   // il2cpp_codegen_initialize_* thunk
            // Managed base-ctor / init boilerplate
            @"System_Object___ctor\([^)]*\)",
            @"^\s*Il2CppObject__.*;\s*$",
            // Decompiler bookkeeping
            @"WARNING: (Globals|Type prop|Could not|Removing|Restarted)",
            @"^\s*halt_baddata\(\);\s*$",
            // Bare local declarations with no initialiser (undefined4 uVarN; etc.)
            @"^\s*(undefined\d*|u?long|u?int|float|double|u?short|byte|bool|code|char)\s*\*?\s*[a-zA-Z_]\w*\s*;\s*$",
            // Empty lines and lone braces
            @"^\s*$",
            @"^\s*[{}]\s*$",
            // Trivial goto/label scaffolding
            @"^\s*goto LAB_\w+;\s*$",
            @"^\s*LAB_\w+:\s*$",
        };

        private static readonly Regex StructRe = new Regex(string.Join("|", StructSkip), RegexOptions.Compiled);
        private static readonly Regex HexStore = new Regex(@"=\s*(0x[0-9a-fA-F]{8,16})\s*;", RegexOptions.Compiled);
        private static readonly Regex DatRef = new Regex(@"\bDAT_([0-9a-fA-F]{5,8})\b", RegexOptions.Compiled);

        private static string AnnotateFloats(string line, BinaryImage img)
        {
            var notes = new List<string>();
            foreach (Match m in HexStore.Matches(line))
            {
                if (Il2CppConstantResolver.TryParseHex(m.Groups[1].Value, out var v))
                {
                    var ann = Il2CppConstantResolver.DecodeImmediate(v);
                    if (ann != null) notes.Add(ann);
                }
            }
            if (img != null)
            {
                var seen = new HashSet<ulong>();
                foreach (Match m in DatRef.Matches(line))
                {
                    if (!Il2CppConstantResolver.TryParseHex("0x" + m.Groups[1].Value, out var va)) continue;
                    if (!seen.Add(va)) continue;
                    var ann = img.ResolveData(va);
                    if (ann != null) notes.Add($"DAT_{m.Groups[1].Value}={ann}");
                }
            }
            var trimmed = line.TrimEnd();
            return notes.Count > 0 ? trimmed + "  /* " + string.Join(", ", notes) + " */" : trimmed;
        }

        /// <summary>
        /// Cleans a block of Ghidra pseudocode. <paramref name="strip"/> removes structural noise;
        /// <paramref name="floats"/> annotates constants (<paramref name="img"/> also resolves DAT_ loads).
        /// </summary>
        public static string Clean(string text, bool strip = true, bool floats = true, BinaryImage img = null)
        {
            var lines = text.Replace("\r", "").Split('\n');
            var outLines = new List<string>(lines.Length);
            foreach (var raw in lines)
            {
                if (strip && StructRe.IsMatch(raw)) continue;
                outLines.Add(floats ? AnnotateFloats(raw, img) : raw.TrimEnd());
            }
            // Collapse runs of blank lines left behind by stripping.
            var sb = new StringBuilder();
            var prevBlank = false;
            foreach (var l in outLines)
            {
                var blank = l.Length == 0;
                if (blank && prevBlank) continue;
                sb.Append(l).Append('\n');
                prevBlank = blank;
            }
            return sb.ToString().TrimEnd('\n');
        }
    }
}
