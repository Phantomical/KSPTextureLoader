using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KSPTextureLoader.Format.Bundle;

/// <summary>
/// A value read out of serialized object data by walking a <see cref="TypeTree"/>.
/// </summary>
internal sealed class TypeTreeValue
{
    public string Type;
    public string Name;

    public long Int;
    public double Float;
    public string Str;

    public bool IsByteArray;
    public long ByteArrayOffset;
    public long ByteArrayLength;

    public Dictionary<string, TypeTreeValue> Fields;
    public List<TypeTreeValue> Elements;

    public TypeTreeValue Field(string name) =>
        Fields is not null && Fields.TryGetValue(name, out var v) ? v : null;

    public long AsInt() => Int;

    public string AsString() => Str;
}

/// <summary>Reads serialized object data into a <see cref="TypeTreeValue"/> tree.</summary>
internal static class TypeTreeReader
{
    public static TypeTreeValue Read(TypeTreeTreeNode node, EndianBinaryReader reader)
    {
        var self = node.Self;
        var value = new TypeTreeValue { Type = self.Type, Name = self.Name };

        if (self.IsArray)
        {
            ReadArray(node, reader, value);
        }
        else if (node.Children.Count == 0)
        {
            ReadPrimitive(self, reader, value);
        }
        else if (node.Children.Count == 1 && node.Children[0].Self.IsArray)
        {
            // A field whose single child is an "Array" node: vector, string or
            // TypelessData. Read the inner array and surface its result here.
            var inner = Read(node.Children[0], reader);
            value.Int = inner.Int;
            value.Str = inner.Str;
            value.IsByteArray = inner.IsByteArray;
            value.ByteArrayOffset = inner.ByteArrayOffset;
            value.ByteArrayLength = inner.ByteArrayLength;
            value.Elements = inner.Elements;
        }
        else
        {
            value.Fields = new Dictionary<string, TypeTreeValue>(node.Children.Count);
            foreach (var child in node.Children)
            {
                var cv = Read(child, reader);
                value.Fields[cv.Name] = cv;
            }
        }

        if (self.AlignBytes)
            reader.Align(4);

        return value;
    }

    static void ReadArray(TypeTreeTreeNode node, EndianBinaryReader reader, TypeTreeValue value)
    {
        if (node.Children.Count < 2)
            throw new InvalidDataException(
                $"array node \"{node.Self.Name}\" is missing size/element children"
            );

        // child[0] is the "size" int, child[1] is the element template.
        var elem = node.Children[1];
        int count = reader.ReadInt32();
        value.Int = count;
        if (count < 0)
            throw new InvalidDataException($"negative array length {count}");

        bool elemIsLeaf = elem.Children.Count == 0;
        if (elemIsLeaf && elem.Self.ByteSize == 1)
        {
            long length = count;
            if (elem.Self.Type == "char")
            {
                value.Str = Encoding.UTF8.GetString(reader.ReadBytes(count));
            }
            else
            {
                value.IsByteArray = true;
                value.ByteArrayOffset = reader.Position;
                value.ByteArrayLength = length;
                reader.Skip(length);
            }
        }
        else
        {
            value.Elements = new List<TypeTreeValue>(count < 4096 ? count : 4096);
            for (int i = 0; i < count; ++i)
                value.Elements.Add(Read(elem, reader));
        }
    }

    static void ReadPrimitive(TypeTreeNode node, EndianBinaryReader reader, TypeTreeValue value)
    {
        switch (node.Type)
        {
            case "SInt8":
                value.Int = reader.ReadSByte();
                break;
            case "UInt8":
            case "char":
                value.Int = reader.ReadByte();
                break;
            case "SInt16":
            case "short":
                value.Int = reader.ReadInt16();
                break;
            case "UInt16":
            case "unsigned short":
                value.Int = reader.ReadUInt16();
                break;
            case "SInt32":
            case "int":
                value.Int = reader.ReadInt32();
                break;
            case "UInt32":
            case "unsigned int":
            case "Type*":
                value.Int = reader.ReadUInt32();
                break;
            case "SInt64":
            case "long long":
                value.Int = reader.ReadInt64();
                break;
            case "UInt64":
            case "unsigned long long":
            case "FileSize":
                value.Int = (long)reader.ReadUInt64();
                break;
            case "float":
                value.Float = reader.ReadSingle();
                break;
            case "double":
                value.Float = reader.ReadDouble();
                break;
            case "bool":
                value.Int = reader.ReadBoolean() ? 1 : 0;
                break;
            default:
                // Unknown leaf type: consume its declared size if we can.
                if (node.ByteSize > 0)
                    reader.Skip(node.ByteSize);
                break;
        }
    }
}
