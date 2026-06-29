# CustomWB_MCP

MCP-сервер на C# (ASP.NET Core), который поднимает Streamable HTTP MCP-эндпоинт и подключается
к Claude / Cowork как кастомный коннектор (remote MCP).

Текущий этап: **реальный Wildberries API в режиме только чтения** (Statistics API). Моки удалены.

> Read-only. Никаких записывающих операций. Токен — категории «Статистика», доступ «Только чтение»,
> хранится в user-secrets и наружу (в код, git, Claude) не передаётся.

## Статус

| Шаг | Состояние |
|-----|-----------|
| Структура проекта + Streamable HTTP `/mcp` | ✅ |
| `ping` через Claude (на заглушках) | ✅ ранее проверено |
| Моки удалены, добавлен реальный WB Statistics API | ✅ |
| `dotnet build` (Release) | ✅ 0 ошибок, 0 предупреждений |
| Инструменты регистрируются (`ping`, `wb_get_sales`, `wb_get_stocks`) | ✅ |
| WB-токен в user-secrets | ✅ |
| Реальный вызов WB без 401/429 | ✅ `wb_get_stocks` (211 строк → сводка) и `wb_get_sales` (продажи за неделю) вернули реальные данные |
| Проверка через публичный HTTPS (ngrok) | ✅ |

## Структура

```
CustomWB_MCP/
├── CustomWB_MCP.csproj          # net8.0, UserSecretsId, ModelContextProtocol.AspNetCore 1.2.0
├── Program.cs                   # хост + именованный HttpClient "WbStatistics" (токен из конфигурации)
├── README.md                    # этот файл
└── Tools/
    ├── PingTool.cs              # ping — проверка связи
    └── WildberriesTools.cs      # wb_get_sales, wb_get_stocks — РЕАЛЬНЫЙ WB API (read-only)
```

## Инструменты

| Имя в MCP | Аргументы | Что делает |
|-----------|-----------|------------|
| `ping` | `message: string` | Возвращает `pong: <message>`. Проверка связи. |
| `wb_get_sales` | `period: "today" \| "week" \| "month"` (по умолчанию `week`) | `GET /api/v1/supplier/sales`. **Агрегирует на сервере**: число продаж, выручка (сумма `finishedPrice`), топ-5 SKU. Возвращает компактную сводку, не сырой массив. |
| `wb_get_stocks` | — | `GET /api/v1/supplier/stocks` (dateFrom = начало месяца). **Агрегирует**: остатки по складам + топ-10 SKU по доступному количеству. Компактная сводка. |

`period → dateFrom`: `today` → сегодня 00:00; `week` → 7 дней назад; `month` → начало текущего месяца. Формат RFC3339 без смещения (`2026-06-01T00:00:00`).

### Обработка ошибок (наружу — короткий текст, тело ошибки WB не отдаётся)
- `401` → «токен недействителен или категория не Статистика»
- `429` → «превышен лимит запросов WB, повторить позже» (у статистики лимит ~1 запрос/мин на метод)
- таймаут / `5xx` → «сервис WB недоступен, повторить позже»

## Требования

- .NET SDK **8+**. На этой машине .NET в `~/.dotnet` и **не в PATH** — перед командами:
  ```bash
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
  ```
- `ngrok` (установлен) для публичного HTTPS-туннеля — нужен для подключения Claude.

## Токен WB — через user-secrets (НЕ в код, НЕ в git)

Токен категории **«Статистика»**, доступ **«Только чтение»** (JWT, действует 180 дней).
Получить в личном кабинете WB → Настройки → Доступ к API.

```bash
cd CustomWB_MCP
dotnet user-secrets init                                  # уже сделано (UserSecretsId в csproj)
dotnet user-secrets set "Wildberries:Token" "<ТОКЕН>"
```

> Категория токена должна совпадать с категорией API, иначе WB вернёт 401.

## Запуск

```bash
cd CustomWB_MCP
dotnet restore   # один раз
dotnet run       # http://localhost:5000/mcp
```

## Локальная проверка

### MCP Inspector
```bash
npx @modelcontextprotocol/inspector
```
Транспорт **Streamable HTTP**, адрес `http://localhost:5000/mcp` → Connect → вызвать `wb_get_sales`, `wb_get_stocks`.

### curl
Streamable HTTP требует заголовок `Accept: application/json, text/event-stream`; ответ — SSE (`data: {...}`).
```bash
curl -s -X POST http://localhost:5000/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"wb_get_sales","arguments":{"period":"week"}}}'
```

## Публичный HTTPS + подключение в Claude

```bash
ngrok http 5000        # выдаст https://<sub>.ngrok-free.dev
```
MCP-эндпоинт для Claude = этот адрес + `/mcp`.

1. **Customize → Connectors → «+» → Add custom connector.**
2. Имя `CustomWB_MCP`, URL — ngrok-адрес с `/mcp`. OAuth не нужен → **Add**.
3. В диалоге: **«+»** внизу слева → **Connectors** → включить `CustomWB_MCP`. То же в Cowork.

Проверочные фразы: «Покажи продажи за неделю» → `wb_get_sales`; «Какие сейчас остатки» → `wb_get_stocks`.

> Бесплатный ngrok даёт новый адрес при каждом перезапуске — после рестарта обновить URL в коннекторе.

## Безопасность

- MCP-эндпоинт публичен через ngrok — кто знает URL, видит данные. Поэтому только **Read-Only токен
  категории Статистика**: даже при утечке записать им нельзя.
- Тест проводить короткой сессией, не оставлять туннель и сервер включёнными без присмотра.
- WB-токен — только в user-secrets, не в коде/логах/git.

## Следующий шаг (зафиксировано, сейчас не делать)

Добавить остальные read-only категории (Аналитика — воронка и поисковые запросы; Продвижение — ДРР/ROAS;
Цены и скидки; Контент; Отзывы). И только потом, с OAuth и подтверждением человеком, — записывающие операции
(ответы на отзывы, смена цен, участие в акциях).
