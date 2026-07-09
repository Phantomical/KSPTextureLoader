using System;
using Unity.Profiling;

namespace KSPTextureLoader.Format.Bundle;

/// <summary>
/// Builds the prefix of a minimal UnityFS bundle containing a single streamed
/// texture object and the <c>AssetBundle</c> that references it. Combined with
/// the pixel payload by <see cref="BundleStream"/>, it is ready to be loaded
/// with <see cref="TextureBundleLoader"/>; the pixel bytes themselves are never
/// copied. Pure CPU work; safe to call from a background thread.
/// </summary>
///
/// <remarks>
/// The whole prefix is written into a single <see cref="BundleBufferWriter"/>: the
/// UnityFS framing (<see cref="BundleWriter"/>), the serialized-file framing
/// (<see cref="SerializedFileWriter"/>) and the two object bodies written here by hand.
/// The object bodies reproduce the exact field order, sizes and alignment padding of
/// Unity 2019.4's own layout — the same layout the embedded type tree encodes, verified
/// against it. Byte offsets and sizes are back-patched, so there are no intermediate
/// buffers and only one final right-sized copy.
/// </remarks>
internal static class TextureBundleBuilder
{
    // Unity BuildTarget.StandaloneWindows64.
    public const int StandaloneWindows64 = 19;

    // UnityEngine.Rendering.TextureDimension values for the classic types.
    const int TextureDimensionTex2D = 2;
    const int TextureDimensionCube = 4;

    // Unity's usual default fallback, TextureFormat.ARGB32.
    const int ForcedFallbackFormat = 4;

    const long AssetBundlePathId = 1;
    const long TexturePathId = 2;

    // Unity canonicalizes the name passed to LoadAsset (lowercase, forward
    // slashes) but compares it against the stored container key verbatim, so
    // a key with uppercase characters or backslashes can never be looked up.
    // A fixed already-canonical key sidesteps that entirely; the caller
    // renames the loaded texture afterwards anyway.
    const string ContainerKey = "texture";

    static readonly ProfilerMarker BuildTextureBundleMarker = new("BuildTextureBundle");

    /// <summary>The texture to wrap in a bundle.</summary>
    public sealed class TextureRequest
    {
        /// <summary>One of the class ids in <see cref="SerializedTypeTrees"/>.</summary>
        public int ClassId;

        /// <summary>Serialized <c>m_Name</c>. Cosmetic only: the asset is looked
        /// up by the fixed container key in <see cref="Built.AssetName"/>, and
        /// callers overwrite the texture's name after loading it.</summary>
        public string Name = "texture";

        public int Width;
        public int Height;

        /// <summary>Array layers (Texture2DArray), depth slices (Texture3D) or
        /// cubemap count (CubemapArray). Ignored by the classic 2D type.</summary>
        public int Depth = 1;

        public int MipCount = 1;

        /// <summary>For the classic types (Texture2D / Cubemap) this is the legacy
        /// <c>TextureFormat</c>; for the modern types it is the
        /// <c>GraphicsFormat</c> written into <c>m_Format</c>.</summary>
        public int Format;

        /// <summary>0 == linear, 1 == sRGB.</summary>
        public int ColorSpace = 1;

        /// <summary>Whether Unity should keep a CPU-side copy of the pixels.</summary>
        public bool Readable;

        /// <summary>The full mip chain in resS byte order. Only used by
        /// <see cref="TextureBundleLoader.CreateAsync"/>; the streamed
        /// <see cref="Build(TextureRequest, long, int)"/> path never
        /// materializes the pixel bytes and ignores this field.</summary>
        public byte[] Pixels = [];
    }

    /// <summary>The built bundle prefix plus the name to request from it. The
    /// complete bundle is the prefix followed by <see cref="PixelsLength"/>
    /// bytes of pixel data, spliced together by <see cref="BundleStream"/>.</summary>
    public readonly struct Built(byte[] prefix, string assetName, long pixelsLength)
    {
        public readonly byte[] Prefix = prefix;
        public readonly string AssetName = assetName;
        public readonly long PixelsLength = pixelsLength;
    }

    public static Built Build(
        TextureRequest req,
        long pixelsLength,
        int targetPlatform = StandaloneWindows64
    ) => Build(req, pixelsLength, externalPath: null, externalOffset: 0, targetPlatform);

