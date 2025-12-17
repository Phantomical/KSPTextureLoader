using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using KSPTextureLoader.Utils;
using SaveUpgradePipeline;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine;

namespace KSPTextureLoader.Format;

internal static class QOILoader
{
    public static IEnumerable<object> LoadQOITexture<T>(
        TextureHandleImpl handle,
        TextureLoadOptions options
    )
        where T : Texture
    {
        var diskPath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", handle.Path);

        QOIHeader header;
        JobHandle jobHandle;
        IFileReadStatus readStatus;
        NativeArray<byte> buffer;

        using (var file = File.OpenRead(diskPath))
        {
            var br = new BinaryReader(file);
            header = new QOIHeader(br);

            if (header.magic != QOIHeader.UIntMagic)
                throw new Exception("Invalid QOI file: incorrect magic number");

            if (header.width > 16384 || header.height > 16384)
                throw new Exception(
                    "texture is too large to be imported into unity, textures larger than 16384 in width or height are not supported"
                );

            if (header.channels != QOIHeader.RGB && header.channels != QOIHeader.RGBA)
                throw new Exception("Unsupported QOI file: unsupported number of channels");

            if (header.colorspace != QOIHeader.sRGB && header.colorspace != QOIHeader.Linear)
                throw new Exception("Unsupported QOI file: unsupported colour space");

            var length = file.Length;
            var offset = file.Position;

            if (length > int.MaxValue)
                throw new Exception(
                    "Unsupported QOI file: file is too large, only files up to 2GB are supported"
                );

            buffer = AllocatorUtil.CreateNativeArrayHGlobal<byte>(
                (int)length,
                NativeArrayOptions.UninitializedMemory
            );
            readStatus = FileLoader.ReadFileContents(diskPath, offset, buffer, out jobHandle);
        }

        using var bufGuard = new NativeArrayGuard<byte>(buffer);
        using var jobGuard = new JobCompleteGuard(jobHandle);

        var pixels = AllocatorUtil.CreateNativeArrayHGlobal<byte>(
            (int)(header.width * header.height * header.channels),
            NativeArrayOptions.UninitializedMemory
        );

        var job = new DecodeQoiJob
        {
            data = buffer,
            pixels = pixels,
            channels = header.channels,
        };
        jobGuard.JobHandle = job.Schedule(jobGuard.JobHandle);
        bufGuard.array.DisposeExt(jobGuard.JobHandle);
        bufGuard.array = pixels;
        JobHandle.ScheduleBatchedJobs();

        if (options.Hint < TextureLoadHint.BatchSynchronous)
        {
            handle.completeHandler = null;
            yield return TextureUploadSignal.Submit();
        }

        var format = header.channels switch
        {
            QOIHeader.RGB => TextureFormat.RGB24,
            QOIHeader.RGBA => TextureFormat.RGBA32,
            _ => throw new NotSupportedException(),
        };
        var texture = TextureUtils.CreateUninitializedTexture2D(
            (int)header.width,
            (int)header.height,
            format,
            mipChain: true,
            linear: header.colorspace == QOIHeader.Linear
        );
        using var texGuard = new TextureDisposeGuard(texture);

        using (handle.WithCompleteHandler(new JobHandleCompleteHandler(jobGuard.JobHandle)))
            yield return new WaitUntil(() => jobGuard.JobHandle.IsCompleted);

        // Ensure that exceptions get rethrown if they happened.
        jobGuard.JobHandle.Complete();

        var data = texture.GetRawTextureData<byte>();
        if (data.Length < pixels.Length)
            throw new Exception("internal error: pixel buffer was too large for texture");

        NativeArray<byte>.Copy(pixels, 0, data, 0, pixels.Length);

        // Dispose of the pixels array on a background thread to avoid blocking
        // the main thread with a large dispose call.
        pixels.DisposeExt(default);

        texture.Apply(true, TextureLoader.Texture2DShouldBeReadable<T>(options));
        texGuard.Clear();
        handle.SetTexture<T>(texture, options);
    }

    struct QOIHeader
    {
        public const uint UIntMagic = 0x66696f71; // 'qoif'

        public const byte RGB = 3;
        public const byte RGBA = 4;
        public const byte sRGB = 0;
        public const byte Linear = 1;

        public uint magic;
        public uint width;
        public uint height;
        public byte channels;
        public byte colorspace;

        public QOIHeader(BinaryReader br)
        {
            magic = br.ReadUInt32();
            width = FromBigEndian(br.ReadUInt32());
            height = FromBigEndian(br.ReadUInt32());
            channels = br.ReadByte();
            colorspace = br.ReadByte();
        }
    }

