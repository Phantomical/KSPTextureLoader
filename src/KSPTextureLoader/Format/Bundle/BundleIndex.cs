using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using KSPTextureLoader.Utils;
using UnityEngine;

namespace KSPTextureLoader.Format.Bundle;

/// <summary>
/// A cached, parse-once index of the texture content of a single mounted asset
/// bundle. Building it parses the UnityFS directory and serialized file(s) once
/// (object table, type trees and the <c>AssetBundle.m_Container</c> name map);
/// after that each <see cref="LoadTextureAsync"/> is a dictionary lookup plus a
/// single <c>AsyncReadManager</c> read of just that texture's pixels.
/// </summary>
///
/// <remarks>
/// The bundle MUST be loaded (mounted) by Unity when <see cref="BuildAsync"/>
/// runs, since all content is read through <c>archive:/</c> virtual paths. The
/// index itself holds only managed metadata (no pixel data and no native
/// buffers), so it needs no disposal and lives as long as the bundle handle.
/// Targets Unity 2019.4 (serialized file format 17); requires type trees.
/// </remarks>
internal sealed class BundleIndex
{
    /// <summary>The Unity runtime class ID of the AssetBundle object.</summary>
    const int AssetBundleClassId = 142;

    /// <param name="Info">The texture's parsed metadata.</param>
    /// <param name="SerializedArchivePath">archive:/ path of the serialized file the texture lives in.</param>
    /// <param name="Cab">The <c>CAB-&lt;hash&gt;</c> base name, for resolving the .resS path.</param>
    readonly record struct Entry(Texture2DInfo Info, string SerializedArchivePath, string Cab);

    readonly Dictionary<string, Entry> entries;
    readonly List<string> names;

    public IReadOnlyList<string> TextureNames => names;

    BundleIndex(Dictionary<string, Entry> entries, List<string> names)
    {
        this.entries = entries;
        this.names = names;
    }

    public bool Contains(string assetName) =>
        NormalizeName(assetName) is string key && entries.ContainsKey(key);

    /// <summary>The per-file result of an indexing job, merged into the bundle index.</summary>
    sealed class PartialIndex
    {
        public readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Names = [];
    }

    /// <summary>
    /// How much of the serialized file we read up-front to recover its metadata.
    /// The metadata (header + type trees + object table) almost always fits; if
    /// not we re-read exactly the metadata region.
    /// </summary>
    const long MetadataPrefix = 256 * 1024;

    /// <summary>
    /// How much of each <c>Texture2D</c> object we read to recover its field
    /// header. Large inline textures only ever need this prefix (never the
    /// pixels); streamed and tiny textures are smaller than this and read whole.
    /// </summary>
    const long TextureRegionCap = 1024;

    /// <summary>
    /// Build the index for the bundle at <paramref name="bundleDiskPath"/>, which
    /// must currently be mounted by Unity.
    /// </summary>
    public static Task<BundleIndex> BuildAsync(string bundleDiskPath)
    {
        return Task.Run(async () =>
        {
            var nodes = await BundleDirectory.ReadAsync(bundleDiskPath);

            // Index every serialized file in parallel.
            var jobs = new List<Task<PartialIndex>>();
            foreach (var node in nodes)
            {
                if (IsResource(node.Path))
                    continue;

                string archivePath = ArchivePath(node.Path, node.Path);
                string cab = StripExtension(LastComponent(node.Path));
                jobs.Add(ReadAndIndexAsync(archivePath, cab, node.Size));
            }

            var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            foreach (var job in jobs)
            {
                PartialIndex partial;
                try
                {
                    partial = await job;
                }
                catch
                {
                    // Not a serialized file we can parse; skip it.
                    continue;
                }

                if (partial is null)
                    continue;

                names.AddRange(partial.Names);
                foreach (var pair in partial.Entries)
                    entries[pair.Key] = pair.Value;
            }

            return new BundleIndex(entries, names);
        });
    }

