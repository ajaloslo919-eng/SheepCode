namespace SheepCode.Distribution;

internal static class PortableChecks
{
    internal static IReadOnlyList<object> Run()
    {
        static void Assert(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
        const long gib = 1073741824; var rows = new List<object>();
        var ac = new PowerInfo(true, true, 92, false); var battery = new PowerInfo(true, false, 38, false);
        foreach (var (ram, expected) in new[] { (4, "qwen3-0.6b"), (8, "qwen3-1.7b"), (16, "qwen3-8b"), (32, "qwen3-14b") })
        {
            var hardware = new SystemHardware("fixture", true, 8, "Laptop CPU", true, ram * gib, ram * gib / 2, 200 * gib, "C:\\",
                [new(0, "Intel(R) Iris(R) Xe Graphics", 0x8086, 128L * 1048576, false, SharedBytes: ram * gib / 2)], true, ac, "SMBIOS 10");
            var plan = ModelCatalog.Recommend(hardware);
            Assert(plan.Id == expected && plan.Context == 4096, "Perfil portátil incorrecto con " + ram + " GB.");
            if (ram == 4) Assert(plan.Kind == "strata-cpu", "El equipo de 4 GB no recibió el motor nativo Strata CPU.");
            Assert(hardware.Gpus[0].Integrated, "La GPU integrada se trató como VRAM adicional.");
            if (ram >= 16) Assert(ModelCatalog.Recommend(hardware with { Power = battery }).Id == "qwen3-4b", "La batería no recibió un perfil pequeño.");
            rows.Add(new { check = "portable-model-capacity", ram, selected = plan.Id, context = plan.Context });
        }
        var hybrid = new SystemHardware("fixture", true, 16, "Laptop CPU", true, 32 * gib, 24 * gib, 200 * gib, "C:\\",
            [new(0, "Intel Iris Xe Graphics", 0x8086, 128L * 1048576, false, SharedBytes: 16 * gib), new(1, "NVIDIA GeForce RTX 4060 Laptop GPU", 0x10de, 8 * gib, false, SharedBytes: 16 * gib)], true, ac);
        var celeron = ModelCatalog.Recommend(hybrid with { RamBytes = 4 * gib, Threads = 2, Avx2 = false, Cpu = "Celeron" });
        Assert(celeron.Kind == "strata-cpu" && celeron.Id == "qwen3-0.6b", "Celeron sin AVX2 no seleccionó el perfil pequeño.");
        var coder = ModelCatalog.LowMemory("qwen2.5-coder-0.5b");
        Assert(coder.Kind == "strata-cpu" && coder.Context == 4096 && coder.Model?.File.EndsWith("q8_0.gguf") == true && coder.DownloadBytes == 675710848,
            "El perfil Coder no usa el activo fijado o los límites de Strata CPU.");
        Assert(!ModelCatalog.CpuSupported("qwen3-1.7b"), "Un modelo grande entró en el perfil limitado.");
        var granite = ModelCatalog.LowMemory("granite-4.0-h-350m");
        Assert(granite.Kind == "strata-cpu" && granite.Context == 4096 && granite.DownloadBytes == 366195616 &&
            granite.Model?.Sha256 == "c7d9873640dc303b6773dcc44e72e5bdf533e1c95ca8421e6191fbff5c94c942" &&
            granite.Model.Repository == "ibm-granite/granite-4.0-h-350m-GGUF" && granite.Explanation.Contains("1536 MiB"),
            "Granite no usa el activo oficial fijado o los límites de Strata CPU.");
        rows.Add(new { check = "granite-pinned-cpu-profile", selected = granite.Id, context = granite.Context, bytes = granite.DownloadBytes });
        var graniteLarger = ModelCatalog.LowMemory("granite-4.0-h-1b");
        Assert(graniteLarger.Kind == "strata-cpu" && graniteLarger.Context == 4096 && graniteLarger.DownloadBytes == 901162208 &&
            graniteLarger.Model?.Sha256 == "da3d737121a96f3c9a316685212376257a7f167b74380855666dd488d6af3bcb" &&
            graniteLarger.Model.File == "granite-4.0-h-1b-Q4_K_M.gguf" && graniteLarger.Label.Contains("experimental"),
            "El Granite de 1,5B no conserva el activo Q4 verificado y sus límites.");
        rows.Add(new { check = "granite-h1b-pinned-cpu-profile", selected = graniteLarger.Id, context = graniteLarger.Context, bytes = graniteLarger.DownloadBytes });
        var probes = new[] { new VerifiedGpu("Vulkan0", "Intel Iris Xe Graphics", 16 * gib), new VerifiedGpu("Vulkan1", "NVIDIA GeForce RTX 4060 Laptop GPU", 7 * gib) };
        var chosen = GpuPlanner.Choose(hybrid, probes, 5 * gib);
        Assert(chosen.Devices == "Vulkan1" && chosen.BudgetBytes <= 7 * gib, "Se sumó la RAM integrada al presupuesto de la GPU portátil.");
        var integrated = GpuPlanner.Choose(hybrid with { Gpus = [hybrid.Gpus[0]] }, probes, 2 * gib);
        Assert(integrated.Devices == "Vulkan0" && integrated.BudgetBytes <= 8 * gib, "No se limitó la RAM compartida.");
        var desktop = hybrid with { Portable = false, Gpus = [new(0, "NVIDIA GeForce RTX 2060 SUPER", 0x10de, 8 * gib, false), new(1, "AMD Radeon RX 580 2048SP", 0x1002, 8 * gib, false)] };
        var dual = GpuPlanner.Choose(desktop, [new("Vulkan0", desktop.Gpus[0].Name, 7 * gib), new("Vulkan1", desktop.Gpus[1].Name, 7 * gib)], 5 * gib);
        Assert(dual.Devices == "Vulkan0,Vulkan1", "Se perdió el soporte de dos GPU de escritorio.");
        Assert(GpuPlanner.Choose(hybrid, [new("Vulkan2", "Unknown GPU", 20 * gib)], gib).Devices == "none", "Se aceptó una GPU sin identidad física verificada.");
        rows.Add(new { check = "hybrid-integrated-desktop-dual-and-unknown-gpu", dedicated = chosen, shared = integrated, desktop = dual, passed = true });
        var intelOnly = hybrid.Gpus[0] with { DeviceId = 0x7d55 };
        var present = new[] { new PresentGraphicsDevice(intelOnly.Name, 0x8086, 0x7d55, 0, true), new PresentGraphicsDevice(hybrid.Gpus[1].Name, 0x10de, 0x28a0, 0, true) };
        var merged = GraphicsInventory.Merge([intelOnly], present);
        Assert(merged.Length == 2 && merged[0].DxgiVisible && !merged[1].DxgiVisible && merged[1].DedicatedBytes == 0 && merged[1].Describe().Contains("sin verificar"), "Se perdió la RTX presente en Windows o se inventó su VRAM.");
        Assert(GpuPlanner.Choose(hybrid with { Gpus = merged }, [new("Vulkan1", hybrid.Gpus[1].Name, 7 * gib)], 5 * gib).Devices == "none", "Se aceptó VRAM sin verificar.");
        var pnpVulkan = GpuPlanner.Choose(hybrid with { Gpus = merged }, [new("Vulkan1", hybrid.Gpus[1].Name, 7 * gib, 8 * gib)], 5 * gib);
        Assert(pnpVulkan.Devices == "Vulkan1" && pnpVulkan.BudgetBytes <= 7 * gib, "La RTX registrada por Windows y verificada por Vulkan no se pudo usar.");
        var disabled = GraphicsInventory.Merge([], [present[1] with { ProblemCode = 22, DriverStarted = false }]);
        Assert(disabled[0].Describe().Contains("Deshabilitada") && GpuPlanner.Choose(hybrid with { Gpus = disabled }, [new("Vulkan1", present[1].Name, 7 * gib, 8 * gib)], gib).Devices == "none", "Se usó una RTX deshabilitada.");
        Assert(GraphicsInventory.Merge([hybrid.Gpus[1] with { DeviceId = 0x28a0 }], [present[1]]).Length == 1, "Se duplicó la misma GPU DXGI/PnP.");
        var sameCard = hybrid.Gpus[1] with { DeviceId = 0x28a0, Luid = 1 }; var logicalCopy = sameCard with { Index = 2, Luid = 2 };
        Assert(GraphicsInventory.Merge([sameCard, logicalCopy], [present[1]]).Length == 1 && GraphicsInventory.Merge([sameCard, logicalCopy], [present[1], present[1]]).Length == 2,
            "Se duplicó una tarjeta lógica o se ocultó una segunda tarjeta física idéntica.");
        rows.Add(new { check = "dxgi-pnp-hybrid-inventory-unknown-vram-disabled-driver-and-vulkan-verification", passed = true, detected = merged, verifiedGpu = pnpVulkan });
        var profile = new RuntimeProfile { Kind = "llama", Threads = 12, Context = 8192, Devices = "Vulkan1", GpuLayers = 999 };
        var eco = LaunchPolicy.For(profile, "auto", battery); var full = LaunchPolicy.For(profile, "performance", battery);
        Assert(eco.Saving && eco.Threads == 4 && eco.Context == 4096 && eco.Devices == "none" && eco.GpuLayers == 0, "El modo batería no limita el arranque.");
        Assert(!full.Saving && full.Threads == 12 && full.Context == 8192 && full.Devices == "Vulkan1", "El usuario no pudo desactivar el ahorro.");
        Assert(LaunchPolicy.For(profile, "eco", ac).Saving && !LaunchPolicy.For(profile, "auto", ac).Saving, "Los modos manual y automático no respetan la corriente.");
        var legacy = LaunchPolicy.For(new RuntimeProfile { Kind = "strata-dual", Threads = 12, Context = 8192, Devices = "Vulkan1", GpuLayers = 999 }, "eco", battery);
        Assert(legacy.Devices == "Vulkan1" && legacy.Context == 8192, "El ahorro reconfiguró Strata o sus GPU.");
        rows.Add(new { check = "battery-automatic-manual-and-legacy-profile-preservation", eco, full, passed = true });
        var unknown = PowerScanner.Decode(255, 255, 255, 255);
        Assert(!unknown.OnBattery && unknown.BatteryPresent is null && unknown.ChargePercent is null, "Se inventó un estado de batería desconocido.");
        Assert(PowerScanner.Decode(1, 128, 255, 0).BatteryPresent == false && PowerScanner.Decode(0, 0, 38, 1).OnBattery, "Flags de batería mal interpretados.");
        var firmware = new byte[] { 0, 3, 0, 0, 8, 0, 0, 0, 3, 6, 0, 0, 0, 0x8a, 0, 0 };
        Assert(PowerScanner.ParseChassis(firmware) == 10 && PowerScanner.ParseChassis([0, 1]) is null, "Chasis SMBIOS incorrecto o fuera de límites.");
        rows.Add(new { check = "unknown-power-no-battery-and-bounded-smbios-parser", passed = true }); return rows;
    }
}
