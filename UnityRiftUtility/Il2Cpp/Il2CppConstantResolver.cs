using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace UnityRift
{
    /// <summary>
    /// Recovers the float/double/int constants that a Ghidra IL2CPP decompilation hides as raw
    /// hex, so the actual game-logic numbers become readable. Two independent jobs:
    ///
    ///   1. Packed immediate decoding (no binary needed). ARM64/x64 materialise float/double field
    ///      initialisers as 32/64-bit immediate stores that Ghidra renders as a hex literal
    ///      (<c>*(undefined8 *)(x + 0x24) = 0x3f19999a3e99999a;</c>). A 16-hex-digit value is two
    ///      little-endian 32-bit floats packed into one 64-bit store (low word = lower address).
    ///
    ///   2. <c>DAT_&lt;va&gt;</c> literal-pool resolution (reads the binary). Constants that don't
    ///      fit an immediate are loaded from a read-only literal pool; the hex in the symbol IS the
    ///      virtual address. <see cref="BinaryImage"/> maps VA → file offset for PE and ELF.
    ///
    /// Framework-agnostic (no Cpp2IL/LibCpp2IL dependency): usable from any .NET target and alongside
    /// a live Ghidra session. Mirrors the standalone il2cpp_floats.py helper.
    /// </summary>
    public static class Il2CppConstantResolver
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // A 32-bit pattern is only shown as a float if the IEEE-754 value is "sane" (not a denormal,
        // not absurdly large) so ordinary ints/enums are not mislabelled as floats.
        private static bool PlausibleFloat(float f)
        {
            if (f == 0f) return true;
            if (float.IsNaN(f) || float.IsInfinity(f)) return false;
            var a = Math.Abs(f);
            return a >= 1e-4 && a <= 1e9;
        }

        private static bool PlausibleDouble(double d)
        {
            if (d == 0d) return true;
            if (double.IsNaN(d) || double.IsInfinity(d)) return false;
            var a = Math.Abs(d);
            return a >= 1e-6 && a <= 1e12;
        }

        private static float F32(uint u) => BitConverter.ToSingle(BitConverter.GetBytes(u), 0);
        private static double F64(ulong u) => BitConverter.ToDouble(BitConverter.GetBytes(u), 0);

        /// <summary>Tidy number formatting: 0.6000000238 → "0.6", 1.0 → "1.0".</summary>
        private static string Fmt(double f)
        {
            var r = Math.Round(f, 6);
            if (r == Math.Truncate(r) && Math.Abs(r) < 1e15)
                return ((long)r).ToString(Inv) + ".0";
            return r.ToString("0.######", Inv);
        }

        /// <summary>
        /// Decodes the int value of a <c>= 0x...;</c> store into a short annotation such as
        /// "(0.3f, 0.6f)", "1.0f" or "(20, 100)", or null if nothing plausible.
        /// </summary>
        public static string DecodeImmediate(ulong hexval)
        {
            if (hexval > 0xFFFFFFFF)
            {
                var lo = (uint)(hexval & 0xFFFFFFFF);
                var hi = (uint)((hexval >> 32) & 0xFFFFFFFF);
                var flo = F32(lo);
                var fhi = F32(hi);
                // Two packed 32-bit floats (low word = lower address, shown first).
                if (PlausibleFloat(flo) && PlausibleFloat(fhi) && !(lo == 0 && hi == 0))
                    return $"({Fmt(flo)}f, {Fmt(fhi)}f)";
                // Or a single 64-bit double.
                var d = F64(hexval);
                if (PlausibleDouble(d) && hexval != 0)
                    return Fmt(d);
                // Or two small packed 32-bit ints (e.g. 0x6400000014 -> (20, 100)).
                if (lo > 0 && lo < 0x100000 && hi > 0 && hi < 0x100000)
                    return $"({lo}, {hi})";
                return null;
            }
            else
            {
                var f = F32((uint)hexval);
                if (PlausibleFloat(f) && hexval != 0)
                    return $"{Fmt(f)}f";
                return null;
            }
        }

        /// <summary>Parses "0x1234" / "1234" (hex) into a ulong; returns false on garbage.</summary>
        public static bool TryParseHex(string s, out ulong value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return ulong.TryParse(s, NumberStyles.HexNumber, Inv, out value);
        }
    }

    /// <summary>
    /// Minimal PE/ELF (32/64-bit) virtual-address → file-offset mapper that reads raw bytes, used to
    /// resolve <c>DAT_&lt;va&gt;</c> literal-pool loads to their constant value. No external dependency.
    /// </summary>
    public sealed class BinaryImage
    {
        private readonly byte[] _data;
        private readonly List<(ulong va, ulong size, long off)> _segs = new List<(ulong, ulong, long)>();

        public bool Ok => _segs.Count > 0;

        private BinaryImage(byte[] data) { _data = data; }

        /// <summary>Opens a GameAssembly.dll (PE) or libil2cpp.so (ELF); returns null if it can't be parsed.</summary>
        public static BinaryImage TryOpen(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var img = new BinaryImage(File.ReadAllBytes(path));
                if (img._data.Length < 0x40) return null;
                var magic = BitConverter.ToUInt16(img._data, 0);
                if (magic == 0x5A4D) img.ParsePe();
                else if (img._data[0] == 0x7F && img._data[1] == (byte)'E' && img._data[2] == (byte)'L' && img._data[3] == (byte)'F') img.ParseElf();
                else return null;
                return img.Ok ? img : null;
            }
            catch
            {
                return null;
            }
        }

        private void ParsePe()
        {
            var lfanew = BitConverter.ToInt32(_data, 0x3C);
            if (BitConverter.ToUInt32(_data, lfanew) != 0x00004550) return; // "PE\0\0"
            var coff = lfanew + 4;
            var numSections = BitConverter.ToUInt16(_data, coff + 2);
            var optSize = BitConverter.ToUInt16(_data, coff + 16);
            var optStart = coff + 20;
            var optMagic = BitConverter.ToUInt16(_data, optStart);
            ulong imageBase;
            if (optMagic == 0x20B) imageBase = BitConverter.ToUInt64(_data, optStart + 24);      // PE32+
            else if (optMagic == 0x10B) imageBase = BitConverter.ToUInt32(_data, optStart + 28); // PE32
            else return;
            var sec = optStart + optSize;
            for (var i = 0; i < numSections; i++)
            {
                var s = sec + i * 40;
                var virtualAddress = BitConverter.ToUInt32(_data, s + 12);
                var sizeOfRaw = BitConverter.ToUInt32(_data, s + 16);
                var ptrToRaw = BitConverter.ToUInt32(_data, s + 20);
                if (sizeOfRaw == 0) continue;
                _segs.Add((imageBase + virtualAddress, sizeOfRaw, ptrToRaw));
            }
        }

        private void ParseElf()
        {
            var is64 = _data[4] == 2;
            var little = _data[5] != 2;
            if (!little) return; // Unity binaries are little-endian
            ulong ePhoff;
            ushort ePhentsize, ePhnum;
            if (is64)
            {
                ePhoff = BitConverter.ToUInt64(_data, 0x20);
                ePhentsize = BitConverter.ToUInt16(_data, 0x36);
                ePhnum = BitConverter.ToUInt16(_data, 0x38);
            }
            else
            {
                ePhoff = BitConverter.ToUInt32(_data, 0x1C);
                ePhentsize = BitConverter.ToUInt16(_data, 0x2A);
                ePhnum = BitConverter.ToUInt16(_data, 0x2C);
            }
            const uint PT_LOAD = 1;
            for (var i = 0; i < ePhnum; i++)
            {
                var off = (long)ePhoff + i * ePhentsize;
                var pType = BitConverter.ToUInt32(_data, (int)off);
                if (pType != PT_LOAD) continue;
                ulong pOffset, pVaddr, pFilesz;
                if (is64)
                {
                    pOffset = BitConverter.ToUInt64(_data, (int)off + 0x08);
                    pVaddr = BitConverter.ToUInt64(_data, (int)off + 0x10);
                    pFilesz = BitConverter.ToUInt64(_data, (int)off + 0x20);
                }
                else
                {
                    pOffset = BitConverter.ToUInt32(_data, (int)off + 0x04);
                    pVaddr = BitConverter.ToUInt32(_data, (int)off + 0x08);
                    pFilesz = BitConverter.ToUInt32(_data, (int)off + 0x10);
                }
                if (pFilesz == 0) continue;
                _segs.Add((pVaddr, pFilesz, (long)pOffset));
            }
        }

        private long VaToOffset(ulong va)
        {
            foreach (var (segVa, size, off) in _segs)
                if (va >= segVa && va < segVa + size) return off + (long)(va - segVa);
            return -1;
        }

        /// <summary>Reads <paramref name="n"/> bytes at virtual address <paramref name="va"/>, or null if out of range.</summary>
        public byte[] Read(ulong va, int n)
        {
            var o = VaToOffset(va);
            if (o < 0 || o + n > _data.Length) return null;
            var buf = new byte[n];
            Array.Copy(_data, o, buf, 0, n);
            return buf;
        }

        /// <summary>Best-guess annotation for a DAT_&lt;va&gt; literal load: a plausible float, then double.</summary>
        public string ResolveData(ulong va)
        {
            var b = Read(va, 8);
            if (b == null)
            {
                b = Read(va, 4);
                if (b == null) return null;
            }
            var u32 = BitConverter.ToUInt32(b, 0);
            var ann = Il2CppConstantResolver.DecodeImmediate(u32);
            if (ann != null && u32 != 0) return ann;
            if (b.Length >= 8)
            {
                var u64 = BitConverter.ToUInt64(b, 0);
                var d = Il2CppConstantResolver.DecodeImmediate(u64);
                if (d != null && u64 != 0) return d;
            }
            return null;
        }
    }
}
