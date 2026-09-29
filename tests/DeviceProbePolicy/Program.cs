using LTOG.Gui.Core;

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine($"PASS: {name}");
}

Check(DeviceProbePolicy.CanProbe(false, () => false), "idle discovery allowed");
Check(!DeviceProbePolicy.CanProbe(false, () => true), "external mount blocks discovery before adoption");
Check(!DeviceProbePolicy.CanProbe(true, () => throw new Exception()), "local operation blocks without process query");
Check(!DeviceProbePolicy.CanProbe(false, () => throw new Exception()), "unknown process state fails closed");
bool mounted = true;
int probes = 0;
for (int tick = 0; tick < 12; tick++)
    if (DeviceProbePolicy.CanProbe(false, () => mounted)) probes++;
Check(probes == 0, "repeated mounted refreshes perform no probes");
mounted = false;
if (DeviceProbePolicy.CanProbe(false, () => mounted)) probes++;
Check(probes == 1, "discovery resumes after unmount");
