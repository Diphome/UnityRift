using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;
using NMatrix = System.Numerics.Matrix4x4;

namespace AssetStudio
{
    /// <summary>
    /// Builds a complete Godot 4 project from a Unity scene: mesh roots as instanced glTF, ParticleSystems/
    /// Lights/Cameras as native nodes at their world transform, and MonoBehaviour script stubs attached to
    /// per-object holder nodes. Shared by the CLI (-m godotscene) and the GUI so both behave identically.
    /// Coordinate conversion is Unity(LH)->Godot(RH) via X-negation, matching the glTF exporter's space.
    /// </summary>
    public static class GodotSceneExporter
    {
        public sealed class Result
        {
            public int Models;
            public int FxNodes;
            public int ScriptStubs;
            public int ScriptedObjects;
            public string ScenePath;
        }

        public static Result Build(IEnumerable<GameObject> allGameObjects, AssemblyLoader assemblyLoader,
            string outRoot, ImageFormat imageFormat, bool overwrite, Action<string> log = null,
            bool autoAttachPlugin = false)
        {
            log = log ?? (_ => { });
            var res = new Result();
            var gameObjects = allGameObjects.Where(g => g != null).ToList();

            // Mesh roots = GameObjects whose Transform has no (resolvable) parent and whose subtree has a mesh.
            var roots = new List<GameObject>();
            foreach (var go in gameObjects)
            {
                var t = go.m_Transform;
                if (t == null) continue;
                if (t.m_Father != null && t.m_Father.TryGet(out _, go.assetsFile))
                    continue; // not a root
                if (SubtreeHasMesh(go))
                    roots.Add(go);
            }

            // FX components + MonoBehaviours on any GameObject.
            var fx = new List<(GameObject go, Object comp, ClassIDType type)>();
            var scripted = new List<(GameObject go, List<MonoBehaviour> mbs)>();
            var allMbs = new List<MonoBehaviour>();
            foreach (var go in gameObjects)
            {
                if (go.m_Components == null) continue;
                List<MonoBehaviour> goMbs = null;
                foreach (var cp in go.m_Components)
                {
                    if (!cp.TryGet<Object>(out var comp, go.assetsFile)) continue;
                    if (comp.type == ClassIDType.ParticleSystem || comp.type == ClassIDType.Light || comp.type == ClassIDType.Camera)
                        fx.Add((go, comp, comp.type));
                    else if (comp is MonoBehaviour mb)
                    {
                        (goMbs ?? (goMbs = new List<MonoBehaviour>())).Add(mb);
                        allMbs.Add(mb);
                    }
                }
                if (goMbs != null) scripted.Add((go, goMbs));
            }

            if (roots.Count == 0 && fx.Count == 0 && scripted.Count == 0)
            {
                log("No 3D objects, particles, lights, cameras or scripts found to build a Godot scene.");
                return res;
            }

            Directory.CreateDirectory(outRoot);
            var modelsDir = Path.Combine(outRoot, "models");
            var scriptsDir = Path.Combine(outRoot, "scripts");

            // ---- glTF mesh roots ----
            var gltfSettings = new Gltf.Settings { Format = Gltf.Format.Glb, ExportAnimations = true, ScaleFactor = 1.0f };
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var instances = new List<(string node, string res)>();
            var meshRootSet = new HashSet<GameObject>(roots);
            var rootInstanceName = new Dictionary<GameObject, string>();
            if (roots.Count > 0) Directory.CreateDirectory(modelsDir);
            foreach (var go in roots)
            {
                var name = Unique(string.IsNullOrEmpty(go.m_Name) ? "Object" : FixName(go.m_Name), usedNames);
                var glbPath = Path.Combine(modelsDir, name + ".glb");
                try
                {
                    if (overwrite || !File.Exists(glbPath))
                        ModelExporter.ExportGltf(glbPath, new ModelConverter(go, imageFormat), gltfSettings);
                    instances.Add((name, "res://models/" + name + ".glb"));
                    rootInstanceName[go] = name;
                    res.Models++;
                }
                catch (Exception ex) { log($"Failed to export \"{go.m_Name}\" to glTF: {ex.Message}"); }
            }

            // ---- script stubs ----
            var scriptMap = allMbs.Count > 0 ? BuildScriptStubs(allMbs, scriptsDir, assemblyLoader, overwrite, log) : new Dictionary<string, string>(StringComparer.Ordinal);
            res.ScriptStubs = scriptMap.Count;

            // ---- assemble scene.tscn ----
            var ext = new StringBuilder();
            var subs = new StringBuilder();
            var nodes = new StringBuilder();
            var nodeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int subCount = 0;

            for (var i = 0; i < instances.Count; i++)
            {
                var id = "m" + (i + 1);
                nodeNames.Add(instances[i].node); // already unique among roots; keep the name stable for the manifest
                ext.Append("[ext_resource type=\"PackedScene\" path=\"").Append(instances[i].res).Append("\" id=\"").Append(id).Append("\"]\n");
                nodes.Append("[node name=\"").Append(instances[i].node).Append("\" parent=\".\" instance=ExtResource(\"").Append(id).Append("\")]\n");
            }

            for (var i = 0; i < fx.Count; i++)
            {
                var (go, comp, type) = fx[i];
                if (!TryWorldTransform(go, out var pos, out var quat, out var scale)) continue;
                var baseName = Unique(string.IsNullOrEmpty(go.m_Name) ? type.ToString() : SanitizeNode(go.m_Name), nodeNames);
                OrderedDictionary dict;
                try { dict = comp.ToType(); } catch { dict = null; }

                string nodeType, props;
                if (type == ClassIDType.ParticleSystem)
                {
                    var inl = GodotParticleExporter.BuildInline(dict, "p" + i);
                    if (!inl.Ok) continue;
                    subs.Append(inl.SubResources).Append('\n');
                    subCount += inl.SubCount;
                    nodeType = "GPUParticles3D"; props = inl.NodeProps;
                }
                else if (type == ClassIDType.Light) { (nodeType, props) = BuildLight(dict); }
                else { nodeType = "Camera3D"; props = BuildCamera(dict); }

                nodes.Append("[node name=\"").Append(baseName).Append("\" type=\"").Append(nodeType).Append("\" parent=\".\"]\n");
                nodes.Append(TransformLine(pos, quat, scale)).Append(props);
                if (!props.EndsWith("\n")) nodes.Append('\n');
                res.FxNodes++;
            }

            // ---- scripts ----
            // GameObjects living inside an exported glTF root go into a manifest so the shipped Godot
            // EditorScript can attach the stub onto the real imported node (reliable, done in-editor where
            // the true tree exists). GameObjects with no mesh ancestor get a <Object>_Scripts holder node.
            var scriptExtId = new Dictionary<string, string>(StringComparer.Ordinal);
            var manifest = new List<(string inst, List<string> path, List<string> files)>();
            if (scriptMap.Count > 0)
            {
                foreach (var (go, mbs) in scripted)
                {
                    var files = new List<string>();
                    foreach (var mb in mbs)
                    {
                        string cn = null, ns = null;
                        if (mb.m_Script.TryGet(out var ms, mb.assetsFile)) { cn = ms.m_ClassName; ns = ms.m_Namespace; }
                        if (string.IsNullOrEmpty(cn)) cn = string.IsNullOrEmpty(mb.m_Name) ? "UnityScript" : mb.m_Name;
                        var key = (string.IsNullOrEmpty(ns) ? "" : ns + ".") + cn;
                        if (scriptMap.TryGetValue(key, out var file) && !files.Contains(file)) files.Add(file);
                    }
                    if (files.Count == 0) continue;

                    // Under a glTF mesh root? -> manifest entry for the editor tool.
                    if (TryFindRootPath(go, meshRootSet, rootInstanceName, out var inst, out var path))
                    {
                        manifest.Add((inst, path, files));
                        res.ScriptedObjects++;
                        continue;
                    }

                    // Otherwise a standalone holder node at world transform.
                    if (!TryWorldTransform(go, out var pos, out var quat, out var scale)) continue;
                    var holder = Unique((string.IsNullOrEmpty(go.m_Name) ? "Object" : SanitizeNode(go.m_Name)) + "_Scripts", nodeNames);
                    nodes.Append("[node name=\"").Append(holder).Append("\" type=\"Node3D\" parent=\".\"]\n");
                    nodes.Append(TransformLine(pos, quat, scale));
                    var childNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var file in files)
                    {
                        if (!scriptExtId.TryGetValue(file, out var sid))
                        {
                            sid = "s" + (scriptExtId.Count + 1);
                            scriptExtId[file] = sid;
                            ext.Append("[ext_resource type=\"Script\" path=\"res://scripts/").Append(file).Append(".gd\" id=\"").Append(sid).Append("\"]\n");
                        }
                        var childName = Unique(SanitizeNode(file), childNames);
                        nodes.Append("[node name=\"").Append(childName).Append("\" type=\"Node\" parent=\"").Append(holder).Append("\"]\n");
                        nodes.Append("script = ExtResource(\"").Append(sid).Append("\")\n");
                    }
                    res.ScriptedObjects++;
                }
            }

