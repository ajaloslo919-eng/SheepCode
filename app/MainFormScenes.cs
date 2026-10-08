using System.Text.Json;

namespace SheepCode;

internal sealed partial class MainForm
{
    private readonly ListBox _sceneList = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Surface, ForeColor = Foreground, Font = new("Segoe UI", 9) };
    private readonly RichTextBox _sceneDetails = CodeBox(true);
    private readonly PictureBox _scenePreview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White, Visible = false };
    private readonly Button _sceneInspect = ButtonFor("🔎 Datos"), _sceneShow = ButtonFor("🧊 Vista"), _sceneInterpret = ButtonFor("👁️ Forma"), _sceneRemove = ButtonFor("✕");
    private readonly CheckBox _scenesEnabled = new() { Text = "🧊 3D", AutoSize = true };
    private bool _refreshingScenes;
    private string? _projectScene;
    private string _sceneLastText = "", _sceneLastPath = "";
    private sealed record SceneChoice(string Path, string Name, bool Pending) { public override string ToString() => (Pending ? "🌷 Por enviar · " : "🧊 ") + Name; }
    private void BuildScenesPanel()
    {
        var page = new Panel { BackColor = Background, Padding = new(6), Text = "🧊 3D" }; var stack = Stack(42, 30, -1, 42); page.Controls.Add(stack);
        var attach = ButtonFor("📎"); attach.Click += (_, _) => ChooseScenes();
        foreach (var button in new[] { attach, _sceneInspect, _sceneShow, _sceneInterpret, _sceneRemove }) { button.Font = new("Segoe UI", 9); button.Padding = new(4, 2, 4, 2); }
        var compact = new[] { (attach, 32), (_sceneInspect, 80), (_sceneShow, 74), (_sceneInterpret, 80), (_sceneRemove, 30) };
        foreach (var (button, width) in compact) { button.AutoSize = false; button.Size = new(width, 28); button.Margin = new(1); }
        var tools = Flow(attach, _sceneInspect, _sceneShow, _sceneInterpret, _sceneRemove); tools.Padding = new(2); stack.Controls.Add(tools, 0, 0); stack.Controls.Add(_sceneList, 0, 1);
        var view = new Panel { Dock = DockStyle.Fill }; _sceneDetails.WordWrap = true; view.Controls.Add(_scenePreview); view.Controls.Add(_sceneDetails); stack.Controls.Add(view, 0, 2);
        var blender = ButtonFor("Blender…"); blender.Font = new("Segoe UI", 9); blender.AutoSize = false; blender.Size = new(84, 28); blender.Margin = new(1); blender.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Filter = "Blender|blender.exe", Title = "Elige Blender instalado" };
            if (picker.ShowDialog(this) == DialogResult.OK) StartTask(async token => { await _agent.SubmitAsync("Configura Blender " + picker.FileName, token); Ui(RefreshScenesPanel); });
        };
        var scope = LabelFor("Solo lectura", 8); scope.Dock = DockStyle.None; scope.Width = 76; scope.Height = 30;
        var settings = Flow(_scenesEnabled, blender, scope); settings.Padding = new(2); stack.Controls.Add(settings, 0, 3); _tabs.TabPages.Add(page);
        _sceneDetails.Text = "🐑 Sheep & 🐱 Kuky · FBX, Blender y Unity\n\nAdjunta un modelo con 📎 o abre el proyecto y selecciona su escena.\n\n" + SceneTools.Limits;
        _sceneList.SelectedIndexChanged += (_, _) => { if (!_refreshingScenes) ShowSceneText(); };
        _scenesEnabled.CheckedChanged += (_, _) => { if (_refreshingScenes) return; Guard(() => _agent.Skills.SetEnabled("scenes", _scenesEnabled.Checked)); };
        _agent.Scenes.Changed += () => Ui(RefreshScenesPanel); _agent.Skills.Changed += () => Ui(RefreshScenesPanel);
        _sceneRemove.Click += (_, _) => { if (_sceneList.SelectedItem is SceneChoice selected && selected.Path.StartsWith("scene-")) Guard(() => _agent.Scenes.RemoveHuman(selected.Path)); };
        _sceneInspect.Click += (_, _) =>
        {
            if (_sceneList.SelectedItem is not SceneChoice selected) return;
            StartTask(async token => { var raw = await _agent.SubmitAsync("Inspecciona la escena " + selected.Path, token); var data = JsonSerializer.Deserialize<SceneRead>(raw, AppPaths.Json)!;
                Ui(() => { _sceneLastPath = selected.Path; _sceneLastText = SceneDisplay(data); ShowSceneText(); }); });
        };
        _sceneShow.Click += (_, _) =>
        {
            if (_sceneList.SelectedItem is not SceneChoice selected) return;
            StartTask(async token => { if (_agent.Scenes.Pending.Any(s => s.Path == selected.Path)) _agent.Scenes.AcceptPending(selected.Path); var input = await _agent.Scenes.PreviewAsync(selected.Path, token);
                Ui(() => { using var bytes = new MemoryStream(input.Png); using var image = Image.FromStream(bytes); var old = _scenePreview.Image; _scenePreview.Image = new Bitmap(image); old?.Dispose(); _sceneDetails.Visible = false; _scenePreview.Visible = true; _scenePreview.BringToFront(); }); });
        };
        _sceneInterpret.Click += (_, _) =>
        {
            if (_sceneList.SelectedItem is not SceneChoice selected) return;
            StartTask(async token => { var raw = await _agent.SubmitAsync("Interpreta el objeto 3D " + selected.Path, token); var read = JsonSerializer.Deserialize<VisualRead>(raw, AppPaths.Json)!;
                Ui(() => { _sceneLastPath = selected.Path; _sceneLastText = AgentController.FriendlyVisual(read); ShowSceneText(); }); });
        };
        var tips = new ToolTip(); tips.SetToolTip(attach, SceneTools.Formats); tips.SetToolTip(_sceneInspect, "Leer geometría base, materiales y objetos o componentes/transformaciones de Unity.");
        tips.SetToolTip(_sceneShow, "Vista geométrica local FBX/BLEND; no renderiza scripts, shaders ni animaciones."); tips.SetToolTip(_sceneInterpret, "Interpretar la forma del modelo con visión local. Necesita el componente visual activado.");
        RefreshScenesPanel();
    }
    private void ChooseScenes()
    {
        if (_busy) return;
        using var picker = new OpenFileDialog { Title = "🧊 Un modelo para Sheep & Kuky", Filter = "Modelos y escenas|*.fbx;*.blend;*.unity;*.prefab;*.asset", Multiselect = true };
        if (picker.ShowDialog(this) == DialogResult.OK) AddScenesHuman(picker.FileNames);
    }
    private void AddScenesHuman(IEnumerable<string> paths) { foreach (var path in paths) Guard(() => _agent.Scenes.AttachHuman(path)); _input.Focus(); }
    private void RefreshScenesPanel()
    {
        if (_sceneList.IsDisposed) return; _refreshingScenes = true; var selected = (_sceneList.SelectedItem as SceneChoice)?.Path;
        _sceneList.Items.Clear(); foreach (var info in _agent.Scenes.Pending) _sceneList.Items.Add(new SceneChoice(info.Path, info.Name, true)); foreach (var info in _agent.Scenes.Attached) _sceneList.Items.Add(new SceneChoice(info.Path, info.Name, false));
        if (_projectScene is { } relative && _agent.Workspace is not null) _sceneList.Items.Add(new SceneChoice(relative, relative, false));
        var index = _sceneList.Items.Cast<SceneChoice>().ToList().FindIndex(s => s.Path == selected); _sceneList.SelectedIndex = index >= 0 ? index : _sceneList.Items.Count > 0 ? 0 : -1;
        _scenesEnabled.Checked = _agent.Scenes.Enabled; _scenesEnabled.Enabled = !_busy;
        var usable = !_busy && _agent.Scenes.Enabled && _sceneList.SelectedItem is not null; _sceneInspect.Enabled = _sceneShow.Enabled = usable;
        _sceneInterpret.Enabled = usable && _agent.Vision.Enabled; _sceneRemove.Enabled = usable && (_sceneList.SelectedItem as SceneChoice)?.Path.StartsWith("scene-") == true;
        _refreshingScenes = false;
    }
    private void ShowSceneText()
    {
        var selected = _sceneList.SelectedItem as SceneChoice; _sceneDetails.Text = selected is null ? SceneTools.Limits : _sceneLastPath == selected.Path ? _sceneLastText : selected.Name + "\n\nPulsa 🔎 Datos para inspeccionar o 🧊 Vista para ver la geometría.\n\n" + SceneTools.Limits;
        _scenePreview.Visible = false; _sceneDetails.Visible = true; _sceneDetails.BringToFront(); RefreshSceneButtons();
    }
    private void RefreshSceneButtons() { var ready = !_busy && _agent.Scenes.Enabled && _sceneList.SelectedItem is not null; _sceneInspect.Enabled = _sceneShow.Enabled = ready; _sceneInterpret.Enabled = ready && _agent.Vision.Enabled; }
    private static string SceneDisplay(SceneRead read) => read.Status != "inspected" ? read.Error + "\n\n" + read.Notice :
        "🧊 " + read.File.Name + " · " + read.Engine + "\n" + read.ObjectCount + " objetos · " + read.MeshCount + " mallas\n" + read.VertexCount + " vértices · " + read.FaceCount + " caras\nMateriales: " + string.Join(", ", read.Materials) + "\n\n" +
        string.Join("\n\n", read.Objects.Select(o => "• " + (o.Name.Length > 0 ? o.Name : o.Id) + " · " + o.Type + "\nPadre/ref.: " + o.Parent + " · vértices " + o.Vertices + " · caras " + o.Faces + "\nComponentes: " + string.Join(", ", o.Components) + "\nGUID/referencias: " + string.Join(", ", o.References))) +
        (read.Truncated ? "\n[Informe parcial; pide scene_inspect con start/count.]" : "") + "\n\n" + read.Notice;
    private void OpenProjectScene(string path) { _projectScene = path; RefreshScenesPanel(); _sceneList.SelectedIndex = _sceneList.Items.Count - 1; _tabs.SelectedIndex = 9; ShowSceneText(); }
    internal object SceneUiSnapshot() => new { selected = (_sceneList.SelectedItem as SceneChoice)?.Path, panelVisible = _tabs.SelectedIndex == 9,
        previewVisible = _scenePreview.Visible, previewLoaded = _scenePreview.Image is not null, text = _sceneDetails.Text,
        inspectVisible = _sceneInspect.Visible && _sceneInspect.Parent!.ClientRectangle.Contains(_sceneInspect.Bounds), shapeVisible = _sceneInterpret.Visible && _sceneInterpret.Parent!.ClientRectangle.Contains(_sceneInterpret.Bounds),
        previewSize = new { width = _scenePreview.Width, height = _scenePreview.Height } };
    internal void SceneFromGui(string path) { AddScenesHuman([path]); _tabs.SelectedIndex = 9; }
    internal async Task InspectSceneFromGuiAsync() { _sceneInspect.PerformClick(); if (_running is not null) await _running.WaitAsync(TimeSpan.FromSeconds(100)); }
    internal async Task PreviewSceneFromGuiAsync() { _sceneShow.PerformClick(); if (_running is not null) await _running.WaitAsync(TimeSpan.FromSeconds(100)); }
}
