using SheepCode.Distribution;

namespace SheepCode;

internal sealed class SetupForm : Form
{
    private static readonly Color Bg = Color.FromArgb(25, 21, 36), Surface = Color.FromArgb(35, 29, 48), Ink = Color.FromArgb(247, 234, 245), Pink = Color.FromArgb(239, 164, 199);
    private readonly TextBox location = Input(), models = Input();
    private readonly ComboBox choice = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = Ink };
    private readonly RichTextBox hardware = new() { Text = "Leyendo tu equipo…", Dock = DockStyle.Fill, ReadOnly = true, WordWrap = true,
        DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Surface, ForeColor = Ink, Font = new("Segoe UI", 10) };
    private readonly Label description = Label("", 10), step = Label("01 · Bienvenido a tu pequeño taller", 13);
    private readonly Button rescan = Button("↻ Volver a detectar");
    private readonly SystemHardware? hardwareFixture;
    private readonly bool upgrading;
    private readonly RichTextBox log = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Surface, ForeColor = Ink, Font = new("Segoe UI", 9) };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Height = 10, Style = ProgressBarStyle.Continuous };
    private readonly CheckBox desktop = new() { Text = "🐑 Acceso directo en el escritorio", AutoSize = true, Checked = true, ForeColor = Ink }, menu = new() { Text = "🌸 Acceso en el menú Inicio", AutoSize = true, Checked = true, ForeColor = Ink };
    private readonly CheckBox dictation = new() { Text = "🎙 Dictado local · 181 MB", AutoSize = true, Checked = true, ForeColor = Ink };
    private readonly Button install = Button("🌸 Preparar mi taller", true), cancel = Button("Cerrar"), open = Button("🐑 Abrir SheepCode", true);
    private SystemHardware? system; private InstallPlan? recommended; private CancellationTokenSource? cancellation; private bool busy, completed;
    internal SetupForm(bool preview, SystemHardware? fixture = null, string? target = null, bool upgrade = false)
    {
        hardwareFixture = fixture;
        upgrading = upgrade;
        Text = "🐑 SheepCode Setup · Sheep & Kuky 🌸"; var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 900);
        Size = new(Math.Min(990, screen.Width - 20), Math.Min(900, screen.Height - 20)); MinimumSize = new(760, 540); StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg; ForeColor = Ink; Font = new("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "sheep.ico"))) Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "sheep.ico"));
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, Height = 1040, Padding = new(28, 18, 28, 12), ColumnCount = 1, RowCount = 11, BackColor = Bg };
        int[] heights = [100, 44, 48, 55, 55, 240, 40, 136, 60, 16, 0];
        for (var i = 0; i < heights.Length; i++) layout.RowStyles.Add(new(heights[i] == 0 ? SizeType.Percent : SizeType.Absolute, heights[i] == 0 ? 100 : heights[i]));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 }; header.ColumnStyles.Add(new(SizeType.Absolute, 185)); header.ColumnStyles.Add(new(SizeType.Percent, 100));
        header.Controls.Add(new MascotBanner(), 0, 0); header.Controls.Add(Label("SheepCode ✦\nSheep & Kuky te preparan un rincón para crear 🌸", 18), 1, 0); layout.Controls.Add(header, 0, 0);
        step.ForeColor = Pink; layout.Controls.Add(step, 0, 1);
        layout.Controls.Add(Label("Código abierto y editable incluido · IA local para tu equipo\nElige dónde guardar tu taller y sus modelos. El setup analiza el PC por ti.", 10), 0, 2);
        location.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SheepCode"); models.Text = HardwareScanner.SuggestedModelFolder();
        if (target is not null) location.Text = Path.GetFullPath(target);
        if (upgrading)
        {
            if (!File.Exists(Path.Combine(location.Text, "installation.json"))) throw new IOException("La carpeta elegida no contiene una instalación de SheepCode.");
            models.Text = InstallationPaths.Resolve(location.Text, InstallationPaths.Load(location.Text).Models);
            dictation.Checked = desktop.Checked = menu.Checked = false;
            install.Text = "🌷 Actualizar mi taller"; step.Text = "01 · Nueva versión, el mismo taller";
        }
        layout.Controls.Add(FolderRow("📂 Taller", location), 0, 3); layout.Controls.Add(FolderRow("🧠 Modelos", models), 0, 4);
        var hardwareBox = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new(12, 6, 12, 8) };
        var hardwareTitle = new TableLayoutPanel { Dock = DockStyle.Top, Height = 48, ColumnCount = 2 }; hardwareTitle.ColumnStyles.Add(new(SizeType.Percent, 100)); hardwareTitle.ColumnStyles.Add(new(SizeType.Absolute, 200));
        hardwareTitle.Controls.Add(Label("💻 Tu equipo · todas las gráficas", 10), 0, 0); rescan.Dock = DockStyle.Fill; rescan.AutoSize = false; rescan.Margin = new(0, 3, 0, 5); hardwareTitle.Controls.Add(rescan, 1, 0);
        hardwareBox.Controls.Add(hardware); hardwareBox.Controls.Add(hardwareTitle); layout.Controls.Add(hardwareBox, 0, 5);
        layout.Controls.Add(choice, 0, 6); description.ForeColor = Color.FromArgb(199, 182, 218); layout.Controls.Add(description, 0, 7);
        var shortcuts = new FlowLayoutPanel { Dock = DockStyle.Fill }; shortcuts.Controls.AddRange([desktop, menu, dictation]); layout.Controls.Add(shortcuts, 0, 8);
        choice.DrawMode = DrawMode.OwnerDrawFixed; choice.ItemHeight = 25;
        choice.DrawItem += (_, e) => { if (e.Index < 0 || e.Graphics is not { } graphics) return; using var background = new SolidBrush(Surface); graphics.FillRectangle(background, e.Bounds); TextRenderer.DrawText(graphics, choice.Items[e.Index]?.ToString() ?? "", Font, e.Bounds, Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter); };
        log.Text = "🐱 Kuky tip: el código editable siempre se incluye.\nPuedes cambiar el modelo elegido o empezar solo con el editor.\n\nDurante la instalación verás aquí las descargas y sus comprobaciones 🌸";
        layout.Controls.Add(progress, 0, 9); layout.Controls.Add(log, 0, 10);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, Padding = new(0, 0, 28, 8), FlowDirection = FlowDirection.RightToLeft }; buttons.Controls.AddRange([install, open, cancel]); open.Visible = false;
        var scrolling = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; scrolling.Controls.Add(layout); Controls.Add(scrolling); Controls.Add(buttons);
        choice.SelectedIndexChanged += (_, _) => { if (choice.SelectedItem is InstallPlan plan) description.Text = plan.Explanation + "\n" + (plan.DownloadBytes > 0 ? $"Descarga aproximada: {plan.DownloadBytes / 1073741824d:F2} GB. " : "") + "Fuente editable en source · lectura de voz RX 580 requiere su paquete original."; };
        install.Click += async (_, _) => await InstallAsync(); cancel.Click += (_, _) => { if (busy) { cancellation?.Cancel(); cancel.Enabled = false; } else Close(); };
        open.Click += (_, _) => { Installer.Launch(location.Text); Close(); };
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; cancellation?.Cancel(); } };
        Shown += (_, _) => Analyze(); models.Leave += (_, _) => { if (!busy) Analyze(); };
        rescan.Click += (_, _) => { if (!busy) Analyze(); };
        if (preview) Shown += async (_, _) => { await Task.Delay(800); using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new(0, 0, Width, Height)); bitmap.Save(Path.Combine(AppContext.BaseDirectory, "setup-preview.png")); Close(); };
    }
    private static TextBox Input() => new() { Dock = DockStyle.Fill, BackColor = Surface, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle };
    private static Label Label(string text, float size) => new() { Text = text, Font = new("Segoe UI", size), ForeColor = Ink, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = false, UseMnemonic = false };
    private static Button Button(string text, bool primary = false) => new() { Text = text, AutoSize = true, Height = 40, Padding = new(12, 6, 12, 6), FlatStyle = FlatStyle.Flat, BackColor = primary ? Pink : Surface, ForeColor = primary ? Bg : Ink, Margin = new(6, 7, 0, 0) };
    private Control FolderRow(string title, TextBox input)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new(0, 8, 0, 8) }; row.RowStyles.Add(new(SizeType.Percent, 100)); row.ColumnStyles.Add(new(SizeType.Absolute, 100)); row.ColumnStyles.Add(new(SizeType.Percent, 100)); row.ColumnStyles.Add(new(SizeType.Absolute, 104));
        row.Controls.Add(Label(title, 10), 0, 0); row.Controls.Add(input, 1, 0); var browse = Button("Elegir…"); browse.Dock = DockStyle.Fill; browse.AutoSize = false; browse.Padding = new(0); browse.Margin = new(6, 0, 0, 0);
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true, SelectedPath = Directory.Exists(input.Text) ? input.Text : "" }; if (dialog.ShowDialog(this) == DialogResult.OK) { input.Text = dialog.SelectedPath; Analyze(); } }; row.Controls.Add(browse, 2, 0); return row;
    }
    private void Analyze()
    {
        try
        {
            system = hardwareFixture ?? HardwareScanner.Scan(models.Text); hardware.Text = system.Describe(); hardware.Select(0, 0); hardware.ScrollToCaret();
            if (upgrading)
            {
                recommended = ModelCatalog.EditorOnly(); choice.Items.Clear(); choice.Items.Add(recommended); choice.SelectedIndex = 0; choice.Enabled = false;
                description.Text = "Actualizar app y código editable. El setup respalda la versión anterior y conserva modelos, dictado, conexiones, proyectos, ajustes y voz neuronal. No descarga un modelo nuevo."; install.Enabled = true; return;
            }
            recommended = ModelCatalog.Recommend(system, ModelCatalog.FindExisting()); choice.Items.Clear(); choice.Items.Add(recommended);
            if (recommended.Kind != "strata-cpu") choice.Items.Add(ModelCatalog.LowMemory());
            choice.Items.Add(ModelCatalog.LowMemory("qwen2.5-coder-0.5b"));
            choice.Items.Add(ModelCatalog.LowMemory("granite-4.0-h-350m"));
            choice.Items.Add(ModelCatalog.LowMemory("granite-4.0-h-1b"));
            if (recommended.Kind == "reuse" && ModelCatalog.StrataHardware(system) && system.DiskFreeBytes >= 100L * 1073741824) choice.Items.Add(ModelCatalog.Strata());
            foreach (var model in ModelCatalog.Models.Where(m => m.Id.StartsWith("qwen3-", StringComparison.Ordinal) && m.Size < (recommended.Model?.Size ?? long.MaxValue) && m.Size + 3L * 1073741824 < system.DiskFreeBytes && m.Size * 1.25 + 700_000_000 < ModelCatalog.MemoryBudget(system)).Reverse())
                choice.Items.Add(new InstallPlan(model.Id, "🌱 " + model.Label + " · alternativa más ligera", "llama", "Descarga más pequeña y menor uso de memoria; revisa los cambios de código que proponga. Usa Vulkan si se verifica o CPU explícitamente.", model.Size, system.Portable || system.RamGiB < 7.5 ? 4096 : 8192, model));
            choice.Items.Add(ModelCatalog.EditorOnly()); choice.SelectedIndex = 0; install.Enabled = true;
        }
        catch (Exception e) { hardware.Text = "💻 " + e.Message; choice.Items.Clear(); choice.Items.Add(ModelCatalog.EditorOnly()); choice.SelectedIndex = 0; install.Enabled = true; }
    }
    private async Task InstallAsync()
    {
        if (choice.SelectedItem is not InstallPlan plan || system is null) return;
        busy = true; cancellation = new(); install.Enabled = rescan.Enabled = false; location.ReadOnly = models.ReadOnly = true; choice.Enabled = desktop.Enabled = menu.Enabled = dictation.Enabled = false;
        cancel.Text = "Cancelar descarga"; step.Text = "02 · Sheep prepara tu taller…"; progress.Style = ProgressBarStyle.Marquee;
        var updates = new Progress<InstallProgress>(p =>
        {
            step.Text = p.Stage; if (p.Fraction is { } fraction) { progress.Style = ProgressBarStyle.Continuous; progress.Value = Math.Clamp((int)(fraction * 100), 0, 100); }
            else progress.Style = ProgressBarStyle.Marquee;
            if (log.TextLength > 12000) log.Text = log.Text[^6000..]; log.AppendText(p.Detail + "\n"); log.ScrollToCaret();
        });
        try
        {
            var request = new InstallRequest(location.Text, models.Text, plan, desktop.Checked, menu.Checked, true, dictation.Checked);
            await Task.Run(() => Installer.InstallAsync(request, updates, cancellation.Token));
            completed = true; step.Text = "03 · Tu taller está listo 🐑🐱🌸"; open.Visible = true; install.Visible = false; cancel.Text = "Terminar";
            log.AppendText("\nCódigo editable: " + Path.Combine(location.Text, "source") + "\nTus proyectos y ajustes se guardan en la carpeta elegida.\n");
        }
        catch (OperationCanceledException) { step.Text = "Descarga pausada 🌸"; log.AppendText("Puedes reintentar: los archivos .part se conservan para continuar.\n"); }
        catch (Exception e) { step.Text = "Hay algo que resolver"; log.AppendText(e.Message + "\nConsulta la guía incluida y vuelve a intentar.\n"); }
        finally
        {
            busy = false; cancellation.Dispose(); cancellation = null; cancel.Enabled = true; if (!completed) { install.Enabled = rescan.Enabled = true; desktop.Enabled = menu.Enabled = dictation.Enabled = true; choice.Enabled = !upgrading; cancel.Text = "Cerrar"; location.ReadOnly = models.ReadOnly = false; }
            progress.Style = ProgressBarStyle.Continuous;
        }
    }
    internal object HardwareLayoutSnapshot(bool scrollToLast)
    {
        if (scrollToLast) { hardware.Select(hardware.TextLength, 0); hardware.ScrollToCaret(); }
        var last = hardware.GetPositionFromCharIndex(Math.Max(0, hardware.TextLength - 1));
        var button = RectangleToClient(install.RectangleToScreen(install.ClientRectangle));
        return new { size = new { Width, Height }, dpi = DeviceDpi, hardwareText = hardware.Text,
            modelChoices = choice.Items.OfType<InstallPlan>().Select(p => new { id = p.Id, label = p.Label }).ToArray(), selectedModel = (choice.SelectedItem as InstallPlan)?.Id,
            scrollable = hardware.ScrollBars == RichTextBoxScrollBars.Vertical, lastLineReachable = last.Y >= 0 && last.Y < hardware.ClientSize.Height,
            installButtonVisible = ClientRectangle.Contains(button), gpuCount = system?.Gpus.Count(g => !g.Software), hardwareHeight = hardware.ClientSize.Height };
    }
    internal void CaptureWindow(string path) { using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new(0, 0, Width, Height)); bitmap.Save(path); }
}
