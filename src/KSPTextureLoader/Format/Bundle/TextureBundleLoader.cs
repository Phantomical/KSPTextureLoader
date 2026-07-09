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
        TextureLoadOptions options,
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
        return LoadAsync(built, stream, options);
    }

    /// <summary>
    /// Load an already-built bundle and return the realized texture.
    /// <paramref name="bundleStream"/> holds the complete bundle bytes;
    /// ownership passes to the loader, which keeps it open until Unity unloads
    /// the bundle.
    /// </summary>
    public static Task<Texture> LoadAsync(
        TextureBundleBuilder.Built built,
        Stream bundleStream,
        TextureLoadOptions options
    ) => AsyncUtil.LaunchMainThreadTask(() => LoadOnMainThread(built, bundleStream, options));

    /// <summary>
    /// Load an already-built bundle whose stream data points at an external
    /// file (<see cref="TextureBundleBuilder.Build(TextureBundleBuilder.TextureRequest, long, string, long, int)"/>):
    /// the prefix is the complete bundle and Unity reads the pixel bytes from
    /// the file itself, so there is no stream to keep alive.
    /// </summary>
    public static Task<Texture> LoadAsync(
        TextureBundleBuilder.Built built,
        TextureLoadOptions options
    ) => AsyncUtil.LaunchMainThreadTask(() => LoadOnMainThread(built, bundleStream: null, options));

    static async Task<Texture> LoadOnMainThread(
        TextureBundleBuilder.Built built,
        Stream bundleStream,
        TextureLoadOptions options
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
            await AwaitOperation(createRequest, new AssetBundleCompleteHandler(createRequest));
            bundle = createRequest.assetBundle;

            if (bundle == null)
                throw new InvalidOperationException(
                    "LoadFromStreamAsync produced no AssetBundle (malformed in-memory bundle)"
                );
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
            await AwaitOperation(loadRequest, new AssetBundleRequestCompleteHandler(loadRequest));

            if (loadRequest.asset is not Texture texture)
            {
                var allAssets = bundle.LoadAllAssets(typeof(Texture));
                Debug.LogError(
                    $"[KSPTextureLoader] in-memory bundle did not contain a Texture named \"{built.AssetName}\"; "
                        + $"loadRequest.asset = {(loadRequest.asset == null ? "null" : loadRequest.asset.GetType().FullName)}, "
                        + $"bundle.Contains(name) = {bundle.Contains(built.AssetName)}, "
                        + $"bundle.GetAllAssetNames() = [{string.Join(", ", bundle.GetAllAssetNames())}], "
                        + $"LoadAllAssets(typeof(Texture)) count = {allAssets.Length}, contents = "
                        + $"[{string.Join(", ", Array.ConvertAll(allAssets, o => $"{o.GetType().FullName}:{o.name}"))}]"
                );
                throw new InvalidOperationException(
                    $"in-memory bundle did not contain a Texture named \"{built.AssetName}\""
                );
            }

            return texture;
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

    sealed class PendingOperation(ICompleteHandler handler, TaskCompletionSource<bool> tcs)
    {
        public readonly ICompleteHandler Handler = handler;
        public readonly TaskCompletionSource<bool> Tcs = tcs;
    }

    // Operations still waiting on the player loop. Only touched on the main
    // thread.
    static readonly List<PendingOperation> pendingOperations = [];
    static bool blockedHookRegistered;

    /// <summary>
    /// Wrap a Unity <see cref="AsyncOperation"/> as a task. Must be called on the
    /// main thread. Normally the operation completes via the player loop, but if
    /// someone blocks the main thread waiting on a texture (so the player loop
    /// never runs) <paramref name="handler"/> is used to complete it
    /// synchronously instead — otherwise the load would deadlock.
    /// </summary>
    static Task AwaitOperation(AsyncOperation operation, ICompleteHandler handler)
    {
        if (operation.isDone)
            return Task.CompletedTask;

        if (!blockedHookRegistered)
        {
            TextureLoader.Context.AddBlockedHook(ForcePendingOperation);
            blockedHookRegistered = true;
        }

        var tcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var pending = new PendingOperation(handler, tcs);
        pendingOperations.Add(pending);
        operation.completed += _ =>
        {
            pendingOperations.Remove(pending);
            tcs.TrySetResult(true);
        };
        return tcs.Task;
    }

    static bool ForcePendingOperation()
    {
        if (pendingOperations.Count == 0)
            return false;

        // Forcing an asset bundle operation while a scene switch is in flight
        // can deadlock inside unity; leave the operation for the player loop,
        // same as the regular asset bundle loader.
        if (TextureLoader.PendingSceneSwitch)
            return false;

        var pending = pendingOperations[0];
        pendingOperations.RemoveAt(0);
        pending.Handler.WaitUntilComplete();
        pending.Tcs.TrySetResult(true);
        return true;
    }
}
