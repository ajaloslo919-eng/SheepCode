using System.Text.Json;
using System.Text.RegularExpressions;

namespace SheepCode;

internal sealed record AgentSkill(string Name, string Description, string Instructions, string Path, string Emoji, string Backend = "workflow", string Triggers = "");
internal sealed class SkillRegistry(Preferences preferences)
{
    internal List<AgentSkill> Items { get; } = [];
    internal List<string> Errors { get; } = [];
    internal event Action? Changed;
    internal Func<string, string>? ExternalState { get; set; }
    internal static string Resolve(string name) => name.ToLowerInvariant() switch { "codigo" or "código" => "code", "pc" or "escritorio" => "desktop", "navegador" or "navegación" or "web" => "browser", "modelos" or "modelo" => "models", "imagen" or "imágenes" or "imagenes" => "images", _ => name.ToLowerInvariant() };
    internal void Reload()
    {
        Items.Clear(); Errors.Clear();
        foreach (var root in new[] { Path.Combine(AppContext.BaseDirectory, "skills"), Path.Combine(AppPaths.State, "skills") })
        {
            Directory.CreateDirectory(root);
            foreach (var folder in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase).Take(160))
            {
                var path = Path.Combine(folder, "SKILL.md");
                try
                {
                    if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 || !File.Exists(path)) continue;
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 18000) throw new InvalidDataException("Skill demasiado grande o enlazada.");
                    var text = File.ReadAllText(path).Replace("\r\n", "\n");
                    var skill = Parse(text, path); var name = skill.Name;
                    if (Items.Any(s => s.Name == name)) throw new InvalidDataException("Nombre duplicado; se conserva la skill incluida.");
                    Items.Add(skill);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { Errors.Add(Path.GetFileName(folder) + ": " + e.Message); }
            }
        }
        Changed?.Invoke();
    }
    internal bool Enabled(string name) => !preferences.DisabledSkills.Contains(Resolve(name), StringComparer.OrdinalIgnoreCase) && Items.Any(s => s.Name == Resolve(name));
    internal static AgentSkill Parse(string text, string path)
    {
        text = text.Replace("\r\n", "\n").TrimStart('\uFEFF');
        var match = Regex.Match(text, @"\A---\n([\s\S]*?)\n---\n([\s\S]+)\z");
        if (!match.Success) throw new InvalidDataException("La skill necesita frontmatter YAML e instrucciones.");
        string Field(string key) { var value = Regex.Match(match.Groups[1].Value, "(?m)^" + key + @":\s*([^\n]*)$").Groups[1].Value.Trim(); if (value.StartsWith('"')) return JsonSerializer.Deserialize<string>(value) ?? ""; if (value.StartsWith('\'')) return value.Trim('\''); return value; }
        var name = Field("name"); var description = Field("description");
        if (!Regex.IsMatch(name, @"\A[a-z][a-z0-9-]{0,63}\z") || description.Length is < 1 or > 800 || description is "|" or ">") throw new InvalidDataException("name en minúsculas y description en una línea.");
        var backend = Field("backend"); if (backend.Length == 0) backend = name is "code" or "desktop" or "browser" or "models" ? name : "workflow";
        if (backend is not ("code" or "desktop" or "browser" or "models" or "images" or "scenes" or "imagegen" or "workflow" or "artifacts" or "git" or "backup" or "automations" or "mcp" or "updater")) throw new InvalidDataException("Backend de skill no registrado.");
        var emoji = Field("emoji"); if (emoji.Length == 0) emoji = name switch { "code" => "🐑", "desktop" => "💻", "browser" => "🌐", "models" => "🧠", _ => "🌸" };
        return new(name, description, match.Groups[2].Value.Trim(), path, emoji, backend, Field("triggers"));
    }
    internal string State(string name)
    {
        var skill = Get(name); if (!Enabled(name)) return "desactivada";
        if (name == "connectors") return "disponible · cliente MCP; configura proveedores";
        if (name == "images") return "disponible · adjuntos/OCR; visión necesita componente local instalado y activo";
        if (name == "scenes") return "disponible · Unity YAML; FBX/BLEND necesitan Blender";
        if (name == "imagegen") return "disponible · generación local requiere instalar SD-Turbo; guardar PNG es humano";
        if (skill.Backend is "desktop" or "browser" or "models" && !Enabled(skill.Backend)) return "desactivada · necesita " + skill.Backend;
        if (skill.Backend == "mcp") return ExternalState?.Invoke(skill.Name) ?? "sin configurar · necesita conexión MCP";
        if (skill.Backend == "git" && AppPaths.ToolExecutable("git.exe") is null) return "sin configurar · necesita Git";
        if (skill.Backend == "workflow" && !Enabled("code")) return "desactivada · necesita code";
        return skill.Backend == "workflow" ? "guía activa · prepara código y propuestas" : "disponible · herramientas locales";
    }
    internal AgentSkill Get(string name) => Items.FirstOrDefault(s => s.Name == Resolve(name)) ?? throw new ArgumentException("No existe la skill " + name + ".");
    internal void SetEnabled(string name, bool enabled)
    {
        name = Get(name).Name; preferences.DisabledSkills.RemoveAll(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (!enabled) preferences.DisabledSkills.Add(name);
        preferences.Save(); Changed?.Invoke();
    }
    internal string LoadInstructions(string name)
    { var skill = Get(name); Require(name); return "Skill " + name + " (instrucciones de flujo; no conceden permisos):\n" + skill.Instructions[..Math.Min(skill.Instructions.Length, 3500)] + (skill.Instructions.Length > 3500 ? "\n[Instrucciones acotadas a 3500 caracteres por skill.]" : ""); }
    internal void Require(string name) { if (!Enabled(name)) throw new InvalidOperationException("Skill " + name + " desactivada. La persona puede activarla en Skills o con «Activa la skill " + name + "»."); }
    internal string Catalog() => JsonSerializer.Serialize(Items.Select(s => new { s.Name, s.Description, s.Backend, enabled = Enabled(s.Name), state = State(s.Name) }));
    internal string PromptCatalog()
    {
        return string.Join(",", Items.Select(s => s.Name + (Enabled(s.Name) ? s.Backend == "mcp" ? "[MCP]" : "" : "[off]"))) + ". MCP exige consultar mcp_status y configurar un proveedor; local usa herramientas del sistema.";
    }
    internal string[] Match(string input)
    {
        var names = Regex.Matches(input, @"\$([a-z][a-z0-9-]*)").Select(m => m.Groups[1].Value).ToList();
        var canonical = input.ToLowerInvariant();
        if (Regex.IsMatch(canonical, @"https?://|pestaña|pestalla|navegador|página web|enlace")) names.Add("browser");
        if (Regex.IsMatch(canonical, @"\bpc\b|ventana|escritorio|aplicación")) names.Add("desktop");
        if (Regex.IsMatch(canonical, @"modelo|strata|gráfica|\bgpu\b|razonamiento|portátil|laptop|batería|ahorro|rendimiento|modo rápido|qwen")) names.Add("models");
        if (Regex.IsMatch(canonical, @"código|archivo|función|corrige|proyecto|bug|refactor")) names.Add("code");
        foreach (var skill in Items.Where(s => s.Triggers.Length > 0))
            if (skill.Triggers.Split('|').Any(t => canonical.Contains(t, StringComparison.OrdinalIgnoreCase))) names.Insert(0, skill.Name);
        if (AgentController.HumanRequestsImage(input)) names.Insert(0, "imagegen");
        return names.Distinct().Where(Enabled).Take(3).ToArray();
    }
    internal string Create(string name, string description, string instructions)
    {
        if (!Regex.IsMatch(name, @"\A[a-z][a-z0-9-]{0,63}\z") || name.EndsWith('-') || name.Contains("--") || Resolve(name) != name || Items.Any(s => s.Name == name)) throw new ArgumentException("Nombre nuevo en minúsculas, números y guiones simples; los alias de las skills incluidas están reservados.");
        if (description.Length is < 12 or > 600 || description.Contains('\n') || description.Contains('\r')) throw new ArgumentException("Descripción de 12 a 600 caracteres en una línea.");
        if (instructions.Length is < 20 or > 10000) throw new ArgumentException("Instrucciones de 20 a 10000 caracteres.");
        var root = Path.Combine(AppPaths.State, "skills");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("La carpeta de skills no puede ser un enlace.");
        var folder = Path.Combine(root, name); if (Directory.Exists(folder)) throw new IOException("Esa carpeta ya existe.");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "SKILL.md"); File.WriteAllText(path, "---\nname: " + name + "\ndescription: " + JsonSerializer.Serialize(description) + "\n---\n\n" + instructions.Trim() + "\n"); Reload(); return path;
    }
    internal string Import(string path)
    {
        var source = System.IO.Path.GetFullPath(path);
        if (!File.Exists(source) || new FileInfo(source).Length > 18000 || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("Selecciona un SKILL.md local de hasta 18 KiB, sin enlaces.");
        var parsed = Parse(File.ReadAllText(source), source);
        return Create(parsed.Name, parsed.Description, parsed.Instructions + "\n\nImportada como instrucciones: las herramientas y recursos del entorno original requieren adaptación local. No ejecuta scripts ni concede permisos.");
    }
}

