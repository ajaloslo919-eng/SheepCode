using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml;

namespace SheepCode;

internal static class ArtifactTools
{
    static ArtifactTools() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    internal static readonly string[] Formats = ["docx", "xlsx", "pptx", "pdf", "csv", "html", "svg", "md", "json"];
    private static string Escape(string text) => System.Security.SecurityElement.Escape(text) ?? "";
    private static XmlReaderSettings XmlSettings => new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 };
    private const string Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string Office = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static string Relationships(params (string Id, string Type, string Target)[] links) =>
        $"<Relationships xmlns=\"{Rel}\">" + string.Join("", links.Select(l => $"<Relationship Id=\"{l.Id}\" Type=\"{Office}/{l.Type}\" Target=\"{Escape(l.Target)}\"/>")) + "</Relationships>";
    private static byte[] Zip(Dictionary<string, string> files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var pair in files) { using var writer = new StreamWriter(zip.CreateEntry(pair.Key).Open(), new UTF8Encoding(false)); writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + pair.Value); }
        return stream.ToArray();
    }
    private static string ContentTypes(IEnumerable<(string Path, string Type)> items) =>
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        string.Join("", items.Select(i => $"<Override PartName=\"/{i.Path}\" ContentType=\"{i.Type}\"/>")) + "</Types>";
    internal static byte[] Create(string format, string content)
    {
        format = format.ToLowerInvariant().TrimStart('.');
        if (!Formats.Contains(format) || Encoding.UTF8.GetByteCount(content) > 128 * 1024) throw new ArgumentException("Formato no disponible o contenido demasiado grande.");
        if (format is "md" or "json" or "html" or "svg" or "csv")
        {
            if (format == "json") { using var valid = JsonDocument.Parse(content); }
            if (format == "svg")
            {
                using var source = new StringReader(content); using var reader = XmlReader.Create(source, XmlSettings); var document = XDocument.Load(reader);
                if (document.Root?.Name.LocalName != "svg" || document.Descendants().Any(e => e.Name.LocalName is "script" or "foreignObject") ||
                    document.Descendants().Attributes().Any(a => a.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase) || a.Name.LocalName == "href" && !a.Value.StartsWith('#')))
                    throw new ArgumentException("SVG requiere dibujo local, sin scripts, eventos ni recursos externos.");
            }
            return Encoding.UTF8.GetBytes(content);
        }
        using var json = JsonDocument.Parse(content); var root = json.RootElement;
        var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "Sheep & Kuky" : "Sheep & Kuky";
        if (format == "xlsx") return Spreadsheet(root);
        if (format == "pptx") return Presentation(root, title);
        var paragraphs = root.TryGetProperty("paragraphs", out var p) ? p.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
        if (paragraphs.Length > 600) throw new ArgumentException("Máximo 600 párrafos.");
        return format == "pdf" ? Pdf(title, paragraphs) : Document(title, paragraphs);
    }
    private static byte[] Document(string title, string[] paragraphs)
    {
        string Paragraph(string text, bool heading = false) => "<w:p><w:pPr><w:spacing w:after=\"180\"/></w:pPr><w:r><w:rPr>" + (heading ? "<w:b/><w:color w:val=\"80578E\"/><w:sz w:val=\"36\"/>" : "<w:sz w:val=\"22\"/>") + "</w:rPr><w:t xml:space=\"preserve\">" + Escape(text) + "</w:t></w:r></w:p>";
        return Zip(new() {
            ["[Content_Types].xml"] = ContentTypes([("word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml")]),
            ["_rels/.rels"] = Relationships(("rId1", "officeDocument", "word/document.xml")),
            ["word/document.xml"] = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" + Paragraph(title, true) + string.Join("", paragraphs.Select(x => Paragraph(x))) +
                "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1134\"/></w:sectPr></w:body></w:document>"
        });
    }
    private static byte[] Spreadsheet(JsonElement root)
    {
        var rows = root.GetProperty("rows").EnumerateArray().ToArray();
        if (rows.Length > 2000 || rows.Any(r => r.ValueKind != JsonValueKind.Array || r.GetArrayLength() > 100)) throw new ArgumentException("Máximo 2000 filas y 100 columnas.");
        var sheetName = root.TryGetProperty("sheet", out var s) ? s.GetString() ?? "Sheep" : "Sheep";
        sheetName = Regex.Replace(sheetName, "[\\\\/*?:\\[\\]]", "_"); sheetName = sheetName[..Math.Min(31, sheetName.Length)]; if (sheetName.Length == 0) sheetName = "Sheep";
        string Column(int n) { var text = ""; do { text = (char)('A' + n % 26) + text; n = n / 26 - 1; } while (n >= 0); return text; }
        var xml = new StringBuilder();
        for (var i = 0; i < rows.Length; i++)
        {
            xml.Append($"<row r=\"{i + 1}\">"); var j = 0;
            foreach (var value in rows[i].EnumerateArray())
            {
                var id = Column(j++) + (i + 1); var style = i == 0 ? " s=\"1\"" : "";
                if (value.ValueKind == JsonValueKind.Number) xml.Append($"<c r=\"{id}\"{style}><v>{value.GetRawText()}</v></c>");
                else if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) xml.Append($"<c r=\"{id}\" t=\"b\"{style}><v>{(value.GetBoolean() ? 1 : 0)}</v></c>");
                else xml.Append($"<c r=\"{id}\" t=\"inlineStr\"{style}><is><t xml:space=\"preserve\">{Escape(value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "")}</t></is></c>");
            }
            xml.Append("</row>");
        }
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return Zip(new() {
            ["[Content_Types].xml"] = ContentTypes([("xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"), ("xl/worksheets/sheet1.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"), ("xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml")]),
            ["_rels/.rels"] = Relationships(("rId1", "officeDocument", "xl/workbook.xml")),
            ["xl/_rels/workbook.xml.rels"] = Relationships(("rId1", "worksheet", "worksheets/sheet1.xml"), ("rId2", "styles", "styles.xml")),
            ["xl/workbook.xml"] = $"<workbook xmlns=\"{ns}\" xmlns:r=\"{Office}\"><sheets><sheet name=\"{Escape(sheetName)}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>",
            ["xl/worksheets/sheet1.xml"] = $"<worksheet xmlns=\"{ns}\"><sheetViews><sheetView workbookViewId=\"0\"/></sheetViews><cols><col min=\"1\" max=\"100\" width=\"22\" customWidth=\"1\"/></cols><sheetData>{xml}</sheetData></worksheet>",
            ["xl/styles.xml"] = $"<styleSheet xmlns=\"{ns}\"><fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><color rgb=\"FF80578E\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>"
        });
    }
    private static byte[] Presentation(JsonElement root, string title)
    {
        var slides = root.GetProperty("slides").EnumerateArray().ToArray(); if (slides.Length is < 1 or > 40) throw new ArgumentException("Entre 1 y 40 diapositivas.");
        const string ns = "http://schemas.openxmlformats.org/presentationml/2006/main", drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
        const string group = "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>";
        string Shape(int id, string name, string[] lines, int y, int height, int size, string color) => $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr txBox=\"1\"/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"685800\" y=\"{y}\"/><a:ext cx=\"10820400\" cy=\"{height}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/></p:spPr><p:txBody><a:bodyPr wrap=\"square\"/><a:lstStyle/>" +
            string.Join("", lines.Select(line => $"<a:p><a:pPr><a:spcAft><a:spcPts val=\"1600\"/></a:spcAft></a:pPr><a:r><a:rPr lang=\"es-ES\" sz=\"{size}\"><a:solidFill><a:srgbClr val=\"{color}\"/></a:solidFill><a:latin typeface=\"Segoe UI\"/></a:rPr><a:t>{Escape(line)}</a:t></a:r><a:endParaRPr lang=\"es-ES\"/></a:p>")) + "</p:txBody></p:sp>";
        var types = new List<(string, string)> { ("ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"), ("ppt/slideMasters/slideMaster1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml"), ("ppt/slideLayouts/slideLayout1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml") };
        var files = new Dictionary<string, string> {
            ["_rels/.rels"] = Relationships(("rId1", "officeDocument", "ppt/presentation.xml")),
            ["ppt/presentation.xml"] = $"<p:presentation xmlns:p=\"{ns}\" xmlns:r=\"{Office}\"><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst><p:sldIdLst>" + string.Join("", slides.Select((_, i) => $"<p:sldId id=\"{256 + i}\" r:id=\"rId{2 + i}\"/>")) + "</p:sldIdLst><p:sldSz cx=\"12192000\" cy=\"6858000\" type=\"screen16x9\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>",
            ["ppt/slideMasters/slideMaster1.xml"] = $"<p:sldMaster xmlns:p=\"{ns}\" xmlns:a=\"{drawing}\" xmlns:r=\"{Office}\"><p:cSld><p:spTree>{group}</p:spTree></p:cSld><p:clrMap accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" bg1=\"lt1\" bg2=\"lt2\" folHlink=\"folHlink\" hlink=\"hlink\" tx1=\"dk1\" tx2=\"dk2\"/><p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst></p:sldMaster>",
            ["ppt/slideMasters/_rels/slideMaster1.xml.rels"] = Relationships(("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml")),
            ["ppt/slideLayouts/slideLayout1.xml"] = $"<p:sldLayout xmlns:p=\"{ns}\" xmlns:a=\"{drawing}\" type=\"blank\"><p:cSld name=\"SheepCode\"><p:spTree>{group}</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>",
            ["ppt/slideLayouts/_rels/slideLayout1.xml.rels"] = Relationships(("rId1", "slideMaster", "../slideMasters/slideMaster1.xml"))
        };
        var links = new List<(string, string, string)> { ("rId1", "slideMaster", "slideMasters/slideMaster1.xml") };
        for (var i = 0; i < slides.Length; i++)
        {
            var heading = slides[i].GetProperty("title").GetString() ?? title;
            var lines = slides[i].GetProperty("bullets").EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            if (lines.Length > 8 || lines.Any(l => l.Length > 180) || heading.Length > 100) throw new ArgumentException("Una diapositiva admite título de 100 caracteres y hasta 8 puntos de 180 caracteres.");
            files[$"ppt/slides/slide{i + 1}.xml"] = $"<p:sld xmlns:p=\"{ns}\" xmlns:a=\"{drawing}\"><p:cSld><p:bg><p:bgPr><a:solidFill><a:srgbClr val=\"FFF7FB\"/></a:solidFill><a:effectLst/></p:bgPr></p:bg><p:spTree>{group}" + Shape(2, "Título", [heading], 500000, 900000, 3200, "80578E") + Shape(3, "Contenido", lines, 1650000, 4600000, 2200, "382C47") + "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>";
            files[$"ppt/slides/_rels/slide{i + 1}.xml.rels"] = Relationships(("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"));
            links.Add(($"rId{i + 2}", "slide", $"slides/slide{i + 1}.xml")); types.Add(($"ppt/slides/slide{i + 1}.xml", "application/vnd.openxmlformats-officedocument.presentationml.slide+xml"));
        }
        files["ppt/_rels/presentation.xml.rels"] = Relationships(links.ToArray()); files["[Content_Types].xml"] = ContentTypes(types); return Zip(files);
    }
    private static byte[] Pdf(string title, string[] paragraphs)
    {
        // Standard PDF WinAnsi text. Unicode Office/SVG output remains available for emoji and non-Latin scripts.
        var encoding = Encoding.GetEncoding(1252, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        string Literal(string value) => value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)").Replace("\r", "").Replace("\n", " ");
        var lines = new List<string>();
        foreach (var paragraph in new[] { title, "" }.Concat(paragraphs))
        { var words = paragraph.Split(' '); var line = ""; foreach (var word in words) { if (line.Length + word.Length > 88 && line.Length > 0) { lines.Add(line); line = ""; } line += (line.Length > 0 ? " " : "") + word; } lines.Add(line); lines.Add(""); }
        var pages = lines.Chunk(45).ToArray(); var objects = new List<byte[]>();
        void Add(string text) => objects.Add(encoding.GetBytes(text));
        Add("<< /Type /Catalog /Pages 2 0 R >>"); Add("<< /Type /Pages /Count " + pages.Length + " /Kids [" + string.Join(" ", pages.Select((_, i) => (4 + i * 2) + " 0 R")) + "] >>"); Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        foreach (var page in pages)
        {
            var next = objects.Count + 1;
            Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {next + 1} 0 R >>");
            var commands = "BT /F1 11 Tf 15 TL 50 790 Td\n" + string.Join("\n", page.Select(line => "(" + Literal(line) + ") Tj T*")) + "\nET";
            Add("<< /Length " + encoding.GetByteCount(commands) + " >>\nstream\n" + commands + "\nendstream");
        }
        using var output = new MemoryStream(); void Write(string text) => output.Write(encoding.GetBytes(text));
        Write("%PDF-1.4\n"); var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++) { offsets.Add(output.Position); Write((i + 1) + " 0 obj\n"); output.Write(objects[i]); Write("\nendobj\n"); }
        var xref = output.Position; Write("xref\n0 " + (objects.Count + 1) + "\n0000000000 65535 f \n"); foreach (var offset in offsets) Write(offset.ToString("D10") + " 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); return output.ToArray();
    }
    internal static string Read(ProjectWorkspace workspace, string path)
    {
        var full = workspace.Resolve(path); var bytes = workspace.ReadBytes(path); var ext = Path.GetExtension(path).ToLowerInvariant(); var text = ""; var notice = "";
        if (ext is ".docx" or ".xlsx" or ".pptx")
        {
            using var stream = new MemoryStream(bytes); using var zip = new ZipArchive(stream); var extracted = new List<string>(); var budget = 0L;
            foreach (var entry in zip.Entries.Where(e => e.FullName is "word/document.xml" or "xl/sharedStrings.xml" || Regex.IsMatch(e.FullName, @"^(xl/worksheets/sheet\d+|ppt/slides/slide\d+)\.xml$")))
            {
                budget += entry.Length; if (budget > 4 * 1024 * 1024) throw new IOException("Documento demasiado grande para la lectura acotada.");
                using var input = entry.Open(); using var reader = XmlReader.Create(input, XmlSettings); var xml = XDocument.Load(reader);
                if (ext == ".xlsx" && entry.FullName.StartsWith("xl/worksheets"))
                    extracted.Add(entry.FullName + ":\n" + string.Join("\n", xml.Descendants().Where(e => e.Name.LocalName == "row").Take(100).Select(row => string.Join(" | ", row.Elements().Select(cell => cell.Attribute("r")?.Value + "=" + string.Concat(cell.Descendants().Where(v => v.Name.LocalName is "t" or "v" or "f").Select(v => v.Value)))))));
                else extracted.Add(entry.FullName + ":\n" + string.Join("\n", xml.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value)));
            }
            text = string.Join("\n\n", extracted); notice = "Lectura de contenido; conserva el archivo original para mantener su formato. XLSX informa referencias y valores almacenados, no recalcula fórmulas.";
        }
        else if (ext == ".pdf")
        {
            var raw = Encoding.GetEncoding(1252).GetString(bytes); text = string.Join("\n", Regex.Matches(raw, @"\(((?:\\.|[^\\)])*)\)\s*Tj").Select(m => m.Groups[1].Value.Replace("\\(", "(").Replace("\\)", ")").Replace("\\\\", "\\")));
            notice = text.Length == 0 ? "Este PDF necesita un lector avanzado u OCR conectado por MCP. No se obtuvo su texto." : "Extracción básica de PDF con texto Tj; OCR, fuentes codificadas y PDF comprimidos requieren un proveedor avanzado.";
        }
        else text = workspace.Read(path).Text;
        return JsonSerializer.Serialize(new { path, format = ext.TrimStart('.'), text = text[..Math.Min(7000, text.Length)], truncated = text.Length > 7000, sha256 = AppPaths.Hash(bytes), notice });
    }
}
