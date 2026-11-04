using System.Globalization;
using Dashboard.Api.Auth;
using Dashboard.Api.Budget;
using Dashboard.Api.Data;
using Dashboard.Api.Demo;
using Dashboard.Api.Import;
using Dashboard.Api.Ops;
using Dashboard.Api.Security;
using Dashboard.Api.SystemInfo;
using Dashboard.Api.Telegram;
using Dashboard.Api.Transactions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Localization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var invariant = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentCulture = invariant;
CultureInfo.DefaultThreadCurrentUICulture = invariant;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Dashboard") ?? "Data Source=dashboard.db";
var encryptionKey = builder.Configuration["Encryption:Key"]
    ?? Environment.GetEnvironmentVariable("APP_ENCRYPTION_KEY")
    ?? "changeit12345678";
if (string.IsNullOrWhiteSpace(encryptionKey))
{
    encryptionKey = "changeit12345678";
}

builder.Services.AddSingleton(new DescriptionEncryptor(encryptionKey));
builder.Services.AddDbContext<DashboardDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddScoped<AccountContext>();
builder.Services.AddScoped<TransactionService>();
builder.Services.AddScoped<BudgetService>();
builder.Services.AddScoped<IBudgetNotifier, TelegramBudgetNotifier>();
builder.Services.AddScoped<ExcelSecurityGate>();
builder.Services.AddScoped<ExcelTransactionImporter>();
builder.Services.AddScoped<OpTemplates>();
builder.Services.AddScoped<OpOplExport>();
builder.Services.AddScoped<DemoDataService>();
builder.Services.AddScoped<AppStatusService>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<CaptchaService>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddScoped<AccountService>();
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.SectionName));
builder.Services.AddHttpClient(nameof(TelegramApiClient));
builder.Services.AddSingleton<TelegramBotService>();
builder.Services.AddHostedService<TelegramBotHostedService>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.Cookie.Name = "dashboard.auth";
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture(invariant);
    options.SupportedCultures = [invariant];
    options.SupportedUICultures = [invariant];
});
builder.Services.AddControllersWithViews();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler(handler =>
{
    handler.Run(async context =>
    {
        var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
        if (exception is TransactionNotFoundException notFound)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = notFound.Message });
            return;
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = "Unexpected error" });
    });
});

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseRequestLocalization();
app.UseStaticFiles();
app.UseAuthentication();
app.UseMiddleware<AccountContextMiddleware>();
app.UseAuthorization();
app.MapControllers();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DashboardDbContext>();
    db.Database.EnsureCreated();
    EnsureSchema(db);

    if (app.Configuration.GetValue<bool>("SeedDemo") && !db.Transactions.Any())
    {
        // Legacy seed without account — skipped when isolation is on; use Advanced → Reset demo.
    }
}

app.Run();

