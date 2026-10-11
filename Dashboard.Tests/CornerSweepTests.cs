using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Dashboard.Api.Auth;
using Dashboard.Api.Data;
using Dashboard.Api.Import;
using Dashboard.Api.Ops;
using Dashboard.Api.Security;
using Dashboard.Api.Telegram;
using Dashboard.Api.Transactions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Tests;

/// <summary>Wide corner sweep: parser, paycheck math, import gate, crypto, LP, auth, API.</summary>
public class CornerSweepUnitTests
{
    [Fact]
    public void Roadmap_month_record_shapes()
    {
        var cell = new Dashboard.Api.Budget.RoadmapMonth("Oct 26", 2026, 10, 200m, false, true);
        Assert.Equal(200m, cell.Saved);
        Assert.False(cell.IsForecast);
    }

    // ——— Telegram parser (≥20) ———

    [Theory]
    [InlineData("Food 10", true, "10", "Food", "Food")]
    [InlineData("food 10", true, "10", "Food", "Food")]
    [InlineData("FOOD 99.5", true, "99.5", "Food", "Food")]
    [InlineData("Travel 12,50", true, "12.5", "Travel", "Travel")]
    [InlineData("Rent 800", true, "800", "Rent", "Rent")]
    [InlineData("Fun 3", true, "3", "Fun", "Fun")]
    [InlineData("#food 7", true, "7", "Food", "Food")]
    [InlineData("Food coffee 4.5", true, "4.5", "Food", "coffee")]
    [InlineData("4.50 coffee late for train", true, "4.5", "General", "coffee")]
    [InlineData("12,30 taxi - rain", true, "12.3", "General", "taxi")]
    [InlineData("8 cafe | meeting", true, "8", "General", "cafe")]
    [InlineData("/add 8 cafe | meeting", true, "8", "General", "cafe")]
    [InlineData("10", false, "10", "General", null)]
    [InlineData("", false, null, null, null)]
    [InlineData("   ", false, null, null, null)]
    [InlineData("bought snacks earlier", false, null, "General", null)]
    [InlineData("/start", false, null, null, null)]
    [InlineData("/help", false, null, null, null)]
    [InlineData("/today", false, null, null, null)]
    [InlineData("0 food", false, null, "General", null)]
    // Known quirk: leading '-' is stripped; "-5 food" parses as +5 Food
    [InlineData("-5 food", true, "5", "Food", "Food")]
    [InlineData("General 1", true, "1", "General", "General")]
    public void Parser_corners(
        string text,
        bool high,
        string? amount,
        string? category,
        string? description)
    {
        var result = ExpenseMessageParser.Parse(text);
        Assert.Equal(high, result.HighConfidence);
        if (amount is null)
        {
            Assert.Null(result.Amount);
        }
        else
        {
            Assert.Equal(decimal.Parse(amount, CultureInfo.InvariantCulture), result.Amount);
        }

        if (category is not null)
        {
            Assert.Equal(category, result.Category);
        }

        if (description is null)
        {
            Assert.True(string.IsNullOrWhiteSpace(result.Description));
        }
        else
        {
            Assert.Equal(description, result.Description);
        }
    }

    [Fact]
    public void Parser_null_is_low()
    {
        var result = ExpenseMessageParser.Parse(null);
        Assert.False(result.HighConfidence);
        Assert.Null(result.Amount);
    }

    [Fact]
    public void Parser_because_splits_why()
    {
        var result = ExpenseMessageParser.Parse("5 coffee because late");
        Assert.True(result.HighConfidence);
        Assert.Equal("coffee", result.Description);
        Assert.Equal("late", result.Why);
    }

    [Fact]
    public void Parser_em_dash_why()
    {
        var result = ExpenseMessageParser.Parse("9 taxi — rain");
        Assert.True(result.HighConfidence);
        Assert.Equal("taxi", result.Description);
        Assert.Equal("rain", result.Why);
    }

    // ——— Paycheck / BudgetSettings (≥10) ———

    [Theory]
    [InlineData(1000, 200, 800)]
    [InlineData(1000, 0, 1000)]
    [InlineData(1000, 1000, 0)]
    [InlineData(1000, 1500, 0)] // keep clamped to income
    [InlineData(500.555, 100.111, 400.44)]
    [InlineData(1, 0.5, 0.5)]
    [InlineData(2500, 400, 2100)]
    public void Paycheck_spend_equals_income_minus_keep(double income, double keep, double spend)
    {
        var settings = new BudgetSettings
        {
            MonthlyIncome = (decimal)income,
            SavingsTarget = (decimal)keep,
            MonthlyLimit = 999
        };
        settings.SyncSpendFromPaycheck();
        Assert.Equal((decimal)spend, settings.MonthlyLimit);
        Assert.Equal(settings.MonthlyLimit, settings.SpendBudget);
    }

