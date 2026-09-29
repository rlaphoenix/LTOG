namespace LTOG.Gui.Core;

/// <summary>
/// Device discovery is not passive: even a zero-access open and property IOCTL
/// enter the tape driver. Keep background probes out of a mount's lifetime.
/// Discovery of other drives is deliberately deferred until all mounts stop.
/// </summary>
public static class DeviceProbePolicy
{
    public static bool CanProbe(bool localOperationActive, Func<bool> mountProcessExists)
    {
        if (localOperationActive) return false;
        try { return !mountProcessExists(); }
        catch { return false; } // Cannot establish idleness: preserve the device.
    }
}
