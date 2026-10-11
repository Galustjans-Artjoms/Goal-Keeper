# Telegram bot + Inbox — Design

Date: 2026-10-07

## Goal

Run a personal Telegram bot inside Dashboard.Api. User pastes `Telegram:BotToken` and `Telegram:AllowedChatId`; while the app runs, messages become transactions or Inbox items.

## Config

- `Telegram:BotToken` — from BotFather (User Secrets / env / Development settings)
- `Telegram:AllowedChatId` — numeric chat id; `/start` tells the user their id if unset
- Bot disabled cleanly when token is empty (no crash)

## Flow

1. Long polling `getUpdates` in a `BackgroundService`
2. Ignore chats not in whitelist (reply once with chat id if whitelist empty/mismatch)
3. Parse text → amount / what / why / category default General
4. High confidence (amount + what) → create Transaction, reply Saved
5. Low confidence → InboxEntry, reply Queued in Inbox
6. More → Inbox: Accept (create transaction) / Discard

## Non-goals

- Multi-user Telegram linking
- Always-online hosting
- NLP / LLM parsing
