using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SheepCode;

internal sealed record BrowserNode(string Id, string Tag, string Type, string Text, string Value, string Href);
internal sealed record BrowserView(string Tab, string Url, string Title, string Text, BrowserNode[] Nodes, int TextStart = 0, int TotalTextCharacters = 0,
    int NodesStart = 0, int TotalNodes = 0, bool Truncated = false);
internal sealed class BrowserTools(Control owner, TabControl host, SkillRegistry skills) : IDisposable
{
    private sealed class Page(string id, TabPage tab, WebView2 web)
    {
        internal string Id = id;
        internal TabPage Tab = tab;
        internal WebView2 Web = web;
        internal BrowserView? Observation;
        internal bool Loading;
    }
    private readonly List<Page> _pages = [];
    private CoreWebView2Environment? _environment;
    internal event Action? Changed;
    internal string? Selected => _pages.FirstOrDefault(p => p.Tab == host.SelectedTab)?.Id;
    internal Task<string> TabsAsync(CancellationToken token) => Ui(() =>
    {
        skills.Require("browser"); token.ThrowIfCancellationRequested();
        return Task.FromResult(JsonSerializer.Serialize(_pages.Select(p => new { tab = p.Id, url = p.Web.Source?.AbsoluteUri, title = p.Tab.Text, loading = p.Loading })));
    });
    internal Task<string> OpenAsync(string url, CancellationToken token) => Ui(async () =>
    {
        skills.Require("browser"); ValidateUrl(url); token.ThrowIfCancellationRequested();
        if (_pages.Count >= 12) throw new InvalidOperationException("Máximo 12 pestañas; cierra una antes de abrir otra.");
        _environment ??= await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.Root, "browser"));
        var tab = new TabPage("🌸 Nueva pestaña") { BackColor = Color.FromArgb(31, 26, 43) };
        var web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(31, 26, 43) };
        var page = new Page(Guid.NewGuid().ToString("N")[..8], tab, web);
        _pages.Add(page); tab.Controls.Add(web); host.TabPages.Add(tab); host.SelectedTab = tab;
        try
        {
            await web.EnsureCoreWebView2Async(_environment);
            web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            web.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            web.CoreWebView2.DownloadStarting += (_, e) => e.Cancel = true;
            web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                // Popup URLs are shown as data; the human or agent can explicitly open a new owned tab.
                e.Handled = true; Changed?.Invoke();
            };
            web.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!ValidUrl(e.Uri)) { e.Cancel = true; return; }
                page.Observation = null; page.Loading = true; Changed?.Invoke();
            };
            web.CoreWebView2.NavigationCompleted += (_, _) => { page.Loading = false; Changed?.Invoke(); };
            web.CoreWebView2.DocumentTitleChanged += (_, _) =>
            { var title = web.CoreWebView2.DocumentTitle; tab.Text = title.Length > 24 ? title[..24] + "…" : title; Changed?.Invoke(); };
            await Navigate(page, () => web.CoreWebView2.Navigate(url), token);
            return JsonSerializer.Serialize(new { tab = page.Id, url = web.Source?.AbsoluteUri, title = tab.Text });
        }
        catch { _pages.Remove(page); host.TabPages.Remove(tab); web.Dispose(); tab.Dispose(); throw; }
    });
    internal Task<BrowserView> ReadAsync(string id, CancellationToken token, int start = 0, int textLength = 2000, int nodesStart = 0, int nodeCount = 20) => Ui(async () =>
    {
        skills.Require("browser"); var page = Get(id); host.SelectedTab = page.Tab; token.ThrowIfCancellationRequested();
        if (page.Loading) throw new InvalidOperationException("La pestaña sigue navegando; vuelve a leer cuando termine.");
        var raw = await page.Web.ExecuteScriptAsync(ReadScript).WaitAsync(token);
        using var doc = JsonDocument.Parse(raw); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new IOException("Esta página no permite leer el documento.");
        var nodes = JsonSerializer.Deserialize<BrowserNode[]>(root.GetProperty("nodes").GetRawText(), AppPaths.Json) ?? [];
        page.Observation = new(id, root.GetProperty("url").GetString() ?? "", root.GetProperty("title").GetString() ?? "", root.GetProperty("text").GetString() ?? "", nodes);
        return Fragment(page.Observation, start, textLength, nodesStart, nodeCount);
    });
    internal static BrowserView Fragment(BrowserView full, int start, int textLength, int nodesStart, int nodeCount)
    {
        start = Math.Clamp(start, 0, full.Text.Length); textLength = Math.Clamp(textLength, 128, 6500);
        nodesStart = Math.Clamp(nodesStart, 0, full.Nodes.Length); nodeCount = Math.Clamp(nodeCount, 1, 40);
        var text = full.Text.Substring(start, Math.Min(textLength, full.Text.Length - start)); var nodes = full.Nodes.Skip(nodesStart).Take(nodeCount).ToArray();
        return full with { Text = text, Nodes = nodes, TextStart = start, TotalTextCharacters = full.Text.Length, NodesStart = nodesStart, TotalNodes = full.Nodes.Length,
            Truncated = start != 0 || text.Length != full.Text.Length || nodesStart != 0 || nodes.Length != full.Nodes.Length };
    }
    internal Task<BrowserView> ActAsync(string action, string id, string node, string text, string humanRequest, CancellationToken token) => Ui(async () =>
    {
        skills.Require("browser"); var page = Get(id); token.ThrowIfCancellationRequested();
        var observation = page.Observation ?? throw new InvalidOperationException("Lee la pestaña antes de actuar.");
        var item = observation.Nodes.FirstOrDefault(n => n.Id == node) ?? throw new InvalidOperationException("El identificador no aparece en la última lectura.");
        if (page.Loading) throw new InvalidOperationException("El documento está cambiando; vuelve a leerlo.");
        if (action == "click") ActionIntent.Check(item.Text + (item.Type == "submit" ? " submit" : ""), humanRequest);
        if (text.Length > 4000) throw new ArgumentException("El campo admite hasta 4000 caracteres por acción.");
        // Only this fixed script runs. Model input is serialized as data, never interpolated as JavaScript code.
        var args = JsonSerializer.Serialize(new { action, node, text, url = observation.Url, tag = item.Tag, type = item.Type, label = item.Text, href = item.Href });
        var script = "((arg) => { if(location.href!==arg.url) return {error:'La página cambió; vuelve a leerla.'}; " +
            "const e=document.querySelector('[data-sheepcode-node=\"'+arg.node+'\"]'); if(!e||!e.isConnected||e.tagName.toLowerCase()!==arg.tag||(e.getAttribute('type')||'').toLowerCase()!==arg.type) return {error:'El control cambió; vuelve a leer.'}; " +
            "const label=(e.innerText||e.getAttribute('aria-label')||e.placeholder||e.value||e.name||'').slice(0,180); if(label!==arg.label||(e.href||'')!==arg.href) return {error:'La identidad del control cambió; vuelve a leer.'}; " +
            "if(e.disabled||!e.getClientRects().length) return {error:'Control no disponible.'}; " +
            "if(arg.action==='fill'){ if(!['input','textarea'].includes(arg.tag)||!['','text','search','email','url','tel','number'].includes(arg.type)||e.readOnly||e.autocomplete==='current-password'||e.autocomplete==='new-password') return {error:'Campo no editable o protegido.'}; " +
            "const proto=arg.tag==='input'?HTMLInputElement.prototype:HTMLTextAreaElement.prototype; Object.getOwnPropertyDescriptor(proto,'value').set.call(e,arg.text); e.dispatchEvent(new Event('input',{bubbles:true})); e.dispatchEvent(new Event('change',{bubbles:true})); } " +
            "else if(arg.action==='click'){ if(e.type==='password') return {error:'Campo protegido.'}; e.click(); } else return {error:'Acción desconocida.'}; return {ok:true};})(" + args + ")";
        var raw = await page.Web.ExecuteScriptAsync(script).WaitAsync(token);
        using (var response = JsonDocument.Parse(raw))
        { if (response.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("El documento navegó; vuelve a leer la pestaña para verificar la acción."); if (response.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString()); }
        page.Observation = null;
        await WaitLoaded(page, token);
        return await ReadAsync(id, token);
    });
    internal Task<string> BackAsync(string id, CancellationToken token) => Ui(async () =>
    {
        skills.Require("browser"); var page = Get(id); if (!page.Web.CoreWebView2.CanGoBack) throw new InvalidOperationException("No hay una página anterior.");
        await Navigate(page, () => page.Web.CoreWebView2.GoBack(), token); return JsonSerializer.Serialize(await ReadAsync(id, token));
    });
    internal Task CloseAsync(string id, CancellationToken token) => Ui(() =>
    {
        skills.Require("browser"); token.ThrowIfCancellationRequested(); var page = Get(id); _pages.Remove(page); host.TabPages.Remove(page.Tab); page.Web.Dispose(); page.Tab.Dispose(); Changed?.Invoke(); return Task.CompletedTask;
    });
    private Page Get(string id) => _pages.FirstOrDefault(p => p.Id == id) ?? throw new ArgumentException("No existe la pestaña " + id);
    private static bool ValidUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo);
    internal static void ValidateUrl(string url) { if (!ValidUrl(url)) throw new ArgumentException("Usa una URL HTTP o HTTPS sin credenciales incrustadas."); }
    private static async Task Navigate(Page page, Action navigate, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Done(object? sender, CoreWebView2NavigationCompletedEventArgs e) { if (e.IsSuccess) completion.TrySetResult(); else completion.TrySetException(new IOException("Navegación falló: " + e.WebErrorStatus)); }
        page.Web.CoreWebView2.NavigationCompleted += Done;
        try { navigate(); await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), token); }
        catch { page.Web.CoreWebView2.Stop(); throw; }
        finally { page.Web.CoreWebView2.NavigationCompleted -= Done; }
    }
    private static async Task WaitLoaded(Page page, CancellationToken token)
    {
        // Give scheduled browser navigation a turn, then wait for the actual NavigationCompleted event state.
        await Task.Delay(180, token); var deadline = DateTime.UtcNow.AddSeconds(30);
        try { while (page.Loading) { if (DateTime.UtcNow > deadline) throw new IOException("La navegación no terminó en 30 segundos."); await Task.Delay(100, token); } }
        catch { page.Web.CoreWebView2.Stop(); throw; }
    }
    private Task<T> Ui<T>(Func<Task<T>> action)
    {
        if (!owner.InvokeRequired) return action();
        var promise = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.BeginInvoke(async () => { try { promise.TrySetResult(await action()); } catch (Exception e) { promise.TrySetException(e); } }); return promise.Task;
    }
    private async Task Ui(Func<Task> action) => await Ui(async () => { await action(); return true; });
    public void Dispose() { foreach (var page in _pages) { page.Web.Dispose(); page.Tab.Dispose(); } _pages.Clear(); }
    private const string ReadScript = """
    (() => {
      const visible=e=>!!e.getClientRects().length && getComputedStyle(e).visibility!=='hidden';
      const nodes=[]; let i=0;
      for(const e of document.querySelectorAll('a[href],button,input,textarea,select,[role=button]')) {
        if(!visible(e)||e.disabled||e.type==='password'||['current-password','new-password'].includes(e.autocomplete)) continue;
        if(nodes.length>=100) break;
        const id='n'+(++i); e.setAttribute('data-sheepcode-node',id);
        nodes.push({id,tag:e.tagName.toLowerCase(),type:(e.getAttribute('type')||'').toLowerCase(),text:(e.innerText||e.getAttribute('aria-label')||e.placeholder||e.value||e.name||'').slice(0,180),value:('value' in e?e.value:'').slice(0,300),href:e.href||''});
      }
      return {url:location.href,title:document.title,text:(document.body?.innerText||'').slice(0,64000),nodes};
    })()
    """;
}
