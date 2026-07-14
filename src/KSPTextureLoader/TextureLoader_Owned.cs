using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using KSPTextureLoader.Format;
using KSPTextureLoader.Utils;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using KopernicusPaletteType = KSPTextureLoader.Format.DDSLoader.KopernicusPaletteType;
using TextureMetadata = KSPTextureLoader.Format.DDSLoader.TextureMetadata;

namespace KSPTextureLoader;

public struct Texture2DConfig
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int MipCount { get; set; }
    public ExtendedTextureFormat Format { get; set; }
    public bool Readable { get; set; }
    public bool Linear { get; set; }

    internal void Validate()
    {
        if (Width <= 0)
            throw new ArgumentException("texture width must be greater than 0");
        if (Height <= 0)
            throw new ArgumentException("texture height must be greater than 0");
        if (MipCount < 0)
            throw new ArgumentException("texture mipmap count must be greater than or equal to 0");

        if (MipCount == 0)
            MipCount = 1 + MathUtil.FloorLog2((uint)Math.Max(Width, Height));
    }
}

public struct Texture3DConfig
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int Depth { get; set; }
    public int MipCount { get; set; }
    public ExtendedTextureFormat Format { get; set; }
    public bool Readable { get; set; }
    public bool Linear { get; set; }

    internal void Validate()
    {
        if (Width <= 0)
            throw new ArgumentException("texture width must be greater than 0");
        if (Height <= 0)
            throw new ArgumentException("texture height must be greater than 0");
        if (Depth <= 0)
            throw new ArgumentException("texture depth must be greater than 0");
        if (MipCount < 0)
            throw new ArgumentException("texture mipmap count must be greater than or equal to 0");

        if (MipCount == 0)
            MipCount = 1 + MathUtil.FloorLog2((uint)Math.Max(Width, Math.Max(Height, Depth)));
    }
}

public struct Texture2DArrayConfig
{
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>The number of elements (slices) in the array.</summary>
    public int Count { get; set; }
    public int MipCount { get; set; }
    public ExtendedTextureFormat Format { get; set; }
    public bool Readable { get; set; }
    public bool Linear { get; set; }

    internal void Validate()
    {
        if (Width <= 0)
            throw new ArgumentException("texture width must be greater than 0");
        if (Height <= 0)
            throw new ArgumentException("texture height must be greater than 0");
        if (Count <= 0)
            throw new ArgumentException("texture array must have at least one element");
        if (MipCount < 0)
            throw new ArgumentException("texture mipmap count must be greater than or equal to 0");

        if (MipCount == 0)
            MipCount = 1 + MathUtil.FloorLog2((uint)Math.Max(Width, Height));
    }
}

public struct CubemapConfig
{
    /// <summary>The edge length of each (square) cubemap face.</summary>
    public int Size { get; set; }
    public int MipCount { get; set; }
    public ExtendedTextureFormat Format { get; set; }
    public bool Readable { get; set; }
    public bool Linear { get; set; }

    internal void Validate()
    {
        if (Size <= 0)
            throw new ArgumentException("cubemap size must be greater than 0");
        if (MipCount < 0)
            throw new ArgumentException("texture mipmap count must be greater than or equal to 0");

        if (MipCount == 0)
            MipCount = 1 + MathUtil.FloorLog2((uint)Size);
    }
}

public struct CubemapArrayConfig
{
    /// <summary>The edge length of each (square) cubemap face.</summary>
    public int Size { get; set; }

    /// <summary>The number of cubemaps in the array.</summary>
    public int CubemapCount { get; set; }
    public int MipCount { get; set; }
    public ExtendedTextureFormat Format { get; set; }
    public bool Readable { get; set; }
    public bool Linear { get; set; }

    internal void Validate()
    {
        if (Size <= 0)
            throw new ArgumentException("cubemap size must be greater than 0");
        if (CubemapCount <= 0)
            throw new ArgumentException("cubemap array must have at least one cubemap");
        if (MipCount < 0)
            throw new ArgumentException("texture mipmap count must be greater than or equal to 0");

        if (MipCount == 0)
            MipCount = 1 + MathUtil.FloorLog2((uint)Size);
    }
}

