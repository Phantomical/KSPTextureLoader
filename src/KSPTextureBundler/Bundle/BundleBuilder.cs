using System.Reflection;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using KSPTextureBundler.Textures;

namespace KSPTextureBundler.Bundle;

/// <summary>
/// Builds a KSPTextureLoader-compatible Unity asset bundle by cloning a seed
/// bundle (which carries the Texture2D + AssetBundle type trees), stripping its
/// placeholder assets, and writing one streamed <c>Texture2D</c> per input texture
/// with its pixels stored in an external <c>.resS</c> resource. The result is
/// packed with LZ4 high-compression (Unity's default and what the loader expects).
/// </summary>
internal static class BundleBuilder
{
    const int Texture2DClassId = 28;
    const int CubemapClassId = 89;
    const int Texture3DClassId = 117;
    const int Texture2DArrayClassId = 187;
    const int CubemapArrayClassId = 188;
    const int AssetBundleClassId = 142;

    // UnityEngine.Rendering.TextureDimension values written to m_TextureDimension.
    const int TextureDimensionTex2D = 2;
    const int TextureDimensionCube = 4;

    /// <summary>The AssetBundle object is conventionally path id 1; textures follow.</summary>
    const long AssetBundlePathId = 1;

    /// <summary>
    /// One texture to write into the bundle: its container key (known up-front from
    /// the file path) plus a deferred decode. Decoding lazily keeps only one
    /// texture's pixels in memory at a time, so multi-GB bundles don't have to hold
    /// every texture's data at once.
    /// </summary>
    public sealed class TextureInput
    {
        public required string AddressableName { get; init; }

        /// <summary>The container key as it appears before the write-time lowercasing
        /// (see <see cref="PopulateAssetBundle"/>), used only for diagnostics so
        /// warnings echo the path the user typed. Falls back to
        /// <see cref="AddressableName"/> when not set.</summary>
        public string? DisplayName { get; init; }

        public required Func<(SourceTexture? texture, SkippedTexture? skip)> Decode { get; init; }
    }

    /// <summary>
    /// A progress update emitted while a bundle is being built. <see cref="Completed"/>
    /// counts inputs fully processed out of <see cref="Total"/>, and <see cref="CurrentName"/>
    /// names the input being worked on (or the phase, e.g. "compressing", once every
    /// input is done).
    /// </summary>
    public readonly record struct BuildProgress(int Completed, int Total, string CurrentName);

    public sealed class BuildResult
    {
        public int Written;
        public readonly List<SkippedTexture> Skipped = [];

        /// <summary>Block-compressed textures whose width or height was not a multiple
        /// of the block size. They were written (stored inline; see
        /// <see cref="NeedsInlineData"/>) but the misalignment is worth flagging.</summary>
        public readonly List<BlockMisalignedTexture> BlockMisaligned = [];

        /// <summary>The bundle's actual identity (m_Name / m_AssetBundleName): an
        /// explicitly requested name verbatim, or the auto-derived name with the CAB
        /// hash appended for uniqueness.</summary>
        public string Identity = "";
    }

    /// <summary>
    /// A block-compressed texture whose dimensions are not a multiple of the block
    /// size, captured so the caller can warn about it. Such a texture is still
    /// written, but stored inline rather than streamed (see <see cref="NeedsInlineData"/>).
    /// </summary>
    public sealed class BlockMisalignedTexture
    {
        public required string SourcePath { get; init; }

        /// <summary>The texture's container key as it will appear in the bundle, before
        /// the write-time lowercasing and backslash-to-slash canonicalization.</summary>
        public required string AddressableName { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required UnityTextureFormat Format { get; init; }
        public required int BlockWidth { get; init; }
        public required int BlockHeight { get; init; }
    }

