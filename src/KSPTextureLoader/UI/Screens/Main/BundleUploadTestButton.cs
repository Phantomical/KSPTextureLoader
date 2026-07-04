using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using KSPTextureLoader.Format.AssetBundle;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace KSPTextureLoader.UI.Screens.Main;

/// <summary>
/// Debug-toolbar button that exercises the in-memory bundle upload path: it
/// builds a small <see cref="Texture2DArray"/> bundle, loads it via
/// <see cref="TextureBundleLoader"/>, and writes the result to
/// <c>BundleUploadTest.log</c>. Run it while profiling to confirm the upload
/// does not stall the main thread.
/// </summary>
internal class BundleUploadTestButton : DebugScreenButton
{
    protected override void OnClick()
    {
        // Fire and forget; the routine logs its own success/failure.
        _ = RunTest();
    }

    static async Task RunTest()
    {
        var log = new StringBuilder();
        var sw = Stopwatch.StartNew();
        try
        {
            const int width = 64;
            const int height = 64;
            const int depth = 4;
            const int mipCount = 1;
            const int graphicsFormatR8G8B8A8UNorm = 8;

            // RGBA8, 4 bytes/texel, one mip, all zero.
            var pixels = new byte[width * height * 4 * depth];

            var request = new TextureBundleBuilder.TextureRequest
            {
                ClassId = SerializedTypeTrees.Texture2DArrayClassId,
                Name = "bundle_upload_test",
                Width = width,
                Height = height,
                Depth = depth,
                MipCount = mipCount,
                Format = graphicsFormatR8G8B8A8UNorm,
                ColorSpace = 0,
                Pixels = pixels,
            };

            log.AppendLine("[BundleUploadTest] building + loading Texture2DArray bundle");
            log.AppendLine(
                $"  request: {width}x{height} depth={depth} mips={mipCount} format={graphicsFormatR8G8B8A8UNorm}"
            );

            // CreateAsync must be called from a background thread.
            Texture texture = await Task.Run(() => TextureBundleLoader.CreateAsync(request));

            sw.Stop();
            log.AppendLine($"  loaded in {sw.ElapsedMilliseconds} ms (wall clock)");
            log.AppendLine($"  result type: {texture.GetType().Name}");
            log.AppendLine($"  dimensions: {texture.width}x{texture.height}");
            log.AppendLine($"  graphicsFormat: {texture.graphicsFormat}");
            log.AppendLine($"  isReadable: {texture.isReadable}");

            bool ok =
                texture is Texture2DArray array
                && array.width == width
                && array.height == height
                && array.depth == depth;

            if (texture is Texture2DArray arr)
                log.AppendLine($"  array depth: {arr.depth}");

            log.AppendLine(ok ? "  RESULT: PASS" : "  RESULT: FAIL (type/dimension mismatch)");
            Debug.Log($"[KSPTextureLoader] BundleUploadTest {(ok ? "PASS" : "FAIL")} — see log");
        }
        catch (Exception e)
        {
            log.AppendLine($"  RESULT: EXCEPTION {e.GetType().Name}: {e.Message}");
            log.AppendLine(e.StackTrace);
            Debug.LogError($"[KSPTextureLoader] BundleUploadTest threw: {e}");
        }
        finally
        {
            var path = DebugDumpHelper.WriteDumpLog("BundleUploadTest.log", log);
            Debug.Log($"[KSPTextureLoader] BundleUploadTest log written to {path}");
        }
    }
}
