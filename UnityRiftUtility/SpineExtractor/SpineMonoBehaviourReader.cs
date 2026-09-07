using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using UnityRift;

namespace SpineExtractor
{
    // Reads spine-unity SkeletonDataAsset / AtlasAsset MonoBehaviours to recover the authoritative
    // skeleton -> atlas grouping (the MonoBehaviour half of the hybrid detector). Fields are read
    // through the serialized type tree when present, otherwise from generated assemblies via
    // ConvertToTypeTree(assemblyLoader); when neither yields the custom fields (e.g. IL2CPP with
    // stripped type trees and no assemblies), reading returns null and the caller falls back to the
    // format heuristics.
    public static class SpineMonoBehaviourReader
    {
        public static bool IsSkeletonDataAsset(string className) =>
            string.Equals(className, "SkeletonDataAsset", StringComparison.Ordinal);

        public static bool IsAtlasAsset(string className) =>
            string.Equals(className, "AtlasAsset", StringComparison.Ordinal) ||
            string.Equals(className, "SpineAtlasAsset", StringComparison.Ordinal);

        // Builds explicit links from every SkeletonDataAsset MonoBehaviour in the set. Only links
        // that resolve both a skeleton and at least one atlas are returned; anything unreadable is
        // left for the heuristic path.
        public static List<SpineLink> BuildLinks(IEnumerable<MonoBehaviour> monoBehaviours, AssemblyLoader assembly, Action<string> log = null)
        {
            var links = new List<SpineLink>();
            if (monoBehaviours == null)
                return links;

            foreach (var mb in monoBehaviours)
            {
                if (mb == null || !mb.m_Script.TryGet(out var script) || !IsSkeletonDataAsset(script.m_ClassName))
                    continue;

                SpineLink link;
                try
                {
                    link = TryReadSkeletonDataAsset(mb, assembly);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"Spine: could not read SkeletonDataAsset \"{mb.m_Name}\": {ex.Message}");
                    continue;
                }
                if (link != null && link.Skeleton != null && link.Atlases.Count > 0)
                    links.Add(link);
            }
            return links;
        }

        private static SpineLink TryReadSkeletonDataAsset(MonoBehaviour mb, AssemblyLoader assembly)
        {
            var dict = ParseMonoBehaviour(mb, assembly);
            if (dict == null)
                return null;

            var skeleton = ResolvePPtr<TextAsset>(dict, mb.assetsFile, "skeletonJSON", "skeletonDataFile", "skeleton");
            if (skeleton == null)
                return null;

            var link = new SpineLink { Skeleton = skeleton, Name = mb.m_Name };

            var atlasArray = Get(dict, "atlasAssets") as object[];
            if (atlasArray != null)
            {
                foreach (var el in atlasArray)
                {
                    var atlasMb = ResolvePPtrFromDict<MonoBehaviour>(el as OrderedDictionary, mb.assetsFile);
                    if (atlasMb == null)
                        continue;
                    var atlasText = TryReadAtlasAsset(atlasMb, assembly);
                    if (atlasText != null)
                        link.Atlases.Add(atlasText);
                }
            }
            return link;
        }

        private static TextAsset TryReadAtlasAsset(MonoBehaviour mb, AssemblyLoader assembly)
        {
            var dict = ParseMonoBehaviour(mb, assembly);
            if (dict == null)
                return null;
            return ResolvePPtr<TextAsset>(dict, mb.assetsFile, "atlasFile", "atlasText", "atlas");
        }

        private static OrderedDictionary ParseMonoBehaviour(MonoBehaviour mb, AssemblyLoader assembly)
        {
            var dict = mb.ToType();
            if (dict != null)
                return dict;
            // No usable serialized type tree. Only reconstruct from assemblies when some are loaded;
            // otherwise skip (avoids noisy failed reads on IL2CPP builds with no assemblies).
            if (assembly == null || !assembly.Loaded)
                return null;
            try
            {
                var type = mb.ConvertToTypeTree(assembly);
                return mb.ToType(type);
            }
            catch
            {
                return null;
            }
        }

        // Case-insensitive lookup: serialized type trees preserve the field name casing, but
        // assembly-derived trees can differ, so match keys loosely.
        private static object Get(OrderedDictionary dict, string key)
        {
            if (dict == null) return null;
            if (dict.Contains(key)) return dict[key];
            foreach (DictionaryEntry e in dict)
                if (e.Key is string k && string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                    return e.Value;
            return null;
        }

        private static T ResolvePPtr<T>(OrderedDictionary dict, SerializedFile assetsFile, params string[] keys) where T : UnityRift.Object
        {
            foreach (var key in keys)
            {
                var result = ResolvePPtrFromDict<T>(Get(dict, key) as OrderedDictionary, assetsFile);
                if (result != null)
                    return result;
            }
            return null;
        }

        private static T ResolvePPtrFromDict<T>(OrderedDictionary pptr, SerializedFile assetsFile) where T : UnityRift.Object
        {
            if (pptr == null || !pptr.Contains("m_PathID"))
                return null;
            try
            {
                var reference = new PPtr<T>
                {
                    m_FileID = Convert.ToInt32(pptr["m_FileID"]),
                    m_PathID = Convert.ToInt64(pptr["m_PathID"]),
                    AssetsFile = assetsFile,
                };
                return reference.TryGet(out var result) ? result : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