    /// <summary>
    /// Build a bundle whose streamed data lives in an existing file on disk.
    /// </summary>
    public static Built Build(
        TextureRequest req,
        long pixelsLength,
        string externalPath,
        long externalOffset,
        int targetPlatform = StandaloneWindows64
    )
    {
        if (req is null)
            throw new ArgumentNullException(nameof(req));
        if (pixelsLength < 0)
            throw new ArgumentOutOfRangeException(nameof(pixelsLength));
        if (externalOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(externalOffset));

        using var scope = BuildTextureBundleMarker.Auto();

        // Use a unique guid so that concurrent loads don't collide.
        string cab = "CAB-" + Guid.NewGuid().ToString("N");
        string streamPath;
        long streamOffset;
        if (externalPath is null)
        {
            streamPath = $"archive:/{cab}/{cab}.resS";
            streamOffset = 0;
        }
        else
        {
            // UnityFS uses forward slashes and runs into issues with backslashes
            // so we need to normalize the path first.
            streamPath = System.IO.Path.GetFullPath(externalPath).Replace('\\', '/');
            streamOffset = externalOffset;
        }

        long streamSize = pixelsLength;
        if (streamOffset + streamSize > uint.MaxValue)
            throw new InvalidOperationException(
                "texture data exceeds 4 GB; stream offsets are 32-bit in Unity bundles"
            );

        long resSLength = externalPath is null ? pixelsLength : 0;

        var w = new BundleBufferWriter(EstimateSize(req));

        var prefix = BundleWriter.WriteHeaderAndBlocksInfo(w, cab, resSLength);

        Span<SerializedFileWriter.ObjectMeta> objects =
        [
            new(AssetBundlePathId, SerializedTypeTrees.AssetBundleClassId),
            new(TexturePathId, req.ClassId),
        ];
        Span<SerializedFileWriter.ObjectSlot> slots = stackalloc SerializedFileWriter.ObjectSlot[2];

        var file = SerializedFileWriter.BeginFile(w, targetPlatform, objects, slots);

        file.BeginObject(w, ref slots[0]);
        WriteAssetBundleBody(w, cab, TexturePathId);
        file.EndObject(w, slots[0]);

        file.BeginObject(w, ref slots[1]);
        WriteTextureBody(w, req, streamPath, streamOffset, streamSize);
        file.EndObject(w, slots[1]);

        long serializedFileLength = file.End(w);

        w.AlignBase = 0;
        BundleWriter.Finish(w, prefix, serializedFileLength);

        return new Built(w.ToArray(), ContainerKey, pixelsLength);
    }

    // A generous starting capacity so the buffer rarely regrows: the framing plus the
    // two verbatim type entries (the type trees dominate) plus room for the bodies.
    static int EstimateSize(TextureRequest req) =>
        1024
        + ReferenceTypeTrees.TypeEntry(SerializedTypeTrees.AssetBundleClassId).Length
        + ReferenceTypeTrees.TypeEntry(req.ClassId).Length;

    static bool IsModern(int classId) =>
        classId == SerializedTypeTrees.Texture3DClassId
        || classId == SerializedTypeTrees.Texture2DArrayClassId
        || classId == SerializedTypeTrees.CubemapArrayClassId;

    static void WriteTextureBody(
        BundleBufferWriter w,
        TextureRequest req,
        string streamPath,
        long streamOffset,
        long streamSize
    )
    {
        if (IsModern(req.ClassId))
            WriteModernTextureBody(w, req, streamPath, streamOffset, streamSize);
        else
            WriteClassicTextureBody(w, req, streamPath, streamOffset, streamSize);
    }

    static void WriteClassicTextureBody(
        BundleBufferWriter w,
        TextureRequest req,
        string streamPath,
        long streamOffset,
        long streamSize
    )
    {
        bool cube = req.ClassId == SerializedTypeTrees.CubemapClassId;
        int imageCount = cube ? 6 : 1;

        // Unlike m_StreamData.size, m_CompleteImageSize is a signed int in the
        // 2019.4 type tree, so the classic types cap at 2 GB per image.
        long imageSize = streamSize / imageCount;
        if (imageSize > int.MaxValue)
            throw new InvalidOperationException(
                "texture data exceeds 2 GB per image; m_CompleteImageSize is a signed 32-bit int"
            );

        w.WriteAlignedString(req.Name); // m_Name
        w.WriteInt32(ForcedFallbackFormat); // m_ForcedFallbackFormat
        w.WriteBool(false); // m_DownscaleFallback
        w.Align(4);
        w.WriteInt32(req.Width); // m_Width
        w.WriteInt32(req.Height); // m_Height
        // The size of one image (a single face's full mip chain), not the total:
        // Unity reads m_ImageCount * m_CompleteImageSize from the stream.
        w.WriteInt32((int)imageSize); // m_CompleteImageSize
        w.WriteInt32(req.Format); // m_TextureFormat
        w.WriteInt32(req.MipCount); // m_MipCount
        w.WriteBool(req.Readable); // m_IsReadable
        w.WriteBool(false); // m_IgnoreMasterTextureLimit
        w.WriteBool(false); // m_IsPreProcessed
        w.WriteBool(false); // m_StreamingMipmaps
        w.Align(4);
        w.WriteInt32(0); // m_StreamingMipmapsPriority
        w.Align(4);
        w.WriteInt32(imageCount); // m_ImageCount
        w.WriteInt32(cube ? TextureDimensionCube : TextureDimensionTex2D); // m_TextureDimension
        WriteTextureSettings(w); // m_TextureSettings
        w.WriteInt32(0); // m_LightmapFormat
        w.WriteInt32(req.ColorSpace); // m_ColorSpace
        WriteEmptyImageData(w); // image data
        WriteStreamData(w, streamOffset, streamSize, streamPath); // m_StreamData

        if (cube)
            w.BeginArray().End(align: true); // m_SourceTextures (empty)
    }

