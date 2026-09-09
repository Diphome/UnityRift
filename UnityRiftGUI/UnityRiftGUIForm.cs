using UnityRift;
using Newtonsoft.Json;
using OpenTK.Graphics.OpenGL;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;
using static UnityRiftGUI.Studio;
using Font = UnityRift.Font;
using Microsoft.WindowsAPICodePack.Taskbar;
#if NET472
using OpenTK;
using Vector2 = OpenTK.Vector2;
using Vector3 = OpenTK.Vector3;
using Vector4 = OpenTK.Vector4;
#else
using Vector2 = OpenTK.Mathematics.Vector2;
using Vector3 = OpenTK.Mathematics.Vector3;
using Vector4 = OpenTK.Mathematics.Vector4;
using Matrix4 = OpenTK.Mathematics.Matrix4;
#endif

namespace UnityRiftGUI
{
    partial class UnityRiftGUIForm : Form
    {
        private AssetItem lastSelectedItem;
        private AssetItem lastPreviewItem;
        private DirectBitmap imageTexture;
        private System.Drawing.Bitmap videoThumb; // OS-generated poster frame, fallback when playback is unavailable
        private Microsoft.Web.WebView2.WinForms.WebView2 videoView; // in-preview VideoClip player
        private bool videoViewReady;
        private int videoPreviewToken; // bumped on every (re)selection to cancel stale async playback
        private string lastVideoTempPath; // temp file backing the currently loaded clip
        private const string VideoHost = "unityrift-clip.local"; // virtual host mapped to the temp folder
        private string tempClipboard;
        private bool isDarkMode;

        #region FMODControl
        private FMOD.System system;
        private FMOD.Sound sound;
        private FMOD.Channel channel;
        private FMOD.MODE loopMode = FMOD.MODE.LOOP_OFF;
        private byte[] soundBuff;
        private uint FMODlenms;
        private uint FMODloopstartms;
        private uint FMODloopendms;
        private float FMODVolume = 0.8f;
        #endregion

        #region SpriteControl
        private SpriteMaskMode spriteMaskVisibleMode = SpriteMaskMode.On;
        #endregion

        #region TexControl
        private static char[] textureChannelNames = new[] { 'B', 'G', 'R', 'A' };
        private bool[] textureChannels = new[] { true, true, true, true };
        #endregion

        #region GLControl
        private bool glControlLoaded;
        private int mdx, mdy;
        private bool lmdown, rmdown;
        private int pgmID, pgmColorID, pgmBlackID;
        private int pgmTexID;
        private int attributeTexPos, attributeTexNormal, attributeTexUv;
        private int uniformTexModel, uniformTexView, uniformTexProj, uniformTexSampler;
        private int attributeVertexPosition;
        private int attributeNormalDirection;
        private int attributeVertexColor;
        private int uniformModelMatrix;
        private int uniformViewMatrix;
        private int uniformProjMatrix;
        private int vao;
        private Vector3[] vertexData;
        private Vector3[] normalData;
        private Vector3[] normal2Data;
        private Vector4[] colorData;
        private Matrix4 modelMatrixData;
        private Matrix4 viewMatrixData;
        private Matrix4 projMatrixData;

        // Animator preview (mesh + skeletal animation playback)
        private AnimationPlayer animPlayer;
        private int animClipIndex = -1;      // -1 = bind pose
        private float animTime;
        private int[] animVertexOffset;      // per player-mesh start index in the combined arrays
        private int animDefaultTex = -1;     // 1x1 white fallback for submeshes without a texture
        private sealed class AnimGLSub { public int Ebo, Count, Tex; public bool OwnsTex; }
        private sealed class AnimGLMesh { public int Vao, Pos, Nor, Uv; public Vector3[] PosBuf, NorBuf; public List<AnimGLSub> Subs = new List<AnimGLSub>(); }
        private readonly List<AnimGLMesh> animGL = new List<AnimGLMesh>();
        private System.Windows.Forms.Timer animTimer;
        private Panel animPanel;
        private ComboBox animClipCombo;
        private Button animPlayButton;
        private CheckBox animLoopCheck;
        private Label animTimeLabel;
        private TrackBar animTrackBar;
        private bool animSuppressEvents;
        private int[] indiceData;
        private int wireFrameMode;
        private int shadeMode;
        private int normalMode;
        #endregion

        //asset list sorting
        private int sortColumn = -1;
        private bool reverseSort;

#if NETFRAMEWORK
        private AlphanumComparatorFast alphanumComparator = new AlphanumComparatorFast();
#else
        private AlphanumComparatorFastNet alphanumComparator = new AlphanumComparatorFastNet();
#endif

        //asset list selection
        // HashSet: ProcessSelectedItems adds/removes per index, and List.Remove is a linear
        // scan, which made deselecting after a Ctrl+A over thousands of rows quadratic.
        private HashSet<int> selectedIndicesPrevList = new HashSet<int>();
        private List<AssetItem> selectedAnimationAssetsList = new List<AssetItem>();

        //asset list filter
        private System.Timers.Timer delayTimer;
        private bool enableFiltering = true;

        //tree search
        private int nextGObject;
        private List<TreeNode> treeSrcResults = new List<TreeNode>();

        //tree selection
        private List<TreeNode> treeNodeSelectedList = new List<TreeNode>();
        private bool treeRecursionEnabled = true;
        private bool isRecursionEvent = false;

        private string openDirectoryBackup = string.Empty;
        private string saveDirectoryBackup = string.Empty;

        private GUILogger logger;

        private TaskbarManager taskbar = TaskbarManager.Instance;

        [DllImport("gdi32.dll")]
        private static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, [In] ref uint pcFonts);

