using System.Collections.Generic;

namespace AssetStudio
{
    public sealed class Texture2DArray : Texture
    {
        public int m_Width;
        public int m_Height;
        public int m_Depth;
        public GraphicsFormat m_Format;
        public int m_MipCount;
        public uint m_DataSize;
        public GLTextureSettings m_TextureSettings;
        public int m_ColorSpace;
        public ResourceReader image_data;
        public StreamingInfo m_StreamData;
        public List<Texture2D> TextureList;

        public Texture2DArray(ObjectReader reader) : base(reader)
        {
            m_ColorSpace = reader.ReadInt32();
            m_Format = (GraphicsFormat)reader.ReadInt32();
            m_Width = reader.ReadInt32();
            m_Height = reader.ReadInt32();
            m_Depth = reader.ReadInt32();
            m_MipCount = reader.ReadInt32();
            m_DataSize = reader.ReadUInt32();
            m_TextureSettings = new GLTextureSettings(reader);
            if (version[0] > 2020 || (version[0] == 2020 && version[1] >= 2)) //2020.2 and up
            {
                var m_UsageMode = reader.ReadInt32();
            }
            var m_IsReadable = reader.ReadBoolean();
            reader.AlignStream(); 

            var image_data_size = reader.ReadInt32();
            if (image_data_size == 0)
            {
                m_StreamData = new StreamingInfo(reader);
            }

            if (!string.IsNullOrEmpty(m_StreamData?.path))
            {
                image_data = new ResourceReader(m_StreamData.path, assetsFile, m_StreamData.offset, (int)m_StreamData.size);
            }
            else
            {
                image_data = new ResourceReader(reader, reader.BaseStream.Position, image_data_size);
            }

            TextureList = new List<Texture2D>();
        }
    }
}
