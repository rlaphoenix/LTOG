# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [2.0.0] - 2026-09-26

### Added

- Drive dashboard: each drive page now has Cartridge and Tape Drive sections
  showing capacity, free and used space with a usage bar, plus identity, usage
  history, physical, application and per-partition detailsread from the
  cartridge's MAM.
- An "All raw attributes" section listing every MAM attribute of the loaded
  cartridge, with multi-row selection and copying.
- A troubleshooting page, shown when no tape drive is found, that walks through
  powering on the drive, checking its connection and installing its driver,
  with a shortcut to Device Manager.
- The WinLtfs version is now printed at the start of the `ltfs`, `mkltfs`,
  `unltfs` and `ltfsck` output shown in the activity log.
- A new donation button, that when clicked, brings you to a page to support me
  and the project.

### Changed

- Redesigned the user interface:
  - The sidebar navigation pane has been replaced by a traditional top navigation
    header.
  - There's now a custom title bar, holding the new top navigation and the About
    and Settings buttons. There's also a custom font for the app name.
  - Each tape drive now has its own tab, shown only when two or more drives are
    connected, instead of all drives being stacked on one page.
  - "Eject cartridge after unmount" and "Remount at system start-up" are now
    global options in Settings instead of per-drive checkboxes.
- The bundled engine is now
  [WinLtfs v1.2.0](https://github.com/rlaphoenix/WinLtfs/releases/tag/v1.2.0).
- While a cartridge is mounted, its MAM attributes are now refreshed every 30 seconds
  through the WinLtfs engine (raw MAM IOCTL on the volume) instead of showing
  the snapshot taken before mounting. The tape device is never opened directly
  while mounted.
- When opening a new LTOG window, with a mounted tape device, it will correctly
  adopt the old abandoned `ltfs.exe` mount process, get all the cartridge details
  from the process when safe, and allow you to control the mounted tape like normal.
  This does take about 10~ seconds on startup, longer if busy, so give it time.
- General device, I/O, events, and general state/polling is now managed a lot better,
  with a lot less duplicate or over-engineered paths, less threads running at any time,
  and less device calls when idle.
- Unmounting no longer waits for the process to exit, time it if it takes too long,
  or other such checks. It now simply sends CTRL+C, logs if it fails to. The new ltfs
  process watcher checks if the ltfs process ever exits, and if it does, we consider it
  to have unmounted.
- All of the UI code have been split up from one large mega MainWindow file to a bunch
  of split files, trying to follow MVC architecture, somewhat. Easier to maintain now.
- The main app codebase folder was renamed from `/gui` to `/src`.

### Fixed

- Windows no longer creates a `$RECYCLE.BIN` folder on the mounted tape.
  This is done somewhat hacky, by simply blocking by path at the fuse/WinFsp layer.
  A full solution does not seem feasible, see
  [WinLtfs Issue #8](https://github.com/rlaphoenix/WinLtfs/issues/5)
- Stale SCSI sense data is now cleared before every command, so an old error is no
  longer mistaken for the result of a new command.
- Unmounting can no longer hit a race-condition causing LTOG to close ltfs, and
  then close itself. It could sometimes send CTRL+C to the ltfs process too fast,
  causing it to affect both the ltfs process and LTOG as they would be attached.
- If the ltfs mount process ever crashes, or gets closed by another process (or
  by task manager), it now gets detected and updates the UI state for a clean
  start so you can re-mount. It also logs this as an error for your record.
- The build command no longer has a possibility of deleting files outside of the
  repository when accidentally using `\` in odd ways.

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

[Unreleased]: https://github.com/rlaphoenix/LTOG/compare/v2.0.0...HEAD
[2.0.0]: https://github.com/rlaphoenix/LTOG/compare/v1.1.0...v2.0.0
[1.1.0]: https://github.com/rlaphoenix/LTOG/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/rlaphoenix/LTOG/releases/tag/v1.0.0
