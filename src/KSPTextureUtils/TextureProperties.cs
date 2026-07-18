using KSPTextureUtils.Textures;
using Microsoft.Extensions.FileSystemGlobbing;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KSPTextureUtils;

/// <summary>
/// The per-texture properties a <c>--properties</c> file resolves to for one
/// input file. Everything keeps Unity's defaults (flags false, bilinear
/// filtering, repeat wrapping) unless an entry overrides it.
/// </summary>
internal sealed class TextureProperties
{
    /// <summary>Serialized into <c>m_IsReadable</c>: Unity keeps a CPU-side copy of
    /// the pixels so scripts can read them back.</summary>
    public bool Readable { get; init; }

    /// <summary>Serialized into <c>m_StreamingMipmaps</c> (classic Texture2D/Cubemap
    /// layout; the modern array/3D types have no such field).</summary>
    public bool MipmapStreaming { get; init; }

    /// <summary>Only meaningful when <see cref="MipmapStreaming"/> is on: higher
    /// priority keeps mips resident longer (-128..127).</summary>
    public int StreamingMipmapsPriority { get; init; }

    public TextureFilterMode Filter { get; init; } = TextureFilterMode.Bilinear;
    public TextureWrapMode WrapU { get; init; } = TextureWrapMode.Repeat;
    public TextureWrapMode WrapV { get; init; } = TextureWrapMode.Repeat;
    public TextureWrapMode WrapW { get; init; } = TextureWrapMode.Repeat;

    /// <summary>Anisotropic filtering level (0..16); 0 disables it entirely.</summary>
    public int Aniso { get; init; } = 1;

    public float MipBias { get; init; }

    /// <summary>0 = linear, 1 = sRGB; null keeps whatever the source file's decoder
    /// detected.</summary>
    public int? ColorSpace { get; init; }

    /// <summary>When true, the (2D) input is a 4x3 cross that gets repacked into a
    /// native cubemap at build time (see <see cref="CubemapPacker"/>) rather than
    /// being converted in-game.</summary>
    public bool Cubemap { get; init; }
}

/// <summary>
/// Source-generator context for the YAML model types below. YamlDotNet's static
/// generator emits reflection-free (de)serializers for every
/// <c>[YamlSerializable]</c> type registered here, so the deserializer works under
/// Native AOT (see <see cref="StaticDeserializerBuilder"/> in
/// <see cref="TexturePropertiesFile.Load"/>).
/// </summary>
[YamlStaticContext]
public partial class TextureYamlContext : StaticContext { }

/// <summary>The top-level shape of a <c>--properties</c> YAML document.</summary>
[YamlSerializable]
internal sealed class TexturePropertiesDocument
{
    public List<TexturePropertiesEntry>? Properties { get; set; }
}

/// <summary>One <c>properties[]</c> entry: a <c>files</c> glob plus the flags it sets.
/// Every flag is nullable so an unset key means "don't override".</summary>
[YamlSerializable]
internal sealed class TexturePropertiesEntry
{
    public string? Files { get; set; }
    public bool? Readable { get; set; }
    public bool? MipmapStreaming { get; set; }
    public string? Filter { get; set; }
    public string? Wrap { get; set; }
    public string? WrapU { get; set; }
    public string? WrapV { get; set; }
    public string? WrapW { get; set; }
    public int? Aniso { get; set; }
    public float? MipBias { get; set; }
    public int? StreamingMipmapsPriority { get; set; }
    public string? ColorSpace { get; set; }
    public bool? Cubemap { get; set; }
}

