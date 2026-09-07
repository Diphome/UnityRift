using System.Collections.Generic;
using UnityRift;

namespace SpineExtractor
{
    // A single texture page referenced by a Spine atlas, resolved to the Unity texture that
    // provides its pixels (Texture may be null if no matching texture was found).
    public class SpinePage
    {
        public string FileName;      // name exactly as written in the atlas, e.g. "samurai_oni.png"
        public Texture2D Texture;    // resolved atlas-page texture, or null if unresolved
    }

    public class SpineAtlasFile
    {
        public string Name;                                   // atlas asset name (without extension)
        public byte[] Data;                                   // raw atlas text bytes, written verbatim
        public List<SpinePage> Pages = new List<SpinePage>();
    }

    // A detected Spine model: one skeleton plus the atlas(es) and texture pages that belong to it.
    public class SpineModel
    {
        public string Name;                    // model/output-folder name
        public byte[] SkeletonData;            // raw skeleton bytes (JSON text or binary .skel)
        public SpineSkeletonKind Kind = SpineSkeletonKind.None;
        public List<SpineAtlasFile> Atlases = new List<SpineAtlasFile>();
    }
}
