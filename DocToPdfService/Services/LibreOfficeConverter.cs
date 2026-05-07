using System.Diagnostics;

namespace DocToPdfService.Services;

public interface IDocumentConverter
{
    Task<byte[]> ConvertToPdfAsync(Stream inputStream, string fileName, CancellationToken ct = default);
}

public class LibreOfficeConverter : IDocumentConverter
{
    private readonly ILogger<LibreOfficeConverter> _logger;
    private readonly string _libreOfficePath;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".doc", ".docx", ".odt", ".rtf" };

    public LibreOfficeConverter(ILogger<LibreOfficeConverter> logger, IConfiguration config)
    {
        _logger = logger;
        _libreOfficePath = config["LibreOffice:ExecutablePath"] ?? DetectLibreOffice();
    }

    public async Task<byte[]> ConvertToPdfAsync(Stream inputStream, string fileName, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(fileName);
        if (!AllowedExtensions.Contains(ext))
            throw new ArgumentException($"File type '{ext}' is not supported. Allowed: {string.Join(", ", AllowedExtensions)}");

        var tempDir = Path.Combine(Path.GetTempPath(), "doctopdf", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var inputPath = Path.Combine(tempDir, Path.GetFileName(fileName));
        var outputPath = Path.ChangeExtension(inputPath, ".pdf");

        try
        {
            await using (var fs = File.Create(inputPath))
                await inputStream.CopyToAsync(fs, ct);

            // LibreOffice is not thread-safe for concurrent conversions on same profile
            await _lock.WaitAsync(ct);
            try
            {
                await RunLibreOfficeAsync(inputPath, tempDir, ct);
            }
            finally
            {
                _lock.Release();
            }

            if (!File.Exists(outputPath))
                throw new InvalidOperationException("Conversion failed: PDF output not found.");

            return await File.ReadAllBytesAsync(outputPath, ct);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private async Task RunLibreOfficeAsync(string inputPath, string outputDir, CancellationToken ct)
    {
        var args = $"--headless --norestore --convert-to pdf \"{inputPath}\" --outdir \"{outputDir}\"";

        var psi = new ProcessStartInfo
        {
            FileName = _libreOfficePath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _logger.LogInformation("Running LibreOffice: {Path} {Args}", _libreOfficePath, args);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start LibreOffice process.");

        var stdOut = await process.StandardOutput.ReadToEndAsync(ct);
        var stdErr = await process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            _logger.LogError("LibreOffice exited with code {Code}. stderr: {Err}", process.ExitCode, stdErr);
            throw new InvalidOperationException($"LibreOffice conversion failed (exit {process.ExitCode}): {stdErr}");
        }

        _logger.LogInformation("LibreOffice output: {Out}", stdOut);
    }

    private static string DetectLibreOffice()
    {
        string[] candidates =
        [
            "/usr/bin/libreoffice",
            "/usr/bin/soffice",
            "/usr/local/bin/libreoffice",
            "/opt/libreoffice/program/soffice",
            "/Applications/LibreOffice.app/Contents/MacOS/soffice",
        ];

        foreach (var path in candidates)
            if (File.Exists(path)) return path;

        // Fallback: rely on PATH
        return "libreoffice";
    }
}
