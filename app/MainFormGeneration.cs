using System.Text.Json;

namespace SheepCode;
internal sealed partial class MainForm
{
    private readonly TextBox _generationPrompt = new() { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BackColor = Background, ForeColor = Foreground, PlaceholderText = "🐑 Describe la imagen… Ejemplo: a cute sheep and a pink kitten, pastel illustration" };
    private readonly PictureBox _generatedPreview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Background };
    private readonly Button _generateImage = ButtonFor("🎨 Crear", true), _saveGenerated = ButtonFor("💾 PNG"), _installGenerator = ButtonFor("📦");
    private readonly CheckBox _generationEnabled = new() { AutoSize = true, Text = "🎨 Activo" };
    private readonly ComboBox _generationBackend = new() { Width = 72, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
    private readonly Label _generationInfo = LabelFor("🎨 Instala el generador local con 📦.", 9);
    private bool _refreshingGeneration;
    private string? _generatedId;
    private void BuildGenerationPanel()
    {
        var page = new Panel { Text = "🎨 Crear", BackColor = Background, Padding = new(6) }; var stack = Stack(58, 36, -1, 34); page.Controls.Add(stack);
        _generationPrompt.Font = new("Segoe UI", 9); stack.Controls.Add(_generationPrompt, 0, 0);
        foreach (var (button, width) in new[] { (_generateImage, 84), (_saveGenerated, 78), (_installGenerator, 32) }) { button.Font = new("Segoe UI", 9); button.AutoSize = false; button.Size = new(width, 26); button.Margin = new(1); }
        var tools = Flow(_generateImage, _saveGenerated, _installGenerator); tools.Padding = new(1); stack.Controls.Add(tools, 0, 1); stack.Controls.Add(_generatedPreview, 0, 2);
        _generationBackend.Items.AddRange(["auto", "cpu"]); _generationBackend.SelectedIndex = 0; StyleCombo(_generationBackend);
        _generationEnabled.Font = new("Segoe UI", 9); _generationBackend.Font = new("Segoe UI", 9); _generationBackend.Margin = new(1); _generationInfo.Dock = DockStyle.None; _generationInfo.Width = 130; _generationInfo.Height = 25;
        var settings = Flow(_generationEnabled, _generationBackend, _generationInfo); settings.Padding = new(1); stack.Controls.Add(settings, 0, 3); _tabs.TabPages.Add(page);
        _generateImage.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_generationPrompt.Text)) { _generationPrompt.Focus(); return; }
            var prompt = _generationPrompt.Text.Trim();
            StartTask(async token => { var raw = await _agent.SubmitAsync("Genera una imagen: " + prompt, token); var result = JsonSerializer.Deserialize<GeneratedImage>(raw, AppPaths.Json)!;
                Ui(() => { if (result.Status == "generated") ShowGenerated(result.Id); else _generationInfo.Text = result.Error; }); });
        };
        _saveGenerated.Click += (_, _) =>
        {
            if (_generatedId is null) return;
            using var picker = new SaveFileDialog { Title = "🐑 Guarda tu creación", Filter = "Imagen PNG|*.png", FileName = "SheepCode-image.png", AddExtension = true, OverwritePrompt = false };
            if (picker.ShowDialog(this) == DialogResult.OK) Guard(() =>
            {
                string? expected = null;
                if (File.Exists(picker.FileName))
                {
                    expected = ImageGeneration.ExistingHashHuman(picker.FileName);
                    if (MessageBox.Show(this, "¿Reemplazar esta imagen? Se conserva una copia .bak del archivo anterior.", "🐑 Guardar imagen", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                }
                var backup = _agent.ImageGenerator.SaveHuman(_generatedId, picker.FileName, expected);
                AppendChat("assistant", "🎨 Imagen guardada: " + picker.FileName + (backup is null ? "" : "\nCopia anterior: " + backup));
            });
        };
        _installGenerator.Click += (_, _) =>
        {
            using var folder = new FolderBrowserDialog { Description = "Carpeta de pesos para el generador local (2,02 GB)", UseDescriptionForTitle = true, InitialDirectory = AppPaths.ModelRoot };
            if (folder.ShowDialog(this) == DialogResult.OK) StartTask(async token => { await _agent.ImageGenerator.InstallHumanAsync(folder.SelectedPath, token); Ui(RefreshGenerationPanel); });
        };
        _generationEnabled.CheckedChanged += (_, _) => { if (_refreshingGeneration || !_agent.Skills.Enabled("imagegen")) return; _preferences.GenerateImages = _generationEnabled.Checked; _preferences.Save(); if (!_preferences.GenerateImages) _agent.ImageGenerator.Cancel(); };
        _generationBackend.SelectedIndexChanged += (_, _) => { if (_refreshingGeneration || !_agent.ImageGenerator.Configured || _generationBackend.SelectedItem is not string mode) return; StartTask(async token => { await _agent.ImageGenerator.SelectHumanAsync(mode, token); Ui(RefreshGenerationPanel); }); };
        _agent.ImageGenerator.Changed += () => Ui(() => { if (_agent.ImageGenerator.Images.LastOrDefault() is { } last) ShowGenerated(last.Id); else { _generatedId = null; var old = _generatedPreview.Image; _generatedPreview.Image = null; old?.Dispose(); } RefreshGenerationPanel(); });
        var tips = new ToolTip(); tips.SetToolTip(_installGenerator, "Instala el componente local SD-Turbo y sus runtimes. Descarga verificada; los modelos de código y voz mantienen sus perfiles."); tips.SetToolTip(_generationBackend, "auto usa una RTX verificada para difusión/VAE; CPU es explícito y puede tardar. La RX 580 se reserva para voz."); tips.SetToolTip(_generationInfo, ImageGeneration.Limits);
        RefreshGenerationPanel();
    }
    private void ShowGenerated(string id)
    {
        var png = _agent.ImageGenerator.Preview(id); using var bytes = new MemoryStream(png, false); using var image = Image.FromStream(bytes); var old = _generatedPreview.Image;
        _generatedPreview.Image = new Bitmap(image); old?.Dispose(); _generatedId = id; _saveGenerated.Enabled = !_busy; _generationInfo.Text = "512 × 512 · vista previa";
    }
    private void RefreshGenerationPanel()
    {
        _refreshingGeneration = true; _generationEnabled.Checked = _preferences.GenerateImages; _generationEnabled.Enabled = !_busy && _agent.Skills.Enabled("imagegen");
        _generateImage.Enabled = !_busy && _agent.ImageGenerator.Enabled; _installGenerator.Enabled = !_busy && _agent.Skills.Enabled("imagegen"); _generationBackend.Enabled = !_busy && _agent.ImageGenerator.Configured && _agent.Skills.Enabled("imagegen");
        _saveGenerated.Enabled = !_busy && _generatedId is not null; _generationPrompt.Enabled = !_busy;
        if (_generatedId is null) _generationInfo.Text = _agent.ImageGenerator.Configured ? "Modelo local preparado" : "Instala con 📦 · 2 GB";
        _refreshingGeneration = false;
    }
    internal object GenerationUiSnapshot() => new { previewLoaded = _generatedPreview.Image is not null, previewVisible = _generatedPreview.Visible,
        promptHeight = _generationPrompt.Height, createVisible = _generateImage.Visible && _generateImage.Parent!.ClientRectangle.Contains(_generateImage.Bounds),
        saveVisible = _saveGenerated.Visible && _saveGenerated.Parent!.ClientRectangle.Contains(_saveGenerated.Bounds), id = _generatedId };
    internal void ShowGenerationFromGui(string id) { _tabs.SelectedIndex = 10; ShowGenerated(id); }
    internal void ShowLastGeneratedFromGui() => Guard(() =>
    {
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppPaths.Root, "checks", "generation-chat.json")));
        if (report.RootElement.GetProperty("status").GetString() != "complete") throw new IOException("La última comprobación de imagen no terminó.");
        var stored = report.RootElement.GetProperty("result").Deserialize<GeneratedImage>(AppPaths.Json)!;
        var loaded = _agent.ImageGenerator.LoadDiagnosticPreview(report.RootElement.GetProperty("sample").GetString()!);
        if (loaded.Sha256 != stored.Sha256) { _agent.ImageGenerator.ClearHuman(); throw new IOException("La imagen guardada cambió desde su creación."); }
        _generationPrompt.Text = stored.Prompt; ShowGenerationFromGui(loaded.Id);
    });
}
