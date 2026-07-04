using System;
using System.Text;

namespace KSPTextureLoader.Format.AssetBundle;

/// <summary>
/// A growable byte-buffer writer that can emit both little-endian and
/// big-endian values, the write-side counterpart of
/// <see cref="EndianBinaryReader"/>.
/// </summary>
internal sealed class EndianBinaryWriter
{
    byte[] buffer;
    int length;

    /// <summary>
    /// When <c>true</c> multi-byte integers are written most-significant-byte
    /// first.
    /// </summary>
    public bool BigEndian;

    public EndianBinaryWriter(int initialCapacity = 4096)
    {
        if (initialCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));
        buffer = new byte[Math.Max(initialCapacity, 16)];
        length = 0;
    }

    /// <summary>The number of bytes written so far (the current write position).</summary>
    public int Length => length;

    void EnsureCapacity(int additional)
    {
        long required = (long)length + additional;
        if (required <= buffer.Length)
            return;

        long capacity = buffer.Length;
        while (capacity < required)
            capacity *= 2;
        if (capacity > int.MaxValue)
            capacity = int.MaxValue;

        Array.Resize(ref buffer, (int)capacity);
    }

    void WriteUInt64As(ulong value, int size)
    {
        EnsureCapacity(size);
        if (BigEndian)
        {
            for (int i = 0; i < size; ++i)
                buffer[length + i] = (byte)(value >> (8 * (size - 1 - i)));
        }
        else
        {
            for (int i = 0; i < size; ++i)
                buffer[length + i] = (byte)(value >> (8 * i));
        }
        length += size;
    }

    public void WriteByte(byte value)
    {
        EnsureCapacity(1);
        buffer[length++] = value;
    }

    public void WriteSByte(sbyte value) => WriteByte((byte)value);

    public void WriteBoolean(bool value) => WriteByte((byte)(value ? 1 : 0));

    public void WriteInt16(short value) => WriteUInt64As((ushort)value, 2);

    public void WriteUInt16(ushort value) => WriteUInt64As(value, 2);

    public void WriteInt32(int value) => WriteUInt64As((uint)value, 4);

    public void WriteUInt32(uint value) => WriteUInt64As(value, 4);

    public void WriteInt64(long value) => WriteUInt64As((ulong)value, 8);

    public void WriteUInt64(ulong value) => WriteUInt64As(value, 8);

    public unsafe void WriteSingle(float value) => WriteUInt32(*(uint*)&value);

    public unsafe void WriteDouble(double value) => WriteUInt64(*(ulong*)&value);

    public void WriteBytes(byte[] value)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));
        WriteBytes(value, 0, value.Length);
    }

    public void WriteBytes(byte[] value, int offset, int count)
    {
        if (value is null)
            throw new ArgumentNullException(nameof(value));
        if ((uint)offset > (uint)value.Length || (uint)count > (uint)(value.Length - offset))
            throw new ArgumentOutOfRangeException(nameof(count));

        EnsureCapacity(count);
        Buffer.BlockCopy(value, offset, buffer, length, count);
        length += count;
    }

    public unsafe void WriteBytes(byte* src, int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0)
            return;
        if (src is null)
            throw new ArgumentNullException(nameof(src));

        EnsureCapacity(count);
        fixed (byte* dst = buffer)
            Buffer.MemoryCopy(src, dst + length, buffer.Length - length, count);
        length += count;
    }

    /// <summary>Write <paramref name="count"/> zero bytes.</summary>
    public void WriteZeros(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        EnsureCapacity(count);
        Array.Clear(buffer, length, count);
        length += count;
    }

    /// <summary>
    /// Pad with zero bytes until the write position is a multiple of
    /// <paramref name="alignment"/>.
    /// </summary>
    public void Align(int alignment = 4)
    {
        int rem = length % alignment;
        if (rem != 0)
            WriteZeros(alignment - rem);
    }

    /// <summary>Write a null-terminated UTF-8 string.</summary>
    public void WriteCString(string value)
    {
        if (!string.IsNullOrEmpty(value))
            WriteBytes(Encoding.UTF8.GetBytes(value));
        WriteByte(0);
    }

    /// <summary>
    /// Write an Int32 length-prefixed string and align to 4 bytes afterwards,
    /// the way strings are stored inside serialized object data.
    /// </summary>
    public void WriteAlignedString(string value)
    {
        byte[] bytes = string.IsNullOrEmpty(value)
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(value);
        WriteInt32(bytes.Length);
        WriteBytes(bytes);
        Align(4);
    }

    /// <summary>
    /// Back-patch four bytes at an earlier position with <paramref name="value"/>,
    /// using the current endianness.
    /// </summary>
    public void PatchUInt32(int position, uint value)
    {
        if ((uint)position > (uint)(length - 4))
            throw new ArgumentOutOfRangeException(nameof(position));

        if (BigEndian)
        {
            for (int i = 0; i < 4; ++i)
                buffer[position + 3 - i] = (byte)(value >> (8 * i));
        }
        else
        {
            for (int i = 0; i < 4; ++i)
                buffer[position + i] = (byte)(value >> (8 * i));
        }
    }

    /// <summary>
    /// Back-patch eight bytes at an earlier position with <paramref name="value"/>,
    /// using the current endianness.
    /// </summary>
    public void PatchInt64(int position, long value)
    {
        if ((uint)position > (uint)(length - 8))
            throw new ArgumentOutOfRangeException(nameof(position));

        ulong v = (ulong)value;
        if (BigEndian)
        {
            for (int i = 0; i < 8; ++i)
                buffer[position + 7 - i] = (byte)(v >> (8 * i));
        }
        else
        {
            for (int i = 0; i < 8; ++i)
                buffer[position + i] = (byte)(v >> (8 * i));
        }
    }

    /// <summary>Copy the written bytes into a new, exactly-sized array.</summary>
    public byte[] ToArray()
    {
        var result = new byte[length];
        Buffer.BlockCopy(buffer, 0, result, 0, length);
        return result;
    }
}
