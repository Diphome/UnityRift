using AssetStudio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static AssetStudioGUI.Studio;

namespace AssetStudioGUI
{
    // Godot 4 export (materials -> .gdshader/.tres, ParticleSystems -> .tscn), added in code so no
    // Designer edits are needed. Mirrors the CLI "-m godot" mode, reusing the shared exporters in
    // AssetStudioUtility (GodotMaterialExporter / GodotParticleExporter).
    partial class AssetStudioGUIForm
    {
        private void InitGodotExportMenu()
        {
            var item = new ToolStripMenuItem("To Godot 4 (materials + FX)")
            {
                Name = "exportGodotMenuItem",
                ToolTipText = "Convert loaded materials to .gdshader/.tres and ParticleSystems to .tscn for Godot 4."
            };
            item.Click += exportGodotMenuItem_Click;
            exportToolStripMenuItem.DropDownItems.Add(new ToolStripSeparator());
            exportToolStripMenuItem.DropDownItems.Add(item);

            var sceneItem = new ToolStripMenuItem("To Godot 4 scene (.tscn)")
            {
                Name = "exportGodotSceneMenuItem",
                ToolTipText = "Export the loaded scene as glTF models + a Godot 4 scene.tscn (a ready-to-open Godot project)."
            };
            sceneItem.Click += exportGodotSceneMenuItem_Click;
            exportToolStripMenuItem.DropDownItems.Add(sceneItem);
        }

        private void exportGodotSceneMenuItem_Click(object sender, EventArgs e)
        {
            var allGameObjects = assetsManager.AssetsFileList.SelectMany(f => f.Objects).OfType<GameObject>().ToList();
            if (allGameObjects.Count == 0)
            {
                StatusStripUpdate("No GameObjects loaded to export as a Godot scene.");
                return;
            }

            var dialog = new OpenFolderDialog { InitialFolder = saveDirectoryBackup };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            saveDirectoryBackup = dialog.Folder;
            var outRoot = dialog.Folder;

            timer.Stop();
            StatusStripUpdate("Exporting Godot 4 scene (meshes + particles/lights/cameras + scripts)...");
            Task.Run(() =>
            {
                try
                {
                    // Best-effort: load managed assemblies so custom MonoBehaviour fields resolve.
                    if (!assemblyLoader.Loaded)
                    {
                        var paths = assetsManager.AssetsFileList.Select(f => f.fullName).Where(p => !string.IsNullOrEmpty(p)).ToList();
                        var managed = AssemblyLoader.FindManagedFolder(paths);
                        if (managed != null) { assemblyLoader.Load(managed); assemblyLoader.Loaded = true; }
                    }

                    var r = GodotSceneExporter.Build(allGameObjects, assemblyLoader, outRoot,
                        ImageFormat.Png, true, msg => Logger.Info(msg));
                    StatusStripUpdate(r.ScenePath == null
                        ? "Nothing to export to a Godot scene."
                        : $"Godot scene done: {r.Models} model(s), {r.FxNodes} FX node(s), scripts on {r.ScriptedObjects} object(s) -> {outRoot} (open in Godot 4, run scene.tscn)");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Godot scene export failed: {ex.Message}");
                    StatusStripUpdate("Godot scene export failed (see log).");
                }
            });
        }

        private void exportGodotMenuItem_Click(object sender, EventArgs e)
        {
            var mats = new List<Material>();
            var particles = new List<AssetStudio.Object>();
            foreach (var file in assetsManager.AssetsFileList)
                foreach (var obj in file.Objects)
                {
                    if (obj is Material m) mats.Add(m);
                    else if (obj.type == ClassIDType.ParticleSystem) particles.Add(obj);
                }

            if (mats.Count == 0 && particles.Count == 0)
            {
                StatusStripUpdate("No materials or particle systems loaded to export to Godot.");
                return;
            }

            var dialog = new OpenFolderDialog { InitialFolder = saveDirectoryBackup };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            saveDirectoryBackup = dialog.Folder;
            var outRoot = dialog.Folder;

            timer.Stop();
            StatusStripUpdate($"Exporting {mats.Count} material(s) and {particles.Count} particle system(s) to Godot...");
            Task.Run(() =>
            {
                int em = 0, ep = 0, et = 0;
                try
                {
                    (em, ep, et) = ExportGodotCore(outRoot, mats, particles);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Godot export failed: {ex.Message}");
                }
                StatusStripUpdate($"Godot export done: {em} material(s), {ep} particle scene(s), {et} texture(s) -> {outRoot}");
            });
        }