/// <summary>
/// A parsed <c>--properties</c> YAML file assigning per-texture flags by glob:
/// <code>
/// properties:
///   - files: 'a/*/*.dds'
///     readable: true
///     mipmapStreaming: true
///     filter: trilinear
///     wrap: clamp
/// </code>
/// Each entry's <c>files</c> glob is matched (case-insensitively) against the
/// same input-relative, forward-slash path that becomes the texture's container
/// key (before <c>--prefix</c> is applied). <c>*</c> matches within one path
/// segment, <c>**</c> spans directories, <c>?</c> matches one character. Every
/// matching entry applies in order, so later entries override earlier ones for
/// whichever flags they specify.
/// </summary>
internal sealed class TexturePropertiesFile
{
    sealed class CompiledEntry
    {
        public required string Pattern;
        public required Matcher Matcher;
        public bool? Readable;
        public bool? MipmapStreaming;
        public TextureFilterMode? Filter;
        public TextureWrapMode? WrapU;
        public TextureWrapMode? WrapV;
        public TextureWrapMode? WrapW;
        public int? Aniso;
        public float? MipBias;
        public int? StreamingMipmapsPriority;
        public int? ColorSpace;
        public bool? Cubemap;
        public int MatchCount;
    }

    readonly List<CompiledEntry> entries;

    TexturePropertiesFile(List<CompiledEntry> entries) => this.entries = entries;

    /// <summary>
    /// Parse and compile a properties file. Throws <see cref="InvalidDataException"/>
    /// on malformed YAML or invalid entries (unknown keys included, to catch typos).
    /// </summary>
    public static TexturePropertiesFile Load(string path)
    {
        var deserializer = new StaticDeserializerBuilder(new TextureYamlContext())
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        TexturePropertiesDocument? doc;
        try
        {
            using var reader = new StreamReader(path);
            doc = deserializer.Deserialize<TexturePropertiesDocument>(reader);
        }
        catch (YamlException e)
        {
            throw new InvalidDataException($"{path}: {Flatten(e)}");
        }

        var compiled = new List<CompiledEntry>();
        foreach (var (entry, index) in (doc?.Properties ?? []).Select((e, i) => (e, i)))
        {
            if (string.IsNullOrWhiteSpace(entry?.Files))
                throw new InvalidDataException(
                    $"{path}: properties[{index}] is missing the 'files' glob"
                );

            var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
            matcher.AddInclude(entry.Files.Replace('\\', '/'));

            // 'wrap' sets every axis at once; an explicit per-axis key in the same
            // entry wins over it. Which axes the sampler actually uses depends on
            // the texture type (wrapW only matters for 3D textures).
            var wrap = ParseWrap(path, index, "wrap", entry.Wrap);
            compiled.Add(
                new CompiledEntry
                {
                    Pattern = entry.Files,
                    Matcher = matcher,
                    Readable = entry.Readable,
                    MipmapStreaming = entry.MipmapStreaming,
                    Filter = ParseFilter(path, index, entry.Filter),
                    WrapU = ParseWrap(path, index, "wrapU", entry.WrapU) ?? wrap,
                    WrapV = ParseWrap(path, index, "wrapV", entry.WrapV) ?? wrap,
                    WrapW = ParseWrap(path, index, "wrapW", entry.WrapW) ?? wrap,
                    Aniso = CheckRange(path, index, "aniso", entry.Aniso, 0, 16),
                    MipBias = entry.MipBias,
                    StreamingMipmapsPriority = CheckRange(
                        path,
                        index,
                        "streamingMipmapsPriority",
                        entry.StreamingMipmapsPriority,
                        sbyte.MinValue,
                        sbyte.MaxValue
                    ),
                    ColorSpace = ParseColorSpace(path, index, entry.ColorSpace),
                    Cubemap = entry.Cubemap,
                }
            );
        }

        return new TexturePropertiesFile(compiled);
    }

