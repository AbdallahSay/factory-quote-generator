using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Spire.Doc;

namespace FactoryQuoteApi.Services;

public interface IPdfConverterService
{
    Task<string> ConvertDocxToPdfAsync(string docxPath, string pdfPath, CancellationToken cancellationToken = default);
}

public class PdfConverterService : IPdfConverterService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PdfConverterService> _logger;

    public PdfConverterService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<PdfConverterService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> ConvertDocxToPdfAsync(string docxPath, string pdfPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(docxPath))
        {
            throw new FileNotFoundException($"DOCX file not found at {docxPath}");
        }

        var pdfDir = Path.GetDirectoryName(pdfPath);
        if (!string.IsNullOrEmpty(pdfDir) && !Directory.Exists(pdfDir))
        {
            Directory.CreateDirectory(pdfDir);
        }

        // 1. Primary: Use FreeSpire.Doc for true local DOCX to PDF conversion
        try
        {
            _logger.LogInformation("Attempting PDF conversion via FreeSpire.Doc: {DocxPath} -> {PdfPath}", docxPath, pdfPath);
            await Task.Run(() =>
            {
                var document = new Document();
                document.LoadFromFile(docxPath);
                document.SaveToFile(pdfPath, FileFormat.PDF);
                document.Close();
                document.Dispose();
            }, cancellationToken);

            if (File.Exists(pdfPath) && new FileInfo(pdfPath).Length > 0)
            {
                _logger.LogInformation("FreeSpire.Doc conversion succeeded: {PdfPath} (Size: {Size} bytes)", pdfPath, new FileInfo(pdfPath).Length);
                return pdfPath;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FreeSpire.Doc conversion failed. Attempting alternative converters.");
        }

        // 2. Secondary: PDFShift API if configured
        var apiKey = _configuration["PdfShift:ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                _logger.LogInformation("Attempting PDF conversion via PDFShift API...");
                var success = await ConvertViaPdfShiftAsync(apiKey, docxPath, pdfPath, cancellationToken);
                if (success)
                {
                    _logger.LogInformation("PDFShift conversion succeeded: {PdfPath}", pdfPath);
                    return pdfPath;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PDFShift API conversion failed. Falling back to local Python script.");
            }
        }

        // 3. Fallback: Python docx_to_pdf.py if available
        var pythonScript = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "scripts", "docx_to_pdf.py"));
        var pythonVenv = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".venv", "Scripts", "python.exe"));

        if (!File.Exists(pythonVenv))
        {
            pythonVenv = "python";
        }

        if (File.Exists(pythonScript))
        {
            try
            {
                _logger.LogInformation("Attempting PDF conversion via local Python script: {Script}", pythonScript);
                var psi = new ProcessStartInfo
                {
                    FileName = pythonVenv,
                    Arguments = $"\"{pythonScript}\" \"{docxPath}\" \"{pdfPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    await process.WaitForExitAsync(cancellationToken);
                    if (process.ExitCode == 0 && File.Exists(pdfPath) && new FileInfo(pdfPath).Length > 0)
                    {
                        _logger.LogInformation("Local Python PDF conversion succeeded: {PdfPath}", pdfPath);
                        return pdfPath;
                    }
                    else
                    {
                        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
                        _logger.LogWarning("Local Python PDF converter exited with code {Code}: {Err}", process.ExitCode, stderr);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Local Python conversion failed.");
            }
        }

        if (File.Exists(pdfPath) && new FileInfo(pdfPath).Length > 0)
        {
            return pdfPath;
        }

        throw new InvalidOperationException($"All PDF conversion strategies failed for '{docxPath}'.");
    }

    private async Task<bool> ConvertViaPdfShiftAsync(string apiKey, string docxPath, string pdfPath, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"api:{apiKey}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

        using var form = new MultipartFormDataContent();
        var fileBytes = await File.ReadAllBytesAsync(docxPath, cancellationToken);
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        form.Add(fileContent, "source", Path.GetFileName(docxPath));

        var response = await client.PostAsync("https://api.pdfshift.io/v3/convert/pdf", form, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var pdfBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            await File.WriteAllBytesAsync(pdfPath, pdfBytes, cancellationToken);
            return true;
        }

        _logger.LogWarning("PDFShift returned status code {StatusCode}: {Reason}", response.StatusCode, response.ReasonPhrase);
        return false;
    }
}
