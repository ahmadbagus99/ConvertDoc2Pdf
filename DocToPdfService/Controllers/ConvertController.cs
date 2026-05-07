using DocToPdfService.Services;
using Microsoft.AspNetCore.Mvc;

namespace DocToPdfService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConvertController : ControllerBase
{
    private readonly IDocumentConverter _converter;
    private readonly ILogger<ConvertController> _logger;
    private const long MaxFileSizeBytes = 50 * 1024 * 1024; // 50 MB

    public ConvertController(IDocumentConverter converter, ILogger<ConvertController> logger)
    {
        _converter = converter;
        _logger = logger;
    }

    /// <summary>
    /// Convert a DOC/DOCX file to PDF.
    /// </summary>
    /// <remarks>
    /// Upload a .doc, .docx, .odt, or .rtf file and receive the converted PDF.
    /// Max file size: 50 MB.
    /// </remarks>
    [HttpPost]
    [RequestSizeLimit(MaxFileSizeBytes)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Convert(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded." });

        if (file.Length > MaxFileSizeBytes)
            return BadRequest(new { error = $"File exceeds the maximum allowed size of {MaxFileSizeBytes / 1024 / 1024} MB." });

        _logger.LogInformation("Converting file: {Name} ({Size} bytes)", file.FileName, file.Length);

        try
        {
            await using var stream = file.OpenReadStream();
            var pdfBytes = await _converter.ConvertToPdfAsync(stream, file.FileName, ct);

            var outputName = Path.ChangeExtension(file.FileName, ".pdf");
            return File(pdfBytes, "application/pdf", outputName);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("antrian penuh") || ex.Message.Contains("sedang sibuk"))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
        catch (TimeoutException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Conversion failed for file {Name}", file.FileName);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Conversion failed. Check server logs for details." });
        }
    }
}
