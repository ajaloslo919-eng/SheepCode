using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SheepCode;

internal sealed record CodeIssue(string Code, string Message, int Line = 0, int Column = 0, string Severity = "error");
internal sealed class CodeValidation
{
    public string Path { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string Status { get; set; } = "passed";
    public string Parser { get; set; } = "";
    public List<string> Checks { get; set; } = [];
    public List<CodeIssue> Diagnostics { get; set; } = [];
    public string Scope { get; set; } = "Análisis estático sin ejecutar el código. No verifica dependencias, resultados ni toda la lógica.";
    internal bool CanPropose => Status is "passed" or "unsupported" or "disabled";
    internal string Summary => Status switch
    {
        "passed" => "🛡️ Sintaxis revisada" + (Checks.Count > 1 ? " y construcciones pedidas comprobadas" : "") +
            (Diagnostics.Count > 0 ? "; hay avisos para revisar" : "") + " · " + Parser + ". Sin ejecutar el programa.",
        "disabled" => "🛡️ Revisión automática desactivada por la persona. Código sin validar.",
        "unsupported" => "🛡️ Sintaxis sin validar: no hay analizador integrado para este formato.",
        "unavailable" => "🛡️ No se pudo validar: " + string.Join("; ", Diagnostics.Select(d => d.Message)),
        _ => "🛡️ Propuesta bloqueada: " + string.Join("; ", Diagnostics.Where(d => d.Severity == "error").Select(d => (d.Line > 0 ? "línea " + d.Line + ": " : "") + d.Message))
    };
}
internal sealed class CodeValidationException(CodeValidation report) : InvalidOperationException(report.Summary)
{
    internal CodeValidation Report { get; } = report;
}

// These requirements come exclusively from this turn's direct human request.
// Code, skills, history and tool output cannot alter them or enable execution.
internal sealed record CodeRequirements(string[] Loops, string[] ForbiddenLoops, bool Input, bool Print, bool Repeated, string[] Targets)
{
    internal static CodeRequirements FromHuman(string request)
    {
        var text = Regex.Replace(request, @"(?s)```.*?```", " ");
        var targets = Regex.Matches(text, @"[\w./\\-]+\.(?:py|pyi|cs|js|mjs|cjs|ts|tsx|jsx)\b", RegexOptions.IgnoreCase).Select(m => m.Value).Distinct().ToArray();
        text = string.Concat(text.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).ToLowerInvariant();
        var loops = new List<string>(); var forbidden = new List<string>();
        foreach (var name in new[] { "while", "for" })
        {
            var denied = Regex.IsMatch(text, @"\b(?:sin|evita|no\s+(?:uses?|utilices?|emplees?))\s+(?:un\s+|el\s+|bloque\s+|bucle\s+|ciclo\s+|loop\s+|`)*" + name + @"\b");
            if (denied) forbidden.Add(name);
            else if (Regex.IsMatch(text, @"\b(?:usa|use|utiliza|emplea|usando|utilizando|mediante|con|using)\b[^.!?\n]{0,65}\b" + name + @"\b")) loops.Add(name);
        }
        bool Call(string name) => Regex.IsMatch(text, @"\b(?:usa|use|utiliza|emplea|usando|utilizando|mediante|con|using)\b[^.!?\n]{0,65}\b" + name + @"\b");
        return new(loops.ToArray(), forbidden.ToArray(), Call("input"), Call("print"),
            Regex.IsMatch(text, @"\b(?:veces|repite|repetir|repeticiones|contador|counted)\b"), targets);
    }
    internal bool AppliesTo(string path) => Targets.Length == 0 || Targets.Any(t =>
        t.Replace('\\', '/').Equals(path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
        System.IO.Path.GetFileName(t).Equals(System.IO.Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
}

internal static class CodeValidator
{
    private static string WorkerDirectory => string.IsNullOrEmpty(typeof(CodeValidator).Assembly.Location) ? AppContext.BaseDirectory : System.IO.Path.GetDirectoryName(typeof(CodeValidator).Assembly.Location)!;
    internal static string? PythonExecutable
    {
        get
        {
            var bundled = System.IO.Path.Combine(AppPaths.Root, "runtime", "code-validation", "python", "python.exe");
            return File.Exists(bundled) ? bundled : AppPaths.ToolsPython;
        }
    }
    internal static object Status(bool enabled) => new
    {
        enabled, scope = "Todos los modelos y motores: write_file y edit_file se revisan antes del diff; JSON y SVG también por artifact_propose.",
        python = new { available = PythonExecutable is not null && File.Exists(System.IO.Path.Combine(WorkerDirectory, "validation", "python_check.py")), version = "AST y compilación del Python local; el setup incluye 3.12.10", isolated = true },
        parsers = new[] { "Python: AST + compile, sin ejecutar", "C#: Roslyn 4.14, sintaxis", "JavaScript: Esprima 3.0.6, sintaxis ECMAScript", "JSON: System.Text.Json", "XML/SVG: lector XML sin DTD" },
        limitations = "TypeScript, JSX y otros formatos quedan marcados sin validar. La lógica y los resultados requieren pruebas autorizadas después de Aplicar.",
        commands = new[] { "Activa la revisión de código", "Desactiva la revisión de código", "Estado de la revisión de código", "Comprueba la sintaxis de ARCHIVO" },
        permissions = "Solo entrada humana o dictado aceptado cambia el ajuste. El modelo puede consultar validation_status y validate_code(path,content); no habilita run_check."
    };
    internal static async Task<CodeValidation> CheckAsync(string path, string content, CodeRequirements requirements, bool enabled, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var report = new CodeValidation { Path = path, ContentHash = AppPaths.HashText(content) };
        if (!enabled) { report.Status = "disabled"; return report; }
        if (Encoding.UTF8.GetByteCount(content) > 256 * 1024) { Fail(report, "size", "El archivo propuesto supera 256 KiB."); return report; }
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var supported = extension is ".py" or ".pyi" or ".cs" or ".js" or ".mjs" or ".cjs" or ".json" or ".xml" or ".svg";
        if (!supported) { report.Status = "unsupported"; return report; }
        var first = content.Replace("\r\n", "\n").TrimStart().Split('\n')[0].Trim();
        if (first.StartsWith("```", StringComparison.Ordinal) || Regex.IsMatch(first, @"^\d+:\s") || first.Equals(System.IO.Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
        { Fail(report, "source_envelope", "Entrega código fuente sin cabecera de nombre de archivo, números de línea ni bloques Markdown.", 1); return report; }
        var facts = new Dictionary<string, int>();
        switch (extension)
        {
            case ".py": case ".pyi":
                await PythonAsync(report, content, facts, token); break;
            case ".cs":
                report.Parser = "Roslyn 4.14 · C#";
                var tree = CSharpSyntaxTree.ParseText(content, path: path, cancellationToken: token);
                foreach (var d in tree.GetDiagnostics(token).Where(d => d.Severity == DiagnosticSeverity.Error).Take(20))
                {
                    var at = d.Location.GetLineSpan().StartLinePosition;
                    Fail(report, d.Id, d.GetMessage(), at.Line + 1, at.Character + 1);
                }
                var nodes = tree.GetRoot(token).DescendantNodes().ToArray();
                facts["while"] = nodes.Count(n => n is WhileStatementSyntax);
                facts["for"] = nodes.Count(n => n is ForStatementSyntax or ForEachStatementSyntax or ForEachVariableStatementSyntax);
                break;
            case ".js": case ".mjs": case ".cjs":
                report.Parser = "Esprima 3.0.6 · JavaScript";
                try
                {
                    var parser = new Esprima.JavaScriptParser(new Esprima.ParserOptions { Tolerant = false, AllowReturnOutsideFunction = extension == ".cjs" });
                    Esprima.Ast.Node parsed;
                    if (extension == ".cjs") parsed = parser.ParseScript(content, path);
                    else
                    {
                        try { parsed = parser.ParseModule(content, path); }
                        catch (Esprima.ParserException) when (extension == ".js") { parsed = parser.ParseScript(content, path); }
                    }
                    var todo = new Stack<Esprima.Ast.Node>(); todo.Push(parsed);
                    while (todo.TryPop(out var n))
                    {
                        token.ThrowIfCancellationRequested();
                        if (n is Esprima.Ast.WhileStatement) facts["while"] = facts.GetValueOrDefault("while") + 1;
                        if (n is Esprima.Ast.ForStatement or Esprima.Ast.ForInStatement or Esprima.Ast.ForOfStatement) facts["for"] = facts.GetValueOrDefault("for") + 1;
                        foreach (var child in n.ChildNodes) todo.Push(child);
                    }
                }
                catch (Esprima.ParserException e) { Fail(report, "javascript_syntax", e.Description ?? e.Message, e.LineNumber, e.Column); }
                break;
            case ".json":
                report.Parser = "System.Text.Json";
                try { using var json = JsonDocument.Parse(content); }
                catch (JsonException e) { Fail(report, "json_syntax", e.Message, (int)(e.LineNumber ?? 0) + 1, (int)(e.BytePositionInLine ?? 0) + 1); }
                break;
            case ".xml": case ".svg":
                report.Parser = "System.Xml · DTD desactivado";
                try
                {
                    using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 256 * 1024 });
                    while (reader.Read()) token.ThrowIfCancellationRequested();
                }
                catch (XmlException e) { Fail(report, "xml_syntax", e.Message, e.LineNumber, e.LinePosition); }
                break;
        }
        if (report.Status != "passed") return report;
        report.Checks.Add("Sintaxis del archivo completo");
        if (!requirements.AppliesTo(path) || extension is not (".py" or ".pyi" or ".cs" or ".js" or ".mjs" or ".cjs")) return report;
        foreach (var loop in requirements.Loops)
        {
            if (facts.GetValueOrDefault(loop) == 0) Fail(report, "required_" + loop, "La petición humana exige un bloque " + loop + " real. Los comentarios y las cadenas no cuentan; no lo sustituyas por otro bucle.");
            else report.Checks.Add("Bloque " + loop + " solicitado presente en AST");
        }
        foreach (var loop in requirements.ForbiddenLoops)
        {
            if (facts.GetValueOrDefault(loop) > 0) Fail(report, "forbidden_" + loop, "La petición humana prohíbe usar " + loop + ".");
            else report.Checks.Add("Sin bloques " + loop + " prohibidos");
        }
        if (extension is ".py" or ".pyi")
        {
            foreach (var call in new[] { ("input", requirements.Input), ("print", requirements.Print) }.Where(c => c.Item2))
            {
                if (facts.GetValueOrDefault(call.Item1) == 0) Fail(report, "required_" + call.Item1, "Falta la llamada " + call.Item1 + " solicitada por la persona.");
                else report.Checks.Add("Llamada " + call.Item1 + " solicitada presente en AST");
                if (requirements.Repeated && requirements.Loops.Length == 1 && facts.GetValueOrDefault(requirements.Loops[0] + "_" + call.Item1) == 0)
                    Fail(report, "loop_" + call.Item1, "El bloque " + requirements.Loops[0] + " debe repetir " + call.Item1 + "; no basta una llamada fuera del bucle.");
                if (call.Item1 == "print" && requirements.Repeated && requirements.Loops.Length == 1 &&
                    facts.GetValueOrDefault("print") > facts.GetValueOrDefault(requirements.Loops[0] + "_print"))
                    report.Diagnostics.Add(new("additional_output", "Hay llamadas print fuera del bucle solicitado. Revisa si esa salida adicional está permitida; comprobar la sintaxis no garantiza el número de salidas.", Severity: "warning"));
            }
            if (requirements.Repeated && requirements.Loops.Contains("while"))
                foreach (var d in report.Diagnostics.Where(d => d.Code is "unchanged_counter" or "duplicate_program").ToArray())
                    Fail(report, d.Code, d.Message, d.Line, d.Column);
        }
        return report;
    }
    private static void Fail(CodeValidation report, string code, string message, int line = 0, int column = 0)
    { report.Status = "failed"; report.Diagnostics.Add(new(code, message, line, column)); }
    private static async Task PythonAsync(CodeValidation report, string content, Dictionary<string, int> facts, CancellationToken token)
    {
        var worker = System.IO.Path.Combine(WorkerDirectory, "validation", "python_check.py");
        var executable = PythonExecutable;
        if (executable is null || !File.Exists(worker))
        { report.Status = "unavailable"; report.Diagnostics.Add(new("python_unavailable", "Falta el analizador Python local. Reinstala el paquete de SheepCode; no se ha validado ni preparado la propuesta.")); return; }
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = WorkerDirectory, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in new[] { "-I", "-S", "-B", worker }) start.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var process = new Process { StartInfo = start };
        try
        {
            token.ThrowIfCancellationRequested(); process.Start();
            var output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var error = ReadBoundedAsync(process.StandardError, timeout.Token);
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { path = report.Path, content }).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await output; var stderr = await error;
            if (process.ExitCode != 0) throw new IOException("El analizador Python terminó con " + process.ExitCode + ": " + stderr);
            using var data = JsonDocument.Parse(stdout); var root = data.RootElement;
            report.Parser = root.GetProperty("parser").GetString()!;
            if (!root.GetProperty("syntax").GetBoolean()) report.Status = "failed";
            foreach (var fact in root.GetProperty("facts").EnumerateObject()) facts[fact.Name] = fact.Value.GetInt32();
            foreach (var d in root.GetProperty("diagnostics").EnumerateArray().Take(20))
                report.Diagnostics.Add(new(d.GetProperty("code").GetString()!, d.GetProperty("message").GetString()!, d.GetProperty("line").GetInt32(), d.GetProperty("column").GetInt32(), d.GetProperty("severity").GetString()!));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { report.Status = "unavailable"; report.Diagnostics.Add(new("validation_timeout", "La revisión Python superó 20 segundos; no se afirma que el código sea válido.")); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException)
        { report.Status = "unavailable"; report.Diagnostics.Add(new("parser_error", e.Message)); }
        finally { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
        token.ThrowIfCancellationRequested();
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[2048]; var result = new StringBuilder(); int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        { if (result.Length + count > 32768) throw new IOException("La salida del analizador supera su límite."); result.Append(buffer, 0, count); }
        return result.ToString();
    }
}
