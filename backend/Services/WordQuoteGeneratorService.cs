using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FactoryQuoteApi.DTOs;
using System.Text;
using System.Text.RegularExpressions;

namespace FactoryQuoteApi.Services;

public interface IWordQuoteGeneratorService
{
    Task<string> GenerateQuoteDocumentAsync(string templatePath, string outputPath, QuoteRequestDto request, string quoteNumber, decimal totalAmount = 0);
    string SanitizeFileName(string? clientName, string? projectName, string fallback = "Quote");
}

public class WordQuoteGeneratorService : IWordQuoteGeneratorService
{
    private readonly ILogger<WordQuoteGeneratorService> _logger;

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

        // Collapse multiple spaces into a single space and trim
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

        // Copy template to destination first
        File.Copy(templatePath, outputPath, true);

        using (var wordDoc = WordprocessingDocument.Open(outputPath, true))
        {
            var mainPart = wordDoc.MainDocumentPart;
            if (mainPart == null || mainPart.Document.Body == null)
            {
                throw new InvalidOperationException("Invalid Word document template structure.");
            }

            var body = mainPart.Document.Body;

            // 1. Dictionary of simple text placeholders (TotalAmount is intentionally excluded to keep it internal)
            var textReplacements = new Dictionary<string, string>
            {
                { "{{CompanyName}}", request.ClientName ?? string.Empty },
                { "{{ContactPerson}}", request.ContactPerson ?? string.Empty },
                { "{{ProjectName}}", request.ProjectName ?? string.Empty },
                { "{{Location}}", request.Location ?? string.Empty },
                { "{{Notes}}", request.Notes ?? string.Empty },
                { "{{PaymentTerms}}", request.PaymentTerms ?? string.Empty },
                { "{{ValidityDays}}", request.ValidityDays?.ToString() ?? "15" },
                { "{{QuoteNumber}}", quoteNumber },
                { "{{Date}}", DateTime.Now.ToString("yyyy/MM/dd") }
            };

            // Replace simple placeholders across paragraphs (handling run-splitting)
            ReplacePlaceholdersAcrossBody(body, textReplacements);

            // 2. Populate product table rows (Dynamic Grid or Legacy Items)
            if (request.Headers != null && request.Headers.Count > 0 && request.Rows != null && request.Rows.Count > 0)
            {
                PopulateDynamicTable(body, request.Headers, request.Rows);
            }
            else if (request.Items != null && request.Items.Count > 0)
            {
                PopulateProductTable(body, request.Items);
            }

            mainPart.Document.Save();
        }

        _logger.LogInformation("Successfully generated Word document at {OutputPath}", outputPath);
        return Task.FromResult(outputPath);
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
        var runs = paragraph.Elements<Run>().ToList();
        if (runs.Count == 0) return;

        // Build full text from all runs
        var fullText = string.Concat(runs.SelectMany(r => r.Elements<Text>()).Select(t => t.Text));
        if (string.IsNullOrEmpty(fullText)) return;

        bool hasAnyMatch = replacements.Keys.Any(k => fullText.Contains(k));
        if (!hasAnyMatch) return;

        // Map characters to (Run, Text)
        var charMap = new List<(Run run, Text textNode, int charIndexInText)>();
        foreach (var run in runs)
        {
            foreach (var textNode in run.Elements<Text>())
            {
                for (int i = 0; i < textNode.Text.Length; i++)
                {
                    charMap.Add((run, textNode, i));
                }
            }
        }

