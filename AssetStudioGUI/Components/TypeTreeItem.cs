using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using AssetStudio;

namespace AssetStudioGUI
{
    internal class TypeTreeItem : ListViewItem
    {
        private TypeTree m_Type;

        public TypeTreeItem(int typeID, TypeTree m_Type)
        {
            this.m_Type = m_Type;
            Text = m_Type.m_Nodes[0].m_Type + " " + m_Type.m_Nodes[0].m_Name;
            SubItems.Add(typeID.ToString());
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            for (var i = 0; i < m_Type.m_Nodes.Count; i++)
            {
                var node = m_Type.m_Nodes[i];
                if (node.m_Type == "string" && m_Type.m_Nodes[i + 1].m_Type != "Array")
                    node.m_Type = "CustomType";

                sb.AppendFormat("{0}{1} {2} {3} {4}\r\n", 
                    new string('\t', node.m_Level), node.m_Type, node.m_Name, node.m_ByteSize, (node.m_MetaFlag & 0x4000) != 0);
            }
            return sb.ToString();
        }
    }
}
