using System;
using System.Text.Json.Serialization;

namespace UnityRift
{
    // Placeholder for a heavy asset (Mesh, AnimationClip, Shader, Font, TextAsset, Avatar,
    // AnimatorController, MovieTexture). At load only the NamedObject header is read, so
    // the object list can be built with the name/type/size while the payload (vertex
    // buffers, curves, shader blobs, text) stays on disk. The full object is parsed the
    // first time something actually needs it - PPtr.TryGet with a concrete type, the
    // GUI/CLI AssetItem.Asset accessor, or Resolve() - and can be dropped again with
    // Release() once an export is done with it. Measured on a 6.4 GB game the eager parse
    // held ~7 GB; the placeholders hold a few hundred bytes each.
    public sealed class LazyObject : NamedObject
    {
        private readonly ObjectInfo _info;
        private readonly Func<SerializedFile, ObjectInfo, Object> _factory;
        private Object _full;

        public LazyObject(ObjectReader reader, ObjectInfo info, Func<SerializedFile, ObjectInfo, Object> factory) : base(reader)
        {
            _info = info;
            _factory = factory;
        }

        [JsonIgnore]
        public bool IsResolved => _full != null;

        // Parses the real object on first call. Hydration re-reads from the file's stream,
        // which is shared by every object of the file (and by every file of a bundle), so
        // it is serialized on the root stream like the parallel loader does.
        public override Object Resolve()
        {
            var full = _full;
            if (full != null)
                return full;
            lock (AssetsManager.SharedStreamOf(assetsFile.reader.BaseStream))
            {
                if (_full == null)
                {
                    full = _factory(assetsFile, _info);
                    if (full != null)
                    {
                        full.Name = Name;
                        full.LazySource = this;
                    }
                    _full = full;
                }
            }
            return _full ?? this;
        }

        // Drop the parsed payload (e.g. after an export) so memory does not regrow to the
        // eager footprint when walking thousands of assets.
        public void Release()
        {
            _full = null;
        }
    }
}
