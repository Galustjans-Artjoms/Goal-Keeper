# Goal Keeper

Neon cyber expense dashboard on ASP.NET Core — быстрый ввод трат, Goals, Analysis, OP-шаблоны, Excel-импорт и опциональный Telegram-inbox.

Портфолио-эволюция [Dashboard](https://github.com/Galustjans-Artjoms/Dashboard) ·  
репо: [Galustjans-Artjoms/Goal-Keeper](https://github.com/Galustjans-Artjoms/Goal-Keeper)

![Goal Keeper — главный экран](docs/images/goal-keeper-ui.jpg)

<p align="center">
  <img src="docs/images/goal-keeper-login.png" alt="Sign in" width="280" />
  &nbsp;
  <img src="docs/images/goal-keeper-goals.png" alt="Goals" width="220" />
  &nbsp;
  <img src="docs/images/goal-keeper-op.png" alt="OP" width="220" />
</p>

![Advanced / System](docs/images/goal-keeper-advanced.jpg)

## Что умеет

| Блок | Зачем |
|------|--------|
| **Face form** | Добавить трату (Amount / Category / What / Why), сразу видно spent / leftover |
| **Analysis** | Paycheck (salary − keep = spend budget), roadmap, категории месяца |
| **Goals** | Year goal + category caps |
| **OP** | LP-шаблоны (hit limit / cut to green…) → Solve, сценарии A/B, export `.mod` / `.dat` |
| **Advanced** | Excel import, Inbox, фильтры, System (DB / Telegram status / Reset demo) |
| **Telegram** | Пишешь сумму боту → транзакция или Inbox *(пока крутится API)* |

## Стек

- .NET 10 · ASP.NET Core MVC + API  
- EF Core · SQLite  
- Cookie auth · AES для What/Why  
- ClosedXML · Telegram long-poll (опционально)

## Требования

- [.NET 10 SDK](https://dotnet.microsoft.com/download)  
- (опционально) бот у [@BotFather](https://t.me/BotFather)

## Установка и запуск

```powershell
git clone https://github.com/Galustjans-Artjoms/Goal-Keeper.git
cd Goal-Keeper

cd Dashboard.Api
dotnet user-secrets set "Encryption:Key" "your-16-plus-char-key"
cd ..

dotnet run --project Dashboard.Api --launch-profile http
```

Открой **http://localhost:5011**

1. **Register** → аккаунт (пароль ≥ 8 символов), пройди puzzle-captcha  
2. **Sign in**  
3. Справа вкладки: **Analysis** → задай paycheck · **Goals** · **OP** · **Advanced**  
4. **Advanced → Reset demo data** — если нужны демо-траты  
5. На face form жми **ADD**

Живость: `GET http://localhost:5011/health`

## Секреты (не коммитить)

В `appsettings.json` Telegram и Encryption **пустые**. Значения только через user-secrets:

```powershell
cd Dashboard.Api
dotnet user-secrets set "Encryption:Key" "your-16-plus-char-key"
dotnet user-secrets set "Telegram:BotToken" ""
dotnet user-secrets set "Telegram:AllowedChatId" "0"
dotnet user-secrets set "Telegram:BoundAccountId" "0"
```

- Токен бота **никогда** не клади в git / README / скрины.  
- Если токен светился — BotFather → **Revoke**, новый только в user-secrets.  
- `dashboard.db` в `.gitignore`.  
- `BoundAccountId` смотри в **Advanced → System** после логина.

Подробно: [TELEGRAM.md](TELEGRAM.md).

## Telegram (кратко)

1. BotFather → токен → `Telegram:BotToken`  
2. Запусти API → боту `/start` → chat id → `AllowedChatId`  
3. `BoundAccountId` из Advanced → System  
4. Пиши: `4.50 coffee late for train`  

Бот живёт только пока запущен `Dashboard.Api`.

## Тесты / CI

```powershell
dotnet test
```

GitHub Actions: `.github/workflows/ci.yml`.

## Архитектура

```text
Face form ──► Transactions ──► Forecast / Goals / Analysis
Excel ──► Security gate ──► Preview ──► Commit
OP templates ──► caps / leftover ──► Solve · A/B · .mod/.dat
Telegram ──► ledger / Inbox
```

## API

`GET/POST/PUT/DELETE /api/transactions` (cookie).  
Фильтры: `fromDate`, `toDate`, `minAmount`, `maxAmount`, `category`, `descriptionContains`.

## Безопасность

- Изоляция данных по аккаунту  
- Excel: только `.xlsx`, лимиты, preview до commit  
- Login throttle + puzzle captcha · security headers · encryption at rest  
