using System;
using System.IO;
using System.Text;

namespace KSPTextureLoader.Format.AssetBundle;

/// <summary>
/// A minimal cursor over an unmanaged byte buffer that can read both
/// little-endian and big-endian values. Unity bundles mix the two: the
/// <c>UnityFS</c> container header and the <see cref="SerializedFile"/> header
/// are big-endian, while the serialized metadata and object data use the
/// endianness declared in the serialized file header (little-endian on PC).
/// </summary>
internal sealed unsafe class EndianBinaryReader
{
    readonly byte* data;
    readonly long length;

    /// <summary>
    /// When <c>true</c> multi-byte integers are read most-significant-byte
    /// first.
    /// </summary>
    public bool BigEndian;

    public long Position { get; set; }
    public long Length => length;
    public long Remaining => length - Position;

    public EndianBinaryReader(byte* data, long length, bool bigEndian = false)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        if (data is null && length != 0)
            throw new ArgumentNullException(nameof(data));

        this.data = data;
        this.length = length;
        this.BigEndian = bigEndian;
    }

    /// <summary>
    /// Create a new reader over a sub-range of this reader's buffer. The new
    /// reader starts at position 0 and inherits the current endianness.
    /// </summary>
    public EndianBinaryReader Slice(long offset, long count)
    {
        if ((ulong)offset > (ulong)length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if ((ulong)count > (ulong)(length - offset))
            throw new ArgumentOutOfRangeException(nameof(count));

        return new EndianBinaryReader(data + offset, count, BigEndian);
    }

    public byte* PointerAt(long offset)
    {
        if ((ulong)offset > (ulong)length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        return data + offset;
    }

    void CheckAvailable(long count)
    {
        if (count < 0 || count > Remaining)
            throw new EndOfStreamException(
                $"attempted to read {count} bytes at offset {Position} of a {length} byte buffer"
            );
    }

    ulong ReadBytesAsUInt64(int size)
    {
        CheckAvailable(size);
        byte* p = data + Position;
        Position += size;

        ulong value = 0;
        if (BigEndian)
        {
            for (int i = 0; i < size; ++i)
                value = (value << 8) | p[i];
        }
        else
        {
            for (int i = size - 1; i >= 0; --i)
                value = (value << 8) | p[i];
        }
        return value;
    }

    public byte ReadByte()
    {
        CheckAvailable(1);
        return data[Position++];
    }

    public sbyte ReadSByte() => (sbyte)ReadByte();

    public bool ReadBoolean() => ReadByte() != 0;

    public short ReadInt16() => (short)ReadBytesAsUInt64(2);

    public ushort ReadUInt16() => (ushort)ReadBytesAsUInt64(2);

    public int ReadInt32() => (int)ReadBytesAsUInt64(4);

    public uint ReadUInt32() => (uint)ReadBytesAsUInt64(4);

    public long ReadInt64() => (long)ReadBytesAsUInt64(8);

    public ulong ReadUInt64() => ReadBytesAsUInt64(8);

    public unsafe float ReadSingle()
    {
        uint bits = ReadUInt32();
        return *(float*)&bits;
    }

    public unsafe double ReadDouble()
    {
        ulong bits = ReadUInt64();
        return *(double*)&bits;
    }

    public byte[] ReadBytes(int count)
    {
        CheckAvailable(count);
        var result = new byte[count];
        fixed (byte* dst = result)
            Buffer.MemoryCopy(data + Position, dst, count, count);
        Position += count;
        return result;
    }

    public void Skip(long count)
    {
        CheckAvailable(count);
        Position += count;
    }

    /// <summary>
    /// Advance the position so that it is a multiple of <paramref name="alignment"/>.
    /// </summary>
    public void Align(int alignment = 4)
    {
        long rem = Position % alignment;
        if (rem != 0)
            Skip(alignment - rem);
    }

    /// <summary>
    /// Read a null-terminated ASCII string. Used by the UnityFS header and the
    /// directory node paths.
    /// </summary>
    public string ReadCString()
    {
        long start = Position;
        while (Position < length && data[Position] != 0)
            Position++;

        int count = (int)(Position - start);
        var s = Encoding.UTF8.GetString(data + start, count);

        if (Position < length)
            Position++; // consume the terminator

        return s;
    }

    /// <summary>
    /// Read an Int32 length-prefixed string and align to 4 bytes afterwards.
    /// This is how strings are stored inside serialized object data.
    /// </summary>
    public string ReadAlignedString()
    {
        int count = ReadInt32();
        if (count < 0 || count > Remaining)
            throw new InvalidDataException($"invalid string length {count}");

        var s = Encoding.UTF8.GetString(data + Position, count);
        Position += count;
        Align(4);
        return s;
    }
}
