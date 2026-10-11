using Dashboard.Api.Telegram;
using Microsoft.Extensions.Options;

namespace Dashboard.Api.Budget;

public class TelegramBudgetNotifier : IBudgetNotifier
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramBudgetNotifier> _logger;

    public TelegramBudgetNotifier(
        IHttpClientFactory httpClientFactory,
        IOptions<TelegramOptions> options,
        ILogger<TelegramBudgetNotifier> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task NotifyAsync(string message, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured || _options.AllowedChatId == 0)
        {
            _logger.LogInformation("Budget alert (Telegram off): {Message}", message);
            return;
        }

        var client = new TelegramApiClient(
            _httpClientFactory.CreateClient(nameof(TelegramApiClient)),
            _options.BotToken);
        await client.SendMessageAsync(_options.AllowedChatId, message, cancellationToken);
    }
}
