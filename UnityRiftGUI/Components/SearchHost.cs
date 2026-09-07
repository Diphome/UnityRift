using System;
using System.Drawing;
using System.Windows.Forms;

namespace UnityRiftGUI
{
    // A polished search input: a neutral 1px border, a magnifier icon on the left, and a
    // clear (×) button that appears when there is text. Focus is shown by a subtle
    // background brighten, not an accent border. It hosts an existing TextBox so all the
    // surrounding search logic keeps working on that TextBox.
    public sealed class SearchHost : Panel
    {
        private readonly TextBox _tb;
        private readonly Label _clear = new Label();
        private readonly System.Collections.Generic.List<SearchToggle> _toggles = new System.Collections.Generic.List<SearchToggle>();
        private bool _focused;
        private bool _dark;
        private Color _border = Color.FromArgb(170, 170, 170);
        private Color _icon = Color.FromArgb(130, 130, 130);

        // Blend with the surroundings when idle; brighten a little on focus.
        private Color FieldBg => _dark
            ? (_focused ? Color.FromArgb(58, 58, 60) : Color.FromArgb(46, 46, 48))
            : (_focused ? Color.White : Color.FromArgb(247, 247, 247));

        public SearchHost(TextBox tb)
        {
            _tb = tb;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 34;
            Padding = new Padding(32, 0, 4, 0); // room for the magnifier icon on the left
            BackColor = Color.White;

            tb.Parent?.Controls.Remove(tb);
            tb.BorderStyle = BorderStyle.None;
            tb.Dock = DockStyle.None;      // positioned + vertically centered in OnLayout
            tb.BackColor = BackColor;

            _clear.Text = "✕";
            _clear.AutoSize = false;
            _clear.Size = new Size(22, 22);
            _clear.TextAlign = ContentAlignment.MiddleCenter;
            _clear.Cursor = Cursors.Hand;
            _clear.Visible = false;
            _clear.Font = new Font("Segoe UI", 9F);
            _clear.Click += (s, e) => { _tb.Clear(); _tb.Focus(); };

            Controls.Add(_clear);
            Controls.Add(tb);

            tb.Enter += (s, e) => { _focused = true; UpdateFieldBackground(); };
            tb.Leave += (s, e) => { _focused = false; UpdateFieldBackground(); };
            tb.TextChanged += (s, e) => { _clear.Visible = _tb.TextLength > 0; PerformLayout(); };
        }

        // Lay the buttons out from the right, inset from the border (so the frame stays
        // continuous), then fit the text field between the icon and the left-most button.
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            const int inset = 5;      // gap from the right border
            const int btnH = 22;
            int cyTop = (ClientSize.Height - btnH) / 2;
            int x = ClientSize.Width - inset;

            if (_clear.Visible)
            {
                x -= _clear.Width;
                _clear.SetBounds(x, cyTop, _clear.Width, btnH);
                x -= 2;
            }
            for (int i = _toggles.Count - 1; i >= 0; i--)
            {
                var t = _toggles[i];
                if (!t.Visible) continue;
                x -= t.Width;
                t.SetBounds(x, cyTop, t.Width, btnH);
            }

            int left = Padding.Left;
            int th = _tb.PreferredHeight;
            int top = Math.Max(0, (ClientSize.Height - th) / 2);
            _tb.SetBounds(left, top, Math.Max(0, x - left - 4), th);
        }

        // Adds an in-field toggle button (VS Code style). Positioned (not docked) in
        // OnLayout so it sits inside the border rather than overpainting it.
        public SearchToggle AddToggle(string glyph, string tooltip)
        {
            var t = new SearchToggle(glyph, tooltip);
            Controls.Add(t);
            _toggles.Add(t);
            t.BackColor = BackColor;
            PerformLayout();
            return t;
        }

        public void Theme(bool dark)
        {
            _dark = dark;
            _tb.ForeColor = dark ? Color.FromArgb(230, 230, 230) : Color.FromArgb(20, 20, 20);
            _border = dark ? Color.FromArgb(78, 78, 82) : Color.FromArgb(190, 190, 192);
            _icon = dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(130, 130, 130);
            _clear.ForeColor = _icon;
            foreach (var t in _toggles)
                t.SetIdleColor(_icon);
            UpdateFieldBackground();
        }

        private void UpdateFieldBackground()
        {
            var bg = FieldBg;
            BackColor = bg;
            _tb.BackColor = bg;
            _clear.BackColor = bg;
            foreach (var t in _toggles)
                t.BackColor = bg;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor); // fill the whole box so the text field blends (no inner rectangle)
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // Neutral 1px border; focus is shown only by the subtle background brighten.
            using (var pen = new Pen(_border, 1f))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            // magnifier
            using (var ip = new Pen(_icon, 1.6f))
            {
                int cx = 14, cy = Height / 2 - 1, r = 5;
                g.DrawEllipse(ip, cx - r, cy - r, r * 2, r * 2);
                g.DrawLine(ip, cx + r - 1, cy + r - 1, cx + r + 3, cy + r + 3);
            }
        }
    }

    // A small square toggle for inside a SearchHost (accent highlight when active).
    public sealed class SearchToggle : Control
    {
        private static readonly Color Accent = Color.FromArgb(0, 122, 204);
        private Color _idle = Color.FromArgb(130, 130, 130);

        public bool Checked { get; private set; }
        public new event EventHandler CheckedChanged;

        public SearchToggle(string glyph, string tooltip)
        {
            Text = glyph;
            Width = 24;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            var tt = new ToolTip { InitialDelay = 300 };
            tt.SetToolTip(this, tooltip);
            Click += (s, e) => { Checked = !Checked; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); };
        }

        public void SetIdleColor(Color c) { _idle = c; Invalidate(); }

        public void SetChecked(bool value)
        {
            if (Checked == value) return;
            Checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // Active = the glyph itself turns accent-blue (no box).
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, Checked ? Accent : _idle,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
