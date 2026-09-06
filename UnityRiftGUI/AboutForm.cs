using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace UnityRiftGUI
{
    public partial class AboutForm : Form
    {
        public AboutForm()
        {
            InitializeComponent();
            var arch = Environment.Is64BitProcess ? "x64" : "x32";
            var appAssembly = typeof(Program).Assembly.GetName();
            const string productName = "UnityRift";
            var productVer = appAssembly.Version.ToString();
            Text += " " + productName;
            productTitleLabel.Text = productName;
            productVersionLabel.Text = $"v{productVer} [{arch}]";
            productNamelabel.Text = productName;
            modVersionLabel.Text = productVer;

            AddUnityRiftAuthor();

            licenseRichTextBox.Text = GetLicenseText();
        }

        // Adds the UnityRift author row to the Authors table in code (no Designer edits).
        private void AddUnityRiftAuthor()
        {
            var role = new Label { Text = "UnityRift:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new System.Windows.Forms.Padding(3, 4, 3, 0), BackColor = System.Drawing.Color.Transparent };
            var author = new Label { Text = "Diphome (c) 2026", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new System.Windows.Forms.Padding(3, 4, 3, 0), BackColor = System.Drawing.Color.Transparent };
            var link = new LinkLabel { Text = "GitHub page", AutoSize = true, BackColor = System.Drawing.Color.Transparent, LinkColor = System.Drawing.SystemColors.MenuHighlight };
            link.LinkClicked += (s, e) => Process.Start(new ProcessStartInfo("https://github.com/Diphome") { UseShellExecute = true });

            tableLayoutPanel2.RowCount = 3;
            tableLayoutPanel2.RowStyles.Clear();
            for (var i = 0; i < 3; i++)
                tableLayoutPanel2.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 33.33F));
            tableLayoutPanel2.Height = 60;
            tableLayoutPanel2.Controls.Add(role, 0, 2);
            tableLayoutPanel2.Controls.Add(author, 1, 2);
            tableLayoutPanel2.Controls.Add(link, 2, 2);
        }

        private string GetLicenseText()
        {
            string license = "MIT License";

            if (File.Exists("LICENSE"))
            {
                string text = File.ReadAllText("LICENSE");
                license = text.Replace("\r", "")
                    .Replace("\n\n", "\r")
                    .Replace("\nCopyright", "\tCopyright")
                    .Replace("\n", " ")
                    .Replace("\r", "\n\n")
                    .Replace("\tCopyright", "\nCopyright");
            }

            return license;
        }

        private void checkUpdatesLinkLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            var ps = new ProcessStartInfo("https://github.com/Diphome/UnityRift/releases")
            {
                UseShellExecute = true
            };
            Process.Start(ps);
        }

        private void gitPerfareLinkLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            var ps = new ProcessStartInfo("https://github.com/Perfare")
            {
                UseShellExecute = true
            };
            Process.Start(ps);
        }

        private void gitAelurumLinkLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            var ps = new ProcessStartInfo("https://github.com/aelurum")
            {
                UseShellExecute = true
            };
            Process.Start(ps);
        }
    }
}
