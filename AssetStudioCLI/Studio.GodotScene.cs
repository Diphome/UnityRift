using AssetStudio;
using AssetStudioCLI.Options;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using static AssetStudioCLI.Exporter;
using Ansi = AssetStudio.ColorConsole;
using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;
using NMatrix = System.Numerics.Matrix4x4;

namespace AssetStudioCLI
{
    /// <summary>
    /// "-m godotscene": export the loaded scene as glTF model(s) (mesh roots) plus native Godot nodes for
    /// ParticleSystems (GPUParticles3D), Lights (Light3D) and Cameras (Camera3D), positioned in the scene
    /// tree at each component's world transform (converted Unity->Godot to match the X-negated glTF space).
    /// </summary>
    internal static partial class Studio
    {
        public static void ExportGodotScene()
        {
            var outRoot = CLIOptions.o_outputFolder.Value;
            var modelsDir = Path.Combine(outRoot, "models");
            var overwrite = CLIOptions.f_overwriteExisting.Value;

            // Mesh roots -> glTF (same rule as splitObjects); every GameObject is scanned for FX components.
            var roots = new List<GameObject>();
            var allGameObjects = new List<GameObject>();
            foreach (var fileNode in gameObjectTree)
                foreach (GameObjectNode rootNode in fileNode.nodes)
                {
                    var gos = new List<GameObject>();
                    CollectNode(rootNode, gos);
                    allGameObjects.AddRange(gos);
                    if (gos.Any(x => x.m_SkinnedMeshRenderer != null || x.m_MeshFilter != null))
                        roots.Add(rootNode.gameObject);
                }

            // ---- Find FX/light/camera components and MonoBehaviours on any GameObject ----
            var fx = new List<(GameObject go, AssetStudio.Object comp, ClassIDType type)>();
            var mbByGo = new List<(GameObject go, List<MonoBehaviour> mbs)>();
            var allMbs = new List<MonoBehaviour>();
            foreach (var go in allGameObjects)
            {
                if (go.m_Components == null) continue;
                List<MonoBehaviour> goMbs = null;
                foreach (var cp in go.m_Components)
                {
                    if (!cp.TryGet<AssetStudio.Object>(out var comp, go.assetsFile))
                        continue;
                    if (comp.type == ClassIDType.ParticleSystem || comp.type == ClassIDType.Light || comp.type == ClassIDType.Camera)
                        fx.Add((go, comp, comp.type));
                    else if (comp is MonoBehaviour mb)
                    {
                        (goMbs ?? (goMbs = new List<MonoBehaviour>())).Add(mb);
                        allMbs.Add(mb);
                    }
                }
                if (goMbs != null)
                    mbByGo.Add((go, goMbs));
            }

            if (roots.Count == 0 && fx.Count == 0 && mbByGo.Count == 0)
            {
                Logger.Warning("No 3D objects, particles, lights or cameras found. Point at a scene/prefab bundle or the game's *_Data folder.");
                return;
            }

            Logger.Info($"Exporting {roots.Count} model root(s) + {fx.Count} FX/light/camera node(s) + scripts on {mbByGo.Count} object(s) to a Godot scene...");
            if (roots.Count > 0) Directory.CreateDirectory(modelsDir);

            // Generate GDScript stubs for all MonoBehaviour classes; map class key -> stub file base name.
            var scriptsDir = Path.Combine(outRoot, "scripts");
            Dictionary<string, string> scriptMap = null;
            if (allMbs.Count > 0)
            {
                EnsureScriptAssembliesLoaded();
                scriptMap = BuildScriptStubs(allMbs, scriptsDir, overwrite);
            }

            var gltfSettings = new Gltf.Settings { Format = Gltf.Format.Glb, ExportAnimations = true, ScaleFactor = 1.0f };
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var instances = new List<(string node, string res)>();
            var exportedModels = 0;

            foreach (var go in roots)
            {
                var name = UniqueSceneName(string.IsNullOrEmpty(go.m_Name) ? "Object" : FixFileName(go.m_Name), usedNames);
                var glbPath = Path.Combine(modelsDir, name + ".glb");
                try
                {
                    if (overwrite || !File.Exists(glbPath))
                        ModelExporter.ExportGltf(glbPath, new ModelConverter(go, ImageFormat.Png), gltfSettings);
                    instances.Add((name, "res://models/" + name + ".glb"));
                    exportedModels++;
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to export \"{go.m_Name}\" to glTF: {ex.Message}");
                }
            }

