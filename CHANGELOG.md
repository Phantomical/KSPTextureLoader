# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## Unreleased
### Added
* Added an OnComplete callback for when a texture load completes successfully.
* Added an OnError callback for when a texture load fails.

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
