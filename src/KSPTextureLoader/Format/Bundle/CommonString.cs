using System.Text;

namespace KSPTextureLoader.Format.Bundle;

/// <summary>
/// The built-in Unity "common string" buffer. Type tree node names and types
/// are stored as offsets into either the per-type local string buffer or, when
/// the high bit of the offset is set, into this shared buffer of well-known
/// identifiers.
/// </summary>
///
/// <remarks>
/// The exact ordering defines the offsets, so it must match the engine's table
/// byte-for-byte. This is the stable list used across Unity 5.x through 2021.x.
/// </remarks>
internal static class CommonString
{
    static readonly string[] Entries =
    [
        "AABB",
        "AnimationClip",
        "AnimationCurve",
        "AnimationState",
        "Array",
        "Base",
        "BitField",
        "bitset",
        "bool",
        "char",
        "ColorRGBA",
        "Component",
        "data",
        "deque",
        "double",
        "dynamic_array",
        "FastPropertyName",
        "first",
        "float",
        "Font",
        "GameObject",
        "Generic Mono",
        "GradientNEW",
        "GUID",
        "GUIStyle",
        "int",
        "list",
        "long long",
        "map",
        "Matrix4x4f",
        "MdFour",
        "MonoBehaviour",
        "MonoScript",
        "m_ByteSize",
        "m_Curve",
        "m_EditorClassIdentifier",
        "m_EditorHideFlags",
        "m_Enabled",
        "m_ExtensionPtr",
        "m_GameObject",
        "m_Index",
        "m_IsArray",
        "m_IsStatic",
        "m_MetaFlag",
        "m_Name",
        "m_ObjectHideFlags",
        "m_PrefabInternal",
        "m_PrefabParentObject",
        "m_Script",
        "m_StaticEditorFlags",
        "m_Type",
        "m_Version",
        "Object",
        "pair",
        "PPtr<Component>",
        "PPtr<GameObject>",
        "PPtr<Material>",
        "PPtr<MonoBehaviour>",
        "PPtr<MonoScript>",
        "PPtr<Object>",
        "PPtr<Prefab>",
        "PPtr<Sprite>",
        "PPtr<TextAsset>",
        "PPtr<Texture>",
        "PPtr<Texture2D>",
        "PPtr<Transform>",
        "Prefab",
        "Quaternionf",
        "Rectf",
        "RectInt",
        "RectOffset",
        "second",
        "set",
        "short",
        "size",
        "SInt16",
        "SInt32",
        "SInt64",
        "SInt8",
        "staticvector",
        "string",
        "TextAsset",
        "TextMesh",
        "Texture",
        "Texture2D",
        "Transform",
        "TypelessData",
        "UInt16",
        "UInt32",
        "UInt64",
        "UInt8",
        "unsigned int",
        "unsigned long long",
        "unsigned short",
        "vector",
        "Vector2f",
        "Vector3f",
        "Vector4f",
        "m_ScriptingClassIdentifier",
        "Gradient",
        "Type*",
        "int2_storage",
        "int3_storage",
        "BoundsInt",
        "m_CorrespondingSourceObject",
        "m_PrefabInstance",
        "m_PrefabAsset",
        "FileSize",
        "Hash128",
    ];

    /// <summary>
    /// The concatenation of every entry followed by a null terminator. Offsets
    /// with the high bit cleared index directly into this buffer.
    /// </summary>
    public static readonly byte[] Buffer = BuildBuffer();

    static byte[] BuildBuffer()
    {
        var sb = new StringBuilder();
        foreach (var entry in Entries)
        {
            sb.Append(entry);
            sb.Append('\0');
        }

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Read a null-terminated string from a buffer starting at <paramref name="offset"/>.
    /// </summary>
    public static string ReadAt(byte[] buffer, uint offset)
    {
        int start = (int)offset;
        if (start < 0 || start >= buffer.Length)
            return $"(invalid string @ {offset})";

        int end = start;
        while (end < buffer.Length && buffer[end] != 0)
            end++;

        return Encoding.ASCII.GetString(buffer, start, end - start);
    }
}
