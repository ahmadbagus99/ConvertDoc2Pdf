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
    private readonly SemaphoreSlim _lock;
    private readonly int _maxQueueSize;
    private readonly int _queueTimeoutSeconds;
    private int _waitingCount;

    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".doc", ".docx", ".odt", ".rtf" };

    public LibreOfficeConverter(ILogger<LibreOfficeConverter> logger, IConfiguration config)
    {
        _logger = logger;
        _libreOfficePath = config["LibreOffice:ExecutablePath"] ?? DetectLibreOffice();

        var maxConcurrency = config.GetValue<int>("Conversion:MaxConcurrency", 2);
        _maxQueueSize = config.GetValue<int>("Conversion:MaxQueueSize", 20);
        _queueTimeoutSeconds = config.GetValue<int>("Conversion:QueueTimeoutSeconds", 30);

        _lock = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public async Task<byte[]> ConvertToPdfAsync(Stream inputStream, string fileName, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(fileName);
        if (!AllowedExtensions.Contains(ext))
            throw new ArgumentException($"File type '{ext}' is not supported. Allowed: {string.Join(", ", AllowedExtensions)}");

        var waiting = Interlocked.Increment(ref _waitingCount);
        if (waiting > _maxQueueSize)
        {
            Interlocked.Decrement(ref _waitingCount);
            _logger.LogWarning("Queue full. Waiting: {Waiting}, Limit: {Limit}", waiting, _maxQueueSize);
            throw new InvalidOperationException($"Server sedang sibuk. Antrian penuh ({_maxQueueSize} request). Coba lagi nanti.");
        }

        _logger.LogInformation("Request masuk antrian. Posisi antrian: {Waiting}", waiting);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_queueTimeoutSeconds));

        try
        {
            await _lock.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Request timeout setelah menunggu {Seconds}s di antrian.", _queueTimeoutSeconds);
            throw new TimeoutException($"Request timeout setelah menunggu {_queueTimeoutSeconds} detik di antrian.");
        }
        finally
        {
            Interlocked.Decrement(ref _waitingCount);
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "doctopdf", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var inputPath = Path.Combine(tempDir, Path.GetFileName(fileName));
        var outputPath = Path.ChangeExtension(inputPath, ".pdf");

        try
        {
            await using (var fs = File.Create(inputPath))
                await inputStream.CopyToAsync(fs, ct);

            await RunLibreOfficeAsync(inputPath, tempDir, ct);

            if (!File.Exists(outputPath))
                throw new InvalidOperationException("Conversion failed: PDF output not found.");

            return await File.ReadAllBytesAsync(outputPath, ct);
        }
        finally
        {
            _lock.Release();
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

        return "libreoffice";
    }
}