    public static BuildResult Build(
        byte[] seedBundle,
        IReadOnlyList<TextureInput> inputs,
        string assetBundleName,
        string outputPath,
        bool appendCabHash = true,
        Action<BuildProgress>? onProgress = null,
        Action<BlockMisalignedTexture>? onBlockMisaligned = null
    )
    {
        // The seed must be loaded from a path; write it to a temp file first.
        string seedTmp = Path.Combine(Path.GetTempPath(), $"ksptb_seed_{Guid.NewGuid():N}.bundle");
        File.WriteAllBytes(seedTmp, seedBundle);

        try
        {
            return BuildFromSeedFile(
                seedTmp,
                inputs,
                assetBundleName,
                outputPath,
                appendCabHash,
                onProgress,
                onBlockMisaligned
            );
        }
        finally
        {
            try
            {
                File.Delete(seedTmp);
            }
            catch
            {
                // best effort
            }
        }
    }

    /// <summary>Convenience overload for callers that already hold decoded textures.</summary>
    public static BuildResult Build(
        byte[] seedBundle,
        IReadOnlyList<SourceTexture> textures,
        string assetBundleName,
        string outputPath,
        bool appendCabHash = true
    )
    {
        var inputs = textures
            .Select(t => new TextureInput
            {
                AddressableName = string.IsNullOrEmpty(t.AddressableName)
                    ? t.Name
                    : t.AddressableName,
                Decode = () => ((SourceTexture?)t, (SkippedTexture?)null),
            })
            .ToList();
        return Build(seedBundle, inputs, assetBundleName, outputPath, appendCabHash);
    }

