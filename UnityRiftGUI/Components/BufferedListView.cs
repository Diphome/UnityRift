using System.Windows.Forms;

namespace UnityRiftGUI
{
    // A ListView that paints through an off-screen buffer. The stock control paints straight
    // to the screen, and with OwnerDraw on every scroll step repaints every visible cell
    // unbuffered, which shows as tearing/flicker. Setting DoubleBuffered on a ListView makes
    // WinForms apply the native LVS_EX_DOUBLEBUFFER extended style, which is the proper fix
    // (it also gives smooth marquee selection). Created as a subclass so the Designer keeps
    // owning the layout/columns and nothing else changes.
    public class BufferedListView : ListView
    {
        public BufferedListView()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        }
    }
}
