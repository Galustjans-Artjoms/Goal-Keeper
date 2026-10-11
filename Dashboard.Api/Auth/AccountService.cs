using System.Text.RegularExpressions;
using Dashboard.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Api.Auth;

public partial class AccountService
{
    private readonly DashboardDbContext _db;
    private readonly PasswordHasher<Account> _passwords = new();

    public AccountService(DashboardDbContext db)
    {
        _db = db;
    }

    public async Task<string?> RegisterAsync(string username, string password, string confirmPassword, CancellationToken cancellationToken)
    {
        var login = username.Trim();
        if (!LoginPattern().IsMatch(login))
        {
            return "Username must be 3–32 characters: letters, digits, and _.";
        }

        if (password.Length < 8 || password.Length > 72)
        {
            return "Password must be at least 8 characters.";
        }

        if (password != confirmPassword)
        {
            return "Passwords do not match.";
        }

        if (await _db.Accounts.AnyAsync(account => account.Username == login, cancellationToken))
        {
            return "That username is already taken.";
        }

        var account = new Account
        {
            Username = login,
            CreatedAt = DateTime.UtcNow
        };
        account.PasswordHash = _passwords.HashPassword(account, password);
        _db.Accounts.Add(account);
        await _db.SaveChangesAsync(cancellationToken);
        return null;
    }

    public async Task<Account?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken)
    {
        var login = username.Trim();
        var account = await _db.Accounts.FirstOrDefaultAsync(item => item.Username == login, cancellationToken);
        if (account is null)
        {
            return null;
        }

        var result = _passwords.VerifyHashedPassword(account, account.PasswordHash, password);
        return result == PasswordVerificationResult.Failed ? null : account;
    }

    [GeneratedRegex("^[a-zA-Z0-9_]{3,32}$")]
    private static partial Regex LoginPattern();
}
