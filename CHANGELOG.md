# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## Unreleased

## v0.0.19
### Fixed
* Fixed an issue where converting certain texture formats to be readable would
  result in corrupted textures.

## v0.0.18
### Fixed
* Fixed a bug where palette DDS textures would fail to load with an exception
  an unsupported texture format.

## v0.0.17
### Fixed
* Fixed incorrect warnings about textures not being readable when loading from
  a PNG.

## v0.0.16
### Fixed
* Simplified texture2d to cubemap conversion.

## v0.0.15
### Added
* Added a function to get which asset bundles are checked for a given path.

## v0.0.14
### Changed
* Disable native texture uploads by default.

## v0.0.13
### Fixed
* Fix textures being incorrectly marked as unreadable.

## v0.0.12
### Fixed
* Fix a NRE due to a typo.

## v0.0.11
### Added
* Automatically convert unreadable textures loaded from asset bundles to be
  readable if needed by the caller.

## v0.0.10
### Added
* Added extra debug logging behind a DebugMode=true flag.
* Correctly handle readability when loading 2D textures that must be converted
  into cubemaps.

### Fixed
* Fixed a bug where we were reading a file offset using `FileStream.Position`,
  which is not valid on all platforms due to buffering.

## v0.0.9
### Changed
* Actually enable native texture uploads by default.

## v0.0.8
### Changed
* Moved the debug UI behind a `DebugMode` config options, disabled by default.
* Changed file reads to happen using jobs by default, instead of Unity's
  AsyncReadManager.
* Change the async upload buffer back to persistent by default.
* Use `Marshal.AllocHGlobal` to allocate file buffers instead of `Allocator.Persistent`.
* Enabled native texture uploading for unreadable dds textures.

### v0.0.7
### Added
* Added an OnComplete callback for when a texture load completes successfully.
* Added an OnError callback for when a texture load fails.

### Fixed
* Fixed the `AssetBundle` property not being set when the texture is loaded from
  an asset bundle.
* Avoid addon binder errors by properly depending on SharpDX dlls.
* Fixed a bug where using `TakeTexture` on a texture returned from an asset bundle
  would result in the asset bundle texture being deleted.

## v0.0.6
### Fixed
* Fixed a case where textures being unloaded before the asset bundle is would
  delete the texture out of the asset bundle.
* Fixed a NRE when warning about being unable to load an asset from the bundle.

## v0.0.5
### Fixed
* Fixed a NRE while waiting for an asset bundle load to complete.

## v0.0.4
### Fixed
* Fixed a number of bugs related to PNG loading.

### Added
* Added support for doing file reads from jobs instead of AsyncReadManager.
  This is currently disabled by default.

## v0.0.3
### Added
* Allow controlling `QualitySettings.asyncUploadPersistentBuffer` via the config.

### Fixed
* Fixed an infinite hang when synchronously loading a PNG/JPEG texture.

## v0.0.1
This is the initial release of KSPTextureLoader.
