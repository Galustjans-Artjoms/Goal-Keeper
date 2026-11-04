# Telegram bot — setup & what it accepts

Goal Keeper Telegram integration is part of the **Alpha (&lt; 1.0)** app — parsing and commands may change.

**Revoke any token that was ever committed to git or shared in chat.**  
[@BotFather](https://t.me/BotFather) → `/revoke` → create a new token.

## Setup

1. BotFather → `/newbot` → copy the **new** token.
2. From `Dashboard.Api`:

```powershell
dotnet user-secrets set "Telegram:BotToken" "PASTE_TOKEN_HERE"
dotnet user-secrets set "Telegram:AllowedChatId" "0"
```

3. Start the app: `dotnet run --project Dashboard.Api --launch-profile http`
4. Message the bot `/start` — it replies with your **chat id** (while `AllowedChatId` is `0`).
5. Bind chat + Goal Keeper account (`BoundAccountId` is on **Advanced → System** after login):

```powershell
dotnet user-secrets set "Telegram:AllowedChatId" "123456789"
dotnet user-secrets set "Telegram:BoundAccountId" "1"
```

6. Restart the app.

`appsettings.json` keeps empty Telegram fields on purpose — never put real tokens there.

The bot only runs while `Dashboard.Api` is running (long polling). If the token is missing, the host stays idle (no 401 spam). Only one chat (`AllowedChatId`) is allowed.

---

## Commands

| Command | What happens |
|---------|----------------|
| `/start` · `/help` | Short usage help |
| `/id` | Prints your Telegram chat id |
| `/today` | Sum + count of **today’s** transactions for the bound account |
| `/add …` | Same as a plain expense line (the `/add` prefix is stripped) |

`/week` is reserved in the parser (not a full report yet).

---

## Expense messages (what gets posted to the ledger)

Send a **single text line**. High-confidence lines become a transaction dated **today**. Unclear lines go to **Advanced → Inbox**.

### Amount

- Required for a successful parse  
- Formats: `4.50` or `12,30` (comma or dot)  
- Must be **> 0**

### What (description)

- Words around the amount (e.g. `coffee`)  
- Needed for **high confidence** (amount alone is not enough → Inbox)

### Why (optional)

Any of these separators:

- ` - ` / ` — ` / ` | ` / ` · `  
- or ` because `

Examples:

```text
4.50 coffee late for train
12,30 taxi - rain
8 cafe | meeting
```

→ amount + What + Why

### Category (optional)

If the **first word** looks like a category, it is used as Category:

- `#Food`, `Food`, `Travel`, `General`, `Rent`, `Fun` (case-insensitive)

Examples:

```text
Food 10
#Travel 25.5 flight - client trip
```

Otherwise Category defaults to **General**.

### `/add` prefix

```text
/add 8 cafe | meeting
```

Same parsing rules after stripping `/add`.

---

## Examples

| You send | Result |
|----------|--------|
| `4.50 coffee late for train` | Transaction: €4.50, What=coffee, Why=late for train, Category=General |
| `12,30 taxi - rain` | €12.30, What=taxi, Why=rain |
| `Food 10` | €10, Category=Food, What=Food |
| `/add 8 cafe \| meeting` | €8, What=cafe, Why=meeting |
| `bought snacks earlier` | **Inbox** (no amount) |
| `15` | **Inbox** (amount but no What) |

---

## Flow

```text
Telegram text
  → only AllowedChatId
  → commands (/start, /today, …) OR ExpenseMessageParser
  → high confidence → Create transaction (BoundAccountId, today)
  → otherwise → Inbox (Advanced → Inbox)
```