    /// <summary>
    /// Index one serialized file: read its metadata, then batch-read just the
    /// field headers of its <c>Texture2D</c> objects (never their inline pixels)
    /// plus the <c>AssetBundle</c> object's container map, in a single dispatch.
    /// Returns null if the file isn't a serialized file we can parse.
    /// </summary>
    static async Task<PartialIndex> ReadAndIndexAsync(string archivePath, string cab, long fileSize)
    {
        var file = await ReadMetadataAsync(archivePath, fileSize);
        if (file is null)
            return null;

        // Collect the AssetBundle object and every Texture2D object.
        var textureObjs = new List<SerializedObject>();
        SerializedObject bundleObj = default;
        bool hasBundle = false;
        foreach (var obj in file.Objects)
        {
            if (obj.ClassId == Texture2DInfo.ClassId)
                textureObjs.Add(obj);
            else if (!hasBundle && obj.ClassId == AssetBundleClassId)
            {
                bundleObj = obj;
                hasBundle = true;
            }
        }

        var result = new PartialIndex();
        if (textureObjs.Count == 0)
            return result;

        // One capped header read per texture, plus the full AssetBundle object
        // (for m_Container), batched into a single AsyncReadManager dispatch.
        var ranges = new List<ReadRange>(textureObjs.Count + 1);
        foreach (var obj in textureObjs)
        {
            ranges.Add(
                new ReadRange(
                    file.ObjectDataOffset(obj),
                    Math.Min((long)obj.ByteSize, TextureRegionCap)
                )
            );
        }

        int bundleRange = -1;
        if (hasBundle)
        {
            bundleRange = ranges.Count;
            ranges.Add(new ReadRange(file.ObjectDataOffset(bundleObj), bundleObj.ByteSize));
        }

        var entriesByPathId = new Dictionary<long, Entry>(textureObjs.Count);

        using var regions = await VfsReader.ReadRangesAsync(archivePath, ranges);

        // Parse each texture's header; collect any that need their full object.
        List<int> incomplete = null;
        for (int i = 0; i < textureObjs.Count; ++i)
        {
            var obj = textureObjs[i];
            bool complete = obj.ByteSize <= TextureRegionCap;
            try
            {
                var info = Texture2DInfo.Extract(file, obj, regions.ReaderFor(i), complete);
                AddTexture(result, entriesByPathId, obj, info, archivePath, cab);
            }
            catch (IncompleteObjectRegionException)
            {
                (incomplete ??= []).Add(i);
            }
            catch
            {
                // Couldn't parse this texture; skip it.
            }
        }

        // Rare: re-read the full object for textures whose header didn't fit.
        if (incomplete is not null)
        {
            foreach (var i in incomplete)
            {
                var obj = textureObjs[i];
                try
                {
                    using var full = await VfsReader.ReadAsync(
                        archivePath,
                        file.ObjectDataOffset(obj),
                        obj.ByteSize
                    );
                    var info = ExtractComplete(file, obj, full);
                    AddTexture(result, entriesByPathId, obj, info, archivePath, cab);
                }
                catch
                {
                    // Couldn't parse this texture; skip it.
                }
            }
        }

        // Prefer the AssetBundle's container map (asset path -> object), which is
        // the same name->object index Unity uses internally.
        if (hasBundle)
        {
            try
            {
                var container = file.ReadObjectFrom(bundleObj, regions.ReaderFor(bundleRange))
                    .Field("m_Container");
                IndexContainer(container, entriesByPathId, result.Entries);
            }
            catch
            {
                // No usable container; the name-based keys still stand.
            }
        }

        return result;
    }

    /// <summary>
    /// Read and parse just the serialized file's metadata (header, type trees and
    /// object table). Returns null for files we can't parse (e.g. format 22+ or
    /// not a serialized file). The returned <see cref="SerializedFile"/>'s
    /// internal reader points into a freed buffer, so callers must only use
    /// <see cref="SerializedFile.ReadObjectFrom"/> and the parsed tables, never
    /// <c>ReadObject</c>.
    /// </summary>
    static async Task<SerializedFile> ReadMetadataAsync(string archivePath, long fileSize)
    {
        long prefixLength = Math.Min(fileSize, MetadataPrefix);

        long dataOffset;
        using (var prefix = await VfsReader.ReadAsync(archivePath, 0, prefixLength))
        {
            dataOffset = PeekDataOffset(prefix);
            if (dataOffset < 0 || dataOffset > fileSize)
                return null;

            if (dataOffset <= prefix.Length)
                return ParseMetadata(prefix);
        }

        // The metadata is larger than our prefix; read exactly the metadata region.
        using var meta = await VfsReader.ReadAsync(archivePath, 0, dataOffset);
        return ParseMetadata(meta);
    }

    /// <summary>
    /// Peek the serialized file's <c>dataOffset</c> (where object data begins)
    /// from its big-endian header. Returns -1 for unsupported/unrecognized files.
    /// </summary>
    static unsafe long PeekDataOffset(LargeNativeArray<byte> buffer)
    {
        if (buffer.Length < 16)
            return -1;

        var reader = new EndianBinaryReader(buffer.GetUnsafePtr(), buffer.Length, bigEndian: true);
        reader.Position = 8;
        uint version = reader.ReadUInt32();
        if (version >= 22)
            return -1; // matches SerializedFile's rejection of 22+

        return reader.ReadUInt32(); // dataOffset
    }

