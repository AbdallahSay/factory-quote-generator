using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FactoryQuoteApi.DTOs;

namespace FactoryQuoteApi.Services;

public interface IWordQuoteGeneratorService
{
    Task<string> GenerateQuoteDocumentAsync(string templatePath, string outputPath, QuoteRequestDto request, string quoteNumber, decimal totalAmount = 0);
    List<string> GetDefaultTerms(string templatePath);
    string SanitizeFileName(string? clientName, string? projectName, string fallback = "Quote");
}

public class WordQuoteGeneratorService : IWordQuoteGeneratorService
{
    private readonly ILogger<WordQuoteGeneratorService> _logger;

    private static readonly string[] ArabicDays = new[]
    {
        "السبت", "الأحد", "الاحد", "الإثنين", "الاثنين", "الثلاثاء", "الأربعاء", "الاربعاء", "الخميس", "الجمعة"
    };

    public WordQuoteGeneratorService(ILogger<WordQuoteGeneratorService> logger)
    {
        _logger = logger;
    }

    public string SanitizeFileName(string? clientName, string? projectName, string fallback = "Quote")
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(clientName))
        {
            parts.Add(clientName.Trim());
        }
        if (!string.IsNullOrWhiteSpace(projectName))
        {
            parts.Add(projectName.Trim());
        }

        string raw = string.Join(" ", parts);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        // Strip invalid characters for Windows file paths (\ / : * ? " < > | and control chars)
        char[] invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (char c in raw)
        {
            if (!invalidChars.Contains(c) && c != '\\' && c != '/' && c != ':' && c != '*' && c != '?' && c != '"' && c != '<' && c != '>' && c != '|')
            {
                sb.Append(c);
            }
        }

        string sanitized = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    public Task<string> GenerateQuoteDocumentAsync(
        string templatePath,
        string outputPath,
        QuoteRequestDto request,
        string quoteNumber,
        decimal totalAmount = 0)
    {
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException($"Template file not found at {templatePath}");
        }

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        // 1. Perfectly clone the original template to destination first
        File.Copy(templatePath, outputPath, true);

        // 2. Determine Day, Date, and Outgoing Number
        var quoteDate = request.QuoteDate ?? DateTime.Now;
        var dateStr = quoteDate.ToString("dd-MM-yyyy");
        var dayName = GetArabicDayName(quoteDate.DayOfWeek);

        using (var wordDoc = WordprocessingDocument.Open(outputPath, true))
        {
            // 3. Mutate SmartArt Diagram parts directly (data1.xml & drawing1.xml)
            UpdateDiagramParts(wordDoc, dayName, dateStr, quoteNumber);

            var mainPart = wordDoc.MainDocumentPart;
            if (mainPart == null || mainPart.Document.Body == null)
            {
                throw new InvalidOperationException("Invalid Word document template structure.");
            }

            var body = mainPart.Document.Body;

            // 4. Update Customer Fields in existing paragraphs
            UpdateCustomerFields(body, request);

            // 5. Update Product Items Table (Mutate existing table in place - NEVER replace or delete it)
            UpdateProductTable(body, request);

            // 6. Update Terms & Conditions (Clone prototype term paragraph if custom terms provided)
            UpdateTerms(body, request.Terms);

            // 7. Update Issuer details (Job title, prefix, and name in existing paragraph)
            UpdateIssuerDetails(body, request);

            // 8. General placeholder fallback replacements
            var genericReplacements = new Dictionary<string, string>
            {
                { "{{CompanyName}}", request.ClientName ?? string.Empty },
                { "{{ContactPerson}}", request.ContactPerson ?? string.Empty },
                { "{{ProjectName}}", request.ProjectName ?? string.Empty },
                { "{{Location}}", request.Location ?? string.Empty },
                { "{{Notes}}", request.Notes ?? string.Empty },
                { "{{PaymentTerms}}", request.PaymentTerms ?? string.Empty },
                { "{{ValidityDays}}", request.ValidityDays?.ToString() ?? "15" },
                { "{{QuoteNumber}}", quoteNumber },
                { "{{Date}}", dateStr }
            };
            ReplacePlaceholdersAcrossBody(body, genericReplacements);

            mainPart.Document.Save();
        }

        _logger.LogInformation("Successfully mutated template and generated Word document at {OutputPath}", outputPath);
        return Task.FromResult(outputPath);
    }

    public List<string> GetDefaultTerms(string templatePath)
    {
        var terms = new List<string>();
        if (!File.Exists(templatePath)) return terms;

        try
        {
            using var wordDoc = WordprocessingDocument.Open(templatePath, false);
            var body = wordDoc.MainDocumentPart?.Document.Body;
            if (body == null) return terms;

            bool capturing = false;
            foreach (var p in body.Elements<Paragraph>())
            {
                var text = string.Concat(p.Descendants<Text>().Select(t => t.Text)).Trim();
                if (text.Contains("الشروط العامة والخاصة لعرض السعر"))
                {
                    capturing = true;
                    continue;
                }
                if (capturing)
                {
                    if (text.Contains("فائق الاحترام والتقدير") || text.Contains("مدير تطوير") || text.Contains("اشرف الشربيني"))
                    {
                        break;
                    }
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        terms.Add(text);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not extract default terms from template. Using fallback default list.");
        }

        if (terms.Count == 0)
        {
            terms.AddRange(new[]
            {
                "السعر غير شامل ضريبة القيمة المضافة.",
                "يتم التوريد بعد استخراج الشيك بقيمة التوريد المستحق .",
                "السعر شامل توصيل الي الموقع .",
                "يتم توريد البضاعة علي بالتات خشب .",
                "الاسعار لا تشمل البالتات الخشب .",
                "يتم استرداد البالتات خلال مدة اقصاها 10 ايام ، فى حالة وجود عجز او تلف البالتات يتم احتساب سعر البالتة 150 جنيه .",
                "يتم تغيير الأسعار في حالة صدور قرارات سيادية فيما يخص أسعار المواد الخام والوقود.",
                "العرض ساري لمدة أسبوع من تاريخه ولا يجدد إلا بالرجوع الي الشركة ولا يلتفت لأي إجراء يتم بعد انتهاء المدة المقررة إلا بإقرار من الشركة.",
                "في حالة إصدار أمر توريد فإن الشروط السابق ذكرها جزء لا يتجزأ من شروط أمر التوريد حتى وإن لم تكتب."
            });
        }

        return terms;
    }

    private void UpdateDiagramParts(WordprocessingDocument wordDoc, string dayName, string dateStr, string quoteNumber)
    {
        foreach (var part in wordDoc.GetAllParts())
        {
            var uri = part.Uri.ToString();
            if (uri.Contains("diagrams/data", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var stream = part.GetStream(FileMode.Open, FileAccess.ReadWrite);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    string xml = reader.ReadToEnd();
                    string updated = UpdateDataXml(xml, dayName, dateStr, quoteNumber);
                    stream.Position = 0;
                    stream.SetLength(0);
                    using var writer = new StreamWriter(stream, Encoding.UTF8);
                    writer.Write(updated);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error updating diagram data part: {Uri}", uri);
                }
            }
            else if (uri.Contains("diagrams/drawing", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var stream = part.GetStream(FileMode.Open, FileAccess.ReadWrite);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    string xml = reader.ReadToEnd();
                    string updated = UpdateDrawingXml(xml, dayName, dateStr, quoteNumber);
                    stream.Position = 0;
                    stream.SetLength(0);
                    using var writer = new StreamWriter(stream, Encoding.UTF8);
                    writer.Write(updated);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error updating diagram drawing part: {Uri}", uri);
                }
            }
        }
    }

    private string UpdateDataXml(string xml, string dayName, string dateStr, string quoteNumber)
    {
        try
        {
            XNamespace dgm = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
            XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
            var doc = XDocument.Parse(xml);

            foreach (var pt in doc.Descendants(dgm + "pt"))
            {
                var mid = (string?)pt.Attribute("modelId");
                if (string.IsNullOrEmpty(mid)) continue;

                var tElem = pt.Descendants(a + "t").FirstOrDefault();
                if (tElem == null) continue;

                if (mid.Contains("C6707BAA", StringComparison.OrdinalIgnoreCase))
                {
                    tElem.Value = dayName;
                }
                else if (mid.Contains("DDEB167C", StringComparison.OrdinalIgnoreCase))
                {
                    tElem.Value = dateStr;
                }
                else if (mid.Contains("CE69EE8D", StringComparison.OrdinalIgnoreCase))
                {
                    tElem.Value = quoteNumber;
                }
            }
            return doc.ToString(SaveOptions.DisableFormatting);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse data1.xml with XDocument. Using regex replacement.");
            return Regex.Replace(xml, @"<a:t>(.*?)</a:t>", m =>
            {
                var val = m.Groups[1].Value.Trim();
                if (ArabicDays.Contains(val)) return $"<a:t>{dayName}</a:t>";
                if (Regex.IsMatch(val, @"^\d{2}-\d{2}-\d{4}$")) return $"<a:t>{dateStr}</a:t>";
                if (val.Contains("06-04-10-2026") || val.StartsWith("Q-", StringComparison.OrdinalIgnoreCase)) return $"<a:t>{quoteNumber}</a:t>";
                return m.Value;
            });
        }
    }

    private string UpdateDrawingXml(string xml, string dayName, string dateStr, string quoteNumber)
    {
        var labels = new HashSet<string> { "اليوم", "التاريخ", "رقم الصادر" };

        return Regex.Replace(xml, @"<a:t>(.*?)</a:t>", m =>
        {
            var txt = m.Groups[1].Value.Trim();
            if (labels.Contains(txt))
            {
                return m.Value;
            }
            if (ArabicDays.Contains(txt))
            {
                return $"<a:t>{dayName}</a:t>";
            }
            if (Regex.IsMatch(txt, @"^\d{2}-\d{2}-\d{4}$"))
            {
                return $"<a:t>{dateStr}</a:t>";
            }
            return $"<a:t>{quoteNumber}</a:t>";
        });
    }

    private void UpdateCustomerFields(Body body, QuoteRequestDto request)
    {
        string clientName = request.ClientName?.Trim() ?? string.Empty;
        string contactPerson = request.ContactPerson?.Trim() ?? string.Empty;
        string projectName = request.ProjectName?.Trim() ?? string.Empty;
        string location = request.Location?.Trim() ?? string.Empty;
        string contactTitle = !string.IsNullOrWhiteSpace(request.ContactTitle) ? request.ContactTitle.Trim() : "المهندس";

        // Determine title honorific (المحترمة vs المحترم)
        bool isFemale = contactTitle.EndsWith("ة") || contactTitle.Contains("مهندسة") || contactTitle.Contains("أستاذة") || contactTitle.Contains("سيدة");
        string honorific = isFemale ? "المحترمة" : "المحترم";

        foreach (var p in body.Elements<Paragraph>())
        {
            var text = string.Concat(p.Descendants<Text>().Select(t => t.Text));

            // Paragraph: السادة شركة : {{CompanyName}}                       المحترمين,,, ,,
            if (text.Contains("السادة شركة"))
            {
                var t = p.Descendants<Text>().FirstOrDefault();
                if (t != null)
                {
                    t.Text = t.Text.Replace("{{CompanyName}}", clientName);
                }
            }

            // Paragraph: عناية المهندسة :  {{ContactPerson}}                                                             المحترمة ,,, ,,
            if (text.Contains("عناية"))
            {
                var t = p.Descendants<Text>().FirstOrDefault();
                if (t != null)
                {
                    var updated = t.Text.Replace("{{ContactPerson}}", contactPerson);
                    if (contactTitle != "المهندسة" && updated.Contains("المهندسة"))
                    {
                        updated = updated.Replace("المهندسة", contactTitle);
                    }
                    if (honorific != "المحترمة" && updated.Contains("المحترمة"))
                    {
                        updated = updated.Replace("المحترمة", honorific);
                    }
                    t.Text = updated;
                }
            }

            // Paragraph: اسم المشروع : {{ProjectName}}
            if (text.Contains("اسم المشروع"))
            {
                var t = p.Descendants<Text>().FirstOrDefault();
                if (t != null)
                {
                    t.Text = t.Text.Replace("{{ProjectName}}", projectName);
                }
            }

            // Paragraph: المكان: {{Location}}
            if (text.Contains("المكان:"))
            {
                var t = p.Descendants<Text>().FirstOrDefault();
                if (t != null)
                {
                    t.Text = t.Text.Replace("{{Location}}", location);
                }
            }
        }
    }

    private void UpdateProductTable(Body body, QuoteRequestDto request)
    {
        // 1. Locate the existing product table in the template
        Table? targetTable = null;
        TableRow? templateRow = null;
        TableRow? headerRow = null;

        foreach (var tbl in body.Descendants<Table>())
        {
            var rows = tbl.Elements<TableRow>().ToList();
            if (rows.Count >= 2)
            {
                var r0Text = string.Concat(rows[0].Descendants<Text>().Select(t => t.Text));
                var r1Text = string.Concat(rows[1].Descendants<Text>().Select(t => t.Text));

                if (r0Text.Contains("النوع") || r0Text.Contains("المقاس") || r0Text.Contains("سعر الالف") ||
                    r1Text.Contains("{{Type}}") || r1Text.Contains("{{ProductName}}"))
                {
                    targetTable = tbl;
                    headerRow = rows[0];
                    templateRow = rows[1];
                    break;
                }
            }
        }

        if (targetTable == null || headerRow == null || templateRow == null)
        {
            _logger.LogWarning("Template product table could not be identified.");
            return;
        }

        bool hasDynamicRows = request.Rows != null && request.Rows.Count > 0;
        bool hasItems = request.Items != null && request.Items.Count > 0;

        if (hasDynamicRows)
        {
            var headers = request.Headers != null && request.Headers.Count > 0
                ? request.Headers
                : request.Rows!.First().Keys.ToList();

            // Mutate Header Row cells
            MutateRowCells(headerRow, headers, isHeader: true);

            // Mutate Data Rows
            var insertPos = templateRow;
            foreach (var rowDict in request.Rows!)
            {
                var newRow = (TableRow)templateRow.CloneNode(true);
                var cellValues = headers.Select(h => rowDict.TryGetValue(h, out var v) ? v ?? string.Empty : string.Empty).ToList();
                MutateRowCells(newRow, cellValues, isHeader: false);

                targetTable.InsertAfter(newRow, insertPos);
                insertPos = newRow;
            }

            // Remove the template placeholder row
            targetTable.RemoveChild(templateRow);
        }
        else if (hasItems)
        {
            var insertPos = templateRow;
            foreach (var item in request.Items!)
            {
                var newRow = (TableRow)templateRow.CloneNode(true);
                var rowReplacements = new Dictionary<string, string>
                {
                    { "{{Type}}", item.ProductName ?? string.Empty },
                    { "{{ProductName}}", item.ProductName ?? string.Empty },
                    { "{{Size}}", item.Size ?? string.Empty },
                    { "{{Capacity}}", item.Capacity ?? string.Empty },
                    { "{{Price}}", item.UnitPrice > 0 ? item.UnitPrice.ToString("N0") : string.Empty }
                };

                foreach (var cell in newRow.Elements<TableCell>())
                {
                    foreach (var para in cell.Elements<Paragraph>())
                    {
                        ReplaceInParagraph(para, rowReplacements);
                    }
                }

                targetTable.InsertAfter(newRow, insertPos);
                insertPos = newRow;
            }

            // Remove the template placeholder row
            targetTable.RemoveChild(templateRow);
        }
    }

    private void MutateRowCells(TableRow row, List<string> values, bool isHeader)
    {
        var existingCells = row.Elements<TableCell>().ToList();
        int targetCount = values.Count;

        // If cell count differs, adjust by cloning or removing cells while preserving formatting
        if (existingCells.Count < targetCount)
        {
            var protoCell = existingCells.LastOrDefault() ?? new TableCell();
            while (existingCells.Count < targetCount)
            {
                var cloned = (TableCell)protoCell.CloneNode(true);
                row.AppendChild(cloned);
                existingCells.Add(cloned);
            }
        }
        else if (existingCells.Count > targetCount)
        {
            for (int i = existingCells.Count - 1; i >= targetCount; i--)
            {
                row.RemoveChild(existingCells[i]);
                existingCells.RemoveAt(i);
            }
        }

        // Adjust widths proportionally to preserve total width (7629 dxa)
        int totalWidthDxa = 7629;
        int colWidth = totalWidthDxa / Math.Max(1, targetCount);

        for (int i = 0; i < targetCount; i++)
        {
            var cell = existingCells[i];
            var tcPr = cell.GetFirstChild<TableCellProperties>();
            if (tcPr == null)
            {
                tcPr = new TableCellProperties();
                cell.PrependChild(tcPr);
            }
            var tcW = tcPr.GetFirstChild<TableCellWidth>();
            if (tcW == null)
            {
                tcW = new TableCellWidth();
                tcPr.AppendChild(tcW);
            }
            tcW.Type = TableWidthUnitValues.Dxa;
            tcW.Width = colWidth.ToString();

            // Set cell text preserving existing paragraph properties and run properties
            SetCellText(cell, values[i]);
        }
    }

    private void SetCellText(TableCell cell, string text)
    {
        var para = cell.Elements<Paragraph>().FirstOrDefault();
        if (para == null)
        {
            para = new Paragraph();
            cell.AppendChild(para);
        }

        var run = para.Elements<Run>().FirstOrDefault();
        if (run != null)
        {
            foreach (var r in para.Elements<Run>().Skip(1).ToList())
            {
                r.Remove();
            }

            var textElem = run.Elements<Text>().FirstOrDefault();
            if (textElem == null)
            {
                textElem = new Text { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve };
                run.AppendChild(textElem);
            }
            textElem.Text = text;
        }
        else
        {
            var newRun = new Run(new Text(text) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve });
            para.AppendChild(newRun);
        }
    }

    private void UpdateTerms(Body body, List<string>? customTerms)
    {
        if (customTerms == null || customTerms.Count == 0)
        {
            // Requirement: Terms keep unchanged by default if not customized
            return;
        }

        Paragraph? headerPara = null;
        Paragraph? closingPara = null;
        var existingTermParas = new List<Paragraph>();

        bool insideTerms = false;
        foreach (var p in body.Elements<Paragraph>())
        {
            var text = string.Concat(p.Descendants<Text>().Select(t => t.Text)).Trim();

            if (text.Contains("الشروط العامة والخاصة لعرض السعر"))
            {
                headerPara = p;
                insideTerms = true;
                continue;
            }

            if (insideTerms)
            {
                if (text.Contains("فائق الاحترام والتقدير") || text.Contains("مدير تطوير") || text.Contains("اشرف الشربيني"))
                {
                    closingPara = p;
                    break;
                }
                existingTermParas.Add(p);
            }
        }

        if (headerPara == null || closingPara == null || existingTermParas.Count == 0)
        {
            _logger.LogWarning("Terms section boundary paragraphs could not be identified.");
            return;
        }

        // Clone prototype term paragraph to inherit exact bullet/numbering ListParagraph & numId="2" formatting
        var prototypePara = (Paragraph)existingTermParas[0].CloneNode(true);

        // Remove old terms
        foreach (var p in existingTermParas)
        {
            p.Remove();
        }

        // Insert new customized terms before closing paragraph
        foreach (var termText in customTerms)
        {
            if (string.IsNullOrWhiteSpace(termText)) continue;

            var newTermPara = (Paragraph)prototypePara.CloneNode(true);

            // Replace text inside paragraph while preserving pPr and rPr
            var run = newTermPara.Elements<Run>().FirstOrDefault();
            if (run != null)
            {
                foreach (var r in newTermPara.Elements<Run>().Skip(1).ToList())
                {
                    r.Remove();
                }
                var textElem = run.Elements<Text>().FirstOrDefault();
                if (textElem == null)
                {
                    textElem = new Text { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve };
                    run.AppendChild(textElem);
                }
                textElem.Text = termText;
            }
            else
            {
                newTermPara.AppendChild(new Run(new Text(termText) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }));
            }

            body.InsertBefore(newTermPara, closingPara);
        }
    }

    private void UpdateIssuerDetails(Body body, QuoteRequestDto request)
    {
        string issuerJobTitle = !string.IsNullOrWhiteSpace(request.IssuerJobTitle)
            ? request.IssuerJobTitle.Trim()
            : "مدير تطوير الاعمال والمبيعات";

        string issuerPrefix = request.IssuerPrefix ?? "م / ";
        string issuerName = !string.IsNullOrWhiteSpace(request.IssuerName)
            ? request.IssuerName.Trim()
            : "اشرف الشربيني";

        string fullIssuer = $"{issuerPrefix.Trim()} {issuerName}".Trim();

        foreach (var p in body.Elements<Paragraph>())
        {
            var text = string.Concat(p.Descendants<Text>().Select(t => t.Text));
            if (text.Contains("الشربيني") || text.Contains("مدير تطوير الاعمال والمبيعات") || text.Contains("مدير تطوير"))
            {
                foreach (var t in p.Descendants<Text>())
                {
                    if (t.Text.Contains("م / اشرف الشربيني"))
                    {
                        t.Text = fullIssuer;
                    }
                    else if (t.Text.Contains("اشرف الشربيني"))
                    {
                        t.Text = issuerName;
                    }
                    else if (t.Text.Contains("تطوير الاعمال والمبيعات"))
                    {
                        if (issuerJobTitle != "مدير تطوير الاعمال والمبيعات")
                        {
                            t.Text = issuerJobTitle;
                        }
                    }
                }
            }
        }
    }

    private void ReplacePlaceholdersAcrossBody(Body body, Dictionary<string, string> replacements)
    {
        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            ReplaceInParagraph(paragraph, replacements);
        }
    }

    private void ReplaceInParagraph(Paragraph paragraph, Dictionary<string, string> replacements)
    {
        foreach (var kvp in replacements)
        {
            var placeholder = kvp.Key;
            var replacement = kvp.Value;
            if (string.IsNullOrEmpty(placeholder) || placeholder == replacement) continue;

            // 1. Direct text node check (fast and clean)
            bool replaced = false;
            foreach (var t in paragraph.Descendants<Text>())
            {
                if (t.Text.Contains(placeholder))
                {
                    t.Text = t.Text.Replace(placeholder, replacement);
                    replaced = true;
                }
            }
            if (replaced) continue;

            // 2. Cross-run fallback
            var texts = paragraph.Descendants<Text>().ToList();
            if (texts.Count <= 1) continue;

            var fullText = string.Concat(texts.Select(t => t.Text));
            if (fullText.Contains(placeholder))
            {
                var newFullText = fullText.Replace(placeholder, replacement);
                texts[0].Text = newFullText;
                for (int i = 1; i < texts.Count; i++)
                {
                    texts[i].Text = string.Empty;
                }
            }
        }
    }

    private static string GetArabicDayName(DayOfWeek dayOfWeek) => dayOfWeek switch
    {
        DayOfWeek.Saturday => "السبت",
        DayOfWeek.Sunday => "الأحد",
        DayOfWeek.Monday => "الإثنين",
        DayOfWeek.Tuesday => "الثلاثاء",
        DayOfWeek.Wednesday => "الأربعاء",
        DayOfWeek.Thursday => "الخميس",
        DayOfWeek.Friday => "الجمعة",
        _ => "الأحد"
    };
}
