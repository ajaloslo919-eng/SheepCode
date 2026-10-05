using System.Text.Json;

namespace SheepCode;

internal sealed partial class MainForm
{
    private void ShowMcpMedia(string path, string mime)
    {
        AppendChat("tool", "📦 Medio recibido: " + path);
        var dialog = new Form { Text = "🐑 Artefacto de la conexión · Kuky 🌸", Size = new(700, 620), StartPosition = FormStartPosition.CenterParent, BackColor = Background, ForeColor = Foreground, Font = Font };
        var layout = Stack(-1, 54); layout.Padding = new(12);
        if (mime is "image/png" or "image/jpeg" or "image/gif")
        {
            try
            {
                using var stream = File.OpenRead(path); using var source = Image.FromStream(stream, false, false);
                if ((long)source.Width * source.Height > 16_000_000) throw new IOException("La imagen es demasiado grande para la vista integrada.");
                var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Image = new Bitmap(source) }; dialog.FormClosed += (_, _) => picture.Image?.Dispose(); layout.Controls.Add(picture, 0, 0);
            }
            catch (Exception e) when (e is IOException or ArgumentException or OutOfMemoryException) { layout.Controls.Add(LabelFor("El archivo se guardó. No se pudo mostrar la imagen: " + e.Message, 10), 0, 0); }
        }
        else layout.Controls.Add(LabelFor("Archivo recibido: " + Path.GetFileName(path) + "\nTipo: " + mime + "\nGuarda una copia para abrirlo con una aplicación compatible.", 11), 0, 0);
        var save = ButtonFor("💾 Guardar copia", true); var close = ButtonFor("Cerrar");
        save.Click += (_, _) => { using var picker = new SaveFileDialog { FileName = Path.GetFileName(path), Filter = "Archivo (*" + Path.GetExtension(path) + ")|*" + Path.GetExtension(path) }; if (picker.ShowDialog(dialog) == DialogResult.OK) { try { File.Copy(path, picker.FileName, true); } catch (Exception e) { MessageBox.Show(dialog, e.Message, "Guardar"); } } };
        close.Click += (_, _) => dialog.Close(); layout.Controls.Add(Flow(save, close), 0, 1); dialog.Controls.Add(layout); dialog.Show(this);
    }
    private void ConnectionsDialog()
    {
        if (_busy) { AppendChat("error", "Termina o detén la tarea antes de cambiar conexiones."); return; }
        using var dialog = new Form { Text = "🔗 Conexiones para Sheep & Kuky", Size = new(790, 690), MinimumSize = new(600, 520), StartPosition = FormStartPosition.CenterParent, BackColor = Background, ForeColor = Foreground, Font = Font };
        using var cancellation = new CancellationTokenSource(); dialog.FormClosing += (_, _) => cancellation.Cancel();
        var layout = Stack(104, -1, 50, 150); layout.Padding = new(14);
        layout.Controls.Add(LabelFor("🧩 Conecta herramientas externas por MCP\nURL HTTPS (HTTP solo local), o command con ruta absoluta de .exe y arguments separados.\nskills indica nombres del catálogo; allowedTools autoriza nombres exactos.\nUsa tokenEnvironment para una variable de entorno: conserva el token fuera de este archivo.\nGuardar es una autorización tuya para conectar; cada llamada muestra sus argumentos.", 9), 0, 0);
        var config = CodeBox(false); config.WordWrap = false; config.Text = McpTools.ConfigurationText(); layout.Controls.Add(config, 0, 1);
        var output = CodeBox(true); output.WordWrap = true; layout.Controls.Add(output, 0, 3);
        var selector = new ComboBox { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Surface, ForeColor = Foreground };
        void RefreshNames() { selector.Items.Clear(); selector.Items.AddRange(McpTools.Configuration().Where(c => c.Enabled).Select(c => (object)c.Name).ToArray()); if (selector.Items.Count > 0) selector.SelectedIndex = 0; }
        var save = ButtonFor("💾 Guardar", true); var inspect = ButtonFor("🔎 Ver herramientas"); var close = ButtonFor("Cerrar");
        save.Click += (_, _) => { try { _agent.Connections.SaveConfiguration(config.Text); config.Text = McpTools.ConfigurationText(); RefreshNames(); RefreshSkills(); output.Text = "Configuración guardada. Añade a allowedTools solo los nombres de herramientas que quieras autorizar.\n" + _agent.Connections.Status(); } catch (Exception e) { output.Text = e.Message; } };
        inspect.Click += async (_, _) =>
        {
            if (selector.SelectedItem is not string server) return; save.Enabled = inspect.Enabled = false;
            try { var result = await _agent.Connections.ListAsync(server, cancellation.Token); if (!output.IsDisposed) { using var json = JsonDocument.Parse(result); output.Text = JsonSerializer.Serialize(json.RootElement, AppPaths.Json); RefreshSkills(); } }
            catch (Exception e) { if (!output.IsDisposed) output.Text = e is OperationCanceledException ? "Consulta detenida." : e.Message; }
            finally { if (!dialog.IsDisposed) save.Enabled = inspect.Enabled = true; }
        };
        close.Click += (_, _) => dialog.Close(); layout.Controls.Add(Flow(save, selector, inspect, close), 0, 2);
        dialog.Controls.Add(layout); RefreshNames(); dialog.ShowDialog(this);
    }
    private Task<bool> ApproveConnectionCallAsync(string server, string tool, string arguments, CancellationToken token)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui(() =>
        {
            if (token.IsCancellationRequested || _closing) { result.TrySetResult(false); return; }
            using var dialog = new Form { Text = "🔗 Revisar llamada externa", Size = new(680, 530), MinimumSize = new(540, 420), StartPosition = FormStartPosition.CenterParent, BackColor = Background, ForeColor = Foreground, Font = Font };
            var layout = Stack(90, -1, 54); layout.Padding = new(14);
            layout.Controls.Add(LabelFor("Servidor: " + server + "\nHerramienta: " + tool + "\nEstos argumentos se enviarán al proveedor. Autoriza esta llamada concreta si coinciden con tu petición.", 10), 0, 0);
            var preview = CodeBox(true); preview.WordWrap = true; using var json = JsonDocument.Parse(arguments); preview.Text = JsonSerializer.Serialize(json.RootElement, AppPaths.Json); layout.Controls.Add(preview, 0, 1);
            var allow = ButtonFor("✓ Autorizar esta vez", true); var reject = ButtonFor("Cancelar");
            allow.DialogResult = DialogResult.OK; reject.DialogResult = DialogResult.Cancel; dialog.CancelButton = reject; layout.Controls.Add(Flow(allow, reject), 0, 2); dialog.Controls.Add(layout);
            using var registration = token.Register(() => Ui(() => { if (!dialog.IsDisposed) dialog.Close(); }));
            result.TrySetResult(dialog.ShowDialog(this) == DialogResult.OK && !token.IsCancellationRequested);
        });
        return result.Task;
    }
    private void UpdatesDialog(bool preview = false)
    {
        try { _agent.Skills.Require("updater"); } catch (Exception e) { AppendChat("error", e.Message); return; }
        using var dialog = new Form { Text = "🌷 SheepCode · Actualizaciones", Size = new(700, 550), MinimumSize = new(580, 450), StartPosition = FormStartPosition.CenterParent, BackColor = Background, ForeColor = Foreground, Font = Font };
        using var cancellation = new CancellationTokenSource(); dialog.FormClosing += (_, _) => cancellation.Cancel();
        var layout = Stack(90, 46, -1, 58); layout.Padding = new(16);
        layout.Controls.Add(LabelFor("🐑 SheepCode " + UpdateManager.CurrentVersion + " · el taller de Sheep & Kuky\nBuscar → descargar y verificar → guardar el editor y abrir setup.\nLa actualización conserva ajustes, modelos y voz; respalda app y source.", 11), 0, 0);
        var automatic = new CheckBox { Text = "🌸 Buscar una vez al día mientras SheepCode esté abierto", Checked = _preferences.AutoCheckUpdates, AutoSize = true, ForeColor = Accent };
        automatic.CheckedChanged += (_, _) => Guard(() => { _agent.Skills.Require("updater"); _preferences.AutoCheckUpdates = automatic.Checked; _preferences.Save(); }); layout.Controls.Add(automatic, 0, 1);
        var status = CodeBox(true); status.Font = new("Segoe UI", 10); status.WordWrap = true; layout.Controls.Add(status, 0, 2);
        Button check = ButtonFor("🔍 Buscar"), download = ButtonFor("📦 Descargar"), install = ButtonFor("🌷 Instalar", true), close = ButtonFor("Cerrar");
        void Refresh() { status.Text = _agent.Updates.Message + "\n\n" + (_agent.Updates.Available is { } release ? release.Page + "\nSHA-256: " + release.Sha256 + "\n" + (_agent.Updates.Downloaded ?? "Pendiente de descargar.") : "Repositorio: https://github.com/" + UpdateManager.Repository); download.Enabled = _agent.Updates.Available is not null; install.Enabled = _agent.Updates.Downloaded is not null; }
        async Task Run(string command)
        {
            check.Enabled = download.Enabled = install.Enabled = automatic.Enabled = false;
            try { status.Text = "🌷 Comprobando…"; await _agent.ControlAsync(command, cancellation.Token); }
            catch (Exception e) { if (!status.IsDisposed) status.Text = e is OperationCanceledException ? "Operación detenida." : e.Message; return; }
            finally { if (!dialog.IsDisposed) { check.Enabled = automatic.Enabled = true; download.Enabled = _agent.Updates.Available is not null; install.Enabled = _agent.Updates.Downloaded is not null; } }
            if (!dialog.IsDisposed) Refresh();
        }
        check.Click += async (_, _) => await Run("Busca actualizaciones"); download.Click += async (_, _) => await Run("Descarga la actualización");
        install.Click += async (_, _) => { try { await _agent.ControlAsync("Instala la actualización", cancellation.Token); dialog.Close(); } catch (Exception e) { status.Text = e.Message; } };
        close.Click += (_, _) => dialog.Close(); layout.Controls.Add(Flow(check, download, install, close), 0, 3); dialog.Controls.Add(layout); Refresh();
        if (preview) dialog.Shown += async (_, _) => { await Task.Delay(100); using var image = new Bitmap(dialog.Width, dialog.Height); dialog.DrawToBitmap(image, new(0, 0, image.Width, image.Height)); image.Save(Path.Combine(AppPaths.Root, "checks", "updater-gui.png")); dialog.Close(); };
        dialog.ShowDialog(this);
    }
    internal object CheckIntegrationsGui()
    {
        ShowSkillPanel(); RefreshSkills();
        var total = _skillCards.Controls.Count; _skillFilter.Text = "spreadsheets";
        if (_skillCards.Controls.Count != 1 || !_skillCards.Controls[0].Controls.OfType<Label>().Any(l => l.Text.Contains("locales"))) throw new IOException("El buscador o el estado de la skill no se muestran.");
        _skillFilter.Clear(); UpdatesDialog(preview: true); ShowSkillPanel();
        return new { loadedSkills = _agent.Skills.Items.Count, cards = total, filterMatches = 1, updater = UpdateManager.CurrentVersion, autoCheck = _preferences.AutoCheckUpdates, localAndMcpStates = true };
    }
}