        private (int materials, int particles, int textures) ExportGodotCore(
            string outRoot, List<Material> mats, List<AssetStudio.Object> particles)
        {
            var texDir = Path.Combine(outRoot, "textures");
            Directory.CreateDirectory(outRoot);

            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var texCache = new Dictionary<long, string>();
            int exportedMaterials = 0, exportedTextures = 0, exportedParticles = 0;

            foreach (var mat in mats)
            {
                try
                {
                    var texResPaths = new Dictionary<string, string>(StringComparer.Ordinal);
                    var texEnvs = mat.m_SavedProperties?.m_TexEnvs;
                    if (texEnvs != null)
                    {
                        foreach (var te in texEnvs)
                        {
                            if (!te.Value.m_Texture.TryGet(out var tex, mat.assetsFile) || !(tex is Texture2D t2d))
                                continue;
                            if (!texCache.TryGetValue(t2d.m_PathID, out var res))
                            {
                                var texName = UniqueGodotName(string.IsNullOrEmpty(t2d.m_Name) ? "tex_" + t2d.m_PathID : GodotFixName(t2d.m_Name), "TEX:", usedNames);
                                var texPath = Path.Combine(texDir, texName + ".png");
                                if (!File.Exists(texPath))
                                {
                                    using (var stream = t2d.ConvertToStream(ImageFormat.Png, flip: true))
                                    {
                                        if (stream == null) continue;
                                        Directory.CreateDirectory(texDir);
                                        using (var fs = File.Create(texPath))
                                            stream.CopyTo(fs);
                                    }
                                    exportedTextures++;
                                }
                                res = "res://textures/" + texName + ".png";
                                texCache[t2d.m_PathID] = res;
                            }
                            texResPaths[te.Key] = res;
                        }
                    }

                    mat.m_Shader.TryGet(out Shader shader, mat.assetsFile);
                    var result = GodotMaterialExporter.Export(mat, shader, texResPaths);
                    var baseName = UniqueGodotName(result.ShaderName, "MAT:", usedNames);

                    var tres = result.Tres.Replace("res://" + result.ShaderName + ".gdshader", "res://" + baseName + ".gdshader");
                    File.WriteAllText(Path.Combine(outRoot, baseName + ".gdshader"), result.GdShader);
                    File.WriteAllText(Path.Combine(outRoot, baseName + ".tres"), tres);
                    if (!string.IsNullOrEmpty(result.ShaderReference))
                        File.WriteAllText(Path.Combine(outRoot, baseName + ".shaderref.txt"), result.ShaderReference);
                    exportedMaterials++;
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to convert material \"{mat.m_Name}\": {ex.Message}");
                }
            }

            var particleDir = Path.Combine(outRoot, "particles");
            var particleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var obj in particles)
            {
                try
                {
                    var dict = obj.ToType();
                    if (dict == null) continue;
                    var name = (obj as NamedObject)?.m_Name;
                    if (string.IsNullOrEmpty(name)) name = "ParticleSystem_" + obj.m_PathID;
                    var pr = GodotParticleExporter.Export(dict, name);
                    if (!pr.Ok) continue;
                    var baseName = UniqueGodotName(pr.Name, "PS:", particleNames);
                    Directory.CreateDirectory(particleDir);
                    File.WriteAllText(Path.Combine(particleDir, baseName + ".tscn"), pr.Tscn);
                    exportedParticles++;
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to convert ParticleSystem \"{obj.m_PathID}\": {ex.Message}");
                }
            }

            return (exportedMaterials, exportedParticles, exportedTextures);
        }

        private static string UniqueGodotName(string baseName, string kindPrefix, HashSet<string> used)
        {
            var name = string.IsNullOrEmpty(baseName) ? "item" : baseName;
            var candidate = name;
            var n = 1;
            while (!used.Add(kindPrefix + candidate))
                candidate = $"{name}_{n++}";
            return candidate;
        }

        private static string GodotFixName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
