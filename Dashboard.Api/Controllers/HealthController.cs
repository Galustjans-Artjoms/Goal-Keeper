using Dashboard.Api.SystemInfo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dashboard.Api.Controllers;

[ApiController]
public class HealthController : ControllerBase
{
    private readonly AppStatusService _status;

    public HealthController(AppStatusService status)
    {
        _status = status;
    }

    [AllowAnonymous]
    [HttpGet("/health")]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var status = await _status.GetAsync(cancellationToken);
        return Ok(new
        {
            status = status.DatabaseOk ? "ok" : "degraded",
            database = status.DatabaseOk,
            telegramConfigured = status.TelegramReady,
            encryptionKeyPresent = status.EncryptionKeyPresent,
            encryptionKeyHardened = status.EncryptionKeyHardened
        });
    }
}
