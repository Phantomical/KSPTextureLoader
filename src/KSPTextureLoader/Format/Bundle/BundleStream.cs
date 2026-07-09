using System;
using System.IO;

namespace KSPTextureLoader.Format.Bundle;

/// <summary>
/// A read-only seekable stream presenting a complete <c>UnityFS</c> bundle to
/// <c>AssetBundle.LoadFromStreamAsync</c>: the prefix built by
/// <see cref="BundleWriter.WriteHeaderAndBlocksInfo"/> followed by the resS pixel payload
/// read from an underlying stream, so the pixel data never has to be copied
/// into a managed array.
/// </summary>
///
/// <remarks>
/// Unity reads from the stream on its internal loading thread and requires it
/// to stay open until the bundle is unloaded; <see cref="AssetBundleHandle"/>
/// disposes it after the unload. Disposing this stream disposes the payload
/// stream (and any buffer it owns).
/// </remarks>
internal sealed class BundleStream : Stream
{
    readonly byte[] prefix;
    readonly Stream payload;
    readonly long payloadOffset;
    readonly long payloadLength;
    long position;

    /// <param name="prefix">The bundle bytes before the resS payload.</param>
    /// <param name="payload">The stream holding the pixel bytes. Ownership
    /// passes to this stream.</param>
    /// <param name="payloadOffset">Where the pixel bytes start within
    /// <paramref name="payload"/> (e.g. the data offset within a DDS file).</param>
    /// <param name="payloadLength">How many pixel bytes to present.</param>
    public BundleStream(byte[] prefix, Stream payload, long payloadOffset, long payloadLength)
    {
        this.prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
        this.payload = payload ?? throw new ArgumentNullException(nameof(payload));
        this.payloadOffset = payloadOffset;
        this.payloadLength = payloadLength;

        if (!payload.CanRead || !payload.CanSeek)
            throw new ArgumentException(
                "payload stream must be readable and seekable",
                nameof(payload)
            );
        if (payloadOffset < 0 || payloadLength < 0)
            throw new ArgumentOutOfRangeException(nameof(payloadLength));
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => prefix.Length + payloadLength;

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
        int total = 0;
        while (count > 0 && position < Length)
        {
            int n;
            if (position < prefix.Length)
            {
                n = (int)Math.Min(count, prefix.Length - position);
                Array.Copy(prefix, position, buffer, offset, n);
            }
            else
            {
                long payloadPosition = payloadOffset + (position - prefix.Length);
                if (payload.Position != payloadPosition)
                    payload.Position = payloadPosition;

                n = payload.Read(buffer, offset, (int)Math.Min(count, Length - position));
                if (n <= 0)
                    throw new EndOfStreamException(
                        "bundle payload ended before the expected pixel data length"
                    );
            }

            position += n;
            offset += n;
            count -= n;
            total += n;
        }

        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => Length + offset,
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
        if (disposing)
            payload.Dispose();
    }
}