    static unsafe SerializedFile ParseMetadata(LargeNativeArray<byte> buffer) =>
        SerializedFile.Parse(new EndianBinaryReader(buffer.GetUnsafePtr(), buffer.Length));

    static unsafe Texture2DInfo ExtractComplete(
        SerializedFile file,
        SerializedObject obj,
        LargeNativeArray<byte> full
    ) =>
        Texture2DInfo.Extract(
            file,
            obj,
            new EndianBinaryReader(full.GetUnsafePtr(), full.Length),
            regionIsComplete: true
        );

    static void AddTexture(
        PartialIndex result,
        Dictionary<long, Entry> entriesByPathId,
        SerializedObject obj,
        Texture2DInfo info,
        string archivePath,
        string cab
    )
    {
        var entry = new Entry(info, archivePath, cab);
        entriesByPathId[obj.PathId] = entry;
        result.Names.Add(info.Name);

        // Always index by the texture's own name as a fallback.
        if (NormalizeName(info.Name) is string nameKey)
            result.Entries[nameKey] = entry;
    }

    static void IndexContainer(
        TypeTreeValue container,
        Dictionary<long, Entry> entriesByPathId,
        Dictionary<string, Entry> entries
    )
    {
        if (container?.Elements is null)
            return;

        foreach (var pair in container.Elements)
        {
            string assetName = pair.Field("first")?.AsString();
            var asset = pair.Field("second")?.Field("asset");
            if (assetName is null || asset is null)
                continue;

            // Only same-file references (m_FileID == 0) resolve within this bundle.
            if ((asset.Field("m_FileID")?.AsInt() ?? 0) != 0)
                continue;

            long pathId = asset.Field("m_PathID")?.AsInt() ?? 0;
            if (
                entriesByPathId.TryGetValue(pathId, out var entry)
                && NormalizeName(assetName) is string key
            )
                entries[key] = entry;
        }
    }

    /// <summary>
    /// Load the named texture's pixels through <c>archive:/</c> and wrap them in a
    /// <see cref="CPUTexture2D"/>. No re-parsing occurs; only the pixel bytes are
    /// read.
    /// </summary>
    public async Task<CPUTexture2D> LoadTextureAsync(string assetName)
    {
        if (NormalizeName(assetName) is not string key || !entries.TryGetValue(key, out var entry))
            throw new FileNotFoundException(
                $"no Texture2D for \"{assetName}\" in bundle. Available: {string.Join(", ", names)}"
            );

        var info = entry.Info;
        if (info.Width <= 0 || info.Height <= 0)
            throw new InvalidDataException(
                $"texture \"{info.Name}\" has invalid dimensions {info.Width}x{info.Height}"
            );

        LargeNativeArray<byte> data;
        if (info.Streamed)
        {
            string resPath = ResolveStreamPath(entry.Cab, info.StreamPath);
            data = await VfsReader.ReadAsync(resPath, info.StreamOffset, info.StreamSize);
        }
        else
        {
            if (info.ImageDataLength <= 0)
                throw new InvalidDataException(
                    $"texture \"{info.Name}\" has no inline image data and no stream data"
                );

            data = await VfsReader.ReadAsync(
                entry.SerializedArchivePath,
                info.ImageDataOffset,
                info.ImageDataLength
            );
        }

        // Create() takes ownership of `data` (and frees it if the format is
        // unsupported, mirroring DDSLoader's CPU path).
        var texture = CPUTexture2D.Create(
            data,
            info.Width,
            info.Height,
            info.MipCount,
            (TextureFormat)info.TextureFormat
        );
        texture.Name = info.Name;
        return texture;
    }

    static bool IsResource(string path) =>
        path.EndsWith(".resS", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".resource", StringComparison.OrdinalIgnoreCase);

    static string ArchivePath(string serializedNodePath, string entryPath)
    {
        string cab = StripExtension(LastComponent(serializedNodePath));
        return $"archive:/{cab}/{LastComponent(entryPath)}";
    }

    static string ResolveStreamPath(string cab, string streamPath)
    {
        if (streamPath.StartsWith("archive:", StringComparison.OrdinalIgnoreCase))
            return streamPath;
        return $"archive:/{cab}/{LastComponent(streamPath)}";
    }

    static string NormalizeName(string assetName)
    {
        if (string.IsNullOrEmpty(assetName))
            return null;
        return StripExtension(LastComponent(assetName));
    }

    static string LastComponent(string path)
    {
        int slash = path.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? path.Substring(slash + 1) : path;
    }

    static string StripExtension(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name.Substring(0, dot) : name;
    }
}
