using UnityRift;
using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using static UnityRiftGUI.Studio;

namespace UnityRiftGUI
{
    /// <summary>
    /// ".NET Classes" tab: a browser over the game's managed assemblies (Mono.Cecil).
    /// Assembly → namespace → type → members. Selecting a node shows a C#-like stub (or the
    /// IL of a method) in the Preview tab's class text box.
    /// </summary>
    partial class UnityRiftGUIForm
    {
        private TabPage dotnetTabPage;
        private TreeView dotnetTreeView;
        private TextBox dotnetSearch;
        private SearchHost dotnetSearchHost;
        private SearchToggle dotnetIlToggle;
        private CheckBox dotnetShowIL;
        private Label dotnetStatusLabel;
        private ToolStripMenuItem loadAssembliesToolStripMenuItem;
        private ToolStripMenuItem loadIl2CppToolStripMenuItem;
        private const string DotnetLoadingTag = "__loading__";

        private void InitDotNetTab()
        {
            dotnetTabPage = new TabPage(".NET Classes") { UseVisualStyleBackColor = true };

            dotnetTreeView = new TreeView
            {
                Dock = DockStyle.Fill,
                BackColor = SystemColors.Window,
                BorderStyle = BorderStyle.FixedSingle,
                HideSelection = false,
                ShowNodeToolTips = true,
            };
            dotnetTreeView.BeforeExpand += dotnetTreeView_BeforeExpand;
            dotnetTreeView.AfterSelect += dotnetTreeView_AfterSelect;

            // Same search row as the other tabs: a Dock=Top panel of Height 48 with an 8px
            // bottom gap, holding the SearchHost. "Show IL" becomes an in-field toggle and
            // Export stays as a single button on the right.
            var topPanel = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(0, 0, 0, 8) };
            dotnetSearch = new TextBox
            {
                PlaceholderText = "Search types…",
                Font = new System.Drawing.Font("Segoe UI", 11F),
            };
            dotnetSearch.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    BuildDotNetTree();
                }
            };
            dotnetSearchHost = new SearchHost(dotnetSearch) { Dock = DockStyle.Fill };

            // "Show IL" is kept as hidden state; the in-field "IL" toggle drives it.
            dotnetShowIL = new CheckBox { Visible = false };
            dotnetShowIL.CheckedChanged += (s, e) => ShowDotNetNode(dotnetTreeView.SelectedNode);
            dotnetIlToggle = dotnetSearchHost.AddToggle("IL", "Show IL disassembly");
            dotnetIlToggle.CheckedChanged += (s, e) => dotnetShowIL.Checked = dotnetIlToggle.Checked;

            topPanel.Controls.Add(dotnetSearchHost);
            InitDotNetExport(topPanel);

            dotnetStatusLabel = new Label
            {
                Dock = DockStyle.Bottom,
                AutoSize = false,
                Height = 18,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = SystemColors.GrayText,
                Text = "No assemblies loaded. File → Load .NET assemblies, or load a game folder with a Managed directory.",
            };

            dotnetTabPage.Controls.Add(dotnetTreeView);
            dotnetTabPage.Controls.Add(topPanel);
            // dotnetStatusLabel is kept as a state holder but not shown (the empty-state
            // overlay conveys the same guidance; the bottom bar is removed).
            tabControl1.TabPages.Add(dotnetTabPage);

            loadAssembliesToolStripMenuItem = new ToolStripMenuItem("Load .NET assemblies folder");
            loadAssembliesToolStripMenuItem.Click += loadAssembliesToolStripMenuItem_Click;
            loadIl2CppToolStripMenuItem = new ToolStripMenuItem("Load IL2CPP binary (GameAssembly.dll / libil2cpp.so)");
            loadIl2CppToolStripMenuItem.Click += loadIl2CppToolStripMenuItem_Click;
            loadIl2CppToolStripMenuItem.Enabled = Il2CppAssemblyProvider.IsSupported;
            var idx = fileToolStripMenuItem.DropDownItems.IndexOf(toolStripMenuItem1);
            if (idx < 0) idx = fileToolStripMenuItem.DropDownItems.Count;
            fileToolStripMenuItem.DropDownItems.Insert(idx, loadAssembliesToolStripMenuItem);
            fileToolStripMenuItem.DropDownItems.Insert(idx + 1, loadIl2CppToolStripMenuItem);

            Studio.AssembliesLoaded = () => BeginInvoke(new Action(BuildDotNetTree));
        }

        private async void loadAssembliesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var openFolderDialog = new OpenFolderDialog();
            openFolderDialog.Title = "Select .NET assemblies (Managed) folder";
            openFolderDialog.InitialFolder = assemblyLoader.LoadedPath ?? openDirectoryBackup;
            if (openFolderDialog.ShowDialog(this) != DialogResult.OK)
                return;
            await LoadAssembliesAsync(openFolderDialog.Folder);
        }

        private async void loadIl2CppToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Select the IL2CPP binary";
                dlg.Filter = "IL2CPP binary|GameAssembly.dll;libil2cpp.so;GameAssembly.so;GameAssembly.dylib;libil2cpp.dylib|All files|*.*";
                dlg.InitialDirectory = openDirectoryBackup;
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;
                var game = Il2CppAssemblyProvider.FromBinary(dlg.FileName);
                if (game == null)
                {
                    // metadata not found automatically: ask for it
                    using (var mdDlg = new OpenFileDialog())
                    {
                        mdDlg.Title = "Select global-metadata.dat";
                        mdDlg.Filter = "global-metadata.dat|global-metadata.dat|All files|*.*";
                        mdDlg.InitialDirectory = Path.GetDirectoryName(dlg.FileName);
                        if (mdDlg.ShowDialog(this) != DialogResult.OK)
                            return;
                        game = Il2CppAssemblyProvider.FromBinary(dlg.FileName, mdDlg.FileName);
                    }
                }
                if (game == null)
                {
                    MessageBox.Show(this, "Could not find global-metadata.dat for this binary.", "IL2CPP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                await LoadIl2CppAsync(game);
            }
        }

        /// <summary>Finds a Managed folder (Mono) or an IL2CPP binary next to the loaded
        /// files. Detection only: a filesystem scan, nothing is parsed or loaded.</summary>
        private async Task<(string managed, Il2CppGame game)> FindProjectAssembliesAsync()
        {
            var paths = assetsManager.AssetsFileList.Select(f => f.originalPath ?? f.fullName).Where(p => p != null).Distinct().ToList();
            if (!string.IsNullOrEmpty(openDirectoryBackup))
                paths.Add(openDirectoryBackup);
            var managed = await Task.Run(() => AssemblyLoader.FindManagedFolder(paths));
            if (managed != null)
                return (managed, null);
            return (null, await Task.Run(() => Il2CppAssemblyProvider.Find(paths)));
        }

        /// <summary>Called after files are loaded: detects assemblies and only logs a hint.
        /// It never loads automatically — loading (IL2CPP dummy-assembly generation above
        /// all) can be slow on big projects, so the user starts it explicitly with the
        /// "Discover assemblies" button on the .NET Classes tab, or from the .NET menu.</summary>
        private async Task TryAutoLoadAssembliesAsync()
        {
            if (assemblyLoader.Loaded)
                return;
            var (managed, game) = await FindProjectAssembliesAsync();
            if (managed != null)
                Logger.Info($"Found .NET assemblies folder: {managed}. Use \"Discover assemblies\" on the .NET Classes tab to load it.");
            else if (game != null && !Il2CppAssemblyProvider.IsSupported)
                Logger.Info($"IL2CPP game detected ({game.BinaryPath}) but IL2CPP support needs the .NET 8+ build.");
            else if (game != null)
                Logger.Info($"Found IL2CPP binary: {game.BinaryPath}. Use \"Discover assemblies\" on the .NET Classes tab to load it — this can take a while.");
            UpdateDotNetEmptyState();
        }

        // --- "Discover assemblies" button on the .NET Classes empty state ---------------
        private Button dotnetDiscoverButton;

        // One click: find the Managed folder / IL2CPP binary that belongs to the loaded
        // project and load it. Only offered once a project is actually loaded.
        private async void DotNetDiscover_Click(object sender, EventArgs e)
        {
            if (assemblyLoader.Loaded || assetsManager.AssetsFileList.Count == 0)
                return;
            dotnetDiscoverButton.Enabled = false;
            BeginBusy("Looking for .NET assemblies…");
            try
            {
                var (managed, game) = await FindProjectAssembliesAsync();
                if (managed != null)
                {
                    Logger.Info($"Found .NET assemblies folder: {managed}");
                    SetBusyText("Loading .NET assemblies…");
                    await LoadAssembliesAsync(managed);
                    return;
                }
                if (game == null)
                {
                    Logger.Warning("No Managed folder or IL2CPP binary was found next to the loaded files. Use the .NET menu to pick one manually.");
                    return;
                }
                if (!Il2CppAssemblyProvider.IsSupported)
                {
                    Logger.Warning($"IL2CPP game detected ({game.BinaryPath}) but IL2CPP support needs the .NET 8+ build.");
                    return;
                }
                Logger.Info($"Found IL2CPP binary: {game.BinaryPath}");
                SetBusyText("Preparing IL2CPP dummy assemblies (this can take a while)…");
                await LoadIl2CppAsync(game);
            }
            catch (Exception ex)
            {
                Logger.Error("Assembly discovery failed", ex);
            }
            finally
            {
                EndBusy();
                if (dotnetDiscoverButton != null)
                    dotnetDiscoverButton.Enabled = true;
                UpdateDotNetEmptyState();
            }
        }

        // Lives on the tab page itself, not on the empty-state Label: a Label is not a
        // container control, and a button parented to one does not reliably show.
        private void UpdateDotNetDiscoverButton(bool loading)
        {
            if (dotnetTabPage == null)
                return;
            if (dotnetDiscoverButton == null)
            {
                dotnetDiscoverButton = new Button
                {
                    Text = "Discover assemblies for this project",
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Padding = new Padding(14, 7, 14, 7),
                    Cursor = Cursors.Hand,
                    Font = new System.Drawing.Font("Segoe UI", 9.5f),
                    Visible = false,
                };
                dotnetDiscoverButton.Click += DotNetDiscover_Click;
                dotnetTabPage.Controls.Add(dotnetDiscoverButton);
                dotnetTabPage.Resize += (s, e) => PositionDotNetDiscoverButton();
                ThemeDotNetDiscoverButton(isDarkMode);
            }
            // Test the empty-state condition itself, not dotnetEmptyLabel.Visible: WinForms
            // reports *effective* visibility, so the label reads as hidden whenever the .NET
            // tab is not the selected one — which is exactly when this runs after a load.
            var show = !loading && !assemblyLoader.Loaded
                && assetsManager.AssetsFileList.Count > 0
                && dotnetTreeView.Nodes.Count == 0;
            dotnetDiscoverButton.Visible = show;
            if (show)
            {
                // The button replaces the empty-state message entirely: it stands on its own,
                // centered, so the redundant "No .NET assemblies loaded" text is not shown.
                if (dotnetEmptyLabel != null)
                    dotnetEmptyLabel.Visible = false;
                PositionDotNetDiscoverButton();
                dotnetDiscoverButton.BringToFront();
            }
        }

        private void PositionDotNetDiscoverButton()
        {
            if (dotnetDiscoverButton == null || dotnetTabPage == null)
                return;
            // Centered on the page (no message above it now).
            dotnetDiscoverButton.Location = new System.Drawing.Point(
                Math.Max(0, (dotnetTabPage.ClientSize.Width - dotnetDiscoverButton.Width) / 2),
                Math.Max(0, (dotnetTabPage.ClientSize.Height - dotnetDiscoverButton.Height) / 2));
        }

        private void ThemeDotNetDiscoverButton(bool dark)
        {
            if (dotnetDiscoverButton == null)
                return;
            dotnetDiscoverButton.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
            dotnetDiscoverButton.UseVisualStyleBackColor = !dark;
            dotnetDiscoverButton.BackColor = dark
                ? System.Drawing.Color.FromArgb(60, 60, 62)
                : System.Drawing.Color.FromArgb(240, 240, 240);
            dotnetDiscoverButton.ForeColor = dark
                ? System.Drawing.Color.FromArgb(225, 225, 225)
                : System.Drawing.Color.FromArgb(20, 20, 20);
            dotnetDiscoverButton.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(90, 90, 94);
        }

        private string LoadedUnityVersionString()
        {
            var first = assetsManager.AssetsFileList.FirstOrDefault();
            return first?.version?.FullVersion;
        }

        private async Task LoadIl2CppAsync(Il2CppGame game)
        {
            var version = LoadedUnityVersionString();
            var cached = Il2CppAssemblyProvider.IsCached(game);
            dotnetStatusLabel.Text = cached
                ? "Loading cached IL2CPP dummy assemblies..."
                : "Generating dummy assemblies from the IL2CPP binary with Cpp2IL (this can take a while and needs a few GB of RAM)...";
            string folder;
            try
            {
                folder = await Task.Run(() => Il2CppAssemblyProvider.GetOrGenerateAssemblies(game, version, msg => Logger.Info(msg)));
            }
            catch (Exception ex)
            {
                Logger.Warning($"IL2CPP processing failed: {ex.Message}");
                dotnetStatusLabel.Text = $"IL2CPP processing failed: {ex.Message}";
                return;
            }
            await LoadAssembliesAsync(folder, il2cpp: true);
        }

        private async Task LoadAssembliesAsync(string folder, bool il2cpp = false)
        {
            dotnetStatusLabel.Text = $"Loading assemblies from {folder}...";
            UpdateDotNetEmptyState("Loading .NET assemblies\u2026");
            await Task.Run(() =>
            {
                assemblyLoader.Clear();
                assemblyLoader.Load(folder);
                assemblyLoader.IsIl2CppStubs = il2cpp;
            });
            Logger.Info($"Loaded {assemblyLoader.Modules.Count} .NET assemblies from {folder}" + (il2cpp ? " (IL2CPP stubs, no method bodies)" : ""));
            BuildDotNetTree();
        }

        private void ClearDotNetTab()
        {
            if (dotnetTreeView == null)
                return;
            dotnetTreeView.Nodes.Clear();
            dotnetShowIL.Enabled = true;
            dotnetShowIL.Text = "Show IL";
            dotnetStatusLabel.Text = "No assemblies loaded. File → Load .NET assemblies, or load a game folder with a Managed directory.";
            UpdateDotNetEmptyState();
        }

        private string DotNetFilter => dotnetSearch.Text.Trim();

        private void BuildDotNetTree()
        {
            if (dotnetTreeView == null)
                return;
            dotnetTreeView.BeginUpdate();
            dotnetTreeView.Nodes.Clear();
            var filter = DotNetFilter;
            var typeCount = 0;
            var shown = 0;
            foreach (var pair in assemblyLoader.Modules.OrderBy(p => AssemblyRank(p.Key)).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                var module = pair.Value;
                var asmNode = new TreeNode(pair.Key) { Tag = module, ToolTipText = module.FileName };
                var nsNodes = new SortedDictionary<string, TreeNode>(StringComparer.Ordinal);
                foreach (var type in module.Types)
                {
                    if (type.Name == "<Module>")
                        continue;
                    typeCount++;
                    if (!DotNetTypeMatches(type, filter))
                        continue;
                    shown++;
                    var ns = string.IsNullOrEmpty(type.Namespace) ? "-" : type.Namespace;
                    if (!nsNodes.TryGetValue(ns, out var nsNode))
                    {
                        nsNode = new TreeNode(ns) { Tag = ns };
                        nsNodes[ns] = nsNode;
                    }
                    nsNode.Nodes.Add(MakeTypeNode(type));
                }
                foreach (var nsNode in nsNodes.Values)
                {
                    nsNode.Text = $"{nsNode.Text} ({nsNode.Nodes.Count})";
                    asmNode.Nodes.Add(nsNode);
                }
                if (asmNode.Nodes.Count == 0 && filter.Length > 0)
                    continue;
                dotnetTreeView.Nodes.Add(asmNode);
            }
            // With a filter (or a small result set) expand so matches are visible.
            if (filter.Length > 0 && shown <= 500)
            {
                foreach (TreeNode asm in dotnetTreeView.Nodes)
                {
                    asm.Expand();
                    foreach (TreeNode ns in asm.Nodes)
                        ns.Expand();
                }
            }
            else
            {
                // Expand the game's main assembly by default.
                var main = dotnetTreeView.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Text.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase));
                main?.Expand();
            }
            dotnetTreeView.EndUpdate();

            var kind = assemblyLoader.IsIl2CppStubs ? " [IL2CPP stubs, no method bodies]" : "";
            dotnetStatusLabel.Text = assemblyLoader.Modules.Count == 0
                ? "No assemblies loaded. File → Load .NET assemblies, or load a game folder with a Managed directory."
                : filter.Length > 0
                    ? $"{shown} / {typeCount} types match \"{filter}\" in {assemblyLoader.Modules.Count} assemblies{kind} ({assemblyLoader.LoadedPath})"
                    : $"{typeCount} types in {assemblyLoader.Modules.Count} assemblies{kind} ({assemblyLoader.LoadedPath})";
            dotnetShowIL.Enabled = !assemblyLoader.IsIl2CppStubs;
            dotnetShowIL.Text = assemblyLoader.IsIl2CppStubs ? "No IL (IL2CPP)" : "Show IL";
            if (assemblyLoader.IsIl2CppStubs)
                dotnetShowIL.Checked = false;
            if (dotnetIlToggle != null)
            {
                dotnetIlToggle.Visible = !assemblyLoader.IsIl2CppStubs; // no IL to show for IL2CPP stubs
                if (assemblyLoader.IsIl2CppStubs)
                    dotnetIlToggle.SetChecked(false);
            }
            UpdateDotNetEmptyState();
        }

        private static int AssemblyRank(string name)
        {
            // Game code first, then Unity, then everything else (System, mscorlib, ...).
            if (name.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase)) return 0;
            if (name.StartsWith("Unity", StringComparison.OrdinalIgnoreCase)) return 2;
            if (name.StartsWith("System", StringComparison.OrdinalIgnoreCase) || name.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase) || name.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Mono.", StringComparison.OrdinalIgnoreCase)) return 3;
            return 1;
        }

        private static bool DotNetTypeMatches(TypeDefinition type, string filter)
        {
            if (filter.Length == 0)
                return true;
            if (type.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return type.HasNestedTypes && type.NestedTypes.Any(n => DotNetTypeMatches(n, filter));
        }

        private static TreeNode MakeTypeNode(TypeDefinition type)
        {
            var node = new TreeNode(TypeLabel(type)) { Tag = type, ToolTipText = type.FullName };
            // placeholder so the node shows an expander; real children are built on expand
            node.Nodes.Add(new TreeNode("...") { Tag = DotnetLoadingTag });
            return node;
        }

        private static string TypeLabel(TypeDefinition type)
        {
            var kind = type.IsEnum ? "enum" : type.IsInterface ? "interface" : type.IsValueType ? "struct" : DotNetTypeDumper.IsDelegate(type) ? "delegate" : "class";
            return $"{DotNetTypeDumper.TypeName(type)}  [{kind}]";
        }

        private void dotnetTreeView_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            var node = e.Node;
            if (!(node.Tag is TypeDefinition type))
                return;
            if (node.Nodes.Count != 1 || !(node.Nodes[0].Tag is string tag) || tag != DotnetLoadingTag)
                return;
            node.Nodes.Clear();
            PopulateTypeNode(node, type);
        }

        private static void PopulateTypeNode(TreeNode node, TypeDefinition type)
        {
            if (type.BaseType != null && type.BaseType.FullName != "System.Object" && !type.IsEnum && !type.IsValueType)
                node.Nodes.Add(new TreeNode("base: " + DotNetTypeDumper.TypeName(type.BaseType, true)) { Tag = type.BaseType, ForeColor = SystemColors.GrayText });

            if (type.IsEnum)
            {
                foreach (var f in type.Fields.Where(f => f.IsStatic && f.HasConstant))
                    node.Nodes.Add(new TreeNode($"{f.Name} = {DotNetTypeDumper.Literal(f.Constant)}") { Tag = f });
                return;
            }

            void Group(string title, IEnumerable<TreeNode> children)
            {
                var list = children.ToList();
                if (list.Count == 0) return;
                var g = new TreeNode($"{title} ({list.Count})") { Tag = title };
                g.Nodes.AddRange(list.ToArray());
                node.Nodes.Add(g);
            }

            Group("Fields", type.Fields.Select(f => new TreeNode($"{f.Name} : {DotNetTypeDumper.TypeName(f.FieldType)}") { Tag = f, ToolTipText = DotNetTypeDumper.FieldDecl(f) }));
            Group("Properties", type.Properties.Select(p => new TreeNode($"{p.Name} : {DotNetTypeDumper.TypeName(p.PropertyType)}") { Tag = p, ToolTipText = DotNetTypeDumper.PropertyDecl(p) }));
            Group("Events", type.Events.Select(ev => new TreeNode($"{ev.Name} : {DotNetTypeDumper.TypeName(ev.EventType)}") { Tag = ev, ToolTipText = DotNetTypeDumper.EventDecl(ev) }));
            Group("Constructors", type.Methods.Where(m => m.IsConstructor).Select(m => new TreeNode(DotNetTypeDumper.MethodLabel(m)) { Tag = m, ToolTipText = DotNetTypeDumper.MethodDecl(m) }));
            Group("Methods", type.Methods.Where(DotNetTypeDumper.IsPlainMethod).Select(m => new TreeNode(DotNetTypeDumper.MethodLabel(m)) { Tag = m, ToolTipText = DotNetTypeDumper.MethodDecl(m) }));
            Group("Nested types", type.NestedTypes.Select(MakeTypeNode));
        }

        private void dotnetTreeView_AfterSelect(object sender, TreeViewEventArgs e)
        {
            ShowDotNetNode(e.Node);
        }

        private void ShowDotNetNode(TreeNode node)
        {
            if (node == null)
                return;
            string text;
            switch (node.Tag)
            {
                case TypeDefinition type:
                    text = DotNetTypeDumper.DumpType(type, dotnetShowIL.Checked);
                    break;
                case MethodDefinition method:
                    text = dotnetShowIL.Checked ? DotNetTypeDumper.DumpMethod(method) : $"// {method.DeclaringType.FullName}\r\n{DotNetTypeDumper.MethodDecl(method)}\r\n";
                    break;
                case FieldDefinition field:
                    text = $"// {field.DeclaringType.FullName}\r\n{DotNetTypeDumper.FieldDecl(field)}\r\n";
                    break;
                case PropertyDefinition prop:
                    text = $"// {prop.DeclaringType.FullName}\r\n{DotNetTypeDumper.PropertyDecl(prop)}\r\n";
                    break;
                case EventDefinition ev:
                    text = $"// {ev.DeclaringType.FullName}\r\n{DotNetTypeDumper.EventDecl(ev)}\r\n";
                    break;
                case TypeReference typeRef:
                    {
                        // "base:" node — resolve and show the base type if it's in the loaded set
                        TypeDefinition resolved = null;
                        try { resolved = typeRef.Resolve(); } catch { /* not loaded */ }
                        text = resolved != null ? DotNetTypeDumper.DumpType(resolved, dotnetShowIL.Checked) : $"// {typeRef.FullName} (not in loaded assemblies)\r\n";
                        break;
                    }
                case ModuleDefinition module:
                    text = DescribeModule(module);
                    break;
                default:
                    // namespace / group nodes: show the parent type when there is one
                    var parentType = node.Parent?.Tag as TypeDefinition;
                    if (parentType == null)
                        return;
                    text = DotNetTypeDumper.DumpType(parentType, dotnetShowIL.Checked);
                    break;
            }

            classTextBox.Visible = true;
            assetInfoLabel.Visible = false;
            assetInfoLabel.Text = null;
            textPreviewBox.Visible = false;
            fontPreviewBox.Visible = false;
            FMODpanel.Visible = false;
            glControl1.Visible = false;
            classTextBox.Text = text;
            lastSelectedItem = null;
            StatusStripUpdate("");
        }

        private static string DescribeModule(ModuleDefinition module)
        {
            var asm = module.Assembly;
            var sb = new System.Text.StringBuilder();
            sb.Append("// ").Append(module.FileName).Append("\r\n");
            sb.Append("assembly ").Append(asm?.Name.FullName ?? module.Name).Append("\r\n");
            sb.Append("runtime: ").Append(module.Runtime).Append("\r\n");
            sb.Append("types: ").Append(module.Types.Count).Append("\r\n");
            if (module.HasAssemblyReferences)
            {
                sb.Append("\r\n// references\r\n");
                foreach (var r in module.AssemblyReferences.OrderBy(r => r.Name))
                    sb.Append("  ").Append(r.FullName).Append("\r\n");
            }
            if (asm != null && asm.HasCustomAttributes)
            {
                sb.Append("\r\n// assembly attributes\r\n");
                foreach (var a in asm.CustomAttributes)
                {
                    var shortName = a.AttributeType.Name;
                    if (shortName.EndsWith("Attribute")) shortName = shortName.Substring(0, shortName.Length - 9);
                    sb.Append("  [").Append(shortName);
                    if (a.HasConstructorArguments)
                        sb.Append('(').Append(string.Join(", ", a.ConstructorArguments.Select(c => DotNetTypeDumper.Literal(c.Value)))).Append(')');
                    sb.Append("]\r\n");
                }
            }
            return sb.ToString();
        }
    }
}
