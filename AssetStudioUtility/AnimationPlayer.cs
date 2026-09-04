using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NMatrix4x4 = System.Numerics.Matrix4x4;
using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

namespace AssetStudio
{
    // Poses and skins a model (from a ModelConverter IImported IR) for real-time preview.
    // Given a clip index and a time, it evaluates the animation, computes each bone's
    // world transform, and CPU-skins every mesh into world-space positions/normals ready
    // to draw. CPU skinning (rather than a GPU shader) keeps this renderer-agnostic and
    // works with both OpenTK 3.x (net472) and 4.x (net8/9).
    //
    // Coordinate convention matches the FBX/glTF exporters: the IR is right-handed with
    // X negated, rotations are stored as FBX-convention Euler degrees (converted to
    // quaternions via Fbx.EulerToQuaternion), and matrices are row-vector (v * M), the
    // same as System.Numerics.
    public sealed class AnimationPlayer
    {
        public sealed class ClipInfo
        {
            public string Name;
            public float Duration;   // seconds
            public float SampleRate;
        }

        // One material group within a mesh: its own triangle list and base-color texture.
        public sealed class SubMesh
        {
            public int[] Indices;           // triangle list into the mesh's vertex arrays
            public byte[] BaseColorTexture; // encoded image bytes (png/…), may be null
            public string Material;
        }

        // A drawable mesh: shared vertex arrays (refreshed per frame by Evaluate) plus
        // one or more material submeshes, each with its own indices + texture.
        public sealed class PreviewMesh
        {
            public string Name;
            public int VertexCount;
            public NVector3[] Positions;   // world space, updated each Evaluate
            public NVector3[] Normals;     // world space, updated each Evaluate
            public float[][] UV0;          // per-vertex UV (may be null)
            public List<SubMesh> Submeshes = new List<SubMesh>();
            public ImportedMesh Source;

            internal (int joint, float weight)[][] Skin; // per-vertex up to 4 (jointIndex, weight); null = rigid
            internal int RigidFrame = -1;                // frame index for a rigid (non-skinned) mesh
        }

        private sealed class Bone
        {
            public string Path;
            public int Parent = -1;
            public NVector3 DefaultT;
            public NQuaternion DefaultR;
            public NVector3 DefaultS;
            // Current animated local (reset to defaults each Evaluate)
            public NVector3 T;
            public NQuaternion R;
            public NVector3 S;
            public NMatrix4x4 World;
        }

        private readonly IImported _imported;
        private readonly List<Bone> _bones = new List<Bone>();
        private readonly Dictionary<string, int> _boneByPath = new Dictionary<string, int>();

        public List<ClipInfo> Clips { get; } = new List<ClipInfo>();
        public List<PreviewMesh> Meshes { get; } = new List<PreviewMesh>();
        public NVector3 BoundsMin { get; private set; }
        public NVector3 BoundsMax { get; private set; }

        public AnimationPlayer(IImported imported)
        {
            _imported = imported;
            BuildSkeleton(imported.RootFrame, -1);
            BuildMeshes();
            BuildClips();
            Evaluate(-1, 0f); // bind pose
            ComputeBounds();
        }

        // ------------------------------------------------------------------ build

        private void BuildSkeleton(ImportedFrame frame, int parent)
        {
            if (frame == null)
                return;
            var b = new Bone
            {
                Path = frame.Path,
                Parent = parent,
                DefaultT = new NVector3(frame.LocalPosition.X, frame.LocalPosition.Y, frame.LocalPosition.Z),
                DefaultR = ToQuat(Fbx.EulerToQuaternion(frame.LocalRotation)),
                DefaultS = new NVector3(frame.LocalScale.X, frame.LocalScale.Y, frame.LocalScale.Z),
            };
            int index = _bones.Count;
            _bones.Add(b);
            if (!_boneByPath.ContainsKey(b.Path))
                _boneByPath[b.Path] = index;
            for (var i = 0; i < frame.Count; i++)
                BuildSkeleton(frame[i], index);
        }

