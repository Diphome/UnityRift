namespace UnityRift
{
    public static class ModelExporter
    {
        public static void ExportFbx(string path, IImported imported, Fbx.Settings settings) => Fbx.Exporter.Export(path, imported, settings);

        public static void ExportGltf(string path, IImported imported, Gltf.Settings settings) => Gltf.Exporter.Export(path, imported, settings);
    }
}
