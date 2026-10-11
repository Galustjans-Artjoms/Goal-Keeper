# Goal Keeper (Dashboard.Net)

Neon cyber expense dashboard in ASP.NET Core — face entry, budget Goals, **OP** (operations research templates), Excel import with a security gate, Telegram inbox.

C# portfolio evolution of [Dashboard](https://github.com/Galustjans-Artjoms/Dashboard).

## Stack

- .NET 10 · ASP.NET Core MVC + API
- EF Core · SQLite (per-account ledger)
- Cookie auth · AES description encryption
- ClosedXML `.xlsx` import · Telegram long-poll (optional)

## 60-second demo

1. `dotnet run --project Dashboard.Api --launch-profile http`
2. Open `http://localhost:5011` → register → sign in
3. **Advanced → Reset demo data**
4. Face form: add a spend; watch month leftover / year pace / savings pace
5. **Goals**: monthly limit + category caps + savings goal
6. **OP**: solve *Hit month limit* / *Cut to green* / *Pace vs year* → save A/B → optional Apply caps
7. **Advanced**: download `template.xlsx`, Preview → Commit (security gate + audit trail)
8. `GET /health` for status JSON

## Secrets (do not commit)

```powershell
cd Dashboard.Api
dotnet user-secrets set "Encryption:Key" "your-16-plus-char-key"
dotnet user-secrets set "Telegram:BotToken" ""
dotnet user-secrets set "Telegram:AllowedChatId" "0"
dotnet user-secrets set "Telegram:BoundAccountId" "0"
```

If a Telegram token was ever in git or chat history: **revoke it in BotFather** and set a new one via user-secrets only. See [TELEGRAM.md](TELEGRAM.md).

## Architecture (interview)

```text
Face form ──► Transactions (AccountId) ──► Forecast / Goals
Excel ──► Security gate ──► Preview ──► Commit + ImportBatch audit
OP templates ──► live spend + caps ──► advice / apply caps / A·B scenarios
Telegram ──► BoundAccountId ledger / Inbox
```

## API

`GET/POST/PUT/DELETE /api/transactions` (auth cookie). Filters: `fromDate`, `toDate`, `minAmount`, `maxAmount`, `category`, `descriptionContains`.

## Test / CI

```powershell
dotnet test
```

GitHub Actions runs `dotnet test` on push (see `.github/workflows/ci.yml`).

## Security notes (portfolio talking points)

- Per-account isolation on ledger, budget, inbox, imports, OP scenarios
- Excel: `.xlsx` only, magic-byte + zip-bomb guards, size/row caps, memory parse, preview before commit, formula-injection sanitization on export
- Login throttle + captcha · security headers · encryption at rest for What/Why
