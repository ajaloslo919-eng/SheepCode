using System.Text.Json;

namespace SheepCode;

internal sealed partial class MainForm
{
    private readonly FlowLayoutPanel _skillCards = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, BackColor = Background, Padding = new(6) };
    private readonly RichTextBox _skillText = CodeBox(true), _modelText = CodeBox(true);
    private readonly TextBox _skillFilter = new() { Dock = DockStyle.Fill, BackColor = Surface, ForeColor = Foreground, PlaceholderText = "🔍 Buscar skills: documentos, Git, imágenes…" };
    private readonly Label _skillCount = LabelFor("🧩 Skills", 11);
    private bool _updateInstallRequested, _autoUpdateChecking;
    private DateTimeOffset _nextUpdateCheck = DateTimeOffset.UtcNow.AddSeconds(15);
    private readonly CancellationTokenSource _backgroundCancellation = new();
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
        var skills = NewPage("Skills"); skills.AutoScroll = true; var layout = Stack(66, 40, -1, 80, 84); layout.Padding = new(8); layout.Dock = DockStyle.Fill; layout.MinimumSize = new(0, 420);
        _skillCount.ForeColor = Accent;
        layout.Controls.Add(_skillCount, 0, 0); layout.Controls.Add(_skillFilter, 0, 1); layout.Controls.Add(_skillCards, 0, 2);
        _skillFilter.TextChanged += (_, _) => RefreshSkills();
        _skillText.Font = new("Segoe UI", 10); _skillText.WordWrap = true; layout.Controls.Add(_skillText, 0, 3);
        Button refresh = ButtonFor("↻ Recargar"), create = ButtonFor("🌸 Crear skill", true), capabilities = ButtonFor("Qué puedo hacer"), import = ButtonFor("📦 Importar"), connections = ButtonFor("🔗 Conexiones"), updates = ButtonFor("🌷 Actualizar");
        refresh.Click += (_, _) => Guard(_agent.Skills.Reload); create.Click += (_, _) => CreateSkillDialog(); capabilities.Click += (_, _) => ShowCapabilities();
        import.Click += (_, _) => { using var picker = new OpenFileDialog { Filter = "Skill (*.md)|*.md", Title = "Importar instrucciones SKILL.md" }; if (picker.ShowDialog(this) == DialogResult.OK) Guard(() => { _agent.Skills.Require("skill-installer"); AppendChat("assistant", "Skill importada: " + _agent.Skills.Import(picker.FileName)); }); };
        connections.Click += (_, _) => ConnectionsDialog(); updates.Click += (_, _) => UpdatesDialog();
        var quickUpdate = ButtonFor("🌷 Actualizar"); quickUpdate.Click += (_, _) => UpdatesDialog(); _toolbar.Controls.Add(quickUpdate); _toolbar.Controls.SetChildIndex(quickUpdate, 3);
        var actions = Flow(refresh, create, import, connections, updates, capabilities); actions.WrapContents = true; layout.Controls.Add(actions, 0, 4); skills.Controls.Add(layout);
        _agent.Connections.ApproveCall = ApproveConnectionCallAsync;
        _agent.Connections.MediaReceived += (path, mime) => Ui(() => ShowMcpMedia(path, mime));
        _agent.RequestUpdateInstall = () => { _agent.Updates.VerifyDownloaded(); _updateInstallRequested = true; BeginInvoke(Close); return Task.FromResult("🌷 Cerrando SheepCode para abrir el setup. Guarda cualquier archivo pendiente en el aviso del editor."); };
        _agent.Skills.Changed += () => Ui(() => { RefreshSkills(); if (_busy) _taskCancellation?.Cancel(); });
        _skillCards.Resize += (_, _) => { foreach (Control card in _skillCards.Controls) card.Width = SkillCardWidth(); };
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

        FlowLayoutPanel ModelRow(params Control[] controls) { var row = Flow(controls); row.Padding = new(4, 0, 4, 0); return row; }
        var models = NewPage("Modelos"); models.AutoScroll = true; var model = Stack(160, 52, 52, 52, 52, 48); model.Padding = new(10); model.Dock = DockStyle.Top; model.Height = 436;
        _modelText.WordWrap = true; _modelText.Font = new("Consolas", 10); model.Controls.Add(_modelText, 0, 0);
        Button load = ButtonFor("✨ Cargar IA", true), unload = ButtonFor("⏸ Liberar"), status = ButtonFor("↻ Estado");
        load.Click += (_, _) => StartTask(async token => { try { await _agent.SubmitAsync("Activa el motor", token); } finally { RefreshModels(); } });
        unload.Click += (_, _) => StartTask(async token => { await _agent.SubmitAsync("Desactiva el motor", token); RefreshModels(); }); status.Click += (_, _) => Guard(RefreshModels);
        Button analyze = ButtonFor("💻 Analizar"), install = ButtonFor("🌸 Instalar");
        analyze.Click += (_, _) => StartTask(async token => { await _agent.SubmitAsync("Analiza el sistema", token); });
        install.Click += (_, _) => StartTask(async token => { try { await _agent.SubmitAsync("Instala el modelo recomendado", token); } finally { RefreshModels(); } });
        Button copyDiagnosis = ButtonFor("📋 Copiar");
        copyDiagnosis.Click += (_, _) => Guard(() => { _agent.Skills.Require("models"); Clipboard.SetText(JsonSerializer.Serialize(_agent.ModelStatus(), AppPaths.Json)); AppendChat("assistant", "🐱 Diagnóstico del modelo copiado. Incluye el perfil seleccionado, archivos, rutas y último error de carga."); });
        model.Controls.Add(ModelRow(load, unload, status, copyDiagnosis), 0, 1); Button repair = ButtonFor("🧰 Reparar");
        repair.Click += (_, _) => StartTask(async token => { try { await _agent.SubmitAsync("Repara el motor", token); } finally { RefreshModels(); } });
        model.Controls.Add(ModelRow(analyze, install, repair), 0, 2);
        Button coder = ButtonFor("🐑 Qwen2.5-Coder"), granite = ButtonFor("🌱 Granite 350M"), granite1b = ButtonFor("🌷 Granite 1.5B"), restore = ButtonFor("↶ Perfil anterior");
        coder.Click += (_, _) => StartTask(async token => { await _agent.SubmitAsync("Instala Qwen2.5-Coder", token); RefreshModels(); RefreshSettings(); });
        granite.Click += (_, _) => StartTask(async token => { await _agent.SubmitAsync("Instala Granite 4.0", token); RefreshModels(); RefreshSettings(); });
        granite1b.Click += (_, _) => StartTask(async token => { await _agent.SubmitAsync("Instala Granite 1.5B", token); RefreshModels(); RefreshSettings(); });
        restore.Click += (_, _) => StartTask(async token => { await _agent.SubmitAsync("Restaura el perfil anterior", token); RefreshModels(); RefreshSettings(); });
        var modelTip = new ToolTip(); modelTip.SetToolTip(copyDiagnosis, "Copiar el diagnóstico completo: modelo seleccionado, rutas y último error."); modelTip.SetToolTip(install, "Instalar y seleccionar el modelo recomendado para este equipo."); modelTip.SetToolTip(repair, "Restaurar el runtime nativo seleccionado desde el paquete local; no descarga pesos ni cambia el perfil."); modelTip.SetToolTip(coder, "Instala y selecciona el modelo oficial 0.5B Instruct Q8_0 en Strata CPU, con descarga verificada. Conserva el perfil anterior. Revisa el código generado.");
        modelTip.SetToolTip(granite, "Opción experimental: Granite 4.0 H 350M Q8_0 oficial de IBM en Strata CPU. Descarga verificada, contexto 4096 y perfil anterior conservado. Las pruebas detectaron código incorrecto; revisa las propuestas.");
        modelTip.SetToolTip(granite1b, "Opción experimental: Granite 4.0 H1B de IBM, 1.5B parámetros, Q4_K_M de 901 MB. Descarga verificada, contexto 4096 y presupuesto 1536 MiB. Las pruebas de creación y corrección de código fallaron. Conserva el perfil anterior; revisa las propuestas.");
        model.Controls.Add(ModelRow(granite1b, restore), 0, 3); model.Controls.Add(ModelRow(coder, granite), 0, 4);
        _powerMode.Items.AddRange(["🔋 Automático", "🌱 Ahorro", "⚡ Perfil completo"]);
        StyleCombo(_powerMode);
        _powerMode.SelectedIndexChanged += (_, _) => { if (_refreshingSettings || _powerMode.SelectedIndex < 0) return; var command = _powerMode.SelectedIndex switch { 0 => "Modo portátil automático", 1 => "Activa el modo ahorro", _ => "Desactiva el modo ahorro" }; StartTask(async token => { try { await _agent.SubmitAsync(command, token); } finally { RefreshSettings(); RefreshModels(); } }); };
        var powerLabel = LabelFor("💻 Energía", 10); powerLabel.Dock = DockStyle.None; powerLabel.Width = 94; powerLabel.Height = 34;
        model.Controls.Add(ModelRow(powerLabel, _powerMode), 0, 5);
        models.Controls.Add(model);
        _tabs.TabPages.Add(_compactFiles);
        _modelTimer.Interval = 5000; _modelTimer.Tick += async (_, _) => { _engine.RefreshPowerPriority(); if (_tabs.SelectedIndex == 6) RefreshModels(); RunDueAutomation(); await AutoCheckUpdateAsync(); }; _modelTimer.Start(); RefreshModels();
    }
    private Panel NewPage(string name) { var page = new Panel { Text = name, BackColor = Background, Padding = new(6) }; _tabs.TabPages.Add(page); return page; }
    private void OpenWeb() { var url = _address.Text.Trim(); StartTask(async token => { await _browserTools.OpenAsync(url, token); }); }
    private void RefreshModels()
    {
        var profile = _engine.Profile; var installation = _engine.InstallationStatus();
        var state = _engine.Ready ? "🐑 IA cargada y lista" : !profile.Configured ? "🌱 Sin modelo seleccionado" :
            !installation.FilesReady ? "⚠ Instalación incompleta" : _engine.LastLoadError is not null ? "⚠ Falló el arranque" :
            "🌷 Archivos presentes · pulsa Cargar IA";
        var text = "🧠 Modelo seleccionado: " + profile.Label + "\n" + state + "\nMotor: " + _engine.State +
            "\nPerfil integrado: " + profile.Kind + "\n\n" + (_engine.LastLoadError is { } error ? "⚠ Último error de carga:\n" + error : installation.Message);
        foreach (var file in installation.Files) text += "\n\n" + (file.Present && file.SizeMatches != false ? "✓ " : "⚠ ") + file.Name + ":\n" + (file.Path.Length == 0 ? "sin configurar" : file.Path);
        text += "\n\n💻 " + SheepCode.Distribution.PowerScanner.Read().Describe() + " · modo " + _preferences.PortableMode +
            "\nVoz: " + _voice.State + "\n\n📋 Copiar diagnóstico incluye el estado completo.\nEl catálogo enumera opciones; no indica que todas estén instaladas o recomendadas.";
        if (_modelText.Text == text) return;
        _modelText.Text = text; _modelText.Select(0, 0); _modelText.ScrollToCaret();
    }
    internal string ModelPanelText() => _modelText.Text;
    internal void RefreshModelPanel() => RefreshModels();
    internal bool ModelSummaryVisible() => _modelText.Visible && _modelText.Parent?.Parent is { } page && page.RectangleToScreen(page.ClientRectangle).Contains(new Rectangle(_modelText.PointToScreen(Point.Empty), new Size(_modelText.Width, Math.Min(70, _modelText.Height))));
    private void RefreshSkills()
    {
        _skillCount.Text = "🧩 " + _agent.Skills.Items.Count + " habilidades para Sheep & Kuky\nHerramientas locales, flujos de código y proveedores MCP configurables 🌸";
        foreach (Control old in _skillCards.Controls.Cast<Control>().ToArray()) old.Dispose(); _skillCards.Controls.Clear();
        foreach (var skill in _agent.Skills.Items.Where(s => (s.Name + " " + s.Description).Contains(_skillFilter.Text.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            var card = new Panel { Width = SkillCardWidth(), Height = 164, BackColor = Surface, Padding = new(8), Margin = new(0, 0, 8, 8) };
            var toggle = new CheckBox { Text = skill.Emoji + "  " + skill.Name, Checked = _agent.Skills.Enabled(skill.Name), ForeColor = Accent, Font = new("Segoe UI", 10, FontStyle.Bold), AutoSize = false, AutoEllipsis = true, Location = new(12, 8), Size = new(card.Width - 84, 36), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            toggle.CheckedChanged += (_, _) => Guard(() => _agent.Skills.SetEnabled(skill.Name, toggle.Checked)); card.Controls.Add(toggle);
            var details = ButtonFor("Ver"); details.Anchor = AnchorStyles.Top | AnchorStyles.Right; details.Location = new(card.Width - 66, 5); details.Width = 56; details.Click += (_, _) => _skillText.Text = skill.Path + "\n\n" + skill.Instructions; card.Controls.Add(details);
            var state = LabelFor(_agent.Skills.State(skill.Name), 8); state.Dock = DockStyle.None; state.SetBounds(12, 43, card.Width - 24, 32); state.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; state.AutoEllipsis = false; card.Controls.Add(state);
            var description = LabelFor(skill.Description, 9); description.Dock = DockStyle.None; description.SetBounds(12, 79, card.Width - 24, 77); description.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; description.AutoEllipsis = false; card.Controls.Add(description); _skillCards.Controls.Add(card);
        }
        if (_agent.Skills.Errors.Count > 0) _skillText.Text = string.Join('\n', _agent.Skills.Errors);
        else if (_skillText.TextLength == 0) _skillText.Text = "🐱 Kuky tip: usa $code, $desktop, $browser o $models en el chat.\n\nTus nuevas skills se guardan en " + Path.Combine(AppPaths.State, "skills") + ". Son instrucciones; las herramientas y permisos los controla SheepCode.";
    }
    private int SkillCardWidth() => Math.Max(216, (_skillCards.ClientSize.Width - 30) / (_skillCards.ClientSize.Width < 530 ? 1 : 2));
    private void RunDueAutomation()
    {
        if (_busy || _checking || _closing || !_engine.Ready || !_agent.Skills.Enabled("automate")) return;
        var item = _agent.Automations.Due(_agent.Workspace?.Root, DateTimeOffset.UtcNow); if (item is null) return;
        item.Next = DateTimeOffset.UtcNow.AddMinutes(item.Minutes); _agent.Automations.Save();
        StartTask(async token =>
        {
            try { AppendChat("tool", "⏰ Automatización: " + item.Name); var result = await _agent.SubmitAsync(item.Prompt, token, scheduled: true); item.LastResult = result[..Math.Min(1000, result.Length)]; }
            catch (OperationCanceledException) { item.LastResult = "Detenida por la persona."; throw; }
            catch (Exception e) { item.LastResult = "Falló: " + e.Message; throw; }
            finally { _agent.Automations.Save(); }
        });
    }
    private async Task AutoCheckUpdateAsync()
    {
        if (_busy || _checking || _closing || _autoUpdateChecking || !_preferences.AutoCheckUpdates || !_agent.Skills.Enabled("updater") || DateTimeOffset.UtcNow < _nextUpdateCheck) return;
        _autoUpdateChecking = true; _nextUpdateCheck = DateTimeOffset.UtcNow.AddDays(1);
        try { var result = await _agent.Updates.CheckAsync(_backgroundCancellation.Token); if (!_closing && !IsDisposed && _agent.Updates.Available is not null) AppendChat("assistant", result); }
        catch (Exception e) { _nextUpdateCheck = DateTimeOffset.UtcNow.AddMinutes(30); if (!_closing && !IsDisposed && e is not OperationCanceledException) AppendChat("error", "Actualizador: " + e.Message); }
        finally { _autoUpdateChecking = false; }
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
