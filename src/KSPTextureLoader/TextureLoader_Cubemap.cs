using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using KSPTextureLoader.Async;
using KSPTextureLoader.Format;
using KSPTextureLoader.Utils;
using Unity.Profiling;
using UnityEngine;

namespace KSPTextureLoader;

/// <summary>
/// The paths to the six faces of a cubemap, for use with
/// <see cref="TextureLoader.LoadDisjointCubemap(CubemapFaces, TextureLoadOptions)"/>.
/// </summary>
public struct CubemapFaces
{
    public string PositiveX { get; set; }
    public string NegativeX { get; set; }
    public string PositiveY { get; set; }
    public string NegativeY { get; set; }
    public string PositiveZ { get; set; }
    public string NegativeZ { get; set; }

    /// <summary>Get or set a face path by its <see cref="CubemapFace"/>.</summary>
    public string this[CubemapFace face]
    {
        readonly get =>
            face switch
            {
                CubemapFace.PositiveX => PositiveX,
                CubemapFace.NegativeX => NegativeX,
                CubemapFace.PositiveY => PositiveY,
                CubemapFace.NegativeY => NegativeY,
                CubemapFace.PositiveZ => PositiveZ,
                CubemapFace.NegativeZ => NegativeZ,
                _ => throw new ArgumentOutOfRangeException(nameof(face)),
            };
        set
        {
            switch (face)
            {
                case CubemapFace.PositiveX:
                    PositiveX = value;
                    break;
                case CubemapFace.NegativeX:
                    NegativeX = value;
                    break;
                case CubemapFace.PositiveY:
                    PositiveY = value;
                    break;
                case CubemapFace.NegativeY:
                    NegativeY = value;
                    break;
                case CubemapFace.PositiveZ:
                    PositiveZ = value;
                    break;
                case CubemapFace.NegativeZ:
                    NegativeZ = value;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(face));
            }
        }
    }

    /// <summary>The face paths in <see cref="CubemapFace"/> order.</summary>
    internal readonly string[] ToArray() =>
        [PositiveX, NegativeX, PositiveY, NegativeY, PositiveZ, NegativeZ];
}

public partial class TextureLoader
{
    private TextureHandle<Cubemap> LoadDisjointCubemapImpl(
        CubemapFaces faces,
        TextureLoadOptions options
    )
    {
        var paths = faces.ToArray();
        for (int i = 0; i < paths.Length; ++i)
        {
            if (paths[i] is null)
                throw new ArgumentNullException(
                    nameof(faces),
                    $"cubemap face {(CubemapFace)i} was null"
                );

            paths[i] = CanonicalizeResourcePath(paths[i]);
        }

        // The key doubles as the handle's path (and so the texture name).
        var key = GetDisjointCubemapKey(paths);
        var handle = GetApplicableExistingHandle(key, key, options);
        if (handle is not null)
            return new TextureHandle<Cubemap>(handle).Acquire();

        handle = new TextureHandleImpl(key, options.Unreadable);
        textures[key] = new WeakReference<TextureHandleImpl>(handle);

        var coroutine = DoLoadDisjointCubemap(handle, paths, options);
        handle.coroutine = coroutine;
        StartCoroutine(coroutine);

        return new(handle);
    }

    /// <summary>
    /// Build the texture cache key for a disjoint cubemap:
    /// <c>disjoint-cubemap:&lt;common-path&gt;:&lt;md5&gt;</c>, where the
    /// common path is the longest prefix shared by all faces (to keep the key
    /// readable) and the hash covers the full set of face paths.
    /// </summary>
    internal static string GetDisjointCubemapKey(string[] paths)
    {
        var prefix = paths[0];
        for (int i = 1; i < paths.Length; ++i)
        {
            int len = Math.Min(prefix.Length, paths[i].Length);
            int j = 0;
            while (
                j < len && char.ToLowerInvariant(prefix[j]) == char.ToLowerInvariant(paths[i][j])
            )
                ++j;
            prefix = prefix.Substring(0, j);
        }

        // The texture cache compares keys case-insensitively, so the hash
        // needs to be case-insensitive as well.
        var joined = string.Join("|", paths).ToLowerInvariant();
        byte[] hash;
        using (var md5 = MD5.Create())
            hash = md5.ComputeHash(Encoding.UTF8.GetBytes(joined));

        var hex = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            hex.Append(b.ToString("x2"));

        return $"disjoint-cubemap:{prefix}:{hex}";
    }

    static readonly ProfilerMarker LoadDisjointCubemapMarker = new("LoadDisjointCubemap");

