using System.ComponentModel;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;

namespace CustomWB_MCP.Tools;

/// <summary>
/// РЕАЛЬНЫЕ read-only инструменты Wildberries (Statistics API).
/// Токен берётся из конфигурации (user-secrets), в код/git/логи не попадает.
/// Сырой ответ WB может быть на тысячи строк — наружу отдаётся только агрегированная сводка,
/// чтобы не забивать контекст модели.
/// </summary>
[McpServerToolType]
public sealed class WildberriesTools
{
    private readonly IHttpClientFactory _httpFactory;

    public WildberriesTools(IHttpClientFactory httpFactory) => _httpFactory = httpFactory;

    private static readonly JsonSerializerOptions JsonOut = new() { WriteIndented = true };

    private static readonly JsonSerializerOptions JsonIn = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    [McpServerTool, Description("Продажи (выкупы) Wildberries за период из Statistics API. " +
                               "Возвращает агрегированную сводку: число продаж, выручку и топ-5 SKU. " +
                               "Не сырой массив, а компактную сводку.")]
    public async Task<string> WbGetSales(
        [Description("Период: 'today', 'week' или 'month'")] string period = "week")
    {
        var dateFrom = PeriodToDateFrom(period);
        var url = $"/api/v1/supplier/sales?dateFrom={Rfc3339(dateFrom)}";

        var (ok, body, error) = await GetAsync(url);
        if (!ok) return error!;

        List<SaleRow>? rows;
        try
        {
            rows = JsonSerializer.Deserialize<List<SaleRow>>(body!, JsonIn);
        }
        catch
        {
            return ErrorJson("не удалось разобрать ответ WB (продажи)");
        }

        rows ??= new List<SaleRow>();

        var totalRevenue = rows.Sum(r => r.FinishedPrice);
        var top = rows
            .GroupBy(r => new { r.NmId, Article = r.SupplierArticle ?? "" })
            .Select(g => new
            {
                nmId = g.Key.NmId,
                supplierArticle = g.Key.Article,
                brand = g.Select(x => x.Brand).FirstOrDefault(b => !string.IsNullOrEmpty(b)),
                subject = g.Select(x => x.Subject).FirstOrDefault(s => !string.IsNullOrEmpty(s)),
                sales = g.Count(),
                revenueRub = Math.Round(g.Sum(x => x.FinishedPrice), 2)
            })
            .OrderByDescending(x => x.sales)
            .Take(5)
            .ToArray();

        var summary = new
        {
            source = "WB Statistics API (real)",
            period,
            dateFrom = Rfc3339(dateFrom),
            totals = new
            {
                salesCount = rows.Count,
                revenueRub = Math.Round(totalRevenue, 2)
            },
            topSku = top
        };

        return JsonSerializer.Serialize(summary, JsonOut);
    }

    [McpServerTool, Description("Текущие остатки Wildberries из Statistics API. " +
                               "Возвращает агрегированную сводку по складам и топ SKU по доступному количеству. " +
                               "Не сырой массив, а компактную сводку.")]
    public async Task<string> WbGetStocks()
    {
        // Для остатков WB ждёт dateFrom; берём начало текущего месяца.
        var dateFrom = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var url = $"/api/v1/supplier/stocks?dateFrom={Rfc3339(dateFrom)}";

        var (ok, body, error) = await GetAsync(url);
        if (!ok) return error!;

        List<StockRow>? rows;
        try
        {
            rows = JsonSerializer.Deserialize<List<StockRow>>(body!, JsonIn);
        }
        catch
        {
            return ErrorJson("не удалось разобрать ответ WB (остатки)");
        }

        rows ??= new List<StockRow>();

        var byWarehouse = rows
            .GroupBy(r => r.WarehouseName ?? "—")
            .Select(g => new { warehouse = g.Key, available = g.Sum(x => x.Quantity) })
            .OrderByDescending(x => x.available)
            .ToArray();

        var topSku = rows
            .GroupBy(r => new { r.NmId, Article = r.SupplierArticle ?? "" })
            .Select(g => new
            {
                nmId = g.Key.NmId,
                supplierArticle = g.Key.Article,
                brand = g.Select(x => x.Brand).FirstOrDefault(b => !string.IsNullOrEmpty(b)),
                available = g.Sum(x => x.Quantity),
                inWayToClient = g.Sum(x => x.InWayToClient),
                inWayFromClient = g.Sum(x => x.InWayFromClient)
            })
            .OrderByDescending(x => x.available)
            .Take(10)
            .ToArray();

        var summary = new
        {
            source = "WB Statistics API (real)",
            totals = new
            {
                rows = rows.Count,
                totalAvailable = rows.Sum(r => r.Quantity),
                warehouses = byWarehouse.Length
            },
            byWarehouse,
            topSku
        };

        return JsonSerializer.Serialize(summary, JsonOut);
    }

    // ---- helpers ----

    /// <summary>Выполняет GET к WB. Возвращает (успех, тело, готовый JSON ошибки).</summary>
    private async Task<(bool ok, string? body, string? error)> GetAsync(string url)
    {
        var client = _httpFactory.CreateClient("WbStatistics");
        try
        {
            using var resp = await client.GetAsync(url);
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                return (true, body, null);
            }

            // Тело ошибки WB наружу не отдаём — только понятный короткий текст.
            var msg = resp.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "токен недействителен или категория не Статистика (401)",
                (HttpStatusCode)429 => "превышен лимит запросов WB, повторить позже (429)",
                >= HttpStatusCode.InternalServerError => "сервис WB недоступен, повторить позже (5xx)",
                _ => $"WB вернул ошибку ({(int)resp.StatusCode})"
            };
            return (false, null, ErrorJson(msg));
        }
        catch (TaskCanceledException)
        {
            return (false, null, ErrorJson("таймаут запроса к WB, повторить позже"));
        }
        catch (HttpRequestException)
        {
            return (false, null, ErrorJson("сервис WB недоступен, повторить позже"));
        }
    }

    private static string ErrorJson(string message) =>
        JsonSerializer.Serialize(new { source = "WB Statistics API (real)", error = message }, JsonOut);

    private static DateTime PeriodToDateFrom(string period) => period?.Trim().ToLowerInvariant() switch
    {
        "today" => DateTime.Today,
        "month" => new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1),
        _ => DateTime.Today.AddDays(-7) // week (по умолчанию)
    };

    // RFC3339 без смещения, как в примерах WB: 2026-06-01T00:00:00
    private static string Rfc3339(DateTime dt) => dt.ToString("yyyy-MM-ddTHH:mm:ss");

    // ---- модели ответов WB (только нужные поля) ----

    private sealed record SaleRow
    {
        public string? Date { get; init; }
        public string? SupplierArticle { get; init; }
        public long NmId { get; init; }
        public string? Brand { get; init; }
        public string? Subject { get; init; }
        public decimal TotalPrice { get; init; }
        public decimal PriceWithDisc { get; init; }
        public decimal FinishedPrice { get; init; }
        public double DiscountPercent { get; init; }
    }

    private sealed record StockRow
    {
        public string? WarehouseName { get; init; }
        public long NmId { get; init; }
        public string? SupplierArticle { get; init; }
        public string? Brand { get; init; }
        public int Quantity { get; init; }
        public int QuantityFull { get; init; }
        public int InWayToClient { get; init; }
        public int InWayFromClient { get; init; }
    }
}
