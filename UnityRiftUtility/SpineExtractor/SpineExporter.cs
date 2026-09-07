using System;
using System.IO;
using UnityRift;

namespace SpineExtractor
{
    // Writes a detected Spine model back out as the raw files Spine can re-import: the skeleton
    // (.json or .skel), each atlas (.atlas) verbatim, and one PNG per atlas page named exactly as
    // the atlas references it.
    public static class SpineExporter
    {
        // Exports the model into a subfolder of baseDir named after the model. Returns the folder
        // path, or null if nothing was written.
        public static string Export(SpineModel model, string baseDir, bool overwrite = true, Action<string> log = null)
        {
            if (model?.SkeletonData == null || model.SkeletonData.Length == 0)
                return null;

            var name = Sanitize(model.Name);
            var dir = Path.Combine(baseDir, name);
            Directory.CreateDirectory(dir);

            var skeletonExt = model.Kind == SpineSkeletonKind.Binary ? ".skel" : ".json";
            WriteFile(Path.Combine(dir, name + skeletonExt), model.SkeletonData, overwrite, log);

            foreach (var atlas in model.Atlases)
            {
                var atlasName = Sanitize(string.IsNullOrEmpty(atlas.Name) ? name : atlas.Name);
                WriteFile(Path.Combine(dir, atlasName + ".atlas"), atlas.Data, overwrite, log);

                foreach (var page in atlas.Pages)
                {
                    if (page.Texture == null)
                        continue; // already warned during collection
                    var pageFile = Path.Combine(dir, SanitizeFileName(page.FileName));
                    if (!overwrite && File.Exists(pageFile))
                        continue;
                    try
                    {
                        using (var stream = page.Texture.ConvertToStream(ImageFormat.Png, flip: true))
                        {
                            if (stream == null)
                            {
                                log?.Invoke($"Spine: failed to decode texture for page \"{page.FileName}\".");
                                continue;
                            }
                            using (var fs = File.OpenWrite(pageFile))
                            {
                                fs.SetLength(0);
                                stream.Position = 0;
                                stream.CopyTo(fs);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"Spine: error exporting page \"{page.FileName}\": {ex.Message}");
                    }
                }
            }

            log?.Invoke($"Spine: exported model \"{name}\" to \"{dir}\".");
            return dir;
        }

        private static void WriteFile(string path, byte[] data, bool overwrite, Action<string> log)
        {
            try
            {
                if (!overwrite && File.Exists(path))
                    return;
                File.WriteAllBytes(path, data);
            }
            catch (Exception ex)
            {
                log?.Invoke($"Spine: error writing \"{Path.GetFileName(path)}\": {ex.Message}");
            }
        }

        // The atlas page name should be kept as-is (the atlas references it verbatim); only strip
        // characters that are illegal in file names.
        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "spine_model";
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