    static BuildResult BuildFromSeedFile(
        string seedPath,
        IReadOnlyList<TextureInput> inputs,
        string assetBundleName,
        string outputPath,
        bool appendCabHash,
        Action<BuildProgress>? onProgress,
        Action<BlockMisalignedTexture>? onBlockMisaligned
    )
    {
        var result = new BuildResult();
        string cab = CabName.ForBundle(assetBundleName, inputs.Select(i => i.AddressableName));
        string resSName = cab + ".resS";
        string streamPath = $"archive:/{cab}/{resSName}";

        // When no explicit name was given, the bundle's identity (m_Name /
        // m_AssetBundleName) carries the CAB hash so two auto-named bundles never
        // collide in Unity's runtime bundle registry. An explicitly requested name is
        // used verbatim. The CAB stays derived from the base name only, so appending
        // it here introduces no circularity and does not move the resS.
        string identity = appendCabHash
            ? $"{assetBundleName}_{cab.Substring("CAB-".Length)}"
            : assetBundleName;
        result.Identity = identity;

        var am = new AssetsManager();
        var bunInst = am.LoadBundleFile(seedPath, true);
        var bundle = bunInst.file;

        int serIdx = FindSerializedEntry(bundle);
        var afileInst = am.LoadAssetsFileFromBundle(bunInst, serIdx, false);
        var afile = afileInst.file;

        if (!afile.Metadata.TypeTreeEnabled)
            throw new InvalidOperationException("seed bundle has no type trees");

        // The seed only carries the Texture2D and AssetBundle type trees. To emit the
        // other texture objects (Cubemap, Texture3D, Texture2DArray, CubemapArray) we
        // synthesise their type trees from the embedded class-data package, matched to
        // the seed file's exact Unity version. AssetFileInfo.Create then registers a
        // class's type tree into the file metadata on first use.
        using (var tpk = OpenEmbeddedClassPackage())
        {
            am.LoadClassPackage(tpk);
            am.LoadClassDatabaseFromPackage(afile.Metadata.UnityVersion);
        }

        // Start from a clean object table, keeping the type-tree metadata.
        foreach (var info in afile.Metadata.AssetInfos.ToList())
            afile.Metadata.RemoveAssetInfo(info);

        // The resS is streamed to a temp file rather than held in memory: Unity
        // allows resS resources up to 4 GB (the 32-bit m_StreamData.offset bound),
        // which is past the 2 GB limit of a byte[]/MemoryStream.
        string resTmp = Path.Combine(Path.GetTempPath(), $"ksptb_ress_{Guid.NewGuid():N}.bin");
        try
        {
            long nextPathId = AssetBundlePathId + 1;
            var containerEntries = new List<(string name, long pathId)>(inputs.Count);

            // Concatenate every texture's mip chain into the resS, recording offsets.
            // Each texture is decoded here, written, then released before the next.
            using (var resS = new FileStream(resTmp, FileMode.Create, FileAccess.Write))
            {
                for (int i = 0; i < inputs.Count; i++)
                {
                    var input = inputs[i];
                    // Report before decoding item i: i inputs are fully processed and
                    // this one is about to be worked on. Decoding is the slow step, so
                    // this keeps the displayed name in sync with the actual work.
                    onProgress?.Invoke(new BuildProgress(i, inputs.Count, input.AddressableName));

                    var (tex, skip) = input.Decode();
                    if (tex is null)
                    {
                        if (skip is not null)
                            result.Skipped.Add(skip);
                        continue;
                    }

                    long size = tex.Data.Length;

                    // Compressed textures whose dimensions are not a multiple of the
                    // block size crash Unity's DX12/Vulkan async streamed-upload path
                    // (it double-frees the per-slot upload record while building the
                    // rescaled copy Unity insists on for a non-block-aligned size).
                    // Store their pixels inline instead, so Unity uploads them during
                    // deserialization rather than through the async pipeline.
                    bool inline = NeedsInlineData(tex);
                    if (inline)
                    {
                        var misaligned = new BlockMisalignedTexture
                        {
                            SourcePath = tex.SourcePath,
                            AddressableName = input.DisplayName ?? input.AddressableName,
                            Width = tex.Width,
                            Height = tex.Height,
                            Format = tex.Format,
                            BlockWidth = TextureFormatInfo.BlockWidth(tex.Format),
                            BlockHeight = TextureFormatInfo.BlockHeight(tex.Format),
                        };
                        result.BlockMisaligned.Add(misaligned);
                        onBlockMisaligned?.Invoke(misaligned);
                    }

                    long offset = 0;
                    if (!inline)
                    {
                        offset = resS.Position;

                        if (offset + size > uint.MaxValue)
                            throw new InvalidOperationException(
                                "resS exceeds 4 GB; stream offsets are 32-bit in Unity asset bundles"
                            );

                        resS.Write(tex.Data, 0, tex.Data.Length);
                    }

                    long pathId = nextPathId++;
                    int classId = ClassIdFor(tex.Kind);

                    // Create the asset info first: for a class not yet in the seed's
                    // type list this pulls and registers its type tree from the class
                    // database, which CreateValueBaseField then reads.
                    var info = AssetFileInfo.Create(afile, pathId, classId, am.ClassDatabase);
                    if (info is null)
                        throw new InvalidOperationException(
                            $"no type tree available for class {classId} ({tex.Kind})"
                        );

                    var texField = am.CreateValueBaseField(afileInst, classId);
                    PopulateForKind(texField, tex, offset, size, streamPath, inline);

                    info.SetNewData(texField);
                    afile.Metadata.AddAssetInfo(info);

                    containerEntries.Add((input.AddressableName, pathId));
                    result.Written++;
                }
            }

            if (result.Written == 0)
                return result; // nothing decoded; caller reports it

            // The AssetBundle object: its container maps asset names -> texture objects.
            var abField = am.CreateValueBaseField(afileInst, AssetBundleClassId);
            PopulateAssetBundle(abField, identity, containerEntries);
            var abInfo = AssetFileInfo.Create(afile, AssetBundlePathId, AssetBundleClassId);
            abInfo.SetNewData(abField);
            afile.Metadata.AddAssetInfo(abInfo);

            // Rebuild the bundle directory: the (renamed) serialized file + our resS.
            // The resS is attached as a stream so it is never fully buffered.
            var serDir = bundle.BlockAndDirInfo.DirectoryInfos[serIdx];
            serDir.Name = cab;
            serDir.SetNewData(afile);

            var resDir = AssetBundleDirectoryInfo.Create(resSName, isSerialized: false);
            resDir.Replacer = new ContentReplacerFromStream(
                File.OpenRead(resTmp),
                0,
                -1, // -1 => the whole stream (its 64-bit length), so > 2 GB is fine
                closeOnWrite: true
            );

            bundle.BlockAndDirInfo.DirectoryInfos.Clear();
            bundle.BlockAndDirInfo.DirectoryInfos.Add(serDir);
            bundle.BlockAndDirInfo.DirectoryInfos.Add(resDir);

            // Every input is decoded; the remaining work is one LZ4 compression pass,
            // which for a large bundle is slow enough to be worth surfacing.
            onProgress?.Invoke(new BuildProgress(inputs.Count, inputs.Count, "compressing (LZ4)"));
            WriteAndPack(bundle, outputPath);
            return result;
        }
        finally
        {
            try
            {
                File.Delete(resTmp);
            }
            catch
            {
                // best effort
            }
        }
    }