    static uint FromBigEndian(uint value)
    {
        return ((value >> 24) & 0x000000FF)
            | ((value >> 8) & 0x0000FF00)
            | ((value << 8) & 0x00FF0000)
            | ((value << 24) & 0xFF000000);
    }

    struct RGBA()
    {
        public byte r;
        public byte g;
        public byte b;
        public byte a;

        public RGBA(byte r, byte g, byte b, byte a = 255)
            : this()
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }
    }

    const byte QOI_OP_RGB = 0b11111110;
    const byte QOI_OP_RGBA = 0b11111111;
    const byte QOI_OP_INDEX = 0b00000000;
    const byte QOI_OP_DIFF = 0b01000000;
    const byte QOI_OP_LUMA = 0b10000000;
    const byte QOI_OP_RUN = 0b11000000;
    const byte QOI_OP_MASK = 0b11000000;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static byte ColorHash(byte r, byte g, byte b, byte a = 255) =>
        (byte)(((uint)r * 3 + (uint)g * 5 + (uint)b * 7 + (uint)a * 11) & 63);

    unsafe struct DecodeQoiJob : IJob
    {
        public NativeArray<byte> data;
        public NativeArray<byte> pixels;
        public int channels;

        public void Execute()
        {
            RGBA* index = stackalloc RGBA[64];
            RGBA px = new(0, 0, 0, 255);
            byte* data = (byte*)this.data.GetUnsafePtr();

            int i = 0;
            int o = 0;

            while (i < this.data.Length)
            {
                if (o == pixels.Length)
                {
                    if (i + 8 >= this.data.Length)
                        throw new IndexOutOfRangeException(
                            "decode error: unexpected end of file when parsing image data"
                        );

                    for (int j = 0; j < 7; ++j)
                    {
                        if (data[i + j] != 0x00)
                            throw new Exception(
                                "invalid QOI file: reached end of image data but the stream end marker was not present"
                            );
                    }

                    if (data[i + 7] != 0x01)
                        throw new Exception(
                            "invalid QOI file: reached end of image data but the stream end marker was not present"
                        );

                    return;
                }

                int b1 = data[i++];
                var topbits = b1 >> 6;

                if (b1 == QOI_OP_RGB)
                {
                    if (i + 3 > this.data.Length)
                        throw new IndexOutOfRangeException(
                            "decode error: unexpected end of file when parsing image data"
                        );
                    px = new RGBA(data[i + 0], data[i + 1], data[i + 2]);

                    i += 3;
                }
                else if (b1 == QOI_OP_RGBA)
                {
                    if (i + 4 > this.data.Length)
                        throw new IndexOutOfRangeException(
                            "decode error: unexpected end of file when parsing image data"
                        );

                    px = new(data[i + 0], data[i + 1], data[i + 2], data[i + 3]);

                    i += 4;
                }
                else if ((b1 & QOI_OP_MASK) == QOI_OP_INDEX)
                {
                    px = index[b1];
                }
                else if ((b1 & QOI_OP_MASK) == QOI_OP_DIFF)
                {
                    px.r += (byte)(((b1 >> 4) & 0x03) - 2);
                    px.g += (byte)(((b1 >> 2) & 0x03) - 2);
                    px.b += (byte)(((b1 >> 0) & 0x03) - 2);
                }
                else if ((b1 & QOI_OP_MASK) == QOI_OP_LUMA)
                {
                    if (i + 1 >= this.data.Length)
                        throw new IndexOutOfRangeException(
                            "decode error: unexpected end of file when parsing image data"
                        );

                    int b2 = data[i];
                    int vg = (b1 & 0x3F) - 32;

                    px.r += (byte)(vg - 8 + ((b2 >> 4) & 0x0F));
                    px.g += (byte)vg;
                    px.b += (byte)(vg - 8 + ((b2 >> 0) & 0x0F));

                    i += 1;
                }
                else if ((b1 & QOI_OP_MASK) == QOI_OP_RUN)
                {
                    int run = (b1 & 0x3F) + 1;
                    if (o + run * channels > pixels.Length)
                        throw new IndexOutOfRangeException(
                            "decode error: image file contained too much data for image size"
                        );

                    for (int j = 0; j < run; ++j)
                    {
                        pixels[o++] = px.r;
                        pixels[o++] = px.g;
                        pixels[o++] = px.b;

                        if (channels == 4)
                            pixels[o++] = px.a;
                    }

                    continue;
                }

                if (o + channels > pixels.Length)
                    throw new IndexOutOfRangeException(
                        "decode error: image file contained too much data for image size"
                    );

                index[ColorHash(px.r, px.g, px.b, px.a)] = px;

                pixels[o++] = px.r;
                pixels[o++] = px.g;
                pixels[o++] = px.b;

                if (channels == 4)
                    pixels[o++] = px.a;
            }
        }
    }
}
