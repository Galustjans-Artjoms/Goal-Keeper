using System.Globalization;
using Dashboard.Api.Auth;
using Dashboard.Api.Data;
using Dashboard.Api.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dashboard.Api.Telegram;

public class TelegramBotService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<TelegramOptions> _options;
    private readonly ILogger<TelegramBotService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public TelegramBotService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<TelegramOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<TelegramBotService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool IsEnabled => _options.CurrentValue.IsConfigured;

    public async Task<long> PollOnceAsync(long offset, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!options.IsConfigured)
        {
            return offset;
        }

        var client = CreateClient(options.BotToken);
        var updates = await client.GetUpdatesAsync(offset, cancellationToken);
        foreach (var update in updates)
        {
            offset = update.UpdateId + 1;
            if (update.Message?.Text is null || update.Message.Chat is null)
            {
                continue;
            }

            await HandleMessageAsync(client, update.Message, options, cancellationToken);
        }

        return offset;
    }

    private TelegramApiClient CreateClient(string token) =>
        new(_httpClientFactory.CreateClient(nameof(TelegramApiClient)), token);

    private async Task HandleMessageAsync(
        TelegramApiClient client,
        TelegramMessage message,
        TelegramOptions options,
        CancellationToken cancellationToken)
    {
        var chatId = message.Chat!.Id;
        var text = message.Text!.Trim();

        if (options.AllowedChatId == 0)
        {
            await client.SendMessageAsync(
                chatId,
                $"Your chat id is {chatId}. Set Telegram:AllowedChatId (user secrets), then restart.",
                cancellationToken);
            return;
        }

        if (chatId != options.AllowedChatId)
        {
            _logger.LogWarning("Ignored Telegram chat {ChatId}", chatId);
            await client.SendMessageAsync(chatId, "This bot is locked to another chat.", cancellationToken);
            return;
        }

        if (options.BoundAccountId <= 0)
        {
            await client.SendMessageAsync(
                chatId,
                "Set Telegram:BoundAccountId to your Goal Keeper account id (see Advanced → System).",
                cancellationToken);
            return;
        }

        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
        {
            await client.SendMessageAsync(
                chatId,
                """
                Send an expense like:
                4.50 coffee late for train
                12,30 taxi - rain
                /add 8 cafe | meeting

                Commands: /today /id
                Unclear lines go to Advanced → Inbox.
                """,
                cancellationToken);
            return;
        }

        if (text.StartsWith("/id", StringComparison.OrdinalIgnoreCase))
        {
            await client.SendMessageAsync(chatId, $"Chat id: {chatId}", cancellationToken);
            return;
        }

        if (text.StartsWith("/today", StringComparison.OrdinalIgnoreCase))
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var account = scope.ServiceProvider.GetRequiredService<AccountContext>();
            account.AccountId = options.BoundAccountId;
            var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
            var today = DateOnly.FromDateTime(DateTime.Today);
            var items = await db.Transactions
                .Where(t => t.AccountId == options.BoundAccountId && t.Date == today)
                .ToListAsync(cancellationToken);
            var total = items.Sum(t => t.Amount);
            await client.SendMessageAsync(
                chatId,
                items.Count == 0
                    ? "Nothing today yet."
                    : $"Today: {total.ToString("0.00", CultureInfo.InvariantCulture)} EUR · {items.Count} entries",
                cancellationToken);
            return;
        }

        var parsed = ExpenseMessageParser.Parse(text);
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var account = scope.ServiceProvider.GetRequiredService<AccountContext>();
            account.AccountId = options.BoundAccountId;
            var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
            var transactions = scope.ServiceProvider.GetRequiredService<TransactionService>();

            if (parsed.HighConfidence && parsed.Amount is decimal amount && !string.IsNullOrWhiteSpace(parsed.Description))
            {
                await transactions.CreateAsync(
                    new TransactionRequest
                    {
                        Date = DateOnly.FromDateTime(DateTime.Today),
                        Amount = amount,
                        Category = parsed.Category ?? "General",
                        Description = parsed.Description,
                        Why = string.IsNullOrWhiteSpace(parsed.Why) ? "—" : parsed.Why
                    },
                    cancellationToken);

                await client.SendMessageAsync(
                    chatId,
                    $"Saved {amount.ToString("0.00", CultureInfo.InvariantCulture)} · {parsed.Description}"
                    + (string.IsNullOrWhiteSpace(parsed.Why) ? "" : $" · {parsed.Why}"),
                    cancellationToken);
                return;
            }

            db.InboxEntries.Add(new InboxEntry
            {
                AccountId = options.BoundAccountId,
                RawText = parsed.RawText,
                Date = DateOnly.FromDateTime(DateTime.Today),
                Amount = parsed.Amount,
                Category = parsed.Category ?? "General",
                Description = parsed.Description,
                Why = parsed.Why,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(cancellationToken);
            await client.SendMessageAsync(
                chatId,
                "Queued in Inbox. Open Advanced → Inbox to accept or discard.",
                cancellationToken);
        }
    }
}