    /// <summary>The class ids the loader can emit an object for, in the order their
    /// type trees are registered into the reference bundle.</summary>
    static readonly int[] BundleClassIds =
    [
        Texture2DClassId,
        CubemapClassId,
        Texture3DClassId,
        Texture2DArrayClassId,
        CubemapArrayClassId,
        AssetBundleClassId,
    ];

    /// <summary>
    /// Build a reference "type-tree bundle": an uncompressed UnityFS bundle whose
    /// serialized file carries the type trees for every texture class plus the
    /// AssetBundle scaffolding, and contains zero objects. The runtime loader ships
    /// this artifact and copies its type-tree section verbatim into the bundles it
    /// generates, so it no longer has to hand-roll those schemas.
    /// </summary>
    public static void BuildTypeTreeBundle(byte[] seedBundle, string outputPath)
    {
        string seedTmp = Path.Combine(Path.GetTempPath(), $"ksptb_seed_{Guid.NewGuid():N}.bundle");
        File.WriteAllBytes(seedTmp, seedBundle);
        try
        {
            BuildTypeTreeBundleFromSeedFile(seedTmp, outputPath);
        }
        finally
        {
            try
            {
                File.Delete(seedTmp);
            }
            catch
            {
                // best effort
            }
        }
    }

    static void BuildTypeTreeBundleFromSeedFile(string seedPath, string outputPath)
    {
        var am = new AssetsManager();
        var bunInst = am.LoadBundleFile(seedPath, true);
        var bundle = bunInst.file;

        int serIdx = FindSerializedEntry(bundle);
        var afileInst = am.LoadAssetsFileFromBundle(bunInst, serIdx, false);
        var afile = afileInst.file;

        if (!afile.Metadata.TypeTreeEnabled)
            throw new InvalidOperationException("seed bundle has no type trees");

        using (var tpk = OpenEmbeddedClassPackage())
        {
            am.LoadClassPackage(tpk);
            am.LoadClassDatabaseFromPackage(afile.Metadata.UnityVersion);
        }

        // Drop every placeholder object; keep only the type-tree metadata.
        foreach (var info in afile.Metadata.AssetInfos.ToList())
            afile.Metadata.RemoveAssetInfo(info);

        // Register the type tree for every class the loader can emit. AssetFileInfo.Create
        // pulls and appends a class's type tree from the class database on first use;
        // Texture2D and AssetBundle already come from the seed, so those calls just find
        // the existing entry. The returned info is discarded -- we want the registered
        // types, not the objects, so AssetInfos stays empty. AssetsFile.Write emits the
        // TypeTreeTypes verbatim regardless of whether any object references them.
        foreach (int classId in BundleClassIds)
        {
            var info = AssetFileInfo.Create(afile, AssetBundlePathId, classId, am.ClassDatabase);
            if (info is null)
                throw new InvalidOperationException($"no type tree available for class {classId}");
        }

        // Serialized file only; no resS (there is no pixel data).
        var serDir = bundle.BlockAndDirInfo.DirectoryInfos[serIdx];
        serDir.SetNewData(afile);

        bundle.BlockAndDirInfo.DirectoryInfos.Clear();
        bundle.BlockAndDirInfo.DirectoryInfos.Add(serDir);

        // Written uncompressed so the loader can parse it without an LZ4 bulk-data
        // decompress step (its slim bundle reader only decompresses the blocks info).
        string outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        Directory.CreateDirectory(outDir);
        using var fs = File.Create(outputPath);
        using var writer = new AssetsFileWriter(fs);
        bundle.Write(writer);
    }