        // --- Dark-theme native helpers ---
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);

        private const int LVM_GETHEADER = 0x1000 + 31;

        // Force a ListView's owner-drawn header to repaint (it caches across theme switches).
        private void RefreshListHeader(ListView lv)
        {
            if (!lv.IsHandleCreated) return;
            var header = SendMessage(lv.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            if (header != IntPtr.Zero)
                InvalidateRect(header, IntPtr.Zero, true);
        }

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20; // dark title bar (Win10 20H1+)

        // --- Modern icon toolbar ---
        private ToolStrip toolStripMain;
        private MenuRenderer menuRenderer;
        private ImageList rowSpacer;

        private string guiTitle;

        public UnityRiftGUIForm()
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
            ConsoleWindow.RunConsole(Properties.Settings.Default.showConsole);
            InitializeComponent();
            ApplyColorTheme(out isDarkMode);
            previewPanel.Image = PreviewPlaceholder();
            if (StartupPaths != null && StartupPaths.Length > 0)
                Shown += async (s, e) => await LoadPathsAsync(StartupPaths.ToList());
            menuStrip1.Padding = new Padding(0); // remove the strip's dead space (left/top/bottom)
            statusStrip1.Visible = false; // remove the "Ready to go" bottom bar (progress is in the popup)
            classesListView.ShowGroups = false; // hide the Unity-version group header (it's in the title bar)
            // Stretch each list's last column so there's no blank trailing area.
            classesListView.SizeChanged += (s, e) => FillLastColumn(classesListView);
            FillLastColumn(classesListView);
            assetListView.SizeChanged += (s, e) => FillLastColumn(assetListView);
            FillLastColumn(assetListView);
            // Taller rows so results breathe (a 1px-wide spacer image sets the row height).
            rowSpacer = new ImageList { ImageSize = new Size(1, 26), ColorDepth = ColorDepth.Depth32Bit };
            assetListView.SmallImageList = rowSpacer;
            classesListView.SmallImageList = rowSpacer;
            InitToolbar();
            InitDotNetTab();
            InitRecentProjectsMenu();
            InitGodotExportMenu();
            InitSpineMenu();
            WrapTreeSearch();
            WrapListSearch();
            ApplyUiFonts();
            // After every tab/control has been created (incl. the runtime .NET Classes tab).
            WireThemeControls();
            ApplyTheme(isDarkMode);
            InitAssetEmptyState();
            InitStatusCounter();

            var appAssembly = typeof(Program).Assembly.GetName();
            guiTitle = $"UnityRift v{appAssembly.Version}";
            Text = guiTitle;

            delayTimer = new System.Timers.Timer(800);
            delayTimer.Elapsed += delayTimer_Elapsed;
            displayAll.Checked = Properties.Settings.Default.displayAll;
            displayInfo.Checked = Properties.Settings.Default.displayInfo;
            enablePreview.Checked = Properties.Settings.Default.enablePreview;
            showConsoleToolStripMenuItem.Checked = Properties.Settings.Default.showConsole;
            buildTreeStructureToolStripMenuItem.Checked = Properties.Settings.Default.buildTreeStructure;
            useAssetLoadingViaTypetreeToolStripMenuItem.Checked = Properties.Settings.Default.useTypetreeLoading;
            useDumpTreeViewToolStripMenuItem.Checked = Properties.Settings.Default.useDumpTreeView;
            autoPlayAudioAssetsToolStripMenuItem.Checked = Properties.Settings.Default.autoplayAudio;
            meshLazyLoadToolStripMenuItem.Checked = Properties.Settings.Default.meshLazyLoad;
            customBlockCompressionComboBox.SelectedIndex = 0;
            customBlockInfoCompressionComboBox.SelectedIndex = 0;
            assetsManager.Options.BundleOptions.DecompressToDisk = Properties.Settings.Default.decompressToDisk;
            var typeTreeDbPath = Path.Combine(Application.StartupPath, "classdata.tpk");
            if (File.Exists(typeTreeDbPath))
                assetsManager.LoadTypeTreeDatabase(typeTreeDbPath);
            FMODinit();
#if NET5_0_OR_GREATER
            // Native watermark cue (replaces the old " Filter " sentinel-text hack).
            listSearch.PlaceholderText = "Search by name, container or path";
            // Shown in the Dump text box while nothing is selected.
            dumpTextBox.PlaceholderText = "Select an asset to view its dump";
            // Scene Hierarchy object search.
            treeSearch.PlaceholderText = "Search objects…";
#endif
            listSearchFilterMode.SelectedIndex = 0;
            FbxInitOptions(Properties.Settings.Default.fbxSettings);

            logger = new GUILogger(StatusStripUpdate);
            Logger.Default = logger;
            writeLogToFileToolStripMenuItem.Checked = Properties.Settings.Default.useFileLogger;

            Progress.Default = new Progress<int>(SetProgressBarValue);
            Progress.SetInstance(index: 1, new Progress<int>(SetProgressBarStringValue));
            Studio.StatusStripUpdate = StatusStripUpdate;
        }

        private void UnityRiftGUIForm_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
            }
        }

        private async void UnityRiftGUIForm_DragDrop(object sender, DragEventArgs e)
        {
            var pathArray = (string[])e.Data?.GetData(DataFormats.FileDrop);
            if (pathArray == null)
                return;
            await LoadPathsAsync(pathArray.ToList());
        }

        // Paths given on the command line (Explorer "Open with", shortcuts, scripted runs);
        // loaded once the window is shown, exactly like a drag-and-drop of the same paths.
        public static string[] StartupPaths;

        // Shared load path for drag-and-drop and command-line arguments.
        private async Task LoadPathsAsync(List<string> pathList)
        {
            assetsManager.LoadOptionFiles(pathList);
            if (pathList.Count == 0)
                return;

            BeginBusy("Loading files…");
            ResetForm();
            for (var i = 0; i < pathList.Count; i++)
            {
                if (pathList[i].ToLower().EndsWith(".lnk"))
                {
                    var targetPath = LnkReader.GetLnkTarget(pathList[i]);
                    if (!string.IsNullOrEmpty(targetPath))
                    {
                        pathList[i] = targetPath;
                    }
                }
            }
            var loadedPaths = pathList.ToArray();
            await Task.Run(() => assetsManager.LoadFilesAndFolders(out openDirectoryBackup, pathList));
            saveDirectoryBackup = openDirectoryBackup;
            AddRecentProject(loadedPaths);
            BuildAssetStructures();
        }

        private async void loadFile_Click(object sender, EventArgs e)
        {
            openFileDialog1.InitialDirectory = openDirectoryBackup;
            if (openFileDialog1.ShowDialog(this) == DialogResult.OK)
            {
                var pathList = openFileDialog1.FileNames.ToList();
                assetsManager.LoadOptionFiles(pathList);
                if (pathList.Count == 0)
                    return;
                BeginBusy("Loading files…");
                ResetForm();
                var loadedPaths = pathList.ToArray();
                await Task.Run(() => assetsManager.LoadFilesAndFolders(out openDirectoryBackup, pathList));
                AddRecentProject(loadedPaths);
                BuildAssetStructures();
            }
        }

        private async void loadFolder_Click(object sender, EventArgs e)
        {
            var openFolderDialog = new OpenFolderDialog();
            openFolderDialog.InitialFolder = openDirectoryBackup;
            if (openFolderDialog.ShowDialog(this) == DialogResult.OK)
            {
                BeginBusy("Loading files…");
                ResetForm();
                await Task.Run(() => assetsManager.LoadFilesAndFolders(out openDirectoryBackup, openFolderDialog.Folder));
                AddRecentProject(new[] { openFolderDialog.Folder });
                BuildAssetStructures();
            }
        }

        private async void extractFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (openFileDialog1.ShowDialog(this) == DialogResult.OK)
            {
                var saveFolderDialog = new OpenFolderDialog();
                saveFolderDialog.Title = "Select the save folder";
                if (saveFolderDialog.ShowDialog(this) == DialogResult.OK)
                {
                    var fileNames = openFileDialog1.FileNames;
                    var savePath = saveFolderDialog.Folder;
                    var extractedCount = await Task.Run(() => ExtractFile(fileNames, savePath));
                    Logger.Info($"Finished extracting {extractedCount} files.");
                }
            }
        }

        private async void extractFolderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var openFolderDialog = new OpenFolderDialog();
            if (openFolderDialog.ShowDialog(this) == DialogResult.OK)
            {
                var saveFolderDialog = new OpenFolderDialog();
                saveFolderDialog.Title = "Select the save folder";
                if (saveFolderDialog.ShowDialog(this) == DialogResult.OK)
                {
                    var path = openFolderDialog.Folder;
                    var savePath = saveFolderDialog.Folder;
                    var extractedCount = await Task.Run(() => ExtractFolder(path, savePath));
                    Logger.Info($"Finished extracting {extractedCount} files.");
                }
            }
        }

        private async void BuildAssetStructures()
        {
            if (assetsManager.AssetsFileList.Count == 0)
            {
                Logger.Info("No Unity file can be loaded.");
                EndBusy();
                return;
            }

            try
            {
                SetBusyText("Building the asset list…");
                var (productName, treeNodeCollection) = await Task.Run(BuildAssetData);
                SetBusyText("Reading class structures…");
                var typeMap = await Task.Run(BuildClassStructure);
                productName = string.IsNullOrEmpty(productName) ? "no productName" : productName;
                Progress.Reset();

                var serializedFile = assetsManager.AssetsFileList[0];
                var tuanjieString = serializedFile.version.IsTuanjie ? " - Tuanjie Engine" : "";
                Text = $"{guiTitle} - {productName} - {serializedFile.version} - {serializedFile.targetPlatformString}{tuanjieString}";

                // Everything below fills the views and so must run on the UI thread, which
                // blocks the message pump. Each step names itself and repaints the popup
                // before it starts, so the window never sits there looking hung.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                SetBusyText($"Populating the asset list ({visibleAssets.Count:N0} assets)…");
                assetListView.BeginUpdate();
                assetListView.VirtualListSize = visibleAssets.Count;
                FillLastColumn(assetListView);
                assetListView.EndUpdate();
                UpdateAssetEmptyState();
                UpdateAssetCounts();
                Logger.Debug($"Asset list populated in {sw.ElapsedMilliseconds} ms");

                sw.Restart();
                PopulateSceneTree(treeNodeCollection);
                Logger.Debug($"Scene hierarchy populated in {sw.ElapsedMilliseconds} ms");

                sw.Restart();
                SetBusyText("Populating asset classes…");
                classesListView.BeginUpdate();
                var versionIndex = 0;
                foreach (var version in typeMap)
                {
                    var versionGroup = new ListViewGroup(version.Key.FullVersion);
                    classesListView.Groups.Add(versionGroup);

                    // AddRange in one call per version: adding thousands of rows one at a
                    // time is markedly slower, even inside Begin/EndUpdate.
                    var classItems = new ListViewItem[version.Value.Count];
                    var n = 0;
                    foreach (var uclass in version.Value)
                    {
                        uclass.Value.Group = versionGroup;
                        uclass.Value.SubItems.Add(version.Key.FullVersion);
                        classItems[n++] = uclass.Value;
                    }
                    classesListView.Items.AddRange(classItems);
                    SetBusyProgress($"Populating asset classes… {++versionIndex} / {typeMap.Count}", versionIndex, typeMap.Count);
                }
                typeMap.Clear();
                classesListView.EndUpdate();
                UpdateClassesEmptyState();
                Logger.Debug($"Asset classes populated in {sw.ElapsedMilliseconds} ms");

                sw.Restart();
                SetBusyText("Building the type filter…");
                var types = new SortedSet<string>();
                types.UnionWith(exportableAssets.Select(x => x.TypeString));
                if (Studio.l2dModelDict.Count > 0)
                {
                    types.Add("MonoBehaviour (Live2D Model)");
                }
                foreach (var typeString in types)
                {
                    var typeItem = new ToolStripMenuItem
                    {
                        CheckOnClick = true,
                        Name = typeString,
                        Size = new Size(180, 22),
                        Text = typeString
                    };
                    typeItem.Click += typeToolStripMenuItem_Click;
                    filterTypeToolStripMenuItem.DropDownItems.Add(typeItem);
                }
                allToolStripMenuItem.Checked = true;
                Logger.Debug($"Type filter built in {sw.ElapsedMilliseconds} ms");

                var log = $"Finished loading {assetsManager.AssetsFileList.Count} file(s) with {assetListView.Items.Count} exportable assets";
                var unityVer = assetsManager.AssetsFileList[0].version;
                // One plain pass over the object tables instead of three LINQ passes with
                // delegates: on a big project these tables hold well over a million entries.
                var skipShaders = unityVer > 2020;
                long m_ObjectsCount = 0;
                long objectsCount = 0;
                foreach (var f in assetsManager.AssetsFileList)
                {
                    if (skipShaders)
                    {
                        foreach (var o in f.m_Objects)
                        {
                            if (o.classID != (int)ClassIDType.Shader)
                                m_ObjectsCount++;
                        }
                    }
                    else
                    {
                        m_ObjectsCount += f.m_Objects.Count;
                    }
                    objectsCount += f.Objects.Count;
                }
                if (m_ObjectsCount != objectsCount)
                {
                    log += $" and {m_ObjectsCount - objectsCount} assets failed to read";
                }
                Logger.Info(log);
                // Refresh the .NET tab now that a project is loaded, so its empty state can
                // offer the "Discover assemblies" button (don't wait on the probe below).
                UpdateDotNetEmptyState();
            }
            finally
            {
                // The load is finished as far as the user is concerned. The assembly probe
                // below is a cheap background scan and must not hold the popup open.
                EndBusy();
            }

            await TryAutoLoadAssembliesAsync();
        }

        // Adding the per-file root nodes is the slowest UI step of a load: their whole
        // subtrees are realized with them. Add the roots in chunks and repaint the popup
        // between chunks, so this phase shows real progress instead of a frozen window.
        private void PopulateSceneTree(List<TreeNode> roots)
        {
            const int chunkSize = 64;
            sceneTreeView.BeginUpdate();
            for (var i = 0; i < roots.Count; i += chunkSize)
            {
                var take = Math.Min(chunkSize, roots.Count - i);
                var slice = new TreeNode[take];
                roots.CopyTo(i, slice, 0, take);
                sceneTreeView.Nodes.AddRange(slice);
                SetBusyProgress($"Building the scene hierarchy… {i + take:N0} / {roots.Count:N0} files", i + take, roots.Count);
            }
            sceneTreeView.EndUpdate();
            roots.Clear();
            UpdateSceneEmptyState();
        }

        private void typeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var typeItem = (ToolStripMenuItem)sender;
            if (typeItem != allToolStripMenuItem)
            {
                allToolStripMenuItem.Checked = false;

                var monoBehaviourItemArray = filterTypeToolStripMenuItem.DropDownItems.Find("MonoBehaviour", false);
                var monoBehaviourMocItemArray = filterTypeToolStripMenuItem.DropDownItems.Find("MonoBehaviour (Live2D Model)", false);
                if (monoBehaviourItemArray.Length > 0 && monoBehaviourMocItemArray.Length > 0)
                {
                    var monoBehaviourItem = (ToolStripMenuItem)monoBehaviourItemArray[0];
                    var monoBehaviourMocItem = (ToolStripMenuItem)monoBehaviourMocItemArray[0];
                    if (typeItem == monoBehaviourItem && monoBehaviourItem.Checked)
                    {
                        monoBehaviourMocItem.Checked = false;
                    }
                    else if (typeItem == monoBehaviourMocItem && monoBehaviourMocItem.Checked)
                    {
                        monoBehaviourItem.Checked = false;
                    }
                }
            }
            else if (allToolStripMenuItem.Checked)
            {
                for (var i = 1; i < filterTypeToolStripMenuItem.DropDownItems.Count; i++)
                {
                    var item = (ToolStripMenuItem)filterTypeToolStripMenuItem.DropDownItems[i];
                    item.Checked = false;
                }
            }
            FilterAssetList();
        }

        private void UnityRiftForm_KeyDown(object sender, KeyEventArgs e)
        {
            // Escape in a search box clears it (and re-runs the filter via TextChanged).
            if (e.KeyCode == Keys.Escape && ActiveControl is TextBox searchBox
                && (searchBox == listSearch || searchBox == treeSearch || searchBox == dotnetSearch)
                && searchBox.Text.Length > 0)
            {
                searchBox.Clear();
                if (searchBox == dotnetSearch)
                    BuildDotNetTree();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            // Ctrl+F / Ctrl+K: jump to the Asset List search box.
            if (e.Control && (e.KeyCode == Keys.F || e.KeyCode == Keys.K))
            {
                tabControl1.SelectedTab = tabPage2;
                listSearch.Focus();
                listSearch.SelectAll();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            if (glControl1.Visible)
            {
                if (e.Control)
                {
                    switch (e.KeyCode)
                    {
                        case Keys.W:
                            //Toggle WireFrame
                            wireFrameMode = (wireFrameMode + 1) % 3;
                            glControl1.Invalidate();
                            break;
                        case Keys.S:
                            //Toggle Shade
                            shadeMode = (shadeMode + 1) % 2;
                            glControl1.Invalidate();
                            break;
                        case Keys.N:
                            //Normal mode
                            normalMode = (normalMode + 1) % 2;
                            CreateVAO();
                            glControl1.Invalidate();
                            break;
                    }
                }
            }
            else if (previewPanel.Visible)
            {
                if (e.Control)
                {
                    var need = false;
                    if (lastSelectedItem?.Type == ClassIDType.Texture2D || lastSelectedItem?.Type == ClassIDType.Texture2DArrayImage)
                    {
                        switch (e.KeyCode)
                        {
                            case Keys.B:
                                textureChannels[0] = !textureChannels[0];
                                need = true;
                                break;
                            case Keys.G:
                                textureChannels[1] = !textureChannels[1];
                                need = true;
                                break;
                            case Keys.R:
                                textureChannels[2] = !textureChannels[2];
                                need = true;
                                break;
                            case Keys.A:
                                textureChannels[3] = !textureChannels[3];
                                need = true;
                                break;
                        }
                    }
                    else if (lastSelectedItem?.Type == ClassIDType.Sprite && !((Sprite)lastSelectedItem.Asset).m_RD.alphaTexture.IsNull)
                    {
                        switch (e.KeyCode)
                        {
                            case Keys.A:
                                spriteMaskVisibleMode = spriteMaskVisibleMode == SpriteMaskMode.On ? SpriteMaskMode.Off : SpriteMaskMode.On;
                                need = true;
                                break;
                            case Keys.M:
                                spriteMaskVisibleMode = spriteMaskVisibleMode == SpriteMaskMode.MaskOnly ? SpriteMaskMode.On : SpriteMaskMode.MaskOnly;
                                need = true;
                                break;
                        }
                    }
                    if (need)
                    {
                        if (lastSelectedItem != null)
                        {
                            PreviewAsset(lastSelectedItem);
                            assetInfoLabel.Text = lastSelectedItem.InfoText;
                        }
                    }
                }
            }
        }

        private void exportClassStructuresMenuItem_Click(object sender, EventArgs e)
        {
            if (classesListView.Items.Count > 0)
            {
                var saveFolderDialog = new OpenFolderDialog();
                if (saveFolderDialog.ShowDialog(this) == DialogResult.OK)
                {
                    var savePath = saveFolderDialog.Folder;
                    var count = classesListView.Items.Count;
                    int i = 0;
                    Progress.Reset();
                    foreach (TypeTreeItem item in classesListView.Items)
                    {
                        var versionPath = Path.Combine(savePath, item.Group.Header);
                        Directory.CreateDirectory(versionPath);

                        var saveFile = $"{versionPath}{Path.DirectorySeparatorChar}{item.SubItems[1].Text} {item.Text}.txt";
                        File.WriteAllText(saveFile, item.ToString());

                        Progress.Report(++i, count);
                    }

                    Logger.Info("Finished exporting class structures");
                }
            }
        }

        private void displayAll_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.displayAll = displayAll.Checked;
            Properties.Settings.Default.Save();
        }

        private void enablePreview_Check(object sender, EventArgs e)
        {
            if (lastSelectedItem != null)
            {
                switch (lastSelectedItem.Type)
                {
                    case ClassIDType.Texture2D:
                    case ClassIDType.Sprite:
                        if (enablePreview.Checked && imageTexture != null)
                        {
                            previewPanel.Image = imageTexture.Bitmap;
                        }
                        else
                        {
                            previewPanel.Image = PreviewPlaceholder();
                            previewPanel.SizeMode = PictureBoxSizeMode.CenterImage;
                        }
                        break;
                    case ClassIDType.Shader:
                    case ClassIDType.TextAsset:
                    case ClassIDType.MonoBehaviour:
                        textPreviewBox.Visible = !textPreviewBox.Visible;
                        break;
                    case ClassIDType.Font:
                        fontPreviewBox.Visible = !fontPreviewBox.Visible;
                        break;
                    case ClassIDType.AudioClip:
                        FMODpanel.Visible = !FMODpanel.Visible;

                        if (sound.hasHandle() && channel.hasHandle())
                        {
                            var result = channel.isPlaying(out var playing);
                            if (result == FMOD.RESULT.OK && playing)
                            {
                                channel.stop();
                                FMODreset();
                            }
                        }
                        else if (FMODpanel.Visible)
                        {
                            PreviewAsset(lastSelectedItem);
                        }
                        break;
                }
            }
            else if (lastSelectedItem != null && enablePreview.Checked)
            {
                PreviewAsset(lastSelectedItem);
            }
            Properties.Settings.Default.enablePreview = enablePreview.Checked;
            Properties.Settings.Default.Save();
        }

        private void displayAssetInfo_Check(object sender, EventArgs e)
        {
            if (displayInfo.Checked && assetInfoLabel.Text != null)
            {
                assetInfoLabel.Visible = true;
            }
            else
            {
                assetInfoLabel.Visible = false;
            }
            Properties.Settings.Default.displayInfo = displayInfo.Checked;
            Properties.Settings.Default.Save();
        }

        private void showExpOpt_Click(object sender, EventArgs e)
        {
            var exportOpt = new ExportOptions();
            exportOpt.ShowDialog(this);
        }

        private void assetListView_RetrieveVirtualItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            var item = visibleAssets[e.ItemIndex];
            item.EnsureSubItems(); // cells are created when a row is first shown, not at load
            e.Item = item;
        }

        private void tabPageSelected(object sender, TabControlEventArgs e)
        {
            switch (e.TabPageIndex)
            {
                case 0:
                    sceneTreeView.Select();
                    break;
                case 1:
                    assetListView.Select();
                    // The virtual list is double-buffered (LVS_EX_DOUBLEBUFFER); when its tab is
                    // hidden and shown again the stale back buffer is blitted and rows only paint
                    // where the mouse later invalidates. Force a full repaint on re-entry.
                    assetListView.Invalidate();
                    break;
                case 2:
                    classesListView.Invalidate(); // same double-buffer redraw fix for Asset Classes
                    break;
                case 3:
                    dotnetTreeView?.Select();
                    UpdateDotNetEmptyState(); // refresh the empty state / discover button
                    break;
            }
            UpdatePreviewPlaceholderForTab();
        }

        private void treeSearch_TextChanged(object sender, EventArgs e)
        {
            treeSrcResults.Clear();
            nextGObject = 0;
        }

        private void treeSearch_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                if (treeSrcResults.Count == 0)
                {
                    var isExactSearch = sceneExactSearchCheckBox.Checked;
                    foreach (TreeNode node in sceneTreeView.Nodes)
                    {
                        TreeNodeSearch(node, isExactSearch);
                    }
                }
                if (treeSrcResults.Count > 0)
                {
                    if (nextGObject >= treeSrcResults.Count)
                    {
                        nextGObject = 0;
                    }
                    treeSrcResults[nextGObject].EnsureVisible();
                    sceneTreeView.SelectedNode = treeSrcResults[nextGObject];
                    nextGObject++;
                }
            }
        }

        private void TreeNodeSearch(TreeNode treeNode, bool isExactSearch)
        {
            if (isExactSearch && string.Equals(treeNode.Text, treeSearch.Text, StringComparison.InvariantCultureIgnoreCase))
            {
                treeSrcResults.Add(treeNode);
            }
            else if (!isExactSearch && treeNode.Text.IndexOf(treeSearch.Text, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                treeSrcResults.Add(treeNode);
            }

            foreach (TreeNode node in treeNode.Nodes)
            {
                TreeNodeSearch(node, isExactSearch);
            }
        }

        private void sceneExactSearchCheckBox_CheckedChanged(object sender, EventArgs e)
        {
            treeSearch_TextChanged(sender, e);
        }

        private void sceneTreeView_AfterCheck(object sender, TreeViewEventArgs e)
        {
            if (!treeRecursionEnabled)
                return;

            if (!isRecursionEvent)
            {
                if (e.Node.Checked)
                {
                    treeNodeSelectedList.Add(e.Node);
                }
                else
                {
                    treeNodeSelectedList.Remove(e.Node);
                }
            }

            foreach (TreeNode childNode in e.Node.Nodes)
            {
                isRecursionEvent = true;
                bool wasChecked = childNode.Checked;
                childNode.Checked = e.Node.Checked;
                if (!wasChecked && childNode.Checked)
                {
                    treeNodeSelectedList.Add(childNode);
                }
                else if (!childNode.Checked)
                {
                    treeNodeSelectedList.Remove(childNode);
                }
            }
            isRecursionEvent = false;

            StatusStripUpdate($"Selected {treeNodeSelectedList.Count} object(s).");
        }


        private void ListSearchTextChanged(object sender, EventArgs e)
        {
            if (enableFiltering)
            {
                if (delayTimer.Enabled)
                {
                    delayTimer.Stop();
                    delayTimer.Start();
                }
                else
                {
                    delayTimer.Start();
                }
            }
        }

        private void delayTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            delayTimer.Stop();
            ListSearchHistoryAdd();
            Invoke(new Action(FilterAssetList));
        }

        private void ListSearchHistoryAdd()
        {
            BeginInvoke(new Action(() =>
            {
                if (listSearch.Text.Length > 0)
                {
                    if (listSearchHistory.Items.Count == listSearchHistory.MaxDropDownItems)
                    {
                        listSearchHistory.Items.RemoveAt(listSearchHistory.MaxDropDownItems - 1);
                    }
                    listSearchHistory.Items.Insert(0, listSearch.Text);
                }
            }));
        }

        private void assetListView_ColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (sortColumn != e.Column)
            {
                reverseSort = false;
            }
            else
            {
                reverseSort = !reverseSort;
            }
            sortColumn = e.Column;
            assetListView.BeginUpdate();
            assetListView.SelectedIndices.Clear();
            selectedIndicesPrevList.Clear();
            selectedAnimationAssetsList.Clear();
            switch (sortColumn)
            {
                case 4: //FullSize
                    visibleAssets.Sort((a, b) =>
                    {
                        var asf = a.FullSize;
                        var bsf = b.FullSize;
                        return reverseSort ? bsf.CompareTo(asf) : asf.CompareTo(bsf);
                    });
                    break;
                case 3: //PathID
                    visibleAssets.Sort((x, y) =>
                    {
                        long pathID_X = x.m_PathID;
                        long pathID_Y = y.m_PathID;
                        return reverseSort ? pathID_Y.CompareTo(pathID_X) : pathID_X.CompareTo(pathID_Y);
                    });
                    break;
                case 0: //Name
                    visibleAssets.Sort((a, b) =>
                    {
                        var at = a.Text;
                        var bt = b.Text;
                        return reverseSort ? alphanumComparator.Compare(bt, at) : alphanumComparator.Compare(at, bt);
                    });
                    break;
                default:
                    visibleAssets.Sort((a, b) =>
                    {
                        var at = a.ColumnText(sortColumn).AsSpan();
                        var bt = b.ColumnText(sortColumn).AsSpan();
                        return reverseSort ? bt.CompareTo(at, StringComparison.OrdinalIgnoreCase) : at.CompareTo(bt, StringComparison.OrdinalIgnoreCase);
                    });
                    break;
            }
            assetListView.EndUpdate();
        }

        private void selectAsset(object sender, ListViewItemSelectionChangedEventArgs e)
        {
            previewPanel.Image = PreviewPlaceholder();
            previewPanel.SizeMode = PictureBoxSizeMode.CenterImage;
            classTextBox.Visible = false;
            assetInfoLabel.Visible = false;
            assetInfoLabel.Text = null;
            textPreviewBox.Visible = false;
            fontPreviewBox.Visible = false;
            FMODpanel.Visible = false;
            glControl1.Visible = false;
            StatusStripUpdate("");

            FMODreset();
            ResetVideoPreview();

            lastSelectedItem = (AssetItem)e.Item;

            if (!e.IsSelected)
                return;

            switch (tabControl2.SelectedIndex)
            {
                case 0 when enablePreview.Checked: //Preview
                    // Fallback if the type has no visual preview; overwritten by PreviewAsset when it does.
                    previewPanel.Image = NoPreviewImage(lastSelectedItem.TypeString);
                    PreviewAsset(lastSelectedItem);
                    if (displayInfo.Checked && lastSelectedItem.InfoText != null)
                    {
                        assetInfoLabel.Text = lastSelectedItem.InfoText;
                        assetInfoLabel.Visible = true;
                    }
                    break;
                case 1: //Dump
                    DumpAsset(lastSelectedItem);
                    break;
            }
        }

        private void DumpAsset(AssetItem assetItem)
        {
            if (assetItem == null)
                return;

            if (useDumpTreeViewToolStripMenuItem.Checked)
            {
                using (var jsonDoc = DumpAssetToJsonDoc(assetItem.Asset))
                {
                    dumpTreeView.LoadFromJson(jsonDoc, assetItem.Text);
                }
            }
            else
            {
                dumpTextBox.Text = Studio.DumpAsset(assetItem.Asset);
            }
        }

        private void classesListView_ItemSelectionChanged(object sender, ListViewItemSelectionChangedEventArgs e)
        {
            classTextBox.Visible = true;
            assetInfoLabel.Visible = false;
            assetInfoLabel.Text = null;
            textPreviewBox.Visible = false;
            fontPreviewBox.Visible = false;
            FMODpanel.Visible = false;
            glControl1.Visible = false;
            StatusStripUpdate("");
            if (e.IsSelected)
            {
                classTextBox.Text = ((TypeTreeItem)classesListView.SelectedItems[0]).ToString();
                lastSelectedItem = null;
            }
        }

        private void preview_Resize(object sender, EventArgs e)
        {
            if (glControlLoaded && glControl1.Visible)
            {
                ChangeGLSize(glControl1.Size);
                glControl1.Invalidate();
            }
        }

        private void PreviewAsset(AssetItem assetItem)
        {
            lastPreviewItem = assetItem;
            if (assetItem == null)
                return;
            StopAnimator(); // stop any running animator preview before showing the next asset
            try
            {
                switch (assetItem.Type)
                {
                    case ClassIDType.Texture2D:
                    case ClassIDType.Texture2DArrayImage:
                        PreviewTexture2D(assetItem, assetItem.Asset as Texture2D);
                        break;
                    case ClassIDType.Texture2DArray:
                        PreviewTexture2DArray(assetItem, assetItem.Asset as Texture2DArray);
                        break;
                    case ClassIDType.AudioClip:
                        PreviewAudioClip(assetItem, assetItem.Asset as AudioClip);
                        break;
                    case ClassIDType.Shader:
                        PreviewShader(assetItem.Asset as Shader);
                        break;
                    case ClassIDType.TextAsset:
                        PreviewTextAsset(assetItem.Asset as TextAsset);
                        break;
                    case ClassIDType.MonoBehaviour:
                        var m_MonoBehaviour = (MonoBehaviour)assetItem.Asset;
                        if (m_MonoBehaviour.m_Script.TryGet(out var m_Script))
                        {
                            if (m_Script.m_ClassName == "CubismMoc")
                            {
                                PreviewMoc(assetItem, m_MonoBehaviour);
                                break;
                            }
                        }
                        PreviewMonoBehaviour(m_MonoBehaviour);
                        break;
                    case ClassIDType.Font:
                        PreviewFont(assetItem.Asset as Font);
                        break;
                    case ClassIDType.Mesh:
                        PreviewMesh(assetItem.Asset as Mesh);
                        break;
                    case ClassIDType.VideoClip:
                        PreviewVideoClip(assetItem, assetItem.Asset as VideoClip);
                        break;
                    case ClassIDType.MovieTexture:
                        StatusStripUpdate("Only supported export.");
                        break;
                    case ClassIDType.Sprite:
                        PreviewSprite(assetItem, assetItem.Asset as Sprite);
                        break;
                    case ClassIDType.Animator:
                        PreviewAnimator(assetItem.Asset as Animator);
                        break;
                    case ClassIDType.AnimationClip:
                        StatusStripUpdate("Can be exported with Animator or Objects");
                        break;
                    default:
                        var str = assetItem.Asset.Dump();
                        if (str != null)
                        {
                            textPreviewBox.Text = str;
                            textPreviewBox.Visible = true;
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                MessageBox.Show($"Preview {assetItem.Type}:{assetItem.Text} error\r\n{e.Message}\r\n{e.StackTrace}");
            }
        }

        private void PreviewTexture2DArray(AssetItem assetItem, Texture2DArray m_Texture2DArray)
        {
            assetItem.InfoText =
                $"Width: {m_Texture2DArray.m_Width}\n" +
                $"Height: {m_Texture2DArray.m_Height}\n" +
                $"Graphics format: {m_Texture2DArray.m_Format}\n" +
                $"Texture format: {m_Texture2DArray.m_Format.ToTextureFormat()}\n" +
                $"Texture count: {m_Texture2DArray.m_Depth}";
        }

        private void PreviewTexture2D(AssetItem assetItem, Texture2D m_Texture2D)
        {
            var image = m_Texture2D.ConvertToImage(true);
            if (image != null)
            {
                var bitmap = new DirectBitmap(image);
                image.Dispose();

                assetItem.InfoText = 
                    $"Width: {m_Texture2D.m_Width}" +
                    $"\nHeight: {m_Texture2D.m_Height}" +
                    $"\nFormat: {m_Texture2D.m_TextureFormat}";
                switch (m_Texture2D.m_TextureSettings.m_FilterMode)
                {
                    case 0: assetItem.InfoText += "\nFilter mode: Point "; break;
                    case 1: assetItem.InfoText += "\nFilter mode: Bilinear "; break;
                    case 2: assetItem.InfoText += "\nFilter mode: Trilinear "; break;
                }
                assetItem.InfoText += $"\nAnisotropic level: {m_Texture2D.m_TextureSettings.m_Aniso}\nMip map bias: {m_Texture2D.m_TextureSettings.m_MipBias}";
                switch (m_Texture2D.m_TextureSettings.m_WrapMode)
                {
                    case 0: assetItem.InfoText += "\nWrap mode: Repeat"; break;
                    case 1: assetItem.InfoText += "\nWrap mode: Clamp"; break;
                }
                assetItem.InfoText += "\nChannels: ";
                var validChannel = 0;
                for (var i = 0; i < 4; i++)
                {
                    if (textureChannels[i])
                    {
                        assetItem.InfoText += textureChannelNames[i];
                        validChannel++;
                    }
                }
                if (validChannel == 0)
                    assetItem.InfoText += "None";
                if (validChannel != 4)
                {
                    var bytes = bitmap.Bits;
                    for (var i = 0; i < bitmap.Height; i++)
                    {
                        var offset = Math.Abs(bitmap.Stride) * i;
                        for (var j = 0; j < bitmap.Width; j++)
                        {
                            bytes[offset] = textureChannels[0] ? bytes[offset] : validChannel == 1 && textureChannels[3] ? byte.MaxValue : byte.MinValue;
                            bytes[offset + 1] = textureChannels[1] ? bytes[offset + 1] : validChannel == 1 && textureChannels[3] ? byte.MaxValue : byte.MinValue;
                            bytes[offset + 2] = textureChannels[2] ? bytes[offset + 2] : validChannel == 1 && textureChannels[3] ? byte.MaxValue : byte.MinValue;
                            bytes[offset + 3] = textureChannels[3] ? bytes[offset + 3] : byte.MaxValue;
                            offset += 4;
                        }
                    }
                }
                var switchSwizzled = m_Texture2D.m_PlatformBlob.Length != 0;
                assetItem.InfoText += assetItem.Asset.platform == BuildTarget.Switch
                    ? $"\nUses texture swizzling: {switchSwizzled}"
                    : "";
                PreviewTexture(bitmap);

                StatusStripUpdate("'Ctrl'+'R'/'G'/'B'/'A' for Channel Toggle");
            }
            else
            {
                StatusStripUpdate("Unsupported image for preview");
            }
        }

        private void PreviewAudioClip(AssetItem assetItem, AudioClip m_AudioClip)
        {
            //Info
            assetItem.InfoText = "Compression format: ";
            if (m_AudioClip.version < 5)
            {
                switch (m_AudioClip.m_Type)
                {
                    case FMODSoundType.AAC:
                        assetItem.InfoText += "AAC";
                        break;
                    case FMODSoundType.AIFF:
                        assetItem.InfoText += "AIFF";
                        break;
                    case FMODSoundType.IT:
                        assetItem.InfoText += "Impulse tracker";
                        break;
                    case FMODSoundType.MOD:
                        assetItem.InfoText += "Protracker / Fasttracker MOD";
                        break;
                    case FMODSoundType.MPEG:
                        assetItem.InfoText += "MP2/MP3 MPEG";
                        break;
                    case FMODSoundType.OGGVORBIS:
                        assetItem.InfoText += "Ogg vorbis";
                        break;
                    case FMODSoundType.S3M:
                        assetItem.InfoText += "ScreamTracker 3";
                        break;
                    case FMODSoundType.WAV:
                        assetItem.InfoText += "Microsoft WAV";
                        break;
                    case FMODSoundType.XM:
                        assetItem.InfoText += "FastTracker 2 XM";
                        break;
                    case FMODSoundType.XMA:
                        assetItem.InfoText += "Xbox360 XMA";
                        break;
                    case FMODSoundType.VAG:
                        assetItem.InfoText += "PlayStation Portable ADPCM";
                        break;
                    case FMODSoundType.AUDIOQUEUE:
                        assetItem.InfoText += "iPhone";
                        break;
                    default:
                        assetItem.InfoText += $"Unknown ({m_AudioClip.m_Type})";
                        break;
                }
            }
            else
            {
                switch (m_AudioClip.m_CompressionFormat)
                {
                    case AudioCompressionFormat.PCM:
                        assetItem.InfoText += "PCM";
                        break;
                    case AudioCompressionFormat.Vorbis:
                        assetItem.InfoText += "Vorbis";
                        break;
                    case AudioCompressionFormat.ADPCM:
                        assetItem.InfoText += "ADPCM";
                        break;
                    case AudioCompressionFormat.MP3:
                        assetItem.InfoText += "MP3";
                        break;
                    case AudioCompressionFormat.PSMVAG:
                        assetItem.InfoText += "PlayStation Portable ADPCM";
                        break;
                    case AudioCompressionFormat.HEVAG:
                        assetItem.InfoText += "PSVita ADPCM";
                        break;
                    case AudioCompressionFormat.XMA:
                        assetItem.InfoText += "Xbox360 XMA";
                        break;
                    case AudioCompressionFormat.AAC:
                        assetItem.InfoText += "AAC";
                        break;
                    case AudioCompressionFormat.GCADPCM:
                        assetItem.InfoText += "Nintendo 3DS/Wii DSP";
                        break;
                    case AudioCompressionFormat.ATRAC9:
                        assetItem.InfoText += "PSVita ATRAC9";
                        break;
                    default:
                        assetItem.InfoText += "Unknown";
                        break;
                }
            }
            soundBuff = BigArrayPool<byte>.Shared.Rent(m_AudioClip.m_AudioData.Size);
            var dataLen = m_AudioClip.m_AudioData.GetData(soundBuff);
            if (dataLen <= 0)
                return;

            var exinfo = new FMOD.CREATESOUNDEXINFO();
            exinfo.cbsize = Marshal.SizeOf(exinfo);
            exinfo.length = (uint)m_AudioClip.m_Size;

            // The bundled FMOD build can't decode AAC/M4A (createStream/createSound both return
            // ERR_FORMAT), so those clips couldn't be previewed. Decode AAC to PCM with Windows
            // Media Foundation and hand the raw PCM to FMOD instead; other formats keep streaming.
            var isAac = m_AudioClip.version < 5
                ? m_AudioClip.m_Type == FMODSoundType.AAC
                : m_AudioClip.m_CompressionFormat == AudioCompressionFormat.AAC;
            var result = isAac
                ? CreateAacSound(soundBuff, dataLen, loopMode, out sound)
                : system.createStream(soundBuff, FMOD.MODE.OPENMEMORY | FMOD.MODE.LOWMEM | FMOD.MODE.IGNORETAGS | FMOD.MODE.ACCURATETIME | loopMode, ref exinfo, out sound);
            if (result != FMOD.RESULT.OK)
            {
                if (m_AudioClip.version < (2, 6) || m_AudioClip.version >= 5)
                {
                    var legacyFormat = m_AudioClip.IsLegacyConvertSupport()
                        ? "\nLegacy audio format: Raw wav data"
                        : "";
                    var channels = m_AudioClip.m_Channels > 0
                        ? $"\nChannel count: {m_AudioClip.m_Channels}"
                        : "";
                    var bits = m_AudioClip.version >= 5
                        ? $"\nBit depth: {m_AudioClip.m_BitsPerSample}"
                        : "";
                    assetItem.InfoText +=
                        legacyFormat +
                        $"\nLength: {m_AudioClip.m_Length:0.0##}" +
                        $"\nSample rate: {m_AudioClip.m_Frequency}" +
                        channels +
                        bits;
                }
                var errorMsg = result == FMOD.RESULT.ERR_VERSION
                    ? "Unsupported version of fmod sound. Try to export raw and convert with an external tool instead."
                    : $"Preview not available, try to export instead. {FMOD.Error.String(result)}";
                StatusStripUpdate(errorMsg);
                FMODreset();
                return;
            }

            sound.getNumSubSounds(out var numsubsounds);
            if (numsubsounds > 0)
            {
                result = sound.getSubSound(0, out var subsound);
                if (result == FMOD.RESULT.OK)
                {
                    sound = subsound;
                }
            }

            result = sound.getLength(out FMODlenms, FMOD.TIMEUNIT.MS);
            if (ERRCHECK(result)) return;

            result = sound.getLoopPoints(out FMODloopstartms, FMOD.TIMEUNIT.MS, out FMODloopendms, FMOD.TIMEUNIT.MS);
            if (result == FMOD.RESULT.OK)
            {
                assetItem.InfoText += $"\nLoop Start: {(FMODloopstartms / 1000 / 60):00}:{(FMODloopstartms / 1000 % 60):00}.{(FMODloopstartms / 10 % 100):00}";
                assetItem.InfoText += $"\nLoop End: {(FMODloopendms / 1000 / 60):00}:{(FMODloopendms / 1000 % 60):00}.{(FMODloopendms / 10 % 100):00}";
            }

            var paused = !autoPlayAudioAssetsToolStripMenuItem.Checked;
            _ = system.getMasterChannelGroup(out var channelGroup);
            result = system.playSound(sound, channelGroup, paused, out channel);
            if (ERRCHECK(result)) return;
            if (!paused) 
            {
                timer.Start();
            }

            FMODpanel.Visible = true;

            result = channel.getFrequency(out var frequency);
            if (ERRCHECK(result)) return;

            FMODinfoLabel.Text = frequency + " Hz";
            FMODtimerLabel.Text = $"00:00.00 / {(FMODlenms / 1000 / 60):00}:{(FMODlenms / 1000 % 60):00}.{(FMODlenms / 10 % 100):00}";

            sound.getFormat(out _, out _, out var audioChannels, out _);
            switch (audioChannels)
            {
                case 1:
                    FMODaudioChannelsLabel.Text = "Mono";
                    break;
                case 2:
                    FMODaudioChannelsLabel.Text = "Stereo";
                    break;
                default:
                    FMODaudioChannelsLabel.Text = $"{audioChannels}-Channel";
                    break;
            }
        }

        // Decode an AAC/M4A audio clip to raw PCM via Windows Media Foundation (the bundled FMOD
        // build can't decode AAC itself) and create an FMOD sound from that PCM so the normal
        // preview player can play it.
        private FMOD.RESULT CreateAacSound(byte[] data, int length, FMOD.MODE loopMode, out FMOD.Sound sound)
        {
            sound = default;
            try
            {
                int channels, sampleRate;
                byte[] pcm;
                using (var ms = new System.IO.MemoryStream(data, 0, length, writable: false))
                using (var reader = new NAudio.Wave.StreamMediaFoundationReader(ms))
                {
                    channels = reader.WaveFormat.Channels;
                    sampleRate = reader.WaveFormat.SampleRate;
                    var pcmProvider = reader.WaveFormat.Encoding == NAudio.Wave.WaveFormatEncoding.Pcm && reader.WaveFormat.BitsPerSample == 16
                        ? (NAudio.Wave.IWaveProvider)reader
                        : new NAudio.Wave.SampleProviders.SampleToWaveProvider16(NAudio.Wave.WaveExtensionMethods.ToSampleProvider(reader));
                    using (var outMs = new System.IO.MemoryStream())
                    {
                        var buf = new byte[65536];
                        int n;
                        while ((n = pcmProvider.Read(buf, 0, buf.Length)) > 0)
                            outMs.Write(buf, 0, n);
                        pcm = outMs.ToArray();
                    }
                }
                if (pcm.Length == 0)
                    return FMOD.RESULT.ERR_FORMAT;

                var exinfo = new FMOD.CREATESOUNDEXINFO();
                exinfo.cbsize = Marshal.SizeOf(exinfo);
                exinfo.length = (uint)pcm.Length;
                exinfo.numchannels = channels;
                exinfo.defaultfrequency = sampleRate;
                exinfo.format = FMOD.SOUND_FORMAT.PCM16;
                return system.createSound(pcm, FMOD.MODE.OPENMEMORY | FMOD.MODE.OPENRAW | FMOD.MODE.LOWMEM | loopMode, ref exinfo, out sound);
            }
            catch
            {
                return FMOD.RESULT.ERR_FORMAT;
            }
        }

        private async void PreviewVideoClip(AssetItem assetItem, VideoClip m_VideoClip)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Width: {m_VideoClip.Width}");
            sb.AppendLine($"Height: {m_VideoClip.Height}");
            sb.AppendLine($"Frame rate: {m_VideoClip.m_FrameRate:.0##}");
            sb.AppendLine($"Split alpha: {m_VideoClip.m_HasSplitAlpha}");
            assetItem.InfoText = sb.ToString();

            if (m_VideoClip?.m_VideoData == null || m_VideoClip.m_VideoData.Size <= 0)
            {
                StatusStripUpdate("No embedded video data. Only supported export.");
                return;
            }

            // Play the clip in an embedded WebView2 (Chromium plays mp4/H.264 and webm/VP8/VP9).
            // The player needs a real file, so dump the bytes to a temp file kept alive while it
            // plays, mapped behind a virtual host so the <video> tag can load it.
            var token = ++videoPreviewToken;
            string path;
            try
            {
                path = WriteVideoTemp(m_VideoClip);
            }
            catch (Exception ex)
            {
                StatusStripUpdate("Video preview failed to unpack: " + ex.Message);
                return;
            }

            try
            {
                await EnsureVideoViewAsync();
                if (token != videoPreviewToken)
                    return; // a different asset was selected while WebView2 was initializing

                videoView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    VideoHost, Path.GetDirectoryName(path),
                    Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

                var src = $"https://{VideoHost}/{Uri.EscapeDataString(Path.GetFileName(path))}";
                var html =
                    "<!DOCTYPE html><html><body style=\"margin:0;height:100vh;background:#111;" +
                    "display:flex;align-items:center;justify-content:center\">" +
                    $"<video src=\"{src}\" controls autoplay loop playsinline " +
                    "style=\"max-width:100%;max-height:100%\"></video></body></html>";
                videoView.NavigateToString(html);
                ShowVideoView();
                StatusStripUpdate("Playing video preview. Use the controls to pause, seek or mute.");
            }
            catch (Exception ex)
            {
                // WebView2 runtime missing or failed: fall back to a static poster frame.
                var thumb = TryMakeVideoThumbnail(m_VideoClip);
                if (thumb != null)
                {
                    ShowVideoThumb(thumb);
                    StatusStripUpdate("Playback unavailable (" + ex.Message + "). Showing poster frame; export to play.");
                }
                else
                {
                    StatusStripUpdate("Video preview unavailable: " + ex.Message);
                }
            }
        }

        private string WriteVideoTemp(VideoClip m_VideoClip)
        {
            var ext = Path.GetExtension(m_VideoClip.m_OriginalPath);
            if (string.IsNullOrEmpty(ext))
                ext = ".mp4"; // best-effort default for Unity's external video resource
            var dir = Path.Combine(Path.GetTempPath(), "UnityRift", "vplay");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "clip_" + Guid.NewGuid().ToString("N") + ext);
            m_VideoClip.m_VideoData.WriteData(path);
            lastVideoTempPath = path;
            return path;
        }

        private async Task EnsureVideoViewAsync()
        {
            if (videoView == null)
            {
                videoView = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill, Visible = false };
                previewPanel.Controls.Add(videoView);
            }
            if (!videoViewReady)
            {
                // Keep the browser profile out of the (possibly read-only) install dir, and allow
                // autoplay so the clip starts without a user gesture.
                var udf = Path.Combine(Path.GetTempPath(), "UnityRift", "WebView2");
                Directory.CreateDirectory(udf);
                var opts = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, udf, opts);
                await videoView.EnsureCoreWebView2Async(env);
                videoViewReady = true;
            }
        }

        private void ShowVideoView()
        {
            previewPanel.Image = null;
            videoView.Visible = true;
            videoView.BringToFront();
        }

        // Stop playback, hide the player and release the temp file lock. Called on every
        // reselection, on project reset and on close.
        private void ResetVideoPreview()
        {
            videoPreviewToken++; // cancel any in-flight async playback
            if (videoView != null)
            {
                try
                {
                    if (videoViewReady && videoView.CoreWebView2 != null)
                        videoView.CoreWebView2.Navigate("about:blank"); // releases the file lock
                }
                catch { /* best effort */ }
                videoView.Visible = false;
            }
            DisposeVideoThumb();
            if (lastVideoTempPath != null)
            {
                try { if (File.Exists(lastVideoTempPath)) File.Delete(lastVideoTempPath); } catch { /* still locked; OS cleans %TEMP% */ }
                lastVideoTempPath = null;
            }
        }

        private System.Drawing.Bitmap TryMakeVideoThumbnail(VideoClip m_VideoClip)
        {
            if (m_VideoClip?.m_VideoData == null || m_VideoClip.m_VideoData.Size <= 0)
                return null;

            var ext = Path.GetExtension(m_VideoClip.m_OriginalPath);
            if (string.IsNullOrEmpty(ext))
                ext = ".mp4"; // best-effort default for Unity's external video resource
            var dir = Path.Combine(Path.GetTempPath(), "UnityRift", "vpreview");
            var tempPath = Path.Combine(dir, "preview_" + Guid.NewGuid().ToString("N") + ext);
            try
            {
                Directory.CreateDirectory(dir);
                m_VideoClip.m_VideoData.WriteData(tempPath);

                using (var shellFile = Microsoft.WindowsAPICodePack.Shell.ShellObject.FromParsingName(tempPath))
                {
                    // ThumbnailOnly so we never get a generic file-type icon back; if the shell
                    // has no frame for us it throws and we fall through to null.
                    shellFile.Thumbnail.FormatOption = Microsoft.WindowsAPICodePack.Shell.ShellThumbnailFormatOption.ThumbnailOnly;
                    shellFile.Thumbnail.AllowBiggerSize = true;
                    return shellFile.Thumbnail.ExtraLargeBitmap;
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            }
        }

        private void ShowVideoThumb(System.Drawing.Bitmap bmp)
        {
            DisposeVideoThumb();
            videoThumb = bmp;
            previewPanel.Image = videoThumb;
            previewPanel.SizeMode = (bmp.Width > previewPanel.Width || bmp.Height > previewPanel.Height)
                ? PictureBoxSizeMode.Zoom
                : PictureBoxSizeMode.CenterImage;
        }

        private void DisposeVideoThumb()
        {
            if (videoThumb == null)
                return;
            if (previewPanel.Image == videoThumb)
                previewPanel.Image = null;
            videoThumb.Dispose();
            videoThumb = null;
        }

        private void PreviewShader(Shader m_Shader)
        {
            var str = ShaderConverter.Convert(m_Shader);
            PreviewText(str == null ? "Serialized Shader can't be read" : str.Replace("\n", "\r\n"));
        }

        private void PreviewTextAsset(TextAsset m_TextAsset)
        {
            var text = Encoding.UTF8.GetString(m_TextAsset.m_Script);
            text = text.Replace("\n", "\r\n").Replace("\0", "");
            PreviewText(text);
        }

        private void PreviewMonoBehaviour(MonoBehaviour m_MonoBehaviour)
        {
            var obj = m_MonoBehaviour.ToType();
            if (obj == null)
            {
                var type = MonoBehaviourToTypeTree(m_MonoBehaviour);
                obj = m_MonoBehaviour.ToType(type);
            }
            var str = JsonConvert.SerializeObject(obj, Formatting.Indented);
            PreviewText(str);
        }

        private void PreviewMoc(AssetItem assetItem, MonoBehaviour m_MonoBehaviour)
        {
            using (var cubismMoc = new CubismMoc(m_MonoBehaviour))
            {
                var sb = new StringBuilder();
                if (Studio.l2dModelDict.TryGetValue(m_MonoBehaviour, out var model) && model != null)
                {
                    sb.AppendLine($"Model Name: {model.Name}");
                }
                sb.AppendLine($"SDK Version: {cubismMoc.VersionDescription}");
                if (cubismMoc.Version > 0)
                {
                    sb.AppendLine($"Canvas Width: {cubismMoc.CanvasWidth}");
                    sb.AppendLine($"Canvas Height: {cubismMoc.CanvasHeight}");
                    sb.AppendLine($"Center X: {cubismMoc.CentralPosX}");
                    sb.AppendLine($"Center Y: {cubismMoc.CentralPosY}");
                    sb.AppendLine($"Pixel Per Unit: {cubismMoc.PixelPerUnit}");
                    sb.AppendLine($"Parameter Count: {cubismMoc.ParamCount}");
                    sb.AppendLine($"Part Count: {cubismMoc.PartCount}");
                    sb.AppendLine($"Pre-linked AnimationClips: {model?.ClipMotionList.Count}");
                }
                assetItem.InfoText = sb.ToString();
            }
            StatusStripUpdate("Can be exported as Live2D Cubism model.");
        }

        private void PreviewFont(Font m_Font)
        {
            if (m_Font.m_FontData != null)
            {
                var data = Marshal.AllocCoTaskMem(m_Font.m_FontData.Length);
                Marshal.Copy(m_Font.m_FontData, 0, data, m_Font.m_FontData.Length);

                uint cFonts = 0;
                var re = AddFontMemResourceEx(data, (uint)m_Font.m_FontData.Length, IntPtr.Zero, ref cFonts);
                if (re != IntPtr.Zero)
                {
                    using (var pfc = new PrivateFontCollection())
                    {
                        pfc.AddMemoryFont(data, m_Font.m_FontData.Length);
                        Marshal.FreeCoTaskMem(data);
                        if (pfc.Families.Length > 0)
                        {
                            fontPreviewBox.SelectionStart = 0;
                            fontPreviewBox.SelectionLength = 80;
                            fontPreviewBox.SelectionFont = new System.Drawing.Font(pfc.Families[0], 16, FontStyle.Regular);
                            fontPreviewBox.SelectionStart = 81;
                            fontPreviewBox.SelectionLength = 56;
                            fontPreviewBox.SelectionFont = new System.Drawing.Font(pfc.Families[0], 12, FontStyle.Regular);
                            fontPreviewBox.SelectionStart = 138;
                            fontPreviewBox.SelectionLength = 56;
                            fontPreviewBox.SelectionFont = new System.Drawing.Font(pfc.Families[0], 18, FontStyle.Regular);
                            fontPreviewBox.SelectionStart = 195;
                            fontPreviewBox.SelectionLength = 56;
                            fontPreviewBox.SelectionFont = new System.Drawing.Font(pfc.Families[0], 24, FontStyle.Regular);
                            fontPreviewBox.SelectionStart = 252;
                            fontPreviewBox.SelectionLength = 56;
                            fontPreviewBox.SelectionFont = new System.Drawing.Font(pfc.Families[0], 36, FontStyle.Regular);
                            fontPreviewBox.SelectionStart = 309;
                            fontPreviewBox.SelectionLength = 56;
                            fontPreviewBox.SelectionFont = new System.Drawing.Font(pfc.Families[0], 48, FontStyle.Regular);
                            fontPreviewBox.SelectionStart = 366;
                            fontPreviewBox.SelectionLength = 56;
                            fontPreviewBox.SelectionFont = new System.Drawing.Font(pfc.Families[0], 60, FontStyle.Regular);
                            fontPreviewBox.SelectionStart = 423;
                            fontPreviewBox.SelectionLength = 55;
                            fontPreviewBox.SelectionFont = new System.Drawing.Font(pfc.Families[0], 72, FontStyle.Regular);
                            fontPreviewBox.Visible = true;
                        }
                    }
                    return;
                }
            }
            StatusStripUpdate("Unsupported font for preview. Try to export.");
        }

        private void PreviewMesh(Mesh m_Mesh)
        {
            m_Mesh.ProcessData();

            if (m_Mesh.m_VertexCount > 0)
            {
                viewMatrixData = Matrix4.CreateRotationY(-MathF.PI / 4) * Matrix4.CreateRotationX(-MathF.PI / 6);

                #region Vertices
                if (m_Mesh.m_Vertices == null || m_Mesh.m_Vertices.Length == 0)
                {
                    StatusStripUpdate("Mesh can't be previewed.");
                    return;
                }
                int count = 3;
                if (m_Mesh.m_Vertices.Length == m_Mesh.m_VertexCount * 4)
                {
                    count = 4;
                }
                vertexData = new Vector3[m_Mesh.m_VertexCount];
                // Calculate Bounding
                float[] min = new float[3];
                float[] max = new float[3];
                for (int i = 0; i < 3; i++)
                {
                    min[i] = m_Mesh.m_Vertices[i];
                    max[i] = m_Mesh.m_Vertices[i];
                }
                for (int v = 0; v < m_Mesh.m_VertexCount; v++)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        min[i] = Math.Min(min[i], m_Mesh.m_Vertices[v * count + i]);
                        max[i] = Math.Max(max[i], m_Mesh.m_Vertices[v * count + i]);
                    }
                    vertexData[v] = new Vector3(
                        m_Mesh.m_Vertices[v * count],
                        m_Mesh.m_Vertices[v * count + 1],
                        m_Mesh.m_Vertices[v * count + 2]);
                }

                // Calculate modelMatrix
                Vector3 dist = Vector3.One, offset = Vector3.Zero;
                for (int i = 0; i < 3; i++)
                {
                    dist[i] = max[i] - min[i];
                    offset[i] = (max[i] + min[i]) / 2;
                }
                float d = Math.Max(1e-5f, dist.Length);
                modelMatrixData = Matrix4.CreateTranslation(-offset) * Matrix4.CreateScale(2f / d);
                #endregion

                #region Indicies
                indiceData = new int[m_Mesh.m_Indices.Count];
                for (int i = 0; i < m_Mesh.m_Indices.Count; i = i + 3)
                {
                    indiceData[i] = (int)m_Mesh.m_Indices[i];
                    indiceData[i + 1] = (int)m_Mesh.m_Indices[i + 1];
                    indiceData[i + 2] = (int)m_Mesh.m_Indices[i + 2];
                }
                #endregion

                #region Normals
                if (m_Mesh.m_Normals != null && m_Mesh.m_Normals.Length > 0)
                {
                    if (m_Mesh.m_Normals.Length == m_Mesh.m_VertexCount * 3)
                        count = 3;
                    else if (m_Mesh.m_Normals.Length == m_Mesh.m_VertexCount * 4)
                        count = 4;
                    normalData = new Vector3[m_Mesh.m_VertexCount];
                    for (int n = 0; n < m_Mesh.m_VertexCount; n++)
                    {
                        normalData[n] = new Vector3(
                            m_Mesh.m_Normals[n * count],
                            m_Mesh.m_Normals[n * count + 1],
                            m_Mesh.m_Normals[n * count + 2]);
                    }
                }
                else
                    normalData = null;

                // calculate normal by ourself
                normal2Data = new Vector3[m_Mesh.m_VertexCount];
                int[] normalCalculatedCount = new int[m_Mesh.m_VertexCount];
                for (int i = 0; i < m_Mesh.m_VertexCount; i++)
                {
                    normal2Data[i] = Vector3.Zero;
                    normalCalculatedCount[i] = 0;
                }
                for (int i = 0; i < m_Mesh.m_Indices.Count; i = i + 3)
                {
                    Vector3 dir1 = vertexData[indiceData[i + 1]] - vertexData[indiceData[i]];
                    Vector3 dir2 = vertexData[indiceData[i + 2]] - vertexData[indiceData[i]];
                    Vector3 normal = Vector3.Cross(dir1, dir2);
                    normal.Normalize();
                    for (int j = 0; j < 3; j++)
                    {
                        normal2Data[indiceData[i + j]] += normal;
                        normalCalculatedCount[indiceData[i + j]]++;
                    }
                }
                for (int i = 0; i < m_Mesh.m_VertexCount; i++)
                {
                    if (normalCalculatedCount[i] == 0)
                        normal2Data[i] = new Vector3(0, 1, 0);
                    else
                        normal2Data[i] /= normalCalculatedCount[i];
                }
                #endregion

                #region Colors
                if (m_Mesh.m_Colors != null && m_Mesh.m_Colors.Length == m_Mesh.m_VertexCount * 3)
                {
                    colorData = new Vector4[m_Mesh.m_VertexCount];
                    for (int c = 0; c < m_Mesh.m_VertexCount; c++)
                    {
                        colorData[c] = new Vector4(
                            m_Mesh.m_Colors[c * 3],
                            m_Mesh.m_Colors[c * 3 + 1],
                            m_Mesh.m_Colors[c * 3 + 2],
                            1.0f);
                    }
                }
                else if (m_Mesh.m_Colors != null && m_Mesh.m_Colors.Length == m_Mesh.m_VertexCount * 4)
                {
                    colorData = new Vector4[m_Mesh.m_VertexCount];
                    for (int c = 0; c < m_Mesh.m_VertexCount; c++)
                    {
                        colorData[c] = new Vector4(
                            m_Mesh.m_Colors[c * 4],
                            m_Mesh.m_Colors[c * 4 + 1],
                            m_Mesh.m_Colors[c * 4 + 2],
                            m_Mesh.m_Colors[c * 4 + 3]);
                    }
                }
                else
                {
                    colorData = new Vector4[m_Mesh.m_VertexCount];
                    for (int c = 0; c < m_Mesh.m_VertexCount; c++)
                    {
                        colorData[c] = new Vector4(0.5f, 0.5f, 0.5f, 1.0f);
                    }
                }
                #endregion

                glControl1.Visible = true;
                CreateVAO();
                StatusStripUpdate("Using OpenGL Version: " + GL.GetString(StringName.Version) + "\n"
                                  + "'Mouse Left'=Rotate | 'Mouse Right'=Move | 'Mouse Wheel'=Zoom \n"
                                  + "'Ctrl W'=Wireframe | 'Ctrl S'=Shade | 'Ctrl N'=ReNormal ");
            }
            else
            {
                StatusStripUpdate("Unable to preview this mesh");
            }
        }

        private void PreviewSprite(AssetItem assetItem, Sprite m_Sprite)
        {
            var image = m_Sprite.GetImage(spriteMaskMode: spriteMaskVisibleMode);
            if (image != null)
            {
                var bitmap = new DirectBitmap(image);
                image.Dispose();
                assetItem.InfoText = $"Width: {bitmap.Width}\nHeight: {bitmap.Height}\n";
                PreviewTexture(bitmap);

                if (!m_Sprite.m_RD.alphaTexture.IsNull)
                {
                    assetItem.InfoText += $"Alpha Mask: {spriteMaskVisibleMode}\n";
                    StatusStripUpdate("'Ctrl'+'A' - Enable/Disable alpha mask usage. 'Ctrl'+'M' - Show alpha mask only.");
                }
            }
            else
            {
                StatusStripUpdate("Unsupported sprite for preview.");
            }
        }

        private void PreviewTexture(DirectBitmap bitmap)
        {
            imageTexture?.Dispose();
            imageTexture = bitmap;
            previewPanel.Image = imageTexture.Bitmap;
            if (imageTexture.Width > previewPanel.Width || imageTexture.Height > previewPanel.Height)
                previewPanel.SizeMode = PictureBoxSizeMode.Zoom;
            else
                previewPanel.SizeMode = PictureBoxSizeMode.CenterImage;
        }

        private void PreviewText(string text)
        {
            textPreviewBox.Text = text;
            textPreviewBox.Visible = true;
        }

        private void SetProgressBarValue(int value)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SetProgressBarValue(value)));
                return;
            }

            const int max = 100;
            taskbar.SetProgressValue(value, max);
            taskbar.SetProgressState(value >= max ? TaskbarProgressBarState.NoProgress : TaskbarProgressBarState.Normal);

            // Drive the spawned popup instead of an inline bar. It appears while work is
            // in progress (1..99%) and closes when the operation finishes or resets.
            if (value <= 0 || value >= max)
            {
                // Inside a busy scope a phase reaching 100% is not the end of the
                // operation, so keep the popup up instead of flashing it away.
                if (busyDepth > 0)
                    ShowProgressMarquee(busyText);
                else
                    HideProgress();
            }
            else
            {
                ShowProgress(value, string.IsNullOrEmpty(lastStatusText) ? $"Processing…  {value}%" : $"{lastStatusText}  ({value}%)");
            }
        }

        private void SetProgressBarStringValue(int value)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SetProgressBarStringValue(value)));
                return;
            }
            if (value <= 0 || value >= 100)
            {
                // A finished LZMA block is not the end of a load: keep the popup up.
                if (busyDepth > 0)
                    ShowProgressMarquee(busyText);
                else
                    HideProgress();
            }
            else
            {
                ShowProgress(value, $"Decompressing LZMA: {value}%");
            }
        }

        private void StatusStripUpdate(string statusText)
        {
            lastStatusText = statusText;
            if (InvokeRequired)
            {
                Invoke(new Action(() => { toolStripStatusLabel1.Text = statusText; }));
            }
            else
            {
                toolStripStatusLabel1.Text = statusText;
            }
        }

        // --- Spawned progress popup (replaces the inline status-bar progress bar) ---
        private ProgressDialog progressDialog;
        private string lastStatusText;

        private void ShowProgress(int value, string text)
        {
            if (progressDialog == null || progressDialog.IsDisposed)
            {
                progressDialog = new ProgressDialog();
                if (isDarkMode)
                    progressDialog.ApplyDark();
            }
            progressDialog.SetText(string.IsNullOrEmpty(text) ? "Processing…" : text);
            progressDialog.SetValue(value);
            if (!progressDialog.Visible)
            {
                progressDialog.CenterOn(this);
                progressDialog.Show(this);
            }
        }

        private void HideProgress()
        {
            if (progressDialog != null && !progressDialog.IsDisposed && progressDialog.Visible)
                progressDialog.Hide();
        }

        private void ShowProgressMarquee(string text)
        {
            if (progressDialog == null || progressDialog.IsDisposed)
            {
                progressDialog = new ProgressDialog();
                if (isDarkMode)
                    progressDialog.ApplyDark();
            }
            progressDialog.SetText(text);
            progressDialog.SetMarquee(true);
            if (!progressDialog.Visible)
            {
                progressDialog.CenterOn(this);
                progressDialog.Show(this);
            }
        }

        // --- Busy scope -----------------------------------------------------------
        // A load runs in several phases (decompress, read objects, build the asset
        // list, build the tree, then populate the views on the UI thread). Each phase
        // ends at 100%, which used to close the popup and leave the window looking
        // frozen through the phases that follow. A busy scope keeps the popup up for
        // the whole operation: it only closes when the scope ends.
        private int busyDepth;
        private string busyText = "Loading…";

        private void BeginBusy(string text)
        {
            busyDepth++;
            busyText = text;
            ShowProgressMarquee(text);
        }

        // Sets the phase label and repaints the popup immediately. Used before each
        // UI-thread step: the message pump is about to block, so the popup has to be
        // painted synchronously or it would show as a blank rectangle.
        private void SetBusyText(string text)
        {
            busyText = text;
            if (busyDepth <= 0 || progressDialog == null || progressDialog.IsDisposed)
                return;
            progressDialog.SetText(text);
            progressDialog.SetMarquee(true);
            progressDialog.Refresh();
        }

        // Same, with a real percentage for UI steps we can count.
        private void SetBusyProgress(string text, int current, int total)
        {
            busyText = text;
            if (busyDepth <= 0 || progressDialog == null || progressDialog.IsDisposed)
                return;
            progressDialog.SetText(text);
            if (total > 0)
                progressDialog.SetValue((int)(current * 100L / total));
            progressDialog.Refresh();
        }

        private void EndBusy()
        {
            if (busyDepth <= 0)
                return;
            if (--busyDepth > 0)
                return;
            HideProgress();
            taskbar.SetProgressState(TaskbarProgressBarState.NoProgress);
        }

        private void ResetForm()
        {
            if (Studio.assetsManager.AssetsFileList.Count > 0)
                Logger.Info("Resetting program...");

            Text = guiTitle;
            StopAnimator();
            Studio.assetsManager.Clear();
            Studio.assemblyLoader.Clear();
            ClearDotNetTab();
            Studio.exportableAssets.Clear();
            Studio.visibleAssets.Clear();
            Studio.l2dModelDict.Clear();
            sceneTreeView.Nodes.Clear();
            assetListView.VirtualListSize = 0;
            assetListView.Items.Clear();
            classesListView.Items.Clear();
            classesListView.Groups.Clear();
            selectedAnimationAssetsList.Clear();
            selectedIndicesPrevList.Clear();
            previewPanel.Image = PreviewPlaceholder();
            previewPanel.SizeMode = PictureBoxSizeMode.CenterImage;
            imageTexture?.Dispose();
            imageTexture = null;
            ResetVideoPreview();
            ClearNoPreviewCache();
            assetInfoLabel.Visible = false;
            assetInfoLabel.Text = null;
            textPreviewBox.Visible = false;
            fontPreviewBox.Visible = false;
            glControl1.Visible = false;
            lastSelectedItem = null;
            sortColumn = -1;
            reverseSort = false;
            listSearch.Text = ""; // colors are owned by the SearchHost theme, don't override
            if (tabControl1.SelectedIndex == 1)
                assetListView.Select();

            var count = filterTypeToolStripMenuItem.DropDownItems.Count;
            for (var i = 1; i < count; i++)
            {
                filterTypeToolStripMenuItem.DropDownItems.RemoveAt(1);
            }

            taskbar.SetProgressState(TaskbarProgressBarState.NoProgress);
            FMODreset();
            UpdateAssetEmptyState();
            UpdateSceneEmptyState();
            UpdateClassesEmptyState();
            UpdateAssetCounts();
        }

        private void tabControl2_SelectedIndexChanged(object sender, EventArgs e)
        {
            switch (tabControl2.SelectedIndex)
            {
                case 0 when enablePreview.Checked: //Preview
                    if (lastPreviewItem != lastSelectedItem)
                    {
                        PreviewAsset(lastSelectedItem);
                        if (displayInfo.Checked && lastSelectedItem?.InfoText != null)
                        {
                            assetInfoLabel.Text = lastSelectedItem.InfoText;
                            assetInfoLabel.Visible = true;
                        }
                    }
                    break;
                case 1: //Dump
                    DumpAsset(lastSelectedItem);
                    break;
            }
        }

        private void assetListView_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && assetListView.SelectedIndices.Count > 0)
            {
                goToSceneHierarchyToolStripMenuItem.Visible = false;
                showOriginalFileToolStripMenuItem.Visible = false;
                exportAnimatorWithSelectedAnimationClipMenuItem.Visible = false;
                exportAsLive2DModelToolStripMenuItem.Visible = false;
                exportL2DWithFadeLstToolStripMenuItem.Visible = false;
                exportL2DWithFadeToolStripMenuItem.Visible = false;
                exportL2DWithClipsToolStripMenuItem.Visible = false;

                if (assetListView.SelectedIndices.Count == 1)
                {
                    // Only offer "Go to scene hierarchy" for assets that actually have a scene
                    // node (Components and the Mesh under a MeshFilter/SkinnedMeshRenderer);
                    // otherwise the item was shown but clicking it did nothing.
                    var single = visibleAssets[assetListView.SelectedIndices[0]];
                    goToSceneHierarchyToolStripMenuItem.Visible = single.TreeNode != null;
                    showOriginalFileToolStripMenuItem.Visible = true;
                }
                if (assetListView.SelectedIndices.Count >= 1)
                {
                    var selectedAssets = GetSelectedAssets();

                    var selectedTypes = (SelectedAssetType)0;
                    foreach (var asset in selectedAssets)
                    {
                        // Switch on the ClassID (which never parses the object) instead of
                        // asset.Asset (which resolves LazyObject placeholders): deciding menu
                        // visibility must not hydrate a whole multi-selection of heavy assets
                        // (Mesh/AnimationClip) and stall the UI on right-click.
                        switch (asset.Type)
                        {
                            case ClassIDType.MonoBehaviour:
                                // Only the Live2D export items need the script name, and only
                                // when the project actually has Cubism models. Resolve (parse)
                                // the MonoBehaviour just in that case, so ordinary projects
                                // never pay for hydration here.
                                if (Studio.l2dModelDict.Count > 0
                                    && asset.Asset is MonoBehaviour m_MonoBehaviour
                                    && m_MonoBehaviour.m_Script.TryGet(out var m_Script))
                                {
                                    if (m_Script.m_ClassName == "CubismMoc")
                                    {
                                        selectedTypes |= SelectedAssetType.MonoBehaviourMoc;
                                    }
                                    else if (m_Script.m_ClassName == "CubismFadeMotionData")
                                    {
                                        selectedTypes |= SelectedAssetType.MonoBehaviourFade;
                                    }
                                    else if (m_Script.m_ClassName == "CubismFadeMotionList")
                                    {
                                        selectedTypes |= SelectedAssetType.MonoBehaviourFadeLst;
                                    }
                                }
                                break;
                            case ClassIDType.AnimationClip:
                                selectedTypes |= SelectedAssetType.AnimationClip;
                                break;
                            case ClassIDType.Animator:
                                selectedTypes |= SelectedAssetType.Animator;
                                break;
                        }
                    }
                    exportAnimatorWithSelectedAnimationClipMenuItem.Visible = (selectedTypes & SelectedAssetType.Animator) != 0 && (selectedTypes & SelectedAssetType.AnimationClip) != 0;
                    exportAsLive2DModelToolStripMenuItem.Visible = (selectedTypes & SelectedAssetType.MonoBehaviourMoc) != 0;
                    exportL2DWithFadeLstToolStripMenuItem.Visible = (selectedTypes & SelectedAssetType.MonoBehaviourMoc) != 0 && (selectedTypes & SelectedAssetType.MonoBehaviourFadeLst) != 0;
                    exportL2DWithFadeToolStripMenuItem.Visible = (selectedTypes & SelectedAssetType.MonoBehaviourMoc) != 0 && (selectedTypes & SelectedAssetType.MonoBehaviourFade) != 0;
                    exportL2DWithClipsToolStripMenuItem.Visible = (selectedTypes & SelectedAssetType.MonoBehaviourMoc) != 0 && (selectedTypes & SelectedAssetType.AnimationClip) != 0;
                }

                var selectedElement = assetListView.HitTest(new Point(e.X, e.Y));
                var subItemIndex = selectedElement.Item.SubItems.IndexOf(selectedElement.SubItem);
                tempClipboard = selectedElement.SubItem.Text;
                copyToolStripMenuItem.Text = $"Copy {assetListView.Columns[subItemIndex].Text}";
                contextMenuStrip1.Show(assetListView, e.X, e.Y);
            }
        }

        private void copyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Clipboard.SetDataObject(tempClipboard);
        }

        private void exportSelectedAssetsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.Selected, ExportType.Convert);
        }

        private void dumpSelectedAssetsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.Selected, ExportType.Dump);
        }

        private void showOriginalFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selectAsset = (AssetItem)assetListView.Items[assetListView.SelectedIndices[0]];
            var args = $"/select, \"{selectAsset.SourceFile.originalPath ?? selectAsset.SourceFile.fullName}\"";
            var pfi = new ProcessStartInfo("explorer.exe", args);
            Process.Start(pfi);
        }

        private void exportAnimatorWithAnimationClipMenuItem_Click(object sender, EventArgs e)
        {
            var selectedAssets = GetSelectedAssets();
            var animator = selectedAssets.FirstOrDefault(x => x.Type == ClassIDType.Animator);
            if (animator == null)
                return;

            var saveFolderDialog = new OpenFolderDialog();
            saveFolderDialog.InitialFolder = saveDirectoryBackup;
            if (saveFolderDialog.ShowDialog(this) == DialogResult.OK)
            {
                saveDirectoryBackup = saveFolderDialog.Folder;
                var exportPath = Path.Combine(saveFolderDialog.Folder, "Animator") + Path.DirectorySeparatorChar;
                ExportAnimatorWithAnimationClip(animator, selectedAnimationAssetsList, exportPath);
            }
        }

        private void exportSelectedObjectsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ExportObjects(false);
        }

        private void exportObjectsWithAnimationClipMenuItem_Click(object sender, EventArgs e)
        {
            ExportObjects(true);
        }

        private void ExportObjects(bool animation)
        {
            if (sceneTreeView.Nodes.Count > 0)
            {
                var saveFolderDialog = new OpenFolderDialog();
                saveFolderDialog.InitialFolder = saveDirectoryBackup;
                if (saveFolderDialog.ShowDialog(this) == DialogResult.OK)
                {
                    saveDirectoryBackup = saveFolderDialog.Folder;
                    var exportPath = Path.Combine(saveFolderDialog.Folder, "GameObject") + Path.DirectorySeparatorChar;
                    List<AssetItem> animationList = null;
                    if (animation && selectedAnimationAssetsList.Count > 0)
                    {
                        animationList = selectedAnimationAssetsList;
                    }
                    ExportObjectsWithAnimationClip(exportPath, sceneTreeView.Nodes, animationList);
                }
            }
            else
            {
                StatusStripUpdate("No Objects available for export");
            }
        }

        private void exportSelectedObjectsMergeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ExportMergeObjects(false);
        }

        private void exportSelectedObjectsMergeWithAnimationClipToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ExportMergeObjects(true);
        }

        private void ExportMergeObjects(bool animation)
        {
            if (sceneTreeView.Nodes.Count > 0)
            {
                var gameObjects = new List<GameObject>();
                GetSelectedParentNode(sceneTreeView.Nodes, gameObjects);
                if (gameObjects.Count > 0)
                {
                    var saveFileDialog = new SaveFileDialog();
                    saveFileDialog.FileName = gameObjects[0].m_Name + " (merge).fbx";
                    saveFileDialog.AddExtension = false;
                    saveFileDialog.Filter = "Fbx file (*.fbx)|*.fbx";
                    saveFileDialog.InitialDirectory = saveDirectoryBackup;
                    if (saveFileDialog.ShowDialog(this) == DialogResult.OK)
                    {
                        saveDirectoryBackup = Path.GetDirectoryName(saveFileDialog.FileName);
                        var exportPath = saveFileDialog.FileName;
                        List<AssetItem> animationList = null;
                        if (animation && selectedAnimationAssetsList.Count > 0)
                        {
                            animationList = selectedAnimationAssetsList;
                        }
                        ExportObjectsMergeWithAnimationClip(exportPath, gameObjects, animationList);
                    }
                }
                else
                {
                    StatusStripUpdate("No Object selected for export.");
                }
            }
        }

        private void goToSceneHierarchyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (assetListView.SelectedIndices.Count == 0)
                return;
            var selectAsset = visibleAssets[assetListView.SelectedIndices[0]];
            if (selectAsset.TreeNode == null)
                return;
            tabControl1.SelectedTab = tabPage1; // switch to the Scene Hierarchy tab first
            sceneTreeView.SelectedNode = selectAsset.TreeNode;
            selectAsset.TreeNode.EnsureVisible(); // expand ancestors and scroll into view
            sceneTreeView.Focus();
        }

        private void exportAllAssetsMenuItem_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.All, ExportType.Convert);
        }

        private void exportSelectedAssetsMenuItem_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.Selected, ExportType.Convert);
        }

        private void exportFilteredAssetsMenuItem_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.Filtered, ExportType.Convert);
        }

        private void toolStripMenuItem4_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.All, ExportType.Raw);
        }

        private void toolStripMenuItem5_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.Selected, ExportType.Raw);
        }

        private void toolStripMenuItem6_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.Filtered, ExportType.Raw);
        }

        private void toolStripMenuItem7_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.All, ExportType.Dump);
        }

        private void toolStripMenuItem8_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.Selected, ExportType.Dump);
        }

        private void toolStripMenuItem9_Click(object sender, EventArgs e)
        {
            ExportAssets(ExportFilter.Filtered, ExportType.Dump);
        }

        private void toolStripMenuItem11_Click(object sender, EventArgs e)
        {
            ExportAssetsList(ExportFilter.All);
        }

        private void toolStripMenuItem12_Click(object sender, EventArgs e)
        {
            ExportAssetsList(ExportFilter.Selected);
        }

        private void toolStripMenuItem13_Click(object sender, EventArgs e)
        {
            ExportAssetsList(ExportFilter.Filtered);
        }

        private void exportAllObjectsSplitToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            if (sceneTreeView.Nodes.Count > 0)
            {
                var saveFolderDialog = new OpenFolderDialog();
                saveFolderDialog.InitialFolder = saveDirectoryBackup;
                if (saveFolderDialog.ShowDialog(this) == DialogResult.OK)
                {
                    saveDirectoryBackup = saveFolderDialog.Folder;
                    var savePath = saveFolderDialog.Folder + Path.DirectorySeparatorChar;
                    ExportSplitObjects(savePath, sceneTreeView.Nodes);
                }
            }
            else
            {
                StatusStripUpdate("No Objects available for export");
            }
        }

        private void assetListView_SelectedIndexChanged(object sender, EventArgs e)
        {
            ProcessSelectedItems();
        }

        private void assetListView_VirtualItemsSelectionRangeChanged(object sender, ListViewVirtualItemsSelectionRangeChangedEventArgs e)
        {
            ProcessSelectedItems();
        }

        private void ProcessSelectedItems()
        {
            if (assetListView.SelectedIndices.Count > 1)
            {
                StatusStripUpdate($"Selected {assetListView.SelectedIndices.Count} assets.");
            }
            UpdateAssetCounts();

            var selectedIndicesList = assetListView.SelectedIndices.Cast<int>().ToList();

            var addedIndices = selectedIndicesList.Except(selectedIndicesPrevList).ToArray();
            foreach (var itemIndex in addedIndices)
            {
                selectedIndicesPrevList.Add(itemIndex);
                var selectedItem = (AssetItem)assetListView.Items[itemIndex];
                if (selectedItem.Type == ClassIDType.AnimationClip)
                {
                    selectedAnimationAssetsList.Add(selectedItem);
                }
            }

            var removedIndices = selectedIndicesPrevList.Except(selectedIndicesList).ToArray();
            foreach (var itemIndex in removedIndices)
            {
                selectedIndicesPrevList.Remove(itemIndex);
                var unselectedItem = (AssetItem)assetListView.Items[itemIndex];
                if (unselectedItem.Type == ClassIDType.AnimationClip)
                {
                    selectedAnimationAssetsList.Remove(unselectedItem);
                }
            }
        }

        private List<AssetItem> GetSelectedAssets()
        {
            var selectedAssets = new List<AssetItem>(assetListView.SelectedIndices.Count);
            foreach (int index in assetListView.SelectedIndices)
            {
                selectedAssets.Add((AssetItem)assetListView.Items[index]);
            }

            return selectedAssets;
        }

        private void FilterAssetList()
        {
            if (exportableAssets.Count < 1)
                return;

            assetListView.BeginUpdate();
            assetListView.SelectedIndices.Clear();
            var show = new List<ClassIDType>();
            var filterMoc = false;
            if (!allToolStripMenuItem.Checked)
            {
                for (var i = 1; i < filterTypeToolStripMenuItem.DropDownItems.Count; i++)
                {
                    var item = (ToolStripMenuItem)filterTypeToolStripMenuItem.DropDownItems[i];
                    if (item.Checked)
                    {
                        if (item.Name == "MonoBehaviour (Live2D Model)")
                            filterMoc = true;
                        else
                            show.Add((ClassIDType)Enum.Parse(typeof(ClassIDType), item.Text));
                    }
                }
                visibleAssets = filterMoc
                    ? exportableAssets.FindAll(x => (x.RawAsset is MonoBehaviour monoBehaviour && l2dModelDict.ContainsKey(monoBehaviour)) || show.Contains(x.Type))
                    : exportableAssets.FindAll(x => show.Contains(x.Type));
            }
            else
            {
                visibleAssets = exportableAssets;
            }

            if (listSearch.Text.Length > 0)
            {
                var term = listSearch.Text;
                var useRegex = searchRegexToggle?.Checked == true;
                var useContent = searchContentToggle?.Checked == true;
                var exclude = searchExcludeToggle?.Checked == true;
                Predicate<AssetItem> matches = null;

                if (useRegex)
                {
                    try
                    {
                        var rx = new Regex(term, RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(2));
                        matches = x => rx.IsMatch(x.Text) || rx.IsMatch(x.Container)
                            || (useContent && Studio.GetSearchableContent(x) is string c && rx.IsMatch(c));
                        StatusStripUpdate("");
                    }
                    catch (ArgumentException ex)
                    {
                        StatusStripUpdate($"Regex error: {ex.Message}");
                    }
                }
                else
                {
                    var lower = term.ToLowerInvariant();
                    matches = x =>
                        x.Text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                        || x.Container.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                        || x.PathIdText.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0
                        || (useContent && Studio.GetSearchableContent(x)?.IndexOf(lower, StringComparison.Ordinal) >= 0);
                }

                if (matches != null)
                {
                    if (useContent)
                        StatusStripUpdate("Searching contents...");
                    var predicate = matches;
                    visibleAssets = visibleAssets.FindAll(x => exclude ? !predicate(x) : predicate(x));
                    if (useContent)
                        StatusStripUpdate($"Found {visibleAssets.Count} asset(s) matching \"{term}\".");
                }
            }
            assetListView.VirtualListSize = visibleAssets.Count;
            assetListView.EndUpdate();
            UpdateAssetEmptyState();
            UpdateAssetCounts();
        }

        private void ExportAssets(ExportFilter type, ExportType exportType)
        {
            if (exportableAssets.Count > 0)
            {
                var saveFolderDialog = new OpenFolderDialog();
                saveFolderDialog.InitialFolder = saveDirectoryBackup;
                if (saveFolderDialog.ShowDialog(this) == DialogResult.OK)
                {
                    timer.Stop();
                    saveDirectoryBackup = saveFolderDialog.Folder;
                    List<AssetItem> toExportAssets = null;
                    switch (type)
                    {
                        case ExportFilter.All:
                            toExportAssets = exportableAssets;
                            break;
                        case ExportFilter.Selected:
                            toExportAssets = GetSelectedAssets();
                            break;
                        case ExportFilter.Filtered:
                            toExportAssets = visibleAssets;
                            break;
                    }

                    if (toExportAssets != null && filterTypeToolStripMenuItem.DropDownItems.ContainsKey("Texture2DArray"))
                    {
                        var tex2dArrayImgPathIdSet = toExportAssets.FindAll(x => x.Type == ClassIDType.Texture2DArrayImage).Select(x => x.m_PathID).ToHashSet();
                        foreach (var pathId in tex2dArrayImgPathIdSet)
                        {
                            toExportAssets = toExportAssets.Where(x =>
                                x.Type != ClassIDType.Texture2DArray
                                || (x.Type == ClassIDType.Texture2DArray && x.m_PathID != pathId))
                                .ToList();
                        }
                    }
                    Studio.ExportAssets(saveFolderDialog.Folder, toExportAssets, exportType);
                }
            }
            else
            {
                StatusStripUpdate("No exportable assets loaded");
            }
        }

        private void ExportAssetsList(ExportFilter type)
        {
            // XXX: Only exporting as XML for now, but would JSON(/CSV/other) be useful too?

            if (exportableAssets.Count > 0)
            {
                var saveFolderDialog = new OpenFolderDialog();
                saveFolderDialog.InitialFolder = saveDirectoryBackup;
                if (saveFolderDialog.ShowDialog(this) == DialogResult.OK)
                {
                    timer.Stop();
                    saveDirectoryBackup = saveFolderDialog.Folder;
                    List<AssetItem> toExportAssets = null;
                    switch (type)
                    {
                        case ExportFilter.All:
                            toExportAssets = exportableAssets;
                            break;
                        case ExportFilter.Selected:
                            toExportAssets = GetSelectedAssets();
                            break;
                        case ExportFilter.Filtered:
                            toExportAssets = visibleAssets;
                            break;
                    }
                    Studio.ExportAssetsList(saveFolderDialog.Folder, toExportAssets, ExportListType.XML);
                }
            }
            else
            {
                StatusStripUpdate("No exportable assets loaded");
            }
        }

        private void toolStripMenuItem15_Click(object sender, EventArgs e)
        {
            GUILogger.ShowDebugMessage = toolStripMenuItem15.Checked;
        }

        private void sceneTreeView_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                sceneTreeView.SelectedNode = e.Node;
                sceneContextMenuStrip.Show(sceneTreeView, e.Location.X, e.Location.Y);
            }
        }

        private void selectAllToolStripMenuItem_Click(object sender, EventArgs e)
        {
            foreach (TreeNode node in sceneTreeView.Nodes)
            {
                node.Checked = true;
            }
        }

        private void clearSelectionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            treeRecursionEnabled = false;
            for (var i = 0; i < treeNodeSelectedList.Count; i++)
            {
                treeNodeSelectedList[i].Checked = false;
            }
            treeRecursionEnabled = true;
            treeNodeSelectedList.Clear();
            StatusStripUpdate($"Selected {treeNodeSelectedList.Count} object(s).");
        }

        private void expandAllToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (sceneTreeView.Nodes.Count > 500)
            {
                MessageBox.Show("Too many elements.");
                return;
            }

            sceneTreeView.BeginUpdate();
            foreach (TreeNode node in sceneTreeView.Nodes)
            {
                node.ExpandAll();
            }
            sceneTreeView.EndUpdate();
        }

        private void collapseAllToolStripMenuItem_Click(object sender, EventArgs e)
        {
            sceneTreeView.BeginUpdate();
            foreach (TreeNode node in sceneTreeView.Nodes)
            {
                node.Collapse(ignoreChildren: false);
            }
            sceneTreeView.EndUpdate();
        }

        private void listSearchFilterMode_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (listSearch.Text.Length > 0)
            {
                FilterAssetList();
            }
        }

        private void listSearchHistory_SelectedIndexChanged(object sender, EventArgs e)
        {
            listSearch.Text = listSearchHistory.Text;
            listSearch.Focus();
            listSearch.SelectionStart = listSearch.Text.Length;
        }

        private void selectRelatedAsset(object sender, EventArgs e)
        {
            var selectedItem = (ToolStripMenuItem)sender;
            var index = int.Parse(selectedItem.Name.Split('_')[0]);

            assetListView.SelectedIndices.Clear();
            tabControl1.SelectedTab = tabPage2;
            var assetItem = assetListView.Items[index];
            assetItem.Selected = true;
            assetItem.EnsureVisible();
        }

        private void selectAllRelatedAssets(object sender, EventArgs e)
        {
            var selectedNode = sceneTreeView.SelectedNode;
            var relatedAssets = visibleAssets.FindAll(x => x.TreeNode == selectedNode);
            if (relatedAssets.Count > 0)
            {
                assetListView.SelectedIndices.Clear();
                tabControl1.SelectedTab = tabPage2;
                foreach (var asset in relatedAssets)
                {
                    var assetItem = assetListView.Items[assetListView.Items.IndexOf(asset)];
                    assetItem.Selected = true;
                }
                assetListView.Items[assetListView.Items.IndexOf(relatedAssets[0])].EnsureVisible();
            }
        }

        private void showRelatedAssetsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var selectedNode = sceneTreeView.SelectedNode;
            var relatedAssets = visibleAssets.FindAll(x => x.TreeNode == selectedNode);
            if (relatedAssets.Count == 0)
            {
                StatusStripUpdate("No related assets were found among the visible assets.");
            }
        }

        private void contextMenuStrip2_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            var selectedNode = sceneTreeView.SelectedNode;
            var relatedAssets = visibleAssets.FindAll(x => x.TreeNode == selectedNode);
            shShowRelatedAssetsToolStripMenuItem.DropDownItems.Clear();
            if (relatedAssets.Count > 1)
            {
                var assetItem = new ToolStripMenuItem
                {
                    CheckOnClick = false,
                    Name = "selectAllRelatedAssetsToolStripMenuItem",
                    Size = new Size(180, 22),
                    Text = "Select all"
                };
                assetItem.Click += selectAllRelatedAssets;
                shShowRelatedAssetsToolStripMenuItem.DropDownItems.Add(assetItem);
            }
            foreach (var asset in relatedAssets)
            {
                var index = assetListView.Items.IndexOf(asset);
                var assetItem = new ToolStripMenuItem
                {
                    CheckOnClick = false,
                    Name = $"{index}_{asset.TypeString}",
                    Size = new Size(180, 22),
                    Text = $"({asset.TypeString}) {asset.Text}"
                };
                assetItem.Click += selectRelatedAsset;
                shShowRelatedAssetsToolStripMenuItem.DropDownItems.Add(assetItem);
            }
        }

        private void showConsoleToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var showConsole = showConsoleToolStripMenuItem.Checked;
            if (showConsole)
                ConsoleWindow.ShowConsoleWindow();
            else
                ConsoleWindow.HideConsoleWindow();

            Properties.Settings.Default.showConsole = showConsole;
            Properties.Settings.Default.Save();
        }

        private void writeLogToFileToolStripMenuItem_CheckedChanged(object sender, EventArgs e)
        {
            var useFileLogger = writeLogToFileToolStripMenuItem.Checked;
            logger.UseFileLogger = useFileLogger;

            Properties.Settings.Default.useFileLogger = useFileLogger;
            Properties.Settings.Default.Save();
        }

        private void UnityRiftGUIForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            Logger.Verbose("Closing UnityRift");
            // Release the long-lived GDI objects we own (the OS would reclaim them at exit,
            // but be explicit so handle-leak tooling stays quiet).
            ClearNoPreviewCache();
            ResetVideoPreview();
            videoView?.Dispose();
            imageTexture?.Dispose();
            previewPlaceholder?.Dispose();
            dotnetPlaceholder?.Dispose();
            foreach (var d in new IDisposable[] { brRowEven, brRowOdd, brRowSelected, brRowHover,
                                                   brHeader, brHeaderPressed, pnRowSeparator, pnHeaderLine })
                d?.Dispose();
        }

        private void buildTreeStructureToolStripMenuItem_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.buildTreeStructure = buildTreeStructureToolStripMenuItem.Checked;
            Properties.Settings.Default.Save();
        }

        private void exportAllL2D_Click(object sender, EventArgs e)
        {
            if (exportableAssets.Count > 0)
            {
                if (Studio.l2dModelDict.Count == 0)
                {
                    Logger.Info("Live2D Cubism models were not found.");
                    return;
                }
                Live2DExporter();
            }
            else
            {
                Logger.Info("No exportable assets loaded");
            }
        }

        private void exportSelectedL2D_Click(object sender, EventArgs e)
        {
            ExportSelectedL2DModels(ExportL2DFilter.Selected);
        }

        private void exportSelectedL2DWithClips_Click(object sender, EventArgs e)
        {
            ExportSelectedL2DModels(ExportL2DFilter.SelectedWithClips);
        }

        private void exportSelectedL2DWithFadeMotions_Click(object sender, EventArgs e)
        {
            ExportSelectedL2DModels(ExportL2DFilter.SelectedWithFade);
        }

        private void exportSelectedL2DWithFadeList_Click(object sender, EventArgs e)
        {
            ExportSelectedL2DModels(ExportL2DFilter.SelectedWithFadeList);
        }

        private void ExportSelectedL2DModels(ExportL2DFilter l2dExportMode)
        {
            if (Studio.exportableAssets.Count == 0)
            {
                Logger.Info("No exportable assets loaded");
                return;
            }
            if (Studio.l2dModelDict.Count == 0)
            {
                Logger.Info("Live2D Cubism models were not found.");
                return;
            }
            var selectedAssets = GetSelectedAssets();
            if (selectedAssets.Count == 0)
                return;

            MonoBehaviour selectedFadeLst = null;
            var selectedMocs = new List<MonoBehaviour>();
            var selectedFadeMotions = new List<MonoBehaviour>();
            var selectedClips = new List<AnimationClip>();
            foreach (var assetItem in selectedAssets)
            {
                switch (assetItem.Asset)
                {
                    case MonoBehaviour m_MonoBehaviour when m_MonoBehaviour.m_Script.TryGet(out var m_Script):
                        switch (m_Script.m_ClassName)
                        {
                            case "CubismMoc":
                                selectedMocs.Add(m_MonoBehaviour);
                                break;
                            case "CubismFadeMotionData":
                                selectedFadeMotions.Add(m_MonoBehaviour);
                                break;
                            case "CubismFadeMotionList":
                                selectedFadeLst = m_MonoBehaviour;
                                break;
                        }
                        break;
                    case AnimationClip m_AnimationClip:
                        selectedClips.Add(m_AnimationClip);
                        break;
                }
            }
            if (selectedMocs.Count == 0)
            {
                Logger.Info("Live2D Cubism models were not selected.");
                return;
            }

            switch (l2dExportMode)
            {
                case ExportL2DFilter.Selected:
                    Live2DExporter(selectedMocs);
                    break;
                case ExportL2DFilter.SelectedWithFadeList:
                    if (selectedFadeLst == null)
                    {
                        Logger.Info("Fade Motion List was not selected.");
                        return;
                    }
                    Live2DExporter(selectedMocs, selFadeLst: selectedFadeLst);
                    break;
                case ExportL2DFilter.SelectedWithFade:
                    if (selectedFadeMotions.Count == 0)
                    {
                        Logger.Info("No Fade motions were selected.");
                        return;
                    }
                    Live2DExporter(selectedMocs, selFadeMotions: selectedFadeMotions);
                    break;
                case ExportL2DFilter.SelectedWithClips:
                    if (selectedClips.Count == 0)
                    {
                        Logger.Info("No AnimationClips were selected.");
                        return;
                    }
                    Live2DExporter(selectedMocs, selectedClips);
                    break;
            }
        }

        private void Live2DExporter(List<MonoBehaviour> selMocs = null, List<AnimationClip> selClipMotions = null, List<MonoBehaviour> selFadeMotions = null, MonoBehaviour selFadeLst = null)
        {
            var saveFolderDialog = new OpenFolderDialog();
            saveFolderDialog.InitialFolder = saveDirectoryBackup;
            if (saveFolderDialog.ShowDialog(this) == DialogResult.OK)
            {
                timer.Stop();
                saveDirectoryBackup = saveFolderDialog.Folder;
                Progress.Reset();
                ShowProgressMarquee("Exporting Live2D…");

                Studio.ExportLive2D(saveFolderDialog.Folder, selMocs, selClipMotions, selFadeMotions, selFadeLst);
                HideProgress();
            }
        }

        private void importOptions_DropDownClose(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(specifyUnityVersionTextBox.Text))
            {
                assetsManager.Options.CustomUnityVersion = null;
                return;
            }

            try
            {
                assetsManager.Options.CustomUnityVersion = new UnityVersion(specifyUnityVersionTextBox.Text);
            }
            catch (Exception ex)
            {
                Logger.Error(ex.Message);
            }
        }

        private void importOptions_DropDownOpened(object sender, EventArgs e)
        {
            if (assetsManager.Options.CustomUnityVersion != null)
            {
                specifyUnityVersionTextBox.Text = assetsManager.Options.CustomUnityVersion.FullVersion;
            }
            alwaysDecompressToDiskToolStripMenuItem.Checked = assetsManager.Options.BundleOptions.DecompressToDisk;
            customBlockInfoCompressionComboBox.SelectedIndex = SetComboBoxIndex(assetsManager.Options.BundleOptions.CustomBlockInfoCompression);
            customBlockCompressionComboBox.SelectedIndex = SetComboBoxIndex(assetsManager.Options.BundleOptions.CustomBlockCompression);
        }

        private static int SetComboBoxIndex(CompressionType compressionType)
        {
            switch (compressionType)
            {
                case CompressionType.Auto: return 0;
                case CompressionType.Lzma: return 4;
                case CompressionType.Lz4:
                case CompressionType.Lz4HC: return 3;
                case CompressionType.Zstd:  return 1;
                case CompressionType.Oodle: return 2;
                default: throw new NotSupportedException();
            }
        }

        private void customBlockCompressionComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            var selectedTypeIndex = customBlockCompressionComboBox.SelectedIndex;
            assetsManager.Options.BundleOptions.CustomBlockCompression = GetCustomCompressionTypes(selectedTypeIndex);
        }

        private void customBlockInfoCompressionComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            var selectedTypeIndex = customBlockInfoCompressionComboBox.SelectedIndex;
            assetsManager.Options.BundleOptions.CustomBlockInfoCompression = GetCustomCompressionTypes(selectedTypeIndex);
        }

        private static CompressionType GetCustomCompressionTypes(int index)
        {
            switch (index)
            {
                case 0: return CompressionType.Auto;
                case 1: return CompressionType.Zstd;
                case 2: return CompressionType.Oodle;
                case 3: return CompressionType.Lz4HC;
                case 4: return CompressionType.Lzma;
                default: throw new NotSupportedException();
            }
        }

        private void alwaysDecompressToDiskToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var isEnabled = alwaysDecompressToDiskToolStripMenuItem.Checked;
            assetsManager.Options.BundleOptions.DecompressToDisk = isEnabled;
            Properties.Settings.Default.decompressToDisk = isEnabled;
            Properties.Settings.Default.Save();
        }

        private void saveOptionsToDiskToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var saveFolderDialog = new OpenFolderDialog();
            saveFolderDialog.Title = "Select the save folder";
            if (saveFolderDialog.ShowDialog(this) == DialogResult.OK)
            {
                var savePath = saveFolderDialog.Folder;
                assetsManager.Options.SaveToFile(savePath);
            }
        }

        private void useAssetLoadingViaTypetreeToolStripMenuItem_CheckedChanged(object sender, EventArgs e)
        {
            var isEnabled = useAssetLoadingViaTypetreeToolStripMenuItem.Checked;
            assetsManager.LoadViaTypeTree = isEnabled;
            Properties.Settings.Default.useTypetreeLoading = isEnabled;
            Properties.Settings.Default.Save();
        }

        private void ApplyColorTheme(out bool isDarkMode)
        {
            isDarkMode = false;
            if (SystemInformation.HighContrast)
                return;

#if NET9_0_OR_GREATER
#pragma warning disable WFO5001 //for evaluation purposes only
            var currentTheme = Properties.Settings.Default.guiColorTheme;
            colorThemeToolStripMenu.Visible = true;
            try
            {
                switch (currentTheme)
                {
                    case GuiColorTheme.System:
                        Application.SetColorMode(SystemColorMode.System);
                        colorThemeAutoToolStripMenuItem.Checked = true;
                        isDarkMode = Application.IsDarkModeEnabled;
                        break;
                    case GuiColorTheme.Light:
                        colorThemeLightToolStripMenuItem.Checked = true;
                        break;
                    case GuiColorTheme.Dark:
                        Application.SetColorMode(SystemColorMode.Dark);
                        colorThemeDarkToolStripMenuItem.Checked = true;
                        isDarkMode = true;
                        break;
                }
            }
            catch (Exception)
            {
                //skip
            }
#pragma warning restore WFO5001
#endif
            if (isDarkMode)
            {
                assetListView.GridLines = false;
            }
            else
            {
                FMODloopButton.UseVisualStyleBackColor = true;
            }
        }

        // ----------------------------------------------------------------- toolbar

        // Builds a small icon toolbar for the most-used actions and docks it just
        // below the menu. Buttons reuse the existing menu Click handlers, so behavior
        // stays in one place. Icons are drawn with GDI (theme-aware, no image assets).
        // Each toolbar button + its icon factory, so icons can be re-tinted on theme switch.
        private readonly List<(ToolStripButton btn, Func<System.Drawing.Color, Bitmap> icon)> toolbarIcons = new List<(ToolStripButton, Func<System.Drawing.Color, Bitmap>)>();
        private ToolStripButton themeToggleButton;

        private void InitToolbar()
        {
            toolStripMain = new ToolStrip
            {
                Dock = DockStyle.Top,
                GripStyle = ToolStripGripStyle.Hidden,
                ImageScalingSize = new Size(16, 16),
                Padding = new Padding(4, 2, 4, 2),
            };

            ToolStripButton Button(string tip, Func<System.Drawing.Color, Bitmap> icon, EventHandler onClick)
            {
                var b = new ToolStripButton
                {
                    DisplayStyle = ToolStripItemDisplayStyle.Image,
                    ToolTipText = tip,
                    ImageScaling = ToolStripItemImageScaling.None,
                    AutoSize = false,
                    Size = new Size(28, 24),
                };
                b.Click += onClick;
                toolbarIcons.Add((b, icon));
                return b;
            }

            toolStripMain.Items.AddRange(new ToolStripItem[]
            {
                Button("Load file", IconLoadFile, loadFile_Click),
                Button("Load folder", IconLoadFolder, loadFolder_Click),
                new ToolStripSeparator(),
                Button("Export all assets", IconExportAll, exportAllAssetsMenuItem_Click),
                Button("Export selected assets", IconExportSelected, exportSelectedAssetsMenuItem_Click),
                Button("Export filtered assets", IconFilter, exportFilteredAssetsMenuItem_Click),
                new ToolStripSeparator(),
                Button("Export options", IconOptions, showExpOpt_Click),
            });

            // Right-aligned sun/moon toggle that flips light/dark in one click.
            themeToggleButton = new ToolStripButton
            {
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                ImageScaling = ToolStripItemImageScaling.None,
                Alignment = ToolStripItemAlignment.Right,
                AutoSize = false,
                Size = new Size(28, 24),
            };
            themeToggleButton.Click += (s, e) => SwitchTheme(isDarkMode ? GuiColorTheme.Light : GuiColorTheme.Dark);
            toolStripMain.Items.Add(themeToggleButton);

            // The toggle replaces the "Color Theme" dropdown menu.
            colorThemeToolStripMenu.Visible = false;

            // Insert below the menu: final Controls order must be
            // [content, toolbar, menuStrip] so the menu stays on top.
            Controls.Add(toolStripMain);
            Controls.SetChildIndex(toolStripMain, 1);
        }

        // --------------------------------------------------------------- icon factory

        private static Bitmap MakeIcon(Action<Graphics> draw)
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                draw(g);
            }
            return bmp;
        }

        private static Bitmap IconLoadFile(System.Drawing.Color c) => MakeIcon(g =>
        {
            using var pen = new Pen(c, 1.4f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
            var page = new[] { new Point(4, 2), new Point(10, 2), new Point(12, 4), new Point(12, 14), new Point(4, 14) };
            g.DrawPolygon(pen, page);
            g.DrawLines(pen, new[] { new Point(10, 2), new Point(10, 4), new Point(12, 4) }); // folded corner
        });

        private static Bitmap IconLoadFolder(System.Drawing.Color c) => MakeIcon(g =>
        {
            using var pen = new Pen(c, 1.4f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
            var folder = new[] { new Point(2, 6), new Point(6, 6), new Point(7, 4), new Point(11, 4), new Point(11, 6), new Point(14, 6), new Point(14, 13), new Point(2, 13) };
            g.DrawPolygon(pen, folder);
        });

        private static Bitmap IconExportSelected(System.Drawing.Color c) => MakeIcon(g =>
        {
            using var pen = new Pen(c, 1.4f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
            g.DrawLine(pen, 3, 13, 13, 13);                       // baseline (out)
            g.DrawLine(pen, 8, 3, 8, 10);                         // shaft
            g.DrawLines(pen, new[] { new Point(5, 6), new Point(8, 3), new Point(11, 6) }); // arrow head
        });

        private static Bitmap IconExportAll(System.Drawing.Color c) => MakeIcon(g =>
        {
            using var pen = new Pen(c, 1.4f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
            g.DrawRectangle(pen, 2, 2, 11, 11);                   // box = everything
            g.DrawLine(pen, 7, 11, 7, 6);
            g.DrawLines(pen, new[] { new Point(5, 8), new Point(7, 6), new Point(9, 8) });
        });

        private static Bitmap IconFilter(System.Drawing.Color c) => MakeIcon(g =>
        {
            using var pen = new Pen(c, 1.4f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
            var funnel = new[] { new Point(3, 3), new Point(13, 3), new Point(9, 8), new Point(9, 13), new Point(7, 11), new Point(7, 8) };
            g.DrawPolygon(pen, funnel);
        });

        private static Bitmap IconOptions(System.Drawing.Color c) => MakeIcon(g =>
        {
            using var pen = new Pen(c, 1.4f);
            using var fill = new SolidBrush(c);
            int[] ys = { 4, 8, 12 };
            int[] knobs = { 11, 5, 9 };
            for (int i = 0; i < 3; i++)
            {
                g.DrawLine(pen, 2, ys[i], 14, ys[i]);
                g.FillEllipse(fill, knobs[i] - 2, ys[i] - 2, 4, 4);
            }
        });

        private static Bitmap IconSun(System.Drawing.Color c) => MakeIcon(g =>
        {
            using var pen = new Pen(c, 1.4f);
            g.DrawEllipse(pen, 5.5f, 5.5f, 5f, 5f); // body, centered on (8,8)
            for (int k = 0; k < 8; k++)
            {
                double a = k * Math.PI / 4;
                g.DrawLine(pen,
                    (float)(8 + Math.Cos(a) * 6), (float)(8 + Math.Sin(a) * 6),
                    (float)(8 + Math.Cos(a) * 7.6), (float)(8 + Math.Sin(a) * 7.6));
            }
        });

        private static Bitmap IconMoon(System.Drawing.Color c) => MakeIcon(g =>
        {
            using var fill = new SolidBrush(c);
            g.FillEllipse(fill, 3, 2, 11, 11);          // full disc
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            using var clear = new SolidBrush(System.Drawing.Color.Transparent);
            g.FillEllipse(clear, 6, 1, 11, 11);         // carve a crescent
        });

        // ------------------------------------------------------------ dark-theme polish

        // Managed colors for surfaces the framework's dark mode does not fully cover
        // (list/tree bodies and the preview panels). Native tweaks (dark title bar and
        // dark scrollbars) need window handles and are applied in OnLoad.
        // Layered dark palette: content areas sit DARKER than the surrounding chrome, so
        // edges read as depth/contrast instead of the white borders they replaced.
        private static readonly System.Drawing.Color DarkContent = System.Drawing.Color.FromArgb(24, 24, 24);  // lists, trees, text, preview
        private static readonly System.Drawing.Color DarkChrome = System.Drawing.Color.FromArgb(45, 45, 45);   // panels, tab pages, containers
        private static readonly System.Drawing.Color DarkField = System.Drawing.Color.FromArgb(60, 60, 60);    // inputs, combos
        private static readonly System.Drawing.Color DarkSeparator = System.Drawing.Color.FromArgb(70, 70, 72);// splitter / visible dividers
        private static readonly System.Drawing.Color DarkFore = System.Drawing.Color.FromArgb(220, 220, 220);

        // Explicit light palette (NOT SystemColors: the .NET color mode remaps SystemColors
        // asynchronously, so reading them right after a live switch returns stale values).
        private static readonly System.Drawing.Color LightContent = System.Drawing.Color.White;
        private static readonly System.Drawing.Color LightChrome = System.Drawing.Color.FromArgb(240, 240, 240);
        private static readonly System.Drawing.Color LightField = System.Drawing.Color.White;
        private static readonly System.Drawing.Color LightSeparator = System.Drawing.Color.FromArgb(200, 200, 200);
        private static readonly System.Drawing.Color LightFore = System.Drawing.Color.FromArgb(20, 20, 20);
        private static readonly System.Drawing.Color LightGrayText = System.Drawing.Color.FromArgb(110, 110, 110);

        // Widen a ListView's right-most column to consume any leftover width, so the
        // area past the last real column isn't shown as a blank extra column.
        private static void FillLastColumn(ListView lv)
        {
            if (lv.Columns.Count == 0)
                return;
            ColumnHeader last = null;
            var maxDisplay = -1;
            foreach (ColumnHeader ch in lv.Columns)
                if (ch.DisplayIndex > maxDisplay) { maxDisplay = ch.DisplayIndex; last = ch; }
            var used = 0;
            foreach (ColumnHeader ch in lv.Columns)
                if (ch != last) used += ch.Width;
            var remaining = lv.ClientSize.Width - used;
            // Only touch the width when it actually changes: setting it forces a full
            // relayout + repaint of the list even when the value is identical.
            if (remaining > 40 && last.Width != remaining)
                last.Width = remaining;
        }

        // Slightly larger UI text for comfortable reading at 100% scaling (menu/toolbar
        // are left as-is per design). The JSON/dump panels get a larger monospace font.
        private void ApplyUiFonts()
        {
            var ui = new System.Drawing.Font("Segoe UI", 10F);
            var tabFont = new System.Drawing.Font("Segoe UI", 10.5F);
            var mono = new System.Drawing.Font("Consolas", 11F);
            foreach (Control c in new Control[] { assetListView, classesListView,
                                                  treeSearch, listSearch, sceneTreeView, dumpTreeView })
            {
                if (c != null) c.Font = ui;
            }
            foreach (var tc in new[] { tabControl1, tabControl2 })
            {
                tc.Font = tabFont;
                tc.Padding = new System.Drawing.Point(18, 8); // taller tabs (x=horizontal, y=vertical)
                tc.ItemSize = new System.Drawing.Size(0, 30);
            }
            foreach (Control c in new Control[] { dumpTextBox, classTextBox, textPreviewBox })
            {
                if (c != null) c.Font = mono;
            }
        }

        // --- Empty-state message for the Asset List ---
        private Label assetEmptyLabel;

        private void InitAssetEmptyState()
        {
            assetEmptyLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Font = new System.Drawing.Font("Segoe UI", 11F),
                ForeColor = System.Drawing.Color.FromArgb(150, 150, 150),
                BackColor = assetListView.BackColor,
                Visible = false,
            };
            tabPage2.Controls.Add(assetEmptyLabel);
            assetEmptyLabel.BringToFront();
            UpdateAssetEmptyState();

            var tip = new ToolTip { AutoPopDelay = 8000, InitialDelay = 400 };
            tip.SetToolTip(listSearch, "Search assets by Name, Container or Path ID  (Ctrl+F)");
            tip.SetToolTip(listSearchFilterMode, "Match filter: Include, Exclude, Regex, or Include (+ content)");
        }

        // Empty-state overlays for the Scene Hierarchy and .NET Classes trees, built the
        // same way as the Asset List one (a Dock.Fill label brought to the front of the page).
        private Label sceneEmptyLabel;
        private Label dotnetEmptyLabel;
        private Label classesEmptyLabel;

        private Label MakeEmptyLabel(Control page, Control over)
        {
            var label = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Font = new System.Drawing.Font("Segoe UI", 11F),
                ForeColor = System.Drawing.Color.FromArgb(150, 150, 150),
                BackColor = over.BackColor,
                Visible = false,
            };
            page.Controls.Add(label);
            label.BringToFront();
            return label;
        }

        private void UpdateSceneEmptyState()
        {
            if (sceneEmptyLabel == null)
                sceneEmptyLabel = MakeEmptyLabel(tabPage1, sceneTreeView);
            var empty = sceneTreeView.Nodes.Count == 0;
            sceneEmptyLabel.Text = exportableAssets.Count == 0
                ? "No scene loaded\r\n\r\nOpen a file or folder to see its GameObject hierarchy"
                : "No GameObjects in the loaded files\r\n\r\nEnable Options \u2192 Build tree structure, then reload";
            sceneEmptyLabel.BackColor = sceneTreeView.BackColor;
            sceneEmptyLabel.Visible = empty;
            if (empty) sceneEmptyLabel.BringToFront();
        }

        private void UpdateClassesEmptyState()
        {
            if (classesEmptyLabel == null)
                classesEmptyLabel = MakeEmptyLabel(tabPage3, classesListView);
            var empty = classesListView.Items.Count == 0;
            classesEmptyLabel.Text = exportableAssets.Count == 0
                ? "No classes loaded\r\n\r\nOpen a file or folder to see its serialized type classes"
                : "No serialized type classes in the loaded files";
            classesEmptyLabel.BackColor = classesListView.BackColor;
            classesEmptyLabel.Visible = empty;
            if (empty) classesEmptyLabel.BringToFront();
        }

        private void UpdateDotNetEmptyState(string loadingText = null)
        {
            if (dotnetTreeView == null)
                return;
            if (dotnetEmptyLabel == null)
                dotnetEmptyLabel = MakeEmptyLabel(dotnetTabPage, dotnetTreeView);
            var empty = dotnetTreeView.Nodes.Count == 0;
            if (loadingText != null)
                dotnetEmptyLabel.Text = loadingText;
            else if (assemblyLoader.Modules.Count == 0)
                dotnetEmptyLabel.Text = assetsManager.AssetsFileList.Count > 0
                    ? "No .NET assemblies loaded\r\n\r\nDiscover the ones that belong to the loaded project,\r\nor use File \u2192 Load .NET assemblies folder / IL2CPP binary"
                    : "No .NET assemblies loaded\r\n\r\nLoad a game folder with a Managed directory,\r\nor use File \u2192 Load .NET assemblies folder / IL2CPP binary";
            else
                dotnetEmptyLabel.Text = "No types match the search\r\n\r\nClear the search box (Esc) and press Enter";
            dotnetEmptyLabel.BackColor = dotnetTreeView.BackColor;
            dotnetEmptyLabel.Visible = empty || loadingText != null;
            if (dotnetEmptyLabel.Visible) dotnetEmptyLabel.BringToFront();
            UpdateDotNetDiscoverButton(loadingText != null);
            UpdateThemeSwitchEnabled();
        }

        // Theme can only be switched before a project is loaded. A live switch while assets
        // or assemblies are on screen is where the async SystemColors remap is most likely
        // to leave a control half-themed, so lock the toggle once anything is loaded.
        private void UpdateThemeSwitchEnabled()
        {
            var anyLoaded = exportableAssets.Count > 0 || (assemblyLoader?.Loaded ?? false);
            if (themeToggleButton != null)
            {
                themeToggleButton.Enabled = !anyLoaded;
                themeToggleButton.ToolTipText = anyLoaded
                    ? "Theme can only be changed before a project is loaded"
                    : (isDarkMode ? "Switch to light theme" : "Switch to dark theme");
            }
            if (colorThemeToolStripMenu != null)
                colorThemeToolStripMenu.Enabled = !anyLoaded;
        }

        // Right-aligned "N assets · M selected" counter in the status bar, so the list
        // size and selection are always visible without reading the log line.
        private ToolStripStatusLabel assetCountLabel;

        private void InitStatusCounter()
        {
            toolStripStatusLabel1.Spring = true;
            toolStripStatusLabel1.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            assetCountLabel = new ToolStripStatusLabel
            {
                Alignment = ToolStripItemAlignment.Right,
                BackColor = System.Drawing.Color.Transparent,
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                TextAlign = System.Drawing.ContentAlignment.MiddleRight,
                Margin = new Padding(8, 3, 4, 2),
            };
            statusStrip1.Items.Add(assetCountLabel);
            UpdateSceneEmptyState();
            UpdateClassesEmptyState();
            UpdateDotNetEmptyState();
            UpdateAssetCounts();
        }

        private void UpdateAssetCounts()
        {
            if (assetCountLabel == null)
                return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action(UpdateAssetCounts));
                return;
            }
            var total = exportableAssets.Count;
            if (total == 0)
            {
                assetCountLabel.Text = "";
                return;
            }
            var visible = visibleAssets?.Count ?? 0;
            var selected = assetListView.SelectedIndices.Count;
            var text = visible == total ? $"{total:N0} assets" : $"{visible:N0} of {total:N0} assets";
            if (selected > 0)
                text += $"  \u00b7  {selected:N0} selected";
            assetCountLabel.Text = text;
        }

        private void UpdateAssetEmptyState()
        {
            // Model / Export / Filter Type only make sense once assets are loaded.
            var loaded = exportableAssets.Count > 0;
            modelToolStripMenuItem.Enabled = loaded;
            // Export stays available if either assets or .NET assemblies are loaded.
            exportToolStripMenuItem.Enabled = loaded || (assemblyLoader?.Loaded ?? false);
            filterTypeToolStripMenuItem.Enabled = loaded;

            UpdateThemeSwitchEnabled();

            if (assetEmptyLabel == null)
                return;
            if (visibleAssets != null && visibleAssets.Count > 0)
            {
                assetEmptyLabel.Visible = false;
                return;
            }
            assetEmptyLabel.Text = exportableAssets.Count == 0
                ? "No assets loaded\r\n\r\nOpen a file or folder to begin  (File → Load file / Load folder)"
                : "No assets found\r\n\r\nSearch by name, container, path, or content.\r\nTry clearing the search or the type filter.";
            assetEmptyLabel.BackColor = assetListView.BackColor;
            assetEmptyLabel.Visible = true;
            assetEmptyLabel.BringToFront();
        }

        private System.Drawing.Image previewPlaceholder;

        // Placeholder shown in the preview area when no asset is selected. Built once,
        // themed for the current mode (subtle gray reads fine on light or dark).
        private bool previewPlaceholderDark;

        private System.Drawing.Image PreviewPlaceholder()
        {
            if (previewPlaceholder != null && previewPlaceholderDark == isDarkMode)
                return previewPlaceholder;
            previewPlaceholder?.Dispose();
            previewPlaceholder = MakePreviewMessage("Select an asset to preview", "Pick an item from the Asset List or Scene Hierarchy");
            previewPlaceholderDark = isDarkMode;
            return previewPlaceholder;
        }

        private System.Drawing.Image dotnetPlaceholder;
        private bool dotnetPlaceholderDark;

        // Placeholder shown in the preview area while the .NET Classes tab is active and no
        // type is selected (the generic "Select an asset" message doesn't fit that tab).
        private System.Drawing.Image DotNetPlaceholder()
        {
            if (dotnetPlaceholder != null && dotnetPlaceholderDark == isDarkMode)
                return dotnetPlaceholder;
            dotnetPlaceholder?.Dispose();
            dotnetPlaceholder = MakePreviewMessage("Select a type to view its source", "Pick a class from the .NET Classes tree");
            dotnetPlaceholderDark = isDarkMode;
            return dotnetPlaceholder;
        }

        // Swap the idle preview placeholder to match the active left-hand tab. Only touches
        // the image when a placeholder (not a live preview) is currently shown, so it never
        // clobbers a real asset preview or the .NET class-source overlay.
        private void UpdatePreviewPlaceholderForTab()
        {
            var cur = previewPanel.Image;
            if (cur != null && cur != previewPlaceholder && cur != dotnetPlaceholder)
                return;
            var wanted = tabControl1.SelectedIndex == 3 ? DotNetPlaceholder() : PreviewPlaceholder();
            if (cur == wanted)
                return;
            previewPanel.Image = wanted;
            previewPanel.SizeMode = PictureBoxSizeMode.CenterImage;
        }

        // Shown when an asset IS selected but has no visual preview (unsupported image,
        // font, sprite, or a type with no preview). Replaces the misleading
        // "Select an asset to preview" for that case.
        // Cached per asset type: this used to allocate a fresh ~165 KB bitmap on every click
        // and orphan the previous one (GDI handle held until the finalizer ran). The set of
        // types is small and bounded, and the cache is dropped on theme change and reset.
        private readonly Dictionary<string, System.Drawing.Image> noPreviewCache = new Dictionary<string, System.Drawing.Image>();

        private System.Drawing.Image NoPreviewImage(string typeName)
        {
            if (!noPreviewCache.TryGetValue(typeName, out var img))
            {
                img = MakePreviewMessage("No preview available", $"{typeName} — use Export or the Dump tab");
                noPreviewCache[typeName] = img;
            }
            return img;
        }

        private void ClearNoPreviewCache()
        {
            foreach (var img in noPreviewCache.Values)
            {
                if (previewPanel.Image == img)
                    previewPanel.Image = null;
                img.Dispose();
            }
            noPreviewCache.Clear();
        }

        // Drop the lowercased TextAsset/MonoBehaviour dump text kept for the "search inside
        // contents" mode. On a big game that is potentially hundreds of MB, so it must not
        // outlive the feature being switched off.
        private void ReleaseSearchContentCaches()
        {
            foreach (var item in exportableAssets)
            {
                item.SearchContentCache = null;
                item.SearchContentBuilt = false;
                // The search parsed TextAssets to read them; drop those payloads too.
                if (item.Type == ClassIDType.TextAsset)
                    (item.RawAsset as LazyObject)?.Release();
            }
        }

        // Centered two-line message image, colored to read on the current theme's preview bg.
        private System.Drawing.Image MakePreviewMessage(string title, string hint)
        {
            // Match the preview panel background so ClearType text renders on an opaque
            // surface (on a transparent bitmap it fringes and reads as unreadable).
            var backColor = isDarkMode ? DarkContent : LightChrome;
            var titleColor = isDarkMode ? System.Drawing.Color.FromArgb(170, 170, 170) : System.Drawing.Color.FromArgb(80, 80, 80);
            var hintColor = isDarkMode ? System.Drawing.Color.FromArgb(120, 120, 120) : System.Drawing.Color.FromArgb(115, 115, 115);
            var bmp = new Bitmap(460, 90);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(backColor);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using var titleFont = new System.Drawing.Font("Segoe UI", 15f);
                using var hintFont = new System.Drawing.Font("Segoe UI", 9.5f);
                using var titleBrush = new SolidBrush(titleColor);
                using var hintBrush = new SolidBrush(hintColor);
                var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(title, titleFont, titleBrush, new RectangleF(0, 10, 460, 40), center);
                g.DrawString(hint, hintFont, hintBrush, new RectangleF(0, 50, 460, 30), center);
            }
            return bmp;
        }

        // Owner-draw + original border styles are wired once; colors are (re)applied by
        // ApplyTheme so the theme can switch live.
        private bool themeWired;
        private BorderStyle ob_sceneTree, ob_dumpTree, ob_split, ob_dumpText, ob_textPrev, ob_classText;
        private BorderStyle ob_assetList, ob_classesList;
        private System.Drawing.Color ob_splitBack;

        private SearchHost treeSearchHost;
        private SearchHost listSearchHost;

        // Wrap the Asset List search box in the polished SearchHost and drop the old history
        // dropdown (the field matches the other search boxes; no autocomplete).
        private SearchToggle searchRegexToggle, searchContentToggle, searchExcludeToggle;

        private void WrapListSearch()
        {
            panel1.Height = 48;
            panel1.Padding = new Padding(0, 0, 0, 8); // gap below the search bar (page padding handles the rest)
            listSearchHistory.Visible = false;      // history dropdown replaced by autocomplete
            panel1.Controls.Remove(listSearchHistory);
            listSearchFilterMode.Visible = false;   // Include/Exclude/Regex dropdown replaced by in-field toggles
            panel1.Controls.Remove(listSearchFilterMode);
            panel1.Controls.Remove(listSearch);
            listSearchHost = new SearchHost(listSearch) { Dock = DockStyle.Fill };
            panel1.Controls.Add(listSearchHost);

            // In-field modifier toggles (left→right): regex, content, exclude.
            searchRegexToggle = listSearchHost.AddToggle(".*", "Regular expression");
            searchContentToggle = listSearchHost.AddToggle("{}", "Also search inside file contents (TextAsset / MonoBehaviour)");
            searchExcludeToggle = listSearchHost.AddToggle("−", "Exclude — hide assets that match");
            foreach (var t in new[] { searchRegexToggle, searchContentToggle, searchExcludeToggle })
                t.CheckedChanged += (s, e) => FilterAssetList();
            // Content search caches dump text per asset; free it as soon as the mode is off.
            searchContentToggle.CheckedChanged += (s, e) =>
            {
                if (!searchContentToggle.Checked)
                    ReleaseSearchContentCaches();
            };
        }

        // Wrap the Scene Hierarchy search box in the polished SearchHost, with an in-field
        // "exact match" toggle (replaces the old external "Exact search" checkbox, which is
        // kept hidden as the state the search logic reads).
        private void WrapTreeSearch()
        {
            tabPage1.Controls.Remove(treeSearch);
            tabPage1.Controls.Remove(sceneExactSearchCheckBox);
            sceneExactSearchCheckBox.Visible = false;
            // Mirror the Asset List's search row exactly: a Dock=Top panel of Height 48 with
            // an 8px bottom padding, the SearchHost filling it. Same position/size as panel1.
            var searchRow = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(0, 0, 0, 8), Name = "treeSearchRow" };
            treeSearchHost = new SearchHost(treeSearch) { Dock = DockStyle.Fill };
            var exact = treeSearchHost.AddToggle("W", "Whole word — exact match");
            exact.CheckedChanged += (s, e) => sceneExactSearchCheckBox.Checked = exact.Checked;
            searchRow.Controls.Add(treeSearchHost);
            // Add last (highest z-index) so docking positions it at the top and the
            // Fill tree takes the space below it (BringToFront would overlap the tree).
            tabPage1.Controls.Add(searchRow);
        }

        private void WireThemeControls()
        {
            if (themeWired)
                return;
            themeWired = true;
            foreach (var lv in new[] { assetListView, classesListView })
            {
                lv.OwnerDraw = true;
                lv.DrawColumnHeader += DarkListView_DrawColumnHeader;
                lv.DrawItem += ListView_DrawItem;
                lv.DrawSubItem += DarkListView_DrawSubItem;
                lv.MouseMove += ListView_HoverMove;
                lv.MouseLeave += ListView_HoverLeave;
            }
            // Remember the light-mode border styles so we can restore them.
            ob_sceneTree = sceneTreeView.BorderStyle;
            ob_dumpTree = dumpTreeView.BorderStyle;
            ob_split = splitContainer1.BorderStyle;
            ob_dumpText = dumpTextBox.BorderStyle;
            ob_textPrev = textPreviewBox.BorderStyle;
            ob_classText = classTextBox.BorderStyle;
            ob_assetList = assetListView.BorderStyle;
            ob_classesList = classesListView.BorderStyle;
            ob_splitBack = splitContainer1.BackColor;
        }

        // Applies the full color theme for either mode. Safe to call repeatedly (live switch).
        private void ApplyTheme(bool dark)
        {
            isDarkMode = dark;
            var content = dark ? DarkContent : LightContent;
            var chrome = dark ? DarkChrome : LightChrome;
            var field = dark ? DarkField : LightField;
            var fore = dark ? DarkFore : LightFore;

            foreach (Control c in new Control[] { assetListView, classesListView, sceneTreeView, dumpTreeView })
            {
                c.BackColor = content;
                c.ForeColor = fore;
            }
            // Native grid lines render light and can't be themed; off in dark mode.
            assetListView.GridLines = !dark;
            classesListView.GridLines = !dark;
            // The list's own 3-D border renders light in dark mode; drop it.
            assetListView.BorderStyle = dark ? BorderStyle.None : ob_assetList;
            classesListView.BorderStyle = dark ? BorderStyle.None : ob_classesList;
            previewPanel.BackColor = dark ? DarkContent : LightChrome;
            FMODpanel.BackColor = dark ? DarkContent : LightChrome;

            // The preview overlay labels (asset info + the FMOD audio player) are hardcoded
            // white in the Designer for the dark preview background; that is unreadable on the
            // light background, so theme their foreground to match the current mode.
            foreach (var lbl in new Label[] { assetInfoLabel, FMODstatusLabel, FMODinfoLabel,
                                              FMODtimerLabel, FMODaudioChannelsLabel, FMODcopyrightLabel })
            {
                if (lbl != null)
                    lbl.ForeColor = fore;
            }

            panel1.BackColor = chrome;
            panel1.ForeColor = fore;

            // All three search boxes are wrapped in SearchHosts, which theme themselves.
            listSearchHost?.Theme(dark);
            treeSearchHost?.Theme(dark);
            dotnetSearchHost?.Theme(dark);

            foreach (Control tb in new Control[] { dumpTextBox, textPreviewBox, fontPreviewBox, classTextBox })
            {
                if (tb == null) continue;
                tb.BackColor = content;
                tb.ForeColor = fore;
            }
            dumpTextBox.BorderStyle = dark ? BorderStyle.None : ob_dumpText;
            dumpTreeView.BorderStyle = dark ? BorderStyle.None : ob_dumpTree;
            if (classTextBox != null) classTextBox.BorderStyle = dark ? BorderStyle.None : ob_classText;
            if (textPreviewBox != null) textPreviewBox.BorderStyle = dark ? BorderStyle.None : ob_textPrev;
            sceneTreeView.BorderStyle = dark ? BorderStyle.None : ob_sceneTree;
            splitContainer1.BorderStyle = dark ? BorderStyle.None : ob_split;
            splitContainer1.BackColor = dark ? DarkSeparator : LightSeparator;

            if (dotnetTreeView != null)
            {
                dotnetTreeView.BackColor = content;
                dotnetTreeView.ForeColor = fore;
            }

            // Tabs: DarkTabControl paints dark when DarkMode is set; native tabs otherwise.
            foreach (var tc in new[] { tabControl1, tabControl2 })
            {
                tc.DarkMode = dark;
                tc.BackColor = chrome;
                foreach (TabPage page in tc.TabPages)
                {
                    page.UseVisualStyleBackColor = !dark;
                    page.BackColor = chrome;
                    page.ForeColor = fore;
                    page.Padding = new Padding(8); // uniform breathing room around the content
                }
                tc.Invalidate(true);
            }

            ThemeContainer(tabPage1, dark);
            if (dotnetTabPage != null)
                ThemeContainer(dotnetTabPage, dark);

            if (assetEmptyLabel != null)
            {
                assetEmptyLabel.BackColor = assetListView.BackColor;
                assetEmptyLabel.ForeColor = dark ? System.Drawing.Color.FromArgb(150, 150, 150) : LightGrayText;
            }
            if (sceneEmptyLabel != null)
            {
                sceneEmptyLabel.BackColor = sceneTreeView.BackColor;
                sceneEmptyLabel.ForeColor = dark ? System.Drawing.Color.FromArgb(150, 150, 150) : LightGrayText;
            }
            if (dotnetEmptyLabel != null && dotnetTreeView != null)
            {
                dotnetEmptyLabel.BackColor = dotnetTreeView.BackColor;
                dotnetEmptyLabel.ForeColor = dark ? System.Drawing.Color.FromArgb(150, 150, 150) : LightGrayText;
            }
            ThemeDotNetDiscoverButton(dark);
            if (classesEmptyLabel != null)
            {
                classesEmptyLabel.BackColor = classesListView.BackColor;
                classesEmptyLabel.ForeColor = dark ? System.Drawing.Color.FromArgb(150, 150, 150) : LightGrayText;
            }

            ApplyToolbarTheme(dark);
            // Menu bar, dropdowns and context menus: clean accent checks + dark surfaces.
            ToolStripManager.Renderer = dark ? (menuRenderer ??= new MenuRenderer()) : new ToolStripProfessionalRenderer();
            menuStrip1.Invalidate();
            ApplyNativeTheme(dark);

            // Force stale-painting controls to redraw fully on a live switch.
            foreach (Control c in new Control[] { assetListView, classesListView, sceneTreeView, dumpTreeView,
                                                  panel1, tabControl1, tabControl2 })
                c?.Refresh();
            RefreshListHeader(assetListView);
            RefreshListHeader(classesListView);
            // Rebuild the preview placeholder for the new theme if it's on screen.
            if (previewPanel.Image == previewPlaceholder && previewPlaceholder != null)
                previewPanel.Image = PreviewPlaceholder();
            else if (previewPanel.Image == dotnetPlaceholder && dotnetPlaceholder != null)
                previewPanel.Image = DotNetPlaceholder();
            ClearNoPreviewCache(); // colored for the old theme
            Invalidate(true);
        }

        private void ApplyToolbarTheme(bool dark)
        {
            if (toolStripMain == null)
                return;
            var glyph = dark ? System.Drawing.Color.FromArgb(225, 225, 225) : System.Drawing.Color.FromArgb(60, 60, 60);
            foreach (var (btn, icon) in toolbarIcons)
            {
                var old = btn.Image;
                btn.Image = icon(glyph);
                old?.Dispose();
            }
            if (themeToggleButton != null)
            {
                var old = themeToggleButton.Image;
                // Show the icon of the mode you'd switch TO.
                themeToggleButton.Image = dark ? IconSun(glyph) : IconMoon(glyph);
                themeToggleButton.ToolTipText = dark ? "Switch to light theme" : "Switch to dark theme";
                old?.Dispose();
            }
            if (dark)
            {
                toolStripMain.Renderer = new ToolStripProfessionalRenderer(new DarkToolStripColorTable()) { RoundedEdges = false };
                toolStripMain.BackColor = DarkChrome;
                toolStripMain.ForeColor = DarkFore;
            }
            else
            {
                toolStripMain.RenderMode = ToolStripRenderMode.System;
                toolStripMain.BackColor = LightChrome;
                toolStripMain.ForeColor = LightFore;
            }
            toolStripMain.Invalidate();
        }

        // Native theming that needs window handles (dark title bar + dark scrollbars).
        private void ApplyNativeTheme(bool dark)
        {
            if (!IsHandleCreated)
                return;
            try
            {
                int on = dark ? 1 : 0;
                DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
                var theme = dark ? "DarkMode_Explorer" : "Explorer";
                foreach (Control c in new Control[] { assetListView, classesListView, sceneTreeView, dumpTreeView, dotnetTreeView })
                {
                    if (c == null || !c.IsHandleCreated) continue;
                    SetWindowTheme(c.Handle, theme, null); // control body + scrollbars
                    if (c is ListView lv) // the header is a separate sub-control
                    {
                        var hdr = SendMessage(lv.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
                        if (hdr != IntPtr.Zero)
                            SetWindowTheme(hdr, theme, null);
                    }
                }
            }
            catch
            {
                // Best-effort on older Windows.
            }
        }

        // Applies the layered dark palette by control type through a container's tree:
        // panels/labels = chrome, tree/list/text = content (darker), inputs = field.
        // Recursively theme a container's children for dark or light.
        private void ThemeContainer(Control root, bool dark)
        {
            var content = dark ? DarkContent : LightContent;
            var chrome = dark ? DarkChrome : LightChrome;
            var field = dark ? DarkField : LightField;
            var fore = dark ? DarkFore : LightFore;
            foreach (Control c in root.Controls)
            {
                switch (c)
                {
                    case SearchHost sh:
                        sh.Theme(dark);
                        continue; // owns its children's colors
                    case TextBox _:
                    case ComboBox _:
                        c.BackColor = field;
                        c.ForeColor = fore;
                        break;
                    case TreeView _:
                    case ListView _:
                        c.BackColor = content;
                        c.ForeColor = fore;
                        break;
                    case CheckBox chk:
                        chk.UseVisualStyleBackColor = !dark;
                        chk.BackColor = chrome;
                        chk.ForeColor = fore;
                        break;
                    case Button btn:
                        btn.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                        btn.UseVisualStyleBackColor = !dark;
                        btn.BackColor = dark ? DarkField : LightChrome;
                        btn.ForeColor = fore;
                        break;
                    case Label _:
                    case Panel _:
                        c.BackColor = chrome;
                        c.ForeColor = fore;
                        break;
                }
                if (c.HasChildren)
                    ThemeContainer(c, dark);
            }
        }

        // Row palette (dark mode only; light mode uses e.DrawDefault).
        private static readonly System.Drawing.Color RowEven = System.Drawing.Color.FromArgb(24, 24, 24);
        private static readonly System.Drawing.Color RowOdd = System.Drawing.Color.FromArgb(32, 32, 34);   // subtle zebra
        private static readonly System.Drawing.Color RowSelected = System.Drawing.Color.FromArgb(38, 79, 120); // muted blue
        private static readonly System.Drawing.Color RowText = System.Drawing.Color.FromArgb(222, 222, 222);
        private static readonly System.Drawing.Color RowTextSel = System.Drawing.Color.FromArgb(245, 245, 245);
        private static readonly System.Drawing.Color RowSeparator = System.Drawing.Color.FromArgb(48, 48, 51);
        private static readonly System.Drawing.Color RowHover = System.Drawing.Color.FromArgb(50, 50, 54);

        // Cached GDI objects for the owner-draw path. The palette is fixed, and the draw
        // handlers run for every visible cell on every scroll step, so allocating a brush
        // and a pen per cell (hundreds per repaint) was pure overhead. Created lazily on the
        // UI thread on first paint (avoids static-initializer ordering with the color consts).
        private SolidBrush brRowEven, brRowOdd, brRowSelected, brRowHover, brHeader, brHeaderPressed;
        private Pen pnRowSeparator, pnHeaderLine;

        private void EnsureRowGdi()
        {
            if (brRowEven != null)
                return;
            brRowEven = new SolidBrush(RowEven);
            brRowOdd = new SolidBrush(RowOdd);
            brRowSelected = new SolidBrush(RowSelected);
            brRowHover = new SolidBrush(RowHover);
            pnRowSeparator = new Pen(RowSeparator);
            brHeader = new SolidBrush(System.Drawing.Color.FromArgb(50, 50, 52));        // a touch lighter than rows
            brHeaderPressed = new SolidBrush(System.Drawing.Color.FromArgb(64, 64, 66));
            pnHeaderLine = new Pen(System.Drawing.Color.FromArgb(58, 58, 60));          // subtle divider, not whitish
        }

        private ListView hoverList;
        private int hoverRowIndex = -1;

        private void ListView_HoverMove(object sender, MouseEventArgs e)
        {
            var lv = (ListView)sender;
            var idx = lv.GetItemAt(e.X, e.Y)?.Index ?? -1;
            if (lv == hoverList && idx == hoverRowIndex)
                return;
            var oldList = hoverList; var oldIdx = hoverRowIndex;
            hoverList = lv; hoverRowIndex = idx;
            try { if (oldList != null && oldIdx >= 0) oldList.Invalidate(oldList.GetItemRect(oldIdx)); } catch { }
            try { if (idx >= 0) lv.Invalidate(lv.GetItemRect(idx)); } catch { }
        }

        private void ListView_HoverLeave(object sender, EventArgs e)
        {
            if (hoverList == null || hoverRowIndex < 0)
                return;
            var lv = hoverList; var idx = hoverRowIndex;
            hoverList = null; hoverRowIndex = -1;
            try { lv.Invalidate(lv.GetItemRect(idx)); } catch { }
        }

        // In Details view the subitems handle drawing; in light mode fall back to default.
        private void ListView_DrawItem(object sender, DrawListViewItemEventArgs e)
        {
            if (!isDarkMode)
                e.DrawDefault = true;
        }

        // Full row owner-draw (dark only): zebra striping, flat selection, no focus dots.
        private void DarkListView_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (!isDarkMode)
            {
                e.DrawDefault = true;
                return;
            }
            var lv = (ListView)sender;
            EnsureRowGdi();
            var selected = e.Item.Selected;
            var hovered = !selected && lv == hoverList && e.ItemIndex == hoverRowIndex;
            var back = selected ? brRowSelected
                     : hovered ? brRowHover
                     : ((e.ItemIndex & 1) == 0 ? brRowEven : brRowOdd);
            e.Graphics.FillRectangle(back, e.Bounds);

            // Subtle column separator on the right edge of each cell (replaces the light
            // native grid lines that don't theme).
            e.Graphics.DrawLine(pnRowSeparator, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom - 1);

            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.LeftAndRightPadding;
            if (e.Header != null)
            {
                if (e.Header.TextAlign == HorizontalAlignment.Center)
                    flags |= TextFormatFlags.HorizontalCenter;
                else if (e.Header.TextAlign == HorizontalAlignment.Right)
                    flags |= TextFormatFlags.Right;
            }
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, lv.Font, e.Bounds,
                selected ? RowTextSel : RowText, flags);
        }

        private void DarkListView_DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            if (!isDarkMode)
            {
                e.DrawDefault = true;
                return;
            }
            EnsureRowGdi();
            var text = System.Drawing.Color.FromArgb(235, 235, 235);
            var pressed = (e.State & ListViewItemStates.Selected) != 0;

            e.Graphics.FillRectangle(pressed ? brHeaderPressed : brHeader, e.Bounds);
            e.Graphics.DrawLine(pnHeaderLine, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom - 1);
            e.Graphics.DrawLine(pnHeaderLine, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

            // Slight extra left padding + bold for a clearer information hierarchy.
            var bounds = e.Bounds;
            bounds.X += 4;
            bounds.Width -= 4;
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            if (e.Header.TextAlign == HorizontalAlignment.Center)
                flags |= TextFormatFlags.HorizontalCenter;
            else if (e.Header.TextAlign == HorizontalAlignment.Right)
                flags |= TextFormatFlags.Right;
            using var bold = new System.Drawing.Font(((ListView)sender).Font, System.Drawing.FontStyle.Bold);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, bold, bounds, text, flags);
        }

        // Dark color table for the toolbar's professional renderer (hover/pressed/borders).
        private sealed class DarkToolStripColorTable : ProfessionalColorTable
        {
            private static readonly System.Drawing.Color Bar = System.Drawing.Color.FromArgb(45, 45, 45);
            private static readonly System.Drawing.Color Hover = System.Drawing.Color.FromArgb(62, 62, 64);
            private static readonly System.Drawing.Color Pressed = System.Drawing.Color.FromArgb(80, 80, 82);
            private static readonly System.Drawing.Color BorderClr = System.Drawing.Color.FromArgb(95, 95, 98);
            private static readonly System.Drawing.Color Sep = System.Drawing.Color.FromArgb(70, 70, 70);

            public DarkToolStripColorTable() { UseSystemColors = false; }

            public override System.Drawing.Color ToolStripGradientBegin => Bar;
            public override System.Drawing.Color ToolStripGradientMiddle => Bar;
            public override System.Drawing.Color ToolStripGradientEnd => Bar;
            public override System.Drawing.Color ToolStripBorder => Bar;
            public override System.Drawing.Color ButtonSelectedHighlight => Hover;
            public override System.Drawing.Color ButtonSelectedGradientBegin => Hover;
            public override System.Drawing.Color ButtonSelectedGradientMiddle => Hover;
            public override System.Drawing.Color ButtonSelectedGradientEnd => Hover;
            public override System.Drawing.Color ButtonSelectedBorder => BorderClr;
            public override System.Drawing.Color ButtonPressedHighlight => Pressed;
            public override System.Drawing.Color ButtonPressedGradientBegin => Pressed;
            public override System.Drawing.Color ButtonPressedGradientMiddle => Pressed;
            public override System.Drawing.Color ButtonPressedGradientEnd => Pressed;
            public override System.Drawing.Color ButtonPressedBorder => BorderClr;
            public override System.Drawing.Color SeparatorDark => Sep;
            public override System.Drawing.Color SeparatorLight => Sep;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ApplyNativeTheme(isDarkMode);
        }

        // Draws over the TabControl's light 3-D frame (outer border + the divider line
        // under the tab row) with the dark background, after the control paints itself.

        private void colorThemeAutoToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!colorThemeAutoToolStripMenuItem.Checked)
                SwitchTheme(GuiColorTheme.System);
        }

        private void colorThemeLightToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!colorThemeLightToolStripMenuItem.Checked)
                SwitchTheme(GuiColorTheme.Light);
        }

        private void colorThemeDarkToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!colorThemeDarkToolStripMenuItem.Checked)
                SwitchTheme(GuiColorTheme.Dark);
        }

        // Live theme switch: no restart. Updates the framework color mode and re-applies
        // the full custom theme in place.
        private void SwitchTheme(GuiColorTheme theme)
        {
            colorThemeAutoToolStripMenuItem.Checked = theme == GuiColorTheme.System;
            colorThemeLightToolStripMenuItem.Checked = theme == GuiColorTheme.Light;
            colorThemeDarkToolStripMenuItem.Checked = theme == GuiColorTheme.Dark;
            Properties.Settings.Default.guiColorTheme = theme;
            Properties.Settings.Default.Save();

            var dark = false;
#if NET9_0_OR_GREATER
#pragma warning disable WFO5001 // evaluation-only API
            try
            {
                switch (theme)
                {
                    case GuiColorTheme.System:
                        Application.SetColorMode(SystemColorMode.System);
                        dark = Application.IsDarkModeEnabled;
                        break;
                    case GuiColorTheme.Light:
                        Application.SetColorMode(SystemColorMode.Classic);
                        break;
                    case GuiColorTheme.Dark:
                        Application.SetColorMode(SystemColorMode.Dark);
                        dark = true;
                        break;
                }
            }
            catch { }
#pragma warning restore WFO5001
#endif
            SuspendLayout();
            ApplyTheme(dark);
            menuStrip1.Refresh();
            toolStripMain?.Refresh();
            ResumeLayout(true);
        }

        private void DumpTreeView_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                dumpTreeView.SelectedNode = e.Node;
                tempClipboard = string.IsNullOrEmpty((string)e.Node.Tag)
                    ? e.Node.Text
                    : $"{e.Node.Name}: {e.Node.Tag}";
                dumpTreeViewContextMenuStrip.Show(dumpTreeView, e.Location.X, e.Location.Y);
            }
        }

        private void copyToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            Clipboard.SetDataObject(tempClipboard);
        }

        private void expandAllToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            dumpTreeView.BeginUpdate();
            foreach (TreeNode node in dumpTreeView.Nodes)
            {
                node.ExpandAll();
            }
            dumpTreeView.EndUpdate();
        }

        private void collapseAllToolStripMenuItem1_Click(object sender, EventArgs e)
        {
            dumpTreeView.BeginUpdate();
            foreach (TreeNode node in dumpTreeView.Nodes)
            {
                node.Collapse(ignoreChildren: false);
            }
            dumpTreeView.EndUpdate();
        }

        private void useDumpTreeViewToolStripMenuItem_CheckedChanged(object sender, EventArgs e)
        {
            var isTreeViewEnabled = useDumpTreeViewToolStripMenuItem.Checked;
            dumpTreeView.Visible = isTreeViewEnabled;
            Properties.Settings.Default.useDumpTreeView = isTreeViewEnabled;
            Properties.Settings.Default.Save();
            if (tabControl2.SelectedIndex == 1)
            {
                DumpAsset(lastSelectedItem);
            }
        }

        private void autoPlayAudioAssetsToolStripMenuItem_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.autoplayAudio = autoPlayAudioAssetsToolStripMenuItem.Checked;
            Properties.Settings.Default.Save();
        }

        private void meshLazyLoadToolStripMenuItem_CheckedChanged(object sender, EventArgs e)
        {
            Properties.Settings.Default.meshLazyLoad = meshLazyLoadToolStripMenuItem.Checked;
            assetsManager.MeshLazyLoad = meshLazyLoadToolStripMenuItem.Checked;
            Properties.Settings.Default.Save();
        }

        private static void FbxInitOptions(string base64String)
        {
            if (string.IsNullOrEmpty(base64String))
            {
                Studio.FbxSettings = new Fbx.Settings();
                Properties.Settings.Default.fbxSettings = Studio.FbxSettings.ToBase64();
                Properties.Settings.Default.Save();
            }
            else
            {
                Studio.FbxSettings = Fbx.Settings.FromBase64(base64String);
            }
        }

        #region FMOD
        private void FMODinit()
        {
            FMODreset();

            var result = FMOD.Factory.System_Create(out system);
            if (ERRCHECK(result)) { return; }

            result = system.getVersion(out var version);
            ERRCHECK(result);
            if (version < FMOD.VERSION.number)
            {
                Logger.Error($"Error! You are using an old version of FMOD {version:X}. This program requires {FMOD.VERSION.number:X}.");
                Application.Exit();
            }

            result = system.init(2, FMOD.INITFLAGS.NORMAL, IntPtr.Zero);
            if (ERRCHECK(result)) { return; }

            _ = system.getMasterChannelGroup(out var channelGroup);
            result = channelGroup.setVolume(FMODVolume);
            if (ERRCHECK(result)) { return; }
        }

        private void FMODreset()
        {
            timer.Stop();
            FMODprogressBar.Value = 0;
            FMODtimerLabel.Text = "00:00.00 / 00:00.00";
            FMODstatusLabel.Text = "Stopped";
            FMODpauseButton.Text = "Pause";
            FMODinfoLabel.Text = "";
            FMODaudioChannelsLabel.Text = "";

            if (sound.hasHandle())
            {
                FMOD.RESULT result;
                sound.getSubSoundParent(out var parentsound);
                result = sound.release();
                ERRCHECK(result);
                sound.clearHandle();
                if (parentsound.hasHandle())
                {
                    result = parentsound.release();
                    ERRCHECK(result);
                    parentsound.clearHandle();
                }
            }
            if (soundBuff != null)
            {
                BigArrayPool<byte>.Shared.Return(soundBuff, clearArray: true);
                soundBuff = null;
            }
        }

        private void FMODplayButton_Click(object sender, EventArgs e)
        {
            if (sound.hasHandle() && channel.hasHandle())
            {
                _ = system.getMasterChannelGroup(out var channelGroup);
                timer.Start();
                var result = channel.isPlaying(out var playing);
                if (result != FMOD.RESULT.OK && result != FMOD.RESULT.ERR_INVALID_HANDLE)
                {
                    if (ERRCHECK(result)) { return; }
                }

                if (playing)
                {
                    result = channel.stop();
                    if (ERRCHECK(result)) { return; }

                    result = system.playSound(sound, channelGroup, false, out channel);
                    if (ERRCHECK(result)) { return; }

                    FMODpauseButton.Text = "Pause";
                }
                else
                {
                    result = system.playSound(sound, channelGroup, false, out channel);
                    if (ERRCHECK(result)) { return; }

                    FMODstatusLabel.Text = "Playing";
                    if (FMODprogressBar.Value > 0)
                    {
                        uint newms = FMODlenms / 1000 * (uint)FMODprogressBar.Value;

                        result = channel.setPosition(newms, FMOD.TIMEUNIT.MS);
                        if (result != FMOD.RESULT.OK && result != FMOD.RESULT.ERR_INVALID_HANDLE)
                        {
                            if (ERRCHECK(result)) { return; }
                        }
                    }
                }
            }
        }

        private void FMODpauseButton_Click(object sender, EventArgs e)
        {
            if (sound.hasHandle() && channel.hasHandle())
            {
                var result = channel.isPlaying(out var playing);
                if (result != FMOD.RESULT.OK && result != FMOD.RESULT.ERR_INVALID_HANDLE)
                {
                    if (ERRCHECK(result)) { return; }
                }

                if (playing)
                {
                    result = channel.getPaused(out var paused);
                    if (ERRCHECK(result)) { return; }

                    result = channel.setPaused(!paused);
                    if (ERRCHECK(result)) { return; }

                    if (paused)
                    {
                        FMODstatusLabel.Text = "Playing";
                        FMODpauseButton.Text = "Pause";
                        timer.Start();
                    }
                    else
                    {
                        FMODstatusLabel.Text = "Paused";
                        FMODpauseButton.Text = "Resume";
                        timer.Stop();
                    }
                }
            }
        }

        private void FMODstopButton_Click(object sender, EventArgs e)
        {
            if (channel.hasHandle())
            {
                var result = channel.isPlaying(out var playing);
                if (result != FMOD.RESULT.OK && result != FMOD.RESULT.ERR_INVALID_HANDLE)
                {
                    if (ERRCHECK(result)) { return; }
                }

                if (playing)
                {
                    result = channel.stop();
                    if (ERRCHECK(result)) { return; }

                    //channel = null;
                    //don't FMODreset, it will nullify the sound
                    timer.Stop();
                    FMODprogressBar.Value = 0;
                    FMODtimerLabel.Text = "00:00.00 / 00:00.00";
                    FMODstatusLabel.Text = "Stopped";
                    FMODpauseButton.Text = "Pause";
                }
            }
        }

        private void FMODloopButton_CheckedChanged(object sender, EventArgs e)
        {
            FMOD.RESULT result;

            loopMode = FMODloopButton.Checked ? FMOD.MODE.LOOP_NORMAL : FMOD.MODE.LOOP_OFF;

            if (sound.hasHandle())
            {
                result = sound.setMode(loopMode);
                if (ERRCHECK(result)) { return; }
            }

            if (channel.hasHandle())
            {
                result = channel.isPlaying(out var playing);
                if (result != FMOD.RESULT.OK && result != FMOD.RESULT.ERR_INVALID_HANDLE)
                {
                    if (ERRCHECK(result)) { return; }
                }

                result = channel.getPaused(out var paused);
                if (result != FMOD.RESULT.OK && result != FMOD.RESULT.ERR_INVALID_HANDLE)
                {
                    if (ERRCHECK(result)) { return; }
                }

                if (playing || paused)
                {
                    result = channel.setMode(loopMode);
                    if (result != FMOD.RESULT.OK && result != FMOD.RESULT.ERR_INVALID_HANDLE)
                    {
                        if (ERRCHECK(result)) { return; }
                    }
                }
            }
        }

        private void FMODvolumeBar_ValueChanged(object sender, EventArgs e)
        {
            FMODVolume = FMODvolumeBar.Value / 10f;

            _ = system.getMasterChannelGroup(out var channelGroup);
            var result = channelGroup.setVolume(FMODVolume);
            if (ERRCHECK(result)) { return; }
        }

        private void FMODprogressBar_Scroll(object sender, EventArgs e)
        {
            if (channel.hasHandle())
            {
                uint newms = FMODlenms / 1000 * (uint)FMODprogressBar.Value;
                FMODtimerLabel.Text = $"{newms / 1000 / 60:00}:{newms / 1000 % 60:00}.{newms / 10 % 100:00} / {FMODlenms / 1000 / 60:00}:{FMODlenms / 1000 % 60:00}.{FMODlenms / 10 % 100:00}";
            }
        }

        private void FMODprogressBar_MouseDown(object sender, MouseEventArgs e)
        {
            timer.Stop();
        }

        private void FMODprogressBar_MouseUp(object sender, MouseEventArgs e)
        {
            if (channel.hasHandle())
            {
                uint newms = FMODlenms / 1000 * (uint)FMODprogressBar.Value;

                var result = channel.setPosition(newms, FMOD.TIMEUNIT.MS);
                if (result != FMOD.RESULT.OK && result != FMOD.RESULT.ERR_INVALID_HANDLE)
                {
                    if (ERRCHECK(result)) { return; }
                }

                result = channel.isPlaying(out var playing);
                if (result != FMOD.RESULT.OK && result != FMOD.RESULT.ERR_INVALID_HANDLE)
                {
                    if (ERRCHECK(result)) { return; }
                }

                if (playing)
                {
                    timer.Start();
                }
            }
        }

        private void timer_Tick(object sender, EventArgs e)
        {
            uint ms = 0;
            bool playing = false;
            bool paused = false;

            if (channel.hasHandle())
            {
                var result = channel.getPosition(out ms, FMOD.TIMEUNIT.MS);
                if (result != FMOD.RESULT.OK && result != FMOD.RESULT.ERR_INVALID_HANDLE)
                {
                    ERRCHECK(result);
                }

                result = channel.isPlaying(out playing);
                if (result != FMOD.RESULT.OK && result != FMOD.RESULT.ERR_INVALID_HANDLE)
                {
                    ERRCHECK(result);
                }

                result = channel.getPaused(out paused);
                if (result != FMOD.RESULT.OK && result != FMOD.RESULT.ERR_INVALID_HANDLE)
                {
                    ERRCHECK(result);
                }

                if (!playing)
                {
                    timer.Stop();
                    ms = 0;
                }
            }

            FMODtimerLabel.Text = $"{ms / 1000 / 60:00}:{ms / 1000 % 60:00}.{ms / 10 % 100:00} / {FMODlenms / 1000 / 60:00}:{FMODlenms / 1000 % 60:00}.{FMODlenms / 10 % 100:00}";
            FMODprogressBar.Value = (int)UnityRift.MathHelper.Clamp(ms * 1000f / FMODlenms, 0, 1000);
            FMODstatusLabel.Text = paused ? "Paused " : playing ? "Playing" : "Stopped";

            if (system.hasHandle() && channel.hasHandle())
            {
                system.update();
            }
        }

        private bool ERRCHECK(FMOD.RESULT result)
        {
            if (result != FMOD.RESULT.OK)
            {
                FMODreset();
                Logger.Warning($"FMOD error! {result} - {FMOD.Error.String(result)}");
                return true;
            }
            return false;
        }
        #endregion

        #region GLControl
        private void InitOpenTK()
        {
            ChangeGLSize(glControl1.Size);
            GL.ClearColor(System.Drawing.Color.CadetBlue);
            pgmID = GL.CreateProgram();
            LoadShader("vs", ShaderType.VertexShader, pgmID, out int vsID);
            LoadShader("fs", ShaderType.FragmentShader, pgmID, out int fsID);
            GL.LinkProgram(pgmID);

            pgmColorID = GL.CreateProgram();
            LoadShader("vs", ShaderType.VertexShader, pgmColorID, out vsID);
            LoadShader("fsColor", ShaderType.FragmentShader, pgmColorID, out fsID);
            GL.LinkProgram(pgmColorID);

            pgmBlackID = GL.CreateProgram();
            LoadShader("vs", ShaderType.VertexShader, pgmBlackID, out vsID);
            LoadShader("fsBlack", ShaderType.FragmentShader, pgmBlackID, out fsID);
            GL.LinkProgram(pgmBlackID);

            pgmTexID = GL.CreateProgram();
            LoadShader("vsTex", ShaderType.VertexShader, pgmTexID, out vsID);
            LoadShader("fsTex", ShaderType.FragmentShader, pgmTexID, out fsID);
            GL.LinkProgram(pgmTexID);

            attributeVertexPosition = GL.GetAttribLocation(pgmID, "vertexPosition");
            attributeNormalDirection = GL.GetAttribLocation(pgmID, "normalDirection");
            attributeVertexColor = GL.GetAttribLocation(pgmColorID, "vertexColor");
            uniformModelMatrix = GL.GetUniformLocation(pgmID, "modelMatrix");
            uniformViewMatrix = GL.GetUniformLocation(pgmID, "viewMatrix");
            uniformProjMatrix = GL.GetUniformLocation(pgmID, "projMatrix");

            attributeTexPos = GL.GetAttribLocation(pgmTexID, "vertexPosition");
            attributeTexNormal = GL.GetAttribLocation(pgmTexID, "normalDirection");
            attributeTexUv = GL.GetAttribLocation(pgmTexID, "vertexUV");
            uniformTexModel = GL.GetUniformLocation(pgmTexID, "modelMatrix");
            uniformTexView = GL.GetUniformLocation(pgmTexID, "viewMatrix");
            uniformTexProj = GL.GetUniformLocation(pgmTexID, "projMatrix");
            uniformTexSampler = GL.GetUniformLocation(pgmTexID, "tex");
        }

        private static void LoadShader(string filename, ShaderType type, int program, out int address)
        {
            address = GL.CreateShader(type);
            var str = (string)Properties.Resources.ResourceManager.GetObject(filename);
            GL.ShaderSource(address, str);
            GL.CompileShader(address);
            GL.AttachShader(program, address);
            GL.DeleteShader(address);
        }

        private static void CreateVBO(out int vboAddress, Vector3[] data, int address)
        {
            GL.GenBuffers(1, out vboAddress);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vboAddress);
            GL.BufferData(BufferTarget.ArrayBuffer,
                (IntPtr)(data.Length * Vector3.SizeInBytes),
                data,
                BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(address, 3, VertexAttribPointerType.Float, false, 0, 0);
            GL.EnableVertexAttribArray(address);
        }

        private static void CreateVBO(out int vboAddress, Vector4[] data, int address)
        {
            GL.GenBuffers(1, out vboAddress);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vboAddress);
            GL.BufferData(BufferTarget.ArrayBuffer,
                (IntPtr)(data.Length * Vector4.SizeInBytes),
                data,
                BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(address, 4, VertexAttribPointerType.Float, false, 0, 0);
            GL.EnableVertexAttribArray(address);
        }

        private static void CreateVBO(out int vboAddress, Matrix4 data, int address)
        {
            GL.GenBuffers(1, out vboAddress);
            GL.UniformMatrix4(address, false, ref data);
        }

        private static void CreateEBO(out int address, int[] data)
        {
            GL.GenBuffers(1, out address);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, address);
            GL.BufferData(BufferTarget.ElementArrayBuffer,
                (IntPtr)(data.Length * sizeof(int)),
                data,
                BufferUsageHint.StaticDraw);
        }

        private readonly List<int> glBufferHandles = new List<int>();

        private void CreateVAO()
        {
            GL.DeleteVertexArray(vao);
            // Delete the previous call's buffers so repeated uploads (e.g. animation
            // playback re-uploading every frame) don't leak GPU memory.
            foreach (var h in glBufferHandles)
                GL.DeleteBuffer(h);
            glBufferHandles.Clear();
            GL.GenVertexArrays(1, out vao);
            GL.BindVertexArray(vao);
            CreateVBO(out var vboPositions, vertexData, attributeVertexPosition);
            glBufferHandles.Add(vboPositions);
            if (normalMode == 1)
            {
                CreateVBO(out var vboNormals, normal2Data, attributeNormalDirection);
                glBufferHandles.Add(vboNormals);
            }
            else
            {
                if (normalData != null)
                {
                    CreateVBO(out var vboNormals, normalData, attributeNormalDirection);
                    glBufferHandles.Add(vboNormals);
                }
            }
            CreateVBO(out var vboColors, colorData, attributeVertexColor);
            glBufferHandles.Add(vboColors);
            CreateVBO(out var vboModelMatrix, modelMatrixData, uniformModelMatrix);
            glBufferHandles.Add(vboModelMatrix);
            CreateVBO(out var vboViewMatrix, viewMatrixData, uniformViewMatrix);
            glBufferHandles.Add(vboViewMatrix);
            CreateVBO(out var vboProjMatrix, projMatrixData, uniformProjMatrix);
            glBufferHandles.Add(vboProjMatrix);
            CreateEBO(out var eboElements, indiceData);
            glBufferHandles.Add(eboElements);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            GL.BindVertexArray(0);
        }

        #region Animator preview

        private void PreviewAnimator(Animator animator)
        {
            try
            {
                StopAnimator();
                // Always decode preview textures as PNG. The user's export format
                // (convertType) may be TGA/WebP, which System.Drawing can't decode in
                // DecodeTexture -> the mesh would render white. PNG is always decodable
                // and only affects this in-memory preview, not exported files.
                var imported = new ModelConverter(animator, UnityRift.ImageFormat.Png, skipAnimationsWithoutMesh: true);
                if (imported.MeshList.Count == 0)
                {
                    StatusStripUpdate("Animator has no previewable mesh.");
                    return;
                }
                animPlayer = new AnimationPlayer(imported);
                if (animPlayer.Meshes.Count == 0)
                {
                    StatusStripUpdate("Animator has no previewable mesh.");
                    animPlayer = null;
                    return;
                }

                viewMatrixData = Matrix4.CreateRotationY(-MathF.PI / 4) * Matrix4.CreateRotationX(-MathF.PI / 6);
                glControl1.Visible = true;
                BuildAnimGL();
                EnsureAnimControls();

                animSuppressEvents = true;
                animClipCombo.Items.Clear();
                animClipCombo.Items.Add("(bind pose)");
                foreach (var c in animPlayer.Clips)
                    animClipCombo.Items.Add($"{c.Name}  ({c.Duration:0.00}s)");
                animClipCombo.SelectedIndex = animPlayer.Clips.Count > 0 ? 1 : 0;
                animSuppressEvents = false;

                animClipIndex = animPlayer.Clips.Count > 0 ? 0 : -1;
                animTime = 0f;

                animPanel.Visible = true;
                animPanel.BringToFront();
                UpdateAnimFrame();

                if (animClipIndex >= 0 && animPlayer.Clips[animClipIndex].Duration > 0f)
                {
                    animPlayButton.Text = "Pause";
                    animTimer.Start();
                }
                else
                {
                    animPlayButton.Text = "Play";
                }
                StatusStripUpdate($"Animator preview: {animPlayer.Meshes.Count} mesh(es), {animPlayer.Clips.Count} clip(s). Pick a clip, Play/Pause, scrub the timeline. Mouse to rotate/zoom.");
            }
            catch (Exception ex)
            {
                Logger.Error("Animator preview failed", ex);
                StatusStripUpdate("Animator preview failed: " + ex.Message);
                StopAnimator();
            }
        }

        private void BuildAnimGL()
        {
            // Model bounds -> centering/scale matrix (mouse rotates via viewMatrixData).
            var mn = animPlayer.BoundsMin;
            var mx = animPlayer.BoundsMax;
            var offset = new Vector3((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2, (mn.Z + mx.Z) / 2);
            var dist = new Vector3(mx.X - mn.X, mx.Y - mn.Y, mx.Z - mn.Z);
            var d = Math.Max(1e-5f, dist.Length);
            modelMatrixData = Matrix4.CreateTranslation(-offset) * Matrix4.CreateScale(2f / d);

            DeleteAnimGL();
            try
            {
                glControl1.MakeCurrent();
            }
            catch
            {
                return; // GL context not ready yet; first paint/re-select will build it
            }
            EnsureWhiteTex();

            foreach (var pm in animPlayer.Meshes)
            {
                var gm = new AnimGLMesh();
                gm.PosBuf = new Vector3[pm.VertexCount];
                gm.NorBuf = new Vector3[pm.VertexCount];
                var uvBuf = new Vector2[pm.VertexCount];
                for (var v = 0; v < pm.VertexCount; v++)
                {
                    var uv = pm.UV0 != null ? pm.UV0[v] : null;
                    // glTF/GL sample from the top; Unity UV origin is bottom -> flip V.
                    uvBuf[v] = uv != null && uv.Length >= 2 ? new Vector2(uv[0], 1f - uv[1]) : Vector2.Zero;
                }

                GL.GenVertexArrays(1, out gm.Vao);
                GL.BindVertexArray(gm.Vao);

                GL.GenBuffers(1, out gm.Pos);
                GL.BindBuffer(BufferTarget.ArrayBuffer, gm.Pos);
                GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(pm.VertexCount * Vector3.SizeInBytes), IntPtr.Zero, BufferUsageHint.DynamicDraw);
                GL.VertexAttribPointer(attributeTexPos, 3, VertexAttribPointerType.Float, false, 0, 0);
                GL.EnableVertexAttribArray(attributeTexPos);

                GL.GenBuffers(1, out gm.Nor);
                GL.BindBuffer(BufferTarget.ArrayBuffer, gm.Nor);
                GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(pm.VertexCount * Vector3.SizeInBytes), IntPtr.Zero, BufferUsageHint.DynamicDraw);
                GL.VertexAttribPointer(attributeTexNormal, 3, VertexAttribPointerType.Float, false, 0, 0);
                GL.EnableVertexAttribArray(attributeTexNormal);

                GL.GenBuffers(1, out gm.Uv);
                GL.BindBuffer(BufferTarget.ArrayBuffer, gm.Uv);
                GL.BufferData(BufferTarget.ArrayBuffer, (IntPtr)(pm.VertexCount * Vector2.SizeInBytes), uvBuf, BufferUsageHint.StaticDraw);
                GL.VertexAttribPointer(attributeTexUv, 2, VertexAttribPointerType.Float, false, 0, 0);
                GL.EnableVertexAttribArray(attributeTexUv);

                GL.BindVertexArray(0); // keep the element buffer out of the VAO; bound per-submesh at draw
                GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

                // One index buffer + texture per material submesh.
                foreach (var sub in pm.Submeshes)
                {
                    var sg = new AnimGLSub { Count = sub.Indices.Length };
                    GL.GenBuffers(1, out sg.Ebo);
                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, sg.Ebo);
                    GL.BufferData(BufferTarget.ElementArrayBuffer, (IntPtr)(sub.Indices.Length * sizeof(int)), sub.Indices, BufferUsageHint.StaticDraw);
                    var decoded = DecodeTexture(sub.BaseColorTexture);
                    sg.Tex = decoded >= 0 ? decoded : animDefaultTex;
                    sg.OwnsTex = decoded >= 0;
                    gm.Subs.Add(sg);
                }
                GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
                animGL.Add(gm);
            }
        }

        private void UpdateAnimFrame()
        {
            if (animPlayer == null)
                return;
            animPlayer.Evaluate(animClipIndex, animTime);
            if (animTimeLabel != null)
            {
                var dur = animClipIndex >= 0 && animClipIndex < animPlayer.Clips.Count ? animPlayer.Clips[animClipIndex].Duration : 0f;
                animTimeLabel.Text = $"{animTime:0.00} / {dur:0.00}s";
            }
            if (!glControlLoaded || animGL.Count != animPlayer.Meshes.Count)
                return;
            glControl1.MakeCurrent();
            for (var m = 0; m < animPlayer.Meshes.Count; m++)
            {
                var pm = animPlayer.Meshes[m];
                var gm = animGL[m];
                for (var v = 0; v < pm.VertexCount; v++)
                {
                    var p = pm.Positions[v];
                    var n = pm.Normals[v];
                    gm.PosBuf[v] = new Vector3(p.X, p.Y, p.Z);
                    gm.NorBuf[v] = new Vector3(n.X, n.Y, n.Z);
                }
                GL.BindBuffer(BufferTarget.ArrayBuffer, gm.Pos);
                GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, (IntPtr)(pm.VertexCount * Vector3.SizeInBytes), gm.PosBuf);
                GL.BindBuffer(BufferTarget.ArrayBuffer, gm.Nor);
                GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, (IntPtr)(pm.VertexCount * Vector3.SizeInBytes), gm.NorBuf);
            }
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            glControl1.Invalidate();
        }

        private void PaintAnimator()
        {
            GL.UseProgram(pgmTexID);
            GL.UniformMatrix4(uniformTexModel, false, ref modelMatrixData);
            GL.UniformMatrix4(uniformTexView, false, ref viewMatrixData);
            GL.UniformMatrix4(uniformTexProj, false, ref projMatrixData);
            GL.Uniform1(uniformTexSampler, 0);
#if NETFRAMEWORK
            GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
#else
            GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
#endif
            GL.ActiveTexture(TextureUnit.Texture0);
            foreach (var gm in animGL)
            {
                GL.BindVertexArray(gm.Vao);
                foreach (var sg in gm.Subs)
                {
                    GL.BindTexture(TextureTarget.Texture2D, sg.Tex);
                    GL.BindBuffer(BufferTarget.ElementArrayBuffer, sg.Ebo);
                    GL.DrawElements(BeginMode.Triangles, sg.Count, DrawElementsType.UnsignedInt, 0);
                }
            }
            GL.BindVertexArray(0);
        }

        private int DecodeTexture(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return -1;
            try
            {
                using (var ms = new MemoryStream(bytes))
                using (var bmp = new System.Drawing.Bitmap(ms))
                {
                    var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
                    var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    GL.GenTextures(1, out int tex);
                    GL.BindTexture(TextureTarget.Texture2D, tex);
                    GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
                    GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, bmp.Width, bmp.Height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);
                    bmp.UnlockBits(data);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
                    GL.BindTexture(TextureTarget.Texture2D, 0);
                    return tex;
                }
            }
            catch
            {
                return -1;
            }
        }

        private void EnsureWhiteTex()
        {
            if (animDefaultTex >= 0)
                return;
            GL.GenTextures(1, out animDefaultTex);
            GL.BindTexture(TextureTarget.Texture2D, animDefaultTex);
            var white = new byte[] { 255, 255, 255, 255 };
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, 1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, white);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.BindTexture(TextureTarget.Texture2D, 0);
        }

        private void DeleteAnimGL()
        {
            if (animGL.Count == 0)
                return;
            try
            {
                glControl1.MakeCurrent();
                foreach (var gm in animGL)
                {
                    GL.DeleteVertexArray(gm.Vao);
                    GL.DeleteBuffer(gm.Pos);
                    GL.DeleteBuffer(gm.Nor);
                    GL.DeleteBuffer(gm.Uv);
                    foreach (var sg in gm.Subs)
                    {
                        GL.DeleteBuffer(sg.Ebo);
                        if (sg.OwnsTex)
                            GL.DeleteTexture(sg.Tex);
                    }
                }
            }
            catch { }
            animGL.Clear();
        }

        private void EnsureAnimControls()
        {
            if (animPanel != null)
                return;

            animPanel = new Panel { Height = 34, Visible = false, Padding = new Padding(6, 4, 6, 4), BackColor = System.Drawing.SystemColors.Control };
            animPanel.Location = new Point(0, Math.Max(0, previewPanel.ClientSize.Height - animPanel.Height));
            animPanel.Width = previewPanel.ClientSize.Width;
            animPanel.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

            animClipCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240, Dock = DockStyle.Left, Margin = new Padding(0, 0, 6, 0), FlatStyle = FlatStyle.System };
            animPlayButton = new Button { Text = "Pause", Width = 64, Dock = DockStyle.Left, FlatStyle = FlatStyle.System };
            animLoopCheck = new CheckBox { Text = "Loop", Checked = true, Width = 54, Dock = DockStyle.Left, TextAlign = System.Drawing.ContentAlignment.MiddleCenter };
            animTimeLabel = new Label { Text = "0.00 / 0.00s", Width = 96, Dock = DockStyle.Right, TextAlign = System.Drawing.ContentAlignment.MiddleRight, AutoSize = false };
            animTrackBar = new TrackBar { Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, Dock = DockStyle.Fill };

            // Docking fills in reverse add-order, so add Fill first (backmost) and the
            // left-group controls last (frontmost) to get: combo | play | loop |=track=| time
            animPanel.Controls.Add(animTrackBar);
            animPanel.Controls.Add(animTimeLabel);
            animPanel.Controls.Add(animLoopCheck);
            animPanel.Controls.Add(animPlayButton);
            animPanel.Controls.Add(animClipCombo);
            previewPanel.Controls.Add(animPanel);

            animClipCombo.SelectedIndexChanged += (s, e) => { if (!animSuppressEvents) SetAnimClip(animClipCombo.SelectedIndex); };
            animPlayButton.Click += (s, e) => ToggleAnimPlay();
            animTrackBar.Scroll += (s, e) => { if (!animSuppressEvents) ScrubAnim(animTrackBar.Value / 1000f); };

            animTimer = new System.Windows.Forms.Timer { Interval = 33 };
            animTimer.Tick += AnimTimer_Tick;
        }

        private void AnimTimer_Tick(object sender, EventArgs e)
        {
            if (animPlayer == null || animClipIndex < 0)
                return;
            var dur = animPlayer.Clips[animClipIndex].Duration;
            if (dur <= 0f)
            {
                animTimer.Stop();
                return;
            }
            animTime += animTimer.Interval / 1000f;
            if (animTime > dur)
            {
                if (animLoopCheck != null && animLoopCheck.Checked)
                {
                    animTime -= dur;
                }
                else
                {
                    animTime = dur;
                    animTimer.Stop();
                    animPlayButton.Text = "Play";
                }
            }
            animSuppressEvents = true;
            animTrackBar.Value = (int)Math.Min(1000, Math.Max(0, animTime / dur * 1000));
            animSuppressEvents = false;
            UpdateAnimFrame();
        }

        private void SetAnimClip(int comboIndex)
        {
            if (animPlayer == null)
                return;
            animClipIndex = comboIndex - 1; // 0 => bind pose (-1)
            animTime = 0f;
            animSuppressEvents = true;
            animTrackBar.Value = 0;
            animSuppressEvents = false;
            if (animClipIndex >= 0 && animPlayer.Clips[animClipIndex].Duration > 0f)
            {
                animTimer.Start();
                animPlayButton.Text = "Pause";
            }
            else
            {
                animTimer.Stop();
                animPlayButton.Text = "Play";
            }
            UpdateAnimFrame();
        }

        private void ToggleAnimPlay()
        {
            if (animPlayer == null)
                return;
            if (animTimer.Enabled)
            {
                animTimer.Stop();
                animPlayButton.Text = "Play";
            }
            else if (animClipIndex >= 0 && animPlayer.Clips[animClipIndex].Duration > 0f)
            {
                animTimer.Start();
                animPlayButton.Text = "Pause";
            }
        }

        private void ScrubAnim(float fraction)
        {
            if (animPlayer == null || animClipIndex < 0)
                return;
            animTimer.Stop();
            animPlayButton.Text = "Play";
            animTime = fraction * animPlayer.Clips[animClipIndex].Duration;
            UpdateAnimFrame();
        }

        private void StopAnimator()
        {
            animTimer?.Stop();
            if (animPanel != null)
                animPanel.Visible = false;
            DeleteAnimGL();
            animPlayer = null;
            animClipIndex = -1;
            animTime = 0f;
        }

        #endregion

        private void ChangeGLSize(Size size)
        {
            GL.Viewport(0, 0, size.Width, size.Height);

            if (size.Width <= size.Height)
            {
                float k = 1.0f * size.Width / size.Height;
                projMatrixData = Matrix4.CreateScale(1, k, 1);
            }
            else
            {
                float k = 1.0f * size.Height / size.Width;
                projMatrixData = Matrix4.CreateScale(k, 1, 1);
            }
        }

        private void glControl1_Load(object sender, EventArgs e)
        {
            InitOpenTK();
            glControlLoaded = true;
        }

        private void glControl1_Paint(object sender, PaintEventArgs e)
        {
            glControl1.MakeCurrent();
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            if (animGL.Count > 0)
            {
                PaintAnimator();
                GL.Flush();
                glControl1.SwapBuffers();
                return;
            }
            GL.BindVertexArray(vao);
            if (wireFrameMode == 0 || wireFrameMode == 2)
            {
                GL.UseProgram(shadeMode == 0 ? pgmID : pgmColorID);
                GL.UniformMatrix4(uniformModelMatrix, false, ref modelMatrixData);
                GL.UniformMatrix4(uniformViewMatrix, false, ref viewMatrixData);
                GL.UniformMatrix4(uniformProjMatrix, false, ref projMatrixData);
#if NETFRAMEWORK
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
#else
                GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
#endif
                GL.DrawElements(BeginMode.Triangles, indiceData.Length, DrawElementsType.UnsignedInt, 0);
            }
            //Wireframe
            if (wireFrameMode == 1 || wireFrameMode == 2)
            {
                GL.Enable(EnableCap.PolygonOffsetLine);
                GL.PolygonOffset(-1, -1);
                GL.UseProgram(pgmBlackID);
                GL.UniformMatrix4(uniformModelMatrix, false, ref modelMatrixData);
                GL.UniformMatrix4(uniformViewMatrix, false, ref viewMatrixData);
                GL.UniformMatrix4(uniformProjMatrix, false, ref projMatrixData);
#if NETFRAMEWORK
                GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
#else
                GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
#endif
                GL.DrawElements(BeginMode.Triangles, indiceData.Length, DrawElementsType.UnsignedInt, 0);
                GL.Disable(EnableCap.PolygonOffsetLine);
            }
            GL.BindVertexArray(0);
            GL.Flush();
            glControl1.SwapBuffers();
        }

        private void glControl1_MouseWheel(object sender, MouseEventArgs e)
        {
            if (glControl1.Visible)
            {
                viewMatrixData *= Matrix4.CreateScale(1 + e.Delta / 1000f);
                glControl1.Invalidate();
            }
        }

        private void glControl1_MouseDown(object sender, MouseEventArgs e)
        {
            mdx = e.X;
            mdy = e.Y;
            if (e.Button == MouseButtons.Left)
            {
                lmdown = true;
            }
            if (e.Button == MouseButtons.Right)
            {
                rmdown = true;
            }
        }

        private void glControl1_MouseMove(object sender, MouseEventArgs e)
        {
            if (lmdown || rmdown)
            {
                float dx = mdx - e.X;
                float dy = mdy - e.Y;
                mdx = e.X;
                mdy = e.Y;
                if (lmdown)
                {
                    dx *= 0.01f;
                    dy *= 0.01f;
                    viewMatrixData *= Matrix4.CreateRotationX(dy);
                    viewMatrixData *= Matrix4.CreateRotationY(dx);
                }
                if (rmdown)
                {
                    dx *= 0.003f;
                    dy *= 0.003f;
                    viewMatrixData *= Matrix4.CreateTranslation(-dx, dy, 0);
                }
                glControl1.Invalidate();
            }
        }

        private void glControl1_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                lmdown = false;
            }
            if (e.Button == MouseButtons.Right)
            {
                rmdown = false;
            }
        }
        #endregion
    }
}
