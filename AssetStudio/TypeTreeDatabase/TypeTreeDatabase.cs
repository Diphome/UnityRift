using System;
using System.Collections.Generic;
using System.IO;
using K4os.Compression.LZ4;

namespace AssetStudio
{
    // Reads a "TPK*" class-database file (the type-tree package format used by
    // UABE/AssetsTools.NET) and produces AssetStudio TypeTrees on demand, keyed by
    // (Unity version, classID). This lets objects from type-tree-STRIPPED builds be
    // deserialized/dumped as if the tree were embedded.
    //
    // Self-contained: uses only System.IO + K4os LZ4 (already referenced by this
    // project), so it works on net472 and .NET alike. Format ported from the MIT
    // AssetsTools.NET ClassPackage reader.
    public sealed class TypeTreeDatabase
    {
        private sealed class TpkNode
        {
            public ushort TypeName;
            public ushort FieldName;
            public int ByteSize;
            public ushort Version;
            public byte TypeFlags;
            public uint MetaFlag;
            public ushort[] SubNodes;
        }

        private sealed class TpkClassType
        {
            public ushort Name;
            public ushort BaseName;
            public byte Flags;
            public ushort EditorRootNode = ushort.MaxValue;
            public ushort ReleaseRootNode = ushort.MaxValue;
        }

        private const byte FlagHasEditorRootNode = 64;
        private const byte FlagHasReleaseRootNode = 128;

        private string[] _strings;
        private TpkNode[] _nodes;
        // classID -> versions (ascending, packed) with their class type (type may be null)
        private readonly Dictionary<int, List<KeyValuePair<ulong, TpkClassType>>> _classes
            = new Dictionary<int, List<KeyValuePair<ulong, TpkClassType>>>();
        private readonly Dictionary<long, TypeTree> _cache = new Dictionary<long, TypeTree>();

        public bool IsLoaded { get; private set; }

        public static TypeTreeDatabase Load(string path)
        {
            using (var fs = File.OpenRead(path))
            {
                return LoadFromStream(fs);
            }
        }

        public static TypeTreeDatabase LoadFromBytes(byte[] data)
        {
            using (var ms = new MemoryStream(data))
            {
                return LoadFromStream(ms);
            }
        }

        public static TypeTreeDatabase LoadFromStream(Stream stream)
        {
            var db = new TypeTreeDatabase();
            db.Read(stream);
            return db;
        }

        private void Read(Stream stream)
        {
            byte[] payload;
            using (var headerReader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                var magic = new string(headerReader.ReadChars(4));
                if (magic != "TPK*")
                    throw new NotSupportedException($"Not a TPK* class database (magic \"{magic}\").");

                byte fileVersion = headerReader.ReadByte();
                if (fileVersion > 2)
                    throw new NotSupportedException($"Unsupported TPK file version {fileVersion}.");
                byte compression = headerReader.ReadByte();
                headerReader.ReadByte(); // dataType
                headerReader.ReadByte(); // reserved
                headerReader.ReadUInt32(); // reserved
                uint compressedSize = headerReader.ReadUInt32();
                uint decompressedSize = headerReader.ReadUInt32();

                var compressed = headerReader.ReadBytes((int)compressedSize);
                switch (compression)
                {
                    case 0: // uncompressed
                        payload = compressed;
                        break;
                    case 1: // LZ4 block
                        payload = new byte[decompressedSize];
                        LZ4Codec.Decode(compressed, 0, compressed.Length, payload, 0, payload.Length);
                        break;
                    default:
                        throw new NotSupportedException($"Unsupported TPK compression type {compression}.");
                }

                ReadBlob(payload, fileVersion);
            }
            IsLoaded = true;
        }

        private void ReadBlob(byte[] payload, byte fileVersion)
        {
            using (var ms = new MemoryStream(payload))
            using (var r = new BinaryReader(ms))
            {
                r.ReadInt64(); // CreationTime

                int versionCount = r.ReadInt32();
                ms.Position += versionCount * sizeof(ulong); // Versions list (unused for lookup)

                int classCount = r.ReadInt32();
                for (int i = 0; i < classCount; i++)
                {
                    int classId = r.ReadInt32();
                    int perClassCount = r.ReadInt32();
                    var list = new List<KeyValuePair<ulong, TpkClassType>>(perClassCount);
                    for (int j = 0; j < perClassCount; j++)
                    {
                        ulong version = r.ReadUInt64();
                        bool hasClassData = r.ReadBoolean();
                        TpkClassType type = null;
                        if (hasClassData)
                        {
                            type = new TpkClassType
                            {
                                Name = r.ReadUInt16(),
                                BaseName = r.ReadUInt16(),
                                Flags = r.ReadByte(),
                            };
                            if ((type.Flags & FlagHasEditorRootNode) != 0)
                                type.EditorRootNode = r.ReadUInt16();
                            if ((type.Flags & FlagHasReleaseRootNode) != 0)
                                type.ReleaseRootNode = r.ReadUInt16();
                        }
                        list.Add(new KeyValuePair<ulong, TpkClassType>(version, type));
                    }
                    _classes[classId] = list;
                }

                SkipCommonString(r, ms, fileVersion);

                int nodeCount = r.ReadInt32();
                _nodes = new TpkNode[nodeCount];
                for (int i = 0; i < nodeCount; i++)
                {
                    var node = new TpkNode
                    {
                        TypeName = r.ReadUInt16(),
                        FieldName = r.ReadUInt16(),
                        ByteSize = r.ReadInt32(),
                        Version = r.ReadUInt16(),
                        TypeFlags = r.ReadByte(),
                        MetaFlag = r.ReadUInt32(),
                    };
                    int subCount = r.ReadUInt16();
                    node.SubNodes = new ushort[subCount];
                    for (int s = 0; s < subCount; s++)
                        node.SubNodes[s] = r.ReadUInt16();
                    _nodes[i] = node;
                }

                int stringCount = r.ReadInt32();
                _strings = new string[stringCount];
                for (int i = 0; i < stringCount; i++)
                    _strings[i] = r.ReadString();
            }
        }

