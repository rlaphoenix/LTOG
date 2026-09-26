using System.Collections.ObjectModel;
using System.Diagnostics;
using LTOG.Core;

namespace LTOG;

/// <summary>
/// The drives store: one <see cref="DriveViewModel"/> per drive, kept in sync with
/// <see cref="TapeMonitor"/>, plus every action on a drive (mount, unmount,
/// eject/load, cartridge tools). Pages render the view models and call these actions;
/// they never touch the monitor or mount manager themselves.
/// </summary>
public sealed class DriveStore(Settings settings, IActivityLog log, bool envOk)
{
    public ObservableCollection<DriveViewModel> Drives { get; } = new();

    private readonly TapeMonitor _monitor = new(log);
    private bool _utilityRunning;

    /// <summary>Subscribe and start polling. The first drive scan runs before this returns.</summary>
    public void Start()
    {
        _monitor.DriveStateChanged += (dev, state) =>
        {
            var vm = Find(dev);
            if (state != null && vm != null) { vm.State = state; return; }
            if (state == null) { Drives.Remove(vm!); return; }
            vm = new DriveViewModel { State = state, GlobalEnabled = envOk && !_utilityRunning };
            vm.SetLetters(_monitor.FreeLetters);
            Drives.Add(vm);
            TryAdopt(vm);   // before its first cartridge read, which is later in the same pass
        };
        _monitor.FreeLettersChanged += letters =>
        {
            foreach (var s in Drives.Where(s => s.Phase == MountPhase.Idle)) s.SetLetters(letters);
        };
        _monitor.Start();
    }

    private DriveViewModel? Find(string device) => Drives.FirstOrDefault(s => s.Drive.Device == device);

    private void UpdateGlobalEnabled()
    {
        bool ok = envOk && !_utilityRunning;
        foreach (var s in Drives) s.GlobalEnabled = ok;
    }

    // ------------------------------------------------------------ mounting

    /// <summary>Mount the vm's cartridge on its selected letter (the caller checks one is selected).</summary>
    public async Task MountAsync(DriveViewModel vm)
    {
        var letter = vm.SelectedLetter!;
        if (settings.CaptureIndex)
            Directory.CreateDirectory(settings.WorkFolder);

        var mapping = new Mapping
        {
            Letter = letter,
            Device = vm.Drive.Device,
            Description = vm.Drive.Display,
            ReadOnly = vm.ReadOnlyChecked,
            // Reuse the cartridge identity already read from the MAM chip.
            VolumeName = vm.LastCart?.State == CartridgeState.Ltfs ? vm.LastCart.VolumeName : null,
            FormatVersion = vm.LastCart?.State == CartridgeState.Ltfs ? vm.LastCart.FormatVersion : null,
        };
        vm.Mapping = mapping;
        vm.Phase = MountPhase.Mounting;
        try
        {
            await _monitor.ExclusiveAsync(mapping.Device, async () =>
            {
                await MountManager.MountAsync(mapping, settings, log, _monitor.VolumeUpAsync(letter));
                _monitor.SetMount(mapping.Device, letter);   // inside: the re-read goes through the volume
            });
            vm.Phase = MountPhase.Mounted;
            vm.SetMountedLetter(letter);
            PersistMapping(mapping);
            WatchForExit(vm, mapping);
        }
        catch (Exception ex)
        {
            // Close the mount entry as failed (covers the volume-timeout case
            // where ltfs.exe is still alive and its Exited handler hasn't fired).
            mapping.Scope?.Complete(null, ex.Message);
            vm.Mapping = null;
            vm.Phase = MountPhase.Idle;
            vm.SetLetters(_monitor.FreeLetters);
        }
    }

    /// <summary>Unmount, or cancel a mount that's still starting. If ltfs.exe can't be signalled, the mount is kept.</summary>
    public async Task UnmountAsync(DriveViewModel vm)
    {
        if (vm.Mapping is not { } mapping) { vm.Phase = MountPhase.Idle; return; }
        var phaseBefore = vm.Phase;
        bool wasMounting = phaseBefore == MountPhase.Mounting;
        bool unmounted = false;
        vm.Phase = MountPhase.Unmounting;
        try { unmounted = await MountManager.UnmountAsync(mapping, log); }
        finally
        {
            if (!unmounted) vm.Phase = phaseBefore;
        }
        if (!unmounted) return;
        Release(vm, mapping);
        if (wasMounting) log.Note($"[{mapping.Letter}] mount cancelled");
    }

    /// <summary>The mount is gone (ltfs.exe exited): free its letter, saved entry and monitor state.</summary>
    private void Release(DriveViewModel vm, Mapping mapping)
    {
        _monitor.SetMount(mapping.Device, null);
        settings.Mappings.RemoveAll(p => p.Letter == mapping.Letter);
        settings.Save();
        settings.ApplyRunKey();
        vm.Mapping = null;
        vm.Phase = MountPhase.Idle;
        vm.SetLetters(_monitor.FreeLetters, prefer: mapping.Letter);
    }

