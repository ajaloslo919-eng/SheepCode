using System.Diagnostics;
using System.Text.Json;

namespace SheepCode;
internal static class GenerationDiagnostics
{
    internal static async Task<int> InstallAsync(string models, string packages)
    {
        try { var prefs = Preferences.Load(); var skills = new SkillRegistry(prefs); skills.Reload(); using var generation = new ImageGeneration(prefs, skills); generation.Progress += Console.WriteLine;
            await generation.InstallHumanAsync(models, CancellationToken.None, packages); AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "generation-install.json"), new { status = "complete", component = generation.Status() }); return 0; }
        catch (Exception error) { AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "generation-install.json"), new { status = "failed", error = error.ToString() }); return 1; }
    }
    private static void Assert(bool ok, string message) { if (!ok) throw new IOException(message); }
    internal static async Task<int> RunAsync()
    {
        var report = new Dictionary<string, object?> { ["status"] = "starting", ["scope"] = "Generación REAL del componente SD-Turbo seleccionado e instalado en SheepCode, desde su diálogo. Controles y cancelación; excluye motor de código, voz y rendimiento en Celeron/portátil físico." };
        var rows = new List<object>(); report["checks"] = rows; var saved = File.Exists(Preferences.PathName) ? File.ReadAllBytes(Preferences.PathName) : null;
        try
        {
            var prefs = new Preferences(); await using var voice = new NeuralVoice(); await using var engine = new EngineHost(voice); var calls = 0;
            var agent = new AgentController(engine, voice, prefs, (_, _, _) => { calls++; throw new IOException("No se debe sustituir por otro modelo."); }); using var generator = agent.ImageGenerator;
            Assert(agent.Capabilities.Any(c => c.Name == "image-generation") && agent.Skills.Get("imagegen").Backend == "imagegen" && agent.BuildSystemPrompt("Crea una imagen", out _).Contains("image_generate"), "Generación no registrada."); rows.Add(new { test = "capability_skill_all_model_prompt", passed = true });
            await agent.SubmitAsync("Desactiva la generación de imágenes", CancellationToken.None); var disabled = await generator.GenerateAsync("a sheep", CancellationToken.None); Assert(disabled.Status == "disabled" && generator.Images.Count == 0, "Desactivación no aplicada.");
            await agent.SubmitAsync("Activa la generación de imágenes", CancellationToken.None); rows.Add(new { test = "human_toggle_disabled", passed = true });
            Assert(generator.Configured && JsonSerializer.Serialize(generator.Status()).Contains("NVIDIA"), "No quedó verificada la RTX: " + JsonSerializer.Serialize(generator.Status())); rows.Add(new { test = "verified_selected_rtx", passed = true, state = generator.Status() });
            generator.Progress += message => Console.WriteLine("🎨 " + message); var changes = Directory.EnumerateFiles(AppPaths.Changes).Count();
            var raw = await agent.SubmitAsync("Genera una imagen: a cute fluffy sheep and a small pink kitten sitting in a flower garden, pastel illustration, clean composition", CancellationToken.None);
            var created = JsonSerializer.Deserialize<GeneratedImage>(raw, AppPaths.Json)!; Assert(created.Status == "generated", "Generación real falló: " + raw);
            var png = generator.Preview(created.Id); Assert(png.Length > 1000 && AppPaths.Hash(png) == created.Sha256 && created.Width == 512 && created.Height == 512, "Salida inválida.");
            Assert(Directory.EnumerateFiles(AppPaths.Changes).Count() == changes && !prefs.AllowChecks && calls == 0, "Se modificaron propuestas, permisos o se llamó otro modelo."); rows.Add(new { test = "real_dialogue_png_no_project_writes", passed = true, result = created });
            var output = Path.Combine(AppPaths.Root, "checks", "generated-sheep-kuky-" + Guid.NewGuid().ToString("N")[..8] + ".png"); generator.SaveHuman(created.Id, output); Assert(AppPaths.Hash(File.ReadAllBytes(output)) == created.Sha256, "Guardar no conservó los píxeles."); report["sample"] = output; rows.Add(new { test = "human_save_verified", passed = true });
            using (var cancel = new CancellationTokenSource())
            {
                var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); void Started(string text) { if (text.StartsWith("Generando")) began.TrySetResult(); }
                generator.Progress += Started; var pending = generator.GenerateAsync("a blue cube", cancel.Token); await began.Task.WaitAsync(TimeSpan.FromSeconds(20)); var clock = Stopwatch.StartNew(); cancel.Cancel();
                try { await pending; throw new IOException("Cancelación no propagada."); } catch (OperationCanceledException) { }
                generator.Progress -= Started; Assert(clock.Elapsed < TimeSpan.FromSeconds(5) && generator.Images.Count == 1, "Cancelación tardía o imagen incompleta."); rows.Add(new { test = "owned_generator_cancel", passed = true, seconds = clock.Elapsed.TotalSeconds });
            }
            Assert(!Directory.EnumerateDirectories(AppPaths.Temp, "imagegen-*").Any(), "Quedaron temporales propios."); rows.Add(new { test = "temporary_cleanup", passed = true });
            agent.Skills.SetEnabled("imagegen", false); try { await generator.GenerateAsync("a sheep", CancellationToken.None); throw new IOException("Skill desactivada permitió crear."); } catch (InvalidOperationException) { }
            agent.Skills.SetEnabled("imagegen", true); rows.Add(new { test = "skill_disable", passed = true }); generator.ClearHuman(); Assert(generator.Images.Count == 0, "Vistas no retiradas."); report["status"] = "complete";
        }
        catch (Exception error) { report["status"] = "failed"; report["error"] = error.ToString(); }
        finally { if (saved is not null) File.WriteAllBytes(Preferences.PathName, saved); else if (File.Exists(Preferences.PathName)) File.Delete(Preferences.PathName); AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "generation.json"), report); }
        return report["status"]?.ToString() == "complete" ? 0 : 1;
    }
    internal static int StorageCheck()
    {
        var rows = new List<object>(); var folder = Path.Combine(AppPaths.Root, "checks", "generation-storage-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        var report = new Dictionary<string, object?> { ["scope"] = "Guardar y reemplazar un PNG previamente generado por el componente instalado; fixture de concurrencia, sin nueva inferencia.", ["checks"] = rows };
        try
        {
            using var prior = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppPaths.Root, "checks", "generation.json"))); var sample = prior.RootElement.GetProperty("sample").GetString()!;
            var prefs = new Preferences(); var skills = new SkillRegistry(prefs); skills.Reload(); using var generator = new ImageGeneration(prefs, skills);
            var image = generator.LoadDiagnosticPreview(sample); var target = Path.Combine(folder, "saved.png");
            generator.SaveHuman(image.Id, target); Assert(ImageGeneration.ExistingHashHuman(target) == image.Sha256, "PNG nuevo inválido."); rows.Add(new { test = "save_new_png", passed = true });
            try { generator.SaveHuman(image.Id, target); throw new Exception("Se sobrescribió sin confirmación."); } catch (IOException) { }
            rows.Add(new { test = "no_unconfirmed_overwrite", passed = true });
            using (var fixture = new Bitmap(2, 2)) { fixture.SetPixel(0, 0, Color.Blue); fixture.Save(target, System.Drawing.Imaging.ImageFormat.Png); }
            var before = File.ReadAllBytes(target); var expected = ImageGeneration.ExistingHashHuman(target);
            var backup = generator.SaveHuman(image.Id, target, expected);
            Assert(backup is not null && File.ReadAllBytes(backup).SequenceEqual(before) && ImageGeneration.ExistingHashHuman(target) == image.Sha256, "No se preservó el PNG anterior.");
            rows.Add(new { test = "confirmed_replace_with_exact_backup", passed = true });
            try { generator.SaveHuman(image.Id, target, expected); throw new Exception("Se ignoró la edición concurrente."); } catch (IOException) { }
            Assert(ImageGeneration.ExistingHashHuman(target) == image.Sha256, "La edición concurrente se dañó."); rows.Add(new { test = "changed_target_preserved", passed = true });
            try { generator.SaveHuman(image.Id, Path.Combine(folder, "script.py")); throw new Exception("Se guardó otro formato."); } catch (ArgumentException) { }
            rows.Add(new { test = "png_only", passed = true });
            Assert(!Directory.EnumerateFiles(folder, "*.tmp").Any(), "Quedaron archivos temporales."); rows.Add(new { test = "temporary_cleanup", passed = true }); report["status"] = "complete";
        }
        catch (Exception error) { report["status"] = "failed"; report["error"] = error.ToString(); }
        finally { Directory.Delete(folder, true); AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "generation-save.json"), report); }
        return report["status"]?.ToString() == "complete" ? 0 : 1;
    }
    internal static async Task<int> PermissionCheckAsync()
    {
        var rows = new List<object>();
        var report = new Dictionary<string, object?> { ["scope"] = "Entrada conversacional instalada: enrutado, contexto inicial y permisos con respuestas deterministas simuladas y generación desactivada. No ejecuta inferencia ni evalúa otra IA.", ["checks"] = rows };
        var savedPreferences = File.Exists(Preferences.PathName) ? File.ReadAllBytes(Preferences.PathName) : null;
        try
        {
            await using var voice = new NeuralVoice(); await using var engine = new EngineHost(voice);
            foreach (var (human, allowed) in new[] {
                ("crea la imagen de un gato", true), ("crea una imagen de un gato", true),
                ("genera la imagen de un gato", true), ("Genera una imagen: un gato", true),
                ("haz la imagen de un gato", true), ("Hazme un dibujo de un gato", true),
                ("Por favor, crea una imagen de una oveja", true), ("Por favor crea la imagen de un gato", true),
                ("Puedes hacer un dibujo de un gato", true), ("¿Me puedes generar la imagen de un gato?", true),
                ("Quiero que me crees una imagen de un gato", true), ("Genérame una imagen de un gato", true),
                ("crea un icono de un gato", true), ("diseña un logo de un gato", true),
                ("Please generate an image of a sheep", true), ("Could you create the image of a cat?", true),
                ("Resume la frase: crea la imagen de un gato", false), ("Lee literalmente: genera una imagen azul", false),
                ("Crea un programa Python que genere una imagen", false), ("Crea un script que cree la imagen de un gato", false),
                ("No generes una imagen: quiero código", false), ("Por favor, no crees la imagen de un gato", false),
                ("Inspecciona el texto del archivo; contiene: crea una imagen", false), ("\"crea la imagen de un gato\"", false),
                ("crea la imagen de un gato, pero no la generes todavía", false), ("Create an image of a cat, but don't generate it yet", false) })
            {
                var prefs = new Preferences { GenerateImages = false }; var calls = 0; var result = "";
                var agent = new AgentController(engine, voice, prefs, (_, _, _) => Task.FromResult(++calls == 1 ? JsonSerializer.Serialize(new { action = "image_generate", prompt = "a sheep", message = "Genero" }) : "{\"action\":\"finish\",\"message\":\"Prueba terminada\"}"));
                using var images = agent.Images; using var vision = agent.Vision; using var scenes = agent.Scenes; using var generator = agent.ImageGenerator; using var connections = agent.Connections; using var updates = agent.Updates;
                agent.ToolResult += (tool, raw) => { if (tool == "image_generate") result = raw; };
                await agent.SubmitAsync(human, CancellationToken.None);
                Assert(!prefs.GenerateImages && !prefs.AllowChecks && generator.Images.Count == 0, "La ruta alteró permisos, ajustes o creó una imagen.");
                if (allowed)
                {
                    Assert(calls == 0 && agent.LastActions.SequenceEqual(new[] { "image_generate" }) && result.Contains("disabled"), "La orden se desvió al modelo o ignoró la desactivación: " + human);
                    Assert(agent.Skills.Match(human).FirstOrDefault() == "imagegen", "La orden cargó la skill de lectura en vez de creación: " + human);
                    using var state = JsonDocument.Parse(result);
                    Assert(state.RootElement.GetProperty("prompt").GetString() is { Length: > 0 }, "Se perdió la descripción humana.");
                    if (human == "crea la imagen de un gato") Assert(state.RootElement.GetProperty("prompt").GetString() == "de un gato", "Se recortó la descripción de la captura.");
                }
                else
                {
                    Assert(!AgentController.HumanRequestsImage(human), "Una negación, cita o petición de código autorizó generación: " + human);
                    Assert(calls == 0 ? result == "" : calls == 2 && result.Contains("ERROR") && result.Contains("petición humana"), "La herramienta ignoró el límite de autorización: " + human);
                }
                rows.Add(new { test = "human_routing_and_permissions", human, allowed, languageModelCalls = calls, passed = true });
            }
            {
                var prefs = new Preferences { GenerateImages = false }; var calls = 0;
                var agent = new AgentController(engine, voice, prefs, (_, _, _) => { calls++; throw new IOException("No debe inferir para pedir una descripción."); });
                using var images = agent.Images; using var vision = agent.Vision; using var scenes = agent.Scenes; using var generator = agent.ImageGenerator; using var connections = agent.Connections; using var updates = agent.Updates;
                var reply = await agent.SubmitAsync("crea la imagen", CancellationToken.None);
                Assert(reply.Contains("Describe") && calls == 0 && generator.Images.Count == 0 && agent.LastActions.Count == 0, "Una orden sin descripción agotó contexto o inventó una imagen.");
                prefs.DisabledSkills.Add("imagegen");
                try { await agent.SubmitAsync("crea la imagen de un gato", CancellationToken.None); throw new IOException("Se ignoró la skill desactivada."); }
                catch (InvalidOperationException e) { Assert(e.Message.Contains("desactivada"), "Estado de skill incorrecto."); }
                rows.Add(new { test = "missing_description_and_disabled_skill", passed = true });
            }
            {
                var prefs = Preferences.Load();
                var agent = new AgentController(engine, voice, prefs, (_, _, _) => throw new IOException("Comprobación de contexto sin inferencia."));
                using var images = agent.Images; using var vision = agent.Vision; using var scenes = agent.Scenes; using var generator = agent.ImageGenerator; using var connections = agent.Connections; using var updates = agent.Updates;
                foreach (var human in new[] { "Lee una captura", "Escribe un programa Python con while", "abre la pestaña de discord", "Resume la frase: crea la imagen de un gato" })
                {
                    var system = agent.BuildSystemPrompt(human, out var loadedSkills);
                    var messages = new List<ModelMessage> { new("system", system), new("user", "Proyecto: sin abrir\nPetición humana: " + human) };
                    var remaining = engine.EffectiveContext - PromptContext.Estimate(messages) - 256 - 96 - 512;
                    var reserve = Math.Min(1024, engine.EffectiveContext / 8);
                    Assert(remaining >= reserve, "El contexto inicial no reserva una acción completa: " + human + "; quedan " + remaining + " tokens estimados.");
                    var packed = await PromptContext.FitAsync(messages, engine.EffectiveContext, reserve + 256, (items, _) => Task.FromResult(PromptContext.Estimate(items)), CancellationToken.None);
                    Assert(packed.SequenceEqual(messages) && loadedSkills.Count <= 1 && system.Contains("image_generate") && system.Contains("image_analyze") && system.Contains("scene_inspect") && system.Contains("NEVER pixels"), "La compactación perdió instrucciones o petición humana.");
                    rows.Add(new { test = "selected_profile_prompt_budget", profile = engine.Profile.Kind, context = engine.EffectiveContext, human, estimatedTokens = PromptContext.Estimate(messages), outputReserve = remaining, passed = true });
                }
            }
            Assert(savedPreferences is null ? !File.Exists(Preferences.PathName) : File.ReadAllBytes(Preferences.PathName).SequenceEqual(savedPreferences), "Los diagnósticos alteraron los ajustes instalados.");
            report["status"] = "complete";
        }
        catch (Exception error) { report["status"] = "failed"; report["error"] = error.ToString(); }
        AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "generation-permissions.json"), report); return report["status"]?.ToString() == "complete" ? 0 : 1;
    }
    internal static async Task<int> ChatReproAsync(string request = "crea la imagen de un gato")
    {
        var report = new Dictionary<string, object?> { ["status"] = "starting", ["request"] = request,
            ["scope"] = "Petición exacta de la captura en SheepCode instalado y su generador seleccionado. No llama al motor de código ni a MCP; no prueba rendimiento de otra IA." };
        try
        {
            var prefs = Preferences.Load(); var beforePreferences = File.Exists(Preferences.PathName) ? File.ReadAllBytes(Preferences.PathName) : null;
            await using var voice = new NeuralVoice(); await using var engine = new EngineHost(voice); var calls = 0;
            var agent = new AgentController(engine, voice, prefs, (_, _, _) => { calls++; throw new IOException("Esta petición debe usar el generador local directamente."); });
            using var images = agent.Images; using var vision = agent.Vision; using var scenes = agent.Scenes; using var generator = agent.ImageGenerator; using var connections = agent.Connections; using var updates = agent.Updates;
            generator.Progress += Console.WriteLine;
            Assert(agent.Skills.Get("imagegen").Backend == "imagegen" && generator.Enabled && generator.Configured, "Se está usando una skill vieja o falta activar/configurar el generador local.");
            var changes = Directory.EnumerateFiles(AppPaths.Changes).Count();
            var raw = await agent.SubmitAsync(request, CancellationToken.None);
            var image = JsonSerializer.Deserialize<GeneratedImage>(raw, AppPaths.Json)!;
            Assert(image.Status == "generated", "La petición no produjo una imagen: " + raw);
            Assert(calls == 0 && agent.LastActions.SequenceEqual(new[] { "image_generate" }), "La petición derivó al modelo o a MCP.");
            Assert(Directory.EnumerateFiles(AppPaths.Changes).Count() == changes && (beforePreferences is null ? !File.Exists(Preferences.PathName) : File.ReadAllBytes(Preferences.PathName).SequenceEqual(beforePreferences)), "La petición alteró propuestas o permisos.");
            var sample = Path.Combine(AppPaths.Root, "checks", "generated-cat-" + Guid.NewGuid().ToString("N")[..8] + ".png"); generator.SaveHuman(image.Id, sample);
            Assert(ImageGeneration.ExistingHashHuman(sample) == image.Sha256 && image.Width == 512 && image.Height == 512, "PNG inválido.");
            report["status"] = "complete"; report["sample"] = sample; report["result"] = image; report["languageModelCalls"] = calls; report["actions"] = agent.LastActions;
        }
        catch (Exception error) { report["status"] = "failed"; report["error"] = error.ToString(); }
        AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "generation-chat.json"), report); return report["status"]?.ToString() == "complete" ? 0 : 1;
    }
    internal static async Task GuiAsync(MainForm form, AgentController agent)
    {
        var rows = new List<object>(); var file = Path.Combine(AppPaths.Root, "checks", "generation-gui.json");
        try
        {
            using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppPaths.Root, "checks", "generation.json"))); var sample = report.RootElement.GetProperty("sample").GetString()!;
            var image = agent.ImageGenerator.LoadDiagnosticPreview(sample); form.ShowGenerationFromGui(image.Id);
            foreach (var size in new[] { new Size(800, 600), new Size(1024, 650), new Size(1366, 728), new Size(1520, 900) })
            {
                form.Size = size; await Task.Delay(100); var state = form.GenerationUiSnapshot(); rows.Add(state); using var json = JsonDocument.Parse(JsonSerializer.Serialize(state));
                Assert(json.RootElement.GetProperty("createVisible").GetBoolean() && json.RootElement.GetProperty("saveVisible").GetBoolean() && json.RootElement.GetProperty("previewLoaded").GetBoolean(), "Controles fuera de la ventana.");
                form.CaptureWindow(Path.Combine(AppPaths.Root, "checks", "generation-" + size.Width + ".png"));
            }
            AppPaths.SaveJson(file, new { status = "complete", rows, scope = "GUI real con el PNG ya generado por el componente activo; cuatro tamaños. No ejecuta otra IA ni genera otra imagen." });
        }
        catch (Exception error) { AppPaths.SaveJson(file, new { status = "failed", rows, error = error.ToString() }); }
        finally { form.Close(); }
    }
}
