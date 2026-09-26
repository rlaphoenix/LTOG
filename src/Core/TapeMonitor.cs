namespace LTOG.Core;

/// <summary>Everything known about one drive, published whole on every change.</summary>
/// <param name="Cartridge">null = unread or unreadable</param>
/// <param name="Usage">mounted volume's size and free space; null when not mounted</param>
public sealed record DriveState(TapeDrive Drive, CartridgeInfo? Cartridge = null,
    (long Total, long Free)? Usage = null);

/// <summary>
/// The one owner of tape-drive I/O and the app's only poll loop. tape.sys raises
/// no arrival or media-change events, so this polls and publishes what changed;
/// everything else only subscribes, or asks for the next pass. Events are raised
/// on the thread that called <see cref="Start"/> (the UI thread).
/// </summary>
public sealed class TapeMonitor(IActivityLog log)
{
    // ---- publish -------------------------------------------------------------

    /// <summary>A drive appeared or its state changed (by device); null state = the drive is gone.</summary>
    public event Action<string, DriveState?>? DriveStateChanged;
    public event Action<IReadOnlyList<string>>? FreeLettersChanged;

    public IReadOnlyList<string> FreeLetters { get; private set; } = [];

    // ---- inputs --------------------------------------------------------------

    /// <summary>
    /// A mount came up (read through its volume from now on) or went away (null:
    /// the drive is fully re-read directly on a pass started now).
    /// </summary>
    public void SetMount(string device, string? letter)
    {
        if (!_drives.TryGetValue(device, out var d)) return;
        d.Letter = letter;
        d.MamReadAt = default;
        if (letter != null) return;
        d.Status = uint.MaxValue;
        Publish(d, d.State with { Usage = null });
        _ = PollNowAsync();
    }

    /// <summary>
    /// Run work that drives the device itself (mount, tool, eject). The drive isn't
    /// polled meanwhile, and is fully re-read (and published) before this returns.
    /// </summary>
    public async Task ExclusiveAsync(string device, Func<Task> work)
    {
        var d = _drives.GetValueOrDefault(device);
        if (d != null) d.Holds++;
        await _pass;   // let an in-flight read finish before handing the drive over
        try { await work(); }
        finally
        {
            if (d != null) { d.Holds--; d.Status = uint.MaxValue; }
            await PollNowAsync();
        }
    }

    /// <summary>Completes once a volume is serving at <paramref name="letter"/> ("T:"), checked every pass.</summary>
    public Task VolumeUpAsync(string letter)
    {
        if (!_volumeWaits.TryGetValue(letter, out var t))
            _volumeWaits[letter] = t = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return t.Task;
    }

    /// <summary>Wake the loop now; completes when a pass that started after this call has finished.</summary>
    public Task PollNowAsync()
    {
        var done = _passDone.Task;
        _wake.TrySetResult();
        return done;
    }

    public Task LoadOrEjectAsync(string device, bool load) => ExclusiveAsync(device, async () =>
    {
        var scope = log.Begin(LogKind.Native, $"{device}: {(load ? "Load" : "Eject")} cartridge",
            $@"PrepareTape(\\.\{device}, {(load ? "TAPE_LOAD" : "TAPE_UNLOAD")})");
        try
        {
            await Task.Run(() => { if (load) NativeTape.Load(device); else NativeTape.Eject(device); });
            scope.Line(load ? "Cartridge loaded." : "Cartridge ejected.");
            scope.Complete();
        }
        catch (Exception ex) { scope.Complete(null, ex.Message); }
    });

    // ---- polling -------------------------------------------------------------

    /// <summary>The published state plus the private polling bookkeeping behind it.</summary>
    private sealed class Drive(TapeDrive info)
    {
        public DriveState State = new(info);
        public string? Letter;                  // mounted volume
        public int Holds;                       // ExclusiveAsync in progress
        public uint Status = uint.MaxValue;     // at the last full read; MaxValue = re-read
        public DateTime MamReadAt;              // last MAM read through the mounted volume
    }

