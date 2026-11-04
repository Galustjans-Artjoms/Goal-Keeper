namespace Dashboard.Api.Import;

public sealed class ExcelSecurityGate
{
    public const long MaxBytes = 2 * 1024 * 1024;
    public const int MaxRows = 2000;
    public const int MaxColumns = 32;
    public const int MaxZipEntries = 200;

    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];

    public ExcelGateResult Inspect(string? fileName, string? contentType, long length, Stream content)
    {
        if (length <= 0 || length > MaxBytes)
        {
            return ExcelGateResult.Fail($"File must be between 1 byte and {MaxBytes / (1024 * 1024)} MB.");
        }

        var name = Path.GetFileName(fileName ?? "");
        if (string.IsNullOrWhiteSpace(name))
        {
            return ExcelGateResult.Fail("Missing file name.");
        }

        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext is ".xlsm" or ".xls" or ".xlsb" or ".xltm" or ".xltx")
        {
            return ExcelGateResult.Fail("Only .xlsx workbooks are allowed (no macros / legacy formats).");
        }

        if (ext != ".xlsx")
        {
            return ExcelGateResult.Fail("Only .xlsx files are accepted.");
        }

        if (!string.IsNullOrWhiteSpace(contentType)
            && !contentType.Contains("spreadsheet", StringComparison.OrdinalIgnoreCase)
            && !contentType.Contains("excel", StringComparison.OrdinalIgnoreCase)
            && !contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
            && !contentType.Equals("application/zip", StringComparison.OrdinalIgnoreCase))
        {
            return ExcelGateResult.Fail("Unexpected content type.");
        }

        Span<byte> header = stackalloc byte[4];
        var read = content.Read(header);
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        if (read < 4 || !header.SequenceEqual(ZipMagic))
        {
            return ExcelGateResult.Fail("File is not a valid OOXML (.xlsx) package.");
        }

        try
        {
            using var zip = new System.IO.Compression.ZipArchive(content, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count > MaxZipEntries)
            {
                return ExcelGateResult.Fail("Workbook package has too many parts.");
            }

            var totalUncompressed = zip.Entries.Sum(entry => entry.Length);
            if (totalUncompressed > MaxBytes * 20)
            {
                return ExcelGateResult.Fail("Workbook expands too large (zip bomb guard).");
            }
        }
        catch
        {
            return ExcelGateResult.Fail("Could not open workbook package.");
        }
        finally
        {
            if (content.CanSeek)
            {
                content.Position = 0;
            }
        }

        return ExcelGateResult.Ok(name);
    }
}

public sealed record ExcelGateResult(bool Success, string? FileName, string? Error)
{
    public static ExcelGateResult Ok(string fileName) => new(true, fileName, null);
    public static ExcelGateResult Fail(string error) => new(false, null, error);
}
