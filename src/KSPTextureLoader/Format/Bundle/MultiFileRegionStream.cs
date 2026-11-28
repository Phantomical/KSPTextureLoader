using System;
using System.IO;

namespace KSPTextureLoader.Format.Bundle;

/// <summary>
/// A read-only seekable stream that presents an ordered list of file regions
/// as a single contiguous stream. Used to splice the pixel data of several
/// files (e.g. the six faces of a disjoint cubemap) into one bundle payload
/// without copying it into memory first.
/// </summary>
///
/// <remarks>
/// Each file is opened lazily the first time it is read from. Disposing the
/// stream closes every file that was opened.
/// </remarks>
internal sealed class MultiFileRegionStream : Stream
{
    internal readonly struct Region(string path, long offset, long length)
    {
        public readonly string Path = path;
        public readonly long Offset = offset;
        public readonly long Length = length;
    }

    readonly Region[] regions;
    readonly long[] starts;
    readonly FileStream[] files;
    readonly long length;
    long position;

    public MultiFileRegionStream(Region[] regions)
    {
        this.regions = regions ?? throw new ArgumentNullException(nameof(regions));
        starts = new long[regions.Length];
        files = new FileStream[regions.Length];

        long total = 0;
        for (int i = 0; i < regions.Length; ++i)
        {
            if (regions[i].Path is null)
                throw new ArgumentException("region path cannot be null", nameof(regions));
            if (regions[i].Offset < 0 || regions[i].Length < 0)
                throw new ArgumentOutOfRangeException(nameof(regions));

            starts[i] = total;
            total += regions[i].Length;
        }

        length = total;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => position;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (buffer is null)
            throw new ArgumentNullException(nameof(buffer));
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count));

        int total = 0;
        while (count > 0 && position < length)
        {
            int index = FindRegion(position);
            ref readonly var region = ref regions[index];
            long within = position - starts[index];
            int want = (int)Math.Min(count, region.Length - within);

            var file = GetFile(index);
            long filePosition = region.Offset + within;
            if (file.Position != filePosition)
                file.Position = filePosition;

            int n = file.Read(buffer, offset, want);
            if (n <= 0)
                throw new EndOfStreamException(
                    $"file {region.Path} ended before the expected end of its region"
                );

            position += n;
            offset += n;
            count -= n;
            total += n;
        }

        return total;
    }

    int FindRegion(long pos)
    {
        // There are only ever a handful of regions, so a linear scan is fine.
        // Zero-length regions are skipped naturally since their end == start.
        for (int i = 0; i < regions.Length; ++i)
        {
            if (pos < starts[i] + regions[i].Length)
                return i;
        }

        throw new InvalidOperationException("position is past the end of the stream");
    }

    FileStream GetFile(int index)
    {
        return files[index] ??= new FileStream(
            regions[index].Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.SequentialScan
        );
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (target < 0)
            throw new IOException("cannot seek before the beginning of the stream");

        return position = target;
    }

    public override void Flush() { }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;

        for (int i = 0; i < files.Length; ++i)
        {
            files[i]?.Dispose();
            files[i] = null;
        }
    }
}
