namespace Dashboard.Api.Import;

public static class ImportSanitize
{
    public static string Text(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var trimmed = value.Trim();
        // Neutralize CSV/Excel formula injection when values are exported later.
        if (trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@')
        {
            trimmed = "'" + trimmed;
        }

        if (trimmed.Length > maxLen)
        {
            trimmed = trimmed[..maxLen];
        }

        return trimmed;
    }

    public static string EscapeCsv(string? value)
    {
        var safe = Text(value, 10_000);
        if (safe.Contains(',') || safe.Contains('"') || safe.Contains('\n') || safe.Contains('\r'))
        {
            return "\"" + safe.Replace("\"", "\"\"") + "\"";
        }

        return safe;
    }
}