    /// <summary>
    /// Resolve the flags for one input file given its input-relative,
    /// forward-slash path. Later matching entries win per flag.
    /// </summary>
    public TextureProperties Resolve(string relativePath)
    {
        bool readable = false;
        bool mipmapStreaming = false;
        var filter = TextureFilterMode.Bilinear;
        var wrapU = TextureWrapMode.Repeat;
        var wrapV = TextureWrapMode.Repeat;
        var wrapW = TextureWrapMode.Repeat;
        int aniso = 1;
        float mipBias = 0f;
        int streamingPriority = 0;
        int? colorSpace = null;
        bool cubemap = false;
        foreach (var entry in entries)
        {
            if (!entry.Matcher.Match(relativePath).HasMatches)
                continue;
            entry.MatchCount++;
            if (entry.Readable is bool r)
                readable = r;
            if (entry.MipmapStreaming is bool m)
                mipmapStreaming = m;
            if (entry.Filter is { } f)
                filter = f;
            if (entry.WrapU is { } u)
                wrapU = u;
            if (entry.WrapV is { } v)
                wrapV = v;
            if (entry.WrapW is { } w)
                wrapW = w;
            if (entry.Aniso is int a)
                aniso = a;
            if (entry.MipBias is float b)
                mipBias = b;
            if (entry.StreamingMipmapsPriority is int p)
                streamingPriority = p;
            if (entry.ColorSpace is int c)
                colorSpace = c;
            if (entry.Cubemap is bool cube)
                cubemap = cube;
        }
        return new TextureProperties
        {
            Readable = readable,
            MipmapStreaming = mipmapStreaming,
            StreamingMipmapsPriority = streamingPriority,
            Filter = filter,
            WrapU = wrapU,
            WrapV = wrapV,
            WrapW = wrapW,
            Aniso = aniso,
            MipBias = mipBias,
            ColorSpace = colorSpace,
            Cubemap = cubemap,
        };
    }

    static TextureFilterMode? ParseFilter(string path, int index, string? value)
    {
        if (value is null)
            return null;
        return value.ToLowerInvariant() switch
        {
            "point" => TextureFilterMode.Point,
            "bilinear" => TextureFilterMode.Bilinear,
            "trilinear" => TextureFilterMode.Trilinear,
            _ => throw new InvalidDataException(
                $"{path}: properties[{index}] has unknown filter '{value}' "
                    + "(expected point, bilinear or trilinear)"
            ),
        };
    }

    static TextureWrapMode? ParseWrap(string path, int index, string key, string? value)
    {
        if (value is null)
            return null;
        return value.ToLowerInvariant() switch
        {
            "repeat" => TextureWrapMode.Repeat,
            "clamp" => TextureWrapMode.Clamp,
            "mirror" => TextureWrapMode.Mirror,
            "mirroronce" => TextureWrapMode.MirrorOnce,
            _ => throw new InvalidDataException(
                $"{path}: properties[{index}] has unknown {key} '{value}' "
                    + "(expected repeat, clamp, mirror or mirrorOnce)"
            ),
        };
    }

    static int? ParseColorSpace(string path, int index, string? value)
    {
        if (value is null)
            return null;
        return value.ToLowerInvariant() switch
        {
            "linear" => 0,
            "srgb" => 1,
            _ => throw new InvalidDataException(
                $"{path}: properties[{index}] has unknown colorSpace '{value}' "
                    + "(expected linear or srgb)"
            ),
        };
    }

    static int? CheckRange(string path, int index, string key, int? value, int min, int max)
    {
        if (value is int v && (v < min || v > max))
            throw new InvalidDataException(
                $"{path}: properties[{index}] has {key} {v} out of range ({min}..{max})"
            );
        return value;
    }

    /// <summary>Globs that matched no input file (only meaningful after every input
    /// has been through <see cref="Resolve"/>); surfaced as warnings to catch typos.</summary>
    public IEnumerable<string> UnmatchedPatterns =>
        entries.Where(e => e.MatchCount == 0).Select(e => e.Pattern);

    static string Flatten(YamlException e)
    {
        // YamlDotNet nests the useful message ("no such property", duplicate key,
        // bad scalar) in the innermost exception; the outer ones just repeat the
        // source position that Message already carries.
        var inner = e.InnerException;
        while (inner?.InnerException is not null)
            inner = inner.InnerException;
        return inner is null ? e.Message : $"{e.Message}: {inner.Message}";
    }
}
