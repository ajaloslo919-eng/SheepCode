using System.Text.Json;

namespace SheepCode;

internal sealed class LocalAutomation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Project { get; set; } = "";
    public int Minutes { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset Next { get; set; }
    public string LastResult { get; set; } = "";
}
internal sealed class LocalAutomations
{
    private static string PathName => Path.Combine(AppPaths.State, "automations.json");
    internal List<LocalAutomation> Items { get; } = File.Exists(PathName) ? JsonSerializer.Deserialize<List<LocalAutomation>>(File.ReadAllText(PathName), AppPaths.Json) ?? [] : [];
    internal void Save() => AppPaths.SaveJson(PathName, Items);
    internal string Status() => JsonSerializer.Serialize(new { items = Items, scope = "Se ejecutan con SheepCode abierto, proyecto seleccionado y motor disponible. Solo lectura y propuestas; sin acciones MCP, PC o web ni cambios de permisos." });
    internal string Create(string name, int minutes, string prompt, ProjectWorkspace workspace)
    {
        if (name.Length is < 1 or > 80 || prompt.Length is < 5 or > 2000 || minutes is < 5 or > 10080 || Items.Count >= 20) throw new ArgumentException("Nombre hasta 80 caracteres, petición hasta 2000, intervalo entre 5 minutos y una semana; máximo 20 automatizaciones.");
        if (Items.Any(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new IOException("Ya existe esa automatización; pausa la anterior o utiliza otro nombre.");
        var item = new LocalAutomation { Name = name, Prompt = prompt, Minutes = minutes, Project = workspace.Root, Next = DateTimeOffset.UtcNow.AddMinutes(minutes) };
        Items.Add(item); Save(); return "Automatización creada: " + item.Id + " · " + name + ". Funciona con SheepCode abierto y ese proyecto seleccionado; prepara propuestas para revisión.";
    }
    internal string Set(string id, bool enabled)
    {
        var item = Items.SingleOrDefault(i => i.Id == id) ?? throw new IOException("No existe esa automatización.");
        item.Enabled = enabled; if (enabled) item.Next = DateTimeOffset.UtcNow.AddMinutes(item.Minutes); Save(); return "Automatización " + id + (enabled ? " activada." : " pausada.");
    }
    internal LocalAutomation? Due(string? project, DateTimeOffset now) => Items.FirstOrDefault(i => i.Enabled && i.Next <= now && string.Equals(i.Project, project, StringComparison.OrdinalIgnoreCase));
}
