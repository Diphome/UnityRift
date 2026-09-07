using UnityRift;
using SpineExtractor;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using static UnityRiftGUI.Studio;

namespace UnityRiftGUI
{
    // Spine (esotericsoftware) export, added in code so no Designer edits are needed. Mirrors the
    // CLI "-m spine" mode, reusing the shared detector/exporter in UnityRiftUtility (SpineExtractor).
    partial class UnityRiftGUIForm
    {
        private void InitSpineMenu()
        {
            var item = new ToolStripMenuItem("To Spine (skeleton + atlas + textures)")
            {
                Name = "exportSpineMenuItem",
            };
            item.Click += exportSpineMenuItem_Click;
            exportToolStripMenuItem.DropDownItems.Add(item);
        }

        private void exportSpineMenuItem_Click(object sender, EventArgs e)
        {
            // Gather the Spine inputs. TextAssets are lazy objects, so resolve by type rather than
            // pattern-matching the placeholder.
            var textAssets = new List<TextAsset>();
            var textures = new List<Texture2D>();
            var monoBehaviours = new List<MonoBehaviour>();
            foreach (var file in assetsManager.AssetsFileList)
                foreach (var obj in file.Objects)
                {
                    if (obj.type == ClassIDType.TextAsset)
                    {
                        if (obj.Resolve() is TextAsset ta) textAssets.Add(ta);
                    }
                    else if (obj.type == ClassIDType.Texture2D)
                    {
                        if (obj.Resolve() is Texture2D tex) textures.Add(tex);
                    }
                    else if (obj.type == ClassIDType.MonoBehaviour)
                    {
                        if (obj.Resolve() is MonoBehaviour mb) monoBehaviours.Add(mb);
                    }
                }

            if (textAssets.Count == 0)
            {
                StatusStripUpdate("No TextAssets loaded — Spine skeletons/atlases are stored as TextAssets.");
                return;
            }

            // Hybrid: authoritative SkeletonDataAsset grouping when the MonoBehaviour fields are
            // readable (serialized type trees, or assemblies loaded via the .NET menu), else heuristics.
            var links = SpineMonoBehaviourReader.BuildLinks(monoBehaviours, assemblyLoader, msg => Logger.Info(msg));
            var models = SpineCollector.Collect(textAssets, textures, explicitLinks: links, log: msg => Logger.Info(msg));
            if (models.Count == 0)
            {
                StatusStripUpdate("No Spine models detected in the loaded assets.");
                return;
            }

            var dialog = new OpenFolderDialog { InitialFolder = saveDirectoryBackup };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            saveDirectoryBackup = dialog.Folder;
            var outRoot = dialog.Folder;

            timer.Stop();
            StatusStripUpdate($"Exporting {models.Count} Spine model(s)...");
            Task.Run(() =>
            {
                var exported = 0;
                try
                {
                    foreach (var model in models)
                        if (SpineExporter.Export(model, outRoot, overwrite: true, log: msg => Logger.Info(msg)) != null)
                            exported++;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Spine export failed: {ex.Message}");
                }
                StatusStripUpdate($"Spine export done: {exported} model(s) -> {outRoot}");
            });
        }
    }
}
