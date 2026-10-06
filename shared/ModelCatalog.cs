using System.Text.Json;

namespace SheepCode.Distribution;

internal sealed record ModelAsset(string Id, string Label, string Repository, string Revision, string File, long Size, string Sha256, string Url, string License);
internal sealed record InstallPlan(string Id, string Label, string Kind, string Explanation, long DownloadBytes, int Context, ModelAsset? Model = null, string? ReuseRoot = null)
{
    public override string ToString() => Label;
}
internal static class ModelCatalog
{
    internal static IReadOnlyList<ModelAsset> Models
    {
        get
        {
            using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream("SheepCode.model-catalog.json") ?? throw new FileNotFoundException("Falta el catálogo de modelos.");
            using var data = JsonDocument.Parse(stream);
            return JsonSerializer.Deserialize<ModelAsset[]>(data.RootElement.GetProperty("models").GetRawText(), DistributionJson.Options)!;
        }
    }
    internal static string? FindExisting()
    {
        var candidates = new[] { Environment.GetEnvironmentVariable("SHEEPCODE_HOME"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SheepCode"), @"E:\SheepCode" };
        return candidates.Where(p => !string.IsNullOrWhiteSpace(p)).FirstOrDefault(p =>
            File.Exists(Path.Combine(p!, "state", "strata.json")) && File.Exists(InstallationPaths.Resolve(p!, InstallationPaths.Load(p!).StrataPython)));
    }
    internal static bool StrataHardware(SystemHardware hardware) => hardware.X64 && hardware.Avx2 && hardware.RamGiB >= 47 &&
        hardware.Gpus.Any(g => !g.Software && g.Usable && g.DxgiVisible && g.DedicatedBytes >= 11L * 1073741824 &&
            (g.VendorId == 0x10de && g.Name.Contains("RTX", StringComparison.OrdinalIgnoreCase) || g.VendorId == 0x1002 &&
             System.Text.RegularExpressions.Regex.IsMatch(g.Name, @"RX (?:6[89]\d\d|7[789]\d\d|90[67]\d)|AI PRO R9700", System.Text.RegularExpressions.RegexOptions.IgnoreCase)));
    internal static InstallPlan Recommend(SystemHardware hardware, string? reuseRoot = null)
    {
        if (!hardware.X64) throw new PlatformNotSupportedException("Esta edición necesita Windows de 64 bits en un procesador x64.");
        if (hardware.RamGiB < 3.5) throw new InvalidOperationException("Se necesitan al menos 4 GB de RAM. Puedes instalar el editor sin descargar un modelo.");
        if (reuseRoot is not null && hardware.RamGiB >= 47 &&
            (RuntimeProfile.Load(reuseRoot).Kind != "strata-dual" || hardware.Gpus.Any(g => g.VendorId == 0x1002 && g.Name.Contains("RX 580")) && hardware.Gpus.Any(g => g.VendorId == 0x10de && g.Name.Contains("RTX 2060 SUPER")))) return Reuse(reuseRoot);
        var battery = hardware.Power?.OnBattery == true || hardware.Power?.BatterySaver == true;
        if (!battery && StrataHardware(hardware) && hardware.DiskFreeBytes >= 100L * 1073741824) return Strata();
        if (hardware.RamGiB < 7.5 || !hardware.Avx2)
        {
            var small = Models.First(m => m.Id == "qwen3-0.6b");
            if (hardware.DiskFreeBytes < small.Size + 3L * 1073741824) throw new IOException("Libera al menos 4 GB de disco para instalar Strata CPU o elige solo el editor.");
            return LowMemory();
        }
        // Total RAM describes the machine; available RAM is shown separately because existing apps may occupy it.
        var memoryBudget = MemoryBudget(hardware);
        var diskBudget = Math.Max(0, hardware.DiskFreeBytes - 3L * 1073741824);
        var model = Models.OrderBy(m => m.Size).LastOrDefault(m => m.Size * 1.25 + 700_000_000 < memoryBudget && m.Size < diskBudget && (!battery || m.Size < 2_750_000_000))
            ?? throw new IOException("No queda espacio o memoria para el modelo mínimo; libera al menos 4 GB de disco o instala solo el editor.");
        var context = hardware.Portable || battery || hardware.RamGiB < 7.5 ? 4096 : 8192;
        return new(model.Id, model.Label + (model.File.Contains("Q8") ? " · Q8" : " · Q4"), "llama",
            (hardware.Portable ? "Perfil portátil: reserva más RAM para Windows y usa contexto 4096. " : "") +
            (battery ? "En batería recomienda hasta 4B; el modo automático carga los perfiles llama.cpp en CPU con hasta 4 hilos. " : "") +
            "Elegido por RAM y disco. La memoria integrada compartida no se suma a la RAM. GPU mediante Vulkan si se verifica; CPU explícita si no está disponible. " +
            (model.Size < 2_000_000_000 ? "Es un modelo pequeño: conviene pedir cambios cortos y revisar sus propuestas. " : "") +
            "La selección estima qué cabe en el equipo; no es una clasificación de rendimiento.", model.Size, context, model);
    }
    internal static double MemoryBudget(SystemHardware hardware) => Math.Max(1.5, hardware.RamGiB * (hardware.Portable ? 0.50 : 0.67) - 0.75) * 1073741824;
    internal static InstallPlan Reuse(string root) => new("reuse-strata", "🐑 Reutilizar Strata ya instalado", "reuse", "Usa los pesos que ya están instalados. El perfil original conserva sus dos GPU y la voz RX 580. Mantiene los permisos de la instalación que se actualiza.", 0, 8192, ReuseRoot: root);
    internal static InstallPlan Strata() => new("strata-iq2-xs", "🐑 Strata · Qwen 3.8 Flash Next · IQ2_XS", "strata",
        "Para equipos con al menos 48 GB de RAM y una GPU compatible con 12 GB. Descarga y prepara el motor oficial; reserva 100 GB de disco. Las GPU del mismo backend pueden compartir el modelo. La mezcla RTX/RX antigua solo existe en el perfil original reutilizado.", 74L * 1073741824, 8192);
    internal static InstallPlan LowMemory()
    {
        var model = Models.First(m => m.Id == "qwen3-0.6b");
        return new(model.Id, "🌱 Strata CPU · Qwen3-0.6B · Celeron / 4 GB", "strata-cpu",
            "Backend del fork Strata para CPU x64, también sin AVX/AVX2. Modelo de 0,6B, contexto 4096, hasta 2 hilos y límite de memoria del motor de 1536 MiB. Sin servidor Python ni GPU; razonamiento oculto desactivado. Revisa sus propuestas pequeñas: este modelo tiene menor capacidad. Windows y las pestañas también necesitan RAM; cierra aplicaciones si el equipo está ocupado. La voz RX 580 de la instalación original conserva su perfil.", model.Size, 4096, model);
    }
    internal static InstallPlan EditorOnly() => new("editor-only", "🌸 Editor y código fuente · descargar el modelo después", "none", "Puedes editar proyectos y skills. La IA estará sin configurar hasta instalar un modelo en Modelos.", 0, 8192);
}