            var extCount = instances.Count + scriptExtId.Count;
            var sb = new StringBuilder();
            sb.Append("[gd_scene load_steps=").Append(extCount + subCount + 1).Append(" format=3]\n\n");
            sb.Append("; Generated by UnityRift. Unity scene -> Godot 4.\n");
            sb.Append("; Mesh roots = instanced glTF; particles/lights/cameras = native nodes. MonoBehaviours\n");
            sb.Append("; inside meshes are listed in scripts_manifest.json (run attach_scripts.gd to bind them\n");
            sb.Append("; onto the imported nodes); the rest get <Object>_Scripts holders. X-negated space.\n\n");
            sb.Append(ext);
            if (subs.Length > 0) sb.Append('\n').Append(subs);
            sb.Append("\n[node name=\"Scene\" type=\"Node3D\"]\n\n");
            sb.Append(nodes);

            res.ScenePath = Path.Combine(outRoot, "scene.tscn");
            File.WriteAllText(res.ScenePath, sb.ToString());

            var usePlugin = autoAttachPlugin && manifest.Count > 0;
            var projectFile = Path.Combine(outRoot, "project.godot");
            if (!File.Exists(projectFile))
                File.WriteAllText(projectFile,
                    "config_version=5\n\n[application]\nconfig/name=\"UnityRift Imported Scene\"\n" +
                    "run/main_scene=\"res://scene.tscn\"\nconfig/features=PackedStringArray(\"4.4\")\n\n" +
                    (usePlugin ? "[editor_plugins]\nenabled=PackedStringArray(\"res://addons/unityrift_attach/plugin.cfg\")\n\n" : "") +
                    "[rendering]\nrenderer/rendering_method=\"gl_compatibility\"\n");

