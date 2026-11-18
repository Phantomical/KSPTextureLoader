using KSP.Testing;
using KSPTextureLoader;
using KSPTextureLoader.Format;
using UnityEngine.Experimental.Rendering;

namespace KSPTextureLoaderTests.Bundle;

public class BundleEligibilityTests : BundleParseTestBase
{
    [TestInfo("BundleEligibility_UncompressedAndCompressed")]
    public void TestFormats()
    {
        var options = new TextureLoadOptions
        {
            Unreadable = true,
            Hint = TextureLoadHint.Asynchronous,
        };

        AssertTrue("odd RGBA", DDSLoader.ShouldLoadViaBundle(
            options, 168, GraphicsFormat.R8G8B8A8_UNorm, 6, 7));
        AssertTrue("float", DDSLoader.ShouldLoadViaBundle(
            options, 168, GraphicsFormat.R32_SFloat, 6, 7));
        AssertTrue("aligned BC", DDSLoader.ShouldLoadViaBundle(
            options, 32, GraphicsFormat.RGBA_DXT1_UNorm, 8, 8));
        AssertTrue("unaligned BC rejected", !DDSLoader.ShouldLoadViaBundle(
            options, 32, GraphicsFormat.RGBA_DXT1_UNorm, 6, 7));

        options.Unreadable = false;
        AssertTrue("readable rejected", !DDSLoader.ShouldLoadViaBundle(
            options, 168, GraphicsFormat.R8G8B8A8_UNorm, 6, 7));
        options.Unreadable = true;
        options.Hint = TextureLoadHint.Synchronous;
        AssertTrue("synchronous rejected", !DDSLoader.ShouldLoadViaBundle(
            options, 168, GraphicsFormat.R8G8B8A8_UNorm, 6, 7));
    }

    [TestInfo("BundleEligibility_MipmapLimitAlignment")]
    public void TestMipmapLimitAlignment()
    {
        foreach (var format in new[] { GraphicsFormat.R8G8B8A8_UNorm, GraphicsFormat.RGBA_DXT1_UNorm })
        {
            AssertTrue($"{format} aligned", DDSLoader.SupportsMipmapLimit(format, 32, 64, 7));
            AssertTrue($"{format} third retained mip unaligned",
                !DDSLoader.SupportsMipmapLimit(format, 16, 64, 7));
            AssertTrue($"{format} height unaligned",
                !DDSLoader.SupportsMipmapLimit(format, 64, 24, 7));
            AssertTrue($"{format} single mip unaligned",
                !DDSLoader.SupportsMipmapLimit(format, 6, 7, 1));
            AssertTrue($"{format} base mip unaligned",
                !DDSLoader.SupportsMipmapLimit(format, 9, 8, 2));
        }
    }
}
