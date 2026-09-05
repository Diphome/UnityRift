using AssetStudio;
using AssetStudioCLI.Options;
using System.Linq;
using Ansi = AssetStudio.ColorConsole;

namespace AssetStudioCLI
{
    /// <summary>
    /// "-m godotscene": export the loaded scene as a Godot 4 project (glTF mesh roots + native
    /// particles/lights/cameras + MonoBehaviour script stubs). All the assembly work lives in the shared
    /// <see cref="GodotSceneExporter"/> so the CLI and GUI produce identical output.
    /// </summary>
    internal static partial class Studio
    {
        public static void ExportGodotScene()
        {
            EnsureScriptAssembliesLoaded(); // Mono / IL2CPP assemblies for MonoBehaviour field resolution

            var allGameObjects = assetsManager.AssetsFileList
                .SelectMany(f => f.Objects)
                .OfType<GameObject>();

            var r = GodotSceneExporter.Build(allGameObjects, assemblyLoader,
                CLIOptions.o_outputFolder.Value, ImageFormat.Png, CLIOptions.f_overwriteExisting.Value,
                msg => Logger.Info(msg));

            if (r.ScenePath == null)
                return;

            Logger.Info($"Exported {r.Models.ToString().Color(Ansi.BrightGreen)} model(s) + " +
                $"{r.FxNodes.ToString().Color(Ansi.BrightGreen)} FX/light/camera node(s) + scripts on " +
                $"{r.ScriptedObjects.ToString().Color(Ansi.BrightGreen)} object(s) ({r.ScriptStubs} stub class(es)); " +
                $"wrote \"{r.ScenePath.Color(Ansi.BrightCyan)}\".");
            Logger.Info("Open the output folder in Godot 4; it imports the .glb files on open, then run scene.tscn. MonoBehaviour stubs are under 'scripts', attached to <Object>_Scripts holder nodes.");
        }
    }
}
