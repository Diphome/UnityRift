using System.Windows.Forms;
using UnityRift;

namespace UnityRiftGUI
{
    internal class AssetItem : ListViewItem
    {
        private readonly Object _asset;
        private string _pathIdText;

        // The fully parsed object. Heavy types are LazyObject placeholders after load and
        // are parsed here on first use (preview, export, dump).
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
        public string InfoText;
        public string UniqueID;
        public GameObjectTreeNode TreeNode;
        // Lazily-built, lowercased text content used by the "Include (+ content)"
        // search mode (TextAsset script / MonoBehaviour dump). Built on first use.
        public string SearchContentCache;
        public bool SearchContentBuilt;

        public AssetItem(Object asset)
        {
            _asset = asset;
            SourceFile = asset.assetsFile;
            Type = asset.type;
            TypeString = Type.ToString();
            m_PathID = asset.m_PathID;
            FullSize = asset.byteSize;
        }

        public string PathIdText => _pathIdText ??= m_PathID.ToString();

        // Column cells are created the first time the row is displayed (virtual-mode
        // RetrieveVirtualItem), not for every asset at load: that was four strings per
        // asset up front on projects with hundreds of thousands of them.
        public void EnsureSubItems()
        {
            if (SubItems.Count > 1)
                return;
            SubItems.AddRange(new[]
            {
                Container,          // Container
                TypeString,         // Type
                PathIdText,         // PathID
                FullSize.ToString(),// Size
            });
        }

        // Text of a column without touching SubItems (sorting/filtering over all rows).
        public string ColumnText(int column)
        {
            switch (column)
            {
                case 0: return Text;
                case 1: return Container;
                case 2: return TypeString;
                case 3: return PathIdText;
                case 4: return FullSize.ToString();
                default: return string.Empty;
            }
        }
    }
}
