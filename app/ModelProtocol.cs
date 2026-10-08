using System.Text.Json;

namespace SheepCode;

internal static class ModelProtocol
{
    private static readonly IReadOnlyDictionary<string, string[]> Fields = new Dictionary<string, string[]>
    {
        ["list_skills"] = [], ["artifact_read"] = ["path"], ["artifact_propose"] = ["path", "format", "content", "skill"], ["git_read"] = ["operation"], ["project_backup"] = [], ["web_search"] = ["query"],
        ["mcp_status"] = [], ["mcp_tools"] = ["server"], ["mcp_call"] = ["server", "tool", "arguments", "skill"], ["automation_status"] = [], ["updater_status"] = [], ["check_updates"] = [],
        ["list_files"] = [], ["read_file"] = ["path"], ["search_files"] = ["query"], ["edit_file"] = ["path", "find", "replace"],
        ["write_file"] = ["path", "content"], ["validate_code"] = ["path", "content"], ["validation_status"] = [], ["run_check"] = ["check"], ["finish"] = ["message"], ["use_skill"] = ["name"],
        ["image_list"] = [], ["image_status"] = [], ["image_read"] = ["path"],
        ["vision_status"] = [], ["image_analyze"] = ["path", "question"], ["scene_status"] = [], ["scene_list"] = [], ["scene_inspect"] = ["path"], ["scene_analyze"] = ["path", "question"],
        ["image_generation_status"] = [], ["image_generate"] = ["prompt"],
        ["pc_windows"] = [], ["pc_read"] = [], ["pc_click"] = ["node"], ["pc_type"] = ["node", "text"],
        ["browser_tabs"] = [], ["browser_open"] = ["url"], ["browser_read"] = ["tab"], ["browser_click"] = ["tab", "node"],
        ["browser_fill"] = ["tab", "node", "text"], ["browser_back"] = ["tab"], ["browser_close"] = ["tab"], ["model_status"] = [], ["system_info"] = [], ["portable_status"] = [], ["performance_status"] = []
    };
    internal static readonly string CpuActionGrammar = BuildCpuActionGrammar();
    private static string BuildCpuActionGrammar()
    {
        var rules = new List<string> { "root ::= " + string.Join(" | ", Fields.Keys.Select(name => "action-" + name.Replace('_', '-'))),
            "string ::= \"\\\"\" ([^\"\\\\\\x7F\\x00-\\x1F] | \"\\\\\" ([\"\\\\bfnrt] | \"u\" [0-9a-fA-F]{4}))* \"\\\"\" ws",
            "integer ::= [0-9]+ ws", "ws ::= | \" \" | \"\\n\" [ \\t]{0,20}" };
        string Literal(string value) => JsonSerializer.Serialize(value) + " ws";
        string Field(string name, string type = "string") => Literal("\"" + name + "\"") + " \":\" ws " + type;
        foreach (var (name, required) in Fields)
        {
            var fields = new List<string> { Literal("\"action\"") + " \":\" ws " + Literal("\"" + name + "\"") };
            fields.AddRange(required.Where(s => s != "message").Select(s => Field(s)));
            var optionalNumbers = name switch {
                "read_file" => new[] { "start_line", "line_count" }, "list_skills" => new[] { "start", "count" },
                "browser_read" => new[] { "start", "text_length", "nodes_start", "node_count" }, _ => Array.Empty<string>() };
            if (name == "image_read") optionalNumbers = ["start", "text_length"];
            if (name == "scene_inspect") optionalNumbers = ["start", "count"];
            var optional = string.Concat(optionalNumbers.Select(s => " (\",\" ws " + Field(s, "integer") + ")?"));
            if (name == "search_files") optional += " (\",\" ws " + Field("path") + ")?";
            rules.Add("action-" + name.Replace('_', '-') + " ::= \"{\" ws " + string.Join(" \",\" ws ", fields) + optional + " \",\" ws " + Field("message") + " \"}\" ws");
        }
        return string.Join('\n', rules) + "\n";
    }
    internal static string? ValidateAction(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String)
            return "Incluye action como cadena en un único objeto JSON.";
        var tool = action.GetString()!;
        if (!Fields.TryGetValue(tool, out var required)) return "Acción no registrada: " + tool + ". Usa únicamente las acciones descritas en el sistema.";
        var missing = required.Where(field => !value.TryGetProperty(field, out var item) || item.ValueKind != JsonValueKind.String).ToArray();
        if (missing.Length == 0) return null;
        return "La acción " + tool + " requiere cadenas en los campos " + string.Join(", ", missing) + ". Colócalos al mismo nivel que action, sin envolverlos en arguments. " +
            (tool == "write_file" ? "Ejemplo: {\"action\":\"write_file\",\"path\":\"hello.py\",\"content\":\"print('Hola')\\n\",\"message\":\"Propongo crear hello.py.\"}. content contiene el código completo. Para editar un archivo largo, lee el fragmento y usa edit_file(path,find,replace) con una sustitución corta." : "Corrige solo la forma de la acción; no se ejecutó ninguna herramienta.");
    }
    internal static bool StructuredFailure(string raw)
    {
        try
        {
            using var data = JsonDocument.Parse(raw);
            return data.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("code", out var code) && code.GetString() == "structured_output_failed";
        }
        catch (JsonException) { return false; }
    }
    internal static int OutputBudget(int attempt, int context) => Math.Min(new[] { 1024, 2048, 3072 }[Math.Clamp(attempt, 0, 2)], context / 2);
}
