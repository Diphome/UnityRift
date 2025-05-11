using AssetStudio;
using AssetStudioCLI.Options;
using CubismLive2DExtractor;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using static AssetStudioCLI.Exporter;
using Ansi = AssetStudio.ColorConsole;

namespace AssetStudioCLI
{
    internal static class Studio
    {
        public static AssetsManager assetsManager = new AssetsManager();
        public static List<AssetItem> parsedAssetsList = new List<AssetItem>();
        public static List<BaseNode> gameObjectTree = new List<BaseNode>();
        public static AssemblyLoader assemblyLoader = new AssemblyLoader();
        public static Dictionary<MonoBehaviour, CubismModel> l2dModelDict = new Dictionary<MonoBehaviour, CubismModel>();
        private static Dictionary<AssetStudio.Object, string> containers = new Dictionary<AssetStudio.Object, string>();

        static Studio()
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("en-US");
            Progress.Default = new Progress<int>(ShowCurProgressValue);
        }

        private static void ShowCurProgressValue(int value)
        {
            Console.Write($"[{value:000}%]\r");
        }

        public static void ExtractBundles()
        {
            var extractedCount = 0;
            var path = CLIOptions.inputPath;
            var savePath = CLIOptions.o_outputFolder.Value;
            Progress.Reset();
            if (Directory.Exists(path))
            {
                var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
                var totalCount = files.Length;
                for (var i = 0; i < totalCount; i++)
                {
                    var file = files[i];
                    var fileOriPath = Path.GetDirectoryName(file);
                    var fileSavePath = fileOriPath.Replace(path, savePath);
                    extractedCount += ExtractFile(file, fileSavePath);
                    Progress.Report(i + 1, totalCount);
                }
            }
            else if (File.Exists(path))
            {
                extractedCount += ExtractFile(path, savePath);
            }

            var status = extractedCount > 0
                ? $"Finished extracting {extractedCount} file(s) to \"{savePath.Color(Ansi.BrightCyan)}\""
                : "Nothing extracted (not extractable or file(s) already exist)";
            Logger.Default.Log(LoggerEvent.Info, status, ignoreLevel: true);
        }

        public static int ExtractFile(string fileName, string savePath)
        {
            var extractedCount = 0;
            var reader = new FileReader(fileName);
            switch (reader.FileType)
            {
                case FileType.BundleFile:
                    extractedCount += ExtractBundleFile(reader, savePath);
                    break;
                case FileType.WebFile:
                    extractedCount += ExtractWebDataFile(reader, savePath);
                    break;
                default:
                    reader.Dispose();
                    break;
            }
            return extractedCount;
        }

        private static int ExtractBundleFile(FileReader reader, string savePath)
        {
            Logger.Info($"Decompressing {reader.FileName} ...");

            Logger.Debug($"Bundle offset: {reader.Position}");
            var count = 0;
            var bundleStream = new OffsetStream(reader);
            var bundleReader = new FileReader(reader.FullPath, bundleStream);
            var bundleFile = new BundleFile(bundleReader, assetsManager.CustomBlockInfoCompression, assetsManager.CustomBlockCompression, assetsManager.SpecifyUnityVersion);
            var extractPath = Path.Combine(savePath, reader.FileName + "_unpacked");
            if (bundleFile.fileList.Length > 0)
            {
                count += ExtractStreamFile(extractPath, bundleFile.fileList);
            }
            while (bundleFile.IsMultiBundle)
            {
                bundleStream.Offset = reader.Position;
                bundleReader = new FileReader($"{reader.FullPath}_0x{bundleStream.Offset:X}", bundleStream);
                if (bundleReader.Position > 0)
                {
                    bundleStream.Offset += bundleReader.Position;
                    bundleReader.FullPath = $"{reader.FullPath}_0x{bundleStream.Offset:X}";
                    bundleReader.FileName = $"{reader.FileName}_0x{bundleStream.Offset:X}";
                }
                Logger.Info($"[MultiBundle] Decompressing \"{reader.FileName}\" from offset: 0x{bundleStream.Offset:X}..");
                bundleFile = new BundleFile(bundleReader, assetsManager.CustomBlockInfoCompression, assetsManager.CustomBlockCompression, assetsManager.SpecifyUnityVersion);
                if (bundleFile.fileList.Length > 0)
                {
                    count += ExtractStreamFile(extractPath, bundleFile.fileList);
                }
            }
            bundleStream.Dispose();
            return count;
        }

        private static int ExtractWebDataFile(FileReader reader, string savePath)
        {
            Logger.Info($"Decompressing {reader.FileName} ...");
            var webFile = new WebFile(reader);
            reader.Dispose();
            if (webFile.fileList.Length > 0)
            {
                var extractPath = Path.Combine(savePath, reader.FileName + "_unpacked");
                return ExtractStreamFile(extractPath, webFile.fileList);
            }
            return 0;
        }

