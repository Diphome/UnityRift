using AssetStudio;
using AssetStudioCLI.Options;
using Newtonsoft.Json;
using System.IO;
using System.Linq;
using System.Text;

namespace AssetStudioCLI
{
    internal static class Exporter
    {
        public static AssemblyLoader assemblyLoader = new AssemblyLoader();

        public static bool ExportTexture2D(AssetItem item, string exportPath, CLIOptions options)
        {
            var m_Texture2D = (Texture2D)item.Asset;
            if (options.convertTexture)
            {
                var type = options.o_imageFormat.Value;
                if (!TryExportFile(exportPath, item, "." + type.ToString().ToLower(), out var exportFullPath))
                    return false;
                var image = m_Texture2D.ConvertToImage(flip: true);
                if (image == null)
                {
                    Logger.Error($"Export error. Failed to convert texture \"{m_Texture2D.m_Name}\" into image");
                    return false;
                }
                using (image)
                {
                    using (var file = File.OpenWrite(exportFullPath))
                    {
                        image.WriteToStream(file, type);
                    }
                    Logger.Debug($"{item.TypeString}: \"{item.Text}\" exported to \"{exportFullPath}\"");
                    return true;
                }
            }
            else
            {
                if (!TryExportFile(exportPath, item, ".tex", out var exportFullPath))
                    return false;
                File.WriteAllBytes(exportFullPath, m_Texture2D.image_data.GetData());
                Logger.Debug($"{item.TypeString}: \"{item.Text}\" exported to \"{exportFullPath}\"");
                return true;
            }
        }

        public static bool ExportAudioClip(AssetItem item, string exportPath, CLIOptions options)
        {
            string exportFullPath;
            var m_AudioClip = (AudioClip)item.Asset;
            var m_AudioData = m_AudioClip.m_AudioData.GetData();
            if (m_AudioData == null || m_AudioData.Length == 0)
            {
                Logger.Error($"Export error. \"{item.Text}\": AudioData was not found");
                return false;
            }
            var converter = new AudioClipConverter(m_AudioClip);
            if (options.o_audioFormat.Value != AudioFormat.None && converter.IsSupport)
            {
                if (!TryExportFile(exportPath, item, ".wav", out exportFullPath))
                    return false;

                var sb = new StringBuilder();
                sb.AppendLine($"Converting \"{m_AudioClip.m_Name}\" to wav..");
                sb.AppendLine(m_AudioClip.version[0] < 5 ? $"AudioClip type: {m_AudioClip.m_Type}" : $"AudioClip compression format: {m_AudioClip.m_CompressionFormat}");
                sb.AppendLine($"AudioClip channel count: {m_AudioClip.m_Channels}");
                sb.AppendLine($"AudioClip sample rate: {m_AudioClip.m_Frequency}");
                sb.AppendLine($"AudioClip bit depth: {m_AudioClip.m_BitsPerSample}");
                Logger.Debug(sb.ToString());

                var buffer = converter.ConvertToWav(m_AudioData);
                if (buffer == null)
                {
                    Logger.Error($"Export error. \"{item.Text}\": Failed to convert to Wav");
                    return false;
                }
                File.WriteAllBytes(exportFullPath, buffer);
            }
            else
            {
                if (!TryExportFile(exportPath, item, converter.GetExtensionName(), out exportFullPath))
                    return false;
                File.WriteAllBytes(exportFullPath, m_AudioData);
            }

            Logger.Debug($"{item.TypeString}: \"{item.Text}\" exported to \"{exportFullPath}\"");
            return true;
        }

