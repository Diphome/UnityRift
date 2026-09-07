using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityRift;

namespace SpineExtractor
{
    // An explicit skeleton -> atlas(es) association discovered by reading a spine-unity
    // SkeletonDataAsset MonoBehaviour (the authoritative grouping). Frontends that can read those
    // fields supply these; when none are available the collector falls back to heuristics.
    public class SpineLink
    {
        public string Name;                                    // preferred model name (e.g. SkeletonDataAsset name)
        public TextAsset Skeleton;
        public List<TextAsset> Atlases = new List<TextAsset>();
    }

    // Groups classified Spine TextAssets and textures into exportable models. Grouping prefers
    // explicit SkeletonDataAsset links (hybrid: the MonoBehaviour path), then falls back to
    // grouping by source assets file, so it also works on IL2CPP builds whose MonoBehaviour
    // fields cannot be read without generated assemblies.
    public static class SpineCollector
    {
        public static List<SpineModel> Collect(
            IReadOnlyList<TextAsset> textAssets,
            IReadOnlyList<Texture2D> textures,
            IReadOnlyList<SpineLink> explicitLinks = null,
            Action<string> log = null)
        {
            var models = new List<SpineModel>();
            if (textAssets == null || textAssets.Count == 0)
                return models;

            // Build a case-insensitive texture lookup keyed by both the bare name and, if the name
            // already carries an extension, its stem. First writer wins on collisions.
            var textureByName = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
            foreach (var tex in textures ?? Array.Empty<Texture2D>())
            {
                if (tex == null || string.IsNullOrEmpty(tex.m_Name)) continue;
                if (!textureByName.ContainsKey(tex.m_Name)) textureByName[tex.m_Name] = tex;
                var stem = Path.GetFileNameWithoutExtension(tex.m_Name);
                if (!string.IsNullOrEmpty(stem) && !textureByName.ContainsKey(stem)) textureByName[stem] = tex;
            }

            // Classify text assets.
            var skeletons = new List<TextAsset>();
            var atlases = new List<TextAsset>();
            foreach (var ta in textAssets)
            {
                if (ta?.m_Script == null || ta.m_Script.Length == 0) continue;
                if (SpineDetector.IsSpineAtlas(ta.m_Script))
                    atlases.Add(ta);
                else if (SpineDetector.IsSpineSkeletonJson(ta.m_Script) || SpineDetector.IsSpineSkeletonBinaryName(ta.m_Name))
                    skeletons.Add(ta);
            }

            var usedSkeletons = new HashSet<TextAsset>();
            var usedAtlases = new HashSet<TextAsset>();

            // 1) Explicit SkeletonDataAsset links (authoritative).
            if (explicitLinks != null)
            {
                foreach (var link in explicitLinks)
                {
                    if (link?.Skeleton?.m_Script == null || link.Skeleton.m_Script.Length == 0) continue;
                    var model = BuildModel(link.Name, link.Skeleton, link.Atlases, textureByName, log);
                    if (model == null) continue;
                    usedSkeletons.Add(link.Skeleton);
                    foreach (var a in link.Atlases) usedAtlases.Add(a);
                    models.Add(model);
                }
            }

            // 2) Heuristic grouping of the remainder, by source assets file.
            var remSkeletons = skeletons.Where(s => !usedSkeletons.Contains(s)).ToList();
            var remAtlases = atlases.Where(a => !usedAtlases.Contains(a)).ToList();

            foreach (var group in remSkeletons.GroupBy(s => s.assetsFile))
            {
                var groupSkeletons = group.ToList();
                var groupAtlases = remAtlases.Where(a => a.assetsFile == group.Key).ToList();

                if (groupSkeletons.Count == 1)
                {
                    // One skeleton in this file: it owns every atlas in the same file.
                    var model = BuildModel(null, groupSkeletons[0], groupAtlases, textureByName, log);
                    if (model != null) models.Add(model);
                }
                else
                {
                    // Multiple skeletons: pair each with the atlas whose stem best matches, else
                    // give it any atlas sharing its stem; unmatched atlases attach to the first.
                    foreach (var sk in groupSkeletons)
                    {
                        var stem = Path.GetFileNameWithoutExtension(sk.m_Name);
                        var matched = groupAtlases
                            .Where(a => NameStem(a).Equals(stem, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        var model = BuildModel(null, sk, matched.Count > 0 ? matched : groupAtlases, textureByName, log);
                        if (model != null) models.Add(model);
                    }
                }
            }

            return models;
        }

        private static string NameStem(TextAsset ta)
        {
            var n = ta.m_Name ?? "";
            // Spine atlas assets are often named "foo.atlas"; strip the trailing ".atlas".
            if (n.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase))
                n = n.Substring(0, n.Length - ".atlas".Length);
            return Path.GetFileNameWithoutExtension(n);
        }

        private static SpineModel BuildModel(
            string preferredName,
            TextAsset skeleton,
            IReadOnlyList<TextAsset> atlasTextAssets,
            Dictionary<string, Texture2D> textureByName,
            Action<string> log)
        {
            var kind = SpineDetector.IsSpineSkeletonJson(skeleton.m_Script)
                ? SpineSkeletonKind.Json
                : SpineSkeletonKind.Binary;

            var model = new SpineModel
            {
                SkeletonData = skeleton.m_Script,
                Kind = kind,
            };

            foreach (var ta in atlasTextAssets ?? (IReadOnlyList<TextAsset>)Array.Empty<TextAsset>())
            {
                if (ta?.m_Script == null) continue;
                var atlas = new SpineAtlasFile
                {
                    Name = NameStem(ta),
                    Data = ta.m_Script,
                };
                var text = System.Text.Encoding.UTF8.GetString(ta.m_Script);
                foreach (var pageName in SpineDetector.ParseAtlasPageNames(text))
                {
                    var stem = Path.GetFileNameWithoutExtension(pageName);
                    textureByName.TryGetValue(pageName, out var tex);
                    if (tex == null) textureByName.TryGetValue(stem, out tex);
                    if (tex == null)
                        log?.Invoke($"Spine: atlas page \"{pageName}\" has no matching texture; it will be skipped.");
                    atlas.Pages.Add(new SpinePage { FileName = pageName, Texture = tex });
                }
                model.Atlases.Add(atlas);
            }

            // Name the model: explicit name, else the single atlas stem, else the skeleton name.
            model.Name = !string.IsNullOrEmpty(preferredName)
                ? preferredName
                : (model.Atlases.Count == 1 && !string.IsNullOrEmpty(model.Atlases[0].Name)
                    ? model.Atlases[0].Name
                    : StemOrDefault(skeleton.m_Name, "spine_model"));

            if (model.Atlases.Count == 0)
                log?.Invoke($"Spine: skeleton \"{model.Name}\" has no matching atlas; exporting skeleton only.");
            return model;
        }

        private static string StemOrDefault(string name, string fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            var stem = Path.GetFileNameWithoutExtension(name);
            return string.IsNullOrEmpty(stem) ? fallback : stem;
        }
    }
}
