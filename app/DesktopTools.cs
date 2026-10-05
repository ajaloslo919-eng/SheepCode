using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace SheepCode;

internal sealed record PcWindow(long Id, int Pid, long Started, string Title, string Process);
internal sealed record PcNode(string Id, string Name, string Type, string Value, bool Clickable, bool Editable, string NativeClass = "");
internal sealed record PcView(PcWindow Window, PcNode[] Nodes, bool Truncated);
internal sealed record PcRequest(string Action, PcWindow? Window = null, string Node = "", string Text = "", PcNode? Expected = null);
internal sealed record PcResponse(bool Ok, string Error, PcWindow[]? Windows = null, PcView? View = null);

internal sealed class DesktopTools(SkillRegistry skills)
{
    internal PcWindow? Selected { get; private set; }
    internal PcView? Observation { get; private set; }
    internal event Action? Changed;
    internal async Task<PcWindow[]> WindowsAsync(CancellationToken token)
    { skills.Require("desktop"); return (await Request(new("list"), token)).Windows ?? []; }
    internal async Task SelectAsync(long id, CancellationToken token)
    {
        var windows = await WindowsAsync(token);
        Selected = windows.FirstOrDefault(w => w.Id == id) ?? throw new ArgumentException("Esa ventana ya no está disponible.");
        if (Selected.Pid == Environment.ProcessId && Selected.Title != "SheepCode · Ventana de prueba de Kuky")
        { Selected = null; throw new InvalidOperationException("Los permisos de SheepCode se configuran desde la entrada humana y sus paneles; no se automatiza su propia GUI."); }
        Observation = null; Changed?.Invoke();
    }
    internal void Release() { Selected = null; Observation = null; Changed?.Invoke(); }
    internal async Task<PcView> ReadAsync(CancellationToken token)
    {
        skills.Require("desktop");
        Observation = (await Request(new("read", RequireWindow()), token)).View ?? throw new IOException("La ventana no devolvió controles.");
        Changed?.Invoke(); return Observation;
    }
    internal async Task<PcView> ActAsync(string action, string node, string text, string humanRequest, CancellationToken token)
    {
        skills.Require("desktop");
        var observed = Observation?.Nodes.FirstOrDefault(n => n.Id == node) ?? throw new InvalidOperationException("Lee la ventana actual antes de actuar en ese control.");
        if (action == "click") ActionIntent.Check(observed.Name, humanRequest);
        if (text.Length > 4000) throw new ArgumentException("Texto demasiado largo; máximo 4000 caracteres.");
        if (action == "type" && System.Text.RegularExpressions.Regex.IsMatch(text, @"(?i)^\s*(?:cmd(?:\.exe)?\s|powershell\s|pwsh\s|bash\s|sh\s|python(?:\.exe)?\s+-|curl\s|wget\s|start-process\s|invoke-expression\s)"))
            throw new InvalidOperationException("Los comandos se gestionan con las comprobaciones habilitadas del proyecto.");
        Observation = (await Request(new(action, RequireWindow(), node, text, observed), token)).View ?? throw new IOException("No se pudo verificar la ventana después de actuar.");
        Changed?.Invoke(); return Observation;
    }
    private PcWindow RequireWindow() => Selected ?? throw new InvalidOperationException("Control del PC sin configurar: selecciona una ventana en PC o con «Selecciona la ventana ID». No se controlan otras ventanas.");
    private static async Task<PcResponse> Request(PcRequest request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        info.ArgumentList.Add("--pc-helper");
        using var process = Process.Start(info) ?? throw new IOException("No arrancó el componente de automatización de Windows.");
        using var stop = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, AppPaths.Json).Replace("\r", "").Replace("\n", "")); process.StandardInput.Close();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("La ventana no respondió en 12 segundos. Se detuvo su helper; puedes volver a inspeccionarla."); }
        var response = JsonSerializer.Deserialize<PcResponse>(await output, AppPaths.Json) ?? throw new IOException("Respuesta de Windows vacía: " + await errors);
        if (!response.Ok) throw new InvalidOperationException(response.Error); return response;
    }
}