    static int ClassIdFor(TextureKind kind) =>
        kind switch
        {
            TextureKind.Cubemap => CubemapClassId,
            TextureKind.Texture3D => Texture3DClassId,
            TextureKind.Texture2DArray => Texture2DArrayClassId,
            TextureKind.CubemapArray => CubemapArrayClassId,
            _ => Texture2DClassId,
        };

    /// <summary>Dispatch to the right field layout for the texture's object kind.</summary>
    static void PopulateForKind(
        AssetTypeValueField tex,
        SourceTexture src,
        long streamOffset,
        long streamSize,
        string streamPath,
        bool inline
    )
    {
        switch (src.Kind)
        {
            // Cubemap shares Texture2D's field layout exactly (plus a trailing,
            // left-empty m_SourceTextures), differing only in image count and the
            // texture dimension.
            case TextureKind.Cubemap:
                PopulateTexture(
                    tex,
                    src,
                    streamOffset,
                    streamSize,
                    streamPath,
                    inline,
                    imageCount: 6,
                    dimension: TextureDimensionCube
                );
                break;

            // Texture3D, Texture2DArray and CubemapArray use the "modern" texture
            // layout (m_Format is a GraphicsFormat, m_Depth / m_CubemapCount, etc.).
            case TextureKind.Texture3D:
            case TextureKind.Texture2DArray:
            case TextureKind.CubemapArray:
                PopulateModernTexture(tex, src, streamOffset, streamSize, streamPath, inline);
                break;

            default:
                PopulateTexture(
                    tex,
                    src,
                    streamOffset,
                    streamSize,
                    streamPath,
                    inline,
                    imageCount: 1,
                    dimension: TextureDimensionTex2D
                );
                break;
        }
    }

    /// <summary>Populate a classic Texture2D / Cubemap object (m_TextureFormat layout).</summary>
    static void PopulateTexture(
        AssetTypeValueField tex,
        SourceTexture src,
        long streamOffset,
        long streamSize,
        string streamPath,
        bool inline,
        int imageCount,
        int dimension
    )
    {
        tex["m_Name"].AsString = src.Name;
        Set(tex, "m_ForcedFallbackFormat", 4); // ARGB32, Unity's usual default
        SetBool(tex, "m_DownscaleFallback", false);
        tex["m_Width"].AsInt = src.Width;
        tex["m_Height"].AsInt = src.Height;
        // The size of one image (a single face's full mip chain), not the total:
        // Unity reads m_ImageCount * m_CompleteImageSize from the stream.
        tex["m_CompleteImageSize"].AsInt = checked((int)(streamSize / imageCount));
        tex["m_TextureFormat"].AsInt = (int)src.Format;
        Set(tex, "m_MipCount", src.MipCount);
        SetBool(tex, "m_IsReadable", src.Readable);
        SetBool(tex, "m_StreamingMipmaps", src.StreamingMipmaps);
        Set(tex, "m_StreamingMipmapsPriority", src.StreamingMipmapsPriority);
        Set(tex, "m_ImageCount", imageCount);
        Set(tex, "m_TextureDimension", dimension);

        ApplyTextureSettings(tex, src);

        Set(tex, "m_LightmapFormat", 0);
        Set(tex, "m_ColorSpace", src.ColorSpace);

        ApplyPixelStorage(tex, src.Data, inline, streamOffset, streamSize, streamPath);
    }

