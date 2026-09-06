using System.Windows.Forms;
using UnityRift;

namespace UnityRiftGUI
{
    internal class GameObjectTreeNode : TreeNode
    {
        public GameObject gameObject;

        public GameObjectTreeNode(GameObject gameObject)
        {
            this.gameObject = gameObject;
            Text = gameObject.m_Name;
        }
    }
}
