using System.Collections.Generic;

namespace KSPTextureLoader.Format.Bundle;

/// <summary>
/// A mutable value supplied to <see cref="SerializedFileWriter"/> when emitting
/// an object; the write-side counterpart of <see cref="TypeTreeValue"/>.
/// </summary>
internal sealed class SerializedValue
{
    /// <summary>Backing value for integer and boolean leaves.</summary>
    public long Int;

    /// <summary>Backing value for <c>float</c> / <c>double</c> leaves.</summary>
    public double Float;

    /// <summary>Backing value for <c>string</c> fields (a <c>char</c> array).</summary>
    public string Str;

    /// <summary>Backing value for <c>TypelessData</c> / <c>UInt8</c> byte arrays.</summary>
    public byte[] Bytes;

    /// <summary>Child fields of a struct, keyed by field name.</summary>
    public Dictionary<string, SerializedValue> Fields;

    /// <summary>Elements of a non-byte array (<c>vector</c> / <c>map</c>).</summary>
    public List<SerializedValue> Elements;

    public static SerializedValue Struct() =>
        new() { Fields = new Dictionary<string, SerializedValue>() };

    public static SerializedValue OfInt(long value) => new() { Int = value };

    public static SerializedValue OfFloat(double value) => new() { Float = value };

    public static SerializedValue OfBool(bool value) => new() { Int = value ? 1 : 0 };

    public static SerializedValue OfString(string value) => new() { Str = value ?? "" };

    public static SerializedValue OfBytes(byte[] value) => new() { Bytes = value ?? [] };

    public static SerializedValue Array() => new() { Elements = [] };

    /// <summary>Add or replace a named child field (the value must be a struct).</summary>
    public SerializedValue Set(string name, SerializedValue value)
    {
        Fields[name] = value;
        return this;
    }

    public SerializedValue SetInt(string name, long value) => Set(name, OfInt(value));

    public SerializedValue SetBool(string name, bool value) => Set(name, OfBool(value));

    public SerializedValue SetFloat(string name, double value) => Set(name, OfFloat(value));

    public SerializedValue SetString(string name, string value) => Set(name, OfString(value));

    public SerializedValue SetBytes(string name, byte[] value) => Set(name, OfBytes(value));
}
