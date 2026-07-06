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
        public required Func<(SourceTexture? texture, SkippedTexture? skip)> Decode { get; init; }
    }

    public sealed class BuildResult
    {
        public int Written;
        public readonly List<SkippedTexture> Skipped = [];

        /// <summary>The bundle's actual identity (m_Name / m_AssetBundleName): an
        /// explicitly requested name verbatim, or the auto-derived name with the CAB
        /// hash appended for uniqueness.</summary>
        public string Identity = "";
    }

    public static BuildResult Build(
        byte[] seedBundle,
        IReadOnlyList<TextureInput> inputs,
        string assetBundleName,
        string outputPath,
        bool streamingMipmaps = false,
        bool appendCabHash = true
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
                streamingMipmaps,
                appendCabHash
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
        bool streamingMipmaps = false,
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
        return Build(
            seedBundle,
            inputs,
            assetBundleName,
            outputPath,
            streamingMipmaps,
            appendCabHash
        );
    }

    static BuildResult BuildFromSeedFile(
        string seedPath,
        IReadOnlyList<TextureInput> inputs,
        string assetBundleName,
        string outputPath,
        bool streamingMipmaps,
        bool appendCabHash
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
                foreach (var input in inputs)
                {
                    var (tex, skip) = input.Decode();
                    if (tex is null)
                    {
                        if (skip is not null)
                            result.Skipped.Add(skip);
                        continue;
                    }

                    long offset = resS.Position;
                    long size = tex.Data.Length;

                    if (offset + size > uint.MaxValue)
                        throw new InvalidOperationException(
                            "resS exceeds 4 GB; stream offsets are 32-bit in Unity asset bundles"
                        );

                    resS.Write(tex.Data, 0, tex.Data.Length);

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
                    PopulateForKind(texField, tex, offset, size, streamPath, streamingMipmaps);

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
        bool streamingMipmaps
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
                    streamingMipmaps,
                    imageCount: 6,
                    dimension: TextureDimensionCube
                );
                break;

            // Texture3D, Texture2DArray and CubemapArray use the "modern" texture
            // layout (m_Format is a GraphicsFormat, m_Depth / m_CubemapCount, etc.).
            case TextureKind.Texture3D:
            case TextureKind.Texture2DArray:
            case TextureKind.CubemapArray:
                PopulateModernTexture(tex, src, streamOffset, streamSize, streamPath);
                break;

            default:
                PopulateTexture(
                    tex,
                    src,
                    streamOffset,
                    streamSize,
                    streamPath,
                    streamingMipmaps,
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
        bool streamingMipmaps,
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
        SetBool(tex, "m_IsReadable", false);
        SetBool(tex, "m_StreamingMipmaps", streamingMipmaps);
        Set(tex, "m_StreamingMipmapsPriority", 0);
        Set(tex, "m_ImageCount", imageCount);
        Set(tex, "m_TextureDimension", dimension);

        ApplyTextureSettings(tex);

        Set(tex, "m_LightmapFormat", 0);
        Set(tex, "m_ColorSpace", src.ColorSpace);

        ApplyStreamData(tex, streamOffset, streamSize, streamPath);
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
        string streamPath
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

        ApplyTextureSettings(tex);
        SetBool(tex, "m_IsReadable", false);

        ApplyStreamData(tex, streamOffset, streamSize, streamPath);
    }

    static void ApplyTextureSettings(AssetTypeValueField tex)
    {
        var ts = tex["m_TextureSettings"];
        if (ts.IsDummy)
            return;
        Set(ts, "m_FilterMode", 1); // bilinear
        Set(ts, "m_Aniso", 1);
        SetFloat(ts, "m_MipBias", 0f);
        Set(ts, "m_WrapU", 0); // repeat
        Set(ts, "m_WrapV", 0);
        Set(ts, "m_WrapW", 0);
    }

    static void ApplyStreamData(
        AssetTypeValueField tex,
        long streamOffset,
        long streamSize,
        string streamPath
    )
    {
        // No inline pixels: the bytes live in the resS instead.
        tex["image data"].AsByteArray = [];

        var sd = tex["m_StreamData"];
        sd["offset"].AsUInt = checked((uint)streamOffset);
        sd["size"].AsUInt = checked((uint)streamSize);
        sd["path"].AsString = streamPath;
    }

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
