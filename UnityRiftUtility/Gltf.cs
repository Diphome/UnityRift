using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;
using SharpGLTF.Transforms;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;
using NVector2 = System.Numerics.Vector2;
using NQuaternion = System.Numerics.Quaternion;
using NMatrix4x4 = System.Numerics.Matrix4x4;

namespace UnityRift
{
    // glTF 2.0 exporter that consumes the same IImported IR as the FBX exporter.
    // The IR is already in a right-handed, X-negated space (see ModelConverter),
    // which matches glTF's right-handed, Y-up convention, so geometry, bind poses
    // and node transforms are consumed directly. Rotations are stored in the IR as
    // FBX-convention Euler angles (degrees); they are converted back to quaternions
    // via Fbx.EulerToQuaternion so orientation matches the FBX exporter exactly.
    public static class Gltf
    {
        public enum Format
        {
            Glb,  // single-file binary (.glb), textures embedded
            Gltf, // JSON (.gltf), textures embedded as data URIs
        }

        public sealed class Settings
        {
            public Format Format { get; set; } = Format.Glb;
            public bool ExportAnimations { get; set; } = true;
            public float ScaleFactor { get; set; } = 1.0f;
        }

        public static class Exporter
        {
            public static void Export(string path, IImported imported, Settings settings)
            {
                new GltfExporterContext(imported, settings).Export(path);
            }
        }
    }

    internal sealed class GltfExporterContext
    {
        // Skinned vs rigid differ only in the skinning vertex fragment.
        private readonly IImported _imported;
        private readonly Gltf.Settings _settings;

        private readonly Dictionary<string, NodeBuilder> _nodeByPath = new Dictionary<string, NodeBuilder>();
        // glTF (SharpGLTF) requires every node name in a skinned armature to be unique.
        private readonly HashSet<string> _usedNames = new HashSet<string>();
        private readonly Dictionary<string, MaterialBuilder> _materialByName = new Dictionary<string, MaterialBuilder>();
        private NodeBuilder _root;
        private MaterialBuilder _defaultMaterial;

        public GltfExporterContext(IImported imported, Gltf.Settings settings)
        {
            _imported = imported;
            _settings = settings;
        }

        public void Export(string path)
        {
            var scene = new SceneBuilder();

            BuildMaterials();
            BuildNodes();

            if (_imported.MeshList != null)
            {
                foreach (var mesh in _imported.MeshList)
                {
                    if (mesh.VertexList == null || mesh.VertexList.Count == 0)
                        continue;
                    if (mesh.BoneList != null && mesh.BoneList.Count > 0)
                        BuildSkinnedMesh(scene, mesh);
                    else
                        BuildRigidMesh(scene, mesh);
                }
            }

            if (_settings.ExportAnimations && _imported.AnimationList != null)
                BuildAnimations();

            var model = scene.ToGltf2();

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (_settings.Format == Gltf.Format.Glb)
            {
                model.SaveGLB(path);
            }
            else
            {
                var ws = new WriteSettings { JsonIndented = true, MergeBuffers = true };
                model.SaveGLTF(path, ws);
            }
        }

        // ------------------------------------------------------------------ nodes

        private void BuildNodes()
        {
            if (_imported.RootFrame == null)
                return;
            _root = BuildNode(_imported.RootFrame, null);

            if (Math.Abs(_settings.ScaleFactor - 1.0f) > 1e-6f)
            {
                var t = _root.LocalTransform;
                _root.LocalTransform = new AffineTransform(t.Scale * _settings.ScaleFactor, t.Rotation, t.Translation);
            }
        }

