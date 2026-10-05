using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Microsoft.Win32;

namespace SheepCode.Distribution;

internal sealed record GraphicsAdapter(int Index, string Name, uint VendorId, long DedicatedBytes, bool Software, long Luid = 0, long SharedBytes = 0,
    uint DeviceId = 0, bool DxgiVisible = true, string DetectionSource = "DXGI", uint? ProblemCode = null, bool? DriverStarted = null)
{
    // DXGI's shared memory is a limit on system RAM, not additional physical VRAM.
    internal bool Integrated => DxgiVisible && !Software && DedicatedBytes < 1073741824 && SharedBytes > 0;
    internal bool Usable => ProblemCode is null or 0 && DriverStarted != false;
    internal string Describe() => !DxgiVisible ? $"{Name} · detectada por Windows/PnP · VRAM sin verificar\n  " +
        (ProblemCode == 22 ? "Deshabilitada en Windows." : ProblemCode is > 0 ? $"Windows informa error {ProblemCode}." : DriverStarted == false ? "El controlador no está iniciado." : "No expuesta por DXGI; Vulkan debe verificarla antes de usarla.") :
        (Integrated ? $"{Name} · integrada · {SharedBytes / 1073741824d:F1} GB compartidos de RAM" :
        $"{Name} · {DedicatedBytes / 1073741824d:F1} GB de VRAM dedicada") +
        (Usable ? "" : $" · no disponible (Windows {ProblemCode?.ToString() ?? "sin controlador"})");
}
internal sealed record SystemHardware(string Os, bool X64, int Threads, string Cpu, bool Avx2,
    long RamBytes, long AvailableRamBytes, long DiskFreeBytes, string DiskRoot, GraphicsAdapter[] Gpus,
    bool Portable = false, PowerInfo? Power = null, string Chassis = "sin datos")
{
    internal double RamGiB => RamBytes / 1073741824d;
    internal string Describe() => $"{(Portable ? "💻 Portátil" : "💻 Equipo")} · {(Power ?? PowerInfo.Unknown).Describe()}\n" +
        $"{Cpu} · {Threads} hilos · AVX2 {(Avx2 ? "sí" : "no")}\n" +
        $"RAM {RamGiB:F1} GB ({AvailableRamBytes / 1073741824d:F1} GB libres) · disco {DiskFreeBytes / 1073741824d:F1} GB libres\n" +
        string.Join("\n", Gpus.Where(g => !g.Software).Select(g => g.Describe())) +
        (Portable && !Gpus.Any(g => !g.Software && g.VendorId == 0x10de) ? "\nSi esperas una NVIDIA: Windows no la enumera. Revisa su controlador y el modo de GPU del portátil y vuelve a detectar." : "");
}

internal static class HardwareScanner
{
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    {
        public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid iid, out nint factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Enumerate(nint factory, uint index, out nint adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Describe(nint adapter, nint data);
    private static GraphicsAdapter[] DxgiGraphics()
    {
        nint factory = 0; var result = new List<GraphicsAdapter>();
        try
        {
            var id = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            if (CreateDXGIFactory1(ref id, out factory) < 0) return [];
            var enumerate = Marshal.GetDelegateForFunctionPointer<Enumerate>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 12 * nint.Size));
            var data = Marshal.AllocHGlobal(1024);
            try
            {
                for (uint i = 0; i < 32; i++)
                {
                    if (enumerate(factory, i, out var adapter) < 0) break;
                    try
                    {
                        var describe = Marshal.GetDelegateForFunctionPointer<Describe>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter), 10 * nint.Size));
                        if (describe(adapter, data) >= 0) result.Add(new((int)i, Marshal.PtrToStringUni(data, 128)!.TrimEnd('\0'),
                            unchecked((uint)Marshal.ReadInt32(data, 256)), Marshal.ReadInt64(data, 272), (Marshal.ReadInt32(data, 304) & 2) != 0, Marshal.ReadInt64(data, 296), Marshal.ReadInt64(data, 288), unchecked((uint)Marshal.ReadInt32(data, 260))));
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
            finally { Marshal.FreeHGlobal(data); }
        }
        catch (Exception e) when (e is ExternalException or DllNotFoundException or EntryPointNotFoundException) { }
        finally { if (factory != 0) Marshal.Release(factory); }
        return result.GroupBy(g => g.Luid != 0 ? "luid:" + g.Luid : "index:" + g.Index).Select(g => g.First()).ToArray();
    }
    internal static GraphicsAdapter[] Graphics() => GraphicsInventory.Merge(DxgiGraphics(), GraphicsInventory.PresentDevices());
    internal static SystemHardware Scan(string destination)
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) throw new IOException("Windows no pudo informar de la memoria física.");
        var disk = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destination))!);
        using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var power = PowerScanner.Read(); var chassis = PowerScanner.ChassisType();
        var portable = chassis is 8 or 9 or 10 or 11 or 14 or 30 or 31 or 32 || chassis is null && power.BatteryPresent == true;
        return new(Environment.OSVersion.VersionString, RuntimeInformation.OSArchitecture == Architecture.X64,
            Environment.ProcessorCount, cpu?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "CPU", Avx2.IsSupported,
            (long)memory.TotalPhysical, (long)memory.AvailablePhysical, disk.AvailableFreeSpace, disk.RootDirectory.FullName, Graphics(), portable, power,
            chassis is { } type ? "SMBIOS " + type : "sin datos; batería como indicio");
    }
    internal static string SuggestedModelFolder()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SheepCode", "models");
        var disks = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).OrderByDescending(d => d.AvailableFreeSpace).ToArray();
        var current = new DriveInfo(Path.GetPathRoot(local)!);
        return disks.FirstOrDefault() is { } best && current.AvailableFreeSpace < 30L * 1073741824 && best.AvailableFreeSpace > current.AvailableFreeSpace + 30L * 1073741824
            ? Path.Combine(best.RootDirectory.FullName, "SheepCodeModels") : local;
    }
}