        private void BuildMeshes()
        {
            if (_imported.MeshList == null)
                return;
            foreach (var m in _imported.MeshList)
            {
                if (m.VertexList == null || m.VertexList.Count == 0)
                    continue;

                var pm = new PreviewMesh
                {
                    Name = m.Path,
                    Source = m,
                    VertexCount = m.VertexList.Count,
                    Positions = new NVector3[m.VertexList.Count],
                    Normals = new NVector3[m.VertexList.Count],
                    UV0 = new float[m.VertexList.Count][],
                };

                // One draw group per material submesh, each with its own texture, so the
                // model renders with the same per-material textures it uses in game.
                foreach (var sub in m.SubmeshList)
                {
                    var subIndices = new List<int>(sub.FaceList.Count * 3);
                    foreach (var face in sub.FaceList)
                    {
                        subIndices.Add(face.VertexIndices[0] + sub.BaseVertex);
                        subIndices.Add(face.VertexIndices[1] + sub.BaseVertex);
                        subIndices.Add(face.VertexIndices[2] + sub.BaseVertex);
                    }
                    pm.Submeshes.Add(new SubMesh
                    {
                        Indices = subIndices.ToArray(),
                        Material = sub.Material,
                        BaseColorTexture = FindBaseColorTexture(sub.Material),
                    });
                }

                for (var v = 0; v < m.VertexList.Count; v++)
                {
                    var uv = m.VertexList[v].UV;
                    if (uv != null && uv.Length > 0 && uv[0] != null && uv[0].Length >= 2)
                        pm.UV0[v] = new[] { uv[0][0], uv[0][1] };
                }

                if (m.BoneList != null && m.BoneList.Count > 0)
                {
                    // Skinned: map each bone to a skeleton index + inverse bind matrix.
                    pm.Skin = new (int, float)[m.VertexList.Count][];
                    for (var v = 0; v < m.VertexList.Count; v++)
                    {
                        var vert = m.VertexList[v];
                        var list = new List<(int, float)>(4);
                        if (vert.BoneIndices != null && vert.Weights != null)
                        {
                            for (var k = 0; k < 4; k++)
                            {
                                var bi = vert.BoneIndices[k];
                                var w = vert.Weights[k];
                                if (w > 0f && bi >= 0 && bi < m.BoneList.Count)
                                    list.Add((bi, w));
                            }
                        }
                        pm.Skin[v] = list.Count > 0 ? list.ToArray() : new (int, float)[] { (0, 1f) };
                    }
                }
                else
                {
                    // Rigid: attach to the frame at the mesh path (fallback to root).
                    pm.RigidFrame = _boneByPath.TryGetValue(m.Path, out var fi) ? fi : 0;
                }

                Meshes.Add(pm);
            }
        }

        private byte[] FindBaseColorTexture(string materialName)
        {
            if (_imported.MaterialList == null || _imported.TextureList == null || materialName == null)
                return null;
            var mat = _imported.MaterialList.FirstOrDefault(x => x.Name == materialName);
            var tex = mat?.Textures?.FirstOrDefault(t => t.Dest == 0);
            if (tex == null)
                return null;
            var it = _imported.TextureList.FirstOrDefault(t => t.Name == tex.Name);
            return it?.Data != null && it.Data.Length > 0 ? it.Data : null;
        }

        private void BuildClips()
        {
            if (_imported.AnimationList == null)
                return;
            foreach (var a in _imported.AnimationList)
            {
                float dur = 0f;
                if (a.TrackList != null)
                    foreach (var tr in a.TrackList)
                    {
                        if (tr.Translations.Count > 0) dur = Math.Max(dur, tr.Translations[tr.Translations.Count - 1].time);
                        if (tr.Rotations.Count > 0) dur = Math.Max(dur, tr.Rotations[tr.Rotations.Count - 1].time);
                        if (tr.Scalings.Count > 0) dur = Math.Max(dur, tr.Scalings[tr.Scalings.Count - 1].time);
                    }
                Clips.Add(new ClipInfo { Name = a.Name, Duration = dur, SampleRate = a.SampleRate });
            }
        }

        // --------------------------------------------------------------- evaluate

        // clipIndex < 0 => bind pose (defaults). Fills each PreviewMesh's Positions/Normals.
        public void Evaluate(int clipIndex, float time)
        {
            // Reset locals to defaults.
            foreach (var b in _bones)
            {
                b.T = b.DefaultT;
                b.R = b.DefaultR;
                b.S = b.DefaultS;
            }

            // Apply animation tracks.
            if (clipIndex >= 0 && clipIndex < Clips.Count && _imported.AnimationList != null)
            {
                var anim = _imported.AnimationList[clipIndex];
                if (anim.TrackList != null)
                {
                    foreach (var track in anim.TrackList)
                    {
                        if (string.IsNullOrEmpty(track.Path) || !_boneByPath.TryGetValue(track.Path, out var bi))
                            continue;
                        var b = _bones[bi];
                        if (track.Translations.Count > 0) b.T = SampleVec(track.Translations, time);
                        if (track.Scalings.Count > 0) b.S = SampleVec(track.Scalings, time);
                        if (track.Rotations.Count > 0) b.R = SampleRot(track.Rotations, time);
                    }
                }
            }

            // World matrices (parents precede children by construction).
            for (var i = 0; i < _bones.Count; i++)
            {
                var b = _bones[i];
                var local = NMatrix4x4.CreateScale(b.S) * NMatrix4x4.CreateFromQuaternion(b.R) * NMatrix4x4.CreateTranslation(b.T);
                b.World = b.Parent >= 0 ? local * _bones[b.Parent].World : local;
            }

            // Skin each mesh.
            foreach (var pm in Meshes)
                SkinMesh(pm);
        }

