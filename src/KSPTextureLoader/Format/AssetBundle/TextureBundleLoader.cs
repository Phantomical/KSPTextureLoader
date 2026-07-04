using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KSPTextureLoader.Utils;
using UnityEngine;

namespace KSPTextureLoader.Format.AssetBundle;

/// <summary>
/// Creates a GPU texture by wrapping the pixel data in a tiny in-memory bundle
/// (<see cref="TextureBundleBuilder"/>) and loading it asynchronously, so the
/// GPU upload runs through Unity's threaded async-upload pipeline instead of
/// stalling the main thread the way code-creating an array/volume texture does.
/// </summary>
internal static class TextureBundleLoader
{
    /// <summary>
    /// Build the bundle on the calling thread (call this from a background
    /// thread), then load it on the main thread and return the realized texture.
    /// The <paramref name="request"/>'s pixel buffer can be released as soon as
    /// this returns the task.
    /// </summary>
    public static Task<Texture> CreateAsync(
        TextureBundleBuilder.TextureRequest request,
        int platform = TextureBundleBuilder.StandaloneWindows64
    )
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        TextureBundleBuilder.Built built = TextureBundleBuilder.Build(request, platform);
        return LoadAsync(built);
    }

    /// <summary>Load an already-built bundle and return the realized texture.</summary>
    public static Task<Texture> LoadAsync(TextureBundleBuilder.Built built) =>
        AsyncUtil.LaunchMainThreadTask(() => LoadOnMainThread(built));

    static async Task<Texture> LoadOnMainThread(TextureBundleBuilder.Built built)
    {
        // Keep bundle unloads from running while this load is in flight;
        // AssetBundle.Unload would stall against the loading thread.
        using var activeLoadGuard = new TextureLoader.ActiveAssetBundleLoadGuard();

        var createRequest = UnityEngine.AssetBundle.LoadFromMemoryAsync(built.Bundle);
        await AwaitOperation(createRequest, new AssetBundleCompleteHandler(createRequest));

        UnityEngine.AssetBundle bundle = createRequest.assetBundle;
        if (bundle == null)
            throw new InvalidOperationException(
                "LoadFromMemoryAsync produced no AssetBundle (malformed in-memory bundle)"
            );

        if (Config.Instance.DebugMode >= DebugLevel.Debug)
            Debug.Log(
                $"[KSPTextureLoader] Mounted in-memory bundle ({built.Bundle.Length} bytes), requesting asset \"{built.AssetName}\""
            );

        var handle = new AssetBundleHandle($"<in-memory:{bundle.name}>", bundle);
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
