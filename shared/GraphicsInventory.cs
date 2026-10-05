using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SheepCode.Distribution;

internal sealed record PresentGraphicsDevice(string Name, uint VendorId, uint DeviceId, uint? ProblemCode, bool? DriverStarted);
internal static class GraphicsInventory
{
    [StructLayout(LayoutKind.Sequential)] private struct DeviceInfo
    { public uint Size; public Guid ClassGuid; public uint DevInst; public nint Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref DeviceInfo info);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(nint set, ref DeviceInfo info, uint property, out uint type, byte[] buffer, uint size, out uint required);
    [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);

    private static string Property(nint set, ref DeviceInfo info, uint property)
    {
        var buffer = new byte[16384];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out var type, buffer, (uint)buffer.Length, out var used) || type is not (1 or 7) || used > buffer.Length) return "";
        return Encoding.Unicode.GetString(buffer, 0, (int)used).TrimEnd('\0');
    }
    internal static PresentGraphicsDevice[] PresentDevices()
    {
        nint set = -1; var devices = new List<PresentGraphicsDevice>();
        try
        {
            var displayClass = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");
            set = SetupDiGetClassDevsW(ref displayClass, null, 0, 2); // DIGCF_PRESENT excludes unplugged and stale devices.
            if (set == -1) return [];
            for (uint i = 0; i < 64; i++)
            {
                var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref info)) break;
                var ids = Property(set, ref info, 1); var match = Regex.Match(ids, @"PCI\\VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase);
                if (!match.Success) continue; // Do not promote remote, software or virtual display adapters.
                var name = Property(set, ref info, 12); if (name.Length == 0) name = Property(set, ref info, 0);
                if (name.Length == 0) name = "Adaptador PCI " + match.Groups[1].Value + ":" + match.Groups[2].Value;
                uint? problem = null; bool? started = null;
                if (CM_Get_DevNode_Status(out var status, out var code, info.DevInst, 0) == 0)
                { problem = (status & 0x400) != 0 ? code : 0; started = (status & 8) != 0; } // DN_HAS_PROBLEM, DN_STARTED.
                devices.Add(new(name, Convert.ToUInt32(match.Groups[1].Value, 16), Convert.ToUInt32(match.Groups[2].Value, 16), problem, started));
            }
        }
        catch (Exception e) when (e is ExternalException or DllNotFoundException or EntryPointNotFoundException) { }
        finally { if (set != -1) SetupDiDestroyDeviceInfoList(set); }
        return devices.ToArray();
    }
    internal static string Normalize(string name) => Regex.Replace(name.Replace("(R)", "", StringComparison.OrdinalIgnoreCase).Replace("(TM)", "", StringComparison.OrdinalIgnoreCase), "[^a-z0-9]", "", RegexOptions.IgnoreCase).ToLowerInvariant();
    internal static GraphicsAdapter[] Merge(IReadOnlyList<GraphicsAdapter> dxgi, IReadOnlyList<PresentGraphicsDevice> present)
    {
        var results = dxgi.ToList(); var claimed = new HashSet<int>();
        foreach (var device in present)
        {
            var index = results.FindIndex(g => g.DxgiVisible && !g.Software && !claimed.Contains(g.Index) && g.VendorId == device.VendorId &&
                (g.DeviceId != 0 && g.DeviceId == device.DeviceId || Normalize(g.Name) == Normalize(device.Name)));
            if (index >= 0)
            {
                claimed.Add(results[index].Index); results[index] = results[index] with { DetectionSource = "DXGI + Windows/PnP", ProblemCode = device.ProblemCode, DriverStarted = device.DriverStarted };
            }
            else results.Add(new(-1 - results.Count, device.Name, device.VendorId, 0, false, DeviceId: device.DeviceId,
                DxgiVisible: false, DetectionSource: "Windows/PnP", ProblemCode: device.ProblemCode, DriverStarted: device.DriverStarted));
        }
        // DXGI can expose several logical adapters for one PnP card. Keep one entry
        // per present device, without collapsing two identical physical cards.
        return results.Where(g => !g.DxgiVisible || g.DetectionSource != "DXGI" || !results.Any(p =>
            p.DxgiVisible && p.DetectionSource == "DXGI + Windows/PnP" && p.VendorId == g.VendorId && p.DeviceId == g.DeviceId &&
            Normalize(p.Name) == Normalize(g.Name) && p.DedicatedBytes == g.DedicatedBytes)).ToArray();
    }
}
