using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using KSPTextureLoader.Utils;
using UnityEngine;

namespace KSPTextureLoader.Format.Bundle;

/// <summary>
/// Creates a GPU texture by wrapping the pixel data in a tiny streamed bundle
/// (<see cref="TextureBundleBuilder"/> + <see cref="BundleStream"/>) and loading
/// it asynchronously, so the GPU upload runs through Unity's threaded
/// async-upload pipeline instead of stalling the main thread the way
/// code-creating an array/volume texture does.
/// </summary>
internal static class TextureBundleLoader
{
    /// <summary>
    /// Build the bundle prefix on the calling thread (call this from a
    /// background thread), then load it on the main thread and return the
    /// realized texture. The <paramref name="request"/>'s pixel buffer must
    /// stay alive until the returned task completes.
    /// </summary>
    public static Task<Texture> CreateAsync(
        TextureBundleBuilder.TextureRequest request,
        int platform = TextureBundleBuilder.StandaloneWindows64
    )
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (request.Pixels is null)
            throw new ArgumentNullException(nameof(request.Pixels));

        var built = TextureBundleBuilder.Build(request, request.Pixels.LongLength, platform);
        var stream = new BundleStream(
            built.Prefix,
            new MemoryStream(request.Pixels, writable: false),
            0,
            request.Pixels.LongLength
        );

        return AsyncUtil.LaunchMainThreadTask(() => LoadOnMainThread(built, stream));
    }

    /// <summary>
    /// Load a texture from a <see cref="TextureBundleBuilder.Built" /> instance.
    /// </summary>
    /// <param name="built"></param>
    /// <param name="bundleStream"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static async Task<Texture> LoadOnMainThread(
        TextureBundleBuilder.Built built,
        Stream bundleStream = null
    )
    {
        // Keep bundle unloads from running while this load is in flight;
        // AssetBundle.Unload would stall against the loading thread.
        using var activeLoadGuard = new TextureLoader.ActiveAssetBundleLoadGuard();

        // Unity reads the stream from its loading thread until the bundle is
        // unloaded, so the stream is handed to the AssetBundleHandle below,
        // which disposes it after the unload.
        AssetBundle bundle;
        try
        {
            var createRequest = bundleStream is null
                ? AssetBundle.LoadFromMemoryAsync(built.Prefix)
                : AssetBundle.LoadFromStreamAsync(bundleStream, 0, 128 * 1024);
            bundle = await AsyncUtil.WaitFor(createRequest);
        }
        catch
        {
            bundleStream?.Dispose();
            throw;
        }

        if (Config.Instance.DebugMode == DebugLevel.Trace)
            Debug.Log(
                $"[KSPTextureLoader] Mounted streamed bundle ({bundleStream?.Length ?? built.Prefix.Length} bytes), requesting asset \"{built.AssetName}\""
            );

        var handle = new AssetBundleHandle($"<in-memory:{bundle.name}>", bundle)
        {
            onUnload = bundleStream,
        };
        try
        {
            var loadRequest = bundle.LoadAssetAsync(built.AssetName, typeof(Texture));
            return await AsyncUtil.WaitFor<Texture>(loadRequest);
        }
        finally
        {
            var loader = TextureLoader.Instance;
            if (loader != null)
            {
                loader.QueueBundleUnload(handle);
                handle.Dispose();
            }
            else
            {
                handle.DestroyNoRemove();
            }
        }
    }
}
