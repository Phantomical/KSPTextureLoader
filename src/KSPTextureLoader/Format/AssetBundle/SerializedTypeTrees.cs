using System;
using System.Collections.Generic;

namespace KSPTextureLoader.Format.AssetBundle;

/// <summary>
/// The serialized field layouts and old-type hashes for the texture classes and
/// <c>AssetBundle</c>, extracted from AssetsTools.NET's class database for Unity
/// <b>2019.4.18f1</b>. These drive <see cref="SerializedFileWriter"/>.
/// </summary>
///
/// <remarks>
/// The emitted bundle carries no type trees — Unity deserializes objects from
/// its compiled-in layout — so these tables must reproduce the engine's exact
/// field order, sizes and alignment points. That is what pins the whole
/// approach to this engine version.
/// </remarks>
internal static class SerializedTypeTrees
{
    public const int Texture2DClassId = 28;
    public const int CubemapClassId = 89;
    public const int Texture3DClassId = 117;
    public const int Texture2DArrayClassId = 187;
    public const int CubemapArrayClassId = 188;
    public const int AssetBundleClassId = 142;

    /// <summary>
    /// The engine version written into the serialized file header, and the only
    /// version these layouts and hashes are valid for.
    /// </summary>
    public const string UnityVersion = "2019.4.18f1";

    /// <summary>A flattened type-tree row, before hierarchy is rebuilt from levels.</summary>
    static TypeTreeNode Node(
        int level,
        string type,
        string name,
        int byteSize,
        bool array,
        bool align
    )
    {
        byte typeFlags = (byte)(array ? 1 : 0);
        int metaFlags = align ? 0x4000 : 0;
        return new TypeTreeNode(0, (byte)level, typeFlags, type, name, byteSize, 0, metaFlags);
    }

    /// <summary>The hierarchical type-tree root for an object class id.</summary>
    public static TypeTreeTreeNode Root(int classId) => BuildTree(Flat(classId));

    /// <summary>The 16-byte old-type hash written into the serialized type entry.</summary>
    public static byte[] OldTypeHash(int classId) =>
        classId switch
        {
            Texture2DClassId => Hex("512dc6156c2d621c9f03b4db6490f569"),
            CubemapClassId => Hex("8a360ff25b259daf4ffaeab24d45aca3"),
            Texture3DClassId => Hex("64b6c814168880c98f6f46ae4d6ee690"),
            Texture2DArrayClassId => Hex("5be4815b2352650c28a100f99fb2769d"),
            CubemapArrayClassId => Hex("642abb854b470045553fbf6514d192aa"),
            AssetBundleClassId => Hex("97da5f4688e45a57c8b42d4f42497297"),
            _ => throw new ArgumentOutOfRangeException(nameof(classId)),
        };

    static List<TypeTreeNode> Flat(int classId) =>
        classId switch
        {
            Texture2DClassId => Texture2D(),
            CubemapClassId => Cubemap(),
            Texture3DClassId => Texture3D(),
            Texture2DArrayClassId => Texture2DArray(),
            CubemapArrayClassId => CubemapArray(),
            AssetBundleClassId => AssetBundle(),
            _ => throw new ArgumentOutOfRangeException(nameof(classId)),
        };

    // The classic Texture2D / Cubemap body, identical between the two.
    // m_TextureFormat is the legacy TextureFormat.
    static void ClassicTextureBody(List<TypeTreeNode> n)
    {
        n.Add(Node(1, "string", "m_Name", -1, false, false));
        n.Add(Node(2, "Array", "Array", -1, true, true));
        n.Add(Node(3, "int", "size", 4, false, false));
        n.Add(Node(3, "char", "data", 1, false, false));
        n.Add(Node(1, "int", "m_ForcedFallbackFormat", 4, false, false));
        n.Add(Node(1, "bool", "m_DownscaleFallback", 1, false, true));
        n.Add(Node(1, "int", "m_Width", 4, false, false));
        n.Add(Node(1, "int", "m_Height", 4, false, false));
        n.Add(Node(1, "int", "m_CompleteImageSize", 4, false, false));
        n.Add(Node(1, "int", "m_TextureFormat", 4, false, false));
        n.Add(Node(1, "int", "m_MipCount", 4, false, false));
        n.Add(Node(1, "bool", "m_IsReadable", 1, false, false));
        n.Add(Node(1, "bool", "m_IgnoreMasterTextureLimit", 1, false, false));
        n.Add(Node(1, "bool", "m_IsPreProcessed", 1, false, false));
        n.Add(Node(1, "bool", "m_StreamingMipmaps", 1, false, true));
        n.Add(Node(1, "int", "m_StreamingMipmapsPriority", 4, false, true));
        n.Add(Node(1, "int", "m_ImageCount", 4, false, false));
        n.Add(Node(1, "int", "m_TextureDimension", 4, false, false));
        TextureSettings(n, 1);
        n.Add(Node(1, "int", "m_LightmapFormat", 4, false, false));
        n.Add(Node(1, "int", "m_ColorSpace", 4, false, false));
        ImageDataAndStream(n, 1);
    }

