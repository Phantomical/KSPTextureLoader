using System;
using System.Collections.Generic;
using System.IO;
using KSPTextureLoader.Utils;
using Unity.Collections.LowLevel.Unsafe;
using Unity.IO.LowLevel.Unsafe;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Networking;

namespace KSPTextureLoader;

partial class TextureLoader
{
    static readonly ProfilerMarker LoadImageMarker = new("LoadImage");

    IEnumerable<object> LoadPNGOrJPEG<T>(TextureHandleImpl handle, TextureLoadOptions options)
        where T : Texture
    {
        Texture2D texture;
        var diskPath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", handle.Path);
        // Cubemap textures need to be converted, so they must be readable.
        var unreadable = typeof(T) != typeof(Cubemap) && options.Unreadable;

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

        var array = new byte[(int)length];
        ulong gchandle;

        ReadHandle readHandle;
        unsafe
        {
            var ptr = UnsafeUtility.PinGCArrayAndGetDataAddress(array, out gchandle);
            var command = new ReadCommand
            {
                Buffer = ptr,
                Offset = 0,
                Size = length,
            };
            readHandle = LaunchRead(diskPath, command);
        }

        using var gcHandleGuard = new GcHandleGuard(gchandle);
        using var readGuard = new SafeReadHandleGuard(readHandle);

        using (handle.WithCompleteHandler(new JobHandleCompleteHandler(readGuard.JobHandle)))
            yield return new WaitUntil(() => readGuard.JobHandle.IsCompleted);

        if (readHandle.Status != ReadStatus.Complete)
            throw new Exception("an error occurred while reading from the file");

        texture = new Texture2D(1, 1);
        using (LoadImageMarker.Auto())
            texture.LoadImage(array, unreadable);
        handle.SetTexture<T>(texture, options);
    }
}
