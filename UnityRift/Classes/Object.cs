using System.Collections.Specialized;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace UnityRift
{
    public class Object
    {
        [JsonIgnore]
        public SerializedFile assetsFile;
        [JsonIgnore]
        public ObjectReader reader;
        public long m_PathID;
        [JsonIgnore]
        public UnityVersion version;
        [JsonIgnore]
        public BuildTarget platform;
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ClassIDType type;
        [JsonIgnore]
        public SerializedType serializedType;
        public int classID;
        public uint byteSize;
        [JsonIgnore]
        public string Name;
        // Set on an object that was hydrated from a LazyObject placeholder: the placeholder
        // is the identity the object lists / dictionaries were built with.
        [JsonIgnore]
        public Object LazySource;
        private static readonly JsonSerializerOptions jsonOptions;

        // Returns the fully parsed object. Plain objects are already complete; a LazyObject
        // placeholder parses its payload on first call.
        public virtual Object Resolve() => this;

        static Object()
        {
            jsonOptions = new JsonSerializerOptions
            {
                Converters = { new JsonConverterHelper.FloatConverter() },
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
                ReferenceHandler = ReferenceHandler.IgnoreCycles,
                PropertyNameCaseInsensitive = true,
                IncludeFields = true,
                WriteIndented = true,
            };
        }

        public Object() { }

        public Object(ObjectReader reader)
        {
            this.reader = reader;
            reader.Reset();
            assetsFile = reader.assetsFile;
            type = reader.type;
            m_PathID = reader.m_PathID;
            version = reader.version;
            platform = reader.platform;
            serializedType = reader.serializedType;
            classID = reader.classID;
            byteSize = reader.byteSize;

            if (platform == BuildTarget.NoTarget)
            {
                var m_ObjectHideFlags = reader.ReadUInt32();
            }
        }

        public string DumpObject()
        {
            string str = null;
            try
            {
                if (this is Mesh m_Mesh)
                {
                    m_Mesh.ProcessData();
                }

                str = JsonSerializer.Deserialize<JsonObject>(JsonSerializer.SerializeToUtf8Bytes(this, GetType(), jsonOptions))
                    .ToJsonString(jsonOptions).Replace("  ", "    ");
            }
            catch
            {
                //ignore
            }

            return str;
        }

        public string Dump(TypeTree m_Type = null)
        {
            m_Type = m_Type ?? serializedType?.m_Type;
            if (m_Type == null)
                return null;

            return TypeTreeHelper.ReadTypeString(m_Type, reader);
        }

        public OrderedDictionary ToType(TypeTree m_Type = null)
        {
            m_Type = m_Type ?? serializedType?.m_Type;
            if (m_Type == null)
                return null;

            return TypeTreeHelper.ReadType(m_Type, reader);
        }

        public JsonDocument ToJsonDoc(TypeTree m_Type = null)
        {
            var typeDict = ToType(m_Type);
            try
            {
                if (typeDict != null)
                {
                    return JsonSerializer.SerializeToDocument(typeDict, jsonOptions);
                }

                if (this is Mesh m_Mesh)
                {
                    m_Mesh.ProcessData();
                }

                return JsonSerializer.SerializeToDocument(this, GetType(), jsonOptions);
            }
            catch
            {
                //ignore
            }

            return null;
        }

        public byte[] GetRawData()
        {
            reader.Reset();
            return reader.ReadBytes((int)byteSize);
        }
    }
}
