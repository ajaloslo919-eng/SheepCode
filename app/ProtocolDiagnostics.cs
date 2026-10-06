using System.Net;
using System.Text;
using System.Text.Json;

namespace SheepCode;

internal static class ProtocolDiagnostics
{
    private sealed class Gateway(bool alwaysFail = false) : HttpMessageHandler
    {
        internal List<int> Budgets { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath != "/v1/chat/completions") return new(HttpStatusCode.NotFound);
            using var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Budgets.Add(data.RootElement.GetProperty("max_tokens").GetInt32());
            if (Budgets.Count == 1 || alwaysFail) return new(HttpStatusCode.BadGateway)
            { Content = new StringContent("{\"error\":{\"code\":\"structured_output_failed\",\"message\":\"structured output was incomplete (finish_reason=length)\"}}", Encoding.UTF8, "application/json") };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            { choices = new[] { new { message = new { content = "{\"action\":\"write_file\",\"path\":\"hello.py\",\"content\":\"print('Hola')\\n\",\"message\":\"Propuesta\"}" }, finish_reason = "stop" } }, usage = new { completion_tokens = 32 } })) };
        }
    }
    internal static async Task<int> RunAsync()
    {
        var path = Path.Combine(AppPaths.Root, "checks", "protocol-regression.json");
        try
        {
            using var gateway = new Gateway(); using var http = new HttpClient(gateway); await using var voice = new NeuralVoice(); await using var engine = new EngineHost(voice, http);
            var response = await engine.CompleteTransportAsync([new("user", "Preparar un archivo pequeño")], "high", CancellationToken.None);
            using var valid = JsonDocument.Parse(response);
            if (gateway.Budgets.Count != 2 || gateway.Budgets[1] <= gateway.Budgets[0] || ModelProtocol.ValidateAction(valid.RootElement) is not null) throw new IOException("No se recuperó el HTTP 502 incompleto.");
            using var incomplete = JsonDocument.Parse("{\"action\":\"write_file\",\"path\":\"hello.py\",\"message\":\"Propuesta\"}");
            if (ModelProtocol.ValidateAction(incomplete.RootElement) is not { } error || !error.Contains("content")) throw new IOException("No se detectó content ausente.");
            using var nested = JsonDocument.Parse("{\"action\":\"write_file\",\"arguments\":{\"path\":\"hello.py\",\"content\":\"hola\"}}");
            if (ModelProtocol.ValidateAction(nested.RootElement) is null) throw new IOException("Se aceptaron campos en el nivel incorrecto.");
            using var never = new Gateway(true); using var neverHttp = new HttpClient(never); await using var bounded = new EngineHost(voice, neverHttp);
            try { await bounded.CompleteTransportAsync([new("user", "Prueba")], "none", CancellationToken.None); throw new IOException("Se aceptó una salida incompleta."); }
            catch (HttpRequestException) { }
            if (never.Budgets.Count != 3 || ModelProtocol.StructuredFailure("{\"error\":{\"code\":\"out_of_memory\"}}")) throw new IOException("Reintento no acotado o error mal clasificado.");
            AppPaths.SaveJson(path, new { status = "complete", scope = "Regresión determinista de protocolo con respuestas HTTP simuladas; sin ejecutar ni medir una IA.", checks = new[] { "HTTP 502 incomplete → mayor presupuesto → acción completa", "content obligatorio y cadena al nivel superior", "fallos persistentes limitados a tres intentos", "otros errores conservan su identidad" }, budgets = gateway.Budgets, persistentBudgets = never.Budgets }); return 0;
        }
        catch (Exception e) { AppPaths.SaveJson(path, new { status = "failed", error = e.ToString() }); return 1; }
    }
}