            // ---- Build scene.tscn ----
            var ext = new StringBuilder();
            var subs = new StringBuilder();
            var nodes = new StringBuilder();
            var nodeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int subCount = 0;

            for (var i = 0; i < instances.Count; i++)
            {
                var id = $"m{i + 1}";
                ext.Append("[ext_resource type=\"PackedScene\" path=\"").Append(instances[i].res).Append("\" id=\"").Append(id).Append("\"]\n");
                nodes.Append("[node name=\"").Append(UniqueSceneName(instances[i].node, nodeNames)).Append("\" parent=\".\" instance=ExtResource(\"").Append(id).Append("\")]\n");
            }

            var fxCount = 0;
            for (var i = 0; i < fx.Count; i++)
            {
                var (go, comp, type) = fx[i];
                if (!TryWorldTransform(go, out var pos, out var quat, out var scale))
                    continue;
                var baseName = UniqueSceneName(string.IsNullOrEmpty(go.m_Name) ? type.ToString() : SanitizeNode(go.m_Name), nodeNames);
                OrderedDictionary dict;
                try { dict = comp.ToType(); } catch { dict = null; }

                string nodeType, props;
                if (type == ClassIDType.ParticleSystem)
                {
                    var inl = GodotParticleExporter.BuildInline(dict, "p" + i);
                    if (!inl.Ok) continue;
                    subs.Append(inl.SubResources).Append('\n');
                    subCount += inl.SubCount;
                    nodeType = "GPUParticles3D";
                    props = inl.NodeProps;
                }
                else if (type == ClassIDType.Light)
                {
                    (nodeType, props) = BuildLight(dict);
                }
                else // Camera
                {
                    nodeType = "Camera3D";
                    props = BuildCamera(dict);
                }

                nodes.Append("[node name=\"").Append(baseName).Append("\" type=\"").Append(nodeType).Append("\" parent=\".\"]\n");
                nodes.Append(TransformLine(pos, quat, scale));
                nodes.Append(props);
                if (!props.EndsWith("\n")) nodes.Append('\n');
                fxCount++;
            }

            // ---- Script holders: one Node3D per MonoBehaviour GameObject, a child Node per script ----
            var scriptExtId = new Dictionary<string, string>(StringComparer.Ordinal); // file base -> ext id
            var scriptedObjects = 0;
            if (scriptMap != null && scriptMap.Count > 0)
            {
                foreach (var (go, mbs) in mbByGo)
                {
                    // Resolve this GO's MonoBehaviour classes to stub files.
                    var attached = new List<(string cls, string file)>();
                    foreach (var mb in mbs)
                    {
                        string cn = null, ns = null;
                        if (mb.m_Script.TryGet(out var ms, mb.assetsFile)) { cn = ms.m_ClassName; ns = ms.m_Namespace; }
                        if (string.IsNullOrEmpty(cn)) cn = string.IsNullOrEmpty(mb.m_Name) ? "UnityScript" : mb.m_Name;
                        var key = (string.IsNullOrEmpty(ns) ? "" : ns + ".") + cn;
                        if (scriptMap.TryGetValue(key, out var file))
                            attached.Add((cn, file));
                    }
                    if (attached.Count == 0)
                        continue;
                    if (!TryWorldTransform(go, out var pos, out var quat, out var scale))
                        continue;

                    var holder = UniqueSceneName((string.IsNullOrEmpty(go.m_Name) ? "Object" : SanitizeNode(go.m_Name)) + "_Scripts", nodeNames);
                    nodes.Append("[node name=\"").Append(holder).Append("\" type=\"Node3D\" parent=\".\"]\n");
                    nodes.Append(TransformLine(pos, quat, scale));
                    var childNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (cls, file) in attached)
                    {
                        if (!scriptExtId.TryGetValue(file, out var sid))
                        {
                            sid = "s" + (scriptExtId.Count + 1);
                            scriptExtId[file] = sid;
                            ext.Append("[ext_resource type=\"Script\" path=\"res://scripts/").Append(file).Append(".gd\" id=\"").Append(sid).Append("\"]\n");
                        }
                        var childName = UniqueSceneName(SanitizeNode(cls), childNames);
                        nodes.Append("[node name=\"").Append(childName).Append("\" type=\"Node\" parent=\"").Append(holder).Append("\"]\n");
                        nodes.Append("script = ExtResource(\"").Append(sid).Append("\")\n");
                    }
                    scriptedObjects++;
                }
            }