static void EnsureSchema(DashboardDbContext db)
{
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "Accounts" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Accounts" PRIMARY KEY AUTOINCREMENT,
            "Username" TEXT NOT NULL,
            "PasswordHash" TEXT NOT NULL,
            "CreatedAt" TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Accounts_Username" ON "Accounts" ("Username");
        """);
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "InboxEntries" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_InboxEntries" PRIMARY KEY AUTOINCREMENT,
            "AccountId" INTEGER NOT NULL DEFAULT 0,
            "RawText" TEXT NOT NULL,
            "Date" TEXT NOT NULL,
            "Amount" TEXT NULL,
            "Category" TEXT NULL,
            "Description" TEXT NULL,
            "Why" TEXT NULL,
            "CreatedAt" TEXT NOT NULL
        );
        """);
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "BudgetSettings" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_BudgetSettings" PRIMARY KEY AUTOINCREMENT,
            "AccountId" INTEGER NOT NULL DEFAULT 0,
            "MonthlyIncome" TEXT NOT NULL DEFAULT '0',
            "MonthlyLimit" TEXT NOT NULL,
            "SavingsTarget" TEXT NOT NULL DEFAULT '0',
            "YearGoal" TEXT NOT NULL DEFAULT '0',
            "YearGoalName" TEXT NULL,
            "SavingsDeadline" TEXT NULL,
            "AlertYearMonth" INTEGER NOT NULL,
            "AlertLevel" INTEGER NOT NULL,
            "WeeklyAlertYearWeek" INTEGER NOT NULL DEFAULT 0,
            "WeeklyAlertLevel" INTEGER NOT NULL DEFAULT 0
        );
        """);
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "BudgetCategoryCaps" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_BudgetCategoryCaps" PRIMARY KEY AUTOINCREMENT,
            "AccountId" INTEGER NOT NULL DEFAULT 0,
            "Category" TEXT NOT NULL,
            "MonthlyCap" TEXT NOT NULL,
            "AlertYearMonth" INTEGER NOT NULL,
            "OverAlertSent" INTEGER NOT NULL
        );
        """);
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "ImportBatches" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_ImportBatches" PRIMARY KEY AUTOINCREMENT,
            "AccountId" INTEGER NOT NULL,
            "FileName" TEXT NOT NULL,
            "AcceptedRows" INTEGER NOT NULL,
            "RejectedRows" INTEGER NOT NULL,
            "CreatedAt" TEXT NOT NULL
        );
        """);
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "OpScenarios" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_OpScenarios" PRIMARY KEY AUTOINCREMENT,
            "AccountId" INTEGER NOT NULL,
            "Slot" TEXT NOT NULL,
            "Template" TEXT NOT NULL,
            "Title" TEXT NOT NULL,
            "ResultJson" TEXT NOT NULL,
            "SavedAt" TEXT NOT NULL
        );
        """);

    var connection = db.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open)
    {
        connection.Open();
    }

    EnsureColumn(connection, "Transactions", "Why", """ALTER TABLE "Transactions" ADD COLUMN "Why" TEXT NULL""");
    EnsureColumn(connection, "Transactions", "AccountId", """ALTER TABLE "Transactions" ADD COLUMN "AccountId" INTEGER NOT NULL DEFAULT 0""");
    EnsureColumn(connection, "Transactions", "ImportBatchId", """ALTER TABLE "Transactions" ADD COLUMN "ImportBatchId" INTEGER NULL""");
    EnsureColumn(connection, "InboxEntries", "AccountId", """ALTER TABLE "InboxEntries" ADD COLUMN "AccountId" INTEGER NOT NULL DEFAULT 0""");
    EnsureColumn(connection, "BudgetSettings", "AccountId", """ALTER TABLE "BudgetSettings" ADD COLUMN "AccountId" INTEGER NOT NULL DEFAULT 0""");
    EnsureColumn(connection, "BudgetSettings", "MonthlyIncome", """ALTER TABLE "BudgetSettings" ADD COLUMN "MonthlyIncome" TEXT NOT NULL DEFAULT '0'""");
    EnsureColumn(connection, "BudgetSettings", "SavingsTarget", """ALTER TABLE "BudgetSettings" ADD COLUMN "SavingsTarget" TEXT NOT NULL DEFAULT '0'""");
    EnsureColumn(connection, "BudgetSettings", "YearGoal", """ALTER TABLE "BudgetSettings" ADD COLUMN "YearGoal" TEXT NOT NULL DEFAULT '0'""");
    EnsureColumn(connection, "BudgetSettings", "YearGoalName", """ALTER TABLE "BudgetSettings" ADD COLUMN "YearGoalName" TEXT NULL""");
    EnsureColumn(connection, "BudgetSettings", "SavingsDeadline", """ALTER TABLE "BudgetSettings" ADD COLUMN "SavingsDeadline" TEXT NULL""");
    EnsureColumn(connection, "BudgetSettings", "WeeklyAlertYearWeek", """ALTER TABLE "BudgetSettings" ADD COLUMN "WeeklyAlertYearWeek" INTEGER NOT NULL DEFAULT 0""");
    EnsureColumn(connection, "BudgetSettings", "WeeklyAlertLevel", """ALTER TABLE "BudgetSettings" ADD COLUMN "WeeklyAlertLevel" INTEGER NOT NULL DEFAULT 0""");
    EnsureColumn(connection, "BudgetCategoryCaps", "AccountId", """ALTER TABLE "BudgetCategoryCaps" ADD COLUMN "AccountId" INTEGER NOT NULL DEFAULT 0""");

    // Assign orphan rows to first account when present.
    var firstAccountId = db.Accounts.OrderBy(a => a.Id).Select(a => a.Id).FirstOrDefault();
    if (firstAccountId > 0)
    {
        db.Database.ExecuteSqlRaw(
            """UPDATE "Transactions" SET "AccountId" = {0} WHERE "AccountId" = 0""",
            firstAccountId);
        db.Database.ExecuteSqlRaw(
            """UPDATE "InboxEntries" SET "AccountId" = {0} WHERE "AccountId" = 0""",
            firstAccountId);
        db.Database.ExecuteSqlRaw(
            """UPDATE "BudgetSettings" SET "AccountId" = {0} WHERE "AccountId" = 0""",
            firstAccountId);
        db.Database.ExecuteSqlRaw(
            """UPDATE "BudgetCategoryCaps" SET "AccountId" = {0} WHERE "AccountId" = 0""",
            firstAccountId);
    }

    try
    {
        db.Database.ExecuteSqlRaw(
            """CREATE UNIQUE INDEX IF NOT EXISTS "IX_BudgetSettings_AccountId" ON "BudgetSettings" ("AccountId")""");
    }
    catch (SqliteException)
    {
        // duplicates from old singleton rows — ignore for demo DB
    }
}

static void EnsureColumn(System.Data.Common.DbConnection connection, string table, string column, string alterSql)
{
    using var command = connection.CreateCommand();
    command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
    var exists = Convert.ToInt32(command.ExecuteScalar()) > 0;
    if (!exists)
    {
        using var alter = connection.CreateCommand();
        alter.CommandText = alterSql;
        alter.ExecuteNonQuery();
    }
}

public partial class Program;
