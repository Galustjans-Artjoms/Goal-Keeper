using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dashboard.Api.Telegram;

public class TelegramApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly string _token;

    public TelegramApiClient(HttpClient http, string token)
    {
        _http = http;
        _token = token;
    }

    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(long offset, CancellationToken cancellationToken)
    {
        var url = $"https://api.telegram.org/bot{_token}/getUpdates?timeout=25&offset={offset}";
        using var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<TelegramResponse<List<TelegramUpdate>>>(JsonOptions, cancellationToken);
        return payload?.Ok == true ? payload.Result ?? [] : [];
    }

    public async Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
    {
        var url = $"https://api.telegram.org/bot{_token}/sendMessage";
        using var response = await _http.PostAsJsonAsync(
            url,
            new { chat_id = chatId, text },
            JsonOptions,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

public sealed class TelegramResponse<T>
{
    public bool Ok { get; set; }

    public T? Result { get; set; }
}

public sealed class TelegramUpdate
{
    [JsonPropertyName("update_id")]
    public long UpdateId { get; set; }

    public TelegramMessage? Message { get; set; }
}

public sealed class TelegramMessage
{
    [JsonPropertyName("message_id")]
    public long MessageId { get; set; }

    public TelegramChat? Chat { get; set; }

    public string? Text { get; set; }
}

public sealed class TelegramChat
{
    public long Id { get; set; }
}
