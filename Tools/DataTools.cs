using System.ComponentModel;
using System.Text.Json;
using CustomWB_MCP.Wb;
using ModelContextProtocol.Server;

namespace CustomWB_MCP.Tools;

/// <summary>
/// Инструменты чтения данных WB (все read-only). Каждый возвращает компактный JSON:
/// данные + паспорт источника. Интерпретацию делает Claude.
/// </summary>
[McpServerToolType]
public sealed class DataTools
{
    private readonly WbGateway _wb;
    public DataTools(WbGateway wb) => _wb = wb;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static string Dump(object o) => JsonSerializer.Serialize(o, Json);

    [McpServerTool, Description("Полный каталог товаров WB (включая товары без продаж): nmID, артикул, название, предмет, бренд, размеры. С паспортом данных.")]
    public async Task<string> WbGetCatalog(CancellationToken ct)
    {
        var r = await _wb.GetCatalogAsync(ct);
        return Dump(new { count = r.Data?.Count ?? 0, items = r.Data, passport = r.Passport });
    }

    [McpServerTool, Description("Сводка продаж за период (today/week/month): число продаж, выручка к перечислению после возвратов, топ-5 SKU. С паспортом данных.")]
    public async Task<string> WbGetSales(
        [Description("Период: today, week или month")] string period = "week",
        CancellationToken ct = default)
    {
        var r = await _wb.GetSalesAsync(period, ct);
        return Dump(new { sales = r.Data, passport = r.Passport });
    }

    [McpServerTool, Description("Остатки по складам и SKU: доступно к продаже, в пути к клиенту и обратно. С паспортом данных.")]
    public async Task<string> WbGetStocks(CancellationToken ct)
    {
        var r = await _wb.GetStocksAsync(ct);
        return Dump(new { count = r.Data?.Count ?? 0, rows = r.Data, passport = r.Passport });
    }

    [McpServerTool, Description("Финансовая детализация реализации за период (к перечислению, логистика, хранение, штрафы, удержания), агрегировано по SKU. Основа юнит-экономики. С паспортом данных.")]
    public async Task<string> WbGetFinance(
        [Description("Дата начала, YYYY-MM-DD")] string dateFrom,
        [Description("Дата конца, YYYY-MM-DD")] string dateTo,
        CancellationToken ct = default)
    {
        var from = DateTime.TryParse(dateFrom, out var f) ? f : DateTime.UtcNow.Date.AddDays(-28);
        var to = DateTime.TryParse(dateTo, out var t) ? t : DateTime.UtcNow.Date;
        var r = await _wb.GetFinanceAsync(from, to, ct);
        return Dump(new { rows = r.Data, passport = r.Passport });
    }

    [McpServerTool, Description("Текущие цены и скидки по SKU. С паспортом данных.")]
    public async Task<string> WbGetPrices(CancellationToken ct)
    {
        var r = await _wb.GetPricesAsync(ct);
        return Dump(new { count = r.Data?.Count ?? 0, rows = r.Data, passport = r.Passport });
    }

    [McpServerTool, Description("Воронка продаж по SKU за период (today/week/month): показы карточки, добавления в корзину, заказы, выкупы. Данные отстают на 20–30 мин. С паспортом данных.")]
    public async Task<string> WbGetFunnel(
        [Description("Период: today, week или month")] string period = "week",
        CancellationToken ct = default)
    {
        var r = await _wb.GetFunnelAsync(period, ct);
        return Dump(new { count = r.Data?.Count ?? 0, rows = r.Data, passport = r.Passport });
    }

    [McpServerTool, Description("Статистика рекламы за прошлые N дней по SKU: расход, показы, клики, заказы. Сегодняшний день не отдаётся. С паспортом данных.")]
    public async Task<string> WbGetAdStats(
        [Description("За сколько прошлых дней, напр. 7")] int daysBack = 7,
        CancellationToken ct = default)
    {
        var r = await _wb.GetAdStatsAsync(daysBack, ct);
        return Dump(new { count = r.Data?.Count ?? 0, rows = r.Data, passport = r.Passport });
    }

    [McpServerTool, Description("Отзывы по SKU: средний рейтинг и количество. С паспортом данных.")]
    public async Task<string> WbGetFeedbacks(CancellationToken ct)
    {
        var r = await _wb.GetFeedbacksAsync(ct);
        return Dump(new { count = r.Data?.Count ?? 0, rows = r.Data, passport = r.Passport });
    }

    [McpServerTool, Description("Заказы за период (today/week/month): число заказов, сумма заказов (НЕ выручка), отмены, топ-5 SKU. С паспортом данных.")]
    public async Task<string> WbGetOrders(
        [Description("Период: today, week или month")] string period = "week",
        CancellationToken ct = default)
    {
        var r = await _wb.GetOrdersAsync(period, ct);
        return Dump(new { orders = r.Data, passport = r.Passport });
    }

    [McpServerTool, Description("Поставки и товар в пути за период: по SKU, складу, количеству и статусу. С паспортом данных.")]
    public async Task<string> WbGetIncomes(
        [Description("Период: today, week или month")] string period = "month",
        CancellationToken ct = default)
    {
        var r = await _wb.GetIncomesAsync(period, ct);
        return Dump(new { count = r.Data?.Count ?? 0, rows = r.Data, passport = r.Passport });
    }

    [McpServerTool, Description("Остатки по складам и оборачиваемость (асинхронный отчёт WB): по каждому SKU количество в разрезе складов. Для распределения отгрузок. С паспортом данных.")]
    public async Task<string> WbGetWarehouseRemains(CancellationToken ct)
    {
        var r = await _wb.GetWarehouseRemainsAsync(ct);
        return Dump(new { count = r.Data?.Count ?? 0, rows = r.Data, passport = r.Passport });
    }

    [McpServerTool, Description("Платное хранение за период (асинхронный отчёт WB), агрегировано по SKU. Расход для юнит-экономики. С паспортом данных.")]
    public async Task<string> WbGetPaidStorage(
        [Description("Дата начала, YYYY-MM-DD")] string dateFrom,
        [Description("Дата конца, YYYY-MM-DD")] string dateTo,
        CancellationToken ct = default)
    {
        var from = DateTime.TryParse(dateFrom, out var f) ? f : DateTime.UtcNow.Date.AddDays(-28);
        var to = DateTime.TryParse(dateTo, out var t) ? t : DateTime.UtcNow.Date;
        var r = await _wb.GetPaidStorageAsync(from, to, ct);
        return Dump(new { count = r.Data?.Count ?? 0, rows = r.Data, passport = r.Passport });
    }

    [McpServerTool, Description("Еженедельный P&L из отчётов о реализации: по каждому недельному отчёту — выручка, комиссия, логистика, хранение, штрафы, результат ДО себестоимости. Чистая прибыль требует CSV себестоимости (join делает Claude). С паспортом данных.")]
    public async Task<string> WbGetWeeklyPnl(
        [Description("Дата начала, YYYY-MM-DD")] string dateFrom,
        [Description("Дата конца, YYYY-MM-DD")] string dateTo,
        CancellationToken ct = default)
    {
        var from = DateTime.TryParse(dateFrom, out var f) ? f : DateTime.UtcNow.Date.AddDays(-56);
        var to = DateTime.TryParse(dateTo, out var t) ? t : DateTime.UtcNow.Date;
        var r = await _wb.GetWeeklyPnlAsync(from, to, ct);
        return Dump(new { weeks = r.Data?.Count ?? 0, rows = r.Data, passport = r.Passport });
    }
}
