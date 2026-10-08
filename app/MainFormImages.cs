using System.Text.Json;

namespace SheepCode;

internal sealed partial class MainForm
{
    private readonly Button _attachImage = ButtonFor("🖼️"), _removeImage = ButtonFor("✕ Quitar"), _readImage = ButtonFor("🔎 Leer texto");
    private readonly Button _showImagePreview = ButtonFor("🖼️");
    private readonly Button _interpretImage = ButtonFor("👁️ Ver"), _installVision = ButtonFor("📦");
    private readonly CheckBox _visionEnabled = new() { Text = "👁️", AutoSize = true };
    private readonly CheckBox _imageOcr = new() { Text = "🔎 OCR", AutoSize = true };
    private readonly ComboBox _imageLanguage = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Width = 85 };
    private readonly ListBox _imageList = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
    private readonly PictureBox _imagePreview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Background };
    private readonly RichTextBox _imageDetails = CodeBox(true);
    private readonly FlowLayoutPanel _imageTray = new() { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Padding = new(2), BackColor = Surface };
    private TableLayoutPanel _conversation = null!;
    private string? _previewProjectImage;
    private string? _ocrDisplayPath;
    private string _ocrDisplayText = "";
    private bool _refreshingImages;
    private sealed record ImageChoice(ImageInfo Info, bool Pending)
    {
        public override string ToString() => (Pending ? "🌷 Por enviar · " : "🖼️ ") + Info.Name + " · " + Info.Width + " × " + Info.Height;
    }
    private void BuildImagesPanel()
    {
        var page = new Panel { Text = "Imágenes", BackColor = Background, Padding = new(6) };
        var stack = Stack(42, 30, -1, 42); page.Controls.Add(stack);
        var add = ButtonFor("🖼️"); add.Click += (_, _) => ChooseImages();
        _removeImage.Text = "✕"; _readImage.Text = "🔎 Texto";
        foreach (var button in new[] { add, _removeImage, _readImage, _interpretImage, _installVision }) { button.Font = new("Segoe UI", 9); button.Padding = new(5, 2, 5, 2); }
        foreach (var (button, width) in new[] { (add, 34), (_removeImage, 30), (_readImage, 82), (_interpretImage, 74), (_installVision, 34), (_showImagePreview, 34) }) { button.AutoSize = false; button.Size = new(width, 28); button.Margin = new(1); }
        var tools = Flow(add, _removeImage, _readImage, _interpretImage); tools.Padding = new(2);
        stack.Controls.Add(tools, 0, 0);
        _imageList.BackColor = Surface; _imageList.ForeColor = Foreground; _imageList.Font = new("Segoe UI", 9); _imageList.ItemHeight = 24;
        stack.Controls.Add(_imageList, 0, 1);
        var view = new Panel { Dock = DockStyle.Fill, BackColor = Background }; view.Controls.Add(_imageDetails); view.Controls.Add(_imagePreview); stack.Controls.Add(view, 0, 2);
        _imageDetails.WordWrap = true; _imageDetails.Font = new("Segoe UI", 10); _imageDetails.Visible = false;
        _showImagePreview.AutoSize = false; _showImagePreview.Width = 34; _showImagePreview.Click += (_, _) => PreviewSelectedImage();
        _installVision.AutoSize = false; _installVision.Width = 34;
        var settings = Flow(_imageOcr, _imageLanguage, _showImagePreview, _visionEnabled, _installVision); settings.Padding = new(2); stack.Controls.Add(settings, 0, 3); _tabs.TabPages.Add(page);
        _imageLanguage.Items.Add("auto"); _imageLanguage.SelectedIndex = 0;
        _imageLanguage.BackColor = Background; _imageLanguage.ForeColor = Foreground; StyleCombo(_imageLanguage);
        _imageDetails.Text = "🌸 Adjunta una captura con 🖼️, Ctrl+V o arrastrando archivos.\n" + ImageTools.VisionLimit;
        _imageList.SelectedIndexChanged += (_, _) => PreviewSelectedImage();
        _interpretImage.Click += (_, _) =>
        {
            if (_imageList.SelectedItem is not ImageChoice choice) return;
            StartTask(async token =>
            {
                var raw = await _agent.SubmitAsync("Interpreta la imagen " + choice.Info.Path, token);
                var read = JsonSerializer.Deserialize<VisualRead>(raw, AppPaths.Json)!;
                Ui(() => { _ocrDisplayPath = choice.Info.Path; _ocrDisplayText = AgentController.FriendlyVisual(read); _imageDetails.Text = _ocrDisplayText; _imageDetails.Visible = true; _imagePreview.Visible = false; });
            });
        };
        _installVision.Click += (_, _) => StartTask(async token => { var reply = await _agent.SubmitAsync("Instala la visión local", token); Ui(() => { RefreshImagesPanel(); _ocrDisplayPath = null; _imageDetails.Text = reply; _imageDetails.Visible = true; _imagePreview.Visible = false; }); });
        _visionEnabled.CheckedChanged += (_, _) =>
        { if (_refreshingImages || !_agent.Images.Enabled) return; _preferences.VisionEnabled = _visionEnabled.Checked; _preferences.Save(); if (!_preferences.VisionEnabled) _agent.Vision.Cancel(); };
        _removeImage.Click += (_, _) => { if (_imageList.SelectedItem is ImageChoice choice && choice.Info.Path.StartsWith("img-")) Guard(() => _agent.Images.RemoveHuman(choice.Info.Path)); };
        _readImage.Click += (_, _) =>
        {
            if (_imageList.SelectedItem is not ImageChoice choice) return;
            StartTask(async token =>
            {
                var result = await _agent.SubmitAsync("Lee la imagen " + choice.Info.Path, token);
                Ui(() =>
                {
                    RefreshSettings();
                    var read = JsonSerializer.Deserialize<ImageRead>(result, AppPaths.Json);
                    _ocrDisplayPath = choice.Info.Path;
                    _ocrDisplayText = read is null ? result : (read.Text.Length > 0 ? read.Text : read.Error.Length > 0 ? read.Error : "No se reconoció texto.") + "\n\n" + read.Notice;
                    _imageDetails.Text = _ocrDisplayText;
                    _imagePreview.Visible = false; _imageDetails.Visible = true; _imageDetails.BringToFront();
                });
            });
        };
        _imageOcr.CheckedChanged += (_, _) =>
        {
            if (_refreshingImages || !_agent.Images.Enabled) return;
            _preferences.ImageOcr = _imageOcr.Checked; _preferences.Save();
            if (!_preferences.ImageOcr) _agent.Images.Cancel();
        };
        _imageLanguage.SelectedIndexChanged += (_, _) =>
        {
            if (_refreshingImages || _imageLanguage.SelectedItem is not string language || language == _preferences.ImageOcrLanguage) return;
            StartTask(async token => { await _agent.Images.SetLanguageHumanAsync(language, token); Ui(RefreshImagesPanel); });
        };
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_tabs.SelectedIndex != 8 || !_agent.Images.Enabled || _busy) return;
            StartTask(async token =>
            {
                await _agent.Images.StatusAsync(token);
                Ui(() =>
                {
                    _refreshingImages = true;
                    using var status = JsonDocument.Parse(JsonSerializer.Serialize(_agent.Images.Status()));
                    _imageLanguage.Items.Clear(); _imageLanguage.Items.Add("auto");
                    foreach (var language in status.RootElement.GetProperty("availableLanguages").EnumerateArray()) _imageLanguage.Items.Add(language.GetString()!);
                    if (!_imageLanguage.Items.Contains(_preferences.ImageOcrLanguage)) _imageLanguage.Items.Add(_preferences.ImageOcrLanguage);
                    _imageLanguage.SelectedItem = _preferences.ImageOcrLanguage;
                    _refreshingImages = false;
                });
            });
        };
        _attachImage.Click += (_, _) => ChooseImages();
        _agent.Images.Changed += () => Ui(RefreshImagesPanel);
        _agent.Skills.Changed += () => Ui(RefreshImagesPanel);
        _input.AllowDrop = true;
        _input.DragEnter += (_, e) => e.Effect = !_busy && e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.All(f => _agent.Images.Enabled && ImageTools.IsImage(f) || _agent.Scenes.Enabled && SceneTools.IsScene(f)) ? DragDropEffects.Copy : DragDropEffects.None;
        _input.DragDrop += (_, e) => { if (!_busy && e.Data?.GetData(DataFormats.FileDrop) is string[] files) { AddImagesHuman(files.Where(ImageTools.IsImage)); AddScenesHuman(files.Where(SceneTools.IsScene)); } };
        _input.KeyDown += (_, e) =>
        {
            if (!e.Control || e.KeyCode != Keys.V || _busy) return;
            try
            {
                if (Clipboard.ContainsImage())
                {
                    e.SuppressKeyPress = true; using var image = Clipboard.GetImage();
                    if (image is not null) { _agent.Images.AttachClipboardHuman(image); _input.Focus(); }
                }
                else if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList().Cast<string>().ToArray();
                    if (files.Length > 0 && files.All(ImageTools.IsImage)) { e.SuppressKeyPress = true; AddImagesHuman(files); }
                }
            }
            catch (Exception error) { e.SuppressKeyPress = true; AppendChat("error", error.Message); }
        };
        var tips = new ToolTip(); tips.SetToolTip(_attachImage, "Adjunta hasta cuatro imágenes. También Ctrl+V para pegar una captura o arrastra PNG/JPEG/BMP/GIF/TIFF al cuadro de texto.");
        tips.SetToolTip(_imageOcr, "Reconoce texto con los idiomas OCR instalados en Windows. Objetos y dibujos usan el componente visual separado.");
        tips.SetToolTip(_visionEnabled, "Activa/desactiva interpretación de objetos y dibujos con visión local, sin subir imágenes.");
        tips.SetToolTip(_interpretImage, "Interpretar imagen o dibujo con SmolVLM 500M local. Necesita el paquete visual instalado.");
        tips.SetToolTip(_installVision, "Instalar visión local verificada: 636 MB de pesos; mantiene el motor de código y la voz. Se descarga bajo petición humana.");
        tips.SetToolTip(_imageLanguage, "auto usa un idioma OCR disponible en Windows. No descarga modelos ni idiomas.");
        RefreshImagesPanel();
    }
    private void ChooseImages()
    {
        if (_busy) return;
        if (!_agent.Images.Enabled) { AppendChat("error", "Imágenes desactivadas. Actívalas en Skills o con «Activa las imágenes»."); return; }
        using var dialog = new OpenFileDialog { Title = "🖼️ Imágenes para Sheep & Kuky", Multiselect = true,
            Filter = "Imágenes|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff" };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddImagesHuman(dialog.FileNames);
    }
    private void AddImagesHuman(IEnumerable<string> files)
    {
        if (_busy) return;
        foreach (var file in files) Guard(() => _agent.Images.AttachFileHuman(file));
        _input.Focus();
    }
    private void RefreshImagesPanel()
    {
        if (_imageList.IsDisposed) return;
        _refreshingImages = true;
        var selected = (_imageList.SelectedItem as ImageChoice)?.Info.Path;
        _imageOcr.Enabled = _agent.Images.Enabled && !_busy; _imageOcr.Checked = _preferences.ImageOcr;
        _visionEnabled.Enabled = _installVision.Enabled = _agent.Images.Enabled && !_busy; _visionEnabled.Checked = _preferences.VisionEnabled;
        _interpretImage.Enabled = _agent.Images.Enabled && !_busy && _imageList.SelectedItem is ImageChoice;
        _attachImage.Enabled = _agent.Images.Enabled && !_busy; _imageLanguage.Enabled = _agent.Images.Enabled && !_busy;
        _imageList.Items.Clear();
        foreach (var item in _agent.Images.Pending) _imageList.Items.Add(new ImageChoice(item, true));
        foreach (var item in _agent.Images.Attached) _imageList.Items.Add(new ImageChoice(item, false));
        if (_previewProjectImage is { } projectPath && _agent.Workspace is not null && _agent.Images.Enabled)
            try { _imageList.Items.Add(new ImageChoice(_agent.Images.Describe(projectPath, true), false)); } catch (Exception) { _previewProjectImage = null; }
        var index = _imageList.Items.Cast<ImageChoice>().ToList().FindIndex(i => i.Info.Path == selected);
        _imageList.SelectedIndex = index >= 0 ? index : _imageList.Items.Count > 0 ? 0 : -1;
        _refreshingImages = false;
        foreach (var thumbnail in _imageTray.Controls.OfType<PictureBox>()) { thumbnail.Image?.Dispose(); thumbnail.Image = null; }
        foreach (Control control in _imageTray.Controls.Cast<Control>().ToArray()) control.Dispose();
        _imageTray.Controls.Clear();
        foreach (var info in _agent.Images.Pending)
        {
            var card = ButtonFor("🖼️ " + info.Name); card.AutoSize = false; card.Width = 140; card.Height = 34;
            card.Click += (_, _) => { _tabs.SelectedIndex = 8; _imageList.SelectedIndex = _imageList.Items.Cast<ImageChoice>().ToList().FindIndex(i => i.Info.Path == info.Path); };
            var remove = ButtonFor("✕"); remove.AutoSize = false; remove.Width = 32; remove.Height = 34;
            remove.Click += (_, _) => Guard(() => _agent.Images.RemoveHuman(info.Path));
            card.Enabled = remove.Enabled = !_busy; _imageTray.Controls.Add(card); _imageTray.Controls.Add(remove);
        }
        _conversation.RowStyles[3].Height = _agent.Images.Pending.Count > 0 ? (float)(44 * DeviceDpi / 96d) : 0;
        _imageTray.Visible = _agent.Images.Pending.Count > 0;
        PreviewSelectedImage(true);
    }
    private void PreviewSelectedImage(bool preserveText = false)
    {
        if (_refreshingImages) return;
        if (!preserveText) _ocrDisplayPath = null;
        var old = _imagePreview.Image; _imagePreview.Image = null; old?.Dispose();
        if (_imageList.SelectedItem is not ImageChoice choice || !_agent.Images.Enabled) { _ocrDisplayPath = null; _interpretImage.Enabled = _readImage.Enabled = _removeImage.Enabled = false; return; }
        _interpretImage.Enabled = !_busy && _preferences.VisionEnabled;
        _readImage.Enabled = !_busy; _removeImage.Enabled = !_busy && choice.Info.Path.StartsWith("img-");
        try
        {
            _imagePreview.Image = _agent.Images.Preview(choice.Info.Path);
            _imageDetails.Visible = false; _imagePreview.Visible = true; _imagePreview.BringToFront();
            _imageDetails.Text = choice.Info.Name + " · " + choice.Info.Format + " · " + choice.Info.Width + " × " + choice.Info.Height + "\n" + ImageTools.VisionLimit +
                (choice.Info.FirstFrameOnly ? " Solo la primera imagen de GIF/TIFF." : "") + "\nID/ruta: " + choice.Info.Path;
            if (_ocrDisplayPath == choice.Info.Path) { _imageDetails.Text = _ocrDisplayText; _imageDetails.Visible = true; _imagePreview.Visible = false; _imageDetails.BringToFront(); }
        }
        catch (Exception e) { _imageDetails.Text = e.Message; }
    }
    private void OpenProjectImage(string path)
    {
        _previewProjectImage = path; RefreshImagesPanel();
        _imageList.SelectedIndex = _imageList.Items.Cast<ImageChoice>().ToList().FindIndex(i => i.Info.Path == path);
        _tabs.SelectedIndex = 8;
    }
    internal object ImageUiSnapshot() => new { attachedButtonVisible = _attachImage.Visible && _attachImage.Parent!.ClientRectangle.Contains(_attachImage.Bounds),
        inputHeight = _input.Height, sendEnabled = _send.Enabled, pending = _agent.Images.Pending.Count, attached = _agent.Images.Attached.Count,
        previewVisible = _imagePreview.Visible, previewLoaded = _imagePreview.Image is not null,
        previewSize = new { width = _imagePreview.ClientSize.Width, height = _imagePreview.ClientSize.Height }, ocrChecked = _imageOcr.Checked,
        ocrLanguage = _imageLanguage.SelectedItem?.ToString(), ocrControlsVisible = _imageLanguage.Visible && _imageLanguage.Parent!.ClientRectangle.Contains(_imageLanguage.Bounds) && _imageOcr.Parent!.ClientRectangle.Contains(_imageOcr.Bounds),
        ocrTextVisible = _imageDetails.Visible, ocrText = _imageDetails.Text };
    internal void AttachImageFromGui(string path) => AddImagesHuman([path]);
    internal void ShowImagesFromGui() => _tabs.SelectedIndex = 8;
    internal void RemoveImageFromGui() => _removeImage.PerformClick();
    internal void ToggleOcrFromGui() => _imageOcr.Checked = !_imageOcr.Checked;
    internal async Task WaitForImagesUiAsync() { if (_running is { IsCompleted: false }) await _running.WaitAsync(TimeSpan.FromSeconds(25)); }
    internal async Task ReadImageFromGuiAsync() { _readImage.PerformClick(); await WaitForImagesUiAsync(); }
    internal void PreviewImageFromGui() => _showImagePreview.PerformClick();
}