internal static class PcHelper
{
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint parameter);
    private delegate bool EnumProc(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder title, int capacity);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    internal static int Run()
    {
        using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        PcResponse response;
        try
        {
            var request = JsonSerializer.Deserialize<PcRequest>(input.ReadLine() ?? "", AppPaths.Json) ?? throw new ArgumentException("Petición inválida.");
            if (request.Action == "list") response = new(true, "", ListWindows());
            else
            {
                var window = request.Window ?? throw new ArgumentException("No hay una ventana seleccionada.");
                Verify(window);
                var root = AutomationElement.FromHandle((nint)window.Id);
                if (request.Action is "click" or "type")
                {
                    if (new[] { "cmd", "powershell", "pwsh", "windowsterminal", "conhost" }.Contains(window.Process.ToLowerInvariant())) throw new InvalidOperationException("Los terminales se gestionan con las comprobaciones del proyecto.");
                    if (request.Node.StartsWith("hw", StringComparison.Ordinal))
                    {
                        NativeControls.Act(window, request);
                        response = new(true, "", View: ReadView(window, root));
                        output.WriteLine(JsonSerializer.Serialize(response, AppPaths.Json).Replace("\r", "").Replace("\n", "")); return 0;
                    }
                    var item = Walk(root).FirstOrDefault(p => NodeId(p.Element) == request.Node).Element ?? throw new InvalidOperationException("El control cambió o desapareció. Vuelve a leer la ventana.");
                    var current = item.Current;
                    var observed = request.Expected ?? throw new InvalidOperationException("Falta la lectura previa del control.");
                    if (current.Name != observed.Name || current.ControlType.ProgrammaticName != observed.Type) throw new InvalidOperationException("El control cambió desde la lectura. Vuelve a inspeccionar la ventana.");
                    if (current.IsPassword || !current.IsEnabled || current.IsOffscreen) throw new InvalidOperationException("El control está oculto, desactivado o contiene una contraseña.");
                    for (var parent = item; parent is not null; parent = TreeWalker.ControlViewWalker.GetParent(parent))
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(parent.Current.Name, @"(?i)\b(terminal|powershell|command prompt|símbolo del sistema)\b")) throw new InvalidOperationException("No se automatizan terminales embebidos.");
                        if (parent == root) break;
                    }
                    Verify(window);
                    if (request.Action == "type")
                    {
                        if (!item.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || ((ValuePattern)pattern).Current.IsReadOnly) throw new InvalidOperationException("Ese campo no permite escribir mediante UI Automation.");
                        ((ValuePattern)pattern).SetValue(request.Text);
                    }
                    else if (item.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) ((InvokePattern)invoke).Invoke();
                    else if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection)) ((SelectionItemPattern)selection).Select();
                    else if (item.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle)) ((TogglePattern)toggle).Toggle();
                    else throw new InvalidOperationException("Ese control no tiene una acción compatible. No se usaron clics a ciegas.");
                }
                else if (request.Action == "focus") { if (!SetForegroundWindow((nint)window.Id)) throw new InvalidOperationException("Windows no permitió enfocar la ventana."); }
                else if (request.Action != "read") throw new ArgumentException("Acción de Windows desconocida.");
                Verify(window); response = new(true, "", View: ReadView(window, root));
            }
        }
        catch (Exception e) { response = new(false, e.Message); }
        output.WriteLine(JsonSerializer.Serialize(response, AppPaths.Json).Replace("\r", "").Replace("\n", "")); return response.Ok ? 0 : 1;
    }
    private static PcView ReadView(PcWindow window, AutomationElement root)
    {
        PcNode[] nodes;
        try { nodes = Walk(root).Select(p => ToNode(p.Element)).ToArray(); }
        catch (ElementNotAvailableException) { nodes = []; }
        // Standard WinForms EDIT/BUTTON controls have stable HWNDs. Keep them usable when their UIA provider returns an incomplete tree.
        var native = NativeControls.Read(window);
        for (var i = 0; i < native.Count; i++)
        {
            var node = native[i];
            var index = Array.FindIndex(nodes, n => n.Id == node.Id);
            if (index >= 0) native[i] = node with { Name = nodes[index].Name, Type = nodes[index].Type };
        }
        var nativeIds = native.Select(n => n.Id).ToHashSet();
        nodes = nodes.Where(n => !nativeIds.Contains(n.Id)).Concat(native).Take(180).ToArray(); return new(window, nodes, nodes.Length >= 180);
    }
    private static PcWindow[] ListWindows()
    {
        var result = new List<PcWindow>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            var title = new StringBuilder(512); GetWindowText(handle, title, title.Capacity); if (title.Length == 0) return true;
            GetWindowThreadProcessId(handle, out var pid);
            try { using var process = Process.GetProcessById((int)pid); result.Add(new(handle, (int)pid, process.StartTime.ToUniversalTime().Ticks, title.ToString(), process.ProcessName)); } catch (Exception) { }
            return result.Count < 100;
        }, 0); return result.ToArray();
    }
    private static void Verify(PcWindow window)
    {
        if (!IsWindow((nint)window.Id)) throw new InvalidOperationException("La ventana seleccionada se cerró.");
        GetWindowThreadProcessId((nint)window.Id, out var pid);
        using var process = Process.GetProcessById((int)pid);
        if (pid != window.Pid || process.StartTime.ToUniversalTime().Ticks != window.Started) throw new InvalidOperationException("La ventana ya pertenece a otro proceso; vuelve a seleccionarla.");
    }
    private static IEnumerable<(AutomationElement Element, int Depth)> Walk(AutomationElement root)
    {
        var queue = new Queue<(AutomationElement Element, int Depth)>(); queue.Enqueue((root, 0)); var count = 0;
        while (queue.Count > 0 && count++ < 180)
        {
            var item = queue.Dequeue(); yield return item; if (item.Depth >= 8 || item.Element.Current.IsPassword) continue;
            var child = TreeWalker.ControlViewWalker.GetFirstChild(item.Element);
            while (child is not null && queue.Count + count < 180) { queue.Enqueue((child, item.Depth + 1)); child = TreeWalker.ControlViewWalker.GetNextSibling(child); }
        }
    }
    private static string NodeId(AutomationElement element) => AppPaths.HashText(string.Join("-", element.GetRuntimeId()))[..12].ToLowerInvariant();
    private static PcNode ToNode(AutomationElement element)
    {
        var current = element.Current; var value = "";
        if (!current.IsPassword && element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)) value = ((ValuePattern)pattern).Current.Value;
        var nativeClass = current.NativeWindowHandle != 0 ? NativeControls.Class((nint)current.NativeWindowHandle) : "";
        var id = NativeControls.Supported(nativeClass) ? "hw" + current.NativeWindowHandle.ToString("x") : NodeId(element);
        return new(id, current.IsPassword ? "[Contraseña protegida]" : current.Name[..Math.Min(current.Name.Length, 250)], current.ControlType.ProgrammaticName,
            value[..Math.Min(value.Length, 500)], !current.IsPassword && (element.TryGetCurrentPattern(InvokePattern.Pattern, out _) || element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _) || element.TryGetCurrentPattern(TogglePattern.Pattern, out _)),
            !current.IsPassword && element.TryGetCurrentPattern(ValuePattern.Pattern, out var editable) && !((ValuePattern)editable).Current.IsReadOnly);
    }
}

