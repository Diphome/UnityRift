using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace UnityRiftGUI
{
    static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            // Files/folders passed on the command line are loaded once the window is up.
            UnityRiftGUIForm.StartupPaths = args;
#if !NETFRAMEWORK
            // Per-monitor v2: crisp rendering on high-DPI/scaled displays and when the
            // window is dragged between monitors at different scales.
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
#endif
#if NET6_0_OR_GREATER
            // Modern UI font. Set as the app-wide default before any control is created,
            // so autoscale metrics are computed against it (avoids layout drift that a
            // post-init form-font swap would cause). Must NOT be disposed: WinForms holds
            // this Font instance as the ambient default for the app's lifetime; disposing
            // it leaves a dead GDI handle and controls throw on first measure.
            Application.SetDefaultFont(new System.Drawing.Font("Segoe UI", 9F));
#endif
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new UnityRiftGUIForm());
        }
    }
}
