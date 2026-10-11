namespace Dashboard.Api.Telegram;

public class TelegramOptions
{
    public const string SectionName = "Telegram";

    public string BotToken { get; set; } = "";

    public long AllowedChatId { get; set; }

    /// <summary>Account that receives Telegram expenses / inbox. 0 = disabled writes.</summary>
    public long BoundAccountId { get; set; }

    /// <summary>Token present → poll. Chat/account binding checked per message.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken);
}