        foreach (var kvp in replacements)
        {
            var placeholder = kvp.Key;
            var replacement = kvp.Value;

            int index;
            while ((index = fullText.IndexOf(placeholder, StringComparison.Ordinal)) >= 0)
            {
                var firstCharMapping = charMap[index];
                var lastCharMapping = charMap[index + placeholder.Length - 1];

                if (firstCharMapping.run == lastCharMapping.run)
                {
                    // Placeholder is contained entirely within one run
                    var runText = firstCharMapping.textNode.Text;
                    var localIndex = firstCharMapping.charIndexInText;
                    firstCharMapping.textNode.Text = runText.Substring(0, localIndex) + replacement + runText.Substring(localIndex + placeholder.Length);
                }
                else
                {
                    // Placeholder spans multiple runs
                    // Replace in first run, empty intermediate/remaining text nodes
                    var startRunText = firstCharMapping.textNode.Text;
                    firstCharMapping.textNode.Text = startRunText.Substring(0, firstCharMapping.charIndexInText) + replacement;

                    var runsToClear = charMap.Skip(index + 1).Take(placeholder.Length - 1).Select(m => m.textNode).Distinct();
                    foreach (var node in runsToClear)
                    {
                        if (node == lastCharMapping.textNode)
                        {
                            var endRunText = node.Text;
                            node.Text = endRunText.Substring(lastCharMapping.charIndexInText + 1);
                        }
                        else
                        {
                            node.Text = string.Empty;
                        }
                    }
                }

                // Refresh fullText and charMap for subsequent replacements
                fullText = string.Concat(runs.SelectMany(r => r.Elements<Text>()).Select(t => t.Text));
                charMap.Clear();
                foreach (var run in runs)
                {
                    foreach (var textNode in run.Elements<Text>())
                    {
                        for (int i = 0; i < textNode.Text.Length; i++)
                        {
                            charMap.Add((run, textNode, i));
                        }
                    }
                }
            }
        }
    }

    private void PopulateProductTable(Body body, List<ProductItemDto> items)
    {
        // Find the table that contains our row placeholder: {{Type}} or {{ProductName}}
        TableRow? templateRow = null;
        Table? targetTable = null;

        foreach (var table in body.Descendants<Table>())
        {
            foreach (var row in table.Elements<TableRow>())
            {
                var rowText = string.Concat(row.Descendants<Text>().Select(t => t.Text));
                if (rowText.Contains("{{Type}}") || rowText.Contains("{{ProductName}}"))
                {
                    templateRow = row;
                    targetTable = table;
                    break;
                }
            }
            if (templateRow != null) break;
        }

        if (targetTable == null || templateRow == null)
        {
            _logger.LogWarning("Template table row containing {{Type}} or {{ProductName}} was not found.");
            return;
        }

        var insertPosition = templateRow;

        foreach (var item in items)
        {
            var newRow = (TableRow)templateRow.CloneNode(true);

            var rowReplacements = new Dictionary<string, string>
            {
                { "{{Type}}", item.ProductName ?? string.Empty },
                { "{{ProductName}}", item.ProductName ?? string.Empty },
                { "{{Size}}", item.Size ?? "25*12*6" },
                { "{{Capacity}}", item.Capacity ?? item.Quantity.ToString("N0") },
                { "{{Price}}", item.UnitPrice > 0 ? item.UnitPrice.ToString("N2") : "1450" }
            };

            foreach (var cell in newRow.Elements<TableCell>())
            {
                foreach (var paragraph in cell.Elements<Paragraph>())
                {
                    ReplaceInParagraph(paragraph, rowReplacements);
                }
            }

            targetTable.InsertAfter(newRow, insertPosition);
            insertPosition = newRow;
        }

        // Remove original placeholder row
        targetTable.RemoveChild(templateRow);
    }

    private void PopulateDynamicTable(Body body, List<string> headers, List<Dictionary<string, string>> rows)
    {
        var table = new Table();

        // 1. Table Properties (100% width, borders, RTL visual order, margins)
        var tblPr = new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 8, Color = "1E3A8A" },
                new BottomBorder { Val = BorderValues.Single, Size = 8, Color = "1E3A8A" },
                new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "CBD5E1" },
                new RightBorder { Val = BorderValues.Single, Size = 4, Color = "CBD5E1" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "E2E8F0" },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "E2E8F0" }
            ),
            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
            new TableJustification { Val = TableRowAlignmentValues.Center },
            new BiDiVisual(),
            new TableCellMarginDefault(
                new TopMargin { Width = "120", Type = TableWidthUnitValues.Dxa },
                new BottomMargin { Width = "120", Type = TableWidthUnitValues.Dxa },
                new LeftMargin { Width = "160", Type = TableWidthUnitValues.Dxa },
                new RightMargin { Width = "160", Type = TableWidthUnitValues.Dxa }
            )
        );
        table.AppendChild(tblPr);

        // 2. Header Row
        var headerRow = new TableRow();
        headerRow.AppendChild(new TableRowProperties(new TableHeader(), new CantSplit()));

        foreach (var headerText in headers)
        {
            var cell = new TableCell();
            var cellPr = new TableCellProperties(
                new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "1E3A8A" },
                new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center }
            );
            cell.AppendChild(cellPr);

            var para = new Paragraph(
                new ParagraphProperties(
                    new Justification { Val = JustificationValues.Center },
                    new BiDi()
                ),
                new Run(
                    new RunProperties(
                        new Bold(),
                        new Color { Val = "FFFFFF" },
                        new FontSize { Val = "22" },
                        new RunFonts { Ascii = "Cairo", HighAnsi = "Cairo", ComplexScript = "Cairo" }
                    ),
                    new Text(headerText) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }
                )
            );
            cell.AppendChild(para);
            headerRow.AppendChild(cell);
        }
        table.AppendChild(headerRow);

        // 3. Data Rows
        for (int r = 0; r < rows.Count; r++)
        {
            var rowDict = rows[r];
            var isEven = (r % 2 == 0);
            var dataRow = new TableRow();
            dataRow.AppendChild(new TableRowProperties(new CantSplit()));

            foreach (var header in headers)
            {
                rowDict.TryGetValue(header, out var cellValue);
                cellValue ??= string.Empty;

                var cell = new TableCell();
                var cellPr = new TableCellProperties(
                    new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = isEven ? "FFFFFF" : "F8FAFC" },
                    new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center }
                );
                cell.AppendChild(cellPr);

                var para = new Paragraph(
                    new ParagraphProperties(
                        new Justification { Val = JustificationValues.Center },
                        new BiDi()
                    ),
                    new Run(
                        new RunProperties(
                            new Color { Val = "1E293B" },
                            new FontSize { Val = "20" },
                            new RunFonts { Ascii = "Cairo", HighAnsi = "Cairo", ComplexScript = "Cairo" }
                        ),
                        new Text(cellValue) { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }
                    )
                );
                cell.AppendChild(para);
                dataRow.AppendChild(cell);
            }
            table.AppendChild(dataRow);
        }

        // 4. Locate {{DynamicItemsTable}} placeholder or existing table to replace
        Paragraph? placeholderParagraph = null;
        foreach (var p in body.Descendants<Paragraph>())
        {
            var pText = string.Concat(p.Descendants<Text>().Select(t => t.Text));
            if (pText.Contains("{{DynamicItemsTable}}"))
            {
                placeholderParagraph = p;
                break;
            }
        }

        if (placeholderParagraph != null)
        {
            placeholderParagraph.Parent?.InsertAfter(table, placeholderParagraph);
            placeholderParagraph.Remove();
            _logger.LogInformation("Replaced {Placeholder} with dynamic table.", "{{DynamicItemsTable}}");
            return;
        }

        // Fallback: check if an old template table exists with {{Type}} or {{ProductName}}
        Table? templateTable = null;
        foreach (var t in body.Descendants<Table>())
        {
            var tblText = string.Concat(t.Descendants<Text>().Select(x => x.Text));
            if (tblText.Contains("{{Type}}") || tblText.Contains("{{ProductName}}") || tblText.Contains("سعر الالف"))
            {
                templateTable = t;
                break;
            }
        }

        if (templateTable != null)
        {
            templateTable.Parent?.InsertAfter(table, templateTable);
            templateTable.Remove();
            _logger.LogInformation("Replaced old template table with programmatic dynamic table.");
            return;
        }

        // Final fallback: append table to document body
        body.AppendChild(table);
        _logger.LogInformation("Appended dynamic table to document body.");
    }
}
