using System.Text.Json;
using System.Text.RegularExpressions;

namespace SheepCode;

internal sealed record AgentSkill(string Name, string Description, string Instructions, string Path, string Emoji);
internal sealed class SkillRegistry(Preferences preferences)
{
    internal List<AgentSkill> Items { get; } = [];
    internal List<string> Errors { get; } = [];
    internal event Action? Changed;
    internal static string Resolve(string name) => name.ToLowerInvariant() switch { "codigo" or "código" => "code", "pc" or "escritorio" => "desktop", "navegador" or "navegación" or "web" => "browser", "modelos" or "modelo" => "models", _ => name.ToLowerInvariant() };
    internal void Reload()
    {
        Items.Clear(); Errors.Clear();
        foreach (var root in new[] { Path.Combine(AppContext.BaseDirectory, "skills"), Path.Combine(AppPaths.State, "skills") })
        {
            Directory.CreateDirectory(root);
            foreach (var folder in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase).Take(40))
            {
                var path = Path.Combine(folder, "SKILL.md");
                try
                {
                    if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 || !File.Exists(path)) continue;
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 18000) throw new InvalidDataException("Skill demasiado grande o enlazada.");
                    var text = File.ReadAllText(path).Replace("\r\n", "\n");
                    var match = Regex.Match(text, @"\A---\nname:\s*([a-z][a-z0-9-]{0,63})\ndescription:\s*([^\n]+)\n---\n([\s\S]+)\z");
                    if (!match.Success) throw new InvalidDataException("Formato esperado: name y description en YAML, seguidos de instrucciones.");
                    var name = match.Groups[1].Value;
                    if (Items.Any(s => s.Name == name)) throw new InvalidDataException("Nombre duplicado; se conserva la skill incluida.");
                    var description = match.Groups[2].Value.Trim();
                    if (description.StartsWith('"')) description = JsonSerializer.Deserialize<string>(description) ?? "";
                    Items.Add(new(name, description, match.Groups[3].Value.Trim(), path,
                        name switch { "code" => "🐑", "desktop" => "💻", "browser" => "🌐", "models" => "🧠", _ => "🌸" }));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { Errors.Add(Path.GetFileName(folder) + ": " + e.Message); }
            }
        }
        Changed?.Invoke();
    }
    internal bool Enabled(string name) => !preferences.DisabledSkills.Contains(Resolve(name), StringComparer.OrdinalIgnoreCase) && Items.Any(s => s.Name == Resolve(name));
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
    internal string Catalog() => JsonSerializer.Serialize(Items.Select(s => new { s.Name, s.Description, enabled = Enabled(s.Name) }));
    internal string PromptCatalog()
    {
        var rows = new List<object>(); var budget = 0;
        foreach (var s in Items)
        {
            var description = s.Description[..Math.Min(s.Description.Length, 180)];
            var row = new { s.Name, description, enabled = Enabled(s.Name) }; var length = JsonSerializer.Serialize(row).Length;
            if (budget + length > 4200) { rows.Add(new { note = "Catálogo parcial; el usuario puede consultar todas las skills en Skills." }); break; }
            rows.Add(row); budget += length;
        }
        return JsonSerializer.Serialize(rows);
    }
    internal string[] Match(string input)
    {
        var names = Regex.Matches(input, @"\$([a-z][a-z0-9-]*)").Select(m => m.Groups[1].Value).ToList();
        var canonical = input.ToLowerInvariant();
        if (Regex.IsMatch(canonical, @"https?://|pestaña|pestalla|navegador|página web|enlace")) names.Add("browser");
        if (Regex.IsMatch(canonical, @"\bpc\b|ventana|escritorio|aplicación")) names.Add("desktop");
        if (Regex.IsMatch(canonical, @"modelo|strata|gráfica|\bgpu\b|razonamiento|portátil|laptop|batería|ahorro")) names.Add("models");
        if (Regex.IsMatch(canonical, @"código|archivo|función|corrige|proyecto|bug|refactor")) names.Add("code");
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
