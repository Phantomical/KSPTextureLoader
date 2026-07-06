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
internal static class TextureBundleBuilder
{
    // Unity BuildTarget.StandaloneWindows64.
    public const int StandaloneWindows64 = 19;

    // UnityEngine.Rendering.TextureDimension values for the classic types.
    const int TextureDimensionTex2D = 2;
    const int TextureDimensionCube = 4;

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

        SerializedValue texture = BuildTextureValue(req, streamPath, streamOffset, streamSize);
        SerializedValue assetBundle = BuildAssetBundleValue(cab, TexturePathId);

        var objects = new[]
        {
            new SerializedFileWriter.ObjectEntry(
                AssetBundlePathId,
                SerializedTypeTrees.AssetBundleClassId,
                assetBundle
            ),
            new SerializedFileWriter.ObjectEntry(TexturePathId, req.ClassId, texture),
        };

        byte[] serializedFile = SerializedFileWriter.Build(objects, targetPlatform);
        long resSLength = externalPath is null ? pixelsLength : 0;
        byte[] prefix = BundleWriter.BuildPrefix(cab, serializedFile, resSLength);
        return new Built(prefix, ContainerKey, pixelsLength);
    }

    static SerializedValue BuildTextureValue(
        TextureRequest req,
        string streamPath,
        long streamOffset,
        long streamSize
    )
    {
        return IsModern(req.ClassId)
            ? BuildModernTexture(req, streamPath, streamOffset, streamSize)
            : BuildClassicTexture(req, streamPath, streamOffset, streamSize);
    }

    static bool IsModern(int classId) =>
        classId == SerializedTypeTrees.Texture3DClassId
        || classId == SerializedTypeTrees.Texture2DArrayClassId
        || classId == SerializedTypeTrees.CubemapArrayClassId;

    static SerializedValue BuildClassicTexture(
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

        var tex = SerializedValue
            .Struct()
            .SetString("m_Name", req.Name)
            .SetInt("m_ForcedFallbackFormat", 4) // ARGB32, Unity's usual default
            .SetBool("m_DownscaleFallback", false)
            .SetInt("m_Width", req.Width)
            .SetInt("m_Height", req.Height)
            // The size of one image (a single face's full mip chain), not the
            // total: Unity reads m_ImageCount * m_CompleteImageSize from the stream.
            .SetInt("m_CompleteImageSize", imageSize)
            .SetInt("m_TextureFormat", req.Format)
            .SetInt("m_MipCount", req.MipCount)
            .SetBool("m_IsReadable", req.Readable)
            .SetBool("m_IgnoreMasterTextureLimit", false)
            .SetBool("m_IsPreProcessed", false)
            .SetBool("m_StreamingMipmaps", false)
            .SetInt("m_StreamingMipmapsPriority", 0)
            .SetInt("m_ImageCount", imageCount)
            .SetInt("m_TextureDimension", cube ? TextureDimensionCube : TextureDimensionTex2D)
            .Set("m_TextureSettings", TextureSettings())
            .SetInt("m_LightmapFormat", 0)
            .SetInt("m_ColorSpace", req.ColorSpace)
            .SetBytes("image data", [])
            .Set("m_StreamData", StreamData(streamOffset, streamSize, streamPath));

        if (cube)
            tex.Set("m_SourceTextures", SerializedValue.Array());

        return tex;
    }

    static SerializedValue BuildModernTexture(
        TextureRequest req,
        string streamPath,
        long streamOffset,
        long streamSize
    )
    {
        bool cubemapArray = req.ClassId == SerializedTypeTrees.CubemapArrayClassId;

        var tex = SerializedValue
            .Struct()
            .SetString("m_Name", req.Name)
            .SetInt("m_ForcedFallbackFormat", 4)
            .SetBool("m_DownscaleFallback", false)
            .SetInt("m_ColorSpace", req.ColorSpace)
            .SetInt("m_Format", req.Format) // GraphicsFormat
            .SetInt("m_Width", req.Width);

        if (cubemapArray)
        {
            tex.SetInt("m_CubemapCount", req.Depth);
        }
        else
        {
            tex.SetInt("m_Height", req.Height);
            tex.SetInt("m_Depth", req.Depth);
        }

        tex.SetInt("m_MipCount", req.MipCount)
            // m_DataSize is an unsigned int; the 4 GB bound is checked in Build.
            .SetInt("m_DataSize", streamSize)
            .Set("m_TextureSettings", TextureSettings())
            .SetBool("m_IsReadable", req.Readable)
            .SetBytes("image data", [])
            .Set("m_StreamData", StreamData(streamOffset, streamSize, streamPath));

        return tex;
    }

    static SerializedValue TextureSettings() =>
        SerializedValue
            .Struct()
            .SetInt("m_FilterMode", 1) // bilinear
            .SetInt("m_Aniso", 1)
            .SetFloat("m_MipBias", 0f)
            .SetInt("m_WrapU", 0) // repeat
            .SetInt("m_WrapV", 0)
            .SetInt("m_WrapW", 0);

    // offset and size are unsigned ints; the 4 GB bound is checked in Build.
    static SerializedValue StreamData(long streamOffset, long streamSize, string streamPath) =>
        SerializedValue
            .Struct()
            .SetInt("offset", streamOffset)
            .SetInt("size", streamSize)
            .SetString("path", streamPath);

    static SerializedValue BuildAssetBundleValue(string identity, long texturePathId)
    {
        // The preload table is what LoadAssetAsync actually loads during its
        // asynchronous phase: the preload thread reads the objects listed for
        // the requested asset, including their streamed data. Without an entry
        // the request completes having loaded nothing, and the first access to
        // its `asset` property then performs the entire load (pixel read +
        // upload) synchronously on the main thread.
        var preloadTable = SerializedValue.Array();
        preloadTable.Elements.Add(
            SerializedValue.Struct().SetInt("m_FileID", 0).Set("m_PathID", PathId(texturePathId))
        );

        var container = SerializedValue.Array();
        container.Elements.Add(
            SerializedValue
                .Struct()
                .SetString("first", ContainerKey)
                .Set("second", AssetInfo(0, 1, 0, texturePathId))
        );

        return SerializedValue
            .Struct()
            .SetString("m_Name", identity)
            .Set("m_PreloadTable", preloadTable)
            .Set("m_Container", container)
            .Set("m_MainAsset", AssetInfo(0, 0, 0, 0))
            .SetInt("m_RuntimeCompatibility", 1)
            .SetString("m_AssetBundleName", identity)
            .Set("m_Dependencies", SerializedValue.Array())
            .SetBool("m_IsStreamedSceneAssetBundle", false)
            .SetInt("m_ExplicitDataLayout", 0)
            .SetInt("m_PathFlags", 0)
            .Set("m_SceneHashes", SerializedValue.Array());
    }

    static SerializedValue AssetInfo(int preloadIndex, int preloadSize, int fileId, long pathId) =>
        SerializedValue
            .Struct()
            .SetInt("preloadIndex", preloadIndex)
            .SetInt("preloadSize", preloadSize)
            .Set(
                "asset",
                SerializedValue.Struct().SetInt("m_FileID", fileId).Set("m_PathID", PathId(pathId))
            );

    static SerializedValue PathId(long pathId) => new() { Int = pathId };
}