    static void WriteModernTextureBody(
        BundleBufferWriter w,
        TextureRequest req,
        string streamPath,
        long streamOffset,
        long streamSize
    )
    {
        bool cubemapArray = req.ClassId == SerializedTypeTrees.CubemapArrayClassId;
        bool texture3D = req.ClassId == SerializedTypeTrees.Texture3DClassId;

        w.WriteAlignedString(req.Name); // m_Name
        w.WriteInt32(ForcedFallbackFormat); // m_ForcedFallbackFormat
        w.WriteBool(false); // m_DownscaleFallback
        w.Align(4);
        w.WriteInt32(req.ColorSpace); // m_ColorSpace
        w.WriteInt32(req.Format); // m_Format (GraphicsFormat)
        w.WriteInt32(req.Width); // m_Width

        if (cubemapArray)
        {
            w.WriteInt32(req.Depth); // m_CubemapCount
        }
        else
        {
            w.WriteInt32(req.Height); // m_Height
            w.WriteInt32(req.Depth); // m_Depth
        }

        w.WriteInt32(req.MipCount); // m_MipCount
        // Only Texture3D's m_MipCount carries the align flag.
        if (texture3D)
            w.Align(4);
        w.WriteUInt32((uint)streamSize); // m_DataSize (the 4 GB bound is checked in Build)
        WriteTextureSettings(w); // m_TextureSettings
        w.WriteBool(req.Readable); // m_IsReadable
        w.Align(4);
        WriteEmptyImageData(w); // image data
        WriteStreamData(w, streamOffset, streamSize, streamPath); // m_StreamData
    }

    static void WriteTextureSettings(BundleBufferWriter w)
    {
        w.WriteInt32(1); // m_FilterMode (bilinear)
        w.WriteInt32(1); // m_Aniso
        w.WriteSingle(0f); // m_MipBias
        w.WriteInt32(0); // m_WrapU (repeat)
        w.WriteInt32(0); // m_WrapV
        w.WriteInt32(0); // m_WrapW
    }

    // The inline pixel array is always empty (pixels are streamed), but the node still
    // carries a count prefix and the align flag.
    static void WriteEmptyImageData(BundleBufferWriter w) => w.BeginArray().End(align: true);

    // offset and size are unsigned ints; the 4 GB bound is checked in Build.
    static void WriteStreamData(
        BundleBufferWriter w,
        long streamOffset,
        long streamSize,
        string streamPath
    )
    {
        w.WriteUInt32((uint)streamOffset); // offset
        w.WriteUInt32((uint)streamSize); // size
        w.WriteAlignedString(streamPath); // path
    }

    static void WriteAssetBundleBody(BundleBufferWriter w, string identity, long texturePathId)
    {
        w.WriteAlignedString(identity); // m_Name

        // m_PreloadTable is what LoadAssetAsync actually loads during its asynchronous
        // phase: the preload thread reads the objects listed for the requested asset,
        // including their streamed data. Without an entry the request completes having
        // loaded nothing, and the first access to its `asset` property then performs the
        // entire load (pixel read + upload) synchronously on the main thread.
        var preload = w.BeginArray();
        preload.Add();
        WritePPtr(w, fileId: 0, pathId: texturePathId);
        preload.End(align: true);

        // m_Container: map<string, AssetInfo>. Its Array is not aligned.
        var container = w.BeginArray();
        container.Add();
        w.WriteAlignedString(ContainerKey); // pair.first
        WriteAssetInfo(w, preloadIndex: 0, preloadSize: 1, fileId: 0, pathId: texturePathId);
        container.End();

        WriteAssetInfo(w, preloadIndex: 0, preloadSize: 0, fileId: 0, pathId: 0); // m_MainAsset
        w.WriteUInt32(1); // m_RuntimeCompatibility
        w.WriteAlignedString(identity); // m_AssetBundleName
        w.BeginArray().End(align: true); // m_Dependencies (empty)
        w.WriteBool(false); // m_IsStreamedSceneAssetBundle
        w.Align(4);
        w.WriteInt32(0); // m_ExplicitDataLayout
        w.WriteInt32(0); // m_PathFlags
        w.BeginArray().End(); // m_SceneHashes (empty map, Array not aligned)
    }

    static void WritePPtr(BundleBufferWriter w, int fileId, long pathId)
    {
        w.WriteInt32(fileId); // m_FileID
        w.WriteInt64(pathId); // m_PathID
    }

    static void WriteAssetInfo(
        BundleBufferWriter w,
        int preloadIndex,
        int preloadSize,
        int fileId,
        long pathId
    )
    {
        w.WriteInt32(preloadIndex);
        w.WriteInt32(preloadSize);
        WritePPtr(w, fileId, pathId);
    }
}
