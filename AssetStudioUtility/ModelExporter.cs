namespace AssetStudio
{
    public static class ModelExporter
    {
        public static void ExportFbx(string path, IImported imported, Fbx.Settings settings) => Fbx.Exporter.Export(path, imported, settings);
    }
}