    /// <summary>
    /// Release a mount whose ltfs.exe exits on its own (crash, ended in Task Manager, ...).
    /// An exit during Unmount is left to <see cref="UnmountAsync"/>.
    /// </summary>
    private async void WatchForExit(DriveViewModel vm, Mapping mapping)
    {
        var p = mapping.Proc!;
        try { await p.WaitForExitAsync(); }
        catch { return; }   // can't be waited on: Unmount still tidies up
        if (vm.Mapping != mapping || vm.Phase == MountPhase.Unmounting) return;
        string code;
        try { code = $"exit {p.ExitCode}"; } catch { code = "exit code unknown"; }
        log.Note($"{mapping.Letter}: ltfs.exe exited unexpectedly ({code}), the volume is no longer mounted.", isError: true);
        Release(vm, mapping);
    }

    private void PersistMapping(Mapping m)
    {
        settings.Mappings.RemoveAll(p => p.Letter == m.Letter);
        settings.Mappings.Add(new PersistedMapping
        {
            Letter = m.Letter,
            Device = m.Device,
            ReadOnly = m.ReadOnly,
        });
        settings.Save();
        settings.ApplyRunKey();
    }

    /// <summary>Take over a mount of this drive still running from a previous session.</summary>
    private void TryAdopt(DriveViewModel vm)
    {
        foreach (var p in settings.Mappings.Where(p => p.Device == vm.Drive.Device))
        {
            if (!Directory.Exists($@"{p.Letter}\")) continue;
            int? pid = MountManager.FindExternalMount(p.Device);
            if (pid == null) continue;

            Process? proc = null;
            try { proc = Process.GetProcessById(pid.Value); } catch { continue; }

            var mapping = new Mapping
            {
                Letter = p.Letter,
                Device = p.Device,
                Description = vm.Drive.Display,
                ReadOnly = p.ReadOnly,
                Pid = pid.Value,
                // The real volume label WinFsp set from -o volname.
                VolumeName = MountManager.GetVolumeLabel(p.Letter),
                Proc = proc,
            };
            vm.Mapping = mapping;
            vm.ReadOnlyChecked = p.ReadOnly;
            vm.SetMountedLetter(p.Letter);
            vm.Phase = MountPhase.Mounted;
            _monitor.SetMount(p.Device, p.Letter);
            WatchForExit(vm, mapping);
            log.Note($"Adopted existing mount on {p.Letter} ({p.Device}, pid {pid}) from a previous session.");
            return;
        }
    }

    /// <summary>Startup (--remount): mount every drive that was mounted when LTOG last ran.</summary>
    public async Task RemountPersistedAsync()
    {
        if (!settings.RemountAtStartup) return;
        foreach (var p in settings.Mappings.ToList())
        {
            var vm = Find(p.Device);
            if (vm == null || vm.Phase != MountPhase.Idle) continue;

            var free = _monitor.FreeLetters;
            if (!free.Contains(p.Letter))
            {
                log.Note($"[{p.Letter}] startup remount skipped: letter not free");
                continue;
            }
            vm.ReadOnlyChecked = p.ReadOnly;
            vm.SetLetters(free, prefer: p.Letter);
            await MountAsync(vm);
        }
    }

    // ------------------------------------------------------------ cartridge utilities

    /// <summary>Cartridge utilities need an unmounted drive; logs why not.</summary>
    public bool CanUseUtilities(DriveViewModel vm)
    {
        if (vm.Phase == MountPhase.Idle) return true;
        log.Note($"{vm.Drive.Device} is mounted - unmount it before using cartridge utilities.");
        return false;
    }

    /// <summary>Ejects the cartridge, or loads one when the drive is empty.</summary>
    public async Task EjectOrLoadAsync(DriveViewModel vm)
    {
        if (!CanUseUtilities(vm) || vm.MediaOpRunning) return;
        vm.MediaOpRunning = true;     // spinner until the new state is published
        try { await _monitor.LoadOrEjectAsync(vm.Drive.Device, vm.MediaAbsent); }
        finally { vm.MediaOpRunning = false; }
    }

    /// <summary>Run an LTFS tool (mkltfs, unltfs, ltfsck) against the vm's drive.</summary>
    public async Task RunToolAsync(DriveViewModel vm, string exe, IReadOnlyList<string> args, string what)
    {
        _utilityRunning = true;
        UpdateGlobalEnabled();
        try
        {
            // The command line, streamed output and exit code are logged by ToolRunner.
            await _monitor.ExclusiveAsync(vm.Drive.Device, () => ToolRunner.RunAsync(exe, args, log, LogKind.Tool, what));
        }
        catch (Exception ex) { log.Note($"{what} failed: {ex.Message}", isError: true); }
        finally
        {
            _utilityRunning = false;
            UpdateGlobalEnabled();
        }
    }
}
