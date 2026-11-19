using System.ComponentModel;
using System.Text.Json;
using CustomWB_MCP.Wb;
using ModelContextProtocol.Server;

namespace CustomWB_MCP.Tools;

/// <summary>
/// Флагман: собирает единый датасет аудита по всем SKU (от полного каталога, чтобы товары
/// без продаж не выпали) + паспорт данных. Это единственный вызов, которым Claude запускает аудит.
/// Сервер только СОБИРАЕТ и считает производные метрики; выводы и рекомендации делает Claude.
/// </summary>
[McpServerToolType]
public sealed class AuditTool
{
    private readonly WbGateway _wb;
    public AuditTool(WbGateway wb) => _wb = wb;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [McpServerTool, Description(
        "Сводный датасет аудита магазина WB по всем SKU за период (today/week/month): " +
        "каталог + продажи + финансы + остатки + цены + воронка + реклама + отзывы, объединённые по nmID, " +
        "с конверсиями, ДРР, финрезультатом и предварительным признаком узкого места (bottleneck). " +
        "Плюс паспорт данных по каждому источнику. Расчёты и рекомендации строит Claude по этому датасету.")]
    public async Task<string> WbAuditDataset(
        [Description("Период: today, week или month")] string period = "month",
        [Description("Максимум строк SKU в ответе (защита контекста); 0 = все")] int maxRows = 1000,
        CancellationToken ct = default)
    {
        var passports = new List<DataSource>();

        // Собираем источники последовательно (щадим лимиты WB).
        var catalog = await _wb.GetCatalogAsync(ct);         passports.Add(catalog.Passport);
        var sales   = await _wb.GetSalesAsync(period, ct);   passports.Add(sales.Passport);
        var stocks  = await _wb.GetStocksAsync(ct);          passports.Add(stocks.Passport);
        var prices  = await _wb.GetPricesAsync(ct);          passports.Add(prices.Passport);
        var funnel  = await _wb.GetFunnelAsync(period, ct);  passports.Add(funnel.Passport);
        var ads     = await _wb.GetAdStatsAsync(period == "month" ? 28 : 7, ct); passports.Add(ads.Passport);
        var fbs     = await _wb.GetFeedbacksAsync(ct);       passports.Add(fbs.Passport);

        var from = WbGateway.PeriodStart(period);
        var fin  = await _wb.GetFinanceAsync(from, DateTime.UtcNow.Date, ct); passports.Add(fin.Passport);

        // Индексы по nmID.
        var funnelById = (funnel.Data ?? new()).GroupBy(f => f.NmId).ToDictionary(g => g.Key, g => g.First());
        var finById    = (fin.Data ?? new()).ToDictionary(f => f.NmId);
        var priceById  = (prices.Data ?? new()).GroupBy(p => p.NmId).ToDictionary(g => g.Key, g => g.First());
        var adsById     = (ads.Data ?? new()).ToDictionary(a => a.NmId);
        var fbById      = (fbs.Data ?? new()).ToDictionary(f => f.NmId);
        var stocksByNm  = (stocks.Data ?? new()).GroupBy(s => s.NmId).ToDictionary(g => g.Key, g => g.ToList());
        var salesByNm   = (sales.Data?.TopSku ?? new()).ToDictionary(s => s.NmId); // только топ; выручка общая отдельно

        // Базовая единица — каталог. Если каталог не получен, падать не будем — соберём из того, что есть.
        var catalogById = (catalog.Data ?? new())
            .GroupBy(c => c.NmId).ToDictionary(g => g.Key, g => g.First());

        var baseNmIds = (catalogById.Count > 0)
            ? catalogById.Keys.ToList()
            : funnelById.Keys.Union(finById.Keys).Union(stocksByNm.Keys).Distinct().ToList();

        var rows = new List<AuditRow>(baseNmIds.Count);
        foreach (var nm in baseNmIds)
        {
            catalogById.TryGetValue(nm, out var cat);
            funnelById.TryGetValue(nm, out var fu);
            finById.TryGetValue(nm, out var fi);
            priceById.TryGetValue(nm, out var pr);
            adsById.TryGetValue(nm, out var ad);
            fbById.TryGetValue(nm, out var fb);
            var st = stocksByNm.TryGetValue(nm, out var sl) ? sl : null;

            var row = new AuditRow
            {
                NmId = nm,
                VendorCode = cat?.VendorCode,
                Title = cat?.Title,
                Subject = cat?.Subject,
            };

            if (fu is not null)
            {
                row.OpenCard = fu.OpenCard;
                row.AddToCart = fu.AddToCart;
                row.Orders = fu.Orders;
                row.Buyouts = fu.Buyouts;
                row.CrCart = Pct(fu.AddToCart, fu.OpenCard);
                row.CrOrder = Pct(fu.Orders, fu.AddToCart);
                row.BuyoutRate = Pct(fu.Buyouts, fu.Orders);
            }

            if (fi is not null)
            {
                row.Revenue = fi.Realization;
                row.FinResult = fi.Realization - fi.Logistics - fi.Storage - fi.Penalties - fi.Deduction;
            }
            if (salesByNm.TryGetValue(nm, out var sAgg)) row.SalesCount = sAgg.Count;

            if (pr is not null) { row.Price = pr.Price; row.DiscountPercent = pr.DiscountPercent; }

            if (st is not null)
            {
                row.Stock = st.Sum(s => s.Quantity);
                row.StockByWarehouse = st.GroupBy(s => s.Warehouse)
                    .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
            }

            if (ad is not null)
            {
                row.AdSpend = ad.Spend;
                var rev = row.Revenue ?? 0m;
                row.Drr = rev > 0 ? Math.Round((double)(ad.Spend / rev) * 100, 1) : null;
            }

            if (fb is not null) row.Rating = fb.AvgRating;

            rows.Add(row);
        }

        AssignBottlenecks(rows);

        // Сортировка: сперва значимые по выручке/заказам, затем остальные.
        rows = rows
            .OrderByDescending(r => r.Revenue ?? 0m)
            .ThenByDescending(r => r.Orders ?? 0)
            .ToList();

        var truncated = maxRows > 0 && rows.Count > maxRows;
        var outRows = truncated ? rows.Take(maxRows).ToList() : rows;

        var payload = new
        {
            period,
            generatedAt = DateTime.UtcNow,
            totals = new
            {
                skuInCatalog = catalog.Data?.Count ?? 0,
                skuInDataset = rows.Count,
                salesCount = sales.Data?.SalesCount ?? 0,
                revenueAfterReturns = sales.Data?.Revenue ?? 0m,
                returns = sales.Data?.Returns ?? 0,
            },
            notes = new[]
            {
                "bottleneck — предварительная эвристика сервера, не окончательный диагноз.",
                "Воронка отстаёт на 20–30 мин; реклама не включает сегодняшний день.",
                "Себестоимость и налоги API не отдаёт — для чистой прибыли нужен CSV от пользователя.",
                truncated ? $"Показаны первые {maxRows} из {rows.Count} SKU; увеличь maxRows для полного списка." : "Показаны все SKU."
            },
            rows = outRows,
            passport = passports
        };

        return JsonSerializer.Serialize(payload, Json);
    }

