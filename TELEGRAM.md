# Telegram bot setup

**Revoke any token that was ever committed to git or shared in chat.** Open [@BotFather](https://t.me/BotFather) → `/revoke` → create a new token.

1. Open BotFather → `/newbot` → copy the **new** token.
2. From `Dashboard.Api`:

```powershell
dotnet user-secrets set "Telegram:BotToken" "PASTE_TOKEN_HERE"
dotnet user-secrets set "Telegram:AllowedChatId" "0"
```

3. Start the app (`dotnet run --launch-profile http`).
4. Message the bot `/start` — it replies with your **chat id**.
5. Bind chat + Goal Keeper account (account id is on Advanced → System after login):

```powershell
dotnet user-secrets set "Telegram:AllowedChatId" "123456789"
dotnet user-secrets set "Telegram:BoundAccountId" "1"
```

6. Restart the app.

`appsettings.json` keeps empty Telegram fields on purpose — never put real tokens there.

Send:

```text
4.50 coffee late for train
12,30 taxi - rain
```

Clear parses become transactions for the bound account. Unclear lines go to **Advanced → Inbox**.

Bot only runs while Dashboard.Api is running (long polling). If the token is missing, the host stays idle (no 401 spam).
