using System.Text;
using System.Text.Json;

namespace SheepCode.Distribution;

internal sealed class InstallationPaths
{
    public string Models { get; set; } = "models";
    public string StrataPython { get; set; } = @"runtime\Strata\.venv\Scripts\python.exe";
    public string VoicePython { get; set; } = @"runtime\voice\Scripts\python.exe";
    public string ToolsPython { get; set; } = "";
    internal static string Resolve(string root, string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
    internal static InstallationPaths Load(string root)
    {
        var path = Path.Combine(root, "state", "paths.json");
        if (File.Exists(path)) return JsonSerializer.Deserialize<InstallationPaths>(File.ReadAllText(path), DistributionJson.Options) ?? new();
        // Compatibility for the existing, verified installation; new installs always write paths.json.
        if (string.Equals(Path.GetFullPath(root).TrimEnd('\\'), @"E:\SheepCode", StringComparison.OrdinalIgnoreCase)) return new()
        { Models = @"E:\SheepGPTAI\models", StrataPython = @"E:\SheepGPTAI\engines\strata\.venv\Scripts\python.exe", VoicePython = @"E:\SheepGPTAI\qwen-tts-dml-env\Scripts\python.exe" };
        return new();
    }
}
internal sealed class RuntimeProfile
{
    public string Kind { get; set; } = "unconfigured";
    public string ModelId { get; set; } = "Sin modelo instalado";
    public string Label { get; set; } = "Sin configurar";
    public string Executable { get; set; } = "";
    public string ModelFile { get; set; } = "";
    public string StrataConfig { get; set; } = @"state\strata.json";
    public string Devices { get; set; } = "none";
    public string DeviceDescription { get; set; } = "Sin configurar";
    public int GpuLayers { get; set; }
    public int Context { get; set; } = 8192;
    public int Threads { get; set; } = 4;
    public int Port { get; set; } = 8088;
    public bool RequireRx580Voice { get; set; }
    internal bool Configured => Kind is "llama" or "strata" or "strata-dual";
    internal static RuntimeProfile Load(string root)
    {
        var path = Path.Combine(root, "state", "engine.json");
        if (File.Exists(path))
        {
            var profile = JsonSerializer.Deserialize<RuntimeProfile>(File.ReadAllText(path), DistributionJson.Options) ?? new();
            if (profile.Port is < 1024 or > 65535 || profile.Context is < 2048 or > 32768) throw new InvalidDataException("Perfil de motor no válido.");
            return profile;
        }
        return File.Exists(Path.Combine(root, "state", "strata.json")) ? new()
        { Kind = "strata-dual", ModelId = "qwen3.8-flash-next-iq2_xs", Label = "Qwen 3.8 Flash Next · Strata · IQ2_XS", DeviceDescription = "RTX 2060 SUPER / CUDA + RX 580 / DirectCompute (down Q2_0 de expertos)", RequireRx580Voice = true } : new();
    }
}
internal static class DistributionJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static void Save(string path, object data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, Options), new UTF8Encoding(false)); File.Move(temp, path, true);
    }
}
