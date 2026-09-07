using System.Drawing;
using System.Windows.Forms;

namespace UnityRiftGUI
{
    // Small modeless popup that shows operation progress outside the main window.
    public sealed class ProgressDialog : Form
    {
        private readonly ProgressBar _bar;
        private readonly Label _label;

        public ProgressDialog()
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            ControlBox = false;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "Working…";
            ClientSize = new Size(380, 84);

            _label = new Label
            {
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Processing…",
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 9.5f),
            };
            _bar = new ProgressBar
            {
                Dock = DockStyle.Top,
                Height = 22,
                Style = ProgressBarStyle.Continuous,
                Minimum = 0,
                Maximum = 100,
            };
            var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 16, 12) };
            pad.Controls.Add(_bar);
            pad.Controls.Add(_label);
            Controls.Add(pad);
        }

        public void SetValue(int value)
        {
            if (_bar.Style != ProgressBarStyle.Continuous)
                _bar.Style = ProgressBarStyle.Continuous;
            if (value < 0) value = 0;
            if (value > _bar.Maximum) value = _bar.Maximum;
            _bar.Value = value;
        }

        public void SetMarquee(bool on)
        {
            _bar.Style = on ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        }

        public void SetText(string text)
        {
            if (!string.IsNullOrEmpty(text))
                _label.Text = text;
        }

        // Match the app's dark palette.
        public void ApplyDark()
        {
            BackColor = Color.FromArgb(37, 37, 38);
            ForeColor = Color.FromArgb(220, 220, 220);
            foreach (Control c in Controls)
                c.ForeColor = Color.FromArgb(220, 220, 220);
            _label.ForeColor = Color.FromArgb(220, 220, 220);
        }

        // Center over the owner window.
        public void CenterOn(Form owner)
        {
            if (owner == null) return;
            Location = new Point(
                owner.Left + (owner.Width - Width) / 2,
                owner.Top + (owner.Height - Height) / 2);
        }
    }
}