partial class TextureLoader
{
    #region Texture2D
    /// <summary>
    /// Load a 2d texture from a <see cref="Texture2DConfig" /> and its data in
    /// <paramref name="data"/>.
    /// </summary>
    ///
    /// <remarks>
    /// <paramref name="data" /> must remain live until the task is complete.
    /// An easy way to ensure this is to do:
    /// <code>
    /// var task = TextureLoader.LoadOwnedTexture2D(config, data);
    /// Task.Run(() =>
    /// {
    ///     try { await task; } catch { }
    ///     data.Dispose();
    /// });
    /// </code>
    /// </remarks>
    public static unsafe TextureLoadTask<Texture2D> LoadOwnedTexture2D<TData>(
        Texture2DConfig config,
        NativeArray<TData> data
    )
        where TData : unmanaged
    {
        var bytes = new LargeNativeArray<byte>(
            (byte*)data.GetUnsafePtr(),
            (long)data.Length * sizeof(TData),
            Allocator.None
        );

        var task = Task.Run(() =>
            LoadOwnedTexture2D(config, new PixelDataSource(Task.FromResult(bytes), bytes.Length))
        );

        return new(task);
    }

    /// <summary>
    /// Load a 2d texture of type specified by <see cref="Texture2DConfig" />
    /// from a region of a file on disk.
    /// </summary>
    public static TextureLoadTask<Texture2D> LoadOwnedTexture2D(
        Texture2DConfig config,
        string path,
        long offset,
        long length
    )
    {
        var task = Task.Run(() =>
            LoadOwnedTexture2D(config, new PixelDataSource(path, offset, length))
        );

        return new(task);
    }

    static Task<Texture2D> LoadOwnedTexture2D(Texture2DConfig config, PixelDataSource source)
    {
        config.Validate();
        var (gformat, palette) = GetFormats(config.Format, config.Linear);

        var metadata = new TextureMetadata
        {
            format = gformat,
            width = config.Width,
            height = config.Height,
            depth = 1,
            arraySize = 1,
            mipCount = config.MipCount,
            type = DDSLoader.DDSTextureType.Texture2D,
            paletteType = palette,
        };

        return DoLoadOwnedTexture<Texture2D>(metadata, source, config.Readable);
    }
    #endregion

    #region Texture3D
    /// <summary>
    /// Load a 3d texture from a <see cref="Texture3DConfig" /> and its data in
    /// <paramref name="data"/>.
    /// </summary>
    ///
    /// <remarks>
    /// <paramref name="data" /> must remain live until the task is complete.
    /// An easy way to ensure this is to do:
    /// <code>
    /// var task = TextureLoader.LoadOwnedTexture3D(config, data);
    /// Task.Run(() =>
    /// {
    ///     try { await task; } catch { }
    ///     data.Dispose();
    /// });
    /// </code>
    /// </remarks>
    public static unsafe TextureLoadTask<Texture3D> LoadOwnedTexture3D<TData>(
        Texture3DConfig config,
        NativeArray<TData> data
    )
        where TData : unmanaged
    {
        var bytes = new LargeNativeArray<byte>(
            (byte*)data.GetUnsafePtr(),
            (long)data.Length * sizeof(TData),
            Allocator.None
        );

        var task = Task.Run(() =>
            LoadOwnedTexture3D(config, new PixelDataSource(Task.FromResult(bytes), bytes.Length))
        );

        return new(task);
    }

    /// <summary>
    /// Load a 3d texture of type specified by <see cref="Texture3DConfig" />
    /// from a region of a file on disk.
    /// </summary>
    public static TextureLoadTask<Texture3D> LoadOwnedTexture3D(
        Texture3DConfig config,
        string path,
        long offset,
        long length
    )
    {
        var task = Task.Run(() =>
            LoadOwnedTexture3D(config, new PixelDataSource(path, offset, length))
        );

        return new(task);
    }

    static Task<Texture3D> LoadOwnedTexture3D(Texture3DConfig config, PixelDataSource source)
    {
        config.Validate();
        var (gformat, palette) = GetFormats(config.Format, config.Linear);

        var metadata = new TextureMetadata
        {
            format = gformat,
            width = config.Width,
            height = config.Height,
            depth = config.Depth,
            arraySize = 1,
            mipCount = config.MipCount,
            type = DDSLoader.DDSTextureType.Texture3D,
            paletteType = palette,
        };

        return DoLoadOwnedTexture<Texture3D>(metadata, source, config.Readable);
    }
    #endregion

