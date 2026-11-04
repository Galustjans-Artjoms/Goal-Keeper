using Dashboard.Api.Data;
using Dashboard.Api.Security;
using Dashboard.Api.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dashboard.Api.SystemInfo;

public sealed class AppStatusService
{
    private readonly DashboardDbContext _db;
    private readonly IConfiguration _config;
    private readonly IOptionsMonitor<TelegramOptions> _telegram;
    private readonly DescriptionEncryptor _encryptor;

    public AppStatusService(
        DashboardDbContext db,
        IConfiguration config,
        IOptionsMonitor<TelegramOptions> telegram,
        DescriptionEncryptor encryptor)
    {
        _db = db;
        _config = config;
        _telegram = telegram;
        _encryptor = encryptor;
    }

    public async Task<AppStatus> GetAsync(CancellationToken cancellationToken)
    {
        var canQuery = false;
        try
        {
            canQuery = await _db.Database.CanConnectAsync(cancellationToken);
        }
        catch
        {
            canQuery = false;
        }

        var tg = _telegram.CurrentValue;
        var key = _config["Encryption:Key"]
                  ?? Environment.GetEnvironmentVariable("APP_ENCRYPTION_KEY");
        var encryptionOk = !string.IsNullOrWhiteSpace(key) && key != "changeit12345678";
        // Still "present" if default — note for UI honesty
        var encryptionPresent = !string.IsNullOrWhiteSpace(key) || _encryptor is not null;
        var cs = _config.GetConnectionString("Dashboard") ?? "Data Source=dashboard.db";

        return new AppStatus(
            canQuery,
            tg.IsConfigured && tg.AllowedChatId != 0 && tg.BoundAccountId > 0,
            !string.IsNullOrWhiteSpace(tg.BotToken),
            tg.AllowedChatId != 0,
            tg.BoundAccountId > 0,
            encryptionPresent,
            encryptionOk,
            cs);
    }
}

public sealed record AppStatus(
    bool DatabaseOk,
    bool TelegramReady,
    bool TelegramTokenSet,
    bool TelegramChatBound,
    bool TelegramAccountBound,
    bool EncryptionKeyPresent,
    bool EncryptionKeyHardened,
    string ConnectionString);
