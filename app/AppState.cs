using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SheepCode.Distribution;

namespace SheepCode;

internal static class AppPaths
{
    internal static string Root => Path.GetFullPath(Environment.GetEnvironmentVariable("SHEEPCODE_HOME") ??
        (Path.GetFileName(AppContext.BaseDirectory.TrimEnd('\\')) == "app" ? Directory.GetParent(AppContext.BaseDirectory.TrimEnd('\\'))!.FullName :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SheepCode")));
    internal static string State => Path.Combine(Root, "state");
    internal static string Logs => Path.Combine(Root, "logs");
    internal static string Runtime => Path.Combine(Root, "runtime", "Strata");
    internal static string Temp => Path.Combine(Root, "temp");
    internal static string Sessions => Path.Combine(Root, "sessions");
    internal static string Changes => Path.Combine(Root, "changes");
    internal static string ModelRoot => InstallationPaths.Resolve(Root, InstallationPaths.Load(Root).Models);
    internal static string StrataPython => InstallationPaths.Resolve(Root, InstallationPaths.Load(Root).StrataPython);
    internal static string VoicePython => InstallationPaths.Resolve(Root, InstallationPaths.Load(Root).VoicePython);
    internal static string? ToolsPython => ToolExecutable("python.exe", InstallationPaths.Load(Root).ToolsPython) ?? (File.Exists(StrataPython) ? StrataPython : null);
    internal static string? ToolExecutable(string name, string configured = "")
    {
        if (configured.Length > 0 && File.Exists(InstallationPaths.Resolve(Root, configured))) return InstallationPaths.Resolve(Root, configured);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(directory) || directory.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            try { var path = Path.Combine(directory.Trim('"'), name); if (File.Exists(path)) return Path.GetFullPath(path); } catch (ArgumentException) { }
        }
        return null;
    }
    internal static string EngineConfig => Path.Combine(State, "strata.json");
    internal static string PeerStatus => Path.Combine(Logs, "strata-rx580.json");
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    internal static void Initialize()
    {
        foreach (var path in new[] { State, Logs, Temp, Sessions, Changes }) Directory.CreateDirectory(path);
    }
    internal static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    internal static string HashText(string value) => Hash(Encoding.UTF8.GetBytes(value));
    internal static void SaveJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
}

internal sealed class Preferences
{
    public string LastProject { get; set; } = "";
    public string Reasoning { get; set; } = "none";
    public bool ReadAloud { get; set; }
    public bool AllowChecks { get; set; }
    public string Emotion { get; set; } = "calmness";
    public int MaximumSteps { get; set; } = 10;
    public List<string> DisabledSkills { get; set; } = [];
    public string PortableMode { get; set; } = "auto";
    public static string PathName => Path.Combine(AppPaths.State, "preferences.json");
    public static Preferences Load()
    {
        var value = File.Exists(PathName) ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(PathName), AppPaths.Json) ?? new() : new();
        value.Validate(); return value;
    }
    public void Validate()
    {
        if (Reasoning is not ("none" or "low" or "medium" or "high")) throw new InvalidDataException("Nivel de razonamiento no válido.");
        if (PortableMode is not ("auto" or "eco" or "performance")) throw new InvalidDataException("Modo portátil no válido.");
        MaximumSteps = Math.Clamp(MaximumSteps, 1, 16);
    }
    public void Save() { Validate(); AppPaths.SaveJson(PathName, this); }
}

internal sealed record ChatLine(string Role, string Text, DateTimeOffset At);
internal sealed class ProjectSession
{
    public string Project { get; set; } = "";
    public List<ChatLine> Lines { get; set; } = [];
    public static ProjectSession Load(string project)
    {
        var path = SessionPath(project);
        return File.Exists(path) ? JsonSerializer.Deserialize<ProjectSession>(File.ReadAllText(path), AppPaths.Json) ?? new() { Project = project } : new() { Project = project };
    }
    private static string SessionPath(string project) => Path.Combine(AppPaths.Sessions, AppPaths.HashText(project.ToUpperInvariant())[..16] + ".json");
    public void Add(string role, string text)
    {
        Lines.Add(new(role, text, DateTimeOffset.Now));
        if (Lines.Count > 200) Lines.RemoveRange(0, Lines.Count - 200);
        AppPaths.SaveJson(SessionPath(Project), this);
    }
}
