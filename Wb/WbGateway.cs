using System.Globalization;
using System.Text.Json.Nodes;

namespace CustomWB_MCP.Wb;

/// <summary>
/// Единая точка доступа к WB API (только чтение). Каждый метод возвращает FetchResult:
/// типизированные данные + запись паспорта (что получено / частично / нет прав / ошибка).
/// Расчёты (юнит-экономика, ABC, диагностика) здесь НЕ делаются — это задача Claude в диалоге.
/// </summary>
public sealed class WbGateway
{
    private readonly IHttpClientFactory _factory;
    public WbGateway(IHttpClientFactory factory) => _factory = factory;

    private HttpClient Client(string name) => _factory.CreateClient(name);

    private static string Rfc3339(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
    private static string Day(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateTime PeriodStart(string period) => period.ToLowerInvariant() switch
    {
        "today" => DateTime.UtcNow.Date,
        "month" => DateTime.UtcNow.Date.AddDays(-30),
        _ => DateTime.UtcNow.Date.AddDays(-7),
    };

    // ---------- Каталог (Контент) ----------
    public async Task<FetchResult<List<CatalogItem>>> GetCatalogAsync(CancellationToken ct)
    {
        var passport = new DataSource { Source = "Каталог", Method = "POST /content/v2/get/cards/list" };
        var items = new List<CatalogItem>();
        var client = Client(WbClients.Content);
        JsonNode? cursor = null;
        const int limit = 100;

        try
        {
            for (var page = 0; page < 500; page++) // жёсткий предохранитель от бесконечной пагинации
            {
                var settings = new JsonObject
                {
                    ["cursor"] = new JsonObject { ["limit"] = limit },
                    ["filter"] = new JsonObject { ["withPhoto"] = -1 }
                };
                if (cursor is not null)
                {
                    settings["cursor"]!["updatedAt"] = cursor.GetStr("updatedAt");
                    settings["cursor"]!["nmID"] = cursor.GetLong("nmID");
                }
                var body = new JsonObject { ["settings"] = settings };

                var root = await WbHttp.PostJsonAsync(client, "/content/v2/get/cards/list", body, ct);
                var cards = root.Pick("cards").AsArraySafe();
                foreach (var c in cards)
                {
                    var sizes = c.Pick("sizes").AsArraySafe()
                        .Select(s => s.GetStr("techSize", "wbSize") ?? "")
                        .Where(s => s.Length > 0).ToList();
                    items.Add(new CatalogItem
                    {
                        NmId = c.GetLong("nmID"),
                        VendorCode = c.GetStr("vendorCode"),
                        Title = c.GetStr("title"),
                        Subject = c.GetStr("subjectName"),
                        Brand = c.GetStr("brand"),
                        CreatedAt = c.GetDate("createdAt"),
                        Sizes = sizes
                    });
                }

                cursor = root.Pick("cursor");
                if (cards.Count < limit) break; // последняя страница
            }
            passport.Records = items.Count;
            passport.SetStatus(SourceStatus.Ok);
        }
        catch (WbHttp.WbCallException ex) { passport.SetStatus(ex.Status); passport.Limitations = ex.Message; passport.PaginationComplete = false; }
        catch (Exception ex) { passport.SetStatus(SourceStatus.Error); passport.Limitations = ex.Message; passport.PaginationComplete = false; }

        return new FetchResult<List<CatalogItem>> { Data = items, Passport = passport };
    }

    // ---------- Продажи (Статистика) ----------
    public async Task<FetchResult<SalesSummary>> GetSalesAsync(string period, CancellationToken ct)
    {
        var from = PeriodStart(period);
        var passport = new DataSource
        {
            Source = "Продажи", Method = "GET /api/v1/supplier/sales",
            Period = $"{Day(from)}..{Day(DateTime.UtcNow)}"
        };
        var summary = new SalesSummary { Period = period };
        try
        {
            var root = await WbHttp.GetJsonAsync(Client(WbClients.Statistics),
                $"/api/v1/supplier/sales?dateFrom={Rfc3339(from)}", ct);
            var rows = root.AsArraySafe();
            var byNm = new Dictionary<long, SkuAgg>();
            foreach (var r in rows)
            {
                var saleId = r.GetStr("saleID") ?? "";
                var isReturn = saleId.StartsWith("R", StringComparison.OrdinalIgnoreCase);
                var forPay = r.GetDecimal("forPay");
                var nm = r.GetLong("nmId");
                if (isReturn) { summary.Returns++; summary.Revenue -= forPay; }
                else { summary.SalesCount++; summary.Revenue += forPay; }

                if (!byNm.TryGetValue(nm, out var agg))
                    byNm[nm] = agg = new SkuAgg { NmId = nm, VendorCode = r.GetStr("supplierArticle") };
                agg.Count += isReturn ? -1 : 1;
                agg.Sum += isReturn ? -forPay : forPay;
            }
            summary.TopSku = byNm.Values.OrderByDescending(a => a.Sum).Take(5).ToList();
            passport.Records = rows.Count;
            passport.SetStatus(SourceStatus.Ok);
        }
        catch (WbHttp.WbCallException ex) { passport.SetStatus(ex.Status); passport.Limitations = ex.Message; }
        catch (Exception ex) { passport.SetStatus(SourceStatus.Error); passport.Limitations = ex.Message; }

        return new FetchResult<SalesSummary> { Data = summary, Passport = passport };
    }

    // ---------- Остатки (Статистика) ----------
    public async Task<FetchResult<List<StockRow>>> GetStocksAsync(CancellationToken ct)
    {
        var from = DateTime.UtcNow.Date.AddDays(-1);
        var passport = new DataSource { Source = "Остатки", Method = "GET /api/v1/supplier/stocks" };
        var list = new List<StockRow>();
        try
        {
            var root = await WbHttp.GetJsonAsync(Client(WbClients.Statistics),
                $"/api/v1/supplier/stocks?dateFrom={Rfc3339(from)}", ct);
            foreach (var r in root.AsArraySafe())
                list.Add(new StockRow
                {
                    NmId = r.GetLong("nmId"),
                    VendorCode = r.GetStr("supplierArticle"),
                    Warehouse = r.GetStr("warehouseName") ?? "—",
                    Quantity = r.GetInt("quantity"),
                    InWayToClient = r.GetInt("inWayToClient"),
                    InWayFromClient = r.GetInt("inWayFromClient"),
                });
            passport.Records = list.Count;
            passport.SetStatus(SourceStatus.Ok);
        }
        catch (WbHttp.WbCallException ex) { passport.SetStatus(ex.Status); passport.Limitations = ex.Message; }
        catch (Exception ex) { passport.SetStatus(SourceStatus.Error); passport.Limitations = ex.Message; }

        return new FetchResult<List<StockRow>> { Data = list, Passport = passport };
    }

    // ---------- Финансовая детализация (Статистика, reportDetailByPeriod) ----------
    // Примечание: используется проверенный statistics-api v5. Альтернатива — finance-api
    // /api/finance/v1/sales-reports/detailed; если v5 отдаёт 404 на кабинете — сверить со Swagger.
    public async Task<FetchResult<List<FinanceRow>>> GetFinanceAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var passport = new DataSource
        {
            Source = "Финдетализация", Method = "GET /api/v5/supplier/reportDetailByPeriod",
            Period = $"{Day(from)}..{Day(to)}"
        };
        var byNm = new Dictionary<long, FinanceRow>();
        long rrdid = 0;
        try
        {
            for (var page = 0; page < 1000; page++)
            {
                var root = await WbHttp.GetJsonAsync(Client(WbClients.Statistics),
                    $"/api/v5/supplier/reportDetailByPeriod?dateFrom={Day(from)}&dateTo={Day(to)}&rrdid={rrdid}&limit=100000", ct);
                var rows = root.AsArraySafe();
                if (rows.Count == 0) break;

                foreach (var r in rows)
                {
                    var nm = r.GetLong("nm_id");
                    if (!byNm.TryGetValue(nm, out var fr)) byNm[nm] = fr = new FinanceRow { NmId = nm };
                    fr.Realization += r.GetDecimal("ppvz_for_pay");
                    fr.Logistics   += r.GetDecimal("delivery_rub");
                    fr.Storage     += r.GetDecimal("storage_fee");
                    fr.Penalties   += r.GetDecimal("penalty");
                    fr.Deduction   += r.GetDecimal("deduction") + r.GetDecimal("acquiring_fee");
                    fr.Commission  += r.GetDecimal("retail_price_withdisc_rub") - r.GetDecimal("ppvz_for_pay");
                    rrdid = r.GetLong("rrd_id");
                }
                passport.Records += rows.Count;
            }
            passport.SetStatus(SourceStatus.Ok);
        }
        catch (WbHttp.WbCallException ex) { passport.SetStatus(ex.Status); passport.Limitations = ex.Message; passport.PaginationComplete = false; }
        catch (Exception ex) { passport.SetStatus(SourceStatus.Error); passport.Limitations = ex.Message; passport.PaginationComplete = false; }

        return new FetchResult<List<FinanceRow>> { Data = byNm.Values.ToList(), Passport = passport };
    }

    // ---------- Цены и скидки ----------
    public async Task<FetchResult<List<PriceRow>>> GetPricesAsync(CancellationToken ct)
    {
        var passport = new DataSource { Source = "Цены", Method = "GET /api/v2/list/goods/filter" };
        var list = new List<PriceRow>();
        try
        {
            var offset = 0; const int limit = 1000;
            for (var page = 0; page < 200; page++)
            {
                var root = await WbHttp.GetJsonAsync(Client(WbClients.Prices),
                    $"/api/v2/list/goods/filter?limit={limit}&offset={offset}", ct);
                var goods = root.Pick("data").Pick("listGoods").AsArraySafe();
                if (goods.Count == 0) break;
                foreach (var g in goods)
                {
                    var size = g.Pick("sizes").AsArraySafe().FirstOrDefault();
                    list.Add(new PriceRow
                    {
                        NmId = g.GetLong("nmID"),
                        Price = size.GetDecimal("discountedPrice", "price"),
                        DiscountPercent = g.GetInt("discount"),
                    });
                }
                offset += limit;
                if (goods.Count < limit) break;
            }
            passport.Records = list.Count;
            passport.SetStatus(SourceStatus.Ok);
        }
        catch (WbHttp.WbCallException ex) { passport.SetStatus(ex.Status); passport.Limitations = ex.Message; }
        catch (Exception ex) { passport.SetStatus(SourceStatus.Error); passport.Limitations = ex.Message; }

        return new FetchResult<List<PriceRow>> { Data = list, Passport = passport };
    }

    // ---------- Воронка (Аналитика) ----------
    public async Task<FetchResult<List<FunnelRow>>> GetFunnelAsync(string period, CancellationToken ct)
    {
        var from = PeriodStart(period);
        var to = DateTime.UtcNow.Date;
        var passport = new DataSource
        {
            Source = "Воронка", Method = "POST /api/analytics/v3/sales-funnel/products",
            Period = $"{Day(from)}..{Day(to)}",
            Limitations = "данные воронки отстают на 20–30 минут"
        };
        var list = new List<FunnelRow>();
        try
        {
            var page = 1;
            for (var i = 0; i < 100; i++)
            {
                var body = new JsonObject
                {
                    ["period"] = new JsonObject { ["begin"] = Day(from), ["end"] = Day(to) },
                    ["page"] = page
                };
                var root = await WbHttp.PostJsonAsync(Client(WbClients.Analytics),
                    "/api/analytics/v3/sales-funnel/products", body, ct);
                var products = root.Pick("data").Pick("products").AsArraySafe();
                foreach (var p in products)
                {
                    var stat = p.Pick("statistic").Pick("selected");
                    list.Add(new FunnelRow
                    {
                        NmId = p.GetLong("nmID", "nmId"),
                        OpenCard = stat.GetInt("openCardCount", "openCard"),
                        AddToCart = stat.GetInt("addToCartCount", "addToCart"),
                        Orders = stat.GetInt("ordersCount", "orderCount", "orders"),
                        Buyouts = stat.GetInt("buyoutsCount", "buyouts"),
                        OrdersSum = stat.GetDecimal("ordersSumRub", "orderSum", "ordersSum"),
                    });
                }
                var hasNext = string.Equals(root.Pick("data").Pick("isNextPage")?.ToString(),
                    "true", StringComparison.OrdinalIgnoreCase);
                if (!hasNext || products.Count == 0) break;
                page++;
                await Task.Delay(300, ct); // мягкая пауза под лимиты
            }
            passport.Records = list.Count;
            passport.SetStatus(SourceStatus.Ok);
        }
        catch (WbHttp.WbCallException ex) { passport.SetStatus(ex.Status); passport.Limitations += $"; {ex.Message}"; }
        catch (Exception ex) { passport.SetStatus(SourceStatus.Error); passport.Limitations = ex.Message; }

        return new FetchResult<List<FunnelRow>> { Data = list, Passport = passport };
    }

    // ---------- Реклама (Продвижение) ----------
    public async Task<FetchResult<List<AdRow>>> GetAdStatsAsync(int daysBack, CancellationToken ct)
    {
        // Реклама не отдаёт сегодняшний день: берём прошлые дни.
        var end = DateTime.UtcNow.Date.AddDays(-1);
        var begin = end.AddDays(-Math.Max(1, daysBack - 1));
        var passport = new DataSource
        {
            Source = "Реклама", Method = "GET /adv/v1/promotion/adverts + /adv/v3/fullstats",
            Period = $"{Day(begin)}..{Day(end)}",
            Limitations = "статистика за сегодня не отдаётся"
        };
        var byNm = new Dictionary<long, AdRow>();
        try
        {
            // 1) собрать id кампаний
            var advRoot = await WbHttp.GetJsonAsync(Client(WbClients.Advert), "/adv/v1/promotion/adverts", ct);
            var ids = new List<long>();
            foreach (var group in advRoot.AsArraySafe())
                foreach (var a in group.Pick("advert_list").AsArraySafe())
                    ids.Add(a.GetLong("advertId"));

            if (ids.Count == 0)
            {
                passport.SetStatus(SourceStatus.Ok);
                passport.Limitations += "; активных кампаний не найдено";
                return new FetchResult<List<AdRow>> { Data = new(), Passport = passport };
            }

            // 2) статистика батчами
            foreach (var batch in ids.Chunk(50))
            {
                var idsParam = string.Join(",", batch);
                var root = await WbHttp.GetJsonAsync(Client(WbClients.Advert),
                    $"/adv/v3/fullstats?ids={idsParam}&beginDate={Day(begin)}&endDate={Day(end)}", ct);
                foreach (var adv in root.AsArraySafe())
                    foreach (var day in adv.Pick("days").AsArraySafe())
                        foreach (var app in day.Pick("apps").AsArraySafe())
                            foreach (var nm in app.Pick("nms").AsArraySafe())
                            {
                                var id = nm.GetLong("nmId");
                                if (!byNm.TryGetValue(id, out var row)) byNm[id] = row = new AdRow { NmId = id };
                                row.Spend += nm.GetDecimal("sum");
                                row.Views += nm.GetInt("views");
                                row.Clicks += nm.GetInt("clicks");
                                row.Orders += nm.GetInt("orders");
                            }
                await Task.Delay(300, ct);
            }
            passport.Records = byNm.Count;
            passport.SetStatus(SourceStatus.Ok);
        }
        catch (WbHttp.WbCallException ex) { passport.SetStatus(ex.Status); passport.Limitations += $"; {ex.Message}"; }
        catch (Exception ex) { passport.SetStatus(SourceStatus.Error); passport.Limitations = ex.Message; }

        return new FetchResult<List<AdRow>> { Data = byNm.Values.ToList(), Passport = passport };
    }

    // ---------- Отзывы (Отзывы и вопросы) ----------
    public async Task<FetchResult<List<FeedbackAgg>>> GetFeedbacksAsync(CancellationToken ct)
    {
        var passport = new DataSource { Source = "Отзывы", Method = "GET /api/v1/feedbacks" };
        var byNm = new Dictionary<long, (double sum, int cnt)>();
        try
        {
            foreach (var answered in new[] { true, false })
            {
                var skip = 0; const int take = 5000;
                for (var page = 0; page < 50; page++)
                {
                    var root = await WbHttp.GetJsonAsync(Client(WbClients.Feedbacks),
                        $"/api/v1/feedbacks?isAnswered={answered.ToString().ToLowerInvariant()}&take={take}&skip={skip}", ct);
                    var fbs = root.Pick("data").Pick("feedbacks").AsArraySafe();
                    if (fbs.Count == 0) break;
                    foreach (var f in fbs)
                    {
                        var nm = f.Pick("productDetails").GetLong("nmId");
                        var rating = f.GetInt("productValuation");
                        var cur = byNm.TryGetValue(nm, out var v) ? v : (0d, 0);
                        byNm[nm] = (cur.Item1 + rating, cur.Item2 + 1);
                    }
                    skip += take;
                    if (fbs.Count < take) break;
                    await Task.Delay(300, ct);
                }
            }
            var list = byNm.Select(kv => new FeedbackAgg
            {
                NmId = kv.Key,
                Count = kv.Value.cnt,
                AvgRating = kv.Value.cnt > 0 ? Math.Round(kv.Value.sum / kv.Value.cnt, 2) : 0
            }).ToList();
            passport.Records = list.Count;
            passport.SetStatus(SourceStatus.Ok);
            return new FetchResult<List<FeedbackAgg>> { Data = list, Passport = passport };
        }
        catch (WbHttp.WbCallException ex) { passport.SetStatus(ex.Status); passport.Limitations = ex.Message; }
        catch (Exception ex) { passport.SetStatus(SourceStatus.Error); passport.Limitations = ex.Message; }

        return new FetchResult<List<FeedbackAgg>> { Data = new(), Passport = passport };
    }

    // ---------- Заказы (Статистика) ----------
    public async Task<FetchResult<OrdersSummary>> GetOrdersAsync(string period, CancellationToken ct)
    {
        var from = PeriodStart(period);
        var passport = new DataSource
        {
            Source = "Заказы", Method = "GET /api/v1/supplier/orders",
            Period = $"{Day(from)}..{Day(DateTime.UtcNow)}"
        };
        var s = new OrdersSummary { Period = period };
        try
        {
            var root = await WbHttp.GetJsonAsync(Client(WbClients.Statistics),
                $"/api/v1/supplier/orders?dateFrom={Rfc3339(from)}", ct);
            var rows = root.AsArraySafe();
            var byNm = new Dictionary<long, SkuAgg>();
            foreach (var r in rows)
            {
                var canceled = string.Equals(r.Pick("isCancel")?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
                var sum = r.GetDecimal("priceWithDisc");
                var nm = r.GetLong("nmId");
                if (canceled) s.Canceled++;
                else { s.OrdersCount++; s.OrdersSum += sum; }
                if (!byNm.TryGetValue(nm, out var a))
                    byNm[nm] = a = new SkuAgg { NmId = nm, VendorCode = r.GetStr("supplierArticle") };
                if (!canceled) { a.Count++; a.Sum += sum; }
            }
            s.TopSku = byNm.Values.OrderByDescending(a => a.Sum).Take(5).ToList();
            passport.Records = rows.Count;
            passport.SetStatus(SourceStatus.Ok);
        }
        catch (WbHttp.WbCallException ex) { passport.SetStatus(ex.Status); passport.Limitations = ex.Message; }
        catch (Exception ex) { passport.SetStatus(SourceStatus.Error); passport.Limitations = ex.Message; }

        return new FetchResult<OrdersSummary> { Data = s, Passport = passport };
    }

    // ---------- Поставки / в пути (Статистика) ----------
    public async Task<FetchResult<List<IncomeRow>>> GetIncomesAsync(string period, CancellationToken ct)
    {
        var from = PeriodStart(period);
        var passport = new DataSource { Source = "Поставки", Method = "GET /api/v1/supplier/incomes", Period = $"{Day(from)}.." };
        var list = new List<IncomeRow>();
        try
        {
            var root = await WbHttp.GetJsonAsync(Client(WbClients.Statistics),
                $"/api/v1/supplier/incomes?dateFrom={Rfc3339(from)}", ct);
            foreach (var r in root.AsArraySafe())
                list.Add(new IncomeRow
                {
                    NmId = r.GetLong("nmId"),
                    VendorCode = r.GetStr("supplierArticle"),
                    Warehouse = r.GetStr("warehouseName") ?? "—",
                    Quantity = r.GetInt("quantity"),
                    Date = r.GetDate("date", "dateClose"),
                    Status = r.GetStr("status"),
                });
            passport.Records = list.Count;
            passport.SetStatus(SourceStatus.Ok);
        }
        catch (WbHttp.WbCallException ex) { passport.SetStatus(ex.Status); passport.Limitations = ex.Message; }
        catch (Exception ex) { passport.SetStatus(SourceStatus.Error); passport.Limitations = ex.Message; }

        return new FetchResult<List<IncomeRow>> { Data = list, Passport = passport };
    }

    // ---------- Остатки по складам + оборачиваемость (Аналитика, async) ----------
    public async Task<FetchResult<List<WarehouseRemainRow>>> GetWarehouseRemainsAsync(CancellationToken ct)
    {
        var passport = new DataSource { Source = "Остатки по складам", Method = "GET /api/v1/warehouse_remains (async)" };
        var list = new List<WarehouseRemainRow>();
        try
        {
            var data = await PollReportAsync(Client(WbClients.Analytics),
                "/api/v1/warehouse_remains?groupByNm=true&groupByBrand=false&groupBySubject=false&groupBySa=false&groupBySize=false",
                "/api/v1/warehouse_remains/tasks/{0}/status",
                "/api/v1/warehouse_remains/tasks/{0}/download", ct);

            foreach (var r in data.AsArraySafe())
            {
                var byWh = new Dictionary<string, int>();
                foreach (var w in r.Pick("warehouses").AsArraySafe())
                {
                    var name = w.GetStr("warehouseName") ?? "—";
                    byWh[name] = byWh.GetValueOrDefault(name) + w.GetInt("quantity");
                }
                list.Add(new WarehouseRemainRow
                {
                    NmId = r.GetLong("nmId"),
                    VendorCode = r.GetStr("vendorCode"),
                    Subject = r.GetStr("subjectName"),
                    Total = byWh.Values.Sum(),
                    ByWarehouse = byWh,
                });
            }
            passport.Records = list.Count;
            passport.SetStatus(SourceStatus.Ok);
        }
        catch (WbHttp.WbCallException ex) { passport.SetStatus(ex.Status); passport.Limitations = ex.Message; }
        catch (Exception ex) { passport.SetStatus(SourceStatus.Error); passport.Limitations = ex.Message; }

        return new FetchResult<List<WarehouseRemainRow>> { Data = list, Passport = passport };
    }

    // ---------- Платное хранение (Аналитика, async) ----------
    public async Task<FetchResult<List<PaidStorageAgg>>> GetPaidStorageAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var passport = new DataSource
        {
            Source = "Платное хранение", Method = "GET /api/v1/paid_storage (async)",
            Period = $"{Day(from)}..{Day(to)}"
        };
        var byNm = new Dictionary<long, decimal>();
        try
        {
            var data = await PollReportAsync(Client(WbClients.Analytics),
                $"/api/v1/paid_storage?dateFrom={Day(from)}&dateTo={Day(to)}",
                "/api/v1/paid_storage/tasks/{0}/status",
                "/api/v1/paid_storage/tasks/{0}/download", ct);

            foreach (var r in data.AsArraySafe())
            {
                var nm = r.GetLong("nmId");
                byNm[nm] = byNm.GetValueOrDefault(nm) + r.GetDecimal("warehousePrice");
            }
            passport.Records = byNm.Count;
            passport.SetStatus(SourceStatus.Ok);
        }
        catch (WbHttp.WbCallException ex) { passport.SetStatus(ex.Status); passport.Limitations = ex.Message; }
        catch (Exception ex) { passport.SetStatus(SourceStatus.Error); passport.Limitations = ex.Message; }

        return new FetchResult<List<PaidStorageAgg>>
        {
            Data = byNm.Select(kv => new PaidStorageAgg { NmId = kv.Key, StorageCost = Math.Round(kv.Value, 2) }).ToList(),
            Passport = passport
        };
    }

    // ---------- Еженедельный P&L (Статистика, из детализации по realizationreport_id) ----------
    public async Task<FetchResult<List<WeeklyPnlRow>>> GetWeeklyPnlAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var passport = new DataSource
        {
            Source = "Еженедельный P&L", Method = "GET /api/v5/supplier/reportDetailByPeriod (группировка по неделям)",
            Period = $"{Day(from)}..{Day(to)}",
            Limitations = "результат ДО себестоимости и налогов — для чистой прибыли нужен CSV себестоимости"
        };
        var byReport = new Dictionary<long, WeeklyPnlRow>();
        long rrdid = 0;
        try
        {
            for (var page = 0; page < 1000; page++)
            {
                var root = await WbHttp.GetJsonAsync(Client(WbClients.Statistics),
                    $"/api/v5/supplier/reportDetailByPeriod?dateFrom={Day(from)}&dateTo={Day(to)}&rrdid={rrdid}&limit=100000", ct);
                var rows = root.AsArraySafe();
                if (rows.Count == 0) break;

                foreach (var r in rows)
                {
                    var reportId = r.GetLong("realizationreport_id");
                    if (!byReport.TryGetValue(reportId, out var w))
                        byReport[reportId] = w = new WeeklyPnlRow
                        {
                            ReportId = reportId,
                            DateFrom = r.GetDate("date_from"),
                            DateTo = r.GetDate("date_to"),
                        };
                    var forPay = r.GetDecimal("ppvz_for_pay");
                    var retail = r.GetDecimal("retail_price_withdisc_rub", "retail_amount");
                    w.Retail += retail;
                    w.ForPay += forPay;
                    w.Commission += retail - forPay;
                    w.Logistics += r.GetDecimal("delivery_rub");
                    w.Storage += r.GetDecimal("storage_fee");
                    w.Penalties += r.GetDecimal("penalty");
                    w.Deduction += r.GetDecimal("deduction") + r.GetDecimal("acquiring_fee");
                    w.Rows++;
                    rrdid = r.GetLong("rrd_id");
                }
                passport.Records += rows.Count;
            }
            foreach (var w in byReport.Values)
                w.ResultBeforeCogs = w.ForPay - w.Logistics - w.Storage - w.Penalties - w.Deduction;
            passport.SetStatus(SourceStatus.Ok);
        }
        catch (WbHttp.WbCallException ex) { passport.SetStatus(ex.Status); passport.Limitations += $"; {ex.Message}"; passport.PaginationComplete = false; }
        catch (Exception ex) { passport.SetStatus(SourceStatus.Error); passport.Limitations = ex.Message; passport.PaginationComplete = false; }

        return new FetchResult<List<WeeklyPnlRow>>
        {
            Data = byReport.Values.OrderBy(w => w.DateFrom).ToList(),
            Passport = passport
        };
    }

