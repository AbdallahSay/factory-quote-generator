using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
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
                { "{{Date}}", dateStr },
                { "{{IssuerName}}", request.IssuerName ?? string.Empty },
                { "{{IssuerJobTitle}}", request.IssuerJobTitle ?? string.Empty },
                { "{{IssuerPrefix}}", request.IssuerPrefix ?? string.Empty }
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

                // Requirement: Make day/date/outgoing-number displayed values and labels bold
                foreach (var rPr in pt.Descendants(a + "rPr"))
                {
                    rPr.SetAttributeValue("b", "1");
                }
                foreach (var endParaRPr in pt.Descendants(a + "endParaRPr"))
                {
                    endParaRPr.SetAttributeValue("b", "1");
                }
            }
            return doc.ToString(SaveOptions.DisableFormatting);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse data1.xml with XDocument. Using regex replacement.");
            string res = Regex.Replace(xml, @"<a:t>(.*?)</a:t>", m =>
            {
                var val = m.Groups[1].Value.Trim();
                if (ArabicDays.Contains(val)) return $"<a:t>{dayName}</a:t>";
                if (Regex.IsMatch(val, @"^\d{2}-\d{2}-\d{4}$")) return $"<a:t>{dateStr}</a:t>";
                if (Regex.IsMatch(val, @"^\d{2}-\d{2}-\d{2}-\d{4}$") || val.Contains("06-04-10-2026") || val.StartsWith("Q-", StringComparison.OrdinalIgnoreCase)) return $"<a:t>{quoteNumber}</a:t>";
                return m.Value;
            });

            // Enforce bold on all runs
            res = Regex.Replace(res, @"<a:rPr\b([^>]*)>", m =>
            {
                var attrs = m.Groups[1].Value;
                if (attrs.Contains("b=")) return Regex.Replace(m.Value, @"b=""[^""]*""", @"b=""1""");
                return $"<a:rPr b=\"1\"{attrs}>";
            });

            return res;
        }
    }

    private string UpdateDrawingXml(string xml, string dayName, string dateStr, string quoteNumber)
    {
        var labels = new HashSet<string> { "اليوم", "التاريخ", "رقم الصادر" };

        string replaced = Regex.Replace(xml, @"<a:t>(.*?)</a:t>", m =>
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

        // Enforce bold (b="1") on all run properties and endParaRPr in drawing1.xml
        replaced = Regex.Replace(replaced, @"<a:rPr\b([^>]*)>", m =>
        {
            var attrs = m.Groups[1].Value;
            if (attrs.Contains("b="))
            {
                return Regex.Replace(m.Value, @"b=""[^""]*""", @"b=""1""");
            }
            return $"<a:rPr b=\"1\"{attrs}>";
        });

        replaced = Regex.Replace(replaced, @"<a:endParaRPr\b([^>]*)>", m =>
        {
            var attrs = m.Groups[1].Value;
            if (attrs.Contains("b="))
            {
                return Regex.Replace(m.Value, @"b=""[^""]*""", @"b=""1""");
            }
            return $"<a:endParaRPr b=\"1\"{attrs}>";
        });

        return replaced;
    }

    private void UpdateCustomerFields(Body body, QuoteRequestDto request)
    {
        string clientName = request.ClientName?.Trim() ?? string.Empty;
        string contactPerson = request.ContactPerson?.Trim() ?? string.Empty;
        string projectName = request.ProjectName?.Trim() ?? string.Empty;
        string location = request.Location?.Trim() ?? string.Empty;

        string contactTitle = !string.IsNullOrWhiteSpace(request.ContactTitle) ? request.ContactTitle.Trim() : "المهندس";
        string cleanTitle = contactTitle;
        if (cleanTitle.StartsWith("عناية"))
        {
            cleanTitle = cleanTitle.Substring("عناية".Length).Trim();
        }
        cleanTitle = cleanTitle.TrimEnd(':', ' ').Trim();
        if (string.IsNullOrWhiteSpace(cleanTitle))
        {
            cleanTitle = "المهندس";
        }

        // Determine title honorific (المحترمة vs المحترم)
        bool isFemale = cleanTitle.EndsWith("ة") || cleanTitle.Contains("مهندسة") || cleanTitle.Contains("أستاذة") || cleanTitle.Contains("سيدة") || cleanTitle.Contains("دكتورة");
        string honorific = isFemale ? "المحترمة" : "المحترم";

        string companyPrefix = "السادة شركة : ";
        string companySuffix = "المحترمين ,,,";
        string fullCompanyPrefix = $"{companyPrefix}{clientName}";

        string attentionPrefix = $"عناية {cleanTitle} : ";
        string attentionSuffix = $"{honorific} ,,,";
        string fullAttentionPrefix = $"{attentionPrefix}{contactPerson}";

        const int targetHonorificCol = 55;

        int spaces1 = targetHonorificCol - fullCompanyPrefix.Length;
        if (spaces1 < 4) spaces1 = 4;
        if (fullCompanyPrefix.Length + spaces1 + companySuffix.Length > 68)
        {
            spaces1 = Math.Max(2, 68 - fullCompanyPrefix.Length - companySuffix.Length);
        }

        int spaces2 = targetHonorificCol - fullAttentionPrefix.Length;
        if (spaces2 < 4) spaces2 = 4;
        if (fullAttentionPrefix.Length + spaces2 + attentionSuffix.Length > 68)
        {
            spaces2 = Math.Max(2, 68 - fullAttentionPrefix.Length - attentionSuffix.Length);
        }

        string companyFullLine = $"{fullCompanyPrefix}{new string(' ', spaces1)}{companySuffix}";
        string attentionFullLine = $"{fullAttentionPrefix}{new string(' ', spaces2)}{attentionSuffix}";
        string projectFullLine = $"اسم المشروع : {projectName}";
        string locationFullLine = $"المكان: {location}";

        foreach (var p in body.Elements<Paragraph>())
        {
            var text = string.Concat(p.Descendants<Text>().Select(t => t.Text));

            if (text.Contains("السادة شركة"))
            {
                SetCustomerParagraphContent(p, companyFullLine);
            }
            else if (text.Contains("عناية"))
            {
                SetCustomerParagraphContent(p, attentionFullLine);
            }
            else if (text.Contains("اسم المشروع"))
            {
                SetCustomerParagraphContent(p, projectFullLine);
            }
            else if (text.Contains("المكان:"))
            {
                SetCustomerParagraphContent(p, locationFullLine);
            }
        }
    }

    private static void SetCustomerParagraphContent(Paragraph p, string lineText)
    {
        var runs = p.Elements<Run>().ToList();
        Run targetRun;

        if (runs.Count == 0)
        {
            targetRun = p.AppendChild(new Run());
        }
        else
        {
            targetRun = runs[0];
            for (int i = 1; i < runs.Count; i++)
            {
                p.RemoveChild(runs[i]);
            }
        }

        // Apply bold and preserve/inherit existing run formatting (font, size, RTL, ar-EG)
        var pRPr = p.ParagraphProperties?.GetFirstChild<ParagraphMarkRunProperties>();
        var rPr = targetRun.GetFirstChild<RunProperties>();
        if (rPr == null)
        {
            rPr = new RunProperties();
            if (pRPr != null)
            {
                var runFonts = pRPr.GetFirstChild<RunFonts>();
                if (runFonts != null)
                {
                    rPr.RunFonts = (RunFonts)runFonts.CloneNode(true);
                }
            }
            targetRun.PrependChild(rPr);
        }

        if (rPr.Bold == null) rPr.Bold = new Bold { Val = OnOffValue.FromBoolean(true) };
        else rPr.Bold.Val = OnOffValue.FromBoolean(true);

        if (rPr.BoldComplexScript == null) rPr.BoldComplexScript = new BoldComplexScript { Val = OnOffValue.FromBoolean(true) };
        else rPr.BoldComplexScript.Val = OnOffValue.FromBoolean(true);

        if (rPr.FontSize == null) rPr.FontSize = new FontSize { Val = "28" };
        if (rPr.FontSizeComplexScript == null) rPr.FontSizeComplexScript = new FontSizeComplexScript { Val = "28" };
        if (rPr.RightToLeftText == null) rPr.RightToLeftText = new RightToLeftText();
        if (rPr.Languages == null) rPr.Languages = new Languages { Bidi = "ar-EG" };

        targetRun.RemoveAllChildren<Text>();
        targetRun.AppendChild(new Text(lineText) { Space = SpaceProcessingModeValues.Preserve });
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

        // Requirement 1: Center the products table block horizontally on the page
        var tblPr = targetTable.GetFirstChild<TableProperties>();
        if (tblPr == null)
        {
            tblPr = new TableProperties();
            targetTable.PrependChild(tblPr);
        }

        // Remove floating table positioning properties that cause shifts
        tblPr.RemoveAllChildren<TablePositionProperties>();
        // Remove any indentation that shifts the table left or right
        tblPr.RemoveAllChildren<TableIndentation>();

        // Center the table block horizontally
        var tableJustification = tblPr.GetFirstChild<TableJustification>();
        if (tableJustification == null)
        {
            tableJustification = new TableJustification();
            tblPr.AppendChild(tableJustification);
        }
        tableJustification.Val = TableRowAlignmentValues.Center;

        bool hasDynamicRows = request.Rows != null && request.Rows.Count > 0;
        bool hasItems = request.Items != null && request.Items.Count > 0;

        if (hasDynamicRows)
        {
            var headers = request.Headers != null && request.Headers.Count > 0
                ? request.Headers
                : request.Rows!.First().Keys.ToList();

            // Mutate Header Row cells (bold)
            MutateRowCells(headerRow, headers, isHeader: true);

            // Mutate Data Rows (bold)
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
            // Ensure header row cells are bold and centered
            foreach (var cell in headerRow.Elements<TableCell>())
            {
                foreach (var para in cell.Elements<Paragraph>())
                {
                    MakeParagraphBoldAndCentered(para);
                }
            }

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
                        MakeParagraphBoldAndCentered(para);
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

            // Center vertical alignment inside cell
            var tcVAlign = tcPr.GetFirstChild<TableCellVerticalAlignment>();
            if (tcVAlign == null)
            {
                tcVAlign = new TableCellVerticalAlignment();
                tcPr.AppendChild(tcVAlign);
            }
            tcVAlign.Val = TableVerticalAlignmentValues.Center;

            // Set cell text preserving existing paragraph properties and run properties, and make bold
            SetCellText(cell, values[i], isBold: true);
        }
    }

    private void SetCellText(TableCell cell, string text, bool isBold = true)
    {
        var para = cell.Elements<Paragraph>().FirstOrDefault();
        if (para == null)
        {
            para = new Paragraph();
            cell.AppendChild(para);
        }

        var pPr = para.GetFirstChild<ParagraphProperties>();
        if (pPr == null)
        {
            pPr = new ParagraphProperties();
            para.PrependChild(pPr);
        }

        // Center text inside cell horizontally
        var jc = pPr.GetFirstChild<Justification>();
        if (jc == null)
        {
            jc = new Justification();
            pPr.AppendChild(jc);
        }
        jc.Val = JustificationValues.Center;

        var run = para.Elements<Run>().FirstOrDefault();
        if (run == null)
        {
            run = new Run();
            para.AppendChild(run);
        }
        else
        {
            foreach (var r in para.Elements<Run>().Skip(1).ToList())
            {
                r.Remove();
            }
        }

        var rPr = run.GetFirstChild<RunProperties>();
        if (rPr == null)
        {
            rPr = new RunProperties();
            run.PrependChild(rPr);
        }

        if (isBold)
        {
            var bold = rPr.GetFirstChild<Bold>();
            if (bold == null)
            {
                rPr.AppendChild(new Bold());
            }
            else
            {
                bold.Val = true;
            }

            var boldCs = rPr.GetFirstChild<BoldComplexScript>();
            if (boldCs == null)
            {
                rPr.AppendChild(new BoldComplexScript());
            }
            else
            {
                boldCs.Val = true;
            }
        }

        var textElem = run.Elements<Text>().FirstOrDefault();
        if (textElem == null)
        {
            textElem = new Text { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve };
            run.AppendChild(textElem);
        }
        textElem.Text = text;
    }

    private static void MakeParagraphBoldAndCentered(Paragraph para)
    {
        var pPr = para.GetFirstChild<ParagraphProperties>();
        if (pPr == null)
        {
            pPr = new ParagraphProperties();
            para.PrependChild(pPr);
        }

        var jc = pPr.GetFirstChild<Justification>();
        if (jc == null)
        {
            jc = new Justification();
            pPr.AppendChild(jc);
        }
        jc.Val = JustificationValues.Center;

        foreach (var run in para.Elements<Run>())
        {
            var rPr = run.GetFirstChild<RunProperties>();
            if (rPr == null)
            {
                rPr = new RunProperties();
                run.PrependChild(rPr);
            }

            var bold = rPr.GetFirstChild<Bold>();
            if (bold == null) rPr.AppendChild(new Bold());
            else bold.Val = true;

            var boldCs = rPr.GetFirstChild<BoldComplexScript>();
            if (boldCs == null) rPr.AppendChild(new BoldComplexScript());
            else boldCs.Val = true;
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
        string? newJobTitle = !string.IsNullOrWhiteSpace(request.IssuerJobTitle)
            ? request.IssuerJobTitle.Trim()
            : null;

        string? rawName = !string.IsNullOrWhiteSpace(request.IssuerName)
            ? request.IssuerName.Trim()
            : null;

        string? rawPrefix = !string.IsNullOrWhiteSpace(request.IssuerPrefix)
            ? request.IssuerPrefix.Trim()
            : null;

        string? fullIssuer = null;
        if (!string.IsNullOrEmpty(rawName))
        {
            if (!string.IsNullOrEmpty(rawPrefix) && !rawName.StartsWith(rawPrefix, StringComparison.OrdinalIgnoreCase))
            {
                fullIssuer = $"{rawPrefix} {rawName}".Trim();
            }
            else
            {
                fullIssuer = rawName;
            }
        }

        // Job title target phrases to replace (from most specific to general)
        var jobTitleTargets = new[]
        {
            "مدير تطوير الأعمال والمبيعات",
            "مدير تطوير الاعمال والمبيعات",
            "تطوير الأعمال والمبيعات",
            "تطوير الاعمال والمبيعات"
        };

        // Name / signature target phrases to replace (from most specific to general)
        var nameTargets = new[]
        {
            "م / أشرف الشربيني",
            "م / اشرف الشربيني",
            "م/ أشرف الشربيني",
            "م/ اشرف الشربيني",
            "أشرف الشربيني",
            "اشرف الشربيني"
        };

        foreach (var p in body.Descendants<Paragraph>())
        {
            var text = string.Concat(p.Descendants<Text>().Select(t => t.Text));
            if (string.IsNullOrWhiteSpace(text)) continue;

            // Replace job title if custom title provided
            if (!string.IsNullOrEmpty(newJobTitle))
            {
                foreach (var target in jobTitleTargets)
                {
                    if (text.Contains(target))
                    {
                        ReplaceTextInParagraph(p, target, newJobTitle);
                        text = string.Concat(p.Descendants<Text>().Select(t => t.Text));
                        break;
                    }
                }
            }

            // Replace name / signature if custom name provided
            if (!string.IsNullOrEmpty(fullIssuer))
            {
                foreach (var target in nameTargets)
                {
                    if (text.Contains(target))
                    {
                        ReplaceTextInParagraph(p, target, fullIssuer);
                        text = string.Concat(p.Descendants<Text>().Select(t => t.Text));
                        break;
                    }
                }
            }
        }
    }

    private static void ReplaceTextInParagraph(Paragraph paragraph, string target, string replacement)
    {
        if (string.IsNullOrEmpty(target) || target == replacement) return;

        // 1. Direct single text element check
        bool replaced = false;
        foreach (var t in paragraph.Descendants<Text>())
        {
            if (t.Text.Contains(target))
            {
                t.Text = t.Text.Replace(target, replacement);
                replaced = true;
            }
        }

        // 2. Cross-run fallback if target was split across multiple <w:t> runs
        if (!replaced)
        {
            var texts = paragraph.Descendants<Text>().ToList();
            if (texts.Count > 1)
            {
                var fullText = string.Concat(texts.Select(t => t.Text));
                if (fullText.Contains(target))
                {
                    var newFullText = fullText.Replace(target, replacement);
                    texts[0].Text = newFullText;
                    for (int i = 1; i < texts.Count; i++)
                    {
                        texts[i].Text = string.Empty;
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

            // 2. Cross-run fallback
            if (!replaced)
            {
                var texts = paragraph.Descendants<Text>().ToList();
                if (texts.Count > 1)
                {
                    var fullText = string.Concat(texts.Select(t => t.Text));
                    if (fullText.Contains(placeholder))
                    {
                        var newFullText = fullText.Replace(placeholder, replacement);
                        texts[0].Text = newFullText;
                        for (int i = 1; i < texts.Count; i++)
                        {
                            texts[i].Text = string.Empty;
                        }
                        replaced = true;
                    }
                }
            }

            if (replaced && (placeholder == "{{QuoteNumber}}" || placeholder == "{{Date}}"))
            {
                foreach (var run in paragraph.Elements<Run>())
                {
                    var rPr = run.GetFirstChild<RunProperties>();
                    if (rPr == null)
                    {
                        rPr = new RunProperties();
                        run.PrependChild(rPr);
                    }
                    var bold = rPr.GetFirstChild<Bold>();
                    if (bold == null) rPr.AppendChild(new Bold());
                    else bold.Val = true;

                    var boldCs = rPr.GetFirstChild<BoldComplexScript>();
                    if (boldCs == null) rPr.AppendChild(new BoldComplexScript());
                    else boldCs.Val = true;
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