    #region Texture2DArray
    /// <summary>
    /// Load a 2d texture array from a <see cref="Texture2DArrayConfig" /> and its
    /// data in <paramref name="data"/>.
    /// </summary>
    ///
    /// <remarks>
    /// <paramref name="data" /> must remain live until the task is complete.
    /// An easy way to ensure this is to do:
    /// <code>
    /// var task = TextureLoader.LoadOwnedTexture2DArray(config, data);
    /// Task.Run(() =>
    /// {
    ///     try { await task; } catch { }
    ///     data.Dispose();
    /// });
    /// </code>
    /// </remarks>
    public static unsafe TextureLoadTask<Texture2DArray> LoadOwnedTexture2DArray<TData>(
        Texture2DArrayConfig config,
        NativeArray<TData> data
    )
        where TData : unmanaged
    {
        var bytes = new LargeNativeArray<byte>(
            (byte*)data.GetUnsafePtr(),
            (long)data.Length * sizeof(TData),
            Allocator.None
        );

        var task = Task.Run(() =>
            LoadOwnedTexture2DArray(
                config,
                new PixelDataSource(Task.FromResult(bytes), bytes.Length)
            )
        );

        return new(task);
    }

    /// <summary>
    /// Load a 2d texture array of type specified by <see cref="Texture2DArrayConfig" />
    /// from a region of a file on disk.
    /// </summary>
    public static TextureLoadTask<Texture2DArray> LoadOwnedTexture2DArray(
        Texture2DArrayConfig config,
        string path,
        long offset,
        long length
    )
    {
        var task = Task.Run(() =>
            LoadOwnedTexture2DArray(config, new PixelDataSource(path, offset, length))
        );

        return new(task);
    }

    static Task<Texture2DArray> LoadOwnedTexture2DArray(
        Texture2DArrayConfig config,
        PixelDataSource source
    )
    {
        config.Validate();
        var (gformat, palette) = GetFormats(config.Format, config.Linear);

        var metadata = new TextureMetadata
        {
            format = gformat,
            width = config.Width,
            height = config.Height,
            depth = 1,
            arraySize = config.Count,
            mipCount = config.MipCount,
            type = DDSLoader.DDSTextureType.Texture2DArray,
            paletteType = palette,
        };

        return DoLoadOwnedTexture<Texture2DArray>(metadata, source, config.Readable);
    }
    #endregion

    #region Cubemap
    /// <summary>
    /// Load a cubemap from a <see cref="CubemapConfig" /> and its data in
    /// <paramref name="data"/>.
    /// </summary>
    ///
    /// <remarks>
    /// <paramref name="data" /> must remain live until the task is complete.
    /// An easy way to ensure this is to do:
    /// <code>
    /// var task = TextureLoader.LoadOwnedCubemap(config, data);
    /// Task.Run(() =>
    /// {
    ///     try { await task; } catch { }
    ///     data.Dispose();
    /// });
    /// </code>
    /// </remarks>
    public static unsafe TextureLoadTask<Cubemap> LoadOwnedCubemap<TData>(
        CubemapConfig config,
        NativeArray<TData> data
    )
        where TData : unmanaged
    {
        var bytes = new LargeNativeArray<byte>(
            (byte*)data.GetUnsafePtr(),
            (long)data.Length * sizeof(TData),
            Allocator.None
        );

        var task = Task.Run(() =>
            LoadOwnedCubemap(config, new PixelDataSource(Task.FromResult(bytes), bytes.Length))
        );

        return new(task);
    }

    /// <summary>
    /// Load a cubemap of type specified by <see cref="CubemapConfig" />
    /// from a region of a file on disk.
    /// </summary>
    public static TextureLoadTask<Cubemap> LoadOwnedCubemap(
        CubemapConfig config,
        string path,
        long offset,
        long length
    )
    {
        var task = Task.Run(() =>
            LoadOwnedCubemap(config, new PixelDataSource(path, offset, length))
        );

        return new(task);
    }

    static Task<Cubemap> LoadOwnedCubemap(CubemapConfig config, PixelDataSource source)
    {
        config.Validate();
        var (gformat, palette) = GetFormats(config.Format, config.Linear);

        var metadata = new TextureMetadata
        {
            format = gformat,
            width = config.Size,
            height = config.Size,
            depth = 1,
            arraySize = 6,
            mipCount = config.MipCount,
            type = DDSLoader.DDSTextureType.Cubemap,
            paletteType = palette,
        };

        return DoLoadOwnedTexture<Cubemap>(metadata, source, config.Readable);
    }
    #endregion