        // Advance past the CommonString section (we don't need it to build type trees).
        private static void SkipCommonString(BinaryReader r, MemoryStream ms, byte fileVersion)
        {
            if (fileVersion >= 2)
            {
                int versionCount = r.ReadInt32();
                for (int i = 0; i < versionCount; i++)
                {
                    ms.Position += sizeof(ulong);
                    int entryCount = r.ReadInt32();
                    ms.Position += 4 * entryCount; // each entry: 2x ushort
                }
            }
            else if (fileVersion == 1)
            {
                int versionCount = r.ReadInt32();
                ms.Position += (sizeof(ulong) + sizeof(byte)) * versionCount;
                int indicesCount = r.ReadInt32();
                ms.Position += sizeof(ushort) * indicesCount;
            }
        }

        public bool TryGetTypeTree(UnityVersion version, int classId, out TypeTree typeTree)
        {
            typeTree = null;
            if (!IsLoaded || version == null)
                return false;

            var packed = Pack(version);
            // Cache key: major/minor/patch + classID (build type/number omitted — trees
            // effectively never differ within a patch release).
            var cacheKey = unchecked(((long)version.Major << 40) | ((long)version.Minor << 32) | ((long)version.Patch << 20) | ((uint)classId & 0xFFFFF));
            lock (_cache)
            {
                if (_cache.TryGetValue(cacheKey, out typeTree))
                    return typeTree != null;

                var type = GetTypeForVersion(classId, packed);
                var root = SelectRoot(type);
                if (root == ushort.MaxValue)
                {
                    _cache[cacheKey] = null;
                    return false;
                }

                var nodes = new List<TypeTreeNode>();
                Flatten(root, 0, nodes);
                typeTree = new TypeTree { m_Nodes = nodes, m_StringBuffer = Array.Empty<byte>() };
                _cache[cacheKey] = typeTree;
                return true;
            }
        }

        private TpkClassType GetTypeForVersion(int classId, ulong packed)
        {
            if (!_classes.TryGetValue(classId, out var list) || list.Count == 0)
                return null;
            if (list[0].Key > packed)
                return null;
            var last = list[0].Value;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Key == packed)
                    return list[i].Value;
                if (list[i].Key > packed)
                    return last;
                last = list[i].Value;
            }
            return last;
        }

        private static ushort SelectRoot(TpkClassType type)
        {
            if (type == null)
                return ushort.MaxValue;
            if (type.ReleaseRootNode != ushort.MaxValue)
                return type.ReleaseRootNode;
            return type.EditorRootNode;
        }

        private void Flatten(ushort nodeIndex, int level, List<TypeTreeNode> output)
        {
            var n = _nodes[nodeIndex];
            output.Add(new TypeTreeNode
            {
                m_Type = _strings[n.TypeName],
                m_Name = _strings[n.FieldName],
                m_ByteSize = n.ByteSize,
                m_Index = output.Count,
                m_TypeFlags = n.TypeFlags,
                m_Version = n.Version,
                m_MetaFlag = (int)n.MetaFlag,
                m_Level = level,
            });
            foreach (var sub in n.SubNodes)
                Flatten(sub, level + 1, output);
        }

        // Match AssetsTools.NET UnityVersion packing so version comparisons align.
        private static ulong Pack(UnityVersion v)
        {
            byte typeByte;
            switch (v.BuildType)
            {
                case "a": typeByte = 0; break;
                case "b": typeByte = 1; break;
                case "c": typeByte = 2; break;
                case "f": typeByte = 3; break;
                case "p": typeByte = 4; break;
                case "x": typeByte = 5; break;
                default: typeByte = 3; break; // treat unknown as final release
            }
            return ((ulong)(uint)v.Major << 48)
                 | ((ulong)(uint)v.Minor << 32)
                 | ((ulong)(uint)v.Patch << 16)
                 | ((ulong)typeByte << 8)
                 | (uint)v.Build;
        }
    }
}