        private void SkinMesh(PreviewMesh pm)
        {
            var src = pm.Source;
            if (pm.Skin != null)
            {
                for (var v = 0; v < pm.VertexCount; v++)
                {
                    var vert = src.VertexList[v];
                    var pos = new NVector3(vert.Vertex.X, vert.Vertex.Y, vert.Vertex.Z);
                    var nrm = new NVector3(vert.Normal.X, vert.Normal.Y, vert.Normal.Z);
                    NVector3 accP = NVector3.Zero, accN = NVector3.Zero;
                    foreach (var (joint, weight) in pm.Skin[v])
                    {
                        // skinMatrix = inverseBind * boneWorld  (row-vector: apply inverseBind then world)
                        var inverseBind = ToNumerics(src.BoneList[joint].Matrix);
                        var world = _boneByPath.TryGetValue(src.BoneList[joint].Path, out var bi) ? _bones[bi].World : NMatrix4x4.Identity;
                        var skin = inverseBind * world;
                        accP += NVector3.Transform(pos, skin) * weight;
                        accN += NVector3.TransformNormal(nrm, skin) * weight;
                    }
                    pm.Positions[v] = accP;
                    pm.Normals[v] = accN == NVector3.Zero ? NVector3.UnitY : NVector3.Normalize(accN);
                }
            }
            else
            {
                var world = pm.RigidFrame >= 0 ? _bones[pm.RigidFrame].World : NMatrix4x4.Identity;
                for (var v = 0; v < pm.VertexCount; v++)
                {
                    var vert = src.VertexList[v];
                    pm.Positions[v] = NVector3.Transform(new NVector3(vert.Vertex.X, vert.Vertex.Y, vert.Vertex.Z), world);
                    var n = NVector3.TransformNormal(new NVector3(vert.Normal.X, vert.Normal.Y, vert.Normal.Z), world);
                    pm.Normals[v] = n == NVector3.Zero ? NVector3.UnitY : NVector3.Normalize(n);
                }
            }
        }

        private void ComputeBounds()
        {
            var min = new NVector3(float.MaxValue);
            var max = new NVector3(float.MinValue);
            foreach (var pm in Meshes)
                foreach (var p in pm.Positions)
                {
                    min = NVector3.Min(min, p);
                    max = NVector3.Max(max, p);
                }
            if (Meshes.Count == 0)
            {
                min = new NVector3(-1); max = new NVector3(1);
            }
            BoundsMin = min;
            BoundsMax = max;
        }

        // ----------------------------------------------------------- sampling

        private static NVector3 SampleVec(List<ImportedKeyframe<Vector3>> keys, float time)
        {
            if (keys.Count == 1 || time <= keys[0].time)
                return V(keys[0].value);
            if (time >= keys[keys.Count - 1].time)
                return V(keys[keys.Count - 1].value);
            for (var i = 1; i < keys.Count; i++)
            {
                if (time <= keys[i].time)
                {
                    var t0 = keys[i - 1].time; var t1 = keys[i].time;
                    var f = t1 > t0 ? (time - t0) / (t1 - t0) : 0f;
                    return NVector3.Lerp(V(keys[i - 1].value), V(keys[i].value), f);
                }
            }
            return V(keys[keys.Count - 1].value);
        }

        // Rotation keyframes are stored as Euler degrees; convert to quaternions and slerp.
        private static NQuaternion SampleRot(List<ImportedKeyframe<Vector3>> keys, float time)
        {
            if (keys.Count == 1 || time <= keys[0].time)
                return ToQuat(Fbx.EulerToQuaternion(keys[0].value));
            if (time >= keys[keys.Count - 1].time)
                return ToQuat(Fbx.EulerToQuaternion(keys[keys.Count - 1].value));
            for (var i = 1; i < keys.Count; i++)
            {
                if (time <= keys[i].time)
                {
                    var t0 = keys[i - 1].time; var t1 = keys[i].time;
                    var f = t1 > t0 ? (time - t0) / (t1 - t0) : 0f;
                    var q0 = ToQuat(Fbx.EulerToQuaternion(keys[i - 1].value));
                    var q1 = ToQuat(Fbx.EulerToQuaternion(keys[i].value));
                    return NQuaternion.Slerp(q0, q1, f);
                }
            }
            return ToQuat(Fbx.EulerToQuaternion(keys[keys.Count - 1].value));
        }

        // ----------------------------------------------------------- conversions

        private static NVector3 V(Vector3 v) => new NVector3(v.X, v.Y, v.Z);
        private static NQuaternion ToQuat(Quaternion q) => new NQuaternion(q.X, q.Y, q.Z, q.W);

        private static NMatrix4x4 ToNumerics(Matrix4x4 m)
        {
            return new NMatrix4x4(
                m.M00, m.M01, m.M02, m.M03,
                m.M10, m.M11, m.M12, m.M13,
                m.M20, m.M21, m.M22, m.M23,
                m.M30, m.M31, m.M32, m.M33);
        }
    }
}
