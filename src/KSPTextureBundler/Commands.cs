using System.Reflection;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using KSPTextureBundler.Bundle;
using KSPTextureBundler.Textures;

namespace KSPTextureBundler;

/// <summary>
/// Implementations behind the CLI verbs. Each method returns a process exit code
/// (0 = success). Argument parsing lives in <see cref="Program"/>.
/// </summary>
internal static class Commands
{
    // -------------------------------------------------------------------------
    // build
    // -------------------------------------------------------------------------

    public static int Build(
        IReadOnlyList<string> inputs,
        string output,
        string? name,
        string? seedPath,
        string? prefix,
        bool mipmapStreaming,
        string? propertiesPath
    )
    {
        // When the caller supplies an explicit --name, that name is used verbatim as
        // the bundle identity. Otherwise we fall back to the output file name and
        // append the CAB hash so auto-named bundles never collide in Unity's registry.
        bool appendCabHash = name is null;
        name ??= Path.GetFileNameWithoutExtension(output);
        byte[] seed = seedPath is not null ? File.ReadAllBytes(seedPath) : LoadEmbeddedSeed();

        TexturePropertiesFile? properties = null;
        if (propertiesPath is not null)
        {
            try
            {
                properties = TexturePropertiesFile.Load(propertiesPath);
            }
            catch (Exception e) when (e is IOException or InvalidDataException)
            {
                Console.Error.WriteLine($"error: {e.Message}");
                return 1;
            }
        }

        var files = CollectInputFiles(inputs);
        if (files.Count == 0)
        {
            Console.Error.WriteLine("error: no .dds or .png files found in the given inputs");
            return 1;
        }

        // Container keys come from the file path, so we can sort and dedup-check
        // without decoding anything yet. Each texture is decoded lazily during the
        // build, keeping only one in memory at a time. Property globs match the
        // same input-relative path the container key is built from, minus the
        // --prefix (they describe input files, not bundle paths).
        var keyed = files
            .Select(f =>
            {
                string rel = Path.GetRelativePath(f.baseDir, f.file).Replace('\\', '/');
                return (file: f.file, rel, key: AddressableName(rel, prefix));
            })
            .OrderBy(x => x.key, StringComparer.Ordinal)
            .ToList();

        DuplicateKeyCheck(keyed.Select(x => (x.file, x.key)));

        var jobInputs = keyed
            .Select(x =>
            {
                var resolved =
                    properties?.Resolve(x.rel, mipmapStreaming)
                    ?? new TextureProperties { MipmapStreaming = mipmapStreaming };
                return new BundleBuilder.TextureInput
                {
                    AddressableName = x.key,
                    Decode = () =>
                    {
                        var (tex, skip) = DecodeFile(x.file);
                        // A 'cubemap' entry repacks the decoded 2D cross into a
                        // native cubemap before the per-texture flags are applied
                        // (so filter/wrap/colorSpace still land on the cubemap).
                        if (tex is not null && resolved.Cubemap)
                            (tex, skip) = CubemapPacker.PackCross(tex);
                        if (tex is not null)
                        {
                            tex.AddressableName = x.key;
                            tex.Readable = resolved.Readable;
                            tex.StreamingMipmaps = resolved.MipmapStreaming;
                            tex.StreamingMipmapsPriority = resolved.StreamingMipmapsPriority;
                            tex.Filter = resolved.Filter;
                            tex.WrapU = resolved.WrapU;
                            tex.WrapV = resolved.WrapV;
                            tex.WrapW = resolved.WrapW;
                            tex.Aniso = resolved.Aniso;
                            tex.MipBias = resolved.MipBias;
                            if (resolved.ColorSpace is int colorSpace)
                                tex.ColorSpace = colorSpace;
                        }
                        return (tex, skip);
                    },
                };
            })
            .ToList();

        // Resolve ran for every input above, so any glob still unmatched is a
        // likely typo worth surfacing.
        if (properties is not null)
            foreach (var pattern in properties.UnmatchedPatterns)
                Console.WriteLine($"warning: properties entry '{pattern}' matched no input file");

        var build = BundleBuilder.Build(seed, jobInputs, name, output, appendCabHash);

        foreach (var s in build.Skipped)
            Console.WriteLine($"skip  {Rel(s.SourcePath)}: {s.Reason} ({s.Detail})");

        if (build.Written == 0)
        {
            Console.Error.WriteLine("error: every input was skipped; nothing to bundle");
            return 1;
        }

        Console.WriteLine(
            $"wrote {output}: {build.Written} texture(s), {build.Skipped.Count} skipped, "
                + $"bundle name '{build.Identity}'"
        );
        return 0;
    }