            var extCount = instances.Count + scriptExtId.Count;
            var sb = new StringBuilder();
            sb.Append("[gd_scene load_steps=").Append(extCount + subCount + 1).Append(" format=3]\n\n");
            sb.Append("; Generated by Reunity (AssetStudioMod). Unity scene -> Godot 4.\n");
            sb.Append("; Mesh roots are instanced glTF; particles/lights/cameras are native nodes at their\n");
            sb.Append("; world transform (converted to Godot's X-negated glTF space).\n\n");
            sb.Append(ext);
            if (subs.Length > 0) sb.Append('\n').Append(subs);
            sb.Append("\n[node name=\"Scene\" type=\"Node3D\"]\n\n");
            sb.Append(nodes);

            var scenePath = Path.Combine(outRoot, "scene.tscn");
            File.WriteAllText(scenePath, sb.ToString());

            var projectFile = Path.Combine(outRoot, "project.godot");
            if (!File.Exists(projectFile))
                File.WriteAllText(projectFile,
                    "config_version=5\n\n[application]\nconfig/name=\"Reunity Imported Scene\"\n" +
                    "run/main_scene=\"res://scene.tscn\"\nconfig/features=PackedStringArray(\"4.4\")\n\n" +
                    "[rendering]\nrenderer/rendering_method=\"gl_compatibility\"\n");