        public static bool ExportVideoClip(AssetItem item, string exportPath)
        {
            var m_VideoClip = (VideoClip)item.Asset;
            if (m_VideoClip.m_ExternalResources.m_Size > 0)
            {
                if (!TryExportFile(exportPath, item, Path.GetExtension(m_VideoClip.m_OriginalPath), out var exportFullPath))
                    return false;

                var sb = new StringBuilder();
                sb.AppendLine($"VideoClip format: {m_VideoClip.m_Format}");
                sb.AppendLine($"VideoClip width: {m_VideoClip.Width}");
                sb.AppendLine($"VideoClip height: {m_VideoClip.Height}");
                sb.AppendLine($"VideoClip frame rate: {m_VideoClip.m_FrameRate}");
                sb.AppendLine($"VideoClip split alpha: {m_VideoClip.m_HasSplitAlpha}");
                Logger.Debug(sb.ToString());

                m_VideoClip.m_VideoData.WriteData(exportFullPath);
                Logger.Debug($"{item.TypeString}: \"{item.Text}\" exported to \"{exportFullPath}\"");
                return true;
            }
            return false;
        }

        public static bool ExportMovieTexture(AssetItem item, string exportPath)
        {
            var m_MovieTexture = (MovieTexture)item.Asset;
            if (!TryExportFile(exportPath, item, ".ogv", out var exportFullPath))
                return false;
            File.WriteAllBytes(exportFullPath, m_MovieTexture.m_MovieData);

            Logger.Debug($"{item.TypeString}: \"{item.Text}\" exported to \"{exportFullPath}\"");
            return true;
        }

        public static bool ExportShader(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".shader", out var exportFullPath))
                return false;
            var m_Shader = (Shader)item.Asset;
            var str = m_Shader.Convert();
            File.WriteAllText(exportFullPath, str);

