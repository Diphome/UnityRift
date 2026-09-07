using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UnityRiftGUI
{
    // Dark color table for the menus (bar, dropdowns, hover, separators).
    internal sealed class MenuColors : ProfessionalColorTable
    {
        private static readonly Color Bar = Color.FromArgb(37, 37, 38);
        private static readonly Color Bg = Color.FromArgb(43, 43, 45);
        private static readonly Color Hover = Color.FromArgb(62, 62, 66);
        private static readonly Color Border = Color.FromArgb(60, 60, 63);
        private static readonly Color Sep = Color.FromArgb(70, 70, 73);

        public MenuColors() { UseSystemColors = false; }

        public override Color ToolStripDropDownBackground => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemPressedGradientBegin => Bg;
        public override Color MenuItemPressedGradientMiddle => Bg;
        public override Color MenuItemPressedGradientEnd => Bg;
        public override Color MenuBorder => Border;
        public override Color SeparatorDark => Sep;
        public override Color SeparatorLight => Sep;
        public override Color ToolStripBorder => Bar;
        public override Color MenuStripGradientBegin => Bar;
        public override Color MenuStripGradientEnd => Bar;
    }

    // Draws checked menu items with a clean accent check (no gray box) and keeps text /
    // arrows readable on the dark dropdowns.
    internal sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color Accent = Color.FromArgb(0, 122, 204);
        private static readonly Color Text = Color.FromArgb(228, 228, 228);
        private static readonly Color TextDim = Color.FromArgb(140, 140, 140);

        public MenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : TextDim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item.Enabled ? Text : TextDim;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // Replace the boxed check with a crisp accent tick.
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            float cx = r.Left + r.Width / 2f;
            float cy = r.Top + r.Height / 2f;
            using var pen = new Pen(Accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[]
            {
                new PointF(cx - 4.5f, cy + 0.5f),
                new PointF(cx - 1.5f, cy + 3.5f),
                new PointF(cx + 4.5f, cy - 3.5f),
            });
        }
    }
}
