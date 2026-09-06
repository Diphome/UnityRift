using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnityRift
{
    public static class TypeTreeHelper
    {
        private static readonly JsonSerializerOptions JsonOptions;
        static TypeTreeHelper()
        {
            JsonOptions = new JsonSerializerOptions
            {
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
                ReferenceHandler = ReferenceHandler.IgnoreCycles,
                IncludeFields = true,
            };
        }

        public static string ReadTypeString(TypeTree m_Type, ObjectReader reader)
        {
            reader.Reset();
            var readed = 0L;
            var sb = new StringBuilder();
            var m_Nodes = m_Type.m_Nodes;
            try
            {
                for (var i = 0; i < m_Nodes.Count; i++)
                {
                    ReadStringValue(sb, m_Nodes, reader, ref i);
                    readed = reader.Position - reader.byteStart;
                }
            }
            catch (Exception)
            {
                //Ignore
            }
            if (readed != reader.byteSize)
            {
                Logger.Info($"Failed to read type, read {readed} bytes but expected {reader.byteSize} bytes");
            }

            return sb.ToString();
        }

        private const char ReplacementChar = '�';

        /// <summary>
        /// Reads a <c>char</c> type-tree field. A Unity <c>char</c> is a C++ char and is normally
        /// 1 byte; the code historically read a fixed 2 bytes (<see cref="BitConverter.ToChar(byte[], int)"/>),
        /// which over-reads and misaligns 1-byte <c>vector&lt;char&gt;</c> fields such as
        /// <c>Font.m_FontData</c> — drifting past the end of the object. The element's declared
        /// <paramref name="byteSize"/> is honored instead (2 only when the type tree says so).
        /// Throws <see cref="EndOfStreamException"/> when too few bytes remain so the tolerant
        /// callers abort the walk instead of reading garbage. Binary data can land in the UTF-16
        /// surrogate range; a lone surrogate is not a valid standalone character and would corrupt
        /// UTF-8 output (dump .txt files, JSON export), so it is mapped to U+FFFD.
        /// </summary>
        private static char ReadCharValue(BinaryReader reader, int byteSize)
        {
            var size = byteSize == 2 ? 2 : 1;
            var bytes = reader.ReadBytes(size);
            if (bytes.Length < size)
                throw new EndOfStreamException();
            var code = size == 2 ? (ushort)(bytes[0] | (bytes[1] << 8)) : bytes[0]; // little-endian
            return code >= 0xD800 && code <= 0xDFFF ? ReplacementChar : (char)code;
        }

        private static void ReadStringValue(StringBuilder sb, List<TypeTreeNode> m_Nodes, BinaryReader reader, ref int i)
        {
            var m_Node = m_Nodes[i];
            var level = m_Node.m_Level;
            var varTypeStr = m_Node.m_Type;
            var varNameStr = m_Node.m_Name;
            object value = null;
            var append = true;
            var align = (m_Node.m_MetaFlag & 0x4000) != 0;
            switch (varTypeStr)
            {
                case "SInt8":
                    value = reader.ReadSByte();
                    break;
                case "UInt8":
                    value = reader.ReadByte();
                    break;
                case "char":
                    value = ReadCharValue(reader, m_Node.m_ByteSize);
                    break;
                case "short":
                case "SInt16":
                    value = reader.ReadInt16();
                    break;
                case "UInt16":
                case "unsigned short":
                    value = reader.ReadUInt16();
                    break;
                case "int":
                case "SInt32":
                    value = reader.ReadInt32();
                    break;
                case "UInt32":
                case "unsigned int":
                case "Type*":
                    value = reader.ReadUInt32();
                    break;
                case "long long":
                case "SInt64":
                    value = reader.ReadInt64();
                    break;
                case "UInt64":
                case "unsigned long long":
                case "FileSize":
                    value = reader.ReadUInt64();
                    break;
                case "float":
                    value = reader.ReadSingle();
                    break;
                case "double":
                    value = reader.ReadDouble();
                    break;
                case "bool":
                    value = reader.ReadBoolean();
                    break;
                case "string" when m_Nodes[i + 1].m_Type == "Array":
                    append = false;
                    var str = reader.ReadAlignedString();
                    sb.AppendFormat("{0}{1} {2} = \"{3}\"\r\n", (new string('\t', level)), varTypeStr, varNameStr, str);
                    var toSkip = GetNodes(m_Nodes, i);
                    i += toSkip.Count - 1;
                    break;
                case "map":
                    {
                        if ((m_Nodes[i + 1].m_MetaFlag & 0x4000) != 0)
                            align = true;
                        append = false;
                        sb.AppendFormat("{0}{1} {2}\r\n", (new string('\t', level)), varTypeStr, varNameStr);
                        sb.AppendFormat("{0}{1} {2}\r\n", (new string('\t', level + 1)), "Array", "Array");
                        var size = reader.ReadInt32();
                        CheckArraySize(reader, size, 2);
                        sb.AppendFormat("{0}{1} {2} = {3}\r\n", (new string('\t', level + 1)), "int", "size", size);
                        var map = GetNodes(m_Nodes, i);
                        i += map.Count - 1;
                        var first = GetNodes(map, 4);
                        var next = 4 + first.Count;
                        var second = GetNodes(map, next);
                        for (int j = 0; j < size; j++)
                        {
                            sb.AppendFormat("{0}[{1}]\r\n", (new string('\t', level + 2)), j);
                            sb.AppendFormat("{0}{1} {2}\r\n", (new string('\t', level + 2)), "pair", "data");
                            int tmp1 = 0;
                            int tmp2 = 0;
                            ReadStringValue(sb, first, reader, ref tmp1);
                            ReadStringValue(sb, second, reader, ref tmp2);
                        }
                        break;
                    }
                case "TypelessData":
                    {
                        append = false;
                        var size = reader.ReadInt32();
                        CheckArraySize(reader, size, 1);
                        reader.BaseStream.Position += size;
                        i += 2;
                        sb.AppendFormat("{0}{1} {2}\r\n", (new string('\t', level)), varTypeStr, varNameStr);
                        sb.AppendFormat("{0}{1} {2} = {3}\r\n", (new string('\t', level)), "int", "size", size);
                        break;
                    }
                default:
                    {
                        if (i < m_Nodes.Count - 1 && m_Nodes[i + 1].m_Type == "Array") //Array
                        {
                            if ((m_Nodes[i + 1].m_MetaFlag & 0x4000) != 0)
                                align = true;
                            append = false;
                            sb.AppendFormat("{0}{1} {2}\r\n", (new string('\t', level)), varTypeStr, varNameStr);
                            sb.AppendFormat("{0}{1} {2}\r\n", (new string('\t', level + 1)), "Array", "Array");
                            var size = reader.ReadInt32();
                            var vector = GetNodes(m_Nodes, i);
                            CheckArraySize(reader, size, ElementMinSize(vector, 3));
                            sb.AppendFormat("{0}{1} {2} = {3}\r\n", (new string('\t', level + 1)), "int", "size", size);
                            i += vector.Count - 1;
                            for (int j = 0; j < size; j++)
                            {
                                sb.AppendFormat("{0}[{1}]\r\n", (new string('\t', level + 2)), j);
                                int tmp = 3;
                                ReadStringValue(sb, vector, reader, ref tmp);
                            }
                            break;
                        }
                        else //Class
                        {
                            if (m_Node.m_Type == "string")
                                m_Node.m_Type = "CustomType";
                            append = false;
                            sb.AppendFormat("{0}{1} {2}\r\n", (new string('\t', level)), varTypeStr, varNameStr);
                            var @class = GetNodes(m_Nodes, i);
                            i += @class.Count - 1;
                            for (int j = 1; j < @class.Count; j++)
                            {
                                ReadStringValue(sb, @class, reader, ref j);
                            }
                            break;
                        }
                    }
            }
            if (append)
                sb.AppendFormat("{0}{1} {2} = {3}\r\n", (new string('\t', level)), varTypeStr, varNameStr, value);
            if (align)
                reader.AlignStream();
        }

        private static readonly JsonWriterOptions DirectWriterOptions = new JsonWriterOptions { SkipValidation = true };

        /// <summary>
        /// Reads an object through its type tree and returns it as UTF-8 JSON.
        /// The fast path streams the binary data straight into a <see cref="Utf8JsonWriter"/>
        /// (no boxed intermediate tree, no reflection-based serialization). If the data
        /// cannot be read to the end (truncated/mismatched object), it falls back to the
        /// tolerant <see cref="ReadType"/> path, which keeps whatever was readable.
        /// </summary>
        public static byte[] ReadTypeByteArray(TypeTree m_Types, ObjectReader reader)
        {
            var m_Nodes = m_Types.m_Nodes;
            reader.Reset();
            var initialCapacity = (int)Math.Min(Math.Max(reader.byteSize * 3L, 512), 4 * 1024 * 1024);
            var buffer = new MemoryStream(initialCapacity);
            try
            {
                using (var writer = new Utf8JsonWriter(buffer, DirectWriterOptions))
                {
                    writer.WriteStartObject();
                    for (var i = 1; i < m_Nodes.Count; i++)
                    {
                        writer.WritePropertyName(m_Nodes[i].m_Name);
                        WriteValue(writer, m_Nodes, reader, ref i);
                    }
                    writer.WriteEndObject();
                }
                var readed = reader.Position - reader.byteStart;
                if (readed != reader.byteSize)
                {
                    Logger.Info($"Failed to read type, read {readed} bytes but expected {reader.byteSize} bytes");
                }
                return buffer.ToArray();
            }
            catch (Exception)
            {
                // Partial/garbage object: use the tolerant reader (same behaviour as before).
                var type = ReadType(m_Types, reader);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(type, JsonOptions);
                type.Clear();
                return bytes;
            }
        }

        private static void WriteFloat(Utf8JsonWriter writer, float value)
        {
            if (!float.IsNaN(value) && !float.IsInfinity(value))
                writer.WriteNumberValue(value);
            else
                writer.WriteStringValue(float.IsNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity");
        }

        private static void WriteDouble(Utf8JsonWriter writer, double value)
        {
            if (!double.IsNaN(value) && !double.IsInfinity(value))
                writer.WriteNumberValue(value);
            else
                writer.WriteStringValue(double.IsNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity");
        }

        private static void WriteValue(Utf8JsonWriter writer, List<TypeTreeNode> m_Nodes, BinaryReader reader, ref int i)
        {
            var m_Node = m_Nodes[i];
            var varTypeStr = m_Node.m_Type;
            var align = (m_Node.m_MetaFlag & 0x4000) != 0;
            switch (varTypeStr)
            {
                case "SInt8":
                    writer.WriteNumberValue(reader.ReadSByte());
                    break;
                case "UInt8":
                    writer.WriteNumberValue(reader.ReadByte());
                    break;
                case "char":
                    writer.WriteStringValue(ReadCharValue(reader, m_Node.m_ByteSize).ToString());
                    break;
                case "short":
                case "SInt16":
                    writer.WriteNumberValue(reader.ReadInt16());
                    break;
                case "UInt16":
                case "unsigned short":
                    writer.WriteNumberValue(reader.ReadUInt16());
                    break;
                case "int":
                case "SInt32":
                    writer.WriteNumberValue(reader.ReadInt32());
                    break;
                case "UInt32":
                case "unsigned int":
                case "Type*":
                    writer.WriteNumberValue(reader.ReadUInt32());
                    break;
                case "long long":
                case "SInt64":
                    writer.WriteNumberValue(reader.ReadInt64());
                    break;
                case "UInt64":
                case "unsigned long long":
                case "FileSize":
                    writer.WriteNumberValue(reader.ReadUInt64());
                    break;
                case "float":
                    WriteFloat(writer, reader.ReadSingle());
                    break;
                case "double":
                    WriteDouble(writer, reader.ReadDouble());
                    break;
                case "bool":
                    writer.WriteBooleanValue(reader.ReadBoolean());
                    break;
                case "string" when m_Nodes[i + 1].m_Type == "Array":
                    writer.WriteStringValue(reader.ReadAlignedString());
                    i += CountNodes(m_Nodes, i) - 1;
                    break;
                case "map":
                    {
                        if ((m_Nodes[i + 1].m_MetaFlag & 0x4000) != 0)
                            align = true;
                        var map = GetNodes(m_Nodes, i);
                        i += map.Count - 1;
                        var first = GetNodes(map, 4);
                        var next = 4 + first.Count;
                        var second = GetNodes(map, next);
                        var size = reader.ReadInt32();
                        CheckArraySize(reader, size, 2);
                        writer.WriteStartArray();
                        for (int j = 0; j < size; j++)
                        {
                            int tmp1 = 0;
                            int tmp2 = 0;
                            writer.WriteStartObject();
                            writer.WritePropertyName("Key");
                            WriteValue(writer, first, reader, ref tmp1);
                            writer.WritePropertyName("Value");
                            WriteValue(writer, second, reader, ref tmp2);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                        break;
                    }
                case "TypelessData":
                    {
                        var size = reader.ReadInt32();
                        CheckArraySize(reader, size, 1);
                        var offset = size > 0 ? reader.BaseStream.Position : 0;
                        writer.WriteStartObject();
                        writer.WriteNumber("Offset", offset);
                        writer.WriteNumber("Size", size);
                        writer.WriteEndObject();
                        reader.BaseStream.Position += size;
                        i += 2;
                        break;
                    }
                default:
                    {
                        if (m_Node.m_Type == "string")
                            m_Node.m_Type = "CustomType";
                        if (i < m_Nodes.Count - 1 && m_Nodes[i + 1].m_Type == "Array") //Array
                        {
                            if ((m_Nodes[i + 1].m_MetaFlag & 0x4000) != 0)
                                align = true;
                            var vector = GetNodes(m_Nodes, i);
                            i += vector.Count - 1;
                            var size = reader.ReadInt32();
                            CheckArraySize(reader, size, ElementMinSize(vector, 3));
                            writer.WriteStartArray();
                            for (int j = 0; j < size; j++)
                            {
                                int tmp = 3;
                                WriteValue(writer, vector, reader, ref tmp);
                            }
                            writer.WriteEndArray();
                            break;
                        }
                        else //Class
                        {
                            var @class = GetNodes(m_Nodes, i);
                            i += @class.Count - 1;
                            writer.WriteStartObject();
                            for (int j = 1; j < @class.Count; j++)
                            {
                                writer.WritePropertyName(@class[j].m_Name);
                                WriteValue(writer, @class, reader, ref j);
                            }
                            writer.WriteEndObject();
                            break;
                        }
                    }
            }
            if (align)
                reader.AlignStream();
        }

        // Minimum serialized size of one array element: the element node's fixed size
        // when known, otherwise 1 byte. Used only to reject impossible element counts.
        private static int ElementMinSize(List<TypeTreeNode> vector, int elementIndex)
        {
            if (elementIndex < vector.Count)
            {
                var byteSize = vector[elementIndex].m_ByteSize;
                if (byteSize > 0)
                    return byteSize;
            }
            return 1;
        }

        // Rejects element counts that cannot fit in the remaining object data before
        // anything is allocated. Without this a mismatched type tree (wrong version /
        // stripped build) can read a garbage count and spend seconds allocating and
        // filling giant arrays before finally hitting the end of the stream.
        private static void CheckArraySize(BinaryReader reader, long size, long minElementBytes)
        {
            if (size < 0)
                throw new EndOfStreamException();
            var remaining = reader is ObjectReader objectReader
                ? objectReader.Remaining
                : reader.BaseStream.Length - reader.BaseStream.Position;
            if (size * Math.Max(minElementBytes, 1) > remaining)
                throw new EndOfStreamException();
        }

        public static OrderedDictionary ReadType(TypeTree m_Types, ObjectReader reader)
        {
            reader.Reset();
            var obj = new OrderedDictionary();
            var m_Nodes = m_Types.m_Nodes;
            var readed = 0L;
            try
            {
                for (int i = 1; i < m_Nodes.Count; i++)
                {
                    var m_Node = m_Nodes[i];
                    var varNameStr = m_Node.m_Name;
                    obj[varNameStr] = ReadValue(m_Nodes, reader, ref i);
                    readed = reader.Position - reader.byteStart;
                }
            }
            catch (Exception)
            {
                //Ignore
            }
            if (readed != reader.byteSize)
            {
                Logger.Info($"Failed to read type, read {readed} bytes but expected {reader.byteSize} bytes");
            }
            return obj;
        }

        private static object ReadValue(List<TypeTreeNode> m_Nodes, BinaryReader reader, ref int i)
        {
            var m_Node = m_Nodes[i];
            var varTypeStr = m_Node.m_Type;
            object value;
            var align = (m_Node.m_MetaFlag & 0x4000) != 0;
            switch (varTypeStr)
            {
                case "SInt8":
                    value = reader.ReadSByte();
                    break;
                case "UInt8":
                    value = reader.ReadByte();
                    break;
                case "char":
                    value = ReadCharValue(reader, m_Node.m_ByteSize);
                    break;
                case "short":
                case "SInt16":
                    value = reader.ReadInt16();
                    break;
                case "UInt16":
                case "unsigned short":
                    value = reader.ReadUInt16();
                    break;
                case "int":
                case "SInt32":
                    value = reader.ReadInt32();
                    break;
                case "UInt32":
                case "unsigned int":
                case "Type*":
                    value = reader.ReadUInt32();
                    break;
                case "long long":
                case "SInt64":
                    value = reader.ReadInt64();
                    break;
                case "UInt64":
                case "unsigned long long":
                case "FileSize":
                    value = reader.ReadUInt64();
                    break;
                case "float":
                    value = reader.ReadSingle();
                    break;
                case "double":
                    value = reader.ReadDouble();
                    break;
                case "bool":
                    value = reader.ReadBoolean();
                    break;
                case "string" when m_Nodes[i + 1].m_Type == "Array":
                    value = reader.ReadAlignedString();
                    var toSkip = GetNodes(m_Nodes, i);
                    i += toSkip.Count - 1;
                    break;
                case "map":
                    {
                        if ((m_Nodes[i + 1].m_MetaFlag & 0x4000) != 0)
                            align = true;
                        var map = GetNodes(m_Nodes, i);
                        i += map.Count - 1;
                        var first = GetNodes(map, 4);
                        var next = 4 + first.Count;
                        var second = GetNodes(map, next);
                        var size = reader.ReadInt32();
                        CheckArraySize(reader, size, 2);
                        var dic = new List<KeyValuePair<object, object>>(size);
                        for (int j = 0; j < size; j++)
                        {
                            int tmp1 = 0;
                            int tmp2 = 0;
                            dic.Add(new KeyValuePair<object, object>(ReadValue(first, reader, ref tmp1), ReadValue(second, reader, ref tmp2)));
                        }
                        value = dic;
                        break;
                    }
                case "TypelessData":
                    {
                        var size = reader.ReadInt32();
                        CheckArraySize(reader, size, 1);
                        var offset = size > 0 ? reader.BaseStream.Position : 0;
                        var dic = new OrderedDictionary
                        {
                            {"Offset", offset},
                            {"Size", size}
                        };
                        value = dic;
                        reader.BaseStream.Position += size;
                        i += 2;
                        break;
                    }
                default:
                    {
                        if (m_Node.m_Type == "string")
                            m_Node.m_Type = "CustomType";
                        if (i < m_Nodes.Count - 1 && m_Nodes[i + 1].m_Type == "Array") //Array
                        {
                            if ((m_Nodes[i + 1].m_MetaFlag & 0x4000) != 0)
                                align = true;
                            var vector = GetNodes(m_Nodes, i);
                            i += vector.Count - 1;
                            var size = reader.ReadInt32();
                            CheckArraySize(reader, size, ElementMinSize(vector, 3));
                            var array = new object[size];
                            for (int j = 0; j < size; j++)
                            {
                                int tmp = 3;
                                array[j] = ReadValue(vector, reader, ref tmp);
                            }
                            value = array;
                            break;
                        }
                        else //Class
                        {
                            var @class = GetNodes(m_Nodes, i);
                            i += @class.Count - 1;
                            var obj = new OrderedDictionary();
                            for (int j = 1; j < @class.Count; j++)
                            {
                                var classmember = @class[j];
                                var name = classmember.m_Name;
                                obj[name] = ReadValue(@class, reader, ref j);
                            }
                            value = obj;
                            break;
                        }
                    }
            }
            if (align)
                reader.AlignStream();
            return value;
        }

        private static int CountNodes(List<TypeTreeNode> m_Nodes, int index)
        {
            var level = m_Nodes[index].m_Level;
            var count = 1;
            for (int i = index + 1; i < m_Nodes.Count; i++)
            {
                if (m_Nodes[i].m_Level <= level)
                    break;
                count++;
            }
            return count;
        }

        private static List<TypeTreeNode> GetNodes(List<TypeTreeNode> m_Nodes, int index)
        {
            var nodes = new List<TypeTreeNode>();
            nodes.Add(m_Nodes[index]);
            var level = m_Nodes[index].m_Level;
            for (int i = index + 1; i < m_Nodes.Count; i++)
            {
                var member = m_Nodes[i];
                var level2 = member.m_Level;
                if (level2 <= level)
                {
                    return nodes;
                }
                nodes.Add(member);
            }
            return nodes;
        }
    }
}
