using System.Globalization;
using System.Text.RegularExpressions;

namespace Dashboard.Api.Telegram;

public sealed record ParseResult(
    bool HighConfidence,
    decimal? Amount,
    string? Description,
    string? Why,
    string? Category,
    string RawText);

public static partial class ExpenseMessageParser
{
    [GeneratedRegex(@"^\s*/add\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddCommand();

    [GeneratedRegex(@"(\d+(?:[.,]\d{1,2})?)", RegexOptions.CultureInvariant)]
    private static partial Regex AmountToken();

    public static ParseResult Parse(string? text)
    {
        var raw = (text ?? "").Trim();
        if (raw.Length == 0)
        {
            return new ParseResult(false, null, null, null, null, raw);
        }

        if (raw.StartsWith('/'))
        {
            var command = raw.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
            if (command is "/start" or "/help" or "/today" or "/week" or "/id")
            {
                return new ParseResult(false, null, null, null, null, raw);
            }

            raw = AddCommand().Replace(raw, "").Trim();
        }

        string? why = null;
        var working = raw;
        var whySplit = working.Split([" - ", " — ", " | ", " · "], 2, StringSplitOptions.None);
        if (whySplit.Length == 2)
        {
            working = whySplit[0].Trim();
            why = whySplit[1].Trim();
        }
        else
        {
            var becauseIndex = working.IndexOf(" because ", StringComparison.OrdinalIgnoreCase);
            if (becauseIndex > 0)
            {
                why = working[(becauseIndex + " because ".Length)..].Trim();
                working = working[..becauseIndex].Trim();
            }
        }

        var amountMatch = AmountToken().Match(working);
        if (!amountMatch.Success
            || !decimal.TryParse(
                amountMatch.Value.Replace(',', '.'),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var amount)
            || amount <= 0)
        {
            return new ParseResult(false, null, null, why, "General", raw);
        }

        var withoutAmount = (working[..amountMatch.Index] + working[(amountMatch.Index + amountMatch.Length)..])
            .Trim()
            .Trim(',', '.', '-', '—', '|', '·', ' ');
        withoutAmount = Regex.Replace(withoutAmount, @"\s{2,}", " ").Trim();

        if (string.IsNullOrWhiteSpace(withoutAmount))
        {
            return new ParseResult(false, amount, null, why, "General", raw);
        }

        var description = withoutAmount;
        var category = "General";
        var tokens = withoutAmount.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 1 && LooksLikeCategory(tokens[0]))
        {
            // "Food 10" → category Food, what = Food (or rest of words)
            category = NormalizeCategory(tokens[0]);
            description = tokens.Length == 1
                ? category
                : string.Join(' ', tokens.Skip(1));
        }

        if (string.IsNullOrWhiteSpace(why) && tokens.Length >= 2 && !LooksLikeCategory(tokens[0]))
        {
            var parts = description.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Length >= 3)
            {
                description = parts[0];
                why = parts[1];
            }
        }

        var high = amount > 0 && !string.IsNullOrWhiteSpace(description);
        return new ParseResult(high, amount, description, string.IsNullOrWhiteSpace(why) ? null : why, category, raw);
    }

    private static bool LooksLikeCategory(string token) =>
        token.StartsWith('#')
        || token.Equals("food", StringComparison.OrdinalIgnoreCase)
        || token.Equals("travel", StringComparison.OrdinalIgnoreCase)
        || token.Equals("general", StringComparison.OrdinalIgnoreCase)
        || token.Equals("rent", StringComparison.OrdinalIgnoreCase)
        || token.Equals("fun", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCategory(string token)
    {
        token = token.Trim().TrimStart('#');
        if (token.Length == 0)
        {
            return "General";
        }

        return char.ToUpperInvariant(token[0]) + token[1..].ToLowerInvariant();
    }
}
