using UnityRift;

namespace UnityRiftCLI
{
    internal class AssetItem
    {
        private readonly Object _asset;

        // The fully parsed object. Heavy types are LazyObject placeholders after load and
        // are parsed here on first use (export, dump).
        public Object Asset => _asset.Resolve();
        // The object as it sits in the file's object list (possibly a placeholder). Use
        // this for identity / type checks so they never trigger a parse.
        public Object RawAsset => _asset;

        public SerializedFile SourceFile;
        public string Container = string.Empty;
        public string TypeString;
        public long m_PathID;
        public long FullSize;
        public ClassIDType Type;
        public string Text;
        public string UniqueID;
        public GameObjectNode Node;

        public AssetItem(Object asset)
        {
            _asset = asset;
            SourceFile = asset.assetsFile;
            Type = asset.type;
            TypeString = Type.ToString();
            m_PathID = asset.m_PathID;
            FullSize = asset.byteSize;
        }
    }
}
