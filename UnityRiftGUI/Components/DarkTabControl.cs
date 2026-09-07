using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UnityRiftGUI
{
    // A TabControl that fully owns its painting when DarkMode is on, so the native
    // themed tab chrome (light borders/frame that survive owner-draw and don't adapt
    // to .NET's experimental dark mode on all Windows builds) never shows. Subclassing
    // the control (rather than hooking its handle) keeps the override reliably attached
    // across handle recreation.
    public class DarkTabControl : TabControl
    {
        private const int WM_PAINT = 0x000F;
        private const int WM_ERASEBKGND = 0x0014;

        private static readonly Color Accent = Color.FromArgb(0, 122, 204);    // active accent bar (VS blue)
        // Dark palette
        private static readonly Color DStrip = Color.FromArgb(37, 37, 38);
        private static readonly Color DTabSel = Color.FromArgb(60, 60, 62);   // active = raised/lighter
        private static readonly Color DTextSel = Color.FromArgb(248, 248, 248);
        private static readonly Color DTextNorm = Color.FromArgb(155, 155, 155);
        // Light palette
        private static readonly Color LStrip = Color.FromArgb(230, 230, 232);
        private static readonly Color LTabSel = Color.FromArgb(255, 255, 255);
        private static readonly Color LTextSel = Color.FromArgb(15, 15, 15);
        private static readonly Color LTextNorm = Color.FromArgb(105, 105, 105);

        // Plain field (not a property) to stay clear of the WinForms designer-serialization
        // analyzer; this control is created in code, never configured in the designer.
        public bool DarkMode;

        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public bool fErase;
            public int l, t, r, b;
            public bool fRestore;
            public bool fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] rgbReserved;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr BeginPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

        [DllImport("user32.dll")]
        private static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT ps);

        protected override void WndProc(ref Message m)
        {
            // Owner-paint in BOTH themes so the active tab is clearly marked either way.
            if (m.Msg == WM_ERASEBKGND)
            {
                m.Result = (IntPtr)1; // we paint the whole surface; skip erase to avoid flicker
                return;
            }
            if (m.Msg == WM_PAINT)
            {
                PaintTabs();
                return; // never call base -> native tab chrome is not drawn at all
            }
            base.WndProc(ref m);
        }

        private void PaintTabs()
        {
            var strip = DarkMode ? DStrip : LStrip;
            var tabSel = DarkMode ? DTabSel : LTabSel;
            var textSel = DarkMode ? DTextSel : LTextSel;
            var textNorm = DarkMode ? DTextNorm : LTextNorm;

            var ps = new PAINTSTRUCT();
            IntPtr hdc = BeginPaint(Handle, ref ps);
            try
            {
                using var g = Graphics.FromHdc(hdc);
                g.Clear(strip); // entire client: tab-strip band, frame, and page background
                using var boldFont = new Font(Font, FontStyle.Bold);
                for (int i = 0; i < TabCount; i++)
                {
                    var rect = GetTabRect(i);
                    bool sel = SelectedIndex == i;
                    if (sel)
                    {
                        // Active tab: solid fill flush to the top of the strip (and to the
                        // left edge for the first tab), with a 3px accent bar on top. No
                        // dead space above or beside it.
                        int fx = i == 0 ? 0 : rect.Left;
                        int fw = rect.Right - fx;
                        using (var tb = new SolidBrush(tabSel))
                            g.FillRectangle(tb, fx, 0, fw, rect.Bottom);
                        using (var ab = new SolidBrush(Accent))
                            g.FillRectangle(ab, fx, 0, fw, 3);
                    }
                    TextRenderer.DrawText(g, TabPages[i].Text, sel ? boldFont : Font, rect, sel ? textSel : textNorm,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }

                // Fill the gap the control leaves between the tab row and the page with the
                // page color, so the content sits flush under the tabs (no dark dead band).
                if (TabCount > 0)
                {
                    int tabsBottom = GetTabRect(0).Bottom;
                    int pageTop = DisplayRectangle.Top;
                    if (pageTop > tabsBottom)
                    {
                        var pageColor = SelectedTab?.BackColor ?? strip;
                        using var pb = new SolidBrush(pageColor);
                        g.FillRectangle(pb, 0, tabsBottom, Width, pageTop - tabsBottom);
                    }
                }
            }
            catch
            {
                // Ignore transient GDI errors during rapid repaint.
            }
            finally
            {
                EndPaint(Handle, ref ps);
            }
        }
    }
}
