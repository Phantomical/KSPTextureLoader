# KSPTextureBundler

A standalone command-line tool that packs DDS and PNG textures into a Unity asset
bundle that [KSPTextureLoader](../KSPTextureLoader) can read directly (CPU
textures streamed out of the mounted bundle, no GPU upload).

It is **not** loaded into KSP. It targets modern .NET (net10.0) and uses
[AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET) to write the bundle.

## What it produces

- A UnityFS bundle (serialized file format 17, Unity 2019.4.18f1) packed with
  **LZ4 high-compression** (`AssetBundleCompressionType.LZ4`, which AssetsTools.NET
  encodes with `LZ4HC` — the same compression Unity's own pipeline uses).
- One streamed texture object per input file. A plain DDS becomes a `Texture2D`;
  a DDS that declares a cubemap, array, or volume becomes the matching Unity object
  (`Cubemap`, `Texture2DArray`, `Texture3D`, or `CubemapArray`). Pixel data lives in
  an external `.resS` resource; the object's `m_StreamData` points at it via
  `archive:/CAB-<hash>/CAB-<hash>.resS` — the exact convention the loader resolves.
  The type-tree schemas for the non-`Texture2D` objects (which the seed bundle does
  not carry) are synthesized from an embedded AssetsTools.NET class-data package
  (`classdata/classdata.tpk`), pinned to the seed's Unity version.
- An `AssetBundle` object whose `m_Container` maps each texture's addressable name
  to its object, so textures resolve both by container path and by name.

DDS pixel data (block-compressed or uncompressed) is copied **verbatim** — the DDS
mip-chain layout already matches Unity's streamed layout, including the face order
of cubemaps (`+X,-X,+Y,-Y,+Z,-Z`) and the slice order of arrays and volumes. PNGs
are decoded to `RGBA32`.

The texture kind is detected from the DDS header: a `DDSCAPS2_CUBEMAP` (or DX10
`TEXTURECUBE`) surface with all six faces becomes a `Cubemap` (a DX10 cube array
becomes a `CubemapArray`); a DX10 `arraySize > 1` becomes a `Texture2DArray`; a
volume (`DDSD_DEPTH` / DX10 `TEXTURE3D`) becomes a `Texture3D`. The newer object
types serialize their format as a Unity `GraphicsFormat` rather than the classic
`TextureFormat`; `Texture2D` and `Cubemap` keep `m_TextureFormat`.

## Usage

```
KSPTextureBundler build   -o <output.bundle> [options] <inputs...>
KSPTextureBundler extract -o <out-dir> <bundle>
KSPTextureBundler make-seed --source <bundle> -o <seed.bundle>
```

### build

```
KSPTextureBundler build -o <output.bundle> [options] <inputs...>
```

| Option | Meaning |
| --- | --- |
| `-o, --output` | Output bundle path (required). |
| `-n, --name` | AssetBundle name (default: output file name without extension). The CAB hash is always appended to the stored identity, so names never collide. |
| `--prefix <p>` | Path prefix prepended to every container key (e.g. the GameData mod folder). |
| `--mipmap-streaming` | Enable Unity mipmap streaming (`m_StreamingMipmaps`) on every texture. |
| `--seed <bundle>` | Override the embedded type-tree seed bundle. |

Container keys are each file's path relative to the input directory it was found
under, lowercased and `/`-separated, with `--prefix` prepended.

`<inputs>` are `.dds`/`.png` files or directories (searched recursively).
Unsupported formats (e.g. BC2/DXT3, which has no classic Unity `TextureFormat`)
are skipped with a message.

**Kopernicus 4bpp/8bpp palette DDS images are decoded and converted** to the
smallest format that holds their colours losslessly: `RGB565` (fully opaque),
`RGBA4444` (needs alpha), otherwise `RGBA32`. Only colours actually referenced by
the image influence the choice.

The streamed `.resS` may be up to 4 GB (Unity's 32-bit `m_StreamData.offset`
limit); it is written through a temp file so it is never fully buffered in memory.

### extract

```
KSPTextureBundler extract -o <out-dir> <bundle>
```

Writes every `Texture2D` in the bundle out as a DDS file (the cubemap, array and
volume object types are not round-tripped by `extract`). By default it recreates
the container path tree (the inverse of `build --prefix`); `--flat` writes
each texture as `<name>.dds` directly under the output directory. Block-compressed
and standard uncompressed formats round-trip through `build`; the packed 16-bit
palette formats are expanded to `RGBA32` on the way out.

### Example: mirror the EditorExtensions Parallax bundles

```
KSPTextureBundler build \
  -o parallax-stock-planet-textures.unity3d \
  -n parallax-stock-planet-textures.unity3d \
  --prefix Parallax_StockPlanetTextures \
  GameData/Parallax_StockPlanetTextures
```

This produces container keys like
`parallax_stockplanettextures/bop/plugindata/bop_color.dds` (the input directory's
sub-path with the prefix prepended), identical to the
[EditorExtensions](../../../EditorExtensions) bundler's `addressableNames`, so
KSPTextureLoader resolves them the same way.

## Compatibility with the EditorExtensions bundler

The EditorExtensions Unity bundler (`Packages/Parallax-Editor`) and this tool
produce bundles the loader reads the same way. Matched: CAB/`.resS` naming, the
`archive:/…/….resS` stream path, the lowercased container-key path scheme, LZ4HC
compression, and the 2019.4 type trees.

Intentional differences (both are valid for the loader's raw read path):

- **No vertical flip.** The EditorExtensions postprocessor flips textures that pass
  through Unity's importer, but its own raw `TextureLoader.LoadTexture` path does
  not ("loaded the same way that parallax loads textures and do not need to be
  flipped"). This tool stores raw DDS bytes, matching that raw path.
- **PNGs are stored as `RGBA32`** (lossless), whereas the Unity importer may
  compress them to DXT1.
- Every texture is streamed to the `.resS`; the Unity pipeline inlines some.

## The type-tree seed

AssetsTools.NET writes a bundle by cloning one, so the tool ships an embedded
**seed bundle** (`seed/seed.bundle`) that carries only the `Texture2D` (28) and
`AssetBundle` (142) type-tree schema plus a 1×1 placeholder — no source content.
It was generated from a Unity 2019.4.18f1 Parallax bundle with:

```
KSPTextureBundler make-seed --source <a 2019.4 bundle with a Texture2D> -o seed/seed.bundle
```

Regenerate it from any 2019.4 bundle that contains a `Texture2D` with type trees
(e.g. an existing Parallax/OPM texture bundle) if the schema ever needs updating.