    static void TextureSettings(List<TypeTreeNode> n, int level)
    {
        n.Add(Node(level, "GLTextureSettings", "m_TextureSettings", 24, false, false));
        n.Add(Node(level + 1, "int", "m_FilterMode", 4, false, false));
        n.Add(Node(level + 1, "int", "m_Aniso", 4, false, false));
        n.Add(Node(level + 1, "float", "m_MipBias", 4, false, false));
        n.Add(Node(level + 1, "int", "m_WrapU", 4, false, false));
        n.Add(Node(level + 1, "int", "m_WrapV", 4, false, false));
        n.Add(Node(level + 1, "int", "m_WrapW", 4, false, false));
    }

    static void ImageDataAndStream(List<TypeTreeNode> n, int level)
    {
        n.Add(Node(level, "TypelessData", "image data", -1, true, true));
        n.Add(Node(level + 1, "int", "size", 4, false, false));
        n.Add(Node(level + 1, "UInt8", "data", 1, false, false));

        n.Add(Node(level, "StreamingInfo", "m_StreamData", -1, false, false));
        n.Add(Node(level + 1, "unsigned int", "offset", 4, false, false));
        n.Add(Node(level + 1, "unsigned int", "size", 4, false, false));
        n.Add(Node(level + 1, "string", "path", -1, false, false));
        n.Add(Node(level + 2, "Array", "Array", -1, true, true));
        n.Add(Node(level + 3, "int", "size", 4, false, false));
        n.Add(Node(level + 3, "char", "data", 1, false, false));
    }

    static List<TypeTreeNode> Texture2D()
    {
        var n = new List<TypeTreeNode> { Node(0, "Texture2D", "Base", -1, false, false) };
        ClassicTextureBody(n);
        return n;
    }

    static List<TypeTreeNode> Cubemap()
    {
        var n = new List<TypeTreeNode> { Node(0, "Cubemap", "Base", -1, false, false) };
        ClassicTextureBody(n);
        n.Add(Node(1, "vector", "m_SourceTextures", -1, false, true));
        n.Add(Node(2, "Array", "Array", -1, true, true));
        n.Add(Node(3, "int", "size", 4, false, false));
        n.Add(Node(3, "PPtr<Texture2D>", "data", 12, false, false));
        n.Add(Node(4, "int", "m_FileID", 4, false, false));
        n.Add(Node(4, "SInt64", "m_PathID", 8, false, false));
        return n;
    }

    static List<TypeTreeNode> Texture3D()
    {
        var n = new List<TypeTreeNode> { Node(0, "Texture3D", "Base", -1, false, false) };
        BuildModern(n, cubemapArray: false, mipCountAlign: true);
        return n;
    }

    static List<TypeTreeNode> Texture2DArray()
    {
        var n = new List<TypeTreeNode> { Node(0, "Texture2DArray", "Base", -1, false, false) };
        BuildModern(n, cubemapArray: false, mipCountAlign: false);
        return n;
    }

    static List<TypeTreeNode> CubemapArray()
    {
        var n = new List<TypeTreeNode> { Node(0, "CubemapArray", "Base", -1, false, false) };
        BuildModern(n, cubemapArray: true, mipCountAlign: false);
        return n;
    }

    // The shared Texture3D / Texture2DArray / CubemapArray body. The one quirk
    // between them: Texture3D's m_MipCount carries the align flag.
    static void BuildModern(List<TypeTreeNode> n, bool cubemapArray, bool mipCountAlign)
    {
        n.Add(Node(1, "string", "m_Name", -1, false, false));
        n.Add(Node(2, "Array", "Array", -1, true, true));
        n.Add(Node(3, "int", "size", 4, false, false));
        n.Add(Node(3, "char", "data", 1, false, false));
        n.Add(Node(1, "int", "m_ForcedFallbackFormat", 4, false, false));
        n.Add(Node(1, "bool", "m_DownscaleFallback", 1, false, true));
        n.Add(Node(1, "int", "m_ColorSpace", 4, false, false));
        n.Add(Node(1, "int", "m_Format", 4, false, false));
        n.Add(Node(1, "int", "m_Width", 4, false, false));

        if (cubemapArray)
        {
            n.Add(Node(1, "int", "m_CubemapCount", 4, false, false));
        }
        else
        {
            n.Add(Node(1, "int", "m_Height", 4, false, false));
            n.Add(Node(1, "int", "m_Depth", 4, false, false));
        }

        n.Add(Node(1, "int", "m_MipCount", 4, false, mipCountAlign));
        n.Add(Node(1, "unsigned int", "m_DataSize", 4, false, false));
        TextureSettings(n, 1);
        n.Add(Node(1, "bool", "m_IsReadable", 1, false, true));
        ImageDataAndStream(n, 1);
    }

