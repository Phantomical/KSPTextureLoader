using System;
using System.Threading.Tasks;
using KSP.Testing;
using KSPTextureLoader;
using KSPTextureLoader.Format;
using KSPTextureLoader.Utils;
using Unity.Collections;
using UnityEngine;

namespace KSPTextureLoaderTests;

public class TextureMemoryTests : KSPTextureLoaderTestBase
{
    const int W = 4;
    const int H = 4;

    [TestInfo("DDSLoader_PaletteBufferOwnership")]
    public void TestPaletteBufferOwnership()
    {
        using var input = new LargeNativeArray<byte>(16 * 4 + W * H / 2, Allocator.Persistent);
        var source = input.GetSubArray(0, input.Length);
        var metadata = new DDSLoader.TextureMetadata
        {
            width = W,
            height = H,
            paletteType = DDSLoader.KopernicusPaletteType.Palette4,
        };
        var decode = DDSLoader.DecodePaletteToRGBA32(metadata, Task.FromResult(source));
        using var output = TextureLoader.Context.RunUntilComplete(decode, null);
        if (output.Length != 4 * W * H || output.Allocator != AllocatorUtil.HGlobal)
            throw new Exception("Palette decoding returned a buffer without its HGlobal owner");
    }

    [TestInfo("CPUTexture2D_HGlobalOwnership")]
    public void TestHGlobalOwnership()
    {
        var initial = AllocatorUtil.AllocMem;
        var pixels = AllocatorUtil.CreateNativeArrayHGlobal<Color32>((long)W * H);
        if (AllocatorUtil.AllocMem - initial != W * H * 4)
        {
            pixels.Dispose();
            throw new Exception("RGBA allocation was not accounted for in bytes");
        }

        using var texture = CPUTexture2D.Create(pixels.Reinterpret<byte>(), W, H, 1, TextureFormat.RGBA32);
        if (AllocatorUtil.AllocMem != initial)
            throw new Exception("CPU texture still counts against the temporary load budget");

        var truncated = AllocatorUtil.CreateNativeArrayHGlobal<byte>((long)W * H * 4 - 1);
        try
        {
            using var unexpected = CPUTexture2D.Create(truncated, W, H, 1, TextureFormat.RGBA32);
        }
        catch (Exception)
        {
            if (AllocatorUtil.AllocMem != initial)
                throw new Exception("Rejected CPU texture still counts against the temporary load budget");
            return;
        }

        throw new Exception("Truncated RGBA data was accepted");
    }

    [TestInfo("CPUTexture2D_NativeArrayOwnership")]
    public void TestNativeArrayOwnership()
    {
        var pixels = new NativeArray<byte>(W * H * 4, Allocator.Persistent);
        pixels[0] = 42;
        using var texture = CPUTexture2D.Create(pixels, W, H, 1, TextureFormat.RGBA32);
        if (texture.GetPixel32(0, 0).r != 42)
            throw new Exception("CPU texture lost its backing array");
    }
}
