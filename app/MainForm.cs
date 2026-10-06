using System.Drawing;
using System.Text.RegularExpressions;

namespace SheepCode;

internal sealed partial class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(25, 21, 36), Surface = Color.FromArgb(35, 29, 48),
        Foreground = Color.FromArgb(247, 234, 245), Muted = Color.FromArgb(184, 163, 192), Accent = Color.FromArgb(239, 164, 199);
    private readonly Preferences _preferences;
    private readonly NeuralVoice _voice;
    private readonly EngineHost _engine;
    private readonly AgentController _agent;
    private readonly VoiceInput _dictation = new();
    private readonly TreeView _tree = new();
    private readonly RichTextBox _editor = CodeBox(false), _diff = CodeBox(true), _console = CodeBox(true), _chat = CodeBox(true);
    private readonly TextBox _input = new() { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical };
    private readonly Label _projectLabel = LabelFor("Abre una carpeta para empezar", 9), _fileLabel = LabelFor("Selecciona un archivo", 11),
        _status = LabelFor("Listo · motor sin iniciar", 9), _gpu = LabelFor("RTX + RX 580 · motor sin iniciar", 9), _changeInfo = LabelFor("Los cambios del agente aparecen aquí para revisión.", 9);
    private readonly ListBox _changeList = new();
    private readonly ComboBox _checkList = new(), _reasoning = new(), _emotion = new();
    private readonly CheckBox _allowChecks = new() { Text = "Comprobaciones", AutoSize = true }, _readAloud = new() { Text = "Leer respuestas", AutoSize = true };
    private readonly Button _send = ButtonFor("🐑 Enviar", true), _stop = ButtonFor("⏸ Parar"), _mic = ButtonFor("🎙 Dictar"),
        _open = ButtonFor("📂 Proyecto"), _connect = ButtonFor("✨ Iniciar IA"), _save = ButtonFor("💾 Guardar"),
        _apply = ButtonFor("✓ Aplicar", true), _reject = ButtonFor("Rechazar"), _undo = ButtonFor("↶ Deshacer"), _runCheck = ButtonFor("▶ Ejecutar");
    private readonly PageDeck _tabs = new();
    private TableLayoutPanel _rootLayout = null!, _columns = null!, _headerLayout = null!;
    private Control _explorerLayout = null!;
    private FlowLayoutPanel _toolbar = null!, _navigation = null!;
    private readonly Panel _compactFiles = new() { Text = "Archivos", BackColor = Background, Padding = new(6) };
    private bool _compact;
    private CancellationTokenSource? _taskCancellation;
    private Task? _running;
    private FileText? _openedFile;
    private bool _dirty, _loadingEditor, _refreshingSettings, _closing, _busy, _checking;
    private CancellationTokenSource? _verificationCancellation;
    private readonly System.Windows.Forms.Timer _highlight = new() { Interval = 450 };
    internal AgentController Agent => _agent;

    internal MainForm(Preferences preferences, NeuralVoice voice, EngineHost engine, AgentController agent)
    {
        _preferences = preferences; _voice = voice; _engine = engine; _agent = agent;
        Text = "🐑 SheepCode · Sheep & Kuky 🌸"; var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1520, 940);
        Size = new(Math.Min(1520, screen.Width - 20), Math.Min(940, screen.Height - 20)); MinimumSize = new(760, 540);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Background; ForeColor = Foreground;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        Font = new("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        BuildLayout(); BuildWorkbench(); WireEvents(); RefreshSettings();
        Resize += (_, _) => UpdateResponsiveLayout(); Shown += (_, _) => { var visibleScreen = Screen.FromControl(this).WorkingArea; Size = new(Math.Min(Width, visibleScreen.Width - 20), Math.Min(Height, visibleScreen.Height - 20)); UpdateResponsiveLayout(); }; UpdateResponsiveLayout();
        AppendChat("assistant", "🐑 ¡Hola! Sheep está listo para crear contigo y Kuky nos acompaña 🌸\n\nAbre un proyecto, prueba una skill o dime qué quieres investigar. Puedes hablarme con el micrófono y revisar el texto antes de enviarlo.");
        if (Directory.Exists(preferences.LastProject))
            try { _agent.OpenProject(preferences.LastProject); } catch (Exception e) { AppendChat("error", e.Message); }
        _tabs.SelectedIndex = 3;
    }
    private static Label LabelFor(string text, float size) => new() { Text = text, UseMnemonic = false, AutoSize = false, ForeColor = size < 10 ? Muted : Foreground, Font = new("Segoe UI", size), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private static Button ButtonFor(string text, bool primary = false)
    {
        var b = new DarkButton { Text = text, AutoSize = true, Height = 34, Padding = new(9, 3, 9, 3), FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Surface, ForeColor = primary ? Background : Foreground, Cursor = Cursors.Hand, Margin = new(3) };
        b.FlatAppearance.BorderSize = 0; return b;
    }
    private static RichTextBox CodeBox(bool readOnly) => new() { ReadOnly = readOnly, BorderStyle = BorderStyle.None,
        BackColor = Background, ForeColor = Foreground, Font = new("Consolas", 11), Dock = DockStyle.Fill,
        WordWrap = false, DetectUrls = false, AcceptsTab = !readOnly, HideSelection = false, ScrollBars = RichTextBoxScrollBars.Both };
    private static FlowLayoutPanel Flow(params Control[] controls)
    { var p = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(6), WrapContents = false, AutoScroll = true, BackColor = Surface }; p.Controls.AddRange(controls); return p; }
    private void StyleCombo(ComboBox combo)
    {
        combo.DrawMode = DrawMode.OwnerDrawFixed; combo.ItemHeight = 23;
        combo.DrawItem += (_, e) => { if (e.Index < 0 || e.Graphics is not { } graphics) return; using var brush = new SolidBrush(Background); graphics.FillRectangle(brush, e.Bounds); TextRenderer.DrawText(graphics, combo.Items[e.Index]?.ToString() ?? "", combo.Font, e.Bounds, Foreground, TextFormatFlags.Left | TextFormatFlags.VerticalCenter); };
    }
    private static TableLayoutPanel Stack(params int[] heights)
    {
        var p = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = heights.Length, BackColor = Surface, Margin = Padding.Empty, Padding = Padding.Empty };
        foreach (var h in heights) p.RowStyles.Add(h < 0 ? new(SizeType.Percent, 100) : new(SizeType.Absolute, h));
        return p;
    }
    private void BuildLayout()
    {
        var root = _rootLayout = Stack(110, 52, -1, 34); root.BackColor = Background; root.Padding = new(18, 10, 18, 6); Controls.Add(root);
        var header = _headerLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = Background };
        header.ColumnStyles.Add(new(SizeType.Absolute, 175)); header.ColumnStyles.Add(new(SizeType.Percent, 100)); header.ColumnStyles.Add(new(SizeType.Absolute, 330));
        header.Controls.Add(new MascotBanner(), 0, 0);
        var titles = Stack(40, 24); titles.BackColor = Background;
        titles.Controls.Add(LabelFor("SheepCode  ✦", 26), 0, 0); titles.Controls.Add(LabelFor("SHEEP & KUKY  ·  un pequeño taller para grandes ideas 🌸", 10), 0, 1); header.Controls.Add(titles, 1, 0);
        _gpu.TextAlign = ContentAlignment.MiddleRight; _gpu.ForeColor = Accent; header.Controls.Add(_gpu, 2, 0); root.Controls.Add(header, 0, 0);
        var toolbar = _toolbar = Flow(_open, _connect, ButtonFor("🧩 Skills"));
        ((Button)toolbar.Controls[2]).Click += (_, _) => _tabs.SelectedIndex = 3;
        _projectLabel.Width = 750; _projectLabel.Dock = DockStyle.None; _projectLabel.Height = 34; _projectLabel.Margin = new(16, 0, 0, 0); toolbar.Controls.Add(_projectLabel); root.Controls.Add(toolbar, 0, 1);

        var columns = _columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = new(0, 8, 0, 8) };
        columns.ColumnStyles.Add(new(SizeType.Percent, 18)); columns.ColumnStyles.Add(new(SizeType.Percent, 48)); columns.ColumnStyles.Add(new(SizeType.Percent, 34)); root.Controls.Add(columns, 0, 2);
        var explorer = Stack(42, -1, 105); _explorerLayout = explorer; explorer.Margin = new(0, 0, 8, 0);
        var explorerTitle = LabelFor("  🌿 TU PROYECTO", 10); explorer.Controls.Add(explorerTitle, 0, 0);
        _tree.Dock = DockStyle.Fill; _tree.BackColor = Surface; _tree.ForeColor = Foreground; _tree.BorderStyle = BorderStyle.None;
        _tree.Font = new("Segoe UI", 10); _tree.ItemHeight = 29; _tree.Indent = 16; _tree.ShowLines = false; _tree.HideSelection = false;
        explorer.Controls.Add(_tree, 0, 1); columns.Controls.Add(explorer, 0, 0);
        var buddy = LabelFor("🐑 Sheep piensa contigo\n🐱 Kuky cuida los detalles\n\n🌸 crea · revisa · aprende", 10); buddy.Padding = new(12); buddy.ForeColor = Accent; explorer.Controls.Add(buddy, 0, 2);

        var center = Stack(43, 88, -1); center.Margin = new(0, 0, 8, 0);
        var editorHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        editorHeader.ColumnStyles.Add(new(SizeType.Percent, 100)); editorHeader.ColumnStyles.Add(new(SizeType.Absolute, 110));
        _fileLabel.Padding = new(12, 0, 0, 0); editorHeader.Controls.Add(_fileLabel, 0, 0); editorHeader.Controls.Add(_save, 1, 0); center.Controls.Add(editorHeader, 0, 0);
        _tabs.Dock = DockStyle.Fill; _tabs.Font = new("Segoe UI", 10); _tabs.BackColor = Background;
        center.Controls.Add(_tabs, 0, 2);
        var navigation = _navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(4), WrapContents = true, AutoScroll = true, BackColor = Surface };
        var names = new[] { "🐑 Código", "🌸 Cambios", "▶ Consola", "🧩 Skills", "💻 PC", "🌐 Web", "🧠 Modelos", "🌿 Archivos" };
        for (var i = 0; i < names.Length; i++) { var index = i; var button = ButtonFor(names[i]); button.Click += (_, _) => _tabs.SelectedIndex = index; navigation.Controls.Add(button); }
        _tabs.SelectedIndexChanged += (_, _) => { for (var i = 0; i < navigation.Controls.Count; i++) { var button = navigation.Controls[i]; button.BackColor = i == _tabs.SelectedIndex ? Accent : Surface; button.ForeColor = i == _tabs.SelectedIndex ? Background : Foreground; button.Invalidate(); } };
        center.Controls.Add(navigation, 0, 1);
        var editorTab = new Panel { Text = "Código", BackColor = Background, Padding = new(14) }; editorTab.Controls.Add(_editor); _tabs.TabPages.Add(editorTab);
        var changesTab = new Panel { Text = "Cambios", BackColor = Background, Padding = new(6) };
        var changeLayout = Stack(84, 46, 42, -1);
        _changeList.Dock = DockStyle.Fill; _changeList.BackColor = Surface; _changeList.ForeColor = Foreground; _changeList.BorderStyle = BorderStyle.None; _changeList.Font = new("Consolas", 10); _changeList.ItemHeight = 25;
        changeLayout.Controls.Add(_changeList, 0, 0); changeLayout.Controls.Add(Flow(_apply, _reject, _undo), 0, 1);
        _changeInfo.Padding = new(8); changeLayout.Controls.Add(_changeInfo, 0, 2); _diff.WordWrap = false; changeLayout.Controls.Add(_diff, 0, 3);
        changesTab.Controls.Add(changeLayout); _tabs.TabPages.Add(changesTab);
        var consoleTab = new Panel { Text = "Consola", BackColor = Background, Padding = new(6) };
        var consoleLayout = Stack(48, -1); _checkList.DropDownStyle = ComboBoxStyle.DropDownList; _checkList.Width = 290; _checkList.Height = 32; _checkList.Margin = new(3, 7, 3, 3);
        consoleLayout.Controls.Add(Flow(_checkList, _runCheck), 0, 0); consoleLayout.Controls.Add(_console, 0, 1); consoleTab.Controls.Add(consoleLayout); _tabs.TabPages.Add(consoleTab);
        columns.Controls.Add(center, 1, 0);

        var conversation = Stack(44, -1, 88, 90, 50); conversation.Padding = new(6);
        var agentTitle = LabelFor("  🐑 CONVERSA CON SHEEP  ✨", 10); conversation.Controls.Add(agentTitle, 0, 0);
        _chat.Font = new("Segoe UI", 10); _chat.WordWrap = true; _chat.BackColor = Surface; _chat.ScrollBars = RichTextBoxScrollBars.Vertical; conversation.Controls.Add(_chat, 0, 1);
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(3), WrapContents = true };
        _reasoning.DropDownStyle = ComboBoxStyle.DropDownList; _reasoning.Width = 134; _reasoning.Items.AddRange(new[] { "Sin razonamiento", "Bajo", "Medio", "Alto" });
        _emotion.DropDownStyle = ComboBoxStyle.DropDownList; _emotion.Width = 150;
        foreach (var emotion in _voice.Emotions) _emotion.Items.Add(emotion);
        foreach (var combo in new[] { _reasoning, _emotion, _checkList })
        { combo.BackColor = Background; combo.ForeColor = Foreground; combo.FlatStyle = FlatStyle.Flat; StyleCombo(combo); }
        options.Controls.AddRange([_allowChecks, _readAloud, _reasoning, _emotion]); conversation.Controls.Add(options, 0, 2);
        _input.Dock = DockStyle.Fill; _input.BackColor = Background; _input.ForeColor = Foreground; _input.BorderStyle = BorderStyle.FixedSingle;
        _input.Font = new("Segoe UI", 11); _input.PlaceholderText = "¿Qué creamos hoy? 🌸 Usa $browser, $desktop…\r\nCtrl + Enter para enviar"; conversation.Controls.Add(_input, 0, 3);
        var composerButtons = Flow(_send, _mic, _stop); conversation.Controls.Add(composerButtons, 0, 4); columns.Controls.Add(conversation, 2, 0);
        root.Controls.Add(_status, 0, 3);
        var tip = new ToolTip(); tip.SetToolTip(_allowChecks, "Permite al agente ejecutar las comprobaciones detectadas. Estas ejecutan código del proyecto abierto.");
        tip.SetToolTip(_readAloud, "Lee el resumen de la respuesta con la voz neuronal de Ono_Anna en la RX 580.");
        tip.SetToolTip(_mic, "Pulsa para grabar y vuelve a pulsar para transcribir. Puedes revisar el texto antes de enviarlo.");
        tip.SetToolTip(_connect, "Carga el modelo local elegido por el setup. El perfil original conserva Strata, sus dos GPU y la voz RX 580.");
    }
    private void WireEvents()
    {
        _open.Click += (_, _) =>
        {
            if (!CanLeaveFile()) return;
            using var dialog = new FolderBrowserDialog { Description = "Carpeta en la que trabajará SheepCode", UseDescriptionForTitle = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) Guard(() => _agent.OpenProject(dialog.SelectedPath));
        };
        _connect.Click += (_, _) => StartTask(async token => { await _engine.EnsureAsync(token); AppendChat("assistant", _engine.Profile.Label + " está preparado. " + _engine.Profile.DeviceDescription); });
        _tree.NodeMouseDoubleClick += (_, e) => { if (e.Node.Tag is string path && CanLeaveFile()) Guard(() => OpenFile(path)); };
        _save.Click += (_, _) => Guard(SaveFile);
        _editor.TextChanged += (_, _) => { if (_loadingEditor) return; _dirty = true; _fileLabel.Text = (_openedFile?.Path ?? "Sin archivo") + " •"; _highlight.Stop(); _highlight.Start(); };
        _highlight.Tick += (_, _) => { _highlight.Stop(); HighlightCode(); };
        _changeList.SelectedIndexChanged += (_, _) => ShowSelectedChange();
        _apply.Click += (_, _) => Guard(() => { var change = SelectedChange(); _agent.Changes!.Apply(change.Id); ReloadOpened(); });
        _reject.Click += (_, _) => Guard(() => _agent.Changes!.Reject(SelectedChange().Id));
        _undo.Click += (_, _) => Guard(() => { _agent.Changes!.Undo(SelectedChange().Id); ReloadOpened(); });
        _send.Click += (_, _) => SendInput();
        _input.KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SendInput(); } };
        _stop.Click += async (_, _) => await StopTaskAsync();
        _mic.Click += async (_, _) =>
        {
            try
            {
                if (!_dictation.Recording) { _voice.Interrupt(); _dictation.Start(); _mic.Text = "Terminar"; _status.Text = "Grabando micrófono · máximo 45 s"; }
                else
                {
                    _mic.Enabled = false; _status.Text = "Transcribiendo…";
                    var transcript = await Task.Run(() => _dictation.StopAsync(CancellationToken.None));
                    _input.Text = transcript; _status.Text = _dictation.Backend + " · revisa el texto antes de enviarlo";
                    _mic.Text = "Dictar"; _mic.Enabled = true;
                }
            }
            catch (Exception e) { _mic.Text = "Dictar"; _mic.Enabled = true; AppendChat("error", e.Message); }
        };
        _runCheck.Click += (_, _) =>
        {
            if (_checkList.SelectedItem is not CheckSpec spec || _agent.Workspace is null) return;
            _tabs.SelectedIndex = 2;
            StartTask(async token =>
            {
                var result = await new ChecksRunner(_agent.Workspace).RunAsync(spec.Id, _preferences.AllowChecks, line => Ui(() => AppendConsole(line)), token);
                Ui(() => AppendChat("assistant", $"{spec.Label}: código de salida {result.ExitCode}.\n{result.Output[..Math.Min(result.Output.Length, 1200)]}"));
            });
        };
        _allowChecks.CheckedChanged += (_, _) => SaveSettings(); _readAloud.CheckedChanged += (_, _) => SaveSettings();
        _reasoning.SelectedIndexChanged += (_, _) => SaveSettings(); _emotion.SelectedIndexChanged += (_, _) => SaveSettings();
        _agent.ProjectChanged += () => Ui(RefreshProject);
        _agent.ChangeProposed += change => Ui(() => { RefreshChanges(change.Id); if (change.Status == "pending") _tabs.SelectedIndex = 1; });
        _agent.Output += (role, text) => Ui(() =>
        {
            if (role == "console") AppendConsole(text);
            else if (role == "progress") _status.Text = text;
            else if (role == "activity") AppendChat("tool", text);
            else AppendChat(role, text);
        });
        _engine.Progress += value => Ui(() => { _status.Text = value; _gpu.Text = _engine.Profile.DeviceDescription + " · " + _engine.State; });
        _voice.Progress += value => Ui(() => _status.Text = value);
        FormClosing += async (_, e) =>
        {
            if (_checking) { _verificationCancellation?.Cancel(); e.Cancel = true; return; }
            if (_closing) return;
            if (!CanLeaveFile()) { e.Cancel = true; _updateInstallRequested = false; return; }
            if (_updateInstallRequested) { try { _agent.Updates.StartHandoff(); } catch (Exception error) { _updateInstallRequested = false; AppendChat("error", error.Message); e.Cancel = true; return; } }
            e.Cancel = true; _closing = true; Enabled = false;
            _taskCancellation?.Cancel(); _backgroundCancellation.Cancel(); _agent.Connections.Cancel(); _modelTimer.Stop(); _voice.Interrupt(); _dictation.Dispose();
            await _engine.StopAsync(CancellationToken.None);
            if (_running is not null) try { await _running; } catch (Exception) { }
            await _engine.DisposeAsync(); await _voice.DisposeAsync(); _agent.Connections.Dispose(); _agent.Updates.Dispose(); _browserTools.Dispose(); _highlight.Dispose(); _modelTimer.Dispose(); Close();
        };
    }
    private void RefreshSettings()
    {
        _refreshingSettings = true;
        _allowChecks.Checked = _preferences.AllowChecks; _readAloud.Checked = _preferences.ReadAloud;
        _readAloud.Enabled = File.Exists(Path.Combine(AppPaths.State, "tts-config.json")) && File.Exists(AppPaths.VoicePython);
        _reasoning.Enabled = _engine.Profile.Kind != "strata-cpu";
        _reasoning.SelectedIndex = _reasoning.Enabled ? Array.IndexOf(new[] { "none", "low", "medium", "high" }, _preferences.Reasoning) : 0;
        _emotion.SelectedItem = _preferences.Emotion; if (_emotion.SelectedIndex < 0) _emotion.SelectedItem = "neutral";
        _powerMode.SelectedIndex = Array.IndexOf(new[] { "auto", "eco", "performance" }, _preferences.PortableMode);
        _refreshingSettings = false;
    }
    private void SaveSettings()
    {
        if (_refreshingSettings) return;
        _preferences.AllowChecks = _allowChecks.Checked; _preferences.ReadAloud = _readAloud.Checked;
        if (_reasoning.Enabled) _preferences.Reasoning = new[] { "none", "low", "medium", "high" }[Math.Max(0, _reasoning.SelectedIndex)];
        _preferences.Emotion = _emotion.SelectedItem as string ?? "neutral"; _preferences.Save();
        if (!_preferences.ReadAloud) _voice.Interrupt();
    }
    private void UpdateResponsiveLayout()
    {
        if (_columns is null || _tabs.TabPages.Count < 8) return;
        var scale = DeviceDpi / 96d; var compact = ClientSize.Width < 1180 * scale;
        _columns.SuspendLayout(); _rootLayout.SuspendLayout();
        if (compact != _compact)
        {
            _compact = compact;
            if (compact) _compactFiles.Controls.Add(_tree);
            else { ((TableLayoutPanel)_explorerLayout).Controls.Add(_tree, 0, 1); if (_tabs.SelectedIndex == 7) _tabs.SelectedIndex = 0; }
        }
        _explorerLayout.Visible = !compact; _navigation.Controls[7].Visible = compact;
        _columns.ColumnStyles[0].SizeType = compact ? SizeType.Absolute : SizeType.Percent; _columns.ColumnStyles[0].Width = compact ? 0 : 18;
        _columns.ColumnStyles[1].Width = compact ? 55 : 48; _columns.ColumnStyles[2].Width = compact ? 45 : 34;
        _headerLayout.ColumnStyles[2].Width = compact ? 0 : (float)(330 * scale); _gpu.Visible = !compact;
        _rootLayout.RowStyles[0].Height = (float)((ClientSize.Height < 720 * scale ? 88 : 110) * scale);
        _rootLayout.RowStyles[1].Height = (float)(52 * scale);
        var used = _toolbar.Controls.Cast<Control>().Where(c => c != _projectLabel).Sum(c => c.Width + c.Margin.Horizontal);
        _projectLabel.Width = Math.Max(90, _toolbar.ClientSize.Width - used - (int)(45 * scale));
        _rootLayout.ResumeLayout(); _columns.ResumeLayout();
    }
    internal object ResponsiveSnapshot() => new { size = new { Width, Height }, dpi = DeviceDpi, compact = _compact,
        fileExplorerInTab = _tree.Parent == _compactFiles,
        inputVisible = _input.Visible && ClientRectangle.Contains(RectangleToClient(_input.RectangleToScreen(_input.ClientRectangle))),
        sendVisible = _send.Visible && ClientRectangle.Contains(RectangleToClient(_send.RectangleToScreen(_send.ClientRectangle))) };
    private void RefreshProject()
    {
        var workspace = _agent.Workspace!; _projectLabel.Text = workspace.Root;
        _tree.BeginUpdate(); _tree.Nodes.Clear();
        foreach (var file in workspace.Files())
        {
            var parent = _tree.Nodes; var parts = file.Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                var node = parent.Cast<TreeNode>().FirstOrDefault(n => n.Text == parts[i]);
                if (node is null) { node = new(parts[i]); parent.Add(node); }
                if (i == parts.Length - 1) node.Tag = file;
                parent = node.Nodes;
            }
        }
        _tree.EndUpdate(); _openedFile = null; _editor.Clear(); _dirty = false; _fileLabel.Text = "Selecciona un archivo";
        _checkList.Items.Clear(); foreach (var check in new ChecksRunner(workspace).Available()) _checkList.Items.Add(check);
        _checkList.DisplayMember = nameof(CheckSpec.Label); if (_checkList.Items.Count > 0) _checkList.SelectedIndex = 0;
        _chat.Clear();
        foreach (var line in _agent.Session!.Lines.TakeLast(35)) AppendChat(line.Role, line.Text);
        if (_agent.Session.Lines.Count == 0) AppendChat("assistant", "Proyecto abierto. Pídeme investigar, explicar o corregir el código.");
        RefreshChanges();
    }
    internal void OpenFile(string path)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".xlsx" or ".pptx" or ".pdf")
        {
            _loadingEditor = true;
            try { _openedFile = null; _agent.ActiveFile = path; _editor.ReadOnly = true; _editor.Text = ArtifactTools.Read(_agent.Workspace!, path); _dirty = false; _fileLabel.Text = path + " · vista de contenido"; _tabs.SelectedIndex = 0; }
            finally { _loadingEditor = false; }
            return;
        }
        _editor.ReadOnly = false;
        _openedFile = _agent.Workspace!.Read(path); _agent.ActiveFile = _openedFile.Path; _loadingEditor = true; _editor.Text = _openedFile.Text;
        _loadingEditor = false; _dirty = false; _fileLabel.Text = path; HighlightCode(); _tabs.SelectedIndex = 0;
    }
    private void SaveFile()
    {
        if (_openedFile is null || !_dirty) return;
        _agent.Workspace!.Write(_openedFile.Path, _editor.Text, _openedFile.Hash, _openedFile.Bom, _openedFile.Newline);
        _openedFile = _agent.Workspace.Read(_openedFile.Path); _dirty = false; _fileLabel.Text = _openedFile.Path; _status.Text = "Archivo guardado";
    }
    private bool CanLeaveFile()
    {
        if (!_dirty) return true;
        var result = MessageBox.Show(this, "El editor tiene cambios. ¿Quieres guardarlos?", "SheepCode", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (result == DialogResult.Cancel) return false;
        if (result == DialogResult.Yes) { try { SaveFile(); } catch (Exception e) { AppendChat("error", e.Message); return false; } }
        return true;
    }
    private void ReloadOpened() { if (_openedFile is not null && !_dirty) { var path = _openedFile.Path; if (File.Exists(_agent.Workspace!.Resolve(path))) OpenFile(path); } }
    private sealed record ChangeRow(ProposedChange Change)
    {
        public override string ToString() => $"[{Change.Status switch { "pending" => "pendiente", "applied" => "aplicado", "rejected" => "rechazado", "undone" => "deshecho", _ => "sustituido" }}] {Change.Path} · {Change.Id}";
    }
    private ProposedChange SelectedChange() => (_changeList.SelectedItem as ChangeRow)?.Change ?? throw new InvalidOperationException("Selecciona un cambio.");
    private void RefreshChanges(string? id = null)
    {
        id ??= (_changeList.SelectedItem as ChangeRow)?.Change.Id;
        _changeList.Items.Clear();
        foreach (var change in (_agent.Changes?.Items ?? []).OrderByDescending(c => c.At)) _changeList.Items.Add(new ChangeRow(change));
        if (id is not null) _changeList.SelectedIndex = _changeList.Items.Cast<ChangeRow>().ToList().FindIndex(c => c.Change.Id == id);
        if (_changeList.SelectedIndex < 0 && _changeList.Items.Count > 0) _changeList.SelectedIndex = 0;
        ShowSelectedChange();
    }
    private void ShowSelectedChange()
    {
        var change = (_changeList.SelectedItem as ChangeRow)?.Change;
        _diff.Text = change?.Diff() ?? ""; _changeInfo.Text = change?.Reason ?? "Los cambios del agente aparecen aquí para revisión.";
        var offset = 0;
        foreach (var line in _diff.Lines)
        {
            _diff.Select(offset, line.Length); _diff.SelectionColor = line.StartsWith('+') ? Accent : line.StartsWith('-') ? Color.FromArgb(255, 153, 163) : Muted;
            offset += line.Length + 1;
        }
        _diff.Select(0, 0); _diff.ScrollToCaret();
        _apply.Enabled = _reject.Enabled = !_busy && change?.Status == "pending";
        _undo.Enabled = !_busy && change?.Status == "applied";
    }
    private void HighlightCode()
    {
        if (_editor.TextLength > 90000) return;
        var start = _editor.SelectionStart; var length = _editor.SelectionLength;
        _loadingEditor = true;
        _editor.SelectAll(); _editor.SelectionColor = Foreground;
        foreach (Match match in Regex.Matches(_editor.Text, @"\b(class|internal|public|private|static|void|return|async|await|using|namespace|var|if|else|new|def|import|from|for|in|function|const|let|export|True|False|null|true|false)\b"))
        { _editor.Select(match.Index, match.Length); _editor.SelectionColor = Color.FromArgb(140, 181, 255); }
        foreach (Match match in Regex.Matches(_editor.Text, "\"[^\"\r\n]*\"|'[^'\r\n]*'"))
        { _editor.Select(match.Index, match.Length); _editor.SelectionColor = Accent; }
        _editor.Select(Math.Min(start, _editor.TextLength), Math.Min(length, Math.Max(0, _editor.TextLength - start))); _loadingEditor = false;
    }
    private void SendInput()
    {
        if (_running is { IsCompleted: false } || string.IsNullOrWhiteSpace(_input.Text)) return;
        var text = _input.Text.Trim(); _input.Clear();
        StartTask(async token =>
        {
            var reply = await _agent.SubmitAsync(text, token);
            Ui(RefreshSettings);
            if (_preferences.ReadAloud)
            {
                var spoken = reply.Length > 240 ? reply[..237] + "." : reply;
                await _voice.SpeakAsync(spoken, _preferences.Emotion, token);
            }
        });
    }
    private void StartTask(Func<CancellationToken, Task> action)
    {
        if (_running is { IsCompleted: false }) return;
        _taskCancellation?.Dispose(); _taskCancellation = new();
        var cancellation = _taskCancellation;
        SetBusy(true);
        _running = Run();
        async Task Run()
        {
            try { await action(cancellation.Token); }
            catch (OperationCanceledException) { Ui(() => AppendChat("assistant", "Tarea detenida. Los cambios propuestos siguen disponibles.")); }
            catch (Exception) when (cancellation.IsCancellationRequested) { Ui(() => AppendChat("assistant", "Tarea detenida. Los cambios propuestos siguen disponibles.")); }
            catch (Exception e) { Ui(() => AppendChat("error", e.Message)); }
            finally { Ui(() => { SetBusy(false); _stop.Enabled = true; ShowSelectedChange(); _status.Text = "Listo · " + _engine.State; }); }
        }
    }
    private async Task StopTaskAsync()
    {
        if (!_stop.Enabled) return;
        _stop.Enabled = false; _status.Text = "Deteniendo…";
        try
        {
            var cancellation = _taskCancellation;
            var cancelling = cancellation?.CancelAsync();
            _agent.Connections.Cancel();
            _voice.Interrupt();
            await _engine.StopAsync(CancellationToken.None);
            if (cancelling is not null) await cancelling;
            if (_running is { IsCompleted: false }) await _running.WaitAsync(TimeSpan.FromSeconds(5));
            _status.Text = "Tarea detenida · motor liberado";
        }
        catch (TimeoutException) { _status.Text = "Motor detenido; esperando que termine la herramienta en curso…"; }
        catch (Exception e) { AppendChat("error", "No se completó la detención: " + e.Message); }
        finally { _stop.Enabled = true; }
    }
    private void SetBusy(bool value)
    {
        _busy = value;
        _send.Enabled = _open.Enabled = _connect.Enabled = _runCheck.Enabled = _save.Enabled = _mic.Enabled = !value;
        _apply.Enabled = _reject.Enabled = _undo.Enabled = !value;
    }
    private void AppendChat(string role, string text)
    {
        if (text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('['))
            try { using var data = System.Text.Json.JsonDocument.Parse(text); text = System.Text.Json.JsonSerializer.Serialize(data.RootElement,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }); } catch (System.Text.Json.JsonException) { }
        _chat.SelectionStart = _chat.TextLength; _chat.SelectionColor = role == "error" ? Color.FromArgb(255, 153, 163) : role == "user" ? Foreground : role == "tool" ? Muted : Accent;
        _chat.SelectionFont = new Font("Segoe UI", role == "tool" ? 9 : 10, FontStyle.Bold);
        _chat.AppendText((role switch { "user" => "🌷 TÚ", "error" => "⚠ ERROR", "tool" => "🧩 HERRAMIENTA", _ => "🐑 SHEEP · 🐱 KUKY" }) + "\n");
        _chat.SelectionFont = new Font("Segoe UI", role == "tool" ? 9 : 10); _chat.SelectionColor = role == "tool" ? Muted : Foreground;
        _chat.AppendText(text + "\n\n"); _chat.SelectionStart = _chat.TextLength; _chat.ScrollToCaret();
    }
    private void AppendConsole(string line) { _console.AppendText(line + "\n"); _console.SelectionStart = _console.TextLength; _console.ScrollToCaret(); }
    private void Guard(Action action) { try { action(); } catch (Exception e) { AppendChat("error", e.Message); } }
    private void Ui(Action action) { if (IsDisposed) return; if (InvokeRequired) BeginInvoke(action); else action(); }
    private void ShowCapabilities()
    {
        using var window = new Form { Text = "Capacidades de SheepCode", Size = new(720, 580), StartPosition = FormStartPosition.CenterParent, BackColor = Background };
        var content = CodeBox(true); content.Font = new("Segoe UI", 11); content.WordWrap = true;
        content.Text = string.Join("\n\n", _agent.Capabilities.Select(c => c.Name.ToUpperInvariant() + "\n" + c.Description)) + "\n\nDatos y registros: " + AppPaths.Root;
        window.Controls.Add(content); window.ShowDialog(this);
    }
    internal void CaptureWindow(string path) { using var image = new Bitmap(Width, Height); DrawToBitmap(image, new(0, 0, Width, Height)); image.Save(path, System.Drawing.Imaging.ImageFormat.Png); }
    internal void VerificationMode(bool active, CancellationTokenSource? cancellation = null)
    { _checking = active; _verificationCancellation = active ? cancellation : null; foreach (Control control in Controls) control.Enabled = !active; if (!active) { SetBusy(false); RefreshSettings(); } }
    internal void ShowChanges() => _tabs.SelectedIndex = 1;
    internal async Task SubmitFromGuiAsync(string text)
    {
        _input.Text = text; _send.PerformClick();
        if (_running is not null) await _running;
    }
    internal void ApplyFromGui() => _apply.PerformClick();
    internal void StopFromGui() => _stop.PerformClick();
    internal bool SendEnabled => _send.Enabled;
    internal async Task CheckFromGuiAsync()
    {
        _tabs.SelectedIndex = 2;
        _runCheck.PerformClick(); if (_running is not null) await _running;
    }

    private sealed class DarkButton : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? Background); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = MascotBanner.Rounded(Rectangle.Inflate(ClientRectangle, -1, -1), 10);
            using var brush = new SolidBrush(Enabled ? BackColor : Surface); e.Graphics.FillPath(brush, path);
            using var border = new Pen(Enabled ? Color.FromArgb(95, 72, 112) : Surface); e.Graphics.DrawPath(border, path);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3), ForeColor, BackColor);
        }
    }
}
