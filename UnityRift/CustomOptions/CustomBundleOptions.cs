namespace UnityRift.CustomOptions
{
    public class CustomBundleOptions
    {
        private CompressionType _customBlockCompression = CompressionType.Auto;
        private CompressionType _customBlockInfoCompression = CompressionType.Auto;
        private bool _decompressToDisk;

        public ImportOptions Options;

        public CompressionType CustomBlockCompression
        {
            get => _customBlockCompression;
            set => _customBlockCompression = SetOption(nameof(CustomBlockCompression), value);
        }
        public CompressionType CustomBlockInfoCompression
        {
            get => _customBlockInfoCompression;
            set => _customBlockInfoCompression = SetOption(nameof(CustomBlockInfoCompression), value);
        }
        public bool DecompressToDisk
        {
            get => _decompressToDisk;
            set => _decompressToDisk = SetOption(nameof(DecompressToDisk), value);
        }

        // Per-load decision made by AssetsManager when the project is too big for RAM-mode
        // decompression on this machine. Not a user preference: never persisted, reset on
        // every load, and it never touches DecompressToDisk (the user's explicit choice).
        public bool DecompressToDiskAuto;

        public bool UseDiskDecompression => _decompressToDisk || DecompressToDiskAuto;

        public CustomBundleOptions() { }

        public CustomBundleOptions(ImportOptions importOptions)
        {
            Options = importOptions;
        }

        private static T SetOption<T>(string option, T value)
        {
            Logger.Info($"- {option}: {value}");
            return value;
        }
    }
}
