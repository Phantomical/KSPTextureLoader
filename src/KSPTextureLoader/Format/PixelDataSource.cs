using System;
using System.IO;
using System.Threading.Tasks;
using KSPTextureLoader.Utils;

namespace KSPTextureLoader.Format;

/// <summary>
/// Where a texture's pixel bytes live: a region of a file on disk, or a buffer
/// produced by a decode step. The streamed bundle path reads straight from the
/// source via <see cref="OpenStreamAsync"/> without ever materializing the
/// bytes; the direct upload paths materialize them with <see cref="TakeData"/>.
/// </summary>
///
/// <remarks>
/// The source owns a buffer-backed payload until <see cref="TakeData"/> or
/// <see cref="OpenStreamAsync"/> transfers it out; call <see cref="Release"/>
/// on failure paths to free a still-owned buffer.
/// </remarks>
internal sealed class PixelDataSource
{
    readonly string path;
    readonly long offset;
    Task<LargeNativeArray<byte>> data;

    /// <summary>The number of pixel bytes available.</summary>
    public long Length { get; }

    /// <summary>The backing file's path, or null for buffer-backed sources.</summary>
    public string FilePath => path;

    /// <summary>The offset of the pixel bytes within <see cref="FilePath"/>.</summary>
    public long FileOffset => offset;

    /// <summary>A source backed by a region of a file on disk.</summary>
    public PixelDataSource(string path, long offset, long length)
    {
        this.path = path ?? throw new ArgumentNullException(nameof(path));
        this.offset = offset;
        Length = length;
    }

    /// <summary>A source backed by an in-memory buffer (e.g. a decoded palette
    /// texture). Takes ownership of the buffer.</summary>
    public PixelDataSource(Task<LargeNativeArray<byte>> data, long length)
    {
        this.data = data ?? throw new ArgumentNullException(nameof(data));
        Length = length;
    }

    /// <summary>
    /// Take ownership of the materialized pixel bytes, starting the file read
    /// for file-backed sources. May only be called once.
    /// </summary>
    public Task<LargeNativeArray<byte>> TakeData()
    {
        if (path is not null)
            return FileLoader.ReadFileContentsAsync(
                new FileLoader.FileReadInfo
                {
                    path = path,
                    offset = offset,
                    length = Length,
                }
            );

        var task = data ?? throw new InvalidOperationException("pixel data has already been taken");
        data = null;
        return task;
    }

    /// <summary>
    /// Open a stream over the pixel bytes for <see cref="Bundle.BundleStream"/>,
    /// returning it along with the offset the pixel bytes start at within it.
    /// Ownership of the stream (and the buffer, for buffer-backed sources)
    /// passes to the caller. File-backed sources never read the pixel bytes.
    /// </summary>
    public async Task<(Stream stream, long offset)> OpenStreamAsync()
    {
        if (path is not null)
            return (File.OpenRead(path), offset);

        var array = await TakeData();
        return (new NativeArrayStream(array), 0);
    }

    /// <summary>
    /// Free a buffer this source still owns. A no-op for file-backed sources
    /// or after the payload has been transferred out.
    /// </summary>
    public void Release()
    {
        if (data is null)
            return;

        var task = data;
        data = null;
        Task.Run(async () =>
        {
            try
            {
                var array = await task;
                array.DisposeExt();
            }
            catch { }
        });
    }
}

/// <summary>
/// A read-only stream over a <see cref="LargeNativeArray{T}"/> that owns the
/// array: disposing the stream frees it. Backs buffer-sourced
/// <see cref="Bundle.BundleStream"/> payloads, which Unity keeps reading
/// from until the bundle is unloaded.
/// </summary>
internal sealed unsafe class NativeArrayStream : UnmanagedMemoryStream
{
    LargeNativeArray<byte> array;
    bool freed;

    public NativeArrayStream(LargeNativeArray<byte> array)
        : base(array.GetUnsafePtr(), array.Length)
    {
        this.array = array;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (freed)
            return;

        freed = true;
        array.DisposeExt();
    }
}
