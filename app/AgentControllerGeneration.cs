using System.Text.Json;
using System.Text.RegularExpressions;

namespace SheepCode;
internal sealed partial class AgentController
{
    // Shared by direct human controls, skill selection and the model-tool permission gate.
    // Anchoring excludes quoted/file/web instructions and requests to write a generator program.
    private static readonly Regex HumanImageRequest = new(
        @"\A\s*[¿¡]?\s*(?:(?:por\s+favor\s*[, :]\s*|por\s+favor\s+|(?:me\s+)?(?:puedes|podrías|podrias)\s+|quiero\s+(?:que\s+(?:me\s+)?)?))*" +
        @"(?:genera(?:me|r|s)?|genérame|generes|crea(?:me|r)?|créame|crees|haz(?:me)?|hacer|hagas|dibuja(?:me|r)?|dibújame|dibujes|diseña(?:me|r)?|diséñame|diseñes)\s+" +
        @"(?:(?:un|una|otra|otro|esta|este|esa|ese|la|el|nueva|nuevo)\s+)?(?:imagen|dibujo|foto|ilustraci[oó]n|icono|sprite|logo)\b(?<prompt>[\s\S]*)\z" +
        @"|\A\s*(?:(?:please\s*[, :]\s*|please\s+|(?:can|could)\s+you\s+|i\s+want\s+(?:you\s+)?to\s+))*(?:generate|create|make|draw|design)\s+" +
        @"(?:(?:an?|the|another|new)\s+)?(?:image|picture|drawing|photo|illustration|icon|sprite|logo)\b(?<prompt>[\s\S]*)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    internal static bool TryParseHumanImageRequest(string text, out string prompt)
    {
        prompt = "";
        var match = HumanImageRequest.Match(text);
        if (!match.Success || Regex.IsMatch(text,
            @"(?i)(?:[,;.]\s*|\b(?:pero|but|y|and)\s+)(?:todav[ií]a\s+)?(?:no\s+(?:l[ao]\s+)?(?:generes|crees|dibujes|hagas)|(?:do\s+not|don't)\s+(?:generate|create|draw|make))\b")) return false;
        prompt = match.Groups["prompt"].Value.Trim().TrimStart(':').Trim();
        return true;
    }
    internal static bool HumanRequestsImage(string text) => TryParseHumanImageRequest(text, out _);
    private async Task<string?> GenerationControlAsync(string text, string canonical, CancellationToken token)
    {
        if (canonical is "estado del generador de imágenes" or "estado de la generación de imágenes" or "estado del generador de imagenes") { Skills.Require("imagegen"); return JsonSerializer.Serialize(ImageGenerator.Status(), AppPaths.Json); }
        if (canonical is "instala el generador de imágenes" or "instala el generador de imagenes") { await ImageGenerator.InstallHumanAsync(AppPaths.ModelRoot, token); return "🎨 Generador local instalado. Abre 🎨 Crear o escribe «Genera una imagen: DESCRIPCIÓN»."; }
        if (canonical is "activa la generación de imágenes" or "desactiva la generación de imágenes" or "activa la generacion de imagenes" or "desactiva la generacion de imagenes")
        { Skills.Require("imagegen"); preferences.GenerateImages = canonical.StartsWith("activa"); preferences.Save(); if (!preferences.GenerateImages) ImageGenerator.Cancel(); return "Generación de imágenes " + (preferences.GenerateImages ? "activada." : "desactivada."); }
        if (canonical is "configura generación cpu" or "configura generación auto" or "configura generacion cpu" or "configura generacion auto")
        { await ImageGenerator.SelectHumanAsync(canonical.EndsWith("cpu") ? "cpu" : "auto", token); return JsonSerializer.Serialize(ImageGenerator.Status(), AppPaths.Json); }
        if (!TryParseHumanImageRequest(text, out var prompt)) return null;
        Skills.Require("imagegen");
        if (string.IsNullOrWhiteSpace(prompt)) return "🎨 Describe qué imagen quieres crear. Por ejemplo: «Crea la imagen de un gato».";
        var result = await ImageGenerator.GenerateAsync(prompt, token);
        var raw = JsonSerializer.Serialize(result, AppPaths.Json);
        LastActions.Add("image_generate"); ToolResult?.Invoke("image_generate", raw); return raw;
    }
    internal static string FriendlyGenerated(GeneratedImage image) => image.Status == "generated" ? "🎨 Imagen creada · " + image.Width + " × " + image.Height + "\nAbre 🎨 Crear para verla y guardarla como PNG. Vista previa: " + image.Id + "\n\n" + image.Notice : image.Error + "\n\n" + image.Notice;
    private string? GenerationControlReply(string raw)
    { if (!LastActions.Contains("image_generate") || !raw.TrimStart().StartsWith('{')) return null; try { var result = JsonSerializer.Deserialize<GeneratedImage>(raw, AppPaths.Json); return result?.Status is not null ? FriendlyGenerated(result) : null; } catch (JsonException) { return null; } }
}
