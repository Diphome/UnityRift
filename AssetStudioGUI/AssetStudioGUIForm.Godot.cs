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
            // Gather mesh roots from the Scene Hierarchy tree (same rule as "Export all objects (split)").
            var roots = new List<GameObject>();
            foreach (TreeNode fileNode in sceneTreeView.Nodes)
                foreach (TreeNode child in fileNode.Nodes)
                    if (child is GameObjectTreeNode gnode)
                    {
                        var gos = new List<GameObject>();
                        GodotCollectNode(gnode, gos);
                        if (gos.Any(x => x.m_SkinnedMeshRenderer != null || x.m_MeshFilter != null))
                            roots.Add(gnode.gameObject);
                    }

            if (roots.Count == 0)
            {
                StatusStripUpdate("No 3D objects found in the Scene Hierarchy to export.");
                return;
            }

            var dialog = new OpenFolderDialog { InitialFolder = saveDirectoryBackup };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            saveDirectoryBackup = dialog.Folder;
            var outRoot = dialog.Folder;

            timer.Stop();
            StatusStripUpdate($"Exporting {roots.Count} model root(s) to a Godot 4 scene...");
            Task.Run(() =>
            {
                int n = 0;
                try { n = ExportGodotSceneCore(outRoot, roots); }
                catch (Exception ex) { Logger.Error($"Godot scene export failed: {ex.Message}"); }
                StatusStripUpdate($"Godot scene export done: {n} model(s) -> {outRoot} (open in Godot 4, run scene.tscn)");
            });
        }

        private static void GodotCollectNode(GameObjectTreeNode node, List<GameObject> gameObjects)
        {
            gameObjects.Add(node.gameObject);
            foreach (TreeNode child in node.Nodes)
                if (child is GameObjectTreeNode g)
                    GodotCollectNode(g, gameObjects);
        }

        private int ExportGodotSceneCore(string outRoot, List<GameObject> roots)
        {
            var modelsDir = Path.Combine(outRoot, "models");
            Directory.CreateDirectory(modelsDir);
            var settings = new Gltf.Settings { Format = Gltf.Format.Glb, ExportAnimations = true, ScaleFactor = 1.0f };
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var instances = new List<(string node, string res)>();
            int exported = 0;

            foreach (var go in roots)
            {
                var name = UniqueGodotName(string.IsNullOrEmpty(go.m_Name) ? "Object" : GodotFixName(go.m_Name), "OBJ:", used);
                var glbPath = Path.Combine(modelsDir, name + ".glb");
                try
                {
                    var convert = new ModelConverter(go, ImageFormat.Png);
                    ModelExporter.ExportGltf(glbPath, convert, settings);
                    instances.Add((name, "res://models/" + name + ".glb"));
                    exported++;
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to export \"{go.m_Name}\" to glTF: {ex.Message}");
                }
            }
            if (instances.Count == 0)
                return 0;

            var ext = new StringBuilder();
            var nodes = new StringBuilder();
            for (var i = 0; i < instances.Count; i++)
            {
                var id = $"m{i + 1}";
                ext.Append("[ext_resource type=\"PackedScene\" path=\"").Append(instances[i].res).Append("\" id=\"").Append(id).Append("\"]\n");
                nodes.Append("[node name=\"").Append(instances[i].node).Append("\" parent=\".\" instance=ExtResource(\"").Append(id).Append("\")]\n");
            }
            var sb = new StringBuilder();
            sb.Append("[gd_scene load_steps=").Append(instances.Count + 1).Append(" format=3]\n\n");
            sb.Append("; Generated by Reunity (AssetStudioMod). Unity scene -> Godot 4.\n\n");
            sb.Append(ext).Append('\n');
            sb.Append("[node name=\"Scene\" type=\"Node3D\"]\n\n");
            sb.Append(nodes);
            File.WriteAllText(Path.Combine(outRoot, "scene.tscn"), sb.ToString());

            var projectFile = Path.Combine(outRoot, "project.godot");
            if (!File.Exists(projectFile))
                File.WriteAllText(projectFile,
                    "config_version=5\n\n[application]\nconfig/name=\"Reunity Imported Scene\"\n" +
                    "run/main_scene=\"res://scene.tscn\"\nconfig/features=PackedStringArray(\"4.4\")\n\n" +
                    "[rendering]\nrenderer/rendering_method=\"gl_compatibility\"\n");

            return exported;
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
