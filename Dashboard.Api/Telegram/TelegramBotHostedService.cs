using Microsoft.Extensions.Options;

namespace Dashboard.Api.Telegram;

public class TelegramBotHostedService : BackgroundService
{
    private readonly TelegramBotService _bot;
    private readonly IOptionsMonitor<TelegramOptions> _options;
    private readonly ILogger<TelegramBotHostedService> _logger;

    public TelegramBotHostedService(
        TelegramBotService bot,
        IOptionsMonitor<TelegramOptions> options,
        ILogger<TelegramBotHostedService> logger)
    {
        _bot = bot;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.CurrentValue.IsConfigured)
        {
            _logger.LogInformation("Telegram bot idle: set Telegram:BotToken to enable.");
            return;
        }

        _logger.LogInformation("Telegram bot polling started.");
        long offset = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_options.CurrentValue.IsConfigured)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }

                offset = await _bot.PollOnceAsync(offset, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Telegram poll failed; retrying in 5s.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
