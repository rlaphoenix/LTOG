# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- While a cartridge is mounted, its MAM attributes are now refreshed every 30 seconds
  through the WinLtfs engine (raw MAM IOCTL on the volume) instead of showing
  the snapshot taken before mounting. The tape device is never opened directly
  while mounted.

## [1.1.0] - 2026-09-22

### Added

- Advanced mount options in Settings: append-only mode (LTO-7+), index-partition
  placement rules, a support-ticket (log) folder, and a logging-verbosity level —
  passed through to the LTFS engine on new mounts.
- Visual LTFS index viewer: browse the off-tape index (`.schema`) backups of a
  volume's metadata directly in the GUI.
- LTO-9 tape support, via the updated LTFS engine.
- The log viewer now supports selecting, highlighting, and copying multiple lines
  at once.

### Changed

- The LTFS + WinFsp engine is now sourced from the
  [WinLtfs](https://github.com/rlaphoenix/WinLtfs) project and bundled as a
  pinned, checksum-verified release, rather than built from source in this
  repository. The bundled engine is now WinLtfs 1.1.1 (HPE StoreOpen 3.5.0).
- Volumes are now mounted as global drives via the Mount Manager (`\\.\X:`)
  instead of per-session drive letters (`X:`), for wider compatibility across
  different tape drive brands, firmwares, and drivers.
- LTOG now requires administrator privileges on launch so that WinFsp can mount
  through the Windows Mount Manager.
- Only a single instance of LTOG can run at a time, enforced across all sessions,
  to prevent multiple instances from conflicting over the same tape drive and
  mount state, which could corrupt an in-progress write or leave a volume in an
  inconsistent state.
- The default log directory is now under `%ProgramData%`.
- The About page now references the WinLtfs engine project.

### Removed

- The `volname` mount option has been dropped; the volume name is now controlled
  entirely by the LTFS engine.

### Fixed

- Write and index errors that occur when a file is closed are now surfaced
  instead of being silently dropped by the FUSE layer.
- The drive head position is verified after a `LOCATE`, failing on a mismatch to
  avoid reading or writing at the wrong position on the tape.
- Unmounting no longer forces an unmount; it now attempts a graceful unmount
  instead.

## [1.0.0] - 2026-06-12

### Added

- Initial release.

[Unreleased]: https://github.com/rlaphoenix/LTOG/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/rlaphoenix/LTOG/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/rlaphoenix/LTOG/releases/tag/v1.0.0
