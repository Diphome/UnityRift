using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AssetStudio;
using static AssetStudioGUI.Studio;

namespace AssetStudioGUI
{
    // "File > Recent projects": remembers what was loaded (a folder, or a set of files)
    // so it can be reopened with one click. Entries are persisted in user settings as
    // lines of '|'-separated paths (newest first). Created in code, no Designer edits.
    partial class AssetStudioGUIForm
    {
        private const int MaxRecentProjects = 10;
        private const char RecentPathSeparator = '|';

        private ToolStripMenuItem recentProjectsToolStripMenuItem;

        private void InitRecentProjectsMenu()
        {
            recentProjectsToolStripMenuItem = new ToolStripMenuItem("Recent projects");
            // Right after "Load file" / "Load folder", before the separator.
            var index = fileToolStripMenuItem.DropDownItems.IndexOf(loadFolderToolStripMenuItem) + 1;
            fileToolStripMenuItem.DropDownItems.Insert(index, recentProjectsToolStripMenuItem);
            RebuildRecentProjectsMenu();
        }

        private static List<string[]> GetRecentProjects()
        {
            var raw = Properties.Settings.Default.recentProjects ?? string.Empty;
            return raw
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(new[] { RecentPathSeparator }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim()).Where(p => p.Length > 0).ToArray())
                .Where(paths => paths.Length > 0)
                .ToList();
        }

        private static void SaveRecentProjects(List<string[]> projects)
        {
            Properties.Settings.Default.recentProjects = string.Join("\n",
                projects.Take(MaxRecentProjects).Select(paths => string.Join(RecentPathSeparator.ToString(), paths)));
            Properties.Settings.Default.Save();
        }

        private static bool SamePaths(string[] a, string[] b)
        {
            return a.Length == b.Length
                && a.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(b.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Records a successfully loaded project (called after a load completed).</summary>
        private void AddRecentProject(IEnumerable<string> loadedPaths)
        {
            var paths = loadedPaths
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => { try { return Path.GetFullPath(p); } catch { return p; } })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length == 0)
                return;

            var projects = GetRecentProjects();
            projects.RemoveAll(existing => SamePaths(existing, paths));
            projects.Insert(0, paths);
            SaveRecentProjects(projects);
            RebuildRecentProjectsMenu();
        }

        private static string DescribeProject(string[] paths)
        {
            var first = paths[0];
            var name = Path.GetFileName(first.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name))
                name = first;
            var parent = Path.GetDirectoryName(first.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var label = string.IsNullOrEmpty(parent) ? name : $"{name}  ({parent})";
            if (paths.Length > 1)
                label += $"  [+{paths.Length - 1} more]";
            return label;
        }

        private void RebuildRecentProjectsMenu()
        {
            var menu = recentProjectsToolStripMenuItem;
            menu.DropDownItems.Clear();
            var projects = GetRecentProjects();

            if (projects.Count == 0)
            {
                menu.DropDownItems.Add(new ToolStripMenuItem("(empty)") { Enabled = false });
                return;
            }

            for (var i = 0; i < projects.Count; i++)
            {
                var paths = projects[i];
                var item = new ToolStripMenuItem($"&{(i + 1) % 10}  {DescribeProject(paths)}")
                {
                    Tag = paths,
                    ToolTipText = string.Join(Environment.NewLine, paths),
                };
                item.Click += recentProjectItem_Click;
                menu.DropDownItems.Add(item);
            }

            menu.DropDownItems.Add(new ToolStripSeparator());
            var clear = new ToolStripMenuItem("Clear list");
            clear.Click += (s, e) =>
            {
                SaveRecentProjects(new List<string[]>());
                RebuildRecentProjectsMenu();
            };
            menu.DropDownItems.Add(clear);
        }

        private async void recentProjectItem_Click(object sender, EventArgs e)
        {
            if (!(sender is ToolStripMenuItem item) || !(item.Tag is string[] paths))
                return;

            var missing = paths.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
            if (missing.Count > 0)
            {
                var msg = "This project can no longer be found:\n\n" + string.Join("\n", missing) + "\n\nRemove it from the list?";
                if (MessageBox.Show(msg, "Recent projects", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    var projects = GetRecentProjects();
                    projects.RemoveAll(existing => SamePaths(existing, paths));
                    SaveRecentProjects(projects);
                    RebuildRecentProjectsMenu();
                }
                return;
            }

            var pathList = paths.ToList();
            assetsManager.LoadOptionFiles(pathList);
            if (pathList.Count == 0)
                return;

            ResetForm();
            Logger.Info($"Reopening recent project: {DescribeProject(paths)}");
            await Task.Run(() => assetsManager.LoadFilesAndFolders(out openDirectoryBackup, pathList));
            saveDirectoryBackup = openDirectoryBackup;
            AddRecentProject(paths);
            BuildAssetStructures();
        }
    }
}
