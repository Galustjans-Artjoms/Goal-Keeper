using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Dashboard.Api.Auth;
using Dashboard.Api.Import;
using Dashboard.Api.Ops;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Dashboard.Tests;

public class PortfolioTests : IClassFixture<DashboardFactory>
{
    private readonly DashboardFactory _factory;

    public PortfolioTests(DashboardFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AccountsAreIsolated()
    {
        var a = await SignedInClient("alice");
        var b = await SignedInClient("bob");

        await a.PostAsJsonAsync("/api/transactions", new
        {
            date = "2026-10-01",
            amount = 11.11m,
            category = "Food",
            description = "Alice only"
        });

        var pageB = await b.GetFromJsonAsync<Dashboard.Api.Transactions.PagedTransactions>("/api/transactions");
        Assert.NotNull(pageB);
        Assert.DoesNotContain(pageB.Items, t => t.Description == "Alice only");

        var pageA = await a.GetFromJsonAsync<Dashboard.Api.Transactions.PagedTransactions>("/api/transactions");
        Assert.Contains(pageA!.Items, t => t.Description == "Alice only");
    }

    [Fact]
    public async Task HealthIsAnonymous()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("database", json);
    }

    [Fact]
    public void ExcelGateRejectsMacrosAndTinyGarbage()
    {
        var gate = new ExcelSecurityGate();
        using var junk = new MemoryStream("not-a-zip"u8.ToArray());
        var bad = gate.Inspect("evil.xlsm", "application/octet-stream", junk.Length, junk);
        Assert.False(bad.Success);

        using var garbage = new MemoryStream("PK\0\0"u8.ToArray());
        var badMagic = gate.Inspect("x.xlsx", "application/octet-stream", garbage.Length, garbage);
        Assert.False(badMagic.Success);
    }

    [Fact]
    public async Task ExcelPreviewAndCommitWorks()
    {
        var client = await SignedInClient("importer");
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Transactions");
        sheet.Cell(1, 1).Value = "Date";
        sheet.Cell(1, 2).Value = "Amount";
        sheet.Cell(1, 3).Value = "Category";
        sheet.Cell(1, 4).Value = "What";
        sheet.Cell(1, 5).Value = "Why";
        sheet.Cell(2, 1).Value = "2026-10-02";
        sheet.Cell(2, 2).Value = 3.25;
        sheet.Cell(2, 3).Value = "Food";
        sheet.Cell(2, 4).Value = "=HYPERLINK(\"http://evil\")";
        sheet.Cell(2, 5).Value = "test";
        await using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        ms.Position = 0;

        var page = await client.GetAsync("/transactions");
        var html = await page.Content.ReadAsStringAsync();
        var token = AntiForgery(html);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(token), "__RequestVerificationToken");
        var fileContent = new ByteArrayContent(ms.ToArray());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(fileContent, "file", "demo.xlsx");

        var preview = await client.PostAsync("/import/excel", form);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var previewHtml = await preview.Content.ReadAsStringAsync();
        Assert.Contains("Preview ready", previewHtml);
        Assert.Contains("Commit", previewHtml);

        var previewToken = Regex.Match(previewHtml, "name=\"previewToken\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(previewToken));
        var commitToken = AntiForgery(previewHtml);
        var commit = await client.PostAsync(
            "/import/excel/commit",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = commitToken,
                ["previewToken"] = previewToken
            }));
        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);

        var list = await client.GetFromJsonAsync<Dashboard.Api.Transactions.PagedTransactions>("/api/transactions");
        Assert.Contains(list!.Items, t => t.Amount == 3.25m && t.Category == "Food");
    }

    [Fact]
    public async Task OpSolveHitLimitReturnsPage()
    {
        var client = await SignedInClient("ops");
        await client.PostAsJsonAsync("/api/transactions", new
        {
            date = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd"),
            amount = 40m,
            category = "Food",
            description = "Lunch"
        });

        var page = await client.GetAsync("/transactions");
        var html = await page.Content.ReadAsStringAsync();
        var token = AntiForgery(html);

        // set budget via form
        await client.PostAsync("/budget", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiForgery(await (await client.GetAsync("/transactions")).Content.ReadAsStringAsync()),
            ["monthlyLimit"] = "500"
        }));

        page = await client.GetAsync("/transactions");
        token = AntiForgery(await page.Content.ReadAsStringAsync());
        var solve = await client.PostAsync("/op/solve", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["template"] = "hit-limit",
            ["yearLeftoverTarget"] = "0",
            ["extraCutPercent"] = "0"
        }));
        Assert.Equal(HttpStatusCode.OK, solve.StatusCode);
        var body = await solve.Content.ReadAsStringAsync();
        Assert.Contains("Hit month limit", body);
    }

    [Fact]
    public void SimpleLpSolverMaximizesUnderConstraint()
    {
        // max 3x + 5y s.t. x + y <= 4, x,y >= 0 → optimal objective 20 at (0,4)
        var result = SimpleLpSolver.Maximize(
            [3, 5],
            new double[,] { { 1, 1 } },
            [4]);
        Assert.NotNull(result);
        var objective = 3 * result![0] + 5 * result[1];
        Assert.InRange(objective, 19.5, 20.5);
    }

    [Fact]
    public void ImportSanitizeNeutralizesFormulaPrefix()
    {
        Assert.StartsWith("'", ImportSanitize.Text("=cmd", 100));
    }

    private async Task<HttpClient> SignedInClient(string prefix)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = true
        });
        var name = prefix + Guid.NewGuid().ToString("N")[..6];
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