        private NodeBuilder BuildNode(ImportedFrame frame, NodeBuilder parent)
        {
            var nb = parent == null ? new NodeBuilder() : parent.CreateNode();
            nb.Name = UniqueName(frame.Name);

            var translation = new NVector3(frame.LocalPosition.X, frame.LocalPosition.Y, frame.LocalPosition.Z);
            var rotation = ToQuat(Fbx.EulerToQuaternion(frame.LocalRotation));
            var scale = new NVector3(frame.LocalScale.X, frame.LocalScale.Y, frame.LocalScale.Z);
            nb.LocalTransform = new AffineTransform(scale, rotation, translation);

            var path = frame.Path;
            if (!_nodeByPath.ContainsKey(path))
                _nodeByPath[path] = nb;

            for (var i = 0; i < frame.Count; i++)
                BuildNode(frame[i], nb);

            return nb;
        }

        private NodeBuilder FindNode(string path)
        {
            return FindNodeExact(path) ?? _root;
        }

        private NodeBuilder FindNodeExact(string path)
        {
            if (!string.IsNullOrEmpty(path) && _nodeByPath.TryGetValue(path, out var nb))
                return nb;
            return null;
        }

        // -------------------------------------------------------------- materials

        private void BuildMaterials()
        {
            _defaultMaterial = new MaterialBuilder("__default")
                .WithMetallicRoughnessShader()
                .WithBaseColor(new NVector4(0.8f, 0.8f, 0.8f, 1f))
                .WithMetallicRoughness(0f, 1f)
                .WithDoubleSide(true);

            if (_imported.MaterialList == null)
                return;

            foreach (var im in _imported.MaterialList)
            {
                if (im.Name == null || _materialByName.ContainsKey(im.Name))
                    continue;

                var mb = new MaterialBuilder(Safe(im.Name))
                    .WithMetallicRoughnessShader()
                    .WithBaseColor(new NVector4(im.Diffuse.R, im.Diffuse.G, im.Diffuse.B, im.Diffuse.A))
                    .WithMetallicRoughness(0f, 1f)
                    .WithDoubleSide(true);

                if (im.Textures != null)
                {
                    var baseTex = im.Textures.FirstOrDefault(t => t.Dest == 0);
                    var baseImg = baseTex != null ? GetImage(baseTex.Name) : null;
                    if (baseImg != null)
                        mb.WithChannelImage(KnownChannel.BaseColor, baseImg.Value);

                    var normTex = im.Textures.FirstOrDefault(t => t.Dest == 3 || t.Dest == 1);
                    var normImg = normTex != null ? GetImage(normTex.Name) : null;
                    if (normImg != null)
                        mb.WithChannelImage(KnownChannel.Normal, normImg.Value);
                }

                _materialByName[im.Name] = mb;
            }
        }

        private MaterialBuilder ResolveMaterial(string name)
        {
            if (name != null && _materialByName.TryGetValue(name, out var mb))
                return mb;
            return _defaultMaterial;
        }

        private MemoryImage? GetImage(string name)
        {
            if (string.IsNullOrEmpty(name) || _imported.TextureList == null)
                return null;
            var tex = _imported.TextureList.FirstOrDefault(t => t.Name == name);
            if (tex?.Data == null || tex.Data.Length == 0)
                return null;
            return new MemoryImage(tex.Data);
        }

        // ------------------------------------------------------------------ meshes

        private void BuildRigidMesh(SceneBuilder scene, ImportedMesh mesh)
        {
            var normals = EnsureNormals(mesh);
            var mb = new MeshBuilder<MaterialBuilder, VertexPositionNormal, VertexTexture1, VertexEmpty>(Safe(mesh.Path));

            foreach (var sub in mesh.SubmeshList)
            {
                var prim = mb.UsePrimitive(ResolveMaterial(sub.Material));
                foreach (var face in sub.FaceList)
                {
                    var v0 = RigidVertex(mesh, normals, face.VertexIndices[0] + sub.BaseVertex);
                    var v1 = RigidVertex(mesh, normals, face.VertexIndices[1] + sub.BaseVertex);
                    var v2 = RigidVertex(mesh, normals, face.VertexIndices[2] + sub.BaseVertex);
                    prim.AddTriangle(v0, v1, v2);
                }
            }

            scene.AddRigidMesh(mb, FindNode(mesh.Path));
        }