        private static int ExtractStreamFile(string extractPath, StreamFile[] fileList)
        {
            var extractedCount = 0;
            foreach (var file in fileList)
            {
                var filePath = Path.Combine(extractPath, file.path);
                var fileDirectory = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(fileDirectory))
                {
                    Directory.CreateDirectory(fileDirectory);
                }
                if (!File.Exists(filePath))
                {
                    using (var fileStream = File.Create(filePath))
                    {
                        file.stream.CopyTo(fileStream);
                    }
                    extractedCount += 1;
                }
                file.stream.Dispose();
            }
            return extractedCount;
        }

        public static bool LoadAssets()
        {
            var isLoaded = false;
            assetsManager.SpecifyUnityVersion = CLIOptions.o_unityVersion.Value;
            assetsManager.CustomBlockInfoCompression = CLIOptions.o_bundleBlockInfoCompression.Value;
            assetsManager.CustomBlockCompression = CLIOptions.o_bundleBlockCompression.Value;
            assetsManager.LoadingViaTypeTreeEnabled = !CLIOptions.f_avoidLoadingViaTypetree.Value;
            if (!CLIOptions.f_loadAllAssets.Value)
            {
                assetsManager.SetAssetFilter(CLIOptions.o_exportAssetTypes.Value);
            }
            assetsManager.LoadFilesAndFolders(CLIOptions.inputPath);
            if (assetsManager.assetsFileList.Count == 0)
            {
                Logger.Warning("No Unity file can be loaded.");
            }
            else
            {
                isLoaded = true;
            }

            return isLoaded;
        }

        public static void ParseAssets()
        {
            Logger.Info("Parse assets...");

            var fileAssetsList = new List<AssetItem>();
            var tex2dArrayAssetList = new List<AssetItem>();
            var objectCount = assetsManager.assetsFileList.Sum(x => x.Objects.Count);
            var objectAssetItemDic = new Dictionary<AssetStudio.Object, AssetItem>(objectCount);
            var isL2dMode = CLIOptions.o_workMode.Value == WorkMode.Live2D;

            Progress.Reset();
            var i = 0;
            foreach (var assetsFile in assetsManager.assetsFileList)
            {
                var preloadTable = Array.Empty<PPtr<AssetStudio.Object>>();
                foreach (var asset in assetsFile.Objects)
                {
                    var assetItem = new AssetItem(asset);
                    objectAssetItemDic.Add(asset, assetItem);
                    assetItem.UniqueID = "_#" + i;
                    var isExportable = false;
                    switch (asset)
                    {
                        case PreloadData m_PreloadData:
                            preloadTable = m_PreloadData.m_Assets;
                            break;
                        case AssetBundle m_AssetBundle:
                            var isStreamedSceneAssetBundle = m_AssetBundle.m_IsStreamedSceneAssetBundle;
                            if (!isStreamedSceneAssetBundle)
                            {
                                preloadTable = m_AssetBundle.m_PreloadTable;
                            }
                            assetItem.Text = string.IsNullOrEmpty(m_AssetBundle.m_AssetBundleName) ? m_AssetBundle.m_Name : m_AssetBundle.m_AssetBundleName;

                            foreach (var m_Container in m_AssetBundle.m_Container)
                            {
                                var preloadIndex = m_Container.Value.preloadIndex;
                                var preloadSize = isStreamedSceneAssetBundle ? preloadTable.Length : m_Container.Value.preloadSize;
                                var preloadEnd = preloadIndex + preloadSize;
                                for (var k = preloadIndex; k < preloadEnd; k++)
                                {
                                    var pptr = preloadTable[k];
                                    if (pptr.TryGet(out var obj))
                                    {
                                        containers[obj] = m_Container.Key;
                                    }
                                }
                            }
                            break;
                        case ResourceManager m_ResourceManager:
                            foreach (var m_Container in m_ResourceManager.m_Container)
                            {
                                if (m_Container.Value.TryGet(out var obj))
                                {
                                    containers[obj] = m_Container.Key;
                                }
                            }
                            break;
                        case Texture2D m_Texture2D:
                            if (!string.IsNullOrEmpty(m_Texture2D.m_StreamData?.path))
                                assetItem.FullSize = asset.byteSize + m_Texture2D.m_StreamData.size;
                            assetItem.Text = m_Texture2D.m_Name;
                            break;
                        case Texture2DArray m_Texture2DArray:
                            if (!string.IsNullOrEmpty(m_Texture2DArray.m_StreamData?.path))
                                assetItem.FullSize = asset.byteSize + m_Texture2DArray.m_StreamData.size;
                            assetItem.Text = m_Texture2DArray.m_Name;
                            tex2dArrayAssetList.Add(assetItem);
                            break;
                        case AudioClip m_AudioClip:
                            if (!string.IsNullOrEmpty(m_AudioClip.m_Source))
                                assetItem.FullSize = asset.byteSize + m_AudioClip.m_Size;
                            assetItem.Text = m_AudioClip.m_Name;
                            break;
                        case VideoClip m_VideoClip:
                            if (!string.IsNullOrEmpty(m_VideoClip.m_OriginalPath))
                                assetItem.FullSize = asset.byteSize + m_VideoClip.m_ExternalResources.m_Size;
                            assetItem.Text = m_VideoClip.m_Name;
                            break;
                        case Shader m_Shader:
                            assetItem.Text = m_Shader.m_ParsedForm?.m_Name ?? m_Shader.m_Name;
                            break;
                        case MonoBehaviour m_MonoBehaviour:
                            var assetName = m_MonoBehaviour.m_Name;
                            if (m_MonoBehaviour.m_Script.TryGet(out var m_Script))
                            {
                                assetName = assetName == "" ? m_Script.m_ClassName : assetName;
                                switch (m_Script.m_ClassName)
                                {
                                    case "CubismMoc":
                                        if (!l2dModelDict.ContainsKey(m_MonoBehaviour))
                                        {
                                            l2dModelDict.Add(m_MonoBehaviour, null);
                                        }
                                        break;
                                    case "CubismRenderer":
                                        BindCubismRenderer(m_MonoBehaviour);
                                        break;
                                    case "CubismDisplayInfoParameterName":
                                        BindParamDisplayInfo(m_MonoBehaviour);
                                        break;
                                    case "CubismDisplayInfoPartName":
                                        BindPartDisplayInfo(m_MonoBehaviour);
                                        break;
                                    case "CubismPosePart":
                                        BindCubismPosePart(m_MonoBehaviour);
                                        break;
                                }
                            }
                            assetItem.Text = assetName;
                            break;
                        case GameObject m_GameObject:
                            assetItem.Text = m_GameObject.m_Name;
                            if (m_GameObject.CubismModel != null && TryGetCubismMoc(m_GameObject.CubismModel.CubismModelMono, out var mocMono))
                            {
                                l2dModelDict[mocMono] = m_GameObject.CubismModel;
                                BindAnimationClips(m_GameObject);
                            }
                            break;
                        case Animator m_Animator:
                            if (m_Animator.m_GameObject.TryGet(out var gameObject))
                            {
                                assetItem.Text = gameObject.m_Name;
                            }
                            break;
                        case NamedObject m_NamedObject:
                            assetItem.Text = m_NamedObject.m_Name;
                            break;
                    }
                    if (string.IsNullOrEmpty(assetItem.Text))
                    {
                        assetItem.Text = assetItem.TypeString + assetItem.UniqueID;
                    }

                    isExportable = CLIOptions.o_exportAssetTypes.Value.Contains(asset.type);
                    if (isExportable || (CLIOptions.f_loadAllAssets.Value && CLIOptions.o_exportAssetTypes.Value == CLIOptions.o_exportAssetTypes.DefaultValue))
                    {
                        fileAssetsList.Add(assetItem);
                    }

                    asset.Name = assetItem.Text;
                    Progress.Report(++i, objectCount);
                }

                foreach (var asset in fileAssetsList)
                {
                    if (containers.TryGetValue(asset.Asset, out var container))
                    {
                        asset.Container = container;

                        if (asset.Asset is GameObject m_GameObject && m_GameObject.CubismModel != null)
                        {
                            m_GameObject.CubismModel.Container = container;
                        }
                    }
                }
                foreach (var tex2dAssetItem in tex2dArrayAssetList)
                {
                    var m_Texture2DArray = (Texture2DArray)tex2dAssetItem.Asset;
                    for (var layer = 0; layer < m_Texture2DArray.m_Depth; layer++)
                    {
                        var fakeObj = new Texture2D(m_Texture2DArray, layer);
                        m_Texture2DArray.TextureList.Add(fakeObj);
                    }
                }
                parsedAssetsList.AddRange(fileAssetsList);
                fileAssetsList.Clear();
                tex2dArrayAssetList.Clear();
                if (!isL2dMode)
                {
                    containers.Clear();
                }
            }

            if (CLIOptions.o_workMode.Value == WorkMode.SplitObjects || CLIOptions.o_groupAssetsBy.Value == AssetGroupOption.SceneHierarchy)
            {
                BuildTreeStructure(objectAssetItemDic);
            }

            var log = $"Finished loading {assetsManager.assetsFileList.Count} files with {parsedAssetsList.Count} exportable assets";
            var unityVer = assetsManager.assetsFileList[0].version;
            long m_ObjectsCount;
            if (unityVer > 2020)
            {
                m_ObjectsCount = assetsManager.assetsFileList.Sum(x => x.m_Objects.LongCount(y =>
                    y.classID != (int)ClassIDType.Shader
                    && CLIOptions.o_exportAssetTypes.Value.Any(k => (int)k == y.classID))
                );
            }
            else
            {
                m_ObjectsCount = assetsManager.assetsFileList.Sum(x => x.m_Objects.LongCount(y => CLIOptions.o_exportAssetTypes.Value.Any(k => (int)k == y.classID)));
            }
            var objectsCount = assetsManager.assetsFileList.Sum(x => x.Objects.LongCount(y => CLIOptions.o_exportAssetTypes.Value.Any(k => k == y.type)));
            if (m_ObjectsCount != objectsCount)
            {
                log += $" and {m_ObjectsCount - objectsCount} assets failed to read";
            }
            Logger.Info(log);
        }

        public static void BuildTreeStructure(Dictionary<AssetStudio.Object, AssetItem> objectAssetItemDic)
        {
            Logger.Info("Building tree structure...");

            var treeNodeDictionary = new Dictionary<GameObject, GameObjectNode>();
            var assetsFileCount = assetsManager.assetsFileList.Count;
            int j = 0;
            Progress.Reset();
            foreach (var assetsFile in assetsManager.assetsFileList)
            {
                var fileNode = new BaseNode(assetsFile.fileName);  //RootNode

                foreach (var obj in assetsFile.Objects)
                {
                    if (obj is GameObject m_GameObject)
                    {
                        if (!treeNodeDictionary.TryGetValue(m_GameObject, out var currentNode))
                        {
                            currentNode = new GameObjectNode(m_GameObject);
                            treeNodeDictionary.Add(m_GameObject, currentNode);
                        }
                        
                        foreach (var pptr in m_GameObject.m_Components)
                        {
                            if (pptr.TryGet(out var m_Component))
                            {
                                objectAssetItemDic[m_Component].Node = currentNode;
                                if (m_Component is MeshFilter m_MeshFilter)
                                {
                                    if (m_MeshFilter.m_Mesh.TryGet(out var m_Mesh))
                                    {
                                        objectAssetItemDic[m_Mesh].Node = currentNode;
                                    }
                                }
                                else if (m_Component is SkinnedMeshRenderer m_SkinnedMeshRenderer)
                                {
                                    if (m_SkinnedMeshRenderer.m_Mesh.TryGet(out var m_Mesh))
                                    {
                                        objectAssetItemDic[m_Mesh].Node = currentNode;
                                    }
                                }
                            }
                        }

                        var parentNode = fileNode;
                        if (m_GameObject.m_Transform != null)
                        {
                            if (m_GameObject.m_Transform.m_Father.TryGet(out var m_Father))
                            {
                                if (m_Father.m_GameObject.TryGet(out var parentGameObject))
                                {
                                    if (!treeNodeDictionary.TryGetValue(parentGameObject, out var parentGameObjectNode))
                                    {
                                        parentGameObjectNode = new GameObjectNode(parentGameObject);
                                        treeNodeDictionary.Add(parentGameObject, parentGameObjectNode);
                                    }
                                    parentNode = parentGameObjectNode;
                                }
                            }
                        }
                        parentNode.nodes.Add(currentNode);
                    }
                }

                if (fileNode.nodes.Count > 0)
                {
                    GenerateFullPath(fileNode, fileNode.Text);
                    gameObjectTree.Add(fileNode);
                }

                Progress.Report(++j, assetsFileCount);
            }

            treeNodeDictionary.Clear();
            objectAssetItemDic.Clear();
        }

        private static void GenerateFullPath(BaseNode treeNode, string path)
        {
            treeNode.FullPath = path;
            foreach (var node in treeNode.nodes)
            {
                if (node.nodes.Count > 0)
                {
                    GenerateFullPath(node, Path.Combine(path, node.Text));
                }
                else
                {
                    node.FullPath = Path.Combine(path, node.Text);
                }
            }
        }

        public static void ShowExportableAssetsInfo()
        {
            var exportableAssetsCountDict = new Dictionary<ClassIDType, int>();
            string info = "";
            if (parsedAssetsList.Count > 0)
            {
                foreach (var asset in parsedAssetsList)
                {
                    if (exportableAssetsCountDict.ContainsKey(asset.Type))
                    {
                        exportableAssetsCountDict[asset.Type] += 1;
                    }
                    else
                    {
                        exportableAssetsCountDict.Add(asset.Type, 1);
                    }
                }

                info += "\n[Exportable Assets Count]\n";
                foreach (var assetType in exportableAssetsCountDict.Keys)
                {
                    info += $"# {assetType}: {exportableAssetsCountDict[assetType]}\n";
                }
                if (exportableAssetsCountDict.Count > 1)
                {
                    info += $"#\n# Total: {parsedAssetsList.Count} assets";
                }
            }
            else
            {
                info += "No exportable assets found.";
            }

            if (CLIOptions.o_logLevel.Value > LoggerEvent.Info)
            {
                Console.WriteLine(info);
            }
            else
            {
                Logger.Info(info);
            }
        }

        public static void Filter()
        {
            switch (CLIOptions.o_workMode.Value)
            {
                case WorkMode.Live2D:
                case WorkMode.SplitObjects:
                    break;
                default:
                    FilterAssets();
                    break;
            }
        }

        private static void FilterAssets()
        {
            var assetsCount = parsedAssetsList.Count;
            var filteredAssets = new List<AssetItem>();
            var regexMode = CLIOptions.f_filterWithRegex.Value;
            Regex regex;

            switch(CLIOptions.filterBy)
            {
                case FilterBy.Name when regexMode:
                    regex = new Regex(CLIOptions.o_filterByName.Value[0]);
                    filteredAssets = parsedAssetsList.FindAll(x => regex.IsMatch(x.Text));
                    Logger.Info(
                        $"Found [{filteredAssets.Count}/{assetsCount}] asset(s) " +
                        $"which Names match {regex.ToString().Color(Ansi.BrightYellow)} regexp."
                    );
                    break;
                case FilterBy.Name:
                    filteredAssets = parsedAssetsList.FindAll(x => CLIOptions.o_filterByName.Value.Any(y => x.Text.IndexOf(y, StringComparison.OrdinalIgnoreCase) >= 0));
                    Logger.Info(
                        $"Found [{filteredAssets.Count}/{assetsCount}] asset(s) " +
                        $"that contain {$"\"{string.Join("\", \"", CLIOptions.o_filterByName.Value)}\"".Color(Ansi.BrightYellow)} in their Names."
                    );
                    break;
                case FilterBy.Container when regexMode:
                    regex = new Regex(CLIOptions.o_filterByContainer.Value[0]);
                    filteredAssets = parsedAssetsList.FindAll(x => regex.IsMatch(x.Container));
                    Logger.Info(
                        $"Found [{filteredAssets.Count}/{assetsCount}] asset(s) " +
                        $"which Containers match {regex.ToString().Color(Ansi.BrightYellow)} regexp."
                    );
                    break;
                case FilterBy.Container:
                    filteredAssets = parsedAssetsList.FindAll(x => CLIOptions.o_filterByContainer.Value.Any(y => x.Container.IndexOf(y, StringComparison.OrdinalIgnoreCase) >= 0));
                    Logger.Info(
                        $"Found [{filteredAssets.Count}/{assetsCount}] asset(s) " +
                        $"that contain {$"\"{string.Join("\", \"", CLIOptions.o_filterByContainer.Value)}\"".Color(Ansi.BrightYellow)} in their Containers."
                    );
                    break;
                case FilterBy.PathID:
                    filteredAssets = parsedAssetsList.FindAll(x => CLIOptions.o_filterByPathID.Value.Any(y => x.m_PathID.ToString().IndexOf(y, StringComparison.OrdinalIgnoreCase) >= 0));
                    Logger.Info(
                        $"Found [{filteredAssets.Count}/{assetsCount}] asset(s) " +
                        $"that contain {$"\"{string.Join("\", \"", CLIOptions.o_filterByPathID.Value)}\"".Color(Ansi.BrightYellow)} in their PathIDs."
                    );
                    break;
                case FilterBy.NameOrContainer when regexMode:
                    regex = new Regex(CLIOptions.o_filterByText.Value[0]);
                    filteredAssets = parsedAssetsList.FindAll(x => regex.IsMatch(x.Text) || regex.IsMatch(x.Container));
                    Logger.Info(
                        $"Found [{filteredAssets.Count}/{assetsCount}] asset(s) " +
                        $"which Names or Containers match {regex.ToString().Color(Ansi.BrightYellow)} regexp."
                    );
                    break;
                case FilterBy.NameOrContainer:
                    filteredAssets = parsedAssetsList.FindAll(x =>
                        CLIOptions.o_filterByText.Value.Any(y => x.Text.IndexOf(y, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        CLIOptions.o_filterByText.Value.Any(y => x.Container.IndexOf(y, StringComparison.OrdinalIgnoreCase) >= 0)
                    );
                    Logger.Info(
                        $"Found [{filteredAssets.Count}/{assetsCount}] asset(s) " +
                        $"that contain {$"\"{string.Join("\", \"", CLIOptions.o_filterByText.Value)}\"".Color(Ansi.BrightYellow)} in their Names or Containers."
                    );
                    break;
                case FilterBy.NameAndContainer when regexMode:
                    var nameRegex = new Regex(CLIOptions.o_filterByName.Value[0]);
                    var containerRegex = new Regex(CLIOptions.o_filterByContainer.Value[0]);
                    filteredAssets = parsedAssetsList.FindAll(x => nameRegex.IsMatch(x.Text) && containerRegex.IsMatch(x.Container));
                    Logger.Info(
                        $"Found [{filteredAssets.Count}/{assetsCount}] asset(s) " +
                        $"which Containers match {containerRegex.ToString().Color(Ansi.BrightYellow)} regexp " +
                        $"and which Names match {nameRegex.ToString().Color(Ansi.BrightYellow)} regexp."
                    );
                    break;
                case FilterBy.NameAndContainer:
                    filteredAssets = parsedAssetsList.FindAll(x =>
                        CLIOptions.o_filterByName.Value.Any(y => x.Text.IndexOf(y, StringComparison.OrdinalIgnoreCase) >= 0) &&
                        CLIOptions.o_filterByContainer.Value.Any(y => x.Container.IndexOf(y, StringComparison.OrdinalIgnoreCase) >= 0)
                    );
                    Logger.Info(
                        $"Found [{filteredAssets.Count}/{assetsCount}] asset(s) " +
                        $"that contain {$"\"{string.Join("\", \"", CLIOptions.o_filterByContainer.Value)}\"".Color(Ansi.BrightYellow)} in their Containers " +
                        $"and {$"\"{string.Join("\", \"", CLIOptions.o_filterByName.Value)}\"".Color(Ansi.BrightYellow)} in their Names."
                    );
                    break;
            }
            parsedAssetsList.Clear();
            parsedAssetsList = filteredAssets;
        }

        public static void ExportAssets()
        {
            var savePath = CLIOptions.o_outputFolder.Value;
            var toExportCount = parsedAssetsList.Count;
            var exportedCount = 0;

            var groupOption = CLIOptions.o_groupAssetsBy.Value;
            var parallelExportCount = CLIOptions.o_maxParallelExportTasks.Value;
            var toExportAssetDict = new ConcurrentDictionary<AssetItem, string>();
            var toParallelExportAssetDict = new ConcurrentDictionary<AssetItem, string>();
            Parallel.ForEach(parsedAssetsList, asset =>
            {
                string exportPath;
                switch (groupOption)
                {
                    case AssetGroupOption.TypeName:
                        exportPath = Path.Combine(savePath, asset.TypeString);
                        break;
                    case AssetGroupOption.ContainerPath:
                    case AssetGroupOption.ContainerPathFull:
                        if (!string.IsNullOrEmpty(asset.Container))
                        {
                            exportPath = Path.Combine(savePath, Path.GetDirectoryName(asset.Container));
                            if (groupOption == AssetGroupOption.ContainerPathFull)
                            {
                                exportPath = Path.Combine(exportPath, Path.GetFileNameWithoutExtension(asset.Container));
                            }
                        }
                        else
                        {
                            exportPath = savePath;
                        }
                        break;
                    case AssetGroupOption.SourceFileName:
                        if (string.IsNullOrEmpty(asset.SourceFile.originalPath))
                        {
                            exportPath = Path.Combine(savePath, asset.SourceFile.fileName + "_export");
                        }
                        else
                        {
                            exportPath = Path.Combine(savePath, Path.GetFileName(asset.SourceFile.originalPath) + "_export", asset.SourceFile.fileName);
                        }
                        break;
                    case AssetGroupOption.SceneHierarchy:
                        if (asset.Node != null)
                        {
                            exportPath = Path.Combine(savePath, asset.Node.FullPath);
                        }
                        else
                        {
                            exportPath = Path.Combine(savePath, "_sceneRoot", asset.TypeString);
                        }
                        break;
                    default:
                        exportPath = savePath;
                        break;
                }
                exportPath += Path.DirectorySeparatorChar;

                if (CLIOptions.o_workMode.Value == WorkMode.Export)
                {
                    switch (asset.Type)
                    {
                        case ClassIDType.Texture2D:
                        case ClassIDType.Sprite:
                        case ClassIDType.AudioClip:
                            toParallelExportAssetDict.TryAdd(asset, exportPath);
                            break;
                        case ClassIDType.Texture2DArray:
                            var m_Texture2DArray = (Texture2DArray)asset.Asset;
                            toExportCount += m_Texture2DArray.TextureList.Count - 1;
                            foreach (var texture in m_Texture2DArray.TextureList)
                            {
                                var fakeItem = new AssetItem(texture)
                                {
                                    Text = texture.m_Name,
                                    Container = asset.Container,
                                };
                                toParallelExportAssetDict.TryAdd(fakeItem, exportPath);
                            }
                            break;
                        default:
                            toExportAssetDict.TryAdd(asset, exportPath);
                            break;
                    }
                }
                else
                {
                    toExportAssetDict.TryAdd(asset, exportPath);
                }
            });
            
            foreach (var toExportAsset in toExportAssetDict)
            {
                var asset = toExportAsset.Key;
                var exportPath = toExportAsset.Value;
                var isExported = false;
                try
                {
                    switch (CLIOptions.o_workMode.Value)
                    {
                        case WorkMode.ExportRaw:
                            Logger.Debug($"{CLIOptions.o_workMode}: {asset.Type} : {asset.Container} : {asset.Text}");
                            isExported = ExportRawFile(asset, exportPath);
                            break;
                        case WorkMode.Dump:
                            Logger.Debug($"{CLIOptions.o_workMode}: {asset.Type} : {asset.Container} : {asset.Text}");
                            isExported = ExportDumpFile(asset, exportPath);
                            break;
                        case WorkMode.Export:
                            Logger.Debug($"{CLIOptions.o_workMode}: {asset.Type} : {asset.Container} : {asset.Text}");
                            isExported = ExportConvertFile(asset, exportPath);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"{asset.SourceFile.originalPath ?? asset.SourceFile.fullName}: [{$"{asset.Type}: {asset.Text}".Color(Ansi.BrightRed)}] : Export error\n{ex}");
                }

                if (isExported)
                {
                    exportedCount++;
                }
                Console.Write($"Exported [{exportedCount}/{toExportCount}]\r");
            }

            Parallel.ForEach(toParallelExportAssetDict, new ParallelOptions { MaxDegreeOfParallelism = parallelExportCount }, toExportAsset =>
            {
                var asset = toExportAsset.Key;
                var exportPath = toExportAsset.Value;
                try
                {
                    if (ParallelExporter.ParallelExportConvertFile(asset, exportPath, out var debugLog))
                    {
                        Interlocked.Increment(ref exportedCount);
                        Logger.Debug(debugLog);
                        Console.Write($"Exported [{exportedCount}/{toExportCount}]\r");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"{asset.SourceFile.originalPath ?? asset.SourceFile.fullName}: [{$"{asset.Type}: {asset.Text}".Color(Ansi.BrightRed)}] : Export error\n{ex}");
                }
            });
            ParallelExporter.ClearHash();
            Console.WriteLine("");

            if (exportedCount == 0)
            {
                Logger.Default.Log(LoggerEvent.Info, "Nothing exported.", ignoreLevel: true);
            }
            else if (toExportCount > exportedCount)
            {
                Logger.Default.Log(LoggerEvent.Info, $"Finished exporting {exportedCount} asset(s) to \"{CLIOptions.o_outputFolder.Value.Color(Ansi.BrightYellow)}\".", ignoreLevel: true);
            }
            else
            {
                Logger.Default.Log(LoggerEvent.Info, $"Finished exporting {exportedCount} asset(s) to \"{CLIOptions.o_outputFolder.Value.Color(Ansi.BrightGreen)}\".", ignoreLevel: true);
            }

            if (toExportCount > exportedCount)
            {
                Logger.Default.Log(LoggerEvent.Info, $"{toExportCount - exportedCount} asset(s) skipped (not extractable or file(s) already exist).", ignoreLevel: true);
            }
        }

        public static void ExportAssetList()
        {
            var savePath = CLIOptions.o_outputFolder.Value;

            switch (CLIOptions.o_exportAssetList.Value)
            {
                case ExportListType.XML:
                    var filename = Path.Combine(savePath, "assets.xml");
                    var doc = new XDocument(
                        new XElement("Assets",
                            new XAttribute("filename", filename),
                            new XAttribute("createdAt", DateTime.UtcNow.ToString("s")),
                            parsedAssetsList.Select(
                                asset => new XElement("Asset",
                                    new XElement("Name", asset.Text),
                                    new XElement("Container", asset.Container),
                                    new XElement("Type", new XAttribute("id", (int)asset.Type), asset.TypeString),
                                    new XElement("PathID", asset.m_PathID),
                                    new XElement("Source", asset.SourceFile.fullName),
                                    new XElement("TreeNode", asset.Node != null ? asset.Node.FullPath : ""),
                                    new XElement("Size", asset.FullSize)
                                )
                            )
                        )
                    );
                    doc.Save(filename);

                   break;
            }
            Logger.Info($"Finished exporting asset list with {parsedAssetsList.Count} items.");
        }

        public static void ExportSplitObjects()
        {
            var savePath = CLIOptions.o_outputFolder.Value;
            var searchList = CLIOptions.o_filterByName.Value;
            var isFiltered = CLIOptions.filterBy == FilterBy.Name;

            var exportableObjects = new List<GameObjectNode>();
            var exportedCount = 0;
            var k = 0;

            Logger.Info($"Searching for objects to export..");
            Progress.Reset();
            var count = gameObjectTree.Sum(x => x.nodes.Count);
            foreach (var node in gameObjectTree)
            {
                foreach (GameObjectNode j in node.nodes)
                {
                    if (isFiltered)
                    {
                        if (!searchList.Any(searchText => j.Text.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0))
                            continue;
                    }
                    var gameObjects = new List<GameObject>();
                    CollectNode(j, gameObjects);

                    if (gameObjects.All(x => x.m_SkinnedMeshRenderer == null && x.m_MeshFilter == null))
                    {
                        Progress.Report(++k, count);
                        continue;
                    }
                    exportableObjects.Add(j);
                }
            }
            gameObjectTree.Clear();
            var exportableCount = exportableObjects.Count;
            var log = $"Found {exportableCount} exportable object(s) ";
            if (isFiltered)
            {
                log += $"that contain {$"\"{string.Join("\", \"", CLIOptions.o_filterByName.Value)}\"".Color(Ansi.BrightYellow)} in their Names";
            }
            Logger.Info(log);
            if (exportableCount > 0)
            {
                Progress.Reset();
                k = 0;

                foreach (var gameObjectNode in exportableObjects)
                {
                    var gameObject = gameObjectNode.gameObject;
                    var filename = FixFileName(gameObject.m_Name);
                    var targetPath = $"{savePath}{filename}{Path.DirectorySeparatorChar}";
                    //重名文件处理
                    for (int i = 1; ; i++)
                    {
                        if (Directory.Exists(targetPath))
                        {
                            targetPath = $"{savePath}{filename} ({i}){Path.DirectorySeparatorChar}";
                        }
                        else
                        {
                            break;
                        }
                    }
                    Directory.CreateDirectory(targetPath);
                    //导出FBX
                    Logger.Info($"Exporting {filename}.fbx");
                    Progress.Report(k, exportableCount);
                    try
                    {
                        ExportGameObject(gameObject, targetPath);
                        Logger.Debug($"{gameObject.type} \"{filename}\" saved to \"{targetPath}\"");
                        exportedCount++;
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Export GameObject:{gameObject.m_Name} error", ex);
                    }
                    k++;
                }
            }
            var status = exportedCount > 0
                ? $"Finished exporting [{exportedCount}/{exportableCount}] object(s) to \"{CLIOptions.o_outputFolder.Value.Color(Ansi.BrightCyan)}\""
                : "Nothing exported";
            Logger.Default.Log(LoggerEvent.Info, status, ignoreLevel: true);
        }

        private static void CollectNode(GameObjectNode node, List<GameObject> gameObjects)
        {
            gameObjects.Add(node.gameObject);
            foreach (GameObjectNode i in node.nodes)
            {
                CollectNode(i, gameObjects);
            }
        }

        private static bool TryGetCubismMoc(MonoBehaviour m_MonoBehaviour, out MonoBehaviour mocMono)
        {
            mocMono = null;
            var pptrDict = (OrderedDictionary)CubismParsers.ParseMonoBehaviour(m_MonoBehaviour, CubismParsers.CubismMonoBehaviourType.Model, assemblyLoader)?["_moc"];
            if (pptrDict == null)
                return false;

            var mocPPtr = new PPtr<MonoBehaviour>
            {
                m_FileID = (int)pptrDict["m_FileID"],
                m_PathID = (long)pptrDict["m_PathID"],
                AssetsFile = m_MonoBehaviour.assetsFile
            };
            return mocPPtr.TryGet(out mocMono);
        }

        private static void BindCubismRenderer(MonoBehaviour m_MonoBehaviour)
        {
            if (!m_MonoBehaviour.m_GameObject.TryGet(out var m_GameObject))
                return;

            var rootTransform = GetRootTransform(m_GameObject.m_Transform);
            if (rootTransform.m_GameObject.TryGet(out var rootGameObject) && rootGameObject.CubismModel != null)
            {
                rootGameObject.CubismModel.RenderTextureList.Add(m_MonoBehaviour);
            }
        }

        private static void BindParamDisplayInfo(MonoBehaviour m_MonoBehaviour)
        {
            if (!m_MonoBehaviour.m_GameObject.TryGet(out var m_GameObject))
                return;

            var rootTransform = GetRootTransform(m_GameObject.m_Transform);
            if (rootTransform.m_GameObject.TryGet(out var rootGameObject) && rootGameObject.CubismModel != null)
            {
                rootGameObject.CubismModel.ParamDisplayInfoList.Add(m_MonoBehaviour);
            }
        }

        private static void BindPartDisplayInfo(MonoBehaviour m_MonoBehaviour)
        {
            if (!m_MonoBehaviour.m_GameObject.TryGet(out var m_GameObject))
                return;

            var rootTransform = GetRootTransform(m_GameObject.m_Transform);
            if (rootTransform.m_GameObject.TryGet(out var rootGameObject) && rootGameObject.CubismModel != null)
            {
                rootGameObject.CubismModel.PartDisplayInfoList.Add(m_MonoBehaviour);
            }
        }

        private static void BindCubismPosePart(MonoBehaviour m_MonoBehaviour)
        {
            if (!m_MonoBehaviour.m_GameObject.TryGet(out var m_GameObject))
                return;

            var rootTransform = GetRootTransform(m_GameObject.m_Transform);
            if (rootTransform.m_GameObject.TryGet(out var rootGameObject) && rootGameObject.CubismModel != null)
            {
                rootGameObject.CubismModel.PosePartList.Add(m_MonoBehaviour);
            }
        }

        private static void BindAnimationClips(GameObject gameObject)
        {
            if (gameObject.m_Animator == null || gameObject.m_Animator.m_Controller.IsNull)
                return;

            if (!gameObject.m_Animator.m_Controller.TryGet(out var controller))
                return;

            AnimatorController animatorController;
            if (controller is AnimatorOverrideController overrideController)
            {
                if (!overrideController.m_Controller.TryGet(out animatorController))
                    return;
            }
            else
            {
                animatorController = (AnimatorController)controller;
            }

            foreach (var clipPptr in animatorController.m_AnimationClips)
            {
                if (clipPptr.TryGet(out var m_AnimationClip))
                {
                    gameObject.CubismModel.ClipMotionList.Add(m_AnimationClip);
                }
            }
        }

        private static Transform GetRootTransform(Transform m_Transform)
        {
            if (m_Transform == null)
                return null;

            while (m_Transform.m_Father.TryGet(out var m_Father))
            {
                m_Transform = m_Father;
            }
            return m_Transform;
        }

        private static Dictionary<MonoBehaviour, string> GenerateMocPathDict(Dictionary<MonoBehaviour, CubismModel> mocDict, Dictionary<AssetStudio.Object, string> assetContainers, bool searchByFilename)
        {
            var tempMocPathDict = new Dictionary<MonoBehaviour, (string, string)>();
            var mocPathDict = new Dictionary<MonoBehaviour, string>();
            foreach (var mocMono in l2dModelDict.Keys)
            {
                if (!containers.TryGetValue(mocMono, out var fullContainerPath))
                    continue;
                var pathSepIndex = fullContainerPath.LastIndexOf('/');
                var basePath = pathSepIndex > 0
                    ? fullContainerPath.Substring(0, pathSepIndex)
                    : fullContainerPath;
                tempMocPathDict.Add(mocMono, (fullContainerPath, basePath));
            }

            if (tempMocPathDict.Count > 0)
            {
                var basePathSet = tempMocPathDict.Values.Select(x => x.Item2).ToHashSet();
                var useFullContainerPath = tempMocPathDict.Count != basePathSet.Count;
                foreach (var moc in mocDict.Keys)
                {
                    var mocPath = useFullContainerPath
                        ? tempMocPathDict[moc].Item1 //fullContainerPath
                        : tempMocPathDict[moc].Item2; //basePath
                    if (searchByFilename)
                    {
                        mocPathDict.Add(moc, assetContainers[moc]);
                        if (mocDict.TryGetValue(moc, out var model) && model != null)
                            model.Container = mocPath;
                    }
                    else
                    {
                        mocPathDict.Add(moc, mocPath);
                    }
                }
                tempMocPathDict.Clear();
            }
            return mocPathDict;
        }

        public static void ExportLive2D()
        {
            var baseDestPath = Path.Combine(CLIOptions.o_outputFolder.Value, "Live2DOutput");
            var motionMode = CLIOptions.o_l2dMotionMode.Value;
            var forceBezier = CLIOptions.f_l2dForceBezier.Value;
            var modelGroupOption = CLIOptions.o_l2dGroupOption.Value;
            var searchByFilename = CLIOptions.f_l2dAssetSearchByFilename.Value;
            var mocDict = l2dModelDict; //TODO: filter by name
            var l2dContainers = searchByFilename
                ? new Dictionary<AssetStudio.Object, string>()
                : containers;

            if (l2dModelDict.Count == 0)
            {
                Logger.Default.Log(LoggerEvent.Info, "Live2D Cubism models were not found.", ignoreLevel: true);
                return;
            }

            Progress.Reset();
            Logger.Info("Searching for Live2D files...");

            if (searchByFilename)
            {
                foreach (var assetKvp in containers)
                {
                    var container = string.IsNullOrEmpty(assetKvp.Key.assetsFile.originalPath)
                        ? assetKvp.Key.assetsFile.fullName
                        : assetKvp.Key.assetsFile.originalPath;
                    l2dContainers[assetKvp.Key] = container;
                }
            }
            var mocPathDict = GenerateMocPathDict(mocDict, l2dContainers, searchByFilename);

            var assetDict = new Dictionary<MonoBehaviour, List<AssetStudio.Object>>();
            foreach (var mocKvp in mocPathDict)
            {
                var mocPath = mocKvp.Value;
                var result = l2dContainers.Select(assetKvp =>
                {
                    if (!assetKvp.Value.Contains(mocPath))
                        return null;
                    var mocPathSpan = mocPath.AsSpan();
                    var modelNameFromPath = mocPathSpan.Slice(mocPathSpan.LastIndexOf('/') + 1);
#if NET9_0_OR_GREATER
                    foreach (var range in assetKvp.Value.AsSpan().Split('/'))
                    {
                        if (modelNameFromPath.SequenceEqual(assetKvp.Value.AsSpan()[range]))
                            return assetKvp.Key;
                    }
#else
                    foreach (var str in assetKvp.Value.Split('/'))
                    {
                        if (modelNameFromPath.SequenceEqual(str.AsSpan()))
                            return assetKvp.Key;
                    }
#endif
                    return null;
                }).Where(x => x != null).ToList();

                if (result.Count > 0)
                {
                    assetDict[mocKvp.Key] = result;
                }
            }
            if (searchByFilename)
                l2dContainers.Clear();
            if (mocDict.Keys.First().serializedType?.m_Type == null && CLIOptions.o_assemblyPath.Value == "")
            {
                Logger.Warning("Specifying the assembly folder may be needed for proper extraction");
            }

            var totalModelCount = assetDict.Count;
            Logger.Info($"Found {totalModelCount} model(s).");
            var parallelTaskCount = CLIOptions.o_maxParallelExportTasks.Value;
            var modelCounter = 0;
            Live2DExtractor.MocDict = mocDict;
            Live2DExtractor.Assembly = assemblyLoader;
            foreach (var assetGroupKvp in assetDict)
            {
                var srcContainer = containers[assetGroupKvp.Key];

                Logger.Info($"[{modelCounter + 1}/{totalModelCount}] Exporting Live2D: \"{srcContainer.Color(Ansi.BrightCyan)}\"");
                try
                {
                    var cubismExtractor = new Live2DExtractor(assetGroupKvp);
                    var filename = string.IsNullOrEmpty(cubismExtractor.MocMono.assetsFile.originalPath)
                        ? Path.GetFileNameWithoutExtension(cubismExtractor.MocMono.assetsFile.fileName)
                        : Path.GetFileNameWithoutExtension(cubismExtractor.MocMono.assetsFile.originalPath);
                    string modelPath;
                    switch (modelGroupOption)
                        {
                            case Live2DModelGroupOption.SourceFileName:
                                modelPath = filename;
                                break;
                            case Live2DModelGroupOption.ModelName:
                                modelPath = !string.IsNullOrEmpty(cubismExtractor.Model?.Name)
                                    ? cubismExtractor.Model.Name
                                    : filename;
                                break;
                            default: //ContainerPath
                                var container = searchByFilename && cubismExtractor.Model != null
                                    ? cubismExtractor.Model.Container
                                    : srcContainer;
                                modelPath = Path.HasExtension(container)
                                    ? container.Replace(Path.GetExtension(container), "")
                                    : container;
                                break;
                        }

                    var destPath = Path.Combine(baseDestPath, modelPath) + Path.DirectorySeparatorChar;
                    cubismExtractor.ExtractCubismModel(destPath, motionMode, forceBezier, parallelTaskCount);
                    modelCounter++;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Live2D model export error: \"{srcContainer}\"", ex);
                }
                Progress.Report(modelCounter, (int)totalModelCount);
            }

            var status = modelCounter > 0 ?
                $"Finished exporting [{modelCounter}/{totalModelCount}] Live2D model(s) to \"{CLIOptions.o_outputFolder.Value.Color(Ansi.BrightCyan)}\"" :
                "Nothing exported.";
            Logger.Default.Log(LoggerEvent.Info, status, ignoreLevel: true);
        }
    }
}