internal static class ActionIntent
{
    internal static void Check(string label, string humanRequest)
    {
        var labelWords = Regex.Matches(label.ToLowerInvariant(), @"\p{L}+").Select(m => m.Value).ToHashSet();
        var families = new[]
        {
            (new[] { "send", "submit", "enviar", "envía", "enviar mensaje" }, @"env[ií]a|enviar|send|submit"),
            (new[] { "publicar", "publica", "publish", "post" }, @"publica|publicar|publish|post"),
            (new[] { "pagar", "paga", "pay", "comprar", "compra", "buy", "checkout" }, @"paga|pagar|pay|compra|comprar|buy|checkout"),
            (new[] { "eliminar", "elimina", "borrar", "borra", "delete", "remove" }, @"borra|borrar|elimina|eliminar|delete|remove")
        };
        foreach (var (words, verbs) in families)
        {
            if (!labelWords.Overlaps(words)) continue;
            var request = humanRequest.ToLowerInvariant();
            if (!Regex.IsMatch(request, @"\b(" + verbs + @")\b") || Regex.IsMatch(request, @"\b(no|never|don't|sin|evita|evitar)\b.{0,35}\b(" + verbs + @"|env[ií]es|publiques|pagues|compres|borres|elimines)\b"))
                throw new InvalidOperationException("La acción «" + label + "» requiere una petición humana explícita para esa acción.");
        }
    }
}
