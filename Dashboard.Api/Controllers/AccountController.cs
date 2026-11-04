using System.Security.Claims;
using Dashboard.Api.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Dashboard.Api.Controllers;

public class AccountController : Controller
{
    private readonly AccountService _accounts;
    private readonly CaptchaService _captcha;
    private readonly LoginThrottle _loginThrottle;

    public AccountController(AccountService accounts, CaptchaService captcha, LoginThrottle loginThrottle)
    {
        _accounts = accounts;
        _captcha = captcha;
        _loginThrottle = loginThrottle;
    }

    [AllowAnonymous]
    [HttpGet("/login")]
    public IActionResult Login(string? registered, string? logout, string? error, string? captcha)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return Redirect("/transactions");
        }

        ViewData["Message"] = registered is not null
            ? "Account created. Sign in."
            : logout is not null
                ? "You signed out."
                : error is not null
                    ? "Wrong username or password."
                    : null;
        ViewData["MessageOk"] = registered is not null || logout is not null;
        if (captcha == "locked")
        {
            ViewData["Message"] = "Too many attempts. Wait a couple of minutes.";
            ViewData["MessageOk"] = false;
        }
        else if (captcha is not null)
        {
            ViewData["Message"] = "The piece is not in place.";
            ViewData["MessageOk"] = false;
        }

        ViewData["CaptchaId"] = _captcha.Create();
        return View("~/Views/Account/Login.cshtml");
    }

    [AllowAnonymous]
    [HttpPost("/login")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string username, string password, string captchaId, string captcha, CancellationToken cancellationToken)
    {
        var throttleKey = (username ?? "").Trim().ToLowerInvariant()
                          + "|"
                          + (HttpContext.Connection.RemoteIpAddress?.ToString() ?? "local");
        if (_loginThrottle.IsLocked(throttleKey))
        {
            return Redirect("/login?captcha=locked");
        }

        if (!PassedCaptcha(captchaId, captcha, out var locked))
        {
            return Redirect(locked ? "/login?captcha=locked" : "/login?captcha=1");
        }

        var account = await _accounts.AuthenticateAsync(username ?? "", password ?? "", cancellationToken);
        if (account is null)
        {
            _loginThrottle.RegisterFailure(throttleKey);
            return Redirect("/login?error=1");
        }

        _loginThrottle.Clear(throttleKey);
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, account.Id.ToString()),
                new Claim(ClaimTypes.Name, account.Username)
            ],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));
        return Redirect("/transactions");
    }

    [AllowAnonymous]
    [HttpGet("/register")]
    public IActionResult Register()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return Redirect("/transactions");
        }

        ViewData["CaptchaId"] = _captcha.Create();
        return View("~/Views/Account/Register.cshtml");
    }

    [AllowAnonymous]
    [HttpPost("/register")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(string username, string password, string confirmPassword, string captchaId, string captcha, CancellationToken cancellationToken)
    {
        if (!PassedCaptcha(captchaId, captcha, out var locked))
        {
            ViewData["Error"] = locked
                ? "Too many attempts. Wait a couple of minutes."
                : "The piece is not in place.";
            ViewData["Username"] = username;
            ViewData["CaptchaId"] = _captcha.Create();
            return View("~/Views/Account/Register.cshtml");
        }

        var error = await _accounts.RegisterAsync(username ?? "", password ?? "", confirmPassword ?? "", cancellationToken);
        if (error is not null)
        {
            ViewData["Error"] = error;
            ViewData["Username"] = username;
            ViewData["CaptchaId"] = _captcha.Create();
            return View("~/Views/Account/Register.cshtml");
        }

        return Redirect("/login?registered=1");
    }

    [Authorize]
    [HttpPost("/logout")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/login?logout=1");
    }

    [AllowAnonymous]
    [HttpGet("/captcha/{id}/board")]
    public IActionResult Board(string id)
    {
        var bytes = _captcha.Board(id);
        return bytes is null ? NotFound() : File(bytes, "image/png");
    }

    [AllowAnonymous]
    [HttpGet("/captcha/{id}/piece")]
    public IActionResult Piece(string id)
    {
        var bytes = _captcha.Piece(id);
        return bytes is null ? NotFound() : File(bytes, "image/png");
    }

    private bool PassedCaptcha(string captchaId, string captcha, out bool locked)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "local";
        if (_captcha.TooManyTries(ip))
        {
            locked = true;
            return false;
        }

        if (!_captcha.Verify(captchaId, captcha))
        {
            _captcha.RegisterMiss(ip);
            locked = _captcha.TooManyTries(ip);
            return false;
        }

        _captcha.ClearMisses(ip);
        locked = false;
        return true;
    }
}
