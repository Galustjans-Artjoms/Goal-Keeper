using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Dashboard.Api.Auth;
using Dashboard.Api.Data;
using Dashboard.Api.Transactions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Tests;

public class DashboardFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public DashboardFactory()
    {
        _connection.Open();
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("SeedDemo", "false");
        builder.ConfigureServices(services =>
        {
            var descriptors = services.Where(service =>
                service.ServiceType == typeof(DbContextOptions<DashboardDbContext>) ||
                service.ServiceType == typeof(DashboardDbContext)).ToList();
            foreach (var descriptor in descriptors)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<DashboardDbContext>(options => options.UseSqlite(_connection));
        });
    }

    protected override void Dispose(bool disposing)
    {
        _connection.Dispose();
        base.Dispose(disposing);
    }
}

public class TransactionTests : IClassFixture<DashboardFactory>
{
    private readonly DashboardFactory _factory;

    public TransactionTests(DashboardFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreatesUpdatesAndDeletes()
    {
        var client = await SignedInClient();
        var created = await client.PostAsJsonAsync("/api/transactions", new
        {
            date = "2026-04-01",
            amount = 120.50m,
            category = "Food",
            description = "Card 1234"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<Transaction>();
        Assert.NotNull(body);
        Assert.True(body.Id > 0);

        var updated = await client.PutAsJsonAsync($"/api/transactions/{body.Id}", new
        {
            date = "2026-04-02",
            amount = 200.00m,
            category = "Utilities",
            description = "Updated description"
        });
        var updatedBody = await updated.Content.ReadFromJsonAsync<Transaction>();
        Assert.Equal(200.00m, updatedBody!.Amount);
        Assert.Equal("Utilities", updatedBody.Category);

        var deleted = await client.DeleteAsync($"/api/transactions/{body.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var missing = await client.GetAsync($"/api/transactions/{body.Id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task FiltersByDateAmountAndCategory()
    {
        var client = await SignedInClient();
        await client.PostAsJsonAsync("/api/transactions", Row("2026-01-02", 10, "Food", "Lunch"));
        await client.PostAsJsonAsync("/api/transactions", Row("2026-02-02", 30, "Travel", "Taxi"));
        await client.PostAsJsonAsync("/api/transactions", Row("2026-03-02", 50, "Food", "Dinner"));

        var response = await client.GetAsync(
            "/api/transactions?fromDate=2026-02-01&toDate=2026-03-31&minAmount=20&maxAmount=60&category=Food");
        var page = await response.Content.ReadFromJsonAsync<PagedTransactions>();

        Assert.NotNull(page);
        Assert.Equal(1, page.Total);
        Assert.Equal("Dinner", page.Items[0].Description);
    }

    [Fact]
    public async Task StoresDescriptionEncrypted()
    {
        var client = await SignedInClient();
        var created = await client.PostAsJsonAsync(
            "/api/transactions",
            Row("2026-04-20", 70, "Health", "Sensitive details"));
        var body = await created.Content.ReadFromJsonAsync<Transaction>();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
        var raw = db.Database
            .SqlQueryRaw<string>("SELECT description AS \"Value\" FROM Transactions WHERE Id = {0}", body!.Id)
            .AsEnumerable()
            .Single();

        Assert.NotEqual("Sensitive details", raw);

        var loaded = await client.GetFromJsonAsync<Transaction>($"/api/transactions/{body.Id}");
        Assert.Equal("Sensitive details", loaded!.Description);
    }

    [Fact]
    public async Task GuestIsSentToLogin()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var response = await client.GetAsync("/transactions");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login", response.Headers.Location?.OriginalString);
    }

    private async Task<HttpClient> SignedInClient()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = true
        });
        var name = "user" + Guid.NewGuid().ToString("N")[..8];
        await PostForm(client, "/register", new Dictionary<string, string>
        {
            ["username"] = name,
            ["password"] = "password1",
            ["confirmPassword"] = "password1"
        });
        await PostForm(client, "/login", new Dictionary<string, string>
        {
            ["username"] = name,
            ["password"] = "password1"
        });
        return client;
    }

    private async Task PostForm(HttpClient client, string url, Dictionary<string, string> fields)
    {
        var page = await client.GetAsync(url);
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        if (string.IsNullOrEmpty(token))
        {
            token = Regex.Match(html, "value=\"([^\"]+)\"[^>]*name=\"__RequestVerificationToken\"").Groups[1].Value;
        }

        fields["__RequestVerificationToken"] = token;
        var captchaId = Regex.Match(html, "name=\"captchaId\" value=\"([^\"]+)\"").Groups[1].Value;
        var offset = _factory.Services.GetRequiredService<CaptchaService>().Target(captchaId);
        Assert.NotNull(offset);
        fields["captchaId"] = captchaId;
        fields["captcha"] = offset.Value.ToString(CultureInfo.InvariantCulture);
        var response = await client.PostAsync(url, new FormUrlEncodedContent(fields));
        response.EnsureSuccessStatusCode();
    }

    private static object Row(string date, decimal amount, string category, string description)
    {
        return new { date, amount, category, description };
    }
}