internal static class NativeControls
{
    private delegate bool EnumProc(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumProc callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int Style(nint window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")] private static extern nint ReadMessage(nint window, uint message, nint size, StringBuilder data, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")] private static extern nint TextMessage(nint window, uint message, nint size, string data, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] private static extern nint ClickMessage(nint window, uint message, nint wparam, nint lparam, uint flags, uint timeout, out nint result);
    internal static string Class(nint handle) { var text = new StringBuilder(180); GetClassName(handle, text, text.Capacity); return text.ToString(); }
    internal static bool Supported(string className) => className.StartsWith("WindowsForms10.EDIT.", StringComparison.Ordinal) || className.StartsWith("WindowsForms10.BUTTON.", StringComparison.Ordinal) || className.StartsWith("WindowsForms10.STATIC.", StringComparison.Ordinal);
    private static string Text(nint handle)
    { var text = new StringBuilder(501); if (ReadMessage(handle, 0x000d, text.Capacity, text, 2, 1800, out _) == 0) throw new IOException("El control nativo no respondió."); return text.ToString(); }
    internal static List<PcNode> Read(PcWindow window)
    {
        var nodes = new List<PcNode>();
        EnumChildWindows((nint)window.Id, (handle, _) =>
        {
            var className = Class(handle); if (!Supported(className) || !IsWindowVisible(handle) || !IsWindowEnabled(handle)) return true;
            var edit = className.StartsWith("WindowsForms10.EDIT."); if (edit && (Style(handle, -16) & 0x20) != 0) return true;
            var button = className.StartsWith("WindowsForms10.BUTTON.");
            var text = Text(handle); nodes.Add(new("hw" + handle.ToString("x"), edit ? "Campo de texto" : text, edit ? "ControlType.Edit" : button ? "ControlType.Button" : "ControlType.Text", edit ? text : "", button,
                edit && (Style(handle, -16) & 0x800) == 0, className)); return nodes.Count < 100;
        }, 0); return nodes;
    }
    internal static void Act(PcWindow window, PcRequest request)
    {
        if (!long.TryParse(request.Node.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var raw)) throw new ArgumentException("Identificador nativo inválido.");
        var handle = (nint)raw; var expected = request.Expected ?? throw new InvalidOperationException("Lee el control antes de actuar."); var className = Class(handle);
        if (GetAncestor(handle, 2) != (nint)window.Id || className != expected.NativeClass || !Supported(className)) throw new InvalidOperationException("El control nativo cambió o salió de la ventana autorizada.");
        if (!IsWindowVisible(handle) || !IsWindowEnabled(handle)) throw new InvalidOperationException("El control está oculto o desactivado.");
        if (request.Action == "type")
        {
            if (!expected.Editable || !className.StartsWith("WindowsForms10.EDIT.") || (Style(handle, -16) & (0x20 | 0x800)) != 0) throw new InvalidOperationException("Campo protegido o de solo lectura.");
            if (Text(handle) != expected.Value) throw new InvalidOperationException("El campo cambió desde la lectura; vuelve a inspeccionarlo.");
            if (TextMessage(handle, 0x000c, 0, request.Text, 2, 1800, out var result) == 0 || result == 0) throw new IOException("No se pudo escribir en el control nativo.");
        }
        else
        {
            if (!expected.Clickable || !className.StartsWith("WindowsForms10.BUTTON.") || Text(handle).Replace("&", "") != expected.Name.Replace("&", "")) throw new InvalidOperationException("El botón cambió desde la lectura.");
            if (ClickMessage(handle, 0x00f5, 0, 0, 2, 1800, out _) == 0) throw new IOException("El botón nativo no respondió.");
        }
    }
}