        private VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> RigidVertex(
            ImportedMesh mesh, NVector3[] normals, int gi)
        {
            var v = mesh.VertexList[gi];
            var geo = new VertexPositionNormal(Pos(v), normals[gi]);
            var mat = new VertexTexture1(UV(v));
            return new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(geo, mat);
        }

        private void BuildSkinnedMesh(SceneBuilder scene, ImportedMesh mesh)
        {
            var normals = EnsureNormals(mesh);

            // glTF skins require distinct joint nodes. Bone paths in the IR can be
            // missing or collide (duplicate frame names), so fall back to unique
            // placeholder nodes rather than reusing a node (which SharpGLTF rejects).
            NodeBuilder skeletonRoot = _root;
            if (skeletonRoot == null)
            {
                _root = skeletonRoot = new NodeBuilder();
                skeletonRoot.Name = UniqueName("Armature");
            }
            var used = new HashSet<NodeBuilder>();
            var joints = new List<(NodeBuilder, NMatrix4x4)>(mesh.BoneList.Count);
            for (var i = 0; i < mesh.BoneList.Count; i++)
            {
                var bone = mesh.BoneList[i];
                var node = FindNodeExact(bone.Path);
                if (node == null || used.Contains(node))
                {
                    node = skeletonRoot.CreateNode();
                    node.Name = UniqueName("joint");
                }
                used.Add(node);
                joints.Add((node, ToNumerics(bone.Matrix)));
            }

            var mb = new MeshBuilder<MaterialBuilder, VertexPositionNormal, VertexTexture1, VertexJoints4>(Safe(mesh.Path));

            foreach (var sub in mesh.SubmeshList)
            {
                var prim = mb.UsePrimitive(ResolveMaterial(sub.Material));
                foreach (var face in sub.FaceList)
                {
                    var v0 = SkinnedVertex(mesh, normals, joints.Count, face.VertexIndices[0] + sub.BaseVertex);
                    var v1 = SkinnedVertex(mesh, normals, joints.Count, face.VertexIndices[1] + sub.BaseVertex);
                    var v2 = SkinnedVertex(mesh, normals, joints.Count, face.VertexIndices[2] + sub.BaseVertex);
                    prim.AddTriangle(v0, v1, v2);
                }
            }

            // A skinned mesh's own node transform is ignored by glTF (deformation
            // comes from the joints), and SharpGLTF parents the instance node at the
            // scene root with no name. Give it a readable, unique name.
            scene.AddSkinnedMesh(mb, joints.ToArray()).WithName(UniqueName(LeafName(mesh.Path)));
        }

        private VertexBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4> SkinnedVertex(
            ImportedMesh mesh, NVector3[] normals, int jointCount, int gi)
        {
            var v = mesh.VertexList[gi];
            var geo = new VertexPositionNormal(Pos(v), normals[gi]);
            var mat = new VertexTexture1(UV(v));

            var bindings = new (int, float)[4];
            float sum = 0f;
            for (var k = 0; k < 4; k++)
            {
                var idx = v.BoneIndices != null ? v.BoneIndices[k] : 0;
                var w = v.Weights != null ? v.Weights[k] : 0f;
                if (idx < 0 || idx >= jointCount || w <= 0f)
                {
                    bindings[k] = (0, 0f);
                }
                else
                {
                    bindings[k] = (idx, w);
                    sum += w;
                }
            }
            if (sum <= 0f)
                bindings[0] = (0, 1f);

            var skin = new VertexJoints4(bindings);
            return new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>(geo, mat, skin);
        }

        // -------------------------------------------------------------- animations

