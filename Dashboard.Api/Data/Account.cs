using System.ComponentModel.DataAnnotations;

namespace Dashboard.Api.Data;

public class Account
{
    public long Id { get; set; }

    [MaxLength(32)]
    public string Username { get; set; } = "";

    public string PasswordHash { get; set; } = "";

    public DateTime CreatedAt { get; set; }
}
