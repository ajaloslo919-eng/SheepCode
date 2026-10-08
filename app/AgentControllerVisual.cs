using System.Text.Json;
using System.Text.RegularExpressions;

namespace SheepCode;

internal sealed partial class AgentController
{
    internal async Task<VisualRead> AnalyzeImageAsync(string path, string question, CancellationToken token)
    { var input = Images.VisualInput(path); return await Vision.AnalyzeAsync(input.Info, input.Png, question, token); }
    internal async Task<VisualRead> AnalyzeSceneAsync(string path, string question, CancellationToken token)
    { Skills.Require("scenes"); Skills.Require("images"); var input = await Scenes.PreviewAsync(path, token); return await Vision.AnalyzeAsync(input.Info, input.Png, question, token); }
    private async Task<string?> VisualControlAsync(string text, string canonical, CancellationToken token)
    {
        if (canonical is "estado de la visión" or "estado de la vision") { Skills.Require("images"); return JsonSerializer.Serialize(Vision.Status(), AppPaths.Json); }
        if (canonical is "instala la visión local" or "instala la vision local") { await Vision.InstallHumanAsync(AppPaths.ModelRoot, token); return "👁️ Visión local instalada y verificada. Usa «Describe las imágenes adjuntas» o el botón Interpretar."; }
        if (canonical is "activa la visión" or "desactiva la visión" or "activa la vision" or "desactiva la vision")
        { Skills.Require("images"); preferences.VisionEnabled = canonical.StartsWith("activa"); preferences.Save(); if (!preferences.VisionEnabled) Vision.Cancel(); return "Visión " + (preferences.VisionEnabled ? "activada; necesita el componente instalado." : "desactivada; OCR conserva su propio ajuste."); }
        if (canonical is "activa 3d" or "desactiva 3d" or "activa las escenas" or "desactiva las escenas")
        { Skills.SetEnabled("scenes", canonical.StartsWith("activa")); return "Inspección 3D " + (Scenes.Enabled ? "activada." : "desactivada."); }
        if (canonical is "estado de 3d" or "estado de las escenas") { Skills.Require("scenes"); return JsonSerializer.Serialize(Scenes.Status(), AppPaths.Json); }
        if (canonical is "lista los archivos 3d" or "lista las escenas") { Skills.Require("scenes"); return JsonSerializer.Serialize(new { pending = Scenes.Pending, attached = Scenes.Attached, project = Workspace is null ? Array.Empty<string>() : Scenes.ProjectFiles() }, AppPaths.Json); }
        if (canonical is "inspecciona las escenas adjuntas")
        {
            Skills.Require("scenes"); Scenes.AcceptPending(); if (Scenes.Attached.Count == 0) throw new ArgumentException("Adjunta una escena primero.");
            var results = new List<string>(); foreach (var info in Scenes.Attached.TakeLast(2)) { var raw = JsonSerializer.Serialize(await Scenes.ReadAsync(info.Path, 0, 80, token), AppPaths.Json); LastActions.Add("scene_inspect"); ToolResult?.Invoke("scene_inspect", raw); results.Add(VisualControlReply(raw) ?? raw); }
            return string.Join("\n\n", results);
        }
        var configure = Regex.Match(text, @"^configura blender (.+)$", RegexOptions.IgnoreCase);
        if (configure.Success)
        {
            Skills.Require("scenes"); var path = configure.Groups[1].Value.Trim('"', ' ');
            if (path != "auto" && SceneTools.FindBlender(path) is null) throw new ArgumentException("Elige un blender.exe instalado o auto.");
            preferences.BlenderExecutable = path == "auto" ? "" : Path.GetFullPath(path); preferences.Save(); return "Blender: " + (SceneTools.FindBlender(preferences.BlenderExecutable) ?? "sin configurar");
        }
        var attach = Regex.Match(text, @"^adjunta (?:el archivo 3d|la escena) (.+)$", RegexOptions.IgnoreCase);
        if (attach.Success) { var info = Scenes.AttachHuman(attach.Groups[1].Value.Trim('"', ' ')); return "🧊 " + info.Name + " · " + info.Path + " por enviar. Inspecciona la escena ID o envía tu petición."; }
        var remove = Regex.Match(text, @"^quita (?:el archivo 3d|la escena) (scene-[a-f0-9]{12})$", RegexOptions.IgnoreCase);
        if (remove.Success) { Scenes.RemoveHuman(remove.Groups[1].Value); return "Adjunto 3D retirado; el original conserva su contenido."; }
        if (canonical is "quita todas las escenas" or "quita todos los archivos 3d") { Scenes.ClearHuman(); return "Adjuntos 3D retirados."; }
        var scene = Regex.Match(text, @"^(?:inspecciona|analiza|lee) (?:la escena|el fbx|el archivo blend|el prefab|el archivo 3d) (.+)$", RegexOptions.IgnoreCase);
        if (scene.Success)
        {
            var path = scene.Groups[1].Value.Trim('"', ' '); if (Scenes.Pending.Any(s => s.Path == path)) Scenes.AcceptPending(path);
            var read = await Scenes.ReadAsync(path, 0, 80, token); var raw = JsonSerializer.Serialize(read, AppPaths.Json); LastActions.Add("scene_inspect"); ToolResult?.Invoke("scene_inspect", raw); return raw;
        }
        var shape = Regex.Match(text, @"^interpreta el objeto 3d (.+)$", RegexOptions.IgnoreCase);
        if (shape.Success)
        {
            var path = shape.Groups[1].Value.Trim('"', ' '); if (Scenes.Pending.Any(s => s.Path == path)) Scenes.AcceptPending(path);
            var raw = JsonSerializer.Serialize(await AnalyzeSceneAsync(path, "What 3D shape is shown in this image?", token), AppPaths.Json);
            LastActions.Add("scene_analyze"); ToolResult?.Invoke("scene_analyze", raw); return raw;
        }
        if (canonical is "describe las imágenes adjuntas" or "describe las imagenes adjuntas" or "interpreta las imágenes adjuntas" or "interpreta las imagenes adjuntas" or "interpreta el dibujo" or "qué hay en esta imagen" or "que hay en esta imagen")
        {
            Skills.Require("images"); if (Images.Pending.Count > 0) Images.AcceptPending(); var selected = Images.Attached.TakeLast(4).ToArray();
            if (selected.Length == 0) throw new ArgumentException("Adjunta una imagen primero.");
            var outputs = new List<string>(); foreach (var info in selected) { var read = await AnalyzeImageAsync(info.Path, "", token); var raw = JsonSerializer.Serialize(read); LastActions.Add("image_analyze"); ToolResult?.Invoke("image_analyze", raw); outputs.Add(FriendlyVisual(read)); }
            return string.Join("\n\n", outputs);
        }
        var image = Regex.Match(text, @"^(?:interpreta|describe) (?:la imagen|el dibujo|la foto) (.+)$", RegexOptions.IgnoreCase);
        if (image.Success)
        {
            var path = image.Groups[1].Value.Trim('"', ' '); if (Images.Pending.Any(i => i.Path == path)) Images.AcceptPending(path);
            var raw = JsonSerializer.Serialize(await AnalyzeImageAsync(path, "", token), AppPaths.Json); LastActions.Add("image_analyze"); ToolResult?.Invoke("image_analyze", raw); return raw;
        }
        return null;
    }
    internal static string FriendlyVisual(VisualRead read) => "👁️ " + read.Image.Name + "\n" + (read.Description.Length > 0 ? read.Description : read.Error) + "\n\n" + read.Notice;
    private string? VisualControlReply(string raw)
    {
        if (!raw.TrimStart().StartsWith('{')) return null;
        try
        {
            if (LastActions.Any(a => a is "image_analyze" or "scene_analyze"))
            { var read = JsonSerializer.Deserialize<VisualRead>(raw, AppPaths.Json); if (read?.Image is not null) return FriendlyVisual(read); }
            if (LastActions.Contains("scene_inspect"))
            {
                var read = JsonSerializer.Deserialize<SceneRead>(raw, AppPaths.Json); if (read?.File is null) return null;
                return "🧊 " + read.File.Name + " · " + read.Engine + "\n" + (read.Status == "inspected" ? read.ObjectCount + " objetos · " + read.MeshCount + " mallas · " + read.VertexCount + " vértices · " + read.FaceCount + " caras\n" +
                    string.Join('\n', read.Objects.Take(12).Select(o => o.Name + " · " + o.Type + (o.Parent.Length > 0 ? " · padre " + o.Parent : ""))) : read.Error) + "\n\n" + read.Notice;
            }
        }
        catch (JsonException) { }
        return null;
    }
}
