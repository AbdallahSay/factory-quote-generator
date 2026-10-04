using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FactoryQuoteApi.DTOs;

namespace FactoryQuoteApi.Services;

public interface IWordQuoteGeneratorService
{
    Task<string> GenerateQuoteDocumentAsync(string templatePath, string outputPath, QuoteRequestDto request, string quoteNumber, decimal totalAmount);
}

public class WordQuoteGeneratorService : IWordQuoteGeneratorService
{
    private readonly ILogger<WordQuoteGeneratorService> _logger;

    public WordQuoteGeneratorService(ILogger<WordQuoteGeneratorService> logger)
    {
        _logger = logger;
    }

    public Task<string> GenerateQuoteDocumentAsync(
        string templatePath,
        string outputPath,
        QuoteRequestDto request,
        string quoteNumber,
        decimal totalAmount)
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

            // 1. Dictionary of simple text placeholders
            var textReplacements = new Dictionary<string, string>
            {
                { "{{CompanyName}}", request.ClientName ?? string.Empty },
                { "{{ContactPerson}}", request.ContactPerson ?? string.Empty },
                { "{{ProjectName}}", request.ProjectName ?? string.Empty },
                { "{{Location}}", request.Location ?? string.Empty },
                { "{{QuoteNumber}}", quoteNumber },
                { "{{Date}}", DateTime.Now.ToString("yyyy/MM/dd") },
                { "{{TotalAmount}}", totalAmount.ToString("N2") }
            };

            // Replace simple placeholders across paragraphs (handling run-splitting)
            ReplacePlaceholdersAcrossBody(body, textReplacements);

            // 2. Populate product table rows
            PopulateProductTable(body, request.Items);

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
}