    static void DuplicateKeyCheck(IEnumerable<(string file, string key)> keyed)
    {
        // KSPTextureLoader resolves textures by their full container key (the
        // lowercased, '/'-separated path), and the bundle is written full-path
        // only (m_PathFlags = 0, no basename tables). Only two inputs that map to
        // the same full key are actually indistinguishable, so warn on that.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, key) in keyed)
        {
            if (!seen.Add(key))
                Console.WriteLine(
                    $"warning: '{Rel(file)}' maps to the same container key '{key}' as an "
                        + "earlier texture; the loader cannot tell them apart"
                );
        }
    }

    /// <summary>
    /// Compute the AssetBundle container key for a file, mirroring the
    /// EditorExtensions bundler: the path relative to the input directory it was
    /// found under (<paramref name="rel"/>, already '/'-separated), prefixed with
    /// <paramref name="prefix"/> and lowercased. The extension is kept.
    /// </summary>
    static string AddressableName(string rel, string? prefix)
    {
        string combined = string.IsNullOrEmpty(prefix)
            ? rel
            : prefix.TrimEnd('/', '\\') + "/" + rel;
        return combined.Replace('\\', '/').ToLowerInvariant();
    }

    static (SourceTexture?, SkippedTexture?) DecodeFile(string file)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        return ext switch
        {
            ".dds" => DdsReader.Read(file),
            ".png" => PngReader.Read(file),
            _ => (
                null,
                new SkippedTexture
                {
                    SourcePath = file,
                    Reason = SkipReason.Invalid,
                    Detail = $"unsupported extension '{ext}'",
                }
            ),
        };
    }

    /// <summary>
    /// Gather the .dds/.png files from the inputs, pairing each with the base
    /// directory its container key is relative to: the input directory itself for a
    /// directory input (so nested files keep their sub-path), or the file's own
    /// directory for a bare file input (so its key is just the file name).
    /// </summary>
    static List<(string file, string baseDir)> CollectInputFiles(IEnumerable<string> inputs)
    {
        var files = new List<(string file, string baseDir)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string path, string baseDir)
        {
            string full = Path.GetFullPath(path);
            if (seen.Add(full))
                files.Add((full, baseDir));
        }

        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                string baseDir = Path.GetFullPath(input);
                foreach (var f in Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext is ".dds" or ".png")
                        Add(f, baseDir);
                }
            }
            else if (File.Exists(input))
            {
                Add(input, Path.GetDirectoryName(Path.GetFullPath(input))!);
            }
            else
            {
                throw new FileNotFoundException($"input not found: {input}");
            }
        }

        files.Sort((a, b) => string.Compare(a.file, b.file, StringComparison.OrdinalIgnoreCase));
        return files;
    }

    // -------------------------------------------------------------------------
    // make-seed
    // -------------------------------------------------------------------------

    public static int MakeSeed(string source, string output)
    {
        byte[] sourceBundle = File.ReadAllBytes(source);

        // A 1x1 opaque-white placeholder so both type trees are referenced and kept.
        var dummy = new SourceTexture
        {
            Name = "ksptextureloader_seed_placeholder",
            Width = 1,
            Height = 1,
            MipCount = 1,
            Format = UnityTextureFormat.RGBA32,
            ColorSpace = 1,
            Data = [0xFF, 0xFF, 0xFF, 0xFF],
            SourcePath = source,
        };

        BundleBuilder.Build(sourceBundle, [dummy], "ksptextureloader_seed", output);
        Console.WriteLine($"wrote seed {output} from {source}");
        return 0;
    }

    // -------------------------------------------------------------------------
    // extract
    // -------------------------------------------------------------------------

    /// <summary>
    /// Write every Texture2D in <paramref name="bundlePath"/> out under
    /// <paramref name="outDir"/>. A texture whose container key ends in <c>.png</c>
    /// (i.e. a PNG packed by <c>build</c>) is written back as a PNG; every other
    /// texture is written as a DDS, its extension swapped to <c>.dds</c>. By default
    /// the AssetBundle container paths are recreated as a directory tree (the inverse
    /// of <c>build --prefix</c>); <paramref name="flat"/> writes each texture as
    /// <c>&lt;name&gt;.&lt;ext&gt;</c> instead.
    /// </summary>
    public static int Extract(string bundlePath, string outDir, bool flat)
    {
        var am = new AssetsManager();
        var bunInst = am.LoadBundleFile(bundlePath, true);
        var bundle = bunInst.file;

        var dirs = bundle.BlockAndDirInfo.DirectoryInfos;
        int serIdx = -1,
            resIdx = -1;
        for (int i = 0; i < dirs.Count; i++)
        {
            if ((dirs[i].Flags & 0x4) != 0)
                serIdx = i;
            else
                resIdx = i;
        }
        if (serIdx < 0)
        {
            Console.Error.WriteLine("error: no serialized file in bundle");
            return 1;
        }
        long resOffset = resIdx >= 0 ? dirs[resIdx].Offset : 0;

        var afileInst = am.LoadAssetsFileFromBundle(bunInst, serIdx, false);
        var afile = afileInst.file;

        var pathIdToKey = new Dictionary<long, string>();
        foreach (var info in afile.GetAssetsOfType(AssetClassID.AssetBundle))
        {
            var b = am.GetBaseField(afileInst, info);
            foreach (var pair in b["m_Container"]["Array"])
                pathIdToKey[pair["second"]["asset"]["m_PathID"].AsLong] = pair["first"].AsString;
        }

        int written = 0,
            skipped = 0;
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var info in afile.GetAssetsOfType(AssetClassID.Texture2D))
        {
            var b = am.GetBaseField(afileInst, info);
            string name = b["m_Name"].AsString;
            int w = b["m_Width"].AsInt;
            int h = b["m_Height"].AsInt;
            int mips = Math.Max(1, b["m_MipCount"].AsInt);
            int fmt = b["m_TextureFormat"].AsInt;
            long size = b["m_StreamData"]["size"].AsUInt;
            long off = b["m_StreamData"]["offset"].AsUInt;

            var format = (UnityTextureFormat)fmt;

            // The container key keeps the source file's extension, so a bundle built
            // from a PNG round-trips back to a PNG. Anything else is written as DDS,
            // its extension swapped to .dds.
            pathIdToKey.TryGetValue(info.PathId, out var key);
            bool wantPng =
                key is not null
                && key.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                && PngWriter.CanWrite(format);

            if (!wantPng && !DdsWriter.CanWrite(format))
            {
                Console.WriteLine($"skip  {name}: texture format {fmt} cannot be written as DDS");
                skipped++;
                continue;
            }

            byte[] pixels;
            if (size > 0)
            {
                if (resIdx < 0)
                {
                    Console.WriteLine($"skip  {name}: streamed but no resS in bundle");
                    skipped++;
                    continue;
                }
                bundle.DataReader.Position = resOffset + off;
                pixels = bundle.DataReader.ReadBytes((int)size);
            }
            else
            {
                pixels = b["image data"].AsByteArray;
            }

            if (pixels.Length == 0)
            {
                Console.WriteLine($"skip  {name}: no pixel data");
                skipped++;
                continue;
            }

            string ext = wantPng ? ".png" : ".dds";

            string rel;
            if (!flat && key is not null)
                rel = Path.ChangeExtension(key, ext);
            else
                rel = Sanitize(name) + ext;

            string outPath = Path.GetFullPath(
                Path.Combine(outDir, rel.Replace('/', Path.DirectorySeparatorChar))
            );
            // Guard against odd container keys escaping the output directory.
            if (!outPath.StartsWith(Path.GetFullPath(outDir), StringComparison.OrdinalIgnoreCase))
                outPath = Path.Combine(Path.GetFullPath(outDir), Sanitize(name) + ext);
            if (!usedPaths.Add(outPath))
                outPath = MakeUnique(outPath, usedPaths);

            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            byte[] outBytes = wantPng
                ? PngWriter.Write(format, w, h, pixels)
                : DdsWriter.Write(format, w, h, mips, pixels);
            File.WriteAllBytes(outPath, outBytes);
            written++;
        }

        Console.WriteLine($"extracted {written} texture(s) to {outDir}, {skipped} skipped");
        return written > 0 ? 0 : 1;
    }

    static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Length == 0 ? "texture" : name;
    }

    static string MakeUnique(string path, HashSet<string> used)
    {
        string dir = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (used.Add(candidate))
                return candidate;
        }
    }

    static byte[] LoadEmbeddedSeed()
    {
        var asm = Assembly.GetExecutingAssembly();
        string? resource = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("seed.bundle", StringComparison.OrdinalIgnoreCase));
        if (resource is null)
            throw new InvalidOperationException(
                "no embedded seed bundle; pass --seed <seed.bundle> or run 'make-seed' first"
            );
        using var stream = asm.GetManifestResourceStream(resource)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    static string Rel(string path)
    {
        try
        {
            return Path.GetRelativePath(Directory.GetCurrentDirectory(), path);
        }
        catch
        {
            return path;
        }
    }

    // -------------------------------------------------------------------------
    // dev-only inspection commands
    // -------------------------------------------------------------------------

    // Synthesises Kopernicus palette DDS files (4bpp and 8bpp) covering each output
    // format, runs them through DdsReader, then decodes the produced bytes with the
    // exact math KSPTextureLoader uses and checks every pixel reproduces the
    // original palette colour. Verifies detection, nibble order, endianness and the
    // format choice end to end.
    public static int PaletteTest()
    {
        int fails = 0;

        // Colours that are exact in 5/6/5 and opaque -> expect RGB565.
        var opaque565 = BuildColors(
            24,
            i => ((byte)Exp5(i % 32), (byte)Exp6((i * 5) % 64), (byte)Exp5((i * 3) % 32), (byte)255)
        );
        // Nibble-replicated colours with varying alpha -> expect RGBA4444.
        var alpha4444 = BuildColors(
            16,
            i =>
                (
                    (byte)Exp4(i % 16),
                    (byte)Exp4((i + 5) % 16),
                    (byte)Exp4((i + 9) % 16),
                    (byte)Exp4((i + 1) % 16)
                )
        );
        // Contains a colour exact in neither 565 nor 4444 -> expect RGBA32.
        var mixed = BuildColors(
            10,
            i =>
                i == 3
                    ? ((byte)1, (byte)2, (byte)3, (byte)4)
                    : ((byte)Exp4(i % 16), (byte)Exp4(i % 16), (byte)Exp4(i % 16), (byte)255)
        );

        fails += PaletteCase("8bpp/RGB565", false, opaque565, UnityTextureFormat.RGB565);
        fails += PaletteCase("8bpp/RGBA4444", false, alpha4444, UnityTextureFormat.RGBA4444);
        fails += PaletteCase("8bpp/RGBA32", false, mixed, UnityTextureFormat.RGBA32);
        fails += PaletteCase("4bpp/RGB565", true, opaque565, UnityTextureFormat.RGB565);
        fails += PaletteCase("4bpp/RGBA4444", true, alpha4444, UnityTextureFormat.RGBA4444);

        Console.WriteLine(fails == 0 ? "PALETTE TESTS PASSED" : $"PALETTE TESTS FAILED ({fails})");
        return fails == 0 ? 0 : 2;
    }

    // Builds synthetic 4x3 cross textures whose every element (pixel for
    // uncompressed formats, 4x4 block for compressed ones) is tagged with its grid
    // cell id, runs them through CubemapPacker, and asserts each of the six output
    // faces came from the cell ConvertTexture2dToCubemap reads for that face. Locks
    // down the face ordering, orientation and the block-aligned copy math.
    public static int CubemapTest()
    {
        // Expected cell (col, row) each cube face is taken from, in +X,-X,+Y,-Y,+Z,-Z
        // order. Mirrors TextureUtils.ConvertTexture2dToCubemap.
        var expected = new (int col, int row)[]
        {
            (3, 1), // +X
            (1, 1), // -X
            (2, 0), // +Y
            (2, 2), // -Y
            (2, 1), // +Z
            (0, 1), // -Z
        };

        int fails = 0;
        fails += CubemapCase("RGBA32 face=2", UnityTextureFormat.RGBA32, faceSize: 2, expected);
        fails += CubemapCase("DXT5 face=4", UnityTextureFormat.DXT5, faceSize: 4, expected);
        fails += CubemapCase("DXT1 face=8", UnityTextureFormat.DXT1, faceSize: 8, expected);

        // A face size that isn't block-aligned must be rejected, not mis-copied.
        var bad = MakeCross(UnityTextureFormat.DXT5, faceSize: 2);
        var (badTex, badSkip) = CubemapPacker.PackCross(bad);
        if (badTex is not null || badSkip is null)
        {
            Console.WriteLine("  block-align guard: FAIL expected a skip for a 2px DXT5 face");
            fails++;
        }
        else
        {
            Console.WriteLine($"  block-align guard: ok ({badSkip.Detail})");
        }

        Console.WriteLine(fails == 0 ? "CUBEMAP TESTS PASSED" : $"CUBEMAP TESTS FAILED ({fails})");
        return fails == 0 ? 0 : 2;
    }

    static int CubemapCase(
        string label,
        UnityTextureFormat format,
        int faceSize,
        (int col, int row)[] expected
    )
    {
        var src = MakeCross(format, faceSize);
        var (cube, skip) = CubemapPacker.PackCross(src);
        if (cube is null)
        {
            Console.WriteLine($"  {label}: FAIL packing skipped ({skip?.Detail})");
            return 1;
        }
        if (cube.Kind != TextureKind.Cubemap || cube.Width != faceSize || cube.Height != faceSize)
        {
            Console.WriteLine(
                $"  {label}: FAIL got {cube.Kind} {cube.Width}x{cube.Height}, "
                    + $"want Cubemap {faceSize}x{faceSize}"
            );
            return 1;
        }

        int elemBytes = TextureFormatInfo.BlockOrPixelSize(format);
        long faceBytes = TextureFormatInfo.MipSize(format, faceSize, faceSize);
        if (cube.Data.Length != faceBytes * 6)
        {
            Console.WriteLine($"  {label}: FAIL data {cube.Data.Length} != {faceBytes * 6}");
            return 1;
        }

        // Every element of face i must carry the id of its expected source cell.
        for (int face = 0; face < 6; face++)
        {
            byte want = (byte)(expected[face].row * 4 + expected[face].col);
            long baseOff = faceBytes * face;
            for (long e = 0; e < faceBytes; e += elemBytes)
            {
                if (cube.Data[baseOff + e] != want)
                {
                    Console.WriteLine(
                        $"  {label}: FAIL face {face} element at {e} = {cube.Data[baseOff + e]}, "
                            + $"want cell {want}"
                    );
                    return 1;
                }
            }
        }

        Console.WriteLine($"  {label}: ok (6 faces, {faceBytes} B each)");
        return 0;
    }

    // A 4x3 cross where every element in grid cell (col, row) is filled with the
    // byte value row*4+col, so the packed faces can be checked against their cells.
    static SourceTexture MakeCross(UnityTextureFormat format, int faceSize)
    {
        int width = faceSize * 4;
        int height = faceSize * 3;
        int ew = TextureFormatInfo.BlockWidth(format);
        int eh = TextureFormatInfo.BlockHeight(format);
        int elemBytes = TextureFormatInfo.BlockOrPixelSize(format);
        int colsElem = width / ew;
        int rowsElem = height / eh;

        var data = new byte[(long)colsElem * rowsElem * elemBytes];
        for (int re = 0; re < rowsElem; re++)
        for (int ce = 0; ce < colsElem; ce++)
        {
            int col = ce * ew / faceSize;
            int row = re * eh / faceSize;
            byte id = (byte)(row * 4 + col);
            int off = (re * colsElem + ce) * elemBytes;
            Array.Fill(data, id, off, elemBytes);
        }

        return new SourceTexture
        {
            Name = "cross",
            Width = width,
            Height = height,
            MipCount = 1,
            Format = format,
            ColorSpace = 1,
            Data = data,
            SourcePath = $"synthetic-{format}-{faceSize}.dds",
        };
    }

    static int Exp4(int n) => n * 17;

    static int Exp5(int n) => (n << 3) | (n >> 2);

    static int Exp6(int n) => (n << 2) | (n >> 4);

    static (byte, byte, byte, byte)[] BuildColors(int count, Func<int, (byte, byte, byte, byte)> f)
    {
        var c = new (byte, byte, byte, byte)[count];
        for (int i = 0; i < count; i++)
            c[i] = f(i);
        return c;
    }

    static int PaletteCase(
        string label,
        bool fourBit,
        (byte r, byte g, byte b, byte a)[] colors,
        UnityTextureFormat expectedFormat
    )
    {
        int entries = fourBit ? 16 : 256;
        int used = Math.Min(colors.Length, entries);

        // 64 pixels, each cycling through the used palette entries.
        int width = 8,
            height = 8;
        int pixelCount = width * height;
        var pixelIndex = new int[pixelCount];
        for (int p = 0; p < pixelCount; p++)
            pixelIndex[p] = p % used;

        var palette = new byte[entries * 4];
        for (int i = 0; i < used; i++)
        {
            palette[i * 4 + 0] = colors[i].r;
            palette[i * 4 + 1] = colors[i].g;
            palette[i * 4 + 2] = colors[i].b;
            palette[i * 4 + 3] = colors[i].a;
        }

        byte[] indices;
        if (fourBit)
        {
            indices = new byte[pixelCount / 2];
            for (int k = 0; k < indices.Length; k++)
                indices[k] = (byte)(
                    (pixelIndex[2 * k] & 0xF) | ((pixelIndex[2 * k + 1] & 0xF) << 4)
                );
        }
        else
        {
            indices = new byte[pixelCount];
            for (int p = 0; p < pixelCount; p++)
                indices[p] = (byte)pixelIndex[p];
        }

        byte[] dds = BuildPaletteDds(width, height, fourBit, palette, indices);
        string tmp = Path.Combine(Path.GetTempPath(), $"ksptb_pal_{Guid.NewGuid():N}.dds");
        File.WriteAllBytes(tmp, dds);

        try
        {
            var (tex, skip) = DdsReader.Read(tmp);
            if (tex is null)
            {
                Console.WriteLine($"  {label}: FAIL decode skipped ({skip?.Detail})");
                return 1;
            }
            if (tex.Format != expectedFormat)
            {
                Console.WriteLine(
                    $"  {label}: FAIL format {tex.Format}, expected {expectedFormat}"
                );
                return 1;
            }

            // Decode the produced bytes the way the loader does and compare colours.
            int bad = 0;
            for (int p = 0; p < pixelCount; p++)
            {
                var (r, g, b, a) = colors[pixelIndex[p]];
                var (dr, dg, db, da) = DecodePixel(tex.Format, tex.Data, p);
                bool opaqueExpected = tex.Format == UnityTextureFormat.RGB565;
                if (
                    dr != r
                    || dg != g
                    || db != b
                    || (!opaqueExpected && da != a)
                    || (opaqueExpected && da != 255)
                )
                    bad++;
            }
            if (bad != 0)
            {
                Console.WriteLine($"  {label}: FAIL {bad}/{pixelCount} pixels differ");
                return 1;
            }
            Console.WriteLine($"  {label}: ok ({tex.Format}, {pixelCount} px)");
            return 0;
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    static (byte r, byte g, byte b, byte a) DecodePixel(UnityTextureFormat fmt, byte[] data, int p)
    {
        switch (fmt)
        {
            case UnityTextureFormat.RGBA32:
                return (data[p * 4], data[p * 4 + 1], data[p * 4 + 2], data[p * 4 + 3]);
            case UnityTextureFormat.RGBA4444:
            {
                ushort u = (ushort)(data[p * 2] | (data[p * 2 + 1] << 8));
                return (
                    (byte)Exp4((u >> 12) & 0xF),
                    (byte)Exp4((u >> 8) & 0xF),
                    (byte)Exp4((u >> 4) & 0xF),
                    (byte)Exp4(u & 0xF)
                );
            }
            case UnityTextureFormat.RGB565:
            {
                ushort u = (ushort)(data[p * 2] | (data[p * 2 + 1] << 8));
                return (
                    (byte)Exp5((u >> 11) & 0x1F),
                    (byte)Exp6((u >> 5) & 0x3F),
                    (byte)Exp5(u & 0x1F),
                    (byte)255
                );
            }
            default:
                throw new InvalidOperationException($"unexpected format {fmt}");
        }
    }

    static byte[] BuildPaletteDds(
        int width,
        int height,
        bool fourBit,
        byte[] palette,
        byte[] indices
    )
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0x20534444u); // "DDS "
        w.Write(124); // dwSize
        w.Write(0x1007); // CAPS | HEIGHT | WIDTH | PIXELFORMAT
        w.Write(height);
        w.Write(width);
        w.Write(0); // pitch
        w.Write(0); // depth
        w.Write(0); // mipCount
        for (int i = 0; i < 11; i++)
            w.Write(0); // reserved1
        // DDS_PIXELFORMAT
        w.Write(32); // dwSize
        w.Write(0); // dwFlags (none -> palette)
        w.Write(0); // fourCC
        w.Write(fourBit ? 4 : 8); // rgbBitCount
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(0); // masks
        // caps
        w.Write(0x1000); // TEXTURE
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(0);
        w.Write(palette);
        w.Write(indices);
        return ms.ToArray();
    }

    static string ProbeSettings(AssetTypeValueField b)
    {
        var ts = b["m_TextureSettings"];
        if (ts.IsDummy)
            return "";
        string extra = b["m_ColorSpace"].IsDummy ? "" : $" colorSpace={b["m_ColorSpace"].AsInt}";
        if (!b["m_StreamingMipmapsPriority"].IsDummy)
            extra += $" streamPrio={b["m_StreamingMipmapsPriority"].AsInt}";
        return $" filter={ts["m_FilterMode"].AsInt} aniso={ts["m_Aniso"].AsInt} "
            + $"bias={ts["m_MipBias"].AsFloat} "
            + $"wrap={ts["m_WrapU"].AsInt}/{ts["m_WrapV"].AsInt}/{ts["m_WrapW"].AsInt}{extra}";
    }

    public static int Probe(string path)
    {
        var am = new AssetsManager();
        var bunInst = am.LoadBundleFile(path, true);
        var bundle = bunInst.file;

        Console.WriteLine($"Bundle: {path}");
        foreach (var di in bundle.BlockAndDirInfo.DirectoryInfos)
            Console.WriteLine($"  {di.Name}  flags=0x{di.Flags:X}");

        for (int i = 0; i < bundle.BlockAndDirInfo.DirectoryInfos.Count; i++)
        {
            var di = bundle.BlockAndDirInfo.DirectoryInfos[i];
            if ((di.Flags & 0x4) == 0)
                continue;
            var afile = am.LoadAssetsFileFromBundle(bunInst, i, false).file;
            Console.WriteLine($"--- assets file [{i}] {di.Name} ---");
            Console.WriteLine($"  TypeTreeEnabled={afile.Metadata.TypeTreeEnabled}");
            Console.WriteLine($"  UnityVersion={afile.Metadata.UnityVersion}");

            var afileInst = am.LoadAssetsFileFromBundle(bunInst, i, false);
            foreach (var info in afile.GetAssetsOfType(AssetClassID.Texture2D))
            {
                var b = am.GetBaseField(afileInst, info);
                string spath = b["m_StreamData"]["path"].AsString;
                long size = b["m_StreamData"]["size"].AsUInt;
                Console.WriteLine(
                    $"  TEX pathId={info.PathId} name='{b["m_Name"].AsString}' "
                        + $"{b["m_Width"].AsInt}x{b["m_Height"].AsInt} fmt={b["m_TextureFormat"].AsInt} "
                        + $"mips={b["m_MipCount"].AsInt} readable={b["m_IsReadable"].AsBool} "
                        + $"streamMips={b["m_StreamingMipmaps"].AsBool}{ProbeSettings(b)} "
                        + $"streamSize={size} streamPath='{spath}'"
                );
            }
            foreach (
                var (classId, label) in new[]
                {
                    (89, "CUBE"),
                    (117, "TEX3D"),
                    (187, "TEX2DARR"),
                    (188, "CUBEARR"),
                }
            )
            {
                foreach (var info in afile.GetAssetsOfType((AssetClassID)classId))
                {
                    var b = am.GetBaseField(afileInst, info);
                    int Opt(string field) => b[field].IsDummy ? -1 : b[field].AsInt;
                    int fmt = b["m_TextureFormat"].IsDummy
                        ? Opt("m_Format")
                        : b["m_TextureFormat"].AsInt;
                    Console.WriteLine(
                        $"  {label} pathId={info.PathId} name='{b["m_Name"].AsString}' "
                            + $"{Opt("m_Width")}x{Opt("m_Height")} depth={Opt("m_Depth")} "
                            + $"cubes={Opt("m_CubemapCount")} fmt={fmt} mips={Opt("m_MipCount")} "
                            + $"readable={b["m_IsReadable"].AsBool}{ProbeSettings(b)} "
                            + $"streamSize={b["m_StreamData"]["size"].AsUInt}"
                    );
                }
            }

            foreach (var info in afile.GetAssetsOfType(AssetClassID.AssetBundle))
            {
                var b = am.GetBaseField(afileInst, info);
                Console.WriteLine(
                    $"  AB m_Name='{b["m_Name"].AsString}' m_AssetBundleName='{b["m_AssetBundleName"].AsString}' m_PathFlags={b["m_PathFlags"].AsInt}"
                );
                foreach (var pair in b["m_Container"]["Array"])
                    Console.WriteLine(
                        $"    container '{pair["first"].AsString}' -> pathId {pair["second"]["asset"]["m_PathID"].AsLong}"
                    );
            }
        }
        return 0;
    }

    // Re-reads the bundle and asserts each Texture2D's streamed resS bytes are
    // byte-exact with the original decode and the field header is correct. Sources
    // are keyed by container path (addressable name) so textures that share a file
    // name across sub-directories are still matched to the right source. The resS
    // is read in per-texture slices so bundles larger than 2 GB validate too.
    public static int Validate(string bundlePath, IReadOnlyList<string> inputs, string? prefix)
    {
        var byKey = new Dictionary<string, SourceTexture>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, SourceTexture>(StringComparer.OrdinalIgnoreCase);
        foreach (var (f, baseDir) in CollectInputFiles(inputs))
        {
            var (tex, _) = DecodeFile(f);
            if (tex is null)
                continue;
            byKey[AddressableName(Path.GetRelativePath(baseDir, f).Replace('\\', '/'), prefix)] =
                tex;
            byName[tex.Name] = tex;
        }

        var am = new AssetsManager();
        var bunInst = am.LoadBundleFile(bundlePath, true);
        var bundle = bunInst.file;

        int serIdx = -1,
            resIdx = -1;
        var dirs = bundle.BlockAndDirInfo.DirectoryInfos;
        for (int i = 0; i < dirs.Count; i++)
        {
            if ((dirs[i].Flags & 0x4) != 0)
                serIdx = i;
            else
                resIdx = i;
        }

        var resDi = dirs[resIdx];
        long resLen = resDi.DecompressedSize;

        var afileInst = am.LoadAssetsFileFromBundle(bunInst, serIdx, false);
        var afile = afileInst.file;

        // pathId -> container key, so each texture maps to its specific source.
        var pathIdToKey = new Dictionary<long, string>();
        foreach (var info in afile.GetAssetsOfType(AssetClassID.AssetBundle))
        {
            var b = am.GetBaseField(afileInst, info);
            foreach (var pair in b["m_Container"]["Array"])
                pathIdToKey[pair["second"]["asset"]["m_PathID"].AsLong] = pair["first"].AsString;
        }

        int textures = 0,
            failures = 0;

        foreach (var info in afile.GetAssetsOfType(AssetClassID.Texture2D))
        {
            textures++;
            var b = am.GetBaseField(afileInst, info);
            string nm = b["m_Name"].AsString;
            int w = b["m_Width"].AsInt;
            int h = b["m_Height"].AsInt;
            int fmt = b["m_TextureFormat"].AsInt;
            long off = b["m_StreamData"]["offset"].AsUInt;
            long size = b["m_StreamData"]["size"].AsUInt;
            int inlineLen = b["image data"].AsByteArray.Length;

            // Prefer the container key (unique); fall back to name.
            SourceTexture? src = null;
            if (
                pathIdToKey.TryGetValue(info.PathId, out var key)
                && byKey.TryGetValue(key, out var s)
            )
                src = s;
            else if (byName.TryGetValue(nm, out var s2))
                src = s2;

            string status = "ok";
            if (src is null)
                status = "NO SOURCE";
            else if (inlineLen != 0)
                status = $"FAIL inline data present ({inlineLen})";
            else if (w != src.Width || h != src.Height || fmt != (int)src.Format)
                status =
                    $"FAIL header got {w}x{h}/{fmt} want {src.Width}x{src.Height}/{(int)src.Format}";
            else if (size != src.Data.Length)
                status = $"FAIL stream size {size} != {src.Data.Length}";
            else if (off < 0 || off + size > resLen)
                status = "FAIL stream range out of resS";
            else
            {
                bundle.DataReader.Position = resDi.Offset + off;
                byte[] slice = bundle.DataReader.ReadBytes((int)size);
                if (!slice.AsSpan().SequenceEqual(src.Data))
                    status = "FAIL resS bytes differ from source";
            }

            if (status != "ok")
            {
                failures++;
                Console.WriteLine(
                    $"  tex {nm} {w}x{h} fmt={fmt} off={off} size={size} -> {status}"
                );
            }
        }

        Console.WriteLine($"textures={textures} failures={failures} resSlen={resLen}");
        return failures == 0 && textures > 0 ? 0 : 2;
    }
}