    private void Publish(Drive d, DriveState next)
    {
        if (next == d.State) return;
        d.State = next;
        DriveStateChanged?.Invoke(next.Drive.Device, next);
    }

    private readonly Dictionary<string, Drive> _drives = new();   // by device
    private readonly Dictionary<string, TaskCompletionSource> _volumeWaits = new();   // by letter
    private List<string> _present = [];
    // ponytail: passes are sequential; a slow read on one drive delays holds on the others, per-drive passes if that bites
    private Task _pass = Task.CompletedTask;   // the pass in progress (or the last one)
    private TaskCompletionSource _wake = new(), _passDone = new();

    /// <summary>
    /// The poll loop: a pass now, then every 3 s or when woken. Call once, on the UI
    /// thread. The first pass's drive scan runs before this returns, so drives exist.
    /// </summary>
    public async void Start()
    {
        while (true)
        {
            var done = _passDone;
            (_wake, _passDone) = (new(), new());
            await (_pass = PassAsync());
            done.SetResult();
            await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(3)), _wake.Task);
        }
    }

    private async Task PassAsync()
    {
        Scan();
        foreach (var d in _drives.Values.ToList()) await PollAsync(d);
    }

    /// <summary>Cheap and synchronous: awaited volumes, free letters, and the drive list when the device set changed.</summary>
    private void Scan()
    {
        foreach (var (letter, t) in _volumeWaits.ToList())
            if (Directory.Exists($@"{letter}\")) { _volumeWaits.Remove(letter); t.SetResult(); }

        var letters = NativeTape.UnusedLetters();
        if (!letters.SequenceEqual(FreeLetters)) { FreeLetters = letters; FreeLettersChanged?.Invoke(letters); }

        // re-identify drives only when the set of devices changed (opens no handle otherwise)
        var present = NativeTape.PresentDevices();
        if (present.SequenceEqual(_present)) return;
        _present = present;
        var found = NativeTape.Enumerate(log);
        // keep a mounted or busy drive: its device node can be briefly unopenable
        foreach (var dev in _drives.Where(p => p.Value.Letter == null && p.Value.Holds == 0
                     && found.All(f => f.Device != p.Key)).Select(p => p.Key).ToList())
        {
            _drives.Remove(dev);
            DriveStateChanged?.Invoke(dev, null);
        }
        foreach (var t in found.Where(t => !_drives.ContainsKey(t.Device)))
        {
            var d = _drives[t.Device] = new Drive(t);
            DriveStateChanged?.Invoke(t.Device, d.State);
        }
    }

    private async Task PollAsync(Drive d)
    {
        if (d.Holds > 0) return;
        string dev = d.State.Drive.Device;

        if (d.Letter is { } letter)
        {
            // off the UI thread: LTFS can hold statfs while it writes an index
            (long, long)? usage;
            try { usage = await Task.Run(() => { var di = new DriveInfo(letter); return (di.TotalSize, di.TotalFreeSpace); }); }
            catch { usage = null; }
            Publish(d, d.State with { Usage = usage });

            if (DateTime.UtcNow - d.MamReadAt < TimeSpan.FromSeconds(30)) return;
            d.MamReadAt = DateTime.UtcNow;
            try
            {
                var prev = d.State.Cartridge;
                if (await Task.Run(() => NativeTape.ReadMountedCartridge(letter, prev, log)) is { } c)
                    Publish(d, d.State with { Cartridge = c });
            }
            catch { }
            return;
        }

        // full read (MODE SENSE, MAM, ...) only when the drive's state changed
        uint status = await Task.Run(() => NativeTape.ProbeStatus(dev));
        if (status == d.Status) return;
        d.Status = status;
        CartridgeInfo? cart = null;
        try { cart = await Task.Run(() => NativeTape.ReadCartridgeInfo(dev, log)); }
        catch { }
        Publish(d, d.State with { Cartridge = cart });
    }
}