    /// <summary>
    /// Populate a Texture3D / Texture2DArray / CubemapArray object. These newer types
    /// serialize their format in <c>m_Format</c> as a <c>GraphicsFormat</c> (not the
    /// classic <c>m_TextureFormat</c>), and carry a slice/layer count plus an explicit
    /// <c>m_DataSize</c> rather than <c>m_CompleteImageSize</c>/<c>m_ImageCount</c>.
    /// </summary>
    static void PopulateModernTexture(
        AssetTypeValueField tex,
        SourceTexture src,
        long streamOffset,
        long streamSize,
        string streamPath,
        bool inline
    )
    {
        tex["m_Name"].AsString = src.Name;
        Set(tex, "m_ForcedFallbackFormat", 4); // ARGB32
        SetBool(tex, "m_DownscaleFallback", false);
        Set(tex, "m_ColorSpace", src.ColorSpace);
        Set(tex, "m_Format", TextureFormatInfo.ToGraphicsFormat(src.Format, src.ColorSpace));
        tex["m_Width"].AsInt = src.Width;

        if (src.Kind == TextureKind.CubemapArray)
        {
            // CubemapArray faces are square (no m_Height) and it counts cubemaps.
            Set(tex, "m_CubemapCount", src.Layers);
        }
        else
        {
            tex["m_Height"].AsInt = src.Height;
            // m_Depth is the array layer count (Texture2DArray) or depth (Texture3D).
            Set(tex, "m_Depth", src.Layers);
        }

        Set(tex, "m_MipCount", src.MipCount);
        SetUInt(tex, "m_DataSize", checked((uint)streamSize));

        ApplyTextureSettings(tex, src);
        SetBool(tex, "m_IsReadable", src.Readable);

        ApplyPixelStorage(tex, src.Data, inline, streamOffset, streamSize, streamPath);
    }

    static void ApplyTextureSettings(AssetTypeValueField tex, SourceTexture src)
    {
        var ts = tex["m_TextureSettings"];
        if (ts.IsDummy)
            return;
        Set(ts, "m_FilterMode", (int)src.Filter);
        Set(ts, "m_Aniso", src.Aniso);
        SetFloat(ts, "m_MipBias", src.MipBias);
        Set(ts, "m_WrapU", (int)src.WrapU);
        Set(ts, "m_WrapV", (int)src.WrapV);
        Set(ts, "m_WrapW", (int)src.WrapW);
    }

    /// <summary>
    /// Write the texture's pixels either inline (in <c>image data</c>) or as a
    /// reference into the streamed <c>.resS</c> (<c>m_StreamData</c>). Inline
    /// storage keeps textures that would otherwise crash Unity's DX12/Vulkan async
    /// streamed-upload path off it -- see <see cref="NeedsInlineData"/>.
    /// </summary>
    static void ApplyPixelStorage(
        AssetTypeValueField tex,
        byte[] data,
        bool inline,
        long streamOffset,
        long streamSize,
        string streamPath
    )
    {
        var sd = tex["m_StreamData"];

        if (inline)
        {
            // Pixels live in the serialized object; leave m_StreamData empty so
            // Unity uploads them during deserialization instead of streaming them.
            tex["image data"].AsByteArray = data;
            sd["offset"].AsUInt = 0;
            sd["size"].AsUInt = 0;
            sd["path"].AsString = "";
            return;
        }

        // No inline pixels: the bytes live in the resS instead.
        tex["image data"].AsByteArray = [];
        sd["offset"].AsUInt = checked((uint)streamOffset);
        sd["size"].AsUInt = checked((uint)streamSize);
        sd["path"].AsString = streamPath;
    }

    /// <summary>
    /// Whether a texture's pixels must be stored inline rather than streamed. True
    /// for block-compressed formats whose width or height is not a multiple of the
    /// block size: such a texture forces Unity to build a rescaled upload copy, and
    /// the DX12/Vulkan async upload-completion path double-frees the per-slot upload
    /// record while doing so (a use-after-free that reliably crashes). The runtime
    /// loose-texture path guards the same case in <c>DDSLoader.NeedsUnscaledUpload</c>.
    /// </summary>
    static bool NeedsInlineData(SourceTexture tex) =>
        TextureFormatInfo.IsBlockCompressed(tex.Format)
        && (
            tex.Width % TextureFormatInfo.BlockWidth(tex.Format) != 0
            || tex.Height % TextureFormatInfo.BlockHeight(tex.Format) != 0
        );

    static Stream OpenEmbeddedClassPackage()
    {
        var asm = Assembly.GetExecutingAssembly();
        string? resource = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("classdata.tpk", StringComparison.OrdinalIgnoreCase));
        if (resource is null)
            throw new InvalidOperationException("embedded classdata.tpk not found");
        return asm.GetManifestResourceStream(resource)!;
    }

