using Microsoft.Extensions.Caching.Memory;

namespace Dashboard.Api.Auth;

public sealed class LoginThrottle
{
    private readonly IMemoryCache _cache;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private const int MaxFails = 8;

    public LoginThrottle(IMemoryCache cache)
    {
        _cache = cache;
    }

    public bool IsLocked(string key)
    {
        return _cache.TryGetValue(LockKey(key), out _);
    }

    public void RegisterFailure(string key)
    {
        var countKey = CountKey(key);
        var count = _cache.GetOrCreate(countKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = Window;
            return 0;
        });
        count++;
        _cache.Set(countKey, count, Window);
        if (count >= MaxFails)
        {
            _cache.Set(LockKey(key), true, TimeSpan.FromMinutes(5));
        }
    }

    public void Clear(string key)
    {
        _cache.Remove(CountKey(key));
        _cache.Remove(LockKey(key));
    }

    private static string CountKey(string key) => "login-fail:" + key;
    private static string LockKey(string key) => "login-lock:" + key;
}