        private void BuildAnimations()
        {
            foreach (var anim in _imported.AnimationList)
            {
                var name = Safe(anim.Name);
                if (anim.TrackList == null)
                    continue;

                foreach (var track in anim.TrackList)
                {
                    var node = FindNode(track.Path);
                    if (node == null)
                        continue;

                    if (track.Translations.Count > 0)
                    {
                        var c = node.UseTranslation(name);
                        foreach (var kf in track.Translations)
                            c.SetPoint(kf.time, new NVector3(kf.value.X, kf.value.Y, kf.value.Z));
                    }
                    if (track.Rotations.Count > 0)
                    {
                        var c = node.UseRotation(name);
                        foreach (var kf in track.Rotations)
                            c.SetPoint(kf.time, ToQuat(Fbx.EulerToQuaternion(kf.value)));
                    }
                    if (track.Scalings.Count > 0)
                    {
                        var c = node.UseScale(name);
                        foreach (var kf in track.Scalings)
                            c.SetPoint(kf.time, new NVector3(kf.value.X, kf.value.Y, kf.value.Z));
                    }
                }
            }
        }

        // ------------------------------------------------------------------ helpers

        private static NVector3 Pos(ImportedVertex v) => new NVector3(v.Vertex.X, v.Vertex.Y, v.Vertex.Z);

        private static NVector2 UV(ImportedVertex v)
        {
            if (v.UV != null && v.UV.Length > 0 && v.UV[0] != null && v.UV[0].Length >= 2)
                return new NVector2(v.UV[0][0], 1f - v.UV[0][1]); // glTF V is top-down
            return NVector2.Zero;
        }

        // Return per-vertex normals, computing smooth normals if the source lacks them.
        private NVector3[] EnsureNormals(ImportedMesh mesh)
        {
            var count = mesh.VertexList.Count;
            var normals = new NVector3[count];

            if (mesh.hasNormal)
            {
                for (var i = 0; i < count; i++)
                {
                    var n = mesh.VertexList[i].Normal;
                    normals[i] = new NVector3(n.X, n.Y, n.Z);
                }
                return normals;
            }

            foreach (var sub in mesh.SubmeshList)
            {
                foreach (var face in sub.FaceList)
                {
                    var i0 = face.VertexIndices[0] + sub.BaseVertex;
                    var i1 = face.VertexIndices[1] + sub.BaseVertex;
                    var i2 = face.VertexIndices[2] + sub.BaseVertex;
                    var p0 = Pos(mesh.VertexList[i0]);
                    var p1 = Pos(mesh.VertexList[i1]);
                    var p2 = Pos(mesh.VertexList[i2]);
                    var fn = NVector3.Cross(p1 - p0, p2 - p0);
                    normals[i0] += fn;
                    normals[i1] += fn;
                    normals[i2] += fn;
                }
            }
            for (var i = 0; i < count; i++)
            {
                normals[i] = normals[i].LengthSquared() > 1e-12f ? NVector3.Normalize(normals[i]) : NVector3.UnitY;
            }
            return normals;
        }

        private static NQuaternion ToQuat(Quaternion q) => new NQuaternion(q.X, q.Y, q.Z, q.W);

        // UnityRift.Matrix4x4 stores transforms row-vector (translation in the 4th
        // row: M30/M31/M32), the same convention as System.Numerics (M41/M42/M43), so
        // this is a straight element copy: SN.M(r+1)(c+1) = m.M<r><c>.
        private static NMatrix4x4 ToNumerics(Matrix4x4 m)
        {
            return new NMatrix4x4(
                m.M00, m.M01, m.M02, m.M03,
                m.M10, m.M11, m.M12, m.M13,
                m.M20, m.M21, m.M22, m.M23,
                m.M30, m.M31, m.M32, m.M33);
        }

        private static string Safe(string name) => string.IsNullOrEmpty(name) ? "node" : name;

        private static string LeafName(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "mesh";
            var slash = path.LastIndexOf('/');
            return slash >= 0 && slash < path.Length - 1 ? path.Substring(slash + 1) : path;
        }

        private string UniqueName(string name)
        {
            var baseName = Safe(name);
            var candidate = baseName;
            var i = 1;
            while (!_usedNames.Add(candidate))
            {
                candidate = baseName + "_" + i;
                i++;
            }
            return candidate;
        }
    }
}
