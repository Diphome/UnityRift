using UnityRift;
using UnityRiftCLI.Options;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SpineExtractor;
using Ansi = UnityRift.ColorConsole;

namespace UnityRiftCLI
{
    /// <summary>
    /// "-m spine": detect Spine (esotericsoftware) models embedded in the loaded assets and export
    /// the raw Spine files (skeleton + atlas + texture pages) into a per-model folder, ready to
    /// re-import into the Spine editor.
    /// </summary>
    internal static partial class Studio
    {
        public static void ExportSpine()
        {
            var outRoot = CLIOptions.o_outputFolder.Value;
            var overwrite = CLIOptions.f_overwriteExisting.Value;

            var textAssets = new List<TextAsset>();
            var textures = new List<Texture2D>();
            var monoBehaviours = new List<MonoBehaviour>();
            foreach (var item in parsedAssetsList)
            {
                if (item.Type == ClassIDType.TextAsset && item.Asset is TextAsset ta)
                    textAssets.Add(ta);
                else if (item.Type == ClassIDType.Texture2D && item.Asset is Texture2D tex)
                    textures.Add(tex);
                else if (item.Type == ClassIDType.MonoBehaviour && item.Asset is MonoBehaviour mb)
                    monoBehaviours.Add(mb);
            }

            // Hybrid: authoritative SkeletonDataAsset grouping when the MonoBehaviour fields are
            // readable (serialized type trees, or loaded/generated assemblies), else heuristics.
            var links = SpineMonoBehaviourReader.BuildLinks(monoBehaviours, assemblyLoader, msg => Logger.Info(msg));
            if (links.Count > 0)
                Logger.Info($"Spine: grouped {links.Count} model(s) from SkeletonDataAsset MonoBehaviour(s).");

            var models = SpineCollector.Collect(textAssets, textures, explicitLinks: links, log: msg => Logger.Info(msg));
            if (models.Count == 0)
            {
                Logger.Warning("No Spine models detected. Spine skeletons (.json/.skel) and atlases (.atlas) are stored as TextAssets; make sure the file/bundle that contains them is loaded (for WebGL builds, point at the extracted data.unity3d).");
                return;
            }

            Logger.Info($"Detected {models.Count} Spine model(s). Exporting to \"{outRoot.Color(Ansi.BrightCyan)}\"...");
            Directory.CreateDirectory(outRoot);

            var exported = 0;
            foreach (var model in models)
            {
                var dir = SpineExporter.Export(model, outRoot, overwrite, msg => Logger.Info(msg));
                if (dir != null)
                    exported++;
            }

            Logger.Info($"Finished exporting {exported} Spine model(s) to \"{outRoot.Color(Ansi.BrightCyan)}\".");
        }
    }
}
