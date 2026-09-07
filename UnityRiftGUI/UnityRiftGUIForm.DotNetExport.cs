using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using UnityRift;
using Mono.Cecil;
using static UnityRiftGUI.Studio;

namespace UnityRiftGUI
{
    // Export for the ".NET Classes" tab: C# stub files for the selected type / assembly /
    // everything, or a copy of the assembly files themselves (useful for IL2CPP dummy
    // assemblies that only exist in the cache folder). Reachable from an "Export" button
    // in the tab and from the tree's right-click menu.
    partial class UnityRiftGUIForm
    {
        private ToolStripMenuItem dotnetExportMenuItem; // ".NET Classes" submenu under the main Export menu
        private ContextMenuStrip dotnetExportMenu;
        private ToolStripMenuItem dotnetExportTypeItem;
        private ToolStripMenuItem dotnetExportAssemblyItem;
        private ToolStripMenuItem dotnetExportAllItem;
        private ToolStripMenuItem dotnetExportDllItem;
        private ToolStripMenuItem dotnetExportGhidraItem;

        private void InitDotNetExport(Panel topPanel)
        {
            dotnetExportMenu = new ContextMenuStrip();
            dotnetExportTypeItem = new ToolStripMenuItem("Export selected type (.cs)");
            dotnetExportTypeItem.Click += async (s, e) => await ExportDotNetSelectedTypeAsync();
            dotnetExportAssemblyItem = new ToolStripMenuItem("Export selected assembly (.cs stubs)");
            dotnetExportAssemblyItem.Click += async (s, e) => await ExportDotNetStubsAsync(SelectedDotNetModule() is ModuleDefinition m ? new[] { m } : null);
            dotnetExportAllItem = new ToolStripMenuItem("Export all assemblies (.cs stubs)");
            dotnetExportAllItem.Click += async (s, e) => await ExportDotNetStubsAsync(assemblyLoader.Modules.Values);
            dotnetExportDllItem = new ToolStripMenuItem("Export assembly files (.dll)");
            dotnetExportDllItem.Click += async (s, e) => await ExportDotNetAssemblyFilesAsync();
            dotnetExportGhidraItem = new ToolStripMenuItem("Export Ghidra / Il2CppDumper package");
            dotnetExportGhidraItem.Click += async (s, e) => await ExportIl2CppGhidraPackageAsync();
            dotnetExportMenu.Items.AddRange(new ToolStripItem[]
            {
                dotnetExportTypeItem,
                dotnetExportAssemblyItem,
                dotnetExportAllItem,
                new ToolStripSeparator(),
                dotnetExportDllItem,
                dotnetExportGhidraItem,
            });
            dotnetExportMenu.Opening += (s, e) =>
            {
                var loaded = assemblyLoader.Modules.Count > 0;
                dotnetExportTypeItem.Enabled = loaded && SelectedDotNetType() != null;
                dotnetExportAssemblyItem.Enabled = loaded && SelectedDotNetModule() != null;
                dotnetExportAllItem.Enabled = loaded;
                dotnetExportDllItem.Enabled = loaded;
                dotnetExportGhidraItem.Enabled = loaded && assemblyLoader.IsIl2CppStubs
                    && !string.IsNullOrEmpty(assemblyLoader.LoadedPath)
                    && Il2CppSymbolIndex.Exists(assemblyLoader.LoadedPath);
            };

            // Merge the .NET export actions into the main Export menu as a submenu, so the
            // search row stays clean. Greyed when no assemblies are loaded.
            dotnetExportMenuItem = new ToolStripMenuItem(".NET Classes");
            var mType = new ToolStripMenuItem("Export selected type (.cs)");
            mType.Click += async (s, e) => await ExportDotNetSelectedTypeAsync();
            var mAsm = new ToolStripMenuItem("Export selected assembly (.cs stubs)");
            mAsm.Click += async (s, e) => await ExportDotNetStubsAsync(SelectedDotNetModule() is ModuleDefinition m2 ? new[] { m2 } : null);
            var mAll = new ToolStripMenuItem("Export all assemblies (.cs stubs)");
            mAll.Click += async (s, e) => await ExportDotNetStubsAsync(assemblyLoader.Modules.Values);
            var mDll = new ToolStripMenuItem("Export assembly files (.dll)");
            mDll.Click += async (s, e) => await ExportDotNetAssemblyFilesAsync();
            var mGhidra = new ToolStripMenuItem("Export Ghidra / Il2CppDumper package");
            mGhidra.Click += async (s, e) => await ExportIl2CppGhidraPackageAsync();
            dotnetExportMenuItem.DropDownItems.AddRange(new ToolStripItem[]
            {
                mType, mAsm, mAll, new ToolStripSeparator(), mDll, mGhidra,
            });
            dotnetExportMenuItem.DropDownOpening += (s, e) =>
            {
                var loaded = assemblyLoader.Modules.Count > 0;
                mType.Enabled = loaded && SelectedDotNetType() != null;
                mAsm.Enabled = loaded && SelectedDotNetModule() != null;
                mAll.Enabled = loaded;
                mDll.Enabled = loaded;
                mGhidra.Enabled = loaded && assemblyLoader.IsIl2CppStubs
                    && !string.IsNullOrEmpty(assemblyLoader.LoadedPath)
                    && Il2CppSymbolIndex.Exists(assemblyLoader.LoadedPath);
            };
            exportToolStripMenuItem.DropDownItems.Add(new ToolStripSeparator());
            exportToolStripMenuItem.DropDownItems.Add(dotnetExportMenuItem);
            exportToolStripMenuItem.DropDownOpening += (s, e) =>
                dotnetExportMenuItem.Enabled = assemblyLoader.Loaded;

            dotnetTreeView.ContextMenuStrip = dotnetExportMenu;
            dotnetTreeView.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Right)
                    return;
                var node = dotnetTreeView.GetNodeAt(e.Location);
                if (node != null)
                    dotnetTreeView.SelectedNode = node;
            };
        }

        private TypeDefinition SelectedDotNetType()
        {
            for (var node = dotnetTreeView?.SelectedNode; node != null; node = node.Parent)
            {
                if (node.Tag is TypeDefinition type)
                    return type;
            }
            return null;
        }

        private ModuleDefinition SelectedDotNetModule()
        {
            for (var node = dotnetTreeView?.SelectedNode; node != null; node = node.Parent)
            {
                if (node.Tag is ModuleDefinition module)
                    return module;
            }
            return SelectedDotNetType()?.Module;
        }

        private string AskDotNetExportFolder(string title)
        {
            var dialog = new OpenFolderDialog
            {
                Title = title,
                InitialFolder = saveDirectoryBackup,
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return null;
            saveDirectoryBackup = dialog.Folder;
            return dialog.Folder;
        }

        private bool DotNetExportWithIL => dotnetShowIL.Checked && !assemblyLoader.IsIl2CppStubs;

        private async Task ExportDotNetSelectedTypeAsync()
        {
            var type = SelectedDotNetType();
            if (type == null)
                return;
            var folder = AskDotNetExportFolder($"Export {type.FullName} to");
            if (folder == null)
                return;

            var withIL = DotNetExportWithIL;
            var path = await Task.Run(() => DotNetExporter.ExportTypeStub(type, folder, withIL, Logger.Warning));
            if (path == null)
            {
                Logger.Error($"Failed to export {type.FullName}.");
                return;
            }
            Logger.Info($"Exported {type.FullName} to \"{path}\"");
            if (Properties.Settings.Default.openAfterExport)
                OpenFolderInExplorer(Path.GetDirectoryName(path));
        }

        private async Task ExportDotNetStubsAsync(IEnumerable<ModuleDefinition> modules)
        {
            var list = modules?.ToList();
            if (list == null || list.Count == 0)
                return;
            var what = list.Count == 1 ? list[0].Name : $"{list.Count} assemblies";
            var folder = AskDotNetExportFolder($"Export .cs stubs of {what} to");
            if (folder == null)
                return;

            var withIL = DotNetExportWithIL;
            Logger.Info($"Exporting .NET stubs of {what}...");
            Progress.Reset();
            var result = await Task.Run(() => DotNetExporter.ExportStubs(list, folder, withIL, null, (cur, total) => Progress.Report(cur, total), Logger.Warning));
            Logger.Info($"Exported {result.Types} type(s) as .cs files to \"{folder}\"" + (result.Failed > 0 ? $" ({result.Failed} failed)" : ""));
            if (Properties.Settings.Default.openAfterExport && result.Files > 0)
                OpenFolderInExplorer(folder);
        }

        private async Task ExportDotNetAssemblyFilesAsync()
        {
            var list = assemblyLoader.Modules.Values.ToList();
            if (list.Count == 0)
                return;
            var folder = AskDotNetExportFolder($"Copy {list.Count} assembly file(s) to");
            if (folder == null)
                return;

            Progress.Reset();
            var result = await Task.Run(() => DotNetExporter.ExportAssemblyFiles(list, folder, (cur, total) => Progress.Report(cur, total), Logger.Warning));
            Logger.Info($"Copied {result.Files} assembly file(s) to \"{folder}\"" + (result.Failed > 0 ? $" ({result.Failed} failed)" : ""));
            if (Properties.Settings.Default.openAfterExport && result.Files > 0)
                OpenFolderInExplorer(folder);
        }

        private async Task ExportIl2CppGhidraPackageAsync()
        {
            var src = assemblyLoader.LoadedPath;
            if (string.IsNullOrEmpty(src) || !Il2CppSymbolIndex.Exists(src))
            {
                Logger.Warning("No Ghidra package in the loaded IL2CPP cache. Re-load the IL2CPP binary to regenerate it.");
                return;
            }
            var folder = AskDotNetExportFolder("Export Ghidra / Il2CppDumper package to");
            if (folder == null)
                return;
            var copied = await Task.Run(() => Il2CppAssemblyProvider.ExportGhidraPackage(src, folder));
            Logger.Info($"Copied {copied.Count} Ghidra helper file(s) to \"{folder}\"");
            Logger.Info("Ghidra: import GameAssembly.dll / libil2cpp.so, File > Parse C Source > il2cpp_ghidra.h, then Script Manager → add the 'ghidra' folder and run ghidra.py (or ghidra_with_struct.py) and pick script.json.");
            if (Properties.Settings.Default.openAfterExport && copied.Count > 0)
                OpenFolderInExplorer(folder);
        }
    }
}