            // Manifest + editor tooling to attach the stubs onto the real imported glTF nodes.
            if (manifest.Count > 0)
            {
                File.WriteAllText(Path.Combine(outRoot, "scripts_manifest.json"), BuildManifestJson(manifest));
                File.WriteAllText(Path.Combine(outRoot, "unityrift_attach_core.gd"), AttachCoreScript);
                File.WriteAllText(Path.Combine(outRoot, "attach_scripts.gd"), AttachEditorScript);
                if (usePlugin)
                {
                    var addonDir = Path.Combine(outRoot, "addons", "unityrift_attach");
                    Directory.CreateDirectory(addonDir);
                    File.WriteAllText(Path.Combine(addonDir, "plugin.cfg"), AttachPluginCfg);
                    File.WriteAllText(Path.Combine(addonDir, "plugin.gd"), AttachPluginScript);
                    log($"Wrote scripts_manifest.json ({manifest.Count} object(s)) + auto-attach editor plugin. Opening scene.tscn in Godot attaches the stubs to the imported nodes automatically.");
                }
                else
                {
                    log($"Wrote scripts_manifest.json ({manifest.Count} object(s)) + attach_scripts.gd. In Godot: open scene.tscn, open attach_scripts.gd in the Script editor and File > Run to attach the stubs. (Use --godot-attach-plugin to run it automatically on open.)");
                }
            }