    #region CubemapArray
    /// <summary>
    /// Load a cubemap array from a <see cref="CubemapArrayConfig" /> and its data
    /// in <paramref name="data"/>.
    /// </summary>
    ///
    /// <remarks>
    /// <paramref name="data" /> must remain live until the task is complete.
    /// An easy way to ensure this is to do:
    /// <code>
    /// var task = TextureLoader.LoadOwnedCubemapArray(config, data);
    /// Task.Run(() =>
    /// {
    ///     try { await task; } catch { }
    ///     data.Dispose();
    /// });
    /// </code>
    /// </remarks>
    public static unsafe TextureLoadTask<CubemapArray> LoadOwnedCubemapArray<TData>(
        CubemapArrayConfig config,
        NativeArray<TData> data
    )
        where TData : unmanaged
    {
        var bytes = new LargeNativeArray<byte>(
            (byte*)data.GetUnsafePtr(),
            (long)data.Length * sizeof(TData),
            Allocator.None
        );

        var task = Task.Run(() =>
            LoadOwnedCubemapArray(config, new PixelDataSource(Task.FromResult(bytes), bytes.Length))
        );

        return new(task);
    }

    /// <summary>
    /// Load a cubemap array of type specified by <see cref="CubemapArrayConfig" />
    /// from a region of a file on disk.
    /// </summary>
    public static TextureLoadTask<CubemapArray> LoadOwnedCubemapArray(
        CubemapArrayConfig config,
        string path,
        long offset,
        long length
    )
    {
        var task = Task.Run(() =>
            LoadOwnedCubemapArray(config, new PixelDataSource(path, offset, length))
        );

        return new(task);
    }

    static Task<CubemapArray> LoadOwnedCubemapArray(
        CubemapArrayConfig config,
        PixelDataSource source
    )
    {
        config.Validate();
        var (gformat, palette) = GetFormats(config.Format, config.Linear);

        var metadata = new TextureMetadata
        {
            format = gformat,
            width = config.Size,
            height = config.Size,
            depth = 1,
            arraySize = 6 * config.CubemapCount,
            mipCount = config.MipCount,
            type = DDSLoader.DDSTextureType.CubemapArray,
            paletteType = palette,
        };

        return DoLoadOwnedTexture<CubemapArray>(metadata, source, config.Readable);
    }
    #endregion

    #region Implementation

    static async Task<T> DoLoadOwnedTexture<T>(
        TextureMetadata metadata,
        PixelDataSource source,
        bool readable
    )
        where T : Texture
    {
        var options = new TextureLoadOptions
        {
            Unreadable = !readable,
            AllowImplicitConversions = false,
            Hint = TextureLoadHint.Asynchronous,
        };
        var destination = new OwnedTextureDestination<T>(
            source.FilePath ?? $"memory:{Guid.NewGuid()}"
        );

        await DDSLoader.LoadTextureFromSource<T>(destination, metadata, options, source);
        return await destination.Task;
    }

    static (GraphicsFormat, KopernicusPaletteType) GetFormats(
        ExtendedTextureFormat format,
        bool linear
    )
    {
        GraphicsFormat gformat;
        KopernicusPaletteType paletteType = KopernicusPaletteType.None;
        switch (format)
        {
            case ExtendedTextureFormat.KopernicusPalette4:
                gformat = GraphicsFormatUtility.GetGraphicsFormat(TextureFormat.RGBA32, !linear);
                paletteType = KopernicusPaletteType.Palette4;
                break;
            case ExtendedTextureFormat.KopernicusPalette8:
                gformat = GraphicsFormatUtility.GetGraphicsFormat(TextureFormat.RGBA32, !linear);
                paletteType = KopernicusPaletteType.Palette4;
                break;

            default:
                gformat = GraphicsFormatUtility.GetGraphicsFormat((TextureFormat)format, !linear);
                break;
        }

        return (gformat, paletteType);
    }

    sealed class OwnedTextureDestination<T>(string name)
        : TaskCompletionSource<T>,
            ITextureDestination
        where T : Texture
    {
        public string Path => name;

        public void SetException(ExceptionDispatchInfo ex)
        {
            TrySetException(ex.SourceException);
        }

        public void SetTexture<TTex>(
            Texture tex,
            TextureLoadOptions options,
            TextureConvertOptions setOptions = default
        )
            where TTex : Texture
        {
            if (typeof(T) != typeof(TTex))
            {
                SetException(
                    new Exception(
                        $"owned texture type {typeof(T).Name} does not match loaded texture {typeof(TTex).Name}"
                    )
                );
                return;
            }

            SetResult((T)tex);
        }
    }
    #endregion
}
