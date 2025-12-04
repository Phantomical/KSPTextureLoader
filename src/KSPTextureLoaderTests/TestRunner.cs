using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DDSHeaders;
using KSPTextureLoader;
using UnityEngine;

namespace KSPTextureLoaderTests;

[KSPAddon(KSPAddon.Startup.EveryScene, once: false)]
public class TestRunner : MonoBehaviour
{
    public static TestRunner Instance { get; private set; }

    void Awake()
    {
        Instance = this;
    }

    void OnDestroy()
    {
        Instance = null;
    }

    struct TestCase()
    {
        public string path;
        public bool cubemap = false;
        public bool kopernicus = false;
        public bool parallax = false;
        public bool linear = false;

        public bool Load(ConfigNode node)
        {
            if (!node.TryGetValue(nameof(path), ref path))
                return false;
            node.TryGetValue(nameof(cubemap), ref cubemap);
            node.TryGetValue(nameof(kopernicus), ref kopernicus);
            node.TryGetValue(nameof(parallax), ref parallax);
            node.TryGetValue(nameof(linear), ref linear);
            return true;
        }
    }

    public void RunTests() => StartCoroutine(DoRunTests());

    IEnumerator DoRunTests()
    {
        var tests = GameDatabase.Instance.GetConfigNodes("KSPTextureLoaderTest");

        Debug.Log("Running Tests");
        Debug.Log("==============================================");

        foreach (var node in tests)
        {
            var tc = new TestCase();
            if (!tc.Load(node))
            {
                Debug.LogError("KSPTextureLoader test case was invalid");
            }

            Debug.Log($"Running test case {tc.path}");
            yield return StartCoroutine(RunTest(tc));
        }

        Debug.Log("==============================================");
        Debug.Log($"Ran {tests.Length} tests");
    }

    IEnumerator RunTest(TestCase tc)
    {
        yield return new WaitForEndOfFrame();
        DumpDDSHeader(tc.path);

        if (!tc.cubemap)
        {
            foreach (var value in CatchExceptions(RunTestTexture2D(tc)))
                yield return value;
        }
        else
            throw new NotImplementedException();
    }

    IEnumerable<object> CatchExceptions(IEnumerator inner)
    {
        using var guard = inner as IDisposable;

        while (true)
        {
            object current;
            try
            {
                if (!inner.MoveNext())
                    break;

                current = inner.Current;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                break;
            }

            yield return current;
        }
    }

    IEnumerator RunTestTexture2D(TestCase tc)
    {
        var options = new TextureLoadOptions
        {
            Hint = TextureLoadHint.BatchSynchronous,
            Unreadable = false,
            Linear = tc.linear,
        };
        using var handle = TextureLoader.LoadTexture<Texture2D>(tc.path, options);

        if (tc.parallax)
        {
            Debug.Log("Comparing against parallax texture loader");
            var ptex = Parallax.TextureLoader.LoadTexture(tc.path, tc.linear, false);
            if (ptex != null)
            {
                using var guard = new TextureDestroyGuard(ptex);

                if (!handle.IsComplete)
                    yield return handle;

                CompareTexture2D(handle.GetTexture(), ptex);
            }
        }

        if (tc.kopernicus)
        {
            Debug.Log("Comparing against kopernicus texture loader");
            var ktex = Kopernicus.OnDemand.OnDemandStorage.LoadTexture(
                tc.path,
                false,
                false,
                false
            );
            if (ktex != null)
            {
                using var guard = new TextureDestroyGuard(ktex);

                if (!handle.IsComplete)
                    yield return handle;

                CompareTexture2D(handle.GetTexture(), ktex);
            }
        }
    }

    void CompareTexture2D(Texture2D a, Texture2D b)
    {
        if (a.width != b.width || a.height != b.height)
            throw new Exception(
                $"Texture dimensions did not match! {a.width}x{a.height} != {b.width}x{b.height}"
            );

        if ((a.mipmapCount == 1) != (b.mipmapCount == 1))
            throw new Exception(
                $"Texture mipmap counts did not match! {a.mipmapCount} != {b.mipmapCount}"
            );

        if (a.format != b.format)
            throw new Exception($"Texture formats did not match! {a.format} != {b.format}");

        if (a.graphicsFormat != b.graphicsFormat)
            throw new Exception(
                $"Texture graphics formats did not match! {a.format} != {b.format}"
            );

        var apixels = a.GetPixels32();
        var bpixels = b.GetPixels32();

        if (!Enumerable.SequenceEqual(apixels, bpixels))
            throw new Exception($"Loaded pixels were not equal");
    }

    void DumpDDSHeader(string path)
    {
        var filePath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", path);
        using var file = File.OpenRead(filePath);
        var br = new BinaryReader(file);

        var magic = br.ReadUInt32();
        if (magic != DDSValues.uintMagic)
            throw new Exception("DDS file had an invalid magic number");

        var header = new DDSHeader(br);

        Debug.Log($"DDS Header for {path}");
        Debug.Log($"  dwSize:              {header.dwSize}");
        Debug.Log($"  dwFlags:             0x{header.dwFlags:X}");
        Debug.Log($"  dwWidth:             {header.dwWidth}");
        Debug.Log($"  dwHeight:            {header.dwHeight}");
        Debug.Log($"  dwPitchOrLinearSize: {header.dwPitchOrLinearSize}");
        Debug.Log($"  dwDepth:             {header.dwDepth}");
        Debug.Log($"  dwMipMapCount:       {header.dwMipMapCount}");
        Debug.Log($"  ddspf:");
        Debug.Log($"    dwSize:            {header.ddspf.dwSize}");
        Debug.Log($"    dwFlags:           0x{header.ddspf.dwFlags:X}");
        var fourCC = header.ddspf.dwFourCC;
        if (fourCC != 0)
            Debug.Log(
                $"    dwFourCC:          {fourCC} (\"{(char)(fourCC & 0xFF)}{(char)((fourCC >> 8) & 0xFF)}{(char)((fourCC >> 16) & 0xFF)}{(char)(fourCC >> 24)}\")"
            );
        else
            Debug.Log($"    dwFourCC:          {fourCC}");
        Debug.Log($"    dwRGBBitCount:     {header.ddspf.dwRGBBitCount}");
        Debug.Log($"    dwRBitMask:        0x{header.ddspf.dwRBitMask:X8}");
        Debug.Log($"    dwGBitMask:        0x{header.ddspf.dwGBitMask:X8}");
        Debug.Log($"    dwBBitMask:        0x{header.ddspf.dwBBitMask:X8}");
        Debug.Log($"    dwABitMask:        0x{header.ddspf.dwABitMask:X8}");
        Debug.Log($"  dwCaps:              {header.dwCaps}");
        Debug.Log($"  dwCaps2:             {header.dwCaps2}");

        if (header.ddspf.dwFourCC != DDSValues.uintDX10)
            return;

        var header10 = new DDSHeaderDX10(br);

        Debug.Log($"DX10 Header:");
        Debug.Log($"  dxgiFormat:          {header10.dxgiFormat}");
        Debug.Log($"  resourceDimension:   {header10.resourceDimension}");
        Debug.Log($"  miscFlag:            {header10.miscFlag}");
        Debug.Log($"  arraySize:           {header10.arraySize}");
        Debug.Log($"  miscFlags2:          0x{header10.miscFlags2:X}");
    }

    struct TextureDestroyGuard(Texture tex) : IDisposable
    {
        public readonly void Dispose() => Destroy(tex);
    }
}