            return res;
        }

        private static bool TryFindRootPath(GameObject go, HashSet<GameObject> meshRoots,
            Dictionary<GameObject, string> rootInstanceName, out string instance, out List<string> path)
        {
            instance = null; path = null;
            var chain = new List<string>();
            var cur = go; var guard = 0;
            while (cur != null && guard++ < 4096)
            {
                chain.Add(string.IsNullOrEmpty(cur.m_Name) ? "Object" : cur.m_Name);
                if (meshRoots.Contains(cur))
                {
                    if (!rootInstanceName.TryGetValue(cur, out instance)) return false;
                    chain.Reverse();
                    path = chain;
                    return true;
                }
                var t = cur.m_Transform;
                if (t == null || t.m_Father == null || !t.m_Father.TryGet(out var father, cur.assetsFile)) break;
                if (!father.m_GameObject.TryGet(out cur, father.assetsFile)) break;
            }
            return false;
        }

        private static string BuildManifestJson(List<(string inst, List<string> path, List<string> files)> manifest)
        {
            var sb = new StringBuilder();
            sb.Append("[\n");
            for (var i = 0; i < manifest.Count; i++)
            {
                var m = manifest[i];
                sb.Append("  {\"instance\": ").Append(JStr(m.inst)).Append(", \"path\": [");
                for (var j = 0; j < m.path.Count; j++) { if (j > 0) sb.Append(", "); sb.Append(JStr(m.path[j])); }
                sb.Append("], \"scripts\": [");
                for (var j = 0; j < m.files.Count; j++) { if (j > 0) sb.Append(", "); sb.Append(JStr(m.files[j])); }
                sb.Append("]}");
                if (i < manifest.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            sb.Append("]\n");
            return sb.ToString();
        }

        private static string JStr(string s)
        {
            if (s == null) return "\"\"";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "").Replace("\t", "\\t") + "\"";
        }

        // Shared attach logic (loaded by both the EditorScript and the auto-attach plugin). Walks each
        // manifest name-path against the real imported tree (handling Godot's " (N)" dedup and the extra
        // root wrapper via a recursive fallback), and when a path dead-ends in a Skeleton3D it creates a
        // BoneAttachment3D for the deepest matching bone (Unity objects parented to bones).
        private const string AttachCoreScript =
@"@tool
extends RefCounted

var _root

func attach(root):
    _root = root
    if root == null:
        return [0, 0]
    var f = FileAccess.open(""res://scripts_manifest.json"", FileAccess.READ)
    if f == null:
        return [0, 0]
    var data = JSON.parse_string(f.get_as_text())
    if typeof(data) != TYPE_ARRAY:
        return [0, 0]
    var attached := 0
    var missing := 0
    for entry in data:
        var inst = root.get_node_or_null(NodePath(entry[""instance""]))
        if inst == null:
            missing += 1
            continue
        var node = _walk(inst, entry[""path""])
        if node == null:
            missing += 1
            continue
        root.set_editable_instance(inst, true)
        var scripts: Array = entry[""scripts""]
        for i in scripts.size():
            var scr = load(""res://scripts/%s.gd"" % scripts[i])
            if scr == null:
                continue
            if i == 0:
                if node.get_script() != scr:
                    node.set_script(scr)
                    node.owner = root
            elif node.get_node_or_null(String(scripts[i])) == null:
                var child = Node.new()
                child.name = String(scripts[i])
                node.add_child(child)
                child.owner = root
                child.set_script(scr)
            attached += 1
    print(""UnityRift attach: %d ok, %d not found."" % [attached, missing])
    return [attached, missing]

func _walk(inst, path):
    var node = inst
    var idx = 0
    for raw in path:
        var nm = _norm(String(raw))
        var next = _child(node, nm)
        if next == null:
            next = _find_rec(node, nm)
        if next == null:
            return _bone_attach(node, path, idx)
        node = next
        idx += 1
    return node

func _bone_attach(from, path, idx):
    var skel = _find_skeleton(from)
    if skel == null:
        return null
    var bone := """"
    for j in range(idx, path.size()):
        for cand in [String(path[j]), _norm(String(path[j]))]:
            if skel.find_bone(cand) >= 0:
                bone = cand
    if bone == """":
        return null
    for c in skel.get_children():
        if c is BoneAttachment3D and c.bone_name == bone:
            return c
    var ba = BoneAttachment3D.new()
    ba.name = ""Attach_"" + bone
    ba.bone_name = bone
    skel.add_child(ba)
    ba.owner = _root
    return ba

func _find_skeleton(n):
    if n is Skeleton3D:
        return n
    for c in n.get_children():
        var r = _find_skeleton(c)
        if r != null:
            return r
    return null

func _child(parent, name):
    for c in parent.get_children():
        if _norm(c.name) == name or _base(_norm(c.name)) == name:
            return c
    return null

func _find_rec(parent, name):
    for c in parent.get_children():
        if _norm(c.name) == name or _base(_norm(c.name)) == name:
            return c
    for c in parent.get_children():
        var r = _find_rec(c, name)
        if r != null:
            return r
    return null

func _base(n):
    var i = n.rfind("" ("")
    if i > 0 and n.ends_with("")""):
        return n.substr(0, i)
    return n

func _norm(n):
    for ch in [""."", "":"", ""@"", ""/"", ""%"", '""']:
        n = n.replace(ch, ""_"")
    return n
";

        // EditorScript: open scene.tscn, then File > Run in the Script editor.
        private const string AttachEditorScript =
@"@tool
extends EditorScript

# Attaches UnityRift MonoBehaviour stubs onto the imported glTF nodes. Open scene.tscn, then File > Run.
func _run():
    var root = get_scene()
    if root == null:
        push_error(""Open scene.tscn first, then run this script."")
        return
    var core = preload(""res://unityrift_attach_core.gd"").new()
    core.attach(root)
    print(""UnityRift: done. Save the scene to keep the attached scripts."")
";

        private const string AttachPluginCfg =
@"[plugin]
name=""UnityRift Attach""
description=""Attaches UnityRift MonoBehaviour stubs onto imported glTF nodes when scene.tscn is opened.""
author=""UnityRift""
version=""1.0""
script=""plugin.gd""
";

        // EditorPlugin: runs the attach automatically when a scene is opened in the editor (once per scene),
        // and also adds a Project > Tools menu item to run it on demand.
        private const string AttachPluginScript =
@"@tool
extends EditorPlugin

var _core = preload(""res://unityrift_attach_core.gd"").new()

func _enter_tree():
    scene_changed.connect(_on_scene_changed)
    add_tool_menu_item(""UnityRift: Attach scripts"", _run_now)

func _exit_tree():
    if scene_changed.is_connected(_on_scene_changed):
        scene_changed.disconnect(_on_scene_changed)
    remove_tool_menu_item(""UnityRift: Attach scripts"")

func _on_scene_changed(root):
    if root == null:
        return
    if not root.has_meta(""unityrift_attached""):
        _core.attach(root)
        root.set_meta(""unityrift_attached"", true)

func _run_now():
    var root = get_editor_interface().get_edited_scene_root()
    if root != null:
        _core.attach(root)
";

        // ---- MonoBehaviour stubs (shared) ----
        public static Dictionary<string, string> BuildScriptStubs(IEnumerable<MonoBehaviour> mbs, string scriptsDir,
            AssemblyLoader assemblyLoader, bool overwrite, Action<string> log = null)
        {
            log = log ?? (_ => { });
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mb in mbs)
            {
                string cn = null, ns = null, asm = null;
                if (mb.m_Script.TryGet(out var ms, mb.assetsFile)) { cn = ms.m_ClassName; ns = ms.m_Namespace; asm = ms.m_AssemblyName; }
                if (string.IsNullOrEmpty(cn)) cn = string.IsNullOrEmpty(mb.m_Name) ? "UnityScript" : mb.m_Name;
                var key = (string.IsNullOrEmpty(ns) ? "" : ns + ".") + cn;
                if (map.ContainsKey(key)) continue;

                OrderedDictionary fields = null;
                try
                {
                    fields = mb.ToType();
                    if (fields == null && assemblyLoader != null && assemblyLoader.Modules.Count > 0)
                        fields = mb.ToType(mb.ConvertToTypeTree(assemblyLoader));
                }
                catch (Exception ex) { log($"Field read failed for {key}: {ex.Message}"); }

                try
                {
                    var stub = GodotScriptStubExporter.Export(cn, ns, asm, fields);
                    var baseName = Unique(stub.ClassName, fileNames);
                    var path = Path.Combine(scriptsDir, baseName + ".gd");
                    if (overwrite || !File.Exists(path))
                    {
                        Directory.CreateDirectory(scriptsDir);
                        File.WriteAllText(path, stub.Gd);
                    }
                    map[key] = baseName;
                }
                catch (Exception ex) { log($"Failed to write stub for {key}: {ex.Message}"); }
            }
            return map;
        }

        // ---- transforms ----
        private static bool SubtreeHasMesh(GameObject root)
        {
            var stack = new Stack<Transform>();
            if (root.m_Transform != null) stack.Push(root.m_Transform);
            var guard = 0;
            while (stack.Count > 0 && guard++ < 100000)
            {
                var t = stack.Pop();
                if (t.m_GameObject.TryGet(out var go, t.assetsFile) && (go.m_MeshFilter != null || go.m_SkinnedMeshRenderer != null))
                    return true;
                if (t.m_Children != null)
                    foreach (var c in t.m_Children)
                        if (c.TryGet(out var child, t.assetsFile)) stack.Push(child);
            }
            return false;
        }

        private static bool TryWorldTransform(GameObject go, out NVector3 pos, out NQuaternion quat, out NVector3 scale)
        {
            pos = NVector3.Zero; quat = NQuaternion.Identity; scale = NVector3.One;
            var t = go.m_Transform;
            if (t == null) return false;
            var world = NMatrix.Identity;
            var cur = t; var guard = 0;
            while (cur != null && guard++ < 4096)
            {
                var local = NMatrix.CreateScale(V(cur.m_LocalScale))
                          * NMatrix.CreateFromQuaternion(Q(cur.m_LocalRotation))
                          * NMatrix.CreateTranslation(V(cur.m_LocalPosition));
                world = world * local;
                if (!cur.m_Father.TryGet(out var father, cur.assetsFile)) break;
                cur = father;
            }
            if (!NMatrix.Decompose(world, out var s, out var r, out var tr))
            { tr = world.Translation; s = NVector3.One; r = NQuaternion.Identity; }
            pos = new NVector3(-tr.X, tr.Y, tr.Z);
            quat = new NQuaternion(r.X, -r.Y, -r.Z, r.W);
            scale = s;
            return true;
        }

        private static NVector3 V(Vector3 v) => new NVector3(v.X, v.Y, v.Z);
        private static NQuaternion Q(Quaternion q) => new NQuaternion(q.X, q.Y, q.Z, q.W);

        private static string TransformLine(NVector3 pos, NQuaternion quat, NVector3 scale)
        {
            var b = NMatrix.CreateFromQuaternion(quat);
            float xx = b.M11 * scale.X, xy = b.M12 * scale.X, xz = b.M13 * scale.X;
            float yx = b.M21 * scale.Y, yy = b.M22 * scale.Y, yz = b.M23 * scale.Y;
            float zx = b.M31 * scale.Z, zy = b.M32 * scale.Z, zz = b.M33 * scale.Z;
            var sb = new StringBuilder("transform = Transform3D(");
            sb.Append(F(xx)).Append(", ").Append(F(yx)).Append(", ").Append(F(zx)).Append(", ");
            sb.Append(F(xy)).Append(", ").Append(F(yy)).Append(", ").Append(F(zy)).Append(", ");
            sb.Append(F(xz)).Append(", ").Append(F(yz)).Append(", ").Append(F(zz)).Append(", ");
            sb.Append(F(pos.X)).Append(", ").Append(F(pos.Y)).Append(", ").Append(F(pos.Z)).Append(")\n");
            return sb.ToString();
        }

        // ---- light / camera ----
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
                case 1: nodeType = "DirectionalLight3D"; break;
                case 0: nodeType = "SpotLight3D";
                    p.Append("spot_range = ").Append(F(range)).Append('\n');
                    p.Append("spot_angle = ").Append(F(spot * 0.5f)).Append('\n'); break;
                default: nodeType = "OmniLight3D";
                    p.Append("omni_range = ").Append(F(range)).Append('\n'); break;
            }
            if (color != null) p.Append("light_color = Color(").Append(color).Append(")\n");
            if (Math.Abs(intensity - 1f) > 0.001f) p.Append("light_energy = ").Append(F(intensity)).Append('\n');
            return (nodeType, p.ToString());
        }

        private static string BuildCamera(OrderedDictionary d)
        {
            var p = new StringBuilder();
            bool ortho = GB(d, "orthographic", false);
            if (ortho) { p.Append("projection = 1\n"); p.Append("size = ").Append(F(GF(d, "orthographic size", 5f))).Append('\n'); }
            else p.Append("fov = ").Append(F(GF(d, "field of view", 60f))).Append('\n');
            p.Append("near = ").Append(F(GF(d, "near clip plane", 0.05f))).Append('\n');
            p.Append("far = ").Append(F(GF(d, "far clip plane", 4000f))).Append('\n');
            return p.ToString();
        }

        // ---- helpers ----
        private static OrderedDictionary GetDict(OrderedDictionary d, string k) => d != null && d.Contains(k) ? d[k] as OrderedDictionary : null;
        private static float GF(OrderedDictionary d, string k, float def) { if (d == null || !d.Contains(k) || d[k] == null) return def; try { return Convert.ToSingle(d[k], CultureInfo.InvariantCulture); } catch { return def; } }
        private static int GI(OrderedDictionary d, string k, int def) { if (d == null || !d.Contains(k) || d[k] == null) return def; try { return Convert.ToInt32(d[k], CultureInfo.InvariantCulture); } catch { return def; } }
        private static bool GB(OrderedDictionary d, string k, bool def) { if (d == null || !d.Contains(k) || d[k] == null) return def; try { return Convert.ToBoolean(d[k]); } catch { return def; } }
        private static string GetColor(OrderedDictionary c) => c == null ? null : $"{F(GF(c, "r", 1f))}, {F(GF(c, "g", 1f))}, {F(GF(c, "b", 1f))}, {F(GF(c, "a", 1f))}";

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

        private static string FixName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        private static string Unique(string baseName, HashSet<string> used)
        {
            var name = string.IsNullOrEmpty(baseName) ? "Object" : baseName;
            var candidate = name; var n = 1;
            while (!used.Add(candidate)) candidate = $"{name}_{n++}";
            return candidate;
        }
    }
}
