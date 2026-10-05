using System.Text.Json;

namespace SheepCode;

internal sealed partial class MainForm
{
    private readonly FlowLayoutPanel _skillCards = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, BackColor = Background, Padding = new(6) };
    private readonly RichTextBox _skillText = CodeBox(true), _modelText = CodeBox(true);
    private readonly ComboBox _windows = new() { Width = 320, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = nameof(PcWindow.Title) };
    private readonly ListBox _pcNodes = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Background, ForeColor = Foreground, Font = new("Segoe UI", 10) };
    private readonly Label _pcScope = LabelFor("🌸 Elige una ventana para que Sheep pueda ayudarte en ella.", 10);
    private readonly TextBox _pcText = new() { Width = 320, BackColor = Background, ForeColor = Foreground, PlaceholderText = "Texto para el campo seleccionado…" };
    private readonly TabControl _webTabs = new() { Dock = DockStyle.Fill };
    private readonly TextBox _address = new() { Dock = DockStyle.Fill, BackColor = Background, ForeColor = Foreground, PlaceholderText = "https://…" };
    private readonly Label _webInfo = LabelFor("Pestañas propias de SheepCode · leer, navegar y trabajar con Sheep 🌐", 9);
    private BrowserTools _browserTools = null!;
    private readonly System.Windows.Forms.Timer _modelTimer = new() { Interval = 2500 };
    private readonly ComboBox _powerMode = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Background, ForeColor = Foreground };
    private sealed record NodeRow(PcNode Node) { public override string ToString() => $"{(Node.Editable ? "✎" : Node.Clickable ? "◉" : "·")} {Node.Name} {(Node.Value.Length > 0 ? "= " + Node.Value : "")} [{Node.Id}]"; }

    private void BuildWorkbench()
    {
        var skills = NewPage("Skills"); skills.AutoScroll = true; var layout = Stack(66, -1, 96, 48); layout.Padding = new(8); layout.Dock = DockStyle.Top; layout.Height = 480;
        var introduction = LabelFor("🧩 La caja de habilidades de Sheep\nActiva sus herramientas y descubre instrucciones para cada tarea.", 11); introduction.ForeColor = Accent;
        layout.Controls.Add(introduction, 0, 0); layout.Controls.Add(_skillCards, 0, 1);
        _skillText.Font = new("Segoe UI", 10); _skillText.WordWrap = true; layout.Controls.Add(_skillText, 0, 2);
        Button refresh = ButtonFor("↻ Recargar"), create = ButtonFor("🌸 Crear skill", true), capabilities = ButtonFor("Qué puedo hacer");
        refresh.Click += (_, _) => Guard(_agent.Skills.Reload); create.Click += (_, _) => CreateSkillDialog(); capabilities.Click += (_, _) => ShowCapabilities();
        layout.Controls.Add(Flow(refresh, create, capabilities), 0, 3); skills.Controls.Add(layout);
        _agent.Skills.Changed += () => Ui(() => { RefreshSkills(); if (_busy) _taskCancellation?.Cancel(); });
        _skillCards.Resize += (_, _) => { foreach (Control card in _skillCards.Controls) card.Width = Math.Max(216, (_skillCards.ClientSize.Width - 36) / 2); };
        RefreshSkills();

        var desktop = NewPage("PC"); desktop.AutoScroll = true; var pc = Stack(65, 48, 48, -1, 88); pc.Padding = new(8); pc.Dock = DockStyle.Top; pc.Height = 500;
        pc.Controls.Add(_pcScope, 0, 0);
        foreach (var combo in new[] { _windows }) { combo.BackColor = Background; combo.ForeColor = Foreground; combo.FlatStyle = FlatStyle.Flat; }
        Button list = ButtonFor("↻ Ventanas"), choose = ButtonFor("Elegir", true), inspect = ButtonFor("Leer ventana"), release = ButtonFor("Liberar");
        list.Click += (_, _) => StartTask(async token => { var items = await _agent.Desktop.WindowsAsync(token); _windows.Items.Clear(); _windows.Items.AddRange(items.Cast<object>().ToArray()); if (_windows.Items.Count > 0) _windows.SelectedIndex = 0; });
        choose.Click += (_, _) => { if (_windows.SelectedItem is PcWindow window) StartTask(async token => { await _agent.ControlAsync("Selecciona la ventana " + window.Id, token); await _agent.Desktop.ReadAsync(token); }); };
        inspect.Click += (_, _) => StartTask(async token => { await _agent.Desktop.ReadAsync(token); });
        release.Click += (_, _) => _agent.Desktop.Release();
        pc.Controls.Add(Flow(_windows, list), 0, 1); pc.Controls.Add(Flow(choose, inspect, release), 0, 2); pc.Controls.Add(_pcNodes, 0, 3);
        Button click = ButtonFor("Pulsar control"), type = ButtonFor("Escribir", true);
        click.Click += (_, _) => { if (_pcNodes.SelectedItem is NodeRow row) StartTask(async token => { await _agent.Desktop.ActAsync("click", row.Node.Id, "", "Pulsa " + row.Node.Name, token); }); };
        type.Click += (_, _) => { if (_pcNodes.SelectedItem is NodeRow row) StartTask(async token => { await _agent.Desktop.ActAsync("type", row.Node.Id, _pcText.Text, "Escribe en " + row.Node.Name, token); }); };
        var pcActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, Padding = new(4) }; pcActions.Controls.AddRange([_pcText, type, click]); pc.Controls.Add(pcActions, 0, 4);
        _agent.Desktop.Changed += () => Ui(() =>
        {
            _pcScope.Text = _agent.Desktop.Selected is { } window ? "💻 " + window.Title + "\nSolo esta ventana · puedes liberarla en cualquier momento" : "🌸 Control del PC sin configurar. Elige una ventana.";
            _pcNodes.Items.Clear(); if (_agent.Desktop.Observation is { } view) _pcNodes.Items.AddRange(view.Nodes.Select(n => (object)new NodeRow(n)).ToArray());
        }); desktop.Controls.Add(pc);

        var browser = NewPage("Web"); var web = Stack(44, 44, -1, 42); web.Padding = new(6);
        var address = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 }; address.ColumnStyles.Add(new(SizeType.Percent, 100)); address.ColumnStyles.Add(new(SizeType.Absolute, 92));
        var go = ButtonFor("🌐 Abrir", true); address.Controls.Add(_address, 0, 0); address.Controls.Add(go, 1, 0); web.Controls.Add(address, 0, 0);
        go.Click += (_, _) => OpenWeb(); _address.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OpenWeb(); } };
        _browserTools = new(this, _webTabs, _agent.Skills); _agent.Browser = _browserTools;
        Button back = ButtonFor("← Volver"), read = ButtonFor("Leer página"), close = ButtonFor("Cerrar pestaña");
        back.Click += (_, _) => { if (_browserTools.Selected is { } id) StartTask(async token => { await _browserTools.BackAsync(id, token); }); };
        read.Click += (_, _) => { if (_browserTools.Selected is { } id) StartTask(async token => { AppendChat("assistant", JsonSerializer.Serialize(await _browserTools.ReadAsync(id, token), AppPaths.Json)); }); };
        close.Click += (_, _) => { if (_browserTools.Selected is { } id) StartTask(token => _browserTools.CloseAsync(id, token)); };
        web.Controls.Add(Flow(back, read, close), 0, 1); web.Controls.Add(_webTabs, 0, 2); web.Controls.Add(_webInfo, 0, 3); browser.Controls.Add(web);
        _browserTools.Changed += () => Ui(() => { if (_webTabs.SelectedTab?.Controls.OfType<Microsoft.Web.WebView2.WinForms.WebView2>().FirstOrDefault() is { } current) _address.Text = current.Source?.AbsoluteUri ?? ""; _webInfo.Text = "🌐 " + _webTabs.TabCount + " pestaña(s) · pide a Sheep leer o actuar aquí"; });

        var models = NewPage("Modelos"); models.AutoScroll = true; var model = Stack(80, 52, 52, 48, -1); model.Padding = new(10); model.Dock = DockStyle.Top; model.Height = 520;
        var title = LabelFor("🧠 El cerebro local de Sheep\nTu modelo, sus gráficas y las opciones de instalación\nEl perfil original conserva Strata + RX 580 🌸", 12); title.ForeColor = Accent; model.Controls.Add(title, 0, 0);
        Button load = ButtonFor("✨ Cargar IA", true), unload = ButtonFor("⏸ Liberar"), status = ButtonFor("↻ Estado");
        load.Click += (_, _) => StartTask(async token => { await _agent.SubmitAsync("Activa el motor", token); RefreshModels(); });
        unload.Click += (_, _) => StartTask(async token => { await _agent.SubmitAsync("Desactiva el motor", token); RefreshModels(); }); status.Click += (_, _) => Guard(RefreshModels);
        Button analyze = ButtonFor("💻 Analizar"), install = ButtonFor("🌸 Instalar recomendación");
        analyze.Click += (_, _) => StartTask(async token => { await _agent.SubmitAsync("Analiza el sistema", token); });
        install.Click += (_, _) => StartTask(async token => { await _agent.SubmitAsync("Instala el modelo recomendado", token); RefreshModels(); });
        model.Controls.Add(Flow(load, unload, status), 0, 1); model.Controls.Add(Flow(analyze, install), 0, 2);
        _powerMode.Items.AddRange(["🔋 Automático", "🌱 Ahorro", "⚡ Perfil completo"]);
        StyleCombo(_powerMode);
        _powerMode.SelectedIndexChanged += (_, _) => { if (_refreshingSettings || _powerMode.SelectedIndex < 0) return; var command = _powerMode.SelectedIndex switch { 0 => "Modo portátil automático", 1 => "Activa el modo ahorro", _ => "Desactiva el modo ahorro" }; StartTask(async token => { try { await _agent.SubmitAsync(command, token); } finally { RefreshSettings(); RefreshModels(); } }); };
        var powerLabel = LabelFor("💻 Energía", 10); powerLabel.Dock = DockStyle.None; powerLabel.Width = 94; powerLabel.Height = 34;
        model.Controls.Add(Flow(powerLabel, _powerMode), 0, 3);
        _modelText.WordWrap = true; _modelText.Font = new("Consolas", 10); model.Controls.Add(_modelText, 0, 4); models.Controls.Add(model);
        _tabs.TabPages.Add(_compactFiles);
        _modelTimer.Interval = 5000; _modelTimer.Tick += (_, _) => { _engine.RefreshPowerPriority(); if (_tabs.SelectedIndex == 6) RefreshModels(); }; _modelTimer.Start(); RefreshModels();
    }
    private Panel NewPage(string name) { var page = new Panel { Text = name, BackColor = Background, Padding = new(6) }; _tabs.TabPages.Add(page); return page; }
    private void OpenWeb() { var url = _address.Text.Trim(); StartTask(async token => { await _browserTools.OpenAsync(url, token); }); }
    private void RefreshModels()
    {
        _modelText.Text = "💻 " + SheepCode.Distribution.PowerScanner.Read().Describe() + " · modo " + _preferences.PortableMode + "\nPerfil integrado: " + _engine.Profile.Kind + "\nRazonamiento: " + _preferences.Reasoning + " (ajústalo en el chat)\n" +
            "Motor: " + _engine.State + "\nVoz: " + _voice.State + "\n\nTelemetría y configuración real:\n" + JsonSerializer.Serialize(_agent.ModelStatus(), AppPaths.Json);
    }
    private void RefreshSkills()
    {
        foreach (Control old in _skillCards.Controls.Cast<Control>().ToArray()) old.Dispose(); _skillCards.Controls.Clear();
        foreach (var skill in _agent.Skills.Items)
        {
            var card = new Panel { Width = Math.Max(216, (_skillCards.ClientSize.Width - 36) / 2), Height = 130, BackColor = Surface, Padding = new(8), Margin = new(0, 0, 8, 8) };
            var toggle = new CheckBox { Text = skill.Emoji + "  " + skill.Name.ToUpperInvariant(), Checked = _agent.Skills.Enabled(skill.Name), ForeColor = Accent, Font = new("Segoe UI", 11, FontStyle.Bold), AutoSize = true, Location = new(12, 10) };
            toggle.CheckedChanged += (_, _) => Guard(() => _agent.Skills.SetEnabled(skill.Name, toggle.Checked)); card.Controls.Add(toggle);
            var details = ButtonFor("Ver"); details.Anchor = AnchorStyles.Top | AnchorStyles.Right; details.Location = new(card.Width - 66, 5); details.Width = 56; details.Click += (_, _) => _skillText.Text = skill.Path + "\n\n" + skill.Instructions; card.Controls.Add(details);
            var description = LabelFor(skill.Description, 9); description.Dock = DockStyle.None; description.SetBounds(12, 45, card.Width - 24, 80); description.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; description.AutoEllipsis = false; card.Controls.Add(description); _skillCards.Controls.Add(card);
        }
        if (_agent.Skills.Errors.Count > 0) _skillText.Text = string.Join('\n', _agent.Skills.Errors);
        else if (_skillText.TextLength == 0) _skillText.Text = "🐱 Kuky tip: usa $code, $desktop, $browser o $models en el chat.\n\nTus nuevas skills se guardan en " + Path.Combine(AppPaths.State, "skills") + ". Son instrucciones; las herramientas y permisos los controla SheepCode.";
    }
    private void CreateSkillDialog()
    {
        using var dialog = new Form { Text = "🌸 Crea una skill para Sheep", Size = new(660, 560), MinimumSize = new(550, 480), StartPosition = FormStartPosition.CenterParent, BackColor = Background, ForeColor = Foreground, Font = Font };
        var layout = Stack(34, 40, 34, 54, 34, -1, 52); layout.Padding = new(16);
        var name = new TextBox { Dock = DockStyle.Fill, BackColor = Surface, ForeColor = Foreground, PlaceholderText = "ej. revisar-python" };
        var description = new TextBox { Dock = DockStyle.Fill, BackColor = Surface, ForeColor = Foreground, PlaceholderText = "Cuándo debe usar Sheep esta skill" };
        var instructions = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, BackColor = Surface, ForeColor = Foreground, PlaceholderText = "Pasos, herramientas disponibles y cómo comprobar el resultado…" };
        layout.Controls.Add(LabelFor("Nombre (minúsculas y guiones)", 10), 0, 0); layout.Controls.Add(name, 0, 1); layout.Controls.Add(LabelFor("Descripción · cómo reconocer la tarea", 10), 0, 2); layout.Controls.Add(description, 0, 3);
        layout.Controls.Add(LabelFor("Instrucciones · no cambian los permisos", 10), 0, 4); layout.Controls.Add(instructions, 0, 5);
        var save = ButtonFor("🌸 Guardar skill", true); save.Click += (_, _) => { try { var path = _agent.Skills.Create(name.Text.Trim(), description.Text.Trim(), instructions.Text); AppendChat("assistant", "Skill creada: " + path); dialog.Close(); } catch (Exception e) { MessageBox.Show(dialog, e.Message, "Skill"); } }; layout.Controls.Add(Flow(save), 0, 6);
        dialog.Controls.Add(layout); dialog.ShowDialog(this);
    }
    internal void ShowSkillPanel() => _tabs.SelectedIndex = 3;
    internal void ShowBrowserPanel() => _tabs.SelectedIndex = 5;
    internal void ShowPcPanel() => _tabs.SelectedIndex = 4;
    internal void ShowModelsPanel() => _tabs.SelectedIndex = 6;
}