    private static double? Pct(int num, int den)
        => den > 0 ? Math.Round(num * 100.0 / den, 2) : null;

    /// <summary>
    /// Предварительный признак узкого места по этажам воронки — чтобы Claude знал, куда смотреть.
    /// Пороги относительные (медиана по магазину × 0.5), помечаются как эвристика.
    /// </summary>
    private static void AssignBottlenecks(List<AuditRow> rows)
    {
        double Median(IEnumerable<double> xs)
        {
            var a = xs.Where(x => x > 0).OrderBy(x => x).ToArray();
            if (a.Length == 0) return 0;
            return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
        }

        var medOpen = Median(rows.Where(r => r.OpenCard.HasValue).Select(r => (double)r.OpenCard!.Value));
        var medCart = Median(rows.Where(r => r.CrCart.HasValue).Select(r => r.CrCart!.Value));
        var medOrd  = Median(rows.Where(r => r.CrOrder.HasValue).Select(r => r.CrOrder!.Value));
        var medBuy  = Median(rows.Where(r => r.BuyoutRate.HasValue).Select(r => r.BuyoutRate!.Value));

        foreach (var r in rows)
        {
            // Нет остатка, но спрос был → доступность.
            if ((r.Stock ?? 0) == 0 && (r.Orders ?? 0) > 0) { r.Bottleneck = "stock_availability"; r.Note = "нет остатка при наличии заказов"; continue; }

            if (r.OpenCard is null) { r.Bottleneck = "insufficient_data"; r.Note = "нет данных воронки"; continue; }

            // Сверху вниз: первый этаж, где метрика сильно ниже медианы.
            if (medOpen > 0 && r.OpenCard < medOpen * 0.5) { r.Bottleneck = "visibility"; r.Note = "мало показов относительно магазина"; continue; }
            if (r.CrCart is { } c && medCart > 0 && c < medCart * 0.5) { r.Bottleneck = "content_or_price"; r.Note = "низкая конверсия показ→корзина"; continue; }
            if (r.CrOrder is { } o && medOrd > 0 && o < medOrd * 0.5) { r.Bottleneck = "price_or_reviews"; r.Note = "низкая конверсия корзина→заказ"; continue; }
            if (r.BuyoutRate is { } b && medBuy > 0 && b < medBuy * 0.5) { r.Bottleneck = "logistics_or_quality"; r.Note = "низкий выкуп (учесть зрелость заказов)"; continue; }

            if ((r.OpenCard ?? 0) == 0) { r.Bottleneck = "no_measured_traffic"; r.Note = "нет измеренных переходов за период"; continue; }

            r.Bottleneck = "stable";
        }
    }
}