    // ---------- Общий помощник для асинхронных отчётов WB (create -> status -> download) ----------
    private static async Task<JsonNode?> PollReportAsync(
        HttpClient client, string createPath, string statusPathFmt, string downloadPathFmt, CancellationToken ct)
    {
        var created = await WbHttp.GetJsonAsync(client, createPath, ct);
        var taskId = created.Pick("data").GetStr("taskId") ?? created.GetStr("taskId");
        if (string.IsNullOrEmpty(taskId))
            throw new WbHttp.WbCallException(SourceStatus.Error, "не удалось создать отчёт (нет taskId)");

        for (var i = 0; i < 30; i++)   // ~2.5 мин максимум
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            var st = await WbHttp.GetJsonAsync(client, string.Format(statusPathFmt, taskId), ct);
            var status = st.Pick("data").GetStr("status") ?? st.GetStr("status");
            if (string.Equals(status, "done", StringComparison.OrdinalIgnoreCase))
                return await WbHttp.GetJsonAsync(client, string.Format(downloadPathFmt, taskId), ct);
            if (string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status, "purged", StringComparison.OrdinalIgnoreCase))
                throw new WbHttp.WbCallException(SourceStatus.Error, $"отчёт отменён WB (status={status})");
        }
        throw new WbHttp.WbCallException(SourceStatus.Partial, "отчёт не готов за отведённое время");
    }
}
