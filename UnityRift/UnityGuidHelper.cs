using System;
using System.Buffers.Binary;

namespace UnityRift
{
    // Unity serializes GUIDs (e.g. Sprite.m_RenderDataKey, SpriteAtlas.m_RenderDataMap
    // keys) in a byte layout that doesn't match .NET's Guid(byte[]) constructor: each
    // byte's nibbles are swapped and the first three components are big-endian. Reading
    // the raw bytes straight into a Guid yields a value that doesn't match what Unity
    // shows. This converts Unity's on-disk form to the equivalent .NET Guid.
    // (Ported from Perfare/AssetStudio PR #1034.)
    internal static class UnityGuidHelper
    {
        public static Guid UnityGuidToGuid(byte[] data, int offset = 0)
        {
            for (var i = 0; i < 16; ++i)
            {
                data[i + offset] = (byte)(((data[i + offset] & 0xF0) >> 4) | ((data[i + offset] & 0x0F) << 4));
            }

            var d1 = BinaryPrimitives.ReadInt32BigEndian(new ReadOnlySpan<byte>(data, offset, 4));
            var d2 = BinaryPrimitives.ReadInt16BigEndian(new ReadOnlySpan<byte>(data, offset + 4, 2));
            var d3 = BinaryPrimitives.ReadInt16BigEndian(new ReadOnlySpan<byte>(data, offset + 6, 2));

            return new Guid(d1, d2, d3,
                data[offset + 8], data[offset + 9], data[offset + 10], data[offset + 11],
                data[offset + 12], data[offset + 13], data[offset + 14], data[offset + 15]);
        }
    }
}
