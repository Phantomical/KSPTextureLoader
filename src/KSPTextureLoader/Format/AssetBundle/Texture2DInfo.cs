using System;
using System.Collections.Generic;
using System.IO;

namespace KSPTextureLoader.Format.AssetBundle;

/// <summary>
/// Raised by <see cref="Texture2DInfo.Extract"/> when a capped object region did
/// not contain everything needed (e.g. a streamed texture, or an unusually long
/// field, extending past the prefix). The caller should re-read the object's full
/// region and retry with <c>regionIsComplete: true</c>.
/// </summary>
internal sealed class IncompleteObjectRegionException : Exception { }

/// <summary>
/// The subset of a <c>Texture2D</c>'s serialized fields needed to recover its
/// raw pixel data on the CPU.
/// </summary>
internal struct Texture2DInfo
{
    public string Name;
    public int Width;
    public int Height;
    public int MipCount;

    /// <summary>The serialized Unity <c>TextureFormat</c> value.</summary>
    public int TextureFormat;

    /// <summary>
    /// Offset (absolute, within the serialized file) of the inline image data,
    /// when not streamed. Valid only when <see cref="ImageDataLength"/> &gt; 0.
    /// </summary>
    public long ImageDataOffset;
    public long ImageDataLength;

    public bool Streamed;
    public long StreamOffset;
    public long StreamSize;
    public string StreamPath;

    /// <summary>The Texture2D class ID in Unity's runtime type system.</summary>
    public const int ClassId = 28;

    /// <summary>
    /// Extract a Texture2D's pixel-recovery metadata from <paramref name="region"/>,
    /// a reader whose position 0 is the object's data start.
    /// </summary>
    ///
    /// <param name="file">The serialized file the object belongs to.</param>
    /// <param name="obj">The Texture2D object entry being read.</param>
    /// <param name="region">Reader over the object's data (position 0 == data start).</param>
    /// <param name="regionIsComplete">
    /// Whether <paramref name="region"/> holds the entire object (its length &gt;=
    /// <c>obj.ByteSize</c>). Streamed and tiny inline textures are small enough to
    /// be read whole; large inline textures are read with only a capped header
    /// prefix so their pixels are never touched.
    /// </param>
    ///
    /// <remarks>
    /// A large object that is not fully contained must be an inline texture (a
    /// streamed object carries no inline pixels and so is always small); we read
    /// only the field header up to the <c>image data</c> length. If that header
    /// itself does not fit, or the object turns out to need its full body, this
    /// throws <see cref="IncompleteObjectRegionException"/> so the caller can
    /// re-read the whole object.
    /// </remarks>
    public static Texture2DInfo Extract(
        SerializedFile file,
        SerializedObject obj,
        EndianBinaryReader region,
        bool regionIsComplete
    )
    {
        region.BigEndian = file.BigEndian;
        region.Position = 0;
        long baseOffset = file.ObjectDataOffset(obj);

        if (regionIsComplete)
            return FromCompleteObject(file, obj, region, baseOffset);

        try
        {
            return FromInlineHeader(file, obj, region, baseOffset);
        }
        catch (EndOfStreamException)
        {
            // The header did not fit in the capped region (e.g. a pathological
            // name). Ask the caller to re-read the whole object.
            throw new IncompleteObjectRegionException();
        }
    }

    /// <summary>
    /// Parse a fully-resident object the straightforward way. Handles streamed and
    /// tiny inline textures; the generic reader records the (resident) inline
    /// image data as offset+length and skips it without copying.
    /// </summary>
    static Texture2DInfo FromCompleteObject(
        SerializedFile file,
        SerializedObject obj,
        EndianBinaryReader region,
        long baseOffset
    )
    {
        var v = file.ReadObjectFrom(obj, region);
        var info = FromScalarFields(v);

        var imageData = v.Field("image data");
        if (imageData is not null && imageData.IsByteArray)
        {
            info.ImageDataOffset = baseOffset + imageData.ByteArrayOffset;
            info.ImageDataLength = imageData.ByteArrayLength;
        }

        ApplyStreamData(ref info, v.Field("m_StreamData"));
        return info;
    }

    /// <summary>
    /// Walk a large inline texture's field header (a capped region), reading only
    /// up to the <c>image data</c> length so the inline pixels are never touched.
    /// </summary>
    static Texture2DInfo FromInlineHeader(
        SerializedFile file,
        SerializedObject obj,
        EndianBinaryReader region,
        long baseOffset
    )
    {
        var root = file.RootNode(obj);
        var fields = new Dictionary<string, TypeTreeValue>(root.Children.Count);

        foreach (var child in root.Children)
        {
            if (child.Self.Name == "image data")
            {
                // The "image data" field's single child is the typeless byte
                // array, which begins with its element count. Read just that; the
                // pixels follow but we never read or skip them.
                int count = region.ReadInt32();
                long imageOffset = baseOffset + region.Position;

                // A large object with no inline pixels can't be reconstructed from
                // the header alone (we'd need m_StreamData, which follows the
                // pixels); have the caller re-read the whole object.
                if (count <= 0)
                    throw new IncompleteObjectRegionException();

                var info = FromScalarFields(new TypeTreeValue { Fields = fields });
                info.ImageDataOffset = imageOffset;
                info.ImageDataLength = count;
                return info;
            }

            fields[child.Self.Name] = TypeTreeReader.Read(child, region);
        }

        // No image data field at all: fall back to a full read.
        throw new IncompleteObjectRegionException();
    }

    static Texture2DInfo FromScalarFields(TypeTreeValue v)
    {
        var info = new Texture2DInfo
        {
            Name = v.Field("m_Name")?.AsString() ?? "",
            Width = (int)(v.Field("m_Width")?.AsInt() ?? 0),
            Height = (int)(v.Field("m_Height")?.AsInt() ?? 0),
            TextureFormat = (int)(v.Field("m_TextureFormat")?.AsInt() ?? 0),
        };

        var mipCount = v.Field("m_MipCount");
        if (mipCount is not null)
        {
            info.MipCount = Math.Max(1, (int)mipCount.AsInt());
        }
        else
        {
            // Older Texture2D layouts use a boolean m_MipMap instead.
            var mipMap = v.Field("m_MipMap");
            info.MipCount =
                mipMap is not null && mipMap.AsInt() != 0
                    ? ComputeMipCount(info.Width, info.Height)
                    : 1;
        }

        return info;
    }

    static void ApplyStreamData(ref Texture2DInfo info, TypeTreeValue streamData)
    {
        if (streamData?.Fields is null)
            return;

        long size = streamData.Field("size")?.AsInt() ?? 0;
        if (size <= 0)
            return;

        info.Streamed = true;
        info.StreamSize = size;
        info.StreamOffset = streamData.Field("offset")?.AsInt() ?? 0;
        info.StreamPath = streamData.Field("path")?.AsString() ?? "";
    }

    static int ComputeMipCount(int width, int height)
    {
        int count = 1;
        while (width > 1 || height > 1)
        {
            width = Math.Max(1, width >> 1);
            height = Math.Max(1, height >> 1);
            count++;
        }
        return count;
    }
}
