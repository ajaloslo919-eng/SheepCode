using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SheepCode.Distribution;

internal sealed record PowerInfo(bool? BatteryPresent, bool? OnAc, int? ChargePercent, bool? BatterySaver, int? RemainingSeconds = null)
{
    internal static PowerInfo Unknown => new(null, null, null, null);
    internal bool OnBattery => BatteryPresent == true && OnAc == false;
    internal string Describe() => (OnBattery ? "🔋 batería" : OnAc == true ? "⚡ conectado" : "alimentación desconocida") +
        (BatteryPresent == true && ChargePercent is { } charge ? $" · {charge}%" : "") + (BatterySaver == true ? " · ahorro de Windows" : "");
}
internal static class PowerScanner
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeStatus
    { public byte Ac, Battery, Percent, Saver; public uint Lifetime, FullLifetime; }
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out NativeStatus status);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint provider, uint id, [Out] byte[]? buffer, uint length);
    internal static PowerInfo Decode(byte ac, byte battery, byte percent, byte saver, uint lifetime = uint.MaxValue) => new(
        battery == 255 ? null : (battery & 128) == 0,
        ac is 0 or 1 ? ac == 1 : null, percent <= 100 && battery != 255 && (battery & 128) == 0 ? percent : null,
        saver is 0 or 1 ? saver == 1 : null, lifetime < int.MaxValue ? (int)lifetime : null);
    internal static PowerInfo Read()
    {
        try { return GetSystemPowerStatus(out var status) ? Decode(status.Ac, status.Battery, status.Percent, status.Saver, status.Lifetime) : PowerInfo.Unknown; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return PowerInfo.Unknown; }
    }
    internal static int? ChassisType()
    {
        try
        {
            const uint provider = 0x52534d42; // 'RSMB', RawSMBIOSData: four version bytes and a DWORD length.
            var length = GetSystemFirmwareTable(provider, 0, null, 0);
            if (length is < 8 or > 1048576) return null;
            var bytes = new byte[length]; var read = GetSystemFirmwareTable(provider, 0, bytes, length);
            return read == length ? ParseChassis(bytes) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }
    internal static int? ParseChassis(byte[] bytes)
    {
        if (bytes.Length < 8) return null;
        var declared = BitConverter.ToUInt32(bytes, 4); if (declared > bytes.Length - 8) return null;
        var end = 8 + (int)declared;
        for (var offset = 8; offset + 4 <= end;)
        {
            var type = bytes[offset]; var length = bytes[offset + 1];
            if (length < 4 || offset + length > end) return null;
            if (type == 3 && length >= 6) { var chassis = bytes[offset + 5] & 0x7f; return chassis is 0 or 1 or 2 ? null : chassis; }
            if (type == 127) break;
            offset += length;
            while (offset + 1 < end && (bytes[offset] != 0 || bytes[offset + 1] != 0)) offset++;
            if (offset + 1 >= end) break;
            offset += 2;
        }
        return null;
    }
}
internal sealed record LaunchPolicy(bool Saving, string Mode, int Threads, int Context, string Devices, int GpuLayers, string Explanation)
{
    internal static bool IsSaving(string mode, PowerInfo power) => mode == "eco" || mode == "auto" && (power.OnBattery || power.BatterySaver == true);
    internal static LaunchPolicy For(RuntimeProfile profile, string mode, PowerInfo power)
    {
        var saving = IsSaving(mode, power);
        var limited = saving && profile.NativeExecutable;
        return new(saving, mode, limited ? Math.Min(profile.Threads, 4) : profile.Threads,
            limited ? Math.Min(profile.Context, 4096) : profile.Context, limited ? "none" : profile.Devices, limited ? 0 : profile.GpuLayers,
            limited ? "Ahorro: CPU, hasta 4 hilos y contexto 4096. Se aplica al cargar; no cambia el modelo ni la voz." :
            saving ? "Ahorro: prioridad de CPU reducida; Strata y la voz RX 580 conservan sus gráficas y configuración." :
            "Se usa el perfil configurado. No se modifica el plan de energía de Windows.");
    }
}
internal sealed record VerifiedGpu(string Device, string Name, long FreeBytes, long TotalBytes = 0);
internal sealed record GpuPlan(string Devices, string Description, int Layers, long BudgetBytes);
internal static class GpuPlanner
{
    internal static GpuPlan Choose(SystemHardware hardware, IReadOnlyList<VerifiedGpu> verified, long modelSize)
    {
        var physical = hardware.Gpus.Where(g => !g.Software && g.Usable).ToArray();
        var candidates = verified.Select(v => new { Probe = v, Adapter = physical.FirstOrDefault(g => GraphicsInventory.Normalize(v.Name) == GraphicsInventory.Normalize(g.Name)) })
            .Where(v => v.Adapter is not null && v.Probe.FreeBytes >= 1073741824 && (v.Adapter.DxgiVisible ||
                v.Adapter.VendorId == 0x10de && Regex.IsMatch(v.Adapter.Name, @"\b(RTX|GTX|Quadro)\b", RegexOptions.IgnoreCase) && v.Probe.TotalBytes >= 1073741824))
            .Select(v => new { v.Probe, Adapter = v.Adapter!.DxgiVisible ? v.Adapter : v.Adapter with { DedicatedBytes = v.Probe.TotalBytes } }).ToArray();
        // Hybrid laptops prefer a verified dedicated GPU; a second adapter is usually shared system RAM.
        if (hardware.Portable && candidates.Any(v => !v.Adapter!.Integrated)) candidates = candidates.Where(v => !v.Adapter!.Integrated).OrderByDescending(v => v.Adapter!.DedicatedBytes).Take(1).ToArray();
        else if (candidates.Any(v => !v.Adapter!.Integrated)) candidates = candidates.Where(v => !v.Adapter!.Integrated).ToArray();
        else candidates = candidates.Take(1).ToArray(); // Shared RAM is never summed across integrated adapters.
        long budget = 0;
        foreach (var item in candidates)
        {
            var adapter = item.Adapter!;
            var cap = adapter.Integrated ? Math.Min(adapter.SharedBytes, Math.Min(hardware.RamBytes / 4, hardware.AvailableRamBytes / 3)) : Math.Max(0, adapter.DedicatedBytes - 1073741824);
            budget += Math.Max(0, Math.Min(cap, item.Probe.FreeBytes - 1536L * 1048576));
        }
        if (budget < 256L * 1048576 || modelSize <= 0) return new("none", "CPU · sin presupuesto GPU verificado", 0, 0);
        var layers = budget >= modelSize * 1.5 ? 999 : Math.Clamp((int)Math.Floor(budget / (modelSize * 1.5) * 24), 0, 999);
        if (layers == 0) return new("none", "CPU · presupuesto GPU insuficiente", 0, 0);
        return new(string.Join(',', candidates.Select(v => v.Probe.Device)), string.Join(" + ", candidates.Select(v => v.Probe.Name)) + " / Vulkan" +
            (candidates.Any(v => v.Adapter!.Integrated) ? " · usa RAM compartida" : ""), layers, budget);
    }
}