    static List<TypeTreeNode> AssetBundle()
    {
        var n = new List<TypeTreeNode>
        {
            Node(0, "AssetBundle", "Base", -1, false, false),
            Node(1, "string", "m_Name", -1, false, false),
            Node(2, "Array", "Array", -1, true, true),
            Node(3, "int", "size", 4, false, false),
            Node(3, "char", "data", 1, false, false),
            // m_PreloadTable
            Node(1, "vector", "m_PreloadTable", -1, false, false),
            Node(2, "Array", "Array", -1, true, true),
            Node(3, "int", "size", 4, false, false),
            Node(3, "PPtr<Object>", "data", 12, false, false),
            Node(4, "int", "m_FileID", 4, false, false),
            Node(4, "SInt64", "m_PathID", 8, false, false),
            // m_Container: map<string, AssetInfo> — the map's Array is NOT aligned
            Node(1, "map", "m_Container", -1, false, false),
            Node(2, "Array", "Array", -1, true, false),
            Node(3, "int", "size", 4, false, false),
            Node(3, "pair", "data", -1, false, false),
            Node(4, "string", "first", -1, false, false),
            Node(5, "Array", "Array", -1, true, true),
            Node(6, "int", "size", 4, false, false),
            Node(6, "char", "data", 1, false, false),
            Node(4, "AssetInfo", "second", 20, false, false),
            Node(5, "int", "preloadIndex", 4, false, false),
            Node(5, "int", "preloadSize", 4, false, false),
            Node(5, "PPtr<Object>", "asset", 12, false, false),
            Node(6, "int", "m_FileID", 4, false, false),
            Node(6, "SInt64", "m_PathID", 8, false, false),
            // m_MainAsset
            Node(1, "AssetInfo", "m_MainAsset", 20, false, false),
            Node(2, "int", "preloadIndex", 4, false, false),
            Node(2, "int", "preloadSize", 4, false, false),
            Node(2, "PPtr<Object>", "asset", 12, false, false),
            Node(3, "int", "m_FileID", 4, false, false),
            Node(3, "SInt64", "m_PathID", 8, false, false),
            Node(1, "unsigned int", "m_RuntimeCompatibility", 4, false, false),
            // m_AssetBundleName
            Node(1, "string", "m_AssetBundleName", -1, false, false),
            Node(2, "Array", "Array", -1, true, true),
            Node(3, "int", "size", 4, false, false),
            Node(3, "char", "data", 1, false, false),
            // m_Dependencies
            Node(1, "vector", "m_Dependencies", -1, false, false),
            Node(2, "Array", "Array", -1, true, true),
            Node(3, "int", "size", 4, false, false),
            Node(3, "string", "data", -1, false, false),
            Node(4, "Array", "Array", -1, true, true),
            Node(5, "int", "size", 4, false, false),
            Node(5, "char", "data", 1, false, false),
            Node(1, "bool", "m_IsStreamedSceneAssetBundle", 1, false, true),
            Node(1, "int", "m_ExplicitDataLayout", 4, false, false),
            Node(1, "int", "m_PathFlags", 4, false, false),
            // m_SceneHashes: map<string, string> — the map's Array is NOT aligned
            Node(1, "map", "m_SceneHashes", -1, false, false),
            Node(2, "Array", "Array", -1, true, false),
            Node(3, "int", "size", 4, false, false),
            Node(3, "pair", "data", -1, false, false),
            Node(4, "string", "first", -1, false, false),
            Node(5, "Array", "Array", -1, true, true),
            Node(6, "int", "size", 4, false, false),
            Node(6, "char", "data", 1, false, false),
            Node(4, "string", "second", -1, false, false),
            Node(5, "Array", "Array", -1, true, true),
            Node(6, "int", "size", 4, false, false),
            Node(6, "char", "data", 1, false, false),
        };
        return n;
    }

    // Rebuild the hierarchical tree from the flat, level-ordered node list, the
    // same way the reader does in TypeTree.BuildTree.
    static TypeTreeTreeNode BuildTree(List<TypeTreeNode> flat)
    {
        TypeTreeTreeNode root = null;
        var stack = new Stack<TypeTreeTreeNode>();

        foreach (var node in flat)
        {
            var treeNode = new TypeTreeTreeNode(node);

            while (stack.Count > 0 && stack.Peek().Self.Level >= node.Level)
                stack.Pop();

            if (stack.Count == 0)
                root ??= treeNode;
            else
                stack.Peek().Children.Add(treeNode);

            stack.Push(treeNode);
        }

        return root;
    }

    static byte[] Hex(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; ++i)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}