    private IEnumerator DoLoadDisjointCubemap(
        TextureHandleImpl handle,
        string[] paths,
        TextureLoadOptions options
    )
    {
        // Ensure that the texture handle doesn't get disposed of while we are still working on it.
        using var guard = handle.Acquire();

        IEnumerator<object> coroutine;
        using (CompletionContext.Enter(handle))
        {
            coroutine = ExceptionUtils.CatchExceptions(
                handle,
                DoLoadDisjointCubemapInner(handle, paths, options)
            );
        }

        using var _guard = coroutine;

        while (true)
        {
            using (var scope = LoadDisjointCubemapMarker.Auto())
            using (CompletionContext.Enter(handle))
            {
                if (!coroutine.MoveNext())
                    break;
            }

            yield return coroutine.Current;
        }
    }

    private IEnumerator DoLoadDisjointCubemapInner(
        TextureHandleImpl handle,
        string[] paths,
        TextureLoadOptions options
    )
    {
        if (Config.Instance.DebugMode >= DebugLevel.Debug)
            Debug.Log($"[KSPTextureLoader] Loading disjoint cubemap {handle.Path}");

        if (CanLoadDisjointCubemapViaBundle(paths, options))
        {
            var diskPaths = new string[paths.Length];
            for (int i = 0; i < paths.Length; ++i)
                diskPaths[i] = Path.Combine(PathUtil.GameDataDir, paths[i]);

            var task = Task.Run(() =>
                DDSLoader.TryLoadDisjointCubemapViaBundle(handle, diskPaths, options)
            );
            using (handle.WithCompleteHandler(new TaskCompleteHandler(task)))
                yield return new WaitUntilTask(task);

            if (task.GetResultUnwrapped())
            {
                if (!handle.IsComplete)
                    throw new Exception("internal error: task completed but has not set a texture");
                yield break;
            }

            if (Config.Instance.DebugMode == DebugLevel.Trace)
            {
                Debug.Log(
                    $"[KSPTextureLoader] Disjoint cubemap {handle.Path} is not eligible for the bundle path, falling back to loading each face"
                );
            }
        }

        if (TextureUtils.CanGpuCopyTexture2dToCubemap)
        {
            var faces = new TextureHandle<Texture2D>[paths.Length];
            using var facesGuard = new ArrayDisposeGuard<TextureHandle<Texture2D>>(faces);

            for (int i = 0; i < paths.Length; ++i)
                faces[i] = LoadTexture<Texture2D>(paths[i], options);

            foreach (var face in faces)
            {
                if (face.IsComplete)
                    continue;

                using (handle.WithCompleteHandler(face.handle))
                    yield return face;
            }

            var textures = new Texture2D[faces.Length];
            for (int i = 0; i < faces.Length; ++i)
                textures[i] = faces[i].GetTexture();

            var cube = DisjointCubemapLoader.CombineWithCopyTexture(
                textures,
                readable: !options.Unreadable
            );
            handle.SetTexture<Cubemap>(cube, options, default);
        }
        else
        {
            var faces = new CPUTextureHandle[paths.Length];
            using var facesGuard = new ArrayDisposeGuard<CPUTextureHandle>(faces);

            for (int i = 0; i < paths.Length; ++i)
                faces[i] = LoadCPUTexture(paths[i], options);

            foreach (var face in faces)
            {
                if (face.IsComplete)
                    continue;

                using (handle.WithCompleteHandler(face))
                    yield return face;
            }

            var textures = new CPUTexture2D[faces.Length];
            for (int i = 0; i < faces.Length; ++i)
                textures[i] = faces[i].GetTexture();

            var task = AsyncUtil.LaunchMainThreadTask(() =>
                DisjointCubemapLoader.CombineOnCpu(textures, options)
            );
            using (handle.WithCompleteHandler(new TaskCompleteHandler(task)))
                yield return new WaitUntilTask(task);

            handle.SetTexture<Cubemap>(task.GetResultUnwrapped(), options, default);
        }
    }

    /// <summary>
    /// Cheap main-thread checks for whether the bundle path could apply,
    /// before reading any file headers.
    /// </summary>
    private static bool CanLoadDisjointCubemapViaBundle(string[] paths, TextureLoadOptions options)
    {
        // Mirrors the option checks in DDSLoader.ShouldLoadViaBundle.
        if (!options.Unreadable || options.Hint > TextureLoadHint.BatchAsynchronous)
            return false;

        foreach (var path in paths)
        {
            if (!string.Equals(Path.GetExtension(path), ".dds", StringComparison.OrdinalIgnoreCase))
                return false;

            // A face that would be overridden by an asset bundle needs to go
            // through the regular load path.
            if (GetAssetBundlesForKey(path, options.AssetBundles).Count != 0)
                return false;

            if (!File.Exists(Path.Combine(PathUtil.GameDataDir, path)))
                return false;
        }

        return true;
    }
}
