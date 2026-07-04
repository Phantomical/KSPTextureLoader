using System.IO;
using KSP.Testing;
using KSPTextureLoader.Format.Bundle;

namespace KSPTextureLoaderTests.Bundle;

/// <summary>
/// Tests for <see cref="EndianBinaryReader"/>, the cursor every bundle parser
/// reads through. Covers endianness, the string encodings used by serialized
/// files, alignment and bounds checking.
/// </summary>
public unsafe class EndianBinaryReaderTests : BundleParseTestBase
{
    [TestInfo("EndianBinaryReader_LittleEndianIntegers")]
    public void TestLittleEndianIntegers()
    {
        byte[] data = [0x01, 0x02, 0x03, 0x04, 0xFF, 0xFF];
        fixed (byte* p = data)
        {
            var r = new EndianBinaryReader(p, data.Length, bigEndian: false);
            AssertEqual("u32", r.ReadUInt32(), 0x04030201u);
            AssertEqual("i16", r.ReadInt16(), (short)-1);
            AssertEqual("position", r.Position, 6L);
            AssertEqual("remaining", r.Remaining, 0L);
        }
    }

    [TestInfo("EndianBinaryReader_BigEndianIntegers")]
    public void TestBigEndianIntegers()
    {
        byte[] data = [0x01, 0x02, 0x03, 0x04, 0x00, 0x00, 0x00, 0x05];
        fixed (byte* p = data)
        {
            var r = new EndianBinaryReader(p, data.Length, bigEndian: true);
            AssertEqual("u32", r.ReadUInt32(), 0x01020304u);
            AssertEqual("i32", r.ReadInt32(), 5);
        }
    }

    [TestInfo("EndianBinaryReader_EndianToggleAndI64")]
    public void TestEndianToggleAndI64()
    {
        byte[] data = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x2A];
        fixed (byte* p = data)
        {
            var r = new EndianBinaryReader(p, data.Length, bigEndian: false);
            r.BigEndian = true;
            AssertEqual("i64", r.ReadInt64(), 42L);
        }
    }

    [TestInfo("EndianBinaryReader_Single")]
    public void TestSingle()
    {
        // 1.0f == 0x3F800000, little-endian byte order.
        byte[] data = [0x00, 0x00, 0x80, 0x3F];
        fixed (byte* p = data)
        {
            var r = new EndianBinaryReader(p, data.Length, bigEndian: false);
            AssertEqual("f32", r.ReadSingle(), 1.0f);
        }
    }

    [TestInfo("EndianBinaryReader_CString")]
    public void TestCString()
    {
        byte[] data = [(byte)'C', (byte)'A', (byte)'B', 0x00, (byte)'x'];
        fixed (byte* p = data)
        {
            var r = new EndianBinaryReader(p, data.Length, bigEndian: false);
            AssertEqual("cstring", r.ReadCString(), "CAB");
            AssertEqual("position-after-terminator", r.Position, 4L);
            AssertEqual("next-byte", r.ReadByte(), (byte)'x');
        }
    }

    [TestInfo("EndianBinaryReader_AlignedString")]
    public void TestAlignedString()
    {
        // int32 length = 3, "abc", then one pad byte to reach a 4-byte boundary,
        // then a trailing marker that must be readable right after alignment.
        byte[] data = [0x03, 0x00, 0x00, 0x00, (byte)'a', (byte)'b', (byte)'c', 0x00, 0x7B];
        fixed (byte* p = data)
        {
            var r = new EndianBinaryReader(p, data.Length, bigEndian: false);
            AssertEqual("string", r.ReadAlignedString(), "abc");
            AssertEqual("position-after-align", r.Position, 8L);
            AssertEqual("marker", r.ReadByte(), (byte)0x7B);
        }
    }

    [TestInfo("EndianBinaryReader_Align")]
    public void TestAlign()
    {
        byte[] data = new byte[16];
        fixed (byte* p = data)
        {
            var r = new EndianBinaryReader(p, data.Length, bigEndian: false);
            r.ReadByte(); // position 1
            r.Align(4);
            AssertEqual("aligned-to-4", r.Position, 4L);
            r.Align(4);
            AssertEqual("align-noop-when-aligned", r.Position, 4L);
        }
    }

    [TestInfo("EndianBinaryReader_ReadPastEndThrows")]
    public void TestReadPastEndThrows()
    {
        byte[] data = [0x01, 0x02];
        fixed (byte* p = data)
        {
            var r = new EndianBinaryReader(p, data.Length, bigEndian: false);
            AssertThrows<EndOfStreamException>("read-i32-past-end", () => r.ReadInt32());
        }
    }

    [TestInfo("EndianBinaryReader_Slice")]
    public void TestSlice()
    {
        byte[] data = [0xAA, 0x01, 0x00, 0x00, 0x00, 0xBB];
        fixed (byte* p = data)
        {
            var r = new EndianBinaryReader(p, data.Length, bigEndian: false);
            var slice = r.Slice(1, 4);
            AssertEqual("slice-length", slice.Length, 4L);
            AssertEqual("slice-value", slice.ReadInt32(), 1);
            // Slicing past the end is rejected.
            AssertThrows<System.ArgumentOutOfRangeException>(
                "slice-out-of-range",
                () => r.Slice(4, 8)
            );
        }
    }
}