            Logger.Info($"Exported {exportedModels.ToString().Color(Ansi.BrightGreen)} model(s) + {fxCount.ToString().Color(Ansi.BrightGreen)} FX/light/camera node(s) + scripts on {scriptedObjects.ToString().Color(Ansi.BrightGreen)} object(s); wrote \"{scenePath.Color(Ansi.BrightCyan)}\".");
            Logger.Info("Open the output folder in Godot 4; it imports the .glb files on open, then run scene.tscn. MonoBehaviour stubs are under 'scripts', attached to <Object>_Scripts holder nodes.");
        }

        // ---- transform ----
        private static bool TryWorldTransform(GameObject go, out NVector3 pos, out NQuaternion quat, out NVector3 scale)
        {
            pos = NVector3.Zero; quat = NQuaternion.Identity; scale = NVector3.One;
            var t = go.m_Transform;
            if (t == null) return false;

            var world = NMatrix.Identity;
            var cur = t;
            var guard = 0;
            while (cur != null && guard++ < 4096)
            {
                var local = NMatrix.CreateScale(V(cur.m_LocalScale))
                          * NMatrix.CreateFromQuaternion(Q(cur.m_LocalRotation))
                          * NMatrix.CreateTranslation(V(cur.m_LocalPosition));
                world = world * local; // row-vector: child first, then parent
                if (!cur.m_Father.TryGet(out var father, cur.assetsFile))
                    break;
                cur = father;
            }

            if (!NMatrix.Decompose(world, out var s, out var r, out var tr))
            {
                // Non-decomposable (mirror/shear): fall back to translation only.
                tr = world.Translation; s = NVector3.One; r = NQuaternion.Identity;
            }
            // Unity(LH) -> Godot(RH) via X-negation: mirror the X axis.
            pos = new NVector3(-tr.X, tr.Y, tr.Z);
            quat = new NQuaternion(r.X, -r.Y, -r.Z, r.W);
            scale = s;
            return true;
        }

        private static NVector3 V(Vector3 v) => new NVector3(v.X, v.Y, v.Z);
        private static NQuaternion Q(Quaternion q) => new NQuaternion(q.X, q.Y, q.Z, q.W);

        private static string TransformLine(NVector3 pos, NQuaternion quat, NVector3 scale)
        {
            var sb = new StringBuilder();
            sb.Append("transform = Transform3D(");
            // Basis = rotation * scale, written column-major as Godot expects (x_col, y_col, z_col).
            var b = NMatrix.CreateFromQuaternion(quat);
            float xx = b.M11 * scale.X, xy = b.M12 * scale.X, xz = b.M13 * scale.X;
            float yx = b.M21 * scale.Y, yy = b.M22 * scale.Y, yz = b.M23 * scale.Y;
            float zx = b.M31 * scale.Z, zy = b.M32 * scale.Z, zz = b.M33 * scale.Z;
            // Godot Transform3D(basis columns x,y,z then origin): (xx,yx,zx, xy,yy,zy, xz,yz,zz, ox,oy,oz)
            sb.Append(F(xx)).Append(", ").Append(F(yx)).Append(", ").Append(F(zx)).Append(", ");
            sb.Append(F(xy)).Append(", ").Append(F(yy)).Append(", ").Append(F(zy)).Append(", ");
            sb.Append(F(xz)).Append(", ").Append(F(yz)).Append(", ").Append(F(zz)).Append(", ");
            sb.Append(F(pos.X)).Append(", ").Append(F(pos.Y)).Append(", ").Append(F(pos.Z)).Append(")\n");
            return sb.ToString();
        }

        // ---- light / camera mapping (generic type-tree read) ----
        private static (string type, string props) BuildLight(OrderedDictionary d)
        {
            int lt = GI(d, "m_Type", 2);
            var color = GetColor(GetDict(d, "m_Color"));
            float intensity = GF(d, "m_Intensity", 1f);
            float range = GF(d, "m_Range", 10f);
            float spot = GF(d, "m_SpotAngle", 30f);
            var p = new StringBuilder();
            string nodeType;
            switch (lt)
            {
                case 1: nodeType = "DirectionalLight3D"; break;                 // Directional
                case 0: nodeType = "SpotLight3D";                               // Spot
                    p.Append("spot_range = ").Append(F(range)).Append('\n');
                    p.Append("spot_angle = ").Append(F(spot * 0.5f)).Append('\n');
                    break;
                default: nodeType = "OmniLight3D";                              // Point / other
                    p.Append("omni_range = ").Append(F(range)).Append('\n');
                    break;
            }
            if (color != null) p.Append("light_color = Color(").Append(color).Append(")\n");
            if (Math.Abs(intensity - 1f) > 0.001f) p.Append("light_energy = ").Append(F(intensity)).Append('\n');
            return (nodeType, p.ToString());
        }

        private static string BuildCamera(OrderedDictionary d)
        {
            var p = new StringBuilder();
            float fov = GF(d, "field of view", 60f);
            float near = GF(d, "near clip plane", 0.05f);
            float far = GF(d, "far clip plane", 4000f);
            bool ortho = GB(d, "orthographic", false);
            float orthoSize = GF(d, "orthographic size", 5f);
            if (ortho)
            {
                p.Append("projection = 1\n");
                p.Append("size = ").Append(F(orthoSize)).Append('\n');
            }
            else
            {
                p.Append("fov = ").Append(F(fov)).Append('\n');
            }
            p.Append("near = ").Append(F(near)).Append('\n');
            p.Append("far = ").Append(F(far)).Append('\n');
            return p.ToString();
        }

        // ---- small dict helpers ----
        private static OrderedDictionary GetDict(OrderedDictionary d, string k) => d != null && d.Contains(k) ? d[k] as OrderedDictionary : null;
        private static float GF(OrderedDictionary d, string k, float def) { if (d == null || !d.Contains(k) || d[k] == null) return def; try { return Convert.ToSingle(d[k], CultureInfo.InvariantCulture); } catch { return def; } }
        private static int GI(OrderedDictionary d, string k, int def) { if (d == null || !d.Contains(k) || d[k] == null) return def; try { return Convert.ToInt32(d[k], CultureInfo.InvariantCulture); } catch { return def; } }
        private static bool GB(OrderedDictionary d, string k, bool def) { if (d == null || !d.Contains(k) || d[k] == null) return def; try { return Convert.ToBoolean(d[k]); } catch { return def; } }
        private static string GetColor(OrderedDictionary c)
        {
            if (c == null) return null;
            return $"{F(GF(c, "r", 1f))}, {F(GF(c, "g", 1f))}, {F(GF(c, "b", 1f))}, {F(GF(c, "a", 1f))}";
        }

        private static string F(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v) || v == 0f) return "0.0";
            return v.ToString("0.0######", CultureInfo.InvariantCulture);
        }

        private static string SanitizeNode(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
                sb.Append(ch == '/' || ch == ':' || ch == '.' || ch == '@' || ch == '"' || ch == '%' ? '_' : ch);
            var s = sb.ToString().Trim();
            return string.IsNullOrEmpty(s) ? "Node" : s;
        }

        private static string UniqueSceneName(string baseName, HashSet<string> used)
        {
            var name = string.IsNullOrEmpty(baseName) ? "Object" : baseName;
            var candidate = name;
            var n = 1;
            while (!used.Add(candidate))
                candidate = $"{name}_{n++}";
            return candidate;
        }
    }
}
