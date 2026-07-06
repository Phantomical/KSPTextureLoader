using KSP.Testing;
using KSPTextureLoader.Format.Bundle;

namespace KSPTextureLoaderTests.Bundle;

/// <summary>
/// Tests for the pure path/name helpers in <see cref="BundleIndex"/> that map
/// between asset paths, bundle <c>CAB-*</c> names and <c>archive:/</c> virtual
/// paths. The read/parse path itself needs a mounted bundle and is covered by
/// in-game verification; these lock down the string handling around it.
/// </summary>
public class BundleIndexHelpersTests : BundleParseTestBase
{
    [TestInfo("BundleIndex_NormalizeName")]
    public void TestNormalizeName()
    {
        // Keys are the full path, lowercased with '/' separators (no basename
        // collapse), matching TextureLoader.CanonicalizeAssetPath.
        AssertEqual(
            "lowercases-keeps-path-and-ext",
            BundleIndex.NormalizeName("Foo/Bar/baz.png"),
            "foo/bar/baz.png"
        );
        AssertEqual(
            "backslashes-to-slashes",
            BundleIndex.NormalizeName("Foo\\Bar\\baz.dds"),
            "foo/bar/baz.dds"
        );
        // Same file name under different directories stays distinct.
        AssertEqual(
            "distinct-by-dir",
            BundleIndex.NormalizeName("Duna/PluginData/mid00.dds"),
            "duna/plugindata/mid00.dds"
        );
        AssertEqual("no-dir-no-ext", BundleIndex.NormalizeName("Texture"), "texture");
        AssertEqual("null-is-null", BundleIndex.NormalizeName(null), null);
        AssertEqual("empty-is-null", BundleIndex.NormalizeName(""), null);
    }

    [TestInfo("BundleIndex_StripExtension")]
    public void TestStripExtension()
    {
        AssertEqual("simple", BundleIndex.StripExtension("name.ext"), "name");
        AssertEqual("multiple-dots", BundleIndex.StripExtension("a.b.c"), "a.b");
        AssertEqual("no-extension", BundleIndex.StripExtension("name"), "name");
        // A leading dot is not an extension separator.
        AssertEqual("leading-dot-kept", BundleIndex.StripExtension(".hidden"), ".hidden");
    }

    [TestInfo("BundleIndex_LastComponent")]
    public void TestLastComponent()
    {
        AssertEqual("forward-slash", BundleIndex.LastComponent("a/b/c"), "c");
        AssertEqual("back-slash", BundleIndex.LastComponent("a\\b\\c"), "c");
        AssertEqual("mixed", BundleIndex.LastComponent("a/b\\c"), "c");
        AssertEqual("no-slash", BundleIndex.LastComponent("c"), "c");
    }

    [TestInfo("BundleIndex_ArchivePath")]
    public void TestArchivePath()
    {
        // Built as ArchivePath(node.Path, node.Path) for a serialized CAB entry.
        AssertEqual(
            "cab-entry",
            BundleIndex.ArchivePath("CAB-abc123", "CAB-abc123"),
            "archive:/CAB-abc123/CAB-abc123"
        );
        // The cab name comes from the serialized node, the leaf from the entry.
        AssertEqual(
            "distinct-entry",
            BundleIndex.ArchivePath("dir/CAB-xyz.sharedAssets", "dir/CAB-xyz.resS"),
            "archive:/CAB-xyz/CAB-xyz.resS"
        );
    }

    [TestInfo("BundleIndex_ResolveStreamPath")]
    public void TestResolveStreamPath()
    {
        // An absolute archive path is passed through unchanged.
        AssertEqual(
            "already-archive",
            BundleIndex.ResolveStreamPath("CAB-abc", "archive:/CAB-abc/tex.resS"),
            "archive:/CAB-abc/tex.resS"
        );
        // A bare/relative stream path is rebuilt against this file's cab name.
        AssertEqual(
            "relative-rebuilt",
            BundleIndex.ResolveStreamPath("CAB-abc", "some/dir/tex.resS"),
            "archive:/CAB-abc/tex.resS"
        );
    }
}