    static void PopulateAssetBundle(
        AssetTypeValueField ab,
        string name,
        List<(string name, long pathId)> entries
    )
    {
        ab["m_Name"].AsString = name;
        SetString(ab, "m_AssetBundleName", name);
        Set(ab, "m_RuntimeCompatibility", 1);

        // Full-path lookup only. m_PathFlags controls the secondary name indexes
        // Unity synthesizes from the container at load: bit0 (1) = filename
        // without extension, bit1 (2) = filename with extension, bit2 (4) =
        // case-insensitive queries. 0 (kPathFlagsNone) builds none of them, so a
        // texture is only ever resolvable by its full container path. Textures are
        // always loaded by full path and many share a bare file name across planet
        // sub-directories (mid00, rockatlas, ...), so the basename tables would
        // only introduce silent last-writer-wins collisions. (Full-path lookup is
        // never gated by these flags, so 0 does not disable it.)
        Set(ab, "m_PathFlags", 0);

        var container = ab["m_Container"]["Array"];
        foreach (var (texName, pathId) in entries)
        {
            var pair = ValueBuilder.DefaultValueFieldFromArrayTemplate(container);
            // Container keys must be lowercase with '/' separators, matching the
            // EditorExtensions bundler's NormalizePath: Unity canonicalizes the
            // name passed to LoadAsset the same way but compares it against the
            // stored key verbatim, so a key with uppercase characters or
            // backslashes can never be looked up.
            pair["first"].AsString = texName.Replace('\\', '/').ToLowerInvariant();
            var second = pair["second"];
            second["preloadIndex"].AsInt = 0;
            second["preloadSize"].AsInt = 0;
            second["asset"]["m_FileID"].AsInt = 0;
            second["asset"]["m_PathID"].AsLong = pathId;
            container.Children.Add(pair);
        }
    }

    static void WriteAndPack(AssetBundleFile bundle, string outputPath)
    {
        // Pack reads from a decompressed source, so stage the uncompressed bundle
        // in a temp file first (AssetsFileWriter closes its stream on dispose, so a
        // MemoryStream can't be reused across the write/read boundary).
        string tmp = Path.Combine(Path.GetTempPath(), $"ksptb_unpacked_{Guid.NewGuid():N}.bundle");
        try
        {
            using (var fs = File.Create(tmp))
            using (var writer = new AssetsFileWriter(fs))
                bundle.Write(writer);

            var packed = new AssetBundleFile();
            packed.Read(new AssetsFileReader(File.OpenRead(tmp)));

            string outDir = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
            Directory.CreateDirectory(outDir);
            using (var fs = File.Create(outputPath))
            using (var writer = new AssetsFileWriter(fs))
                packed.Pack(writer, AssetBundleCompressionType.LZ4); // LZ4 == high-compression (HC)

            packed.Close();
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch
            {
                // best effort
            }
        }
    }

    static int FindSerializedEntry(AssetBundleFile bundle)
    {
        var dirs = bundle.BlockAndDirInfo.DirectoryInfos;
        for (int i = 0; i < dirs.Count; i++)
            if ((dirs[i].Flags & 0x4) != 0)
                return i;
        throw new InvalidOperationException("seed bundle has no serialized file entry");
    }

    // --- small helpers that tolerate fields missing from a given type-tree version ---

    static void Set(AssetTypeValueField parent, string field, int value)
    {
        var f = parent[field];
        if (!f.IsDummy)
            f.AsInt = value;
    }

    static void SetString(AssetTypeValueField parent, string field, string value)
    {
        var f = parent[field];
        if (!f.IsDummy)
            f.AsString = value;
    }

    static void SetBool(AssetTypeValueField parent, string field, bool value)
    {
        var f = parent[field];
        if (!f.IsDummy)
            f.AsBool = value;
    }

    static void SetUInt(AssetTypeValueField parent, string field, uint value)
    {
        var f = parent[field];
        if (!f.IsDummy)
            f.AsUInt = value;
    }

    static void SetFloat(AssetTypeValueField parent, string field, float value)
    {
        var f = parent[field];
        if (!f.IsDummy)
            f.AsFloat = value;
    }
}
