# Keep mounted tape devices out of background discovery

## Incident and evidence

On 2026-09-29, an HP LTO-6 drive writing large RAR files through WinLtfs
showed repeated throughput dips roughly 31 seconds apart. Low-speed periods
lasted about nine seconds at 8–17 MB/s. The copy application's memory queue
still held approximately 14.5 GiB, source-wait time was zero, and no deliberate
writer pauses were recorded. Time was spent inside output write calls.

The active LTFS mount used `sync_type=close`, not periodic index synchronization.
PCIe corrected errors were also observed, but did not align one-for-one with the
dips. After the user closed the LTOG GUI, the existing mount and copy continued;
an observed interval ran steadily around 166–175 MB/s.

This is evidence implicating GUI activity, not proof that each property query
issues a physical SCSI command or that five-second polling explains the
31-second pattern. No live A/B reactivation was attempted during the backup.
Driver-level command tracing would be needed to prove that mechanism.

## Source-level defect

The five-second timer called `RefreshDrivesIfChanged` before `PollSlotsAsync`.
The former always called `NativeTape.Enumerate`, opening TAPE0 through TAPE9
and querying `IOCTL_STORAGE_QUERY_PROPERTY` on successfully opened devices.
The mounted-slot guard existed only in cartridge polling, after enumeration.
Zero-access handles still enter the device stack; they do not guarantee no
effect on streaming. The activity log deduplicates identical results, hiding
repeated queries rather than preventing them.

## Fix and tradeoffs

- Gate discovery and cartridge polling while any slot is mounting, mounted,
  unmounting, loading/ejecting, or running a utility.
- Detect any `ltfs.exe` process using process metadata only, including external
  or unadoptable mounts. Fail closed if process enumeration fails.
- At GUI startup, seed cards from persisted device identities instead of probing
  when a mount already exists; normal mount adoption can then proceed.
- Keep mounted card data cached. Stop the timer on window close.
- Resume discovery when no local operation or LTFS process remains. Clear the
  load/eject busy flag before its post-operation cartridge refresh.

This deliberately defers discovery of additional drives while any mount exists,
including read-only mounts. It does not add device-notification subscriptions,
change LTFS synchronization, increase buffering, or change explicit mount/eject
operations. Process inspection is a safety policy, not an interprocess lock:
an independently started external mount can still race an idle probe. Renamed
mount executables are not detected. A future device-notification-based discovery
path should retain the same no-probe gate.

## Verification

Hardware-free checks:

```
dotnet run --project tests/DeviceProbePolicy
dotnet build gui/LTOG.Gui.csproj -c Release -p:Platform=x64 --no-restore
```

Policy checks cover idle probing, external mounts before adoption, local busy
state, fail-closed process lookup, repeated mounted timer ticks, and resumption.
They test the shared gate, not actual IOCTL traffic. The GUI build validates the
integration. No hardware tests, remounts, installation, or running-app restarts
were performed. When safely idle, verify startup adoption with a retained mount,
idle media detection, load/eject refresh, and deferred discovery of a second drive.
