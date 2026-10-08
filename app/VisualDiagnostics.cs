using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;

namespace SheepCode;

internal static class VisualDiagnostics
{
    private static void Assert(bool success, string message) { if (!success) throw new IOException(message); }
    internal static async Task<int> InstallAsync(string folder)
    {
        try
        {
            var prefs = Preferences.Load(); var skills = new SkillRegistry(prefs); skills.Reload(); using var vision = new VisionHost(prefs, skills);
            vision.Progress += message => Console.WriteLine(message); await vision.InstallHumanAsync(folder, CancellationToken.None);
            AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "vision-install.json"), new { status = "complete", component = vision.Status(), modelFolder = folder }); return 0;
        }
        catch (Exception error) { AppPaths.SaveJson(Path.Combine(AppPaths.Root, "checks", "vision-install.json"), new { status = "failed", error = error.ToString() }); return 1; }
    }
    internal static string DrawHouse(string folder)
    {
        Directory.CreateDirectory(folder); var path = Path.Combine(folder, "drawing.png"); using var image = new Bitmap(640, 480); using var graphics = Graphics.FromImage(image);
        graphics.Clear(Color.White); using var red = new SolidBrush(Color.Red); using var blue = new SolidBrush(Color.LightBlue); using var brown = new SolidBrush(Color.SaddleBrown);
        graphics.FillRectangle(blue, 170, 210, 300, 210); graphics.FillPolygon(red, new Point[] { new(140, 210), new(320, 55), new(500, 210) });
        graphics.FillRectangle(brown, 290, 310, 70, 110); using var windows = new SolidBrush(Color.White); graphics.FillRectangle(windows, 200, 250, 65, 65); graphics.FillRectangle(windows, 380, 250, 65, 65);
        using var outline = new Pen(Color.Black, 4); graphics.DrawRectangle(outline, 170, 210, 300, 210); graphics.DrawPolygon(outline, new Point[] { new(140, 210), new(320, 55), new(500, 210) });
        image.Save(path, ImageFormat.Png); return path;
    }
    internal static async Task FixturesAsync(string project, CancellationToken token)
    {
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "fixture.prefab"), "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!1 &100\nGameObject:\n  m_Name: SheepCube\n  m_Component:\n  - component: {fileID: 200}\n  m_IsActive: 1\n--- !u!4 &200\nTransform:\n  m_GameObject: {fileID: 100}\n  m_LocalPosition: {x: 1, y: 2, z: 3}\n  m_LocalScale: {x: 1, y: 1, z: 1}\n  m_Father: {fileID: 0}\n--- !u!114 &300\nMonoBehaviour:\n  m_GameObject: {fileID: 100}\n  m_Script: {fileID: 11500000, guid: 1234567890abcdef1234567890abcdef, type: 3}\n");
        File.Copy(Path.Combine(project, "fixture.prefab"), Path.Combine(project, "fixture.unity"), true); File.Copy(Path.Combine(project, "fixture.prefab"), Path.Combine(project, "fixture.asset"), true);
        var blender = SceneTools.FindBlender() ?? throw new IOException("Falta Blender para la prueba integrada FBX/BLEND.");
        var blend = Path.Combine(project, "cube.blend"); var fbx = Path.Combine(project, "cube.fbx"); var script = Path.Combine(project, "trusted-fixture.py"); var canary = Path.Combine(project, "must-not-execute.txt");
        // Diagnostic fixture only, from fixed code authored here. No project code or third-party script is run.
        var python = "import bpy\nbpy.ops.object.select_all(action='SELECT')\nbpy.ops.object.delete(use_global=False)\nbpy.ops.mesh.primitive_cube_add(size=2)\nobj=bpy.context.object\nobj.name='SheepCube'\nmaterial=bpy.data.materials.new('KukyBlue')\nobj.data.materials.append(material)\ntext=bpy.data.texts.new('do_not_execute.py')\ntext.use_module=True\ntext.write(" + JsonSerializer.Serialize("open(" + JsonSerializer.Serialize(canary) + ", 'w').write('unexpected')") + ")\nbpy.ops.wm.save_as_mainfile(filepath=" + JsonSerializer.Serialize(blend) + ")\nbpy.ops.export_scene.fbx(filepath=" + JsonSerializer.Serialize(fbx) + ",use_selection=True,bake_anim=False)\n";
        File.WriteAllText(script, python); await Distribution.ModelProvisioner.RunAsync(blender, ["--background", "--factory-startup", "--disable-autoexec", "--python-exit-code", "7", "--python", script], project, null, token);
        Assert(File.Exists(blend) && File.Exists(fbx) && !File.Exists(canary), "No se crearon las escenas de prueba o se ejecutó el texto incrustado.");
    }
    internal static async Task<int> RunAsync()
    {
        var checks = new List<object>(); var report = new Dictionary<string, object?> { ["status"] = "starting", ["checks"] = checks,
            ["scope"] = "Flujos de diálogo y herramientas de visión/3D de la instalación activa. Inferencia REAL del componente SmolVLM seleccionado; objetos y dibujo sintéticos deterministas, no pruebas comparativas. No ejecuta otros modelos. No incluye generación del motor de código, voz ni rendimiento de un Celeron físico." };
        var file = Path.Combine(AppPaths.Root, "checks", "vision3d.json"); var saved = File.Exists(Preferences.PathName) ? File.ReadAllBytes(Preferences.PathName) : null;
        var project = Path.Combine(AppPaths.Root, "checks", "vision3d-project");
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(10)); var token = limit.Token;
            await FixturesAsync(project, token); var drawing = DrawHouse(project); var originals = new[] { drawing, Path.Combine(project, "cube.blend"), Path.Combine(project, "cube.fbx"), Path.Combine(project, "fixture.prefab") }.ToDictionary(p => p, p => AppPaths.Hash(File.ReadAllBytes(p)));
            var prefs = new Preferences(); await using var voice = new NeuralVoice(); await using var engine = new EngineHost(voice); var modelCalls = 0;
            var agent = new AgentController(engine, voice, prefs, (_, _, _) => { modelCalls++; throw new IOException("Este diagnóstico de herramientas no debe sustituirse por generación del motor de código."); });
            using var images = agent.Images; using var scenes = agent.Scenes; using var vision = agent.Vision; agent.OpenProject(project);
            Assert(agent.Capabilities.Any(c => c.Name == "vision") && agent.Capabilities.Any(c => c.Name == "scenes") && agent.Skills.Items.Any(s => s.Name == "scenes"), "Capacidades o skill ausentes."); checks.Add(new { test = "capabilities_and_skills", passed = true });
            var prompt = agent.BuildSystemPrompt("Interpreta imágenes y un FBX", out _); Assert(prompt.Contains("image_analyze") && prompt.Contains("scene_inspect") && prompt.Contains("NEVER pixels"), "El motor no recibe las herramientas y límites reales."); checks.Add(new { test = "all_models_prompt", passed = true });
            var staged = agent.Scenes.AttachHuman(Path.Combine(project, "cube.blend"));
            try { await agent.Scenes.ReadAsync(staged.Path, 0, 10, token); throw new IOException("Se leyó una escena sin enviar."); } catch (ArgumentException) { }
            checks.Add(new { test = "unsent_scene_blocked", passed = true });
            var raw = await agent.SubmitAsync("Inspecciona la escena " + staged.Path, token); var blend = JsonSerializer.Deserialize<SceneRead>(raw, AppPaths.Json)!;
            Assert(blend.Status == "inspected" && blend.VertexCount == 8 && blend.FaceCount == 6 && blend.Objects.Any(o => o.Name == "SheepCube") && blend.Materials.Contains("KukyBlue"), "Falló la lectura real de Blender: " + raw);
            Assert(!File.Exists(Path.Combine(project, "must-not-execute.txt")), "El lector ejecutó el texto de Blender."); checks.Add(new { test = "real_blender_dialogue_geometry_materials_no_autoexec", passed = true, result = blend });
            var fbxRaw = await agent.SubmitAsync("Inspecciona el FBX cube.fbx", token); var fbx = JsonSerializer.Deserialize<SceneRead>(fbxRaw, AppPaths.Json)!;
            Assert(fbx.Status == "inspected" && fbx.VertexCount == 8 && fbx.FaceCount == 6, "FBX real falló: " + fbxRaw); checks.Add(new { test = "real_fbx_dialogue", passed = true, result = fbx });
            foreach (var extension in new[] { "prefab", "unity", "asset" })
            {
                var unityRaw = await agent.SubmitAsync("Inspecciona la escena fixture." + extension, token); var unity = JsonSerializer.Deserialize<SceneRead>(unityRaw, AppPaths.Json)!;
                Assert(unity.Status == "inspected" && unity.Objects.Any(o => o.Name == "SheepCube") && unity.Objects.Any(o => o.Type == "Transform" && o.Position.SequenceEqual(new double[] { 1, 2, 3 })) && unity.Objects.Any(o => o.References.Contains("1234567890abcdef1234567890abcdef")), "Unity YAML falló: " + unityRaw);
                checks.Add(new { test = "unity_" + extension + "_structure_transform_guid", passed = true });
            }
            Assert(SceneTools.ReadUnity([0, 1, 2]).Status == "unavailable" && SceneTools.ReadUnity(System.Text.Encoding.UTF8.GetBytes("not Unity")).Status == "unavailable", "Unity binario/no YAML se describió incorrectamente."); checks.Add(new { test = "unity_binary_unavailable", passed = true });
            foreach (var blocked in new[] { "../cube.blend", Path.Combine(project, "cube.blend"), ".env" })
            { try { await scenes.ReadAsync(blocked, 0, 10, token); throw new IOException("Ruta excluida permitida: " + blocked); } catch (InvalidOperationException) { } }
            checks.Add(new { test = "model_scope_absolute_traversal_secret", passed = true });
            var preview = await scenes.PreviewAsync("cube.blend", token); File.WriteAllBytes(Path.Combine(project, "cube-preview.png"), preview.Png);
            Assert(preview.Info.Width == 640 && preview.Info.Height == 480 && preview.Png.Length > 1000, "No se generó la vista geométrica."); checks.Add(new { test = "local_3d_preview", passed = true });
            Assert((await agent.SubmitAsync("Desactiva 3D", token)).Contains("desactivada"), "No cambió el ajuste 3D.");
            try { await scenes.ReadAsync("cube.blend", 0, 10, token); throw new IOException("Se leyó con skill desactivada."); } catch (InvalidOperationException) { }
            await agent.SubmitAsync("Activa 3D", token); await agent.SubmitAsync("Configura Blender auto", token); checks.Add(new { test = "3d_toggle_and_configuration", passed = true });
            var image = images.AttachFileHuman(drawing); images.AcceptPending(); await agent.SubmitAsync("Desactiva la visión", token);
            var disabled = await agent.AnalyzeImageAsync(image.Path, "", token); Assert(disabled.Status == "disabled" && disabled.Description.Length == 0, "Desactivación visual no aplicada.");
            await agent.SubmitAsync("Activa la visión", token); checks.Add(new { test = "vision_toggle_no_cached_description", passed = true });
            Assert(vision.Configured, "El componente visual no está instalado en esta instalación activa.");
            var visionRaw = await agent.SubmitAsync("Interpreta el dibujo " + image.Path, token); var described = JsonSerializer.Deserialize<VisualRead>(visionRaw, AppPaths.Json)!;
            Assert(described.Status == "interpreted" && described.Description.Length > 0 && agent.Session!.Lines.Last().Text.Contains(described.Description), "El flujo visual real no devolvió su descripción: " + visionRaw);
            var houseRecognized = System.Text.RegularExpressions.Regex.IsMatch(described.Description, @"(?i)\b(house|home|building|casa|hogar|edificio)\b");
            checks.Add(new { test = "real_installed_vision_drawing_dialogue", passed = true, semanticMatch = houseRecognized, groundTruth = "dibujo de casa azul con tejado rojo", result = described });
            report["drawingSemanticMatch"] = houseRecognized;
            var objectRaw = await agent.SubmitAsync("Interpreta el objeto 3D cube.blend", token); var objectRead = JsonSerializer.Deserialize<VisualRead>(objectRaw, AppPaths.Json)!;
            Assert(objectRead.Status == "interpreted" && objectRead.Description.Length > 0, "El flujo de forma 3D falló: " + objectRaw);
            var cubeRecognized = System.Text.RegularExpressions.Regex.IsMatch(objectRead.Description, @"(?i)\b(cube|box|cuboid|cubo|caja|block)\b");
            checks.Add(new { test = "real_installed_vision_3d_object", passed = true, semanticMatch = cubeRecognized, groundTruth = "cubo", result = objectRead }); report["objectSemanticMatch"] = cubeRecognized;
            using (var cancel = new CancellationTokenSource())
            {
                var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Progress(string message) { if (message.StartsWith("Cargando")) began.TrySetResult(); }
                vision.Progress += Progress; var pending = agent.AnalyzeImageAsync(image.Path, "Describe this picture.", cancel.Token);
                await began.Task.WaitAsync(TimeSpan.FromSeconds(20)); var watch = Stopwatch.StartNew(); cancel.Cancel();
                try { await pending; throw new IOException("Cancelación visual no propagada."); } catch (OperationCanceledException) { }
                vision.Progress -= Progress; Assert(watch.Elapsed < TimeSpan.FromSeconds(5), "La cancelación tardó más de cinco segundos."); checks.Add(new { test = "real_owned_visual_process_cancellation", passed = true, seconds = watch.Elapsed.TotalSeconds });
            }
            var pages = await scenes.ReadAsync("fixture.prefab", 1, 1, token); Assert(pages.Start == 1 && pages.Objects.Length == 1 && pages.Truncated, "No funcionan offsets 3D."); checks.Add(new { test = "scene_pagination", passed = true });
            Assert(!prefs.AllowChecks && modelCalls == 0, "Una herramienta amplió permisos o ejecutó otro modelo."); checks.Add(new { test = "read_only_no_arbitrary_checks_or_model_substitution", passed = true });
            Assert(originals.All(pair => AppPaths.Hash(File.ReadAllBytes(pair.Key)) == pair.Value), "Se modificó un original."); checks.Add(new { test = "originals_preserved", passed = true });
            agent.OpenProject(project); Assert(scenes.Attached.Count == 0 && images.Attached.Count == 0, "Los adjuntos sobrevivieron al cambio de proyecto."); checks.Add(new { test = "project_switch_clears_attachments", passed = true });
            report["status"] = houseRecognized && cubeRecognized ? "complete" : "complete_with_semantic_limits"; report["vision"] = vision.Status();
        }
        catch (Exception error) { report["status"] = "failed"; report["error"] = error.ToString(); }
        finally { if (saved is not null) File.WriteAllBytes(Preferences.PathName, saved); else if (File.Exists(Preferences.PathName)) File.Delete(Preferences.PathName); AppPaths.SaveJson(file, report); }
        return report["status"]?.ToString() == "failed" ? 1 : 0;
    }
    internal static async Task GuiAsync(MainForm form, AgentController agent)
    {
        var rows = new List<object>(); var output = Path.Combine(AppPaths.Root, "checks", "vision3d-gui.json");
        try
        {
            var project = Path.Combine(AppPaths.Root, "checks", "vision3d-project"); agent.OpenProject(project); form.SceneFromGui(Path.Combine(project, "cube.fbx"));
            await form.InspectSceneFromGuiAsync();
            using (var data = JsonDocument.Parse(JsonSerializer.Serialize(form.SceneUiSnapshot()))) Assert(data.RootElement.GetProperty("text").GetString()!.Contains("8 vértices"), "El botón Datos no mostró la malla.");
            await form.PreviewSceneFromGuiAsync();
            foreach (var size in new[] { new Size(800, 600), new Size(1024, 650), new Size(1366, 728), new Size(1520, 900) })
            {
                form.Size = size; await Task.Delay(100); var state = form.SceneUiSnapshot(); using var data = JsonDocument.Parse(JsonSerializer.Serialize(state));
                form.CaptureWindow(Path.Combine(AppPaths.Root, "checks", "3d-" + size.Width + ".png")); rows.Add(state);
                Assert(data.RootElement.GetProperty("inspectVisible").GetBoolean() && data.RootElement.GetProperty("shapeVisible").GetBoolean() && data.RootElement.GetProperty("previewLoaded").GetBoolean() && data.RootElement.GetProperty("previewVisible").GetBoolean(), "Se cortaron controles 3D en " + size);
                Assert(data.RootElement.GetProperty("previewSize").GetProperty("height").GetInt32() >= 80, "Vista 3D demasiado pequeña.");
            }
            AppPaths.SaveJson(output, new { status = "complete", rows, scope = "GUI real instalada: adjuntar FBX, Datos, Vista y cuatro tamaños; no ejecuta una IA adicional." });
        }
        catch (Exception error) { AppPaths.SaveJson(output, new { status = "failed", rows, error = error.ToString() }); }
        finally { form.Close(); }
    }
}