            Logger.Debug($"{item.TypeString}: \"{item.Text}\" exported to \"{exportFullPath}\"");
            return true;
        }
        
        public static bool ExportTextAsset(AssetItem item, string exportPath, CLIOptions options)
        {
            var m_TextAsset = (TextAsset)item.Asset;
            var extension = ".txt";
            var assetExtension = Path.GetExtension(m_TextAsset.m_Name);
            if (!options.f_notRestoreExtensionName.Value)
            {
                if (!string.IsNullOrEmpty(assetExtension))
                {
                    extension = "";
                }
                else if (!string.IsNullOrEmpty(item.Container))
                {
                    var ext = Path.GetExtension(item.Container);
                    if (!string.IsNullOrEmpty(item.Container))
                    {
                        extension = ext;
                    }
                }
            }
            if (!TryExportFile(exportPath, item, extension, out var exportFullPath))
                return false;
            File.WriteAllBytes(exportFullPath, m_TextAsset.m_Script);

            Logger.Debug($"{item.TypeString}: \"{item.Text}\" exported to \"{exportFullPath}\"");
            return true;
        }
        
        public static bool ExportMonoBehaviour(AssetItem item, string exportPath, CLIOptions options)
        {
            if (!TryExportFile(exportPath, item, ".json", out var exportFullPath))
                return false;
            var m_MonoBehaviour = (MonoBehaviour)item.Asset;
            var type = m_MonoBehaviour.ToType();
            if (type == null)
            {
                var m_Type = MonoBehaviourToTypeTree(m_MonoBehaviour, options);
                type = m_MonoBehaviour.ToType(m_Type);
            }
            var str = JsonConvert.SerializeObject(type, Formatting.Indented);
            File.WriteAllText(exportFullPath, str);

            Logger.Debug($"{item.TypeString}: \"{item.Text}\" exported to \"{exportFullPath}\"");
            return true;
        }

        public static bool ExportFont(AssetItem item, string exportPath)
        {
            var m_Font = (Font)item.Asset;
            if (m_Font.m_FontData != null)
            {
                var extension = ".ttf";
                if (m_Font.m_FontData[0] == 79 && m_Font.m_FontData[1] == 84 && m_Font.m_FontData[2] == 84 && m_Font.m_FontData[3] == 79)
                {
                    extension = ".otf";
                }
                if (!TryExportFile(exportPath, item, extension, out var exportFullPath))
                    return false;
                File.WriteAllBytes(exportFullPath, m_Font.m_FontData);

                Logger.Debug($"{item.TypeString}: \"{item.Text}\" exported to \"{exportFullPath}\"");
                return true;
            }
            return false;
        }

        public static bool ExportSprite(AssetItem item, string exportPath, CLIOptions options)
        {
            var type = options.o_imageFormat.Value;
            var alphaMask = SpriteMaskMode.On;
            if (!TryExportFile(exportPath, item, "." + type.ToString().ToLower(), out var exportFullPath))
                return false;
            var image = ((Sprite)item.Asset).GetImage(alphaMask);
            if (image != null)
            {
                using (image)
                {
                    using (var file = File.OpenWrite(exportFullPath))
                    {
                        image.WriteToStream(file, type);
                    }
                    Logger.Debug($"{item.TypeString}: \"{item.Text}\" exported to \"{exportFullPath}\"");
                    return true;
                }
            }
            return false;
        }

        public static bool ExportRawFile(AssetItem item, string exportPath)
        {
            if (!TryExportFile(exportPath, item, ".dat", out var exportFullPath))
                return false;
            File.WriteAllBytes(exportFullPath, item.Asset.GetRawData());

            Logger.Debug($"{item.TypeString}: \"{item.Text}\" exported to \"{exportFullPath}\"");
            return true;
        }

        public static bool ExportDumpFile(AssetItem item, string exportPath, CLIOptions options)
        {
            if (!TryExportFile(exportPath, item, ".txt", out var exportFullPath))
                return false;
            var str = item.Asset.Dump();
            if (str == null && item.Asset is MonoBehaviour m_MonoBehaviour)
            {
                var m_Type = MonoBehaviourToTypeTree(m_MonoBehaviour, options);
                str = m_MonoBehaviour.Dump(m_Type);
            }
            if (str != null)
            {
                File.WriteAllText(exportFullPath, str);
                Logger.Debug($"{item.TypeString}: \"{item.Text}\" saved to \"{exportFullPath}\"");
                return true;
            }
            return false;
        }

        private static bool TryExportFile(string dir, AssetItem item, string extension, out string fullPath)
        {
            var fileName = FixFileName(item.Text);
            fullPath = Path.Combine(dir, fileName + extension);
            if (!File.Exists(fullPath))
            {
                Directory.CreateDirectory(dir);
                return true;
            }
            fullPath = Path.Combine(dir, fileName + item.UniqueID + extension);
            if (!File.Exists(fullPath))
            {
                Directory.CreateDirectory(dir);
                return true;
            }
            Logger.Error($"Export error. File \"{fullPath.Color(CLIAnsiColors.BrightRed)}\" already exist");
            return false;
        }

        public static bool ExportConvertFile(AssetItem item, string exportPath, CLIOptions options)
        {
            switch (item.Type)
            {
                case ClassIDType.Texture2D:
                    return ExportTexture2D(item, exportPath, options);
                case ClassIDType.AudioClip:
                    return ExportAudioClip(item, exportPath, options);
                case ClassIDType.VideoClip:
                    return ExportVideoClip(item, exportPath);
                case ClassIDType.MovieTexture:
                    return ExportMovieTexture(item, exportPath);
                case ClassIDType.Shader:
                    return ExportShader(item, exportPath);
                case ClassIDType.TextAsset:
                    return ExportTextAsset(item, exportPath, options);
                case ClassIDType.MonoBehaviour:
                    return ExportMonoBehaviour(item, exportPath, options);
                case ClassIDType.Font:
                    return ExportFont(item, exportPath);
                case ClassIDType.Sprite:
                    return ExportSprite(item, exportPath, options);
                default:
                    return ExportRawFile(item, exportPath);
            }
        }

        public static TypeTree MonoBehaviourToTypeTree(MonoBehaviour m_MonoBehaviour, CLIOptions options)
        {
            if (!assemblyLoader.Loaded)
            {
                var assemblyFolder = options.o_assemblyPath.Value;
                if (assemblyFolder != "")
                {
                    assemblyLoader.Load(assemblyFolder);
                }
                else
                {
                    assemblyLoader.Loaded = true;
                }
            }
            return m_MonoBehaviour.ConvertToTypeTree(assemblyLoader);
        }

        public static string FixFileName(string str)
        {
            if (str.Length >= 260) return Path.GetRandomFileName();
            return Path.GetInvalidFileNameChars().Aggregate(str, (current, c) => current.Replace(c, '_'));
        }
    }
}
