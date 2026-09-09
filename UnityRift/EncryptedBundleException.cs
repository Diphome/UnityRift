using System;

namespace UnityRift
{
    // Raised when a bundle's block info or block data cannot be decompressed in a way
    // that indicates the data is encrypted or obfuscated by a custom, game-specific
    // scheme (i.e. not the standard UnityCN encryption, which is detected separately by
    // its header flag). The loader catches this to report a clear, single-line reason
    // instead of a raw decompression stack trace.
    public sealed class EncryptedBundleException : Exception
    {
        public EncryptedBundleException(string message) : base(message) { }
    }
}
