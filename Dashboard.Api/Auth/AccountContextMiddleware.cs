using System.Security.Claims;

namespace Dashboard.Api.Auth;

public sealed class AccountContextMiddleware
{
    private readonly RequestDelegate _next;

    public AccountContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext http, AccountContext account)
    {
        if (http.User.Identity?.IsAuthenticated == true)
        {
            var raw = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (long.TryParse(raw, out var id) && id > 0)
            {
                account.AccountId = id;
            }
        }

        await _next(http);
    }
}