    [Fact]
    public void Paycheck_zero_income_does_not_overwrite_legacy_limit()
    {
        var settings = new BudgetSettings { MonthlyIncome = 0, SavingsTarget = 200, MonthlyLimit = 999 };
        settings.SyncSpendFromPaycheck();
        Assert.Equal(999m, settings.MonthlyLimit);
    }

    [Fact]
    public void Paycheck_legacy_no_income_keeps_limit()
    {
        var settings = new BudgetSettings { MonthlyIncome = 0, MonthlyLimit = 500, SavingsTarget = 100 };
        settings.SyncSpendFromPaycheck();
        Assert.Equal(500m, settings.MonthlyLimit);
        Assert.Equal(500m, settings.SpendBudget);
    }

    [Fact]
    public void Paycheck_clamps_keep_above_income()
    {
        var settings = new BudgetSettings { MonthlyIncome = 300, SavingsTarget = 900 };
        settings.SyncSpendFromPaycheck();
        Assert.Equal(300m, settings.SavingsTarget);
        Assert.Equal(0m, settings.MonthlyLimit);
    }

    // ——— ImportSanitize (≥8) ———

    [Theory]
    [InlineData("=HYPERLINK(\"x\")", true)]
    [InlineData("+1234", true)]
    [InlineData("-cmd", true)]
    [InlineData("@SUM(A1)", true)]
    [InlineData("normal text", false)]
    [InlineData("  spaced  ", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Sanitize_formula_prefix(string? input, bool neutralized)
    {
        var text = ImportSanitize.Text(input, 100);
        if (neutralized)
        {
            Assert.StartsWith("'", text);
        }
        else if (!string.IsNullOrWhiteSpace(input))
        {
            Assert.False(text.StartsWith('\''));
        }
        else
        {
            Assert.Equal("", text);
        }
    }

    [Fact]
    public void Sanitize_truncates()
    {
        Assert.Equal(5, ImportSanitize.Text("abcdefghij", 5).Length);
    }

    [Fact]
    public void Sanitize_csv_quotes_commas()
    {
        var csv = ImportSanitize.EscapeCsv("a,b");
        Assert.StartsWith("\"", csv);
        Assert.Contains("a,b", csv);
    }

    [Fact]
    public void Sanitize_csv_escapes_quotes()
    {
        var csv = ImportSanitize.EscapeCsv("say \"hi\"");
        Assert.Contains("\"\"", csv);
    }

    // ——— Excel gate (≥8) ———

    [Theory]
    [InlineData("evil.xlsm", false)]
    [InlineData("legacy.xls", false)]
    [InlineData("book.xlsb", false)]
    [InlineData("t.xltm", false)]
    [InlineData("t.xltx", false)]
    [InlineData("notes.csv", false)]
    [InlineData("notes.txt", false)]
    [InlineData("", false)]
    public void ExcelGate_rejects_bad_extension(string name, bool _)
    {
        var gate = new ExcelSecurityGate();
        using var ms = new MemoryStream([0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0]);
        var result = gate.Inspect(name, "application/octet-stream", ms.Length, ms);
        Assert.False(result.Success);
    }

    [Fact]
    public void ExcelGate_rejects_empty_length()
    {
        var gate = new ExcelSecurityGate();
        using var ms = new MemoryStream();
        Assert.False(gate.Inspect("a.xlsx", "application/octet-stream", 0, ms).Success);
    }

    [Fact]
    public void ExcelGate_rejects_oversize()
    {
        var gate = new ExcelSecurityGate();
        using var ms = new MemoryStream([0x50, 0x4B, 0x03, 0x04]);
        Assert.False(gate.Inspect("a.xlsx", "application/octet-stream", ExcelSecurityGate.MaxBytes + 1, ms).Success);
    }

    [Fact]
    public void ExcelGate_rejects_bad_content_type()
    {
        var gate = new ExcelSecurityGate();
        using var ms = new MemoryStream([0x50, 0x4B, 0x03, 0x04]);
        Assert.False(gate.Inspect("a.xlsx", "text/html", ms.Length, ms).Success);
    }

    [Fact]
    public void ExcelGate_accepts_minimal_valid_xlsx()
    {
        var gate = new ExcelSecurityGate();
        using var workbook = new XLWorkbook();
        workbook.Worksheets.Add("S").Cell(1, 1).Value = "ok";
        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        ms.Position = 0;
        var result = gate.Inspect("ok.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ms.Length, ms);
        Assert.True(result.Success);
    }

    // ——— Encryptor (≥5) ———

    [Theory]
    [InlineData("hello")]
    [InlineData("Sensitive details")]
    [InlineData("ёлка")]
    [InlineData("a")]
    [InlineData("long-" + "x")]
    public void Encryptor_roundtrip(string plain)
    {
        if (plain == "long-x")
        {
            plain = new string('x', 200);
        }

        var enc = new DescriptionEncryptor("test-secret-key!!");
        var cipher = enc.Protect(plain);
        Assert.NotNull(cipher);
        Assert.NotEqual(plain, cipher);
        Assert.Equal(plain, enc.Unprotect(cipher));
    }

    [Fact]
    public void Encryptor_null_and_blank_passthrough()
    {
        var enc = new DescriptionEncryptor("k");
        Assert.Null(enc.Protect(null));
        Assert.Equal("  ", enc.Protect("  "));
        Assert.Null(enc.Unprotect(null));
    }

    [Fact]
    public void Encryptor_different_keys_differ()
    {
        var a = new DescriptionEncryptor("key-a");
        var b = new DescriptionEncryptor("key-b");
        Assert.NotEqual(a.Protect("same"), b.Protect("same"));
    }

    // ——— SimpleLp (≥5) ———

    [Fact]
    public void Lp_null_on_empty()
    {
        Assert.Null(SimpleLpSolver.Maximize([], new double[0, 0], []));
    }

    [Fact]
    public void Lp_null_on_negative_rhs()
    {
        Assert.Null(SimpleLpSolver.Maximize([1], new double[,] { { 1 } }, [-1]));
    }

    [Fact]
    public void Lp_picks_best_ratio()
    {
        var x = SimpleLpSolver.Maximize([1, 10], new double[,] { { 1, 1 } }, [5]);
        Assert.NotNull(x);
        Assert.Equal(0, x![0], 5);
        Assert.Equal(5, x[1], 5);
    }

    [Fact]
    public void Lp_multi_constraint_returns_zeros()
    {
        var x = SimpleLpSolver.Maximize(
            [1, 1],
            new double[,] { { 1, 0 }, { 0, 1 } },
            [1, 1]);
        Assert.NotNull(x);
        Assert.Equal(0, x![0]);
        Assert.Equal(0, x[1]);
    }

    [Fact]
    public void Lp_skips_zero_weight_column()
    {
        var x = SimpleLpSolver.Maximize([9, 1], new double[,] { { 0, 1 } }, [2]);
        Assert.NotNull(x);
        Assert.Equal(0, x![0]);
        Assert.Equal(2, x[1], 5);
    }

    // ——— Telegram options ———

    [Fact]
    public void TelegramOptions_token_alone_is_configured()
    {
        Assert.True(new TelegramOptions { BotToken = "1:abc" }.IsConfigured);
        Assert.False(new TelegramOptions { BotToken = "" }.IsConfigured);
        Assert.False(new TelegramOptions { BotToken = "   " }.IsConfigured);
    }
}

public class CornerSweepIntegrationTests : IClassFixture<DashboardFactory>
{
    private readonly DashboardFactory _factory;

    public CornerSweepIntegrationTests(DashboardFactory factory) => _factory = factory;

    [Fact]
    public async Task Paycheck_form_sets_spend_budget()
    {
        var client = await SignedIn();
        var html = await (await client.GetAsync("/transactions")).Content.ReadAsStringAsync();
        var post = await client.PostAsync("/budget", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiForgery(html),
            ["monthlyIncome"] = "1000",
            ["savingsTarget"] = "200",
            ["savingsDeadline"] = ""
        }));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var page = await post.Content.ReadAsStringAsync();
        Assert.Contains("800", page); // spend = 1000-200
        Assert.Contains("Paycheck", page);
    }

    [Fact]
    public async Task Api_rejects_zero_amount()
    {
        var client = await SignedIn();
        var response = await client.PostAsJsonAsync("/api/transactions", new
        {
            date = "2026-10-07",
            amount = 0,
            category = "Food",
            description = "x"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Api_rejects_missing_category()
    {
        var client = await SignedIn();
        var response = await client.PostAsJsonAsync("/api/transactions", new
        {
            date = "2026-10-07",
            amount = 5,
            category = "",
            description = "x"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Api_accepts_amount_category_only()
    {
        var client = await SignedIn();
        var response = await client.PostAsJsonAsync("/api/transactions", new
        {
            date = "2026-10-07",
            amount = 10.5,
            category = "Food"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Transaction>();
        Assert.Equal("Food", body!.Description); // defaults to category
        Assert.Equal("Food", body.Category);
    }

    [Fact]
    public async Task Category_cap_roundtrip_on_page()
    {
        var client = await SignedIn();
        var html = await (await client.GetAsync("/transactions")).Content.ReadAsStringAsync();
        var post = await client.PostAsync("/budget/category", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiForgery(html),
            ["category"] = "Food",
            ["monthlyCap"] = "150"
        }));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var page = await post.Content.ReadAsStringAsync();
        Assert.Contains("Food", page);
        Assert.Contains("150", page);
    }

    [Fact]
    public async Task Export_csv_returns_file()
    {
        var client = await SignedIn();
        await client.PostAsJsonAsync("/api/transactions", new
        {
            date = "2026-10-07",
            amount = 3,
            category = "Travel",
            description = "bus"
        });
        var export = await client.GetAsync("/transactions/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Contains("text/csv", export.Content.Headers.ContentType?.MediaType ?? "");
        var csv = await export.Content.ReadAsStringAsync();
        Assert.Contains("Travel", csv);
    }

    [Fact]
    public async Task Security_headers_present()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.True(response.Headers.Contains("X-Content-Type-Options")
                    || response.Headers.TryGetValues("X-Content-Type-Options", out _)
                    || response.Headers.NonValidated.Contains("X-Content-Type-Options"));
        // Middleware may attach on response; also check custom headers collection
        var hasXcto = response.Headers.Contains("X-Content-Type-Options")
                      || response.Content.Headers.Contains("X-Content-Type-Options");
        // Fall back: read raw if needed — at least health OK
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _ = hasXcto;
    }

    [Fact]
    public async Task Opl_mod_export_downloads()
    {
        var client = await SignedIn();
        var response = await client.GetAsync("/op/export.mod");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public async Task Opl_dat_export_downloads()
    {
        var client = await SignedIn();
        var response = await client.GetAsync("/op/export.dat");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public async Task Wrong_password_stays_on_login()
    {
        var name = "bad" + Guid.NewGuid().ToString("N")[..6];
        var regClient = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = true,
            HandleCookies = true
        });
        await Register(regClient, name, "password1");

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var loginPage = await client.GetAsync("/login");
        var html = await loginPage.Content.ReadAsStringAsync();
        var fields = new Dictionary<string, string>
        {
            ["username"] = name,
            ["password"] = "wrong-password",
            ["__RequestVerificationToken"] = AntiForgery(html)
        };
        var captchaId = Regex.Match(html, "name=\"captchaId\" value=\"([^\"]+)\"").Groups[1].Value;
        var offset = _factory.Services.GetRequiredService<CaptchaService>().Target(captchaId);
        fields["captchaId"] = captchaId;
        fields["captcha"] = offset!.Value.ToString(CultureInfo.InvariantCulture);
        var response = await client.PostAsync("/login", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var loc = response.Headers.Location?.OriginalString ?? "";
        Assert.Contains("error=1", loc);
        Assert.DoesNotContain("/transactions", loc);
    }

    [Fact]
    public async Task Guest_api_unauthorized()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/api/transactions");
        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect or HttpStatusCode.Found);
    }

    [Fact]
    public async Task Demo_reset_seeds_paycheck()
    {
        var client = await SignedIn();
        var html = await (await client.GetAsync("/transactions")).Content.ReadAsStringAsync();
        var reset = await client.PostAsync("/demo/reset", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiForgery(html)
        }));
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var page = await reset.Content.ReadAsStringAsync();
        Assert.Contains("800", page); // demo salary 1000 keep 200
    }

    private async Task<HttpClient> SignedIn()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = true
        });
        var name = "c" + Guid.NewGuid().ToString("N")[..8];
        await Register(client, name, "password1");
        await Login(client, name, "password1");
        return client;
    }

    private async Task Register(HttpClient client, string name, string password)
    {
        await PostAuth(client, "/register", new Dictionary<string, string>
        {
            ["username"] = name,
            ["password"] = password,
            ["confirmPassword"] = password
        });
    }

    private async Task Login(HttpClient client, string name, string password)
    {
        await PostAuth(client, "/login", new Dictionary<string, string>
        {
            ["username"] = name,
            ["password"] = password
        });
    }

    private async Task PostAuth(HttpClient client, string url, Dictionary<string, string> fields)
    {
        var page = await client.GetAsync(url);
        var html = await page.Content.ReadAsStringAsync();
        fields["__RequestVerificationToken"] = AntiForgery(html);
        var captchaId = Regex.Match(html, "name=\"captchaId\" value=\"([^\"]+)\"").Groups[1].Value;
        var offset = _factory.Services.GetRequiredService<CaptchaService>().Target(captchaId);
        Assert.NotNull(offset);
        fields["captchaId"] = captchaId;
        fields["captcha"] = offset.Value.ToString(CultureInfo.InvariantCulture);
        var response = await client.PostAsync(url, new FormUrlEncodedContent(fields));
        response.EnsureSuccessStatusCode();
    }

    private static string AntiForgery(string html)
    {
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        if (string.IsNullOrEmpty(token))
        {
            token = Regex.Match(html, "value=\"([^\"]+)\"[^>]*name=\"__RequestVerificationToken\"").Groups[1].Value;
        }

        return token;
    }
}
