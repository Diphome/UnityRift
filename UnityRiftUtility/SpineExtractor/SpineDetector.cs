using System;
using System.Collections.Generic;
using System.Text;

namespace SpineExtractor
{
    public enum SpineSkeletonKind
    {
        None,
        Json,
        Binary,
    }

    // Format heuristics for detecting Spine (esotericsoftware) assets embedded in Unity as
    // TextAssets, and for reading the texture-page names out of a Spine atlas. All checks are
    // deliberately conservative (they key off Spine's very specific atlas header lines and the
    // JSON skeleton shape) so unrelated TextAssets are not misclassified.
    public static class SpineDetector
    {
        // A Spine JSON skeleton is a JSON object carrying a "skeleton" block plus "bones"/"slots".
        public static bool IsSpineSkeletonJson(byte[] data)
        {
            if (data == null || data.Length < 16)
                return false;

            var i = 0;
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                i = 3; // skip UTF-8 BOM
            while (i < data.Length && (data[i] == ' ' || data[i] == '\t' || data[i] == '\r' || data[i] == '\n'))
                i++;
            if (i >= data.Length || data[i] != (byte)'{')
                return false;

            var head = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 8192));
            return head.Contains("\"skeleton\"") && (head.Contains("\"bones\"") || head.Contains("\"slots\""));
        }

        // Spine binary skeletons carry no fixed magic number, so they are detected by the
        // conventional file name/extension used by spine-unity exports.
        public static bool IsSpineSkeletonBinaryName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            var n = name.ToLowerInvariant();
            return n.EndsWith(".skel") || n.EndsWith(".skel.bytes") || n.EndsWith(".skel.txt");
        }

        // A Spine atlas is text whose per-page header carries the distinctive "size:", "filter:"
        // and "format:"/"repeat:"/"pma:" lines.
        public static bool IsSpineAtlas(byte[] data)
        {
            if (data == null || data.Length < 16)
                return false;

            string head;
            try
            {
                head = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 16384));
            }
            catch
            {
                return false;
            }

            bool hasSize = false, hasFilter = false, hasFormatOrRepeat = false;
            foreach (var raw in head.Split('\n'))
            {
                var line = raw.Trim();
                if (StartsWithKey(line, "size")) hasSize = true;
                else if (StartsWithKey(line, "filter")) hasFilter = true;
                else if (StartsWithKey(line, "format") || StartsWithKey(line, "repeat") || StartsWithKey(line, "pma")) hasFormatOrRepeat = true;
            }
            return hasSize && hasFilter && hasFormatOrRepeat;
        }

        private static bool StartsWithKey(string line, string key)
        {
            // matches "key:" and "key :" (some exporters pad before the colon)
            if (!line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                return false;
            var rest = line.Substring(key.Length).TrimStart();
            return rest.StartsWith(":");
        }

        // The image page files referenced by an atlas. Pages are the first line of the file and
        // any line that immediately follows a blank line; the header/region lines in between are
        // skipped.
        public static List<string> ParseAtlasPageNames(string atlasText)
        {
            var pages = new List<string>();
            if (string.IsNullOrEmpty(atlasText))
                return pages;

            var expectPage = true;
            foreach (var raw in atlasText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (raw.Trim().Length == 0)
                {
                    expectPage = true;
                    continue;
                }
                if (expectPage)
                {
                    pages.Add(raw.Trim());
                    expectPage = false;
                }
            }
            return pages;
        }
    }
}
