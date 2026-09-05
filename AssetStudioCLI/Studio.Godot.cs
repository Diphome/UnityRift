using UnityRift;
using UnityRiftCLI.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static UnityRiftCLI.Exporter;
using Ansi = UnityRift.ColorConsole;

namespace UnityRiftCLI
{
    /// <summary>
    /// "-m godot": convert loaded Unity materials into Godot 4 scaffolds
    /// (.gdshader + .tres ShaderMaterial) with their referenced textures, ready to drop into a Godot project.
    /// </summary>
    internal static partial class Studio
    {
        public static void ExportGodotMaterials()
        {
            var outRoot = CLIOptions.o_outputFolder.Value;
            var texDir = Path.Combine(outRoot, "textures");
            var overwrite = CLIOptions.f_overwriteExisting.Value;

            var materials = new List<Material>();
            foreach (var item in parsedAssetsList)
                if (item.Asset is Material mat)
                    materials.Add(mat);

            var particleCount = parsedAssetsList.Count(x => x.Type == ClassIDType.ParticleSystem);
            if (materials.Count == 0 && particleCount == 0)
            {
                Logger.Warning("No Material or ParticleSystem assets found. They often live in a scene/asset bundle; try pointing at the game's *_Data folder or a bundle that contains them.");
                return;
            }

            Logger.Info($"Converting {materials.Count} material(s) and {particleCount} particle system(s) to Godot 4 scaffolds...");
            Directory.CreateDirectory(outRoot);

            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var texCache = new Dictionary<long, string>(); // texture PathID -> res:// path (dedupe)
            var exportedMaterials = 0;
            var exportedTextures = 0;

            foreach (var mat in materials)
            {
                try
                {
                    // Resolve + export textures referenced by this material.
                    var texResPaths = new Dictionary<string, string>(StringComparer.Ordinal);
                    var texEnvs = mat.m_SavedProperties?.m_TexEnvs;
                    if (texEnvs != null)
                    {
                        foreach (var te in texEnvs)
                        {
                            // Material may have been parsed from the type-tree JSON path, where nested
                            // PPtrs have no assetsFile set; pass the material's own file so FileID-relative
                            // resolution works.
                            if (!te.Value.m_Texture.TryGet(out var tex, mat.assetsFile) || !(tex is Texture2D t2d))
                                continue;
                            if (!texCache.TryGetValue(t2d.m_PathID, out var res))
                            {
                                var texName = UniqueTextureName(t2d, usedNames);
                                var texPath = Path.Combine(texDir, texName + ".png");
                                if (overwrite || !File.Exists(texPath))
                                {
                                    using (var stream = t2d.ConvertToStream(ImageFormat.Png, flip: true))
                                    {
                                        if (stream == null)
                                            continue;
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

                    Shader shader = null;
                    mat.m_Shader.TryGet(out shader, mat.assetsFile);

                    var result = GodotMaterialExporter.Export(mat, shader, texResPaths);
                    var baseName = UniqueMaterialName(result.ShaderName, usedNames);

                    var gdPath = Path.Combine(outRoot, baseName + ".gdshader");
                    var tresPath = Path.Combine(outRoot, baseName + ".tres");
                    if (!overwrite && (File.Exists(gdPath) || File.Exists(tresPath)))
                    {
                        Logger.Debug($"Skipping existing \"{baseName}\" (use -r to overwrite).");
                        continue;
                    }
                    // The .tres references <baseName>.gdshader; keep both names in sync.
                    var tres = result.Tres.Replace("res://" + result.ShaderName + ".gdshader", "res://" + baseName + ".gdshader");
                    File.WriteAllText(gdPath, result.GdShader);
                    File.WriteAllText(tresPath, tres);
                    if (!string.IsNullOrEmpty(result.ShaderReference))
                        File.WriteAllText(Path.Combine(outRoot, baseName + ".shaderref.txt"), result.ShaderReference);
                    exportedMaterials++;
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to convert material \"{mat.m_Name}\": {ex.Message}");
                }
            }

            // ---- ParticleSystems -> Godot GPUParticles3D scenes ----
            var exportedParticles = 0;
            var particleDir = Path.Combine(outRoot, "particles");
            var particleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in parsedAssetsList)
            {
                if (item.Type != ClassIDType.ParticleSystem)
                    continue;
                try
                {
                    var dict = item.Asset.ToType();
                    if (dict == null)
                    {
                        Logger.Debug($"ParticleSystem \"{item.Text}\" has no readable type tree; skipped.");
                        continue;
                    }
                    var pr = GodotParticleExporter.Export(dict, item.Text);
                    if (!pr.Ok)
                        continue;
                    var baseName = UniqueMaterialName(pr.Name, particleNames);
                    var path = Path.Combine(particleDir, baseName + ".tscn");
                    if (!overwrite && File.Exists(path))
                        continue;
                    Directory.CreateDirectory(particleDir);
                    File.WriteAllText(path, pr.Tscn);
                    exportedParticles++;
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to convert ParticleSystem \"{item.Text}\": {ex.Message}");
                }
            }

            Logger.Info($"Exported {exportedMaterials.ToString().Color(Ansi.BrightGreen)} Godot material(s), {exportedParticles.ToString().Color(Ansi.BrightGreen)} particle scene(s) and {exportedTextures} texture(s) to \"{outRoot.Color(Ansi.BrightCyan)}\".");
            Logger.Info("Drop the output folder into your Godot project (keep 'textures'/'particles' subfolders). Materials: .gdshader carries render_mode/uniforms (port the pixel logic from the reference block). Particles: .tscn holds a GPUParticles3D + ParticleProcessMaterial (curve/gradient fields are approximated).");
        }

        private static string UniqueMaterialName(string baseName, HashSet<string> used)
        {
            var name = string.IsNullOrEmpty(baseName) ? "material" : baseName;
            var candidate = name;
            var n = 1;
            while (!used.Add("MAT:" + candidate))
                candidate = $"{name}_{n++}";
            return candidate;
        }

        private static string UniqueTextureName(Texture2D tex, HashSet<string> used)
        {
            var name = string.IsNullOrEmpty(tex.m_Name) ? "tex_" + tex.m_PathID : FixFileName(tex.m_Name);
            var candidate = name;
            var n = 1;
            while (!used.Add("TEX:" + candidate))
                candidate = $"{name}_{n++}";
            return candidate;
        }
    }
}
