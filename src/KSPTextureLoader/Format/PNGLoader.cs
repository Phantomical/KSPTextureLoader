using System;
using System.Collections.Generic;
using System.IO;
using KSPTextureLoader.Utils;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Networking;

namespace KSPTextureLoader.Format;

// This also handles JPEG, since that goes through the same path in unity.
internal static class PNGLoader
{
    static readonly ProfilerMarker LoadImageMarker = new("LoadImage");

    public static IEnumerable<object> LoadPNGOrJPEG<T>(
        TextureHandleImpl handle,
        TextureLoadOptions options
    )
        where T : Texture
    {
        Texture2D texture;
        var diskPath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", handle.Path);
        // Cubemap textures need to be converted, so they must be readable.
        var unreadable = false;

        if (options.Hint < TextureLoadHint.BatchSynchronous)
        {
            var url = new Uri(diskPath);
            using var request = UnityWebRequestTexture.GetTexture(url, unreadable);

            yield return request.SendWebRequest();

            // We cannot block on the completion of a web request. That just results
            // in an infinite hang. If the web request isn't complete then we fall
            // back to a synchronous read off disk.
            if (request.isDone)
            {
                if (request.isNetworkError || request.isHttpError)
                    throw new Exception($"Failed to load image: {request.error}");

                texture = DownloadHandlerTexture.GetContent(request);
                handle.SetTexture<T>(texture, options);
                yield break;
            }
        }

        var info = new FileInfo(diskPath);
        if (!info.Exists)
            throw new FileNotFoundException("file not found");

        var length = info.Length;
        if (length > int.MaxValue)
            throw new Exception(
                "image was too large to be loaded. Only images up to 2GB in size are supported."
            );

        if (options.Hint < TextureLoadHint.BatchSynchronous && AllocatorUtil.IsAboveWatermark)
            yield return AllocatorUtil.WaitUntilMemoryBelowWatermark();

        var array = new byte[(int)length];
        ulong gchandle;

        IFileReadStatus readStatusTmp;
        JobHandle jobHandle;
        unsafe
        {
            var ptr = UnsafeUtility.PinGCArrayAndGetDataAddress(array, out gchandle);
            var buffer = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(
                ptr,
                (int)length,
                Allocator.Invalid
            );

            readStatusTmp = FileLoader.ReadFileContents(diskPath, 0, buffer, out jobHandle);
        }

        using var readStatus = readStatusTmp;
        using var gcHandleGuard = new GcHandleGuard(gchandle);

        using (handle.WithCompleteHandler(new JobHandleCompleteHandler(jobHandle)))
            yield return new WaitUntil(() => jobHandle.IsCompleted);

        readStatus.ThrowIfError();

        texture = new Texture2D(1, 1);
        using (LoadImageMarker.Auto())
            texture.LoadImage(array, unreadable);
        handle.SetTexture<T>(texture, options);
    }
}
