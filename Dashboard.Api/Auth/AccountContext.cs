namespace Dashboard.Api.Auth;

/// <summary>Per-request account scope. Set from auth cookie or Telegram binding.</summary>
public sealed class AccountContext
{
    public long AccountId { get; set; }

    public bool IsSet => AccountId > 0;

    public long Require() =>
        IsSet ? AccountId : throw new InvalidOperationException("Account context is not set.");
}
