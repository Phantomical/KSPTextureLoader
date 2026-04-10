# Native Texture & SRV Ownership in CreateExternalTexture and UpdateExternalTexture

How Unity (2019.4.18f1, D3D11) manages ownership of native D3D11 objects passed
to `Texture2D.CreateExternalTexture` and `Texture2D.UpdateExternalTexture`,
based on reverse engineering the native implementations in `UnityPlayer.dll`.

## Registration

Both `CreateExternalTexture` and `UpdateExternalTexture` funnel into the same
native function: `GfxDeviceD3D11Base::RegisterNativeTexture`, which calls
`TexturesD3D11Base::RegisterNativeTexture`.

This function probes the incoming pointer via `QueryInterface` to determine
whether it is an `ID3D11ShaderResourceView` or an `ID3D11Texture2D`.

### When the input is an ID3D11ShaderResourceView*

1. `QueryInterface(IID_ID3D11ShaderResourceView, &srv)` -- **AddRefs the SRV**
2. `srv->GetResource(&resource)` / `resource->Release()` -- net 0 on resource
3. `resource->QueryInterface(IID_ID3D11Texture2D, &tex)` / `tex->Release()` -- net 0 on texture
4. Both raw pointers stored in an internal record

**Net result: SRV is AddRef'd +1. Texture is not AddRef'd** (the stored
pointer is borrowed -- kept alive by the SRV's internal reference to its
resource).

### When the input is an ID3D11Texture2D*

1. `QueryInterface(IID_ID3D11Texture2D, &tex)` / `tex->Release()` -- net 0
2. Unity creates a **new SRV** from the texture (refcount 1, owned by Unity)
3. Both raw pointers stored in an internal record

**Net result: Texture is not AddRef'd. Unity creates and owns a new SRV.**

## Destruction

Unity has two destruction paths, selected based on internal flags:

### PATH 1: UnregisterNativeTexture

**When:** `kTextureInitIsNativeTexture` is set in `m_InitFlags`.

This is the path for textures created via `CreateExternalTexture` (i.e.
`Internal_Create` with a non-null `nativeTex`).

- **Releases the SRV** (balances the QI AddRef from registration)
- **Does NOT release the texture resource**
- Frees the internal record

### PATH 2: DeleteTexture

**When:** The texture was created by Unity normally (owns its gfx resources).

This is the path for textures where `UpdateExternalTexture` was called after
normal creation, because **UpdateExternalTexture does not set
`kTextureInitIsNativeTexture`** on the Texture2D object.

- **Releases the SRV** (balances the QI AddRef from registration)
- **Releases the texture resource** (even though registration never AddRef'd it)
- Frees the internal record

## Key Asymmetry

`CreateExternalTexture` and `UpdateExternalTexture` register the native pointer
identically (same AddRef behavior), but the texture is destroyed differently:

| API                      | Sets kTextureInitIsNativeTexture | Destroy path             | Releases texture resource |
|--------------------------|:-------------------------------:|--------------------------|:-------------------------:|
| `CreateExternalTexture`  | Yes                             | UnregisterNativeTexture  | No                        |
| `UpdateExternalTexture`  | No                              | DeleteTexture            | **Yes**                   |

Since neither path AddRefs the texture resource during registration, but
`DeleteTexture` releases it on destroy, **callers of `UpdateExternalTexture`
must manually AddRef the texture resource** to compensate for the Release that
Unity will perform on destruction.

Callers of `CreateExternalTexture` do not need to do this.

## The UpdateExternalTexture Overwrite Leak

When `RegisterNativeTexture` stores a new record in the internal texture map, it
**unconditionally overwrites** the old entry. The previous record (and its held
SRV reference) is orphaned -- no Release or free is performed on the old entry.

Unity has a separate internal `UpdateNativeTexture` function that properly
updates an existing entry in-place, but it is **not called** from the
`UpdateExternalTexture` C# scripting API.

This means each call to `UpdateExternalTexture` leaks one SRV reference and one
pool allocation from the previous entry. For a texture that is only updated
once, this is bounded. Calling it repeatedly on the same texture would leak
continuously.
