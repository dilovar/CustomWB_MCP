namespace CustomWB_MCP.Wb;

// Компактные модели — то, что уходит в Claude. Сырые массивы WB наружу не отдаём.

public sealed class CatalogItem
{
    public long NmId { get; set; }
    public string? VendorCode { get; set; }
    public string? Title { get; set; }
    public string? Subject { get; set; }
    public string? Brand { get; set; }
    public DateTime? CreatedAt { get; set; }
    public List<string> Sizes { get; set; } = new();
}

public sealed class SalesSummary
{
    public string Period { get; set; } = "";
    public int SalesCount { get; set; }
    public decimal Revenue { get; set; }        // сумма к перечислению продавцу (forPay), после возвратов
    public int Returns { get; set; }
    public List<SkuAgg> TopSku { get; set; } = new();
}

public sealed class SkuAgg
{
    public long NmId { get; set; }
    public string? VendorCode { get; set; }
    public int Count { get; set; }
    public decimal Sum { get; set; }
}

public sealed class StockRow
{
    public long NmId { get; set; }
    public string? VendorCode { get; set; }
    public string Warehouse { get; set; } = "";
    public int Quantity { get; set; }      // доступно к продаже
    public int InWayToClient { get; set; }
    public int InWayFromClient { get; set; }
}

public sealed class FinanceRow
{
    public long NmId { get; set; }
    public decimal Realization { get; set; }   // ppvz_for_pay — к перечислению
    public decimal Commission { get; set; }
    public decimal Logistics { get; set; }
    public decimal Storage { get; set; }
    public decimal Penalties { get; set; }
    public decimal Deduction { get; set; }
}

public sealed class PriceRow
{
    public long NmId { get; set; }
    public decimal Price { get; set; }
    public int DiscountPercent { get; set; }
}

public sealed class FunnelRow
{
    public long NmId { get; set; }
    public int OpenCard { get; set; }        // переходы в карточку
    public int AddToCart { get; set; }       // корзины
    public int Orders { get; set; }          // заказы
    public int Buyouts { get; set; }         // выкупы
    public decimal OrdersSum { get; set; }
}

public sealed class AdRow
{
    public long NmId { get; set; }
    public decimal Spend { get; set; }
    public int Views { get; set; }
    public int Clicks { get; set; }
    public int Orders { get; set; }
}

public sealed class FeedbackAgg
{
    public long NmId { get; set; }
    public double AvgRating { get; set; }
    public int Count { get; set; }
}

public sealed class OrdersSummary
{
    public string Period { get; set; } = "";
    public int OrdersCount { get; set; }
    public decimal OrdersSum { get; set; }   // сумма заказов (priceWithDisc), НЕ выручка
    public int Canceled { get; set; }
    public List<SkuAgg> TopSku { get; set; } = new();
}

public sealed class IncomeRow
{
    public long NmId { get; set; }
    public string? VendorCode { get; set; }
    public string Warehouse { get; set; } = "";
    public int Quantity { get; set; }
    public DateTime? Date { get; set; }
    public string? Status { get; set; }
}

public sealed class WarehouseRemainRow
{
    public long NmId { get; set; }
    public string? VendorCode { get; set; }
    public string? Subject { get; set; }
    public int Total { get; set; }                               // суммарно по всем складам
    public Dictionary<string, int> ByWarehouse { get; set; } = new();
}

public sealed class PaidStorageAgg
{
    public long NmId { get; set; }
    public decimal StorageCost { get; set; }   // суммарная стоимость платного хранения за период
}

/// <summary>Один недельный отчёт о реализации WB (P&L до себестоимости).</summary>
public sealed class WeeklyPnlRow
{
    public long ReportId { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public decimal Retail { get; set; }        // розничная сумма с учётом скидок
    public decimal ForPay { get; set; }        // к перечислению продавцу (после комиссии WB)
    public decimal Commission { get; set; }    // Retail - ForPay (доля WB), оценочно
    public decimal Logistics { get; set; }
    public decimal Storage { get; set; }
    public decimal Penalties { get; set; }
    public decimal Deduction { get; set; }
    public decimal ResultBeforeCogs { get; set; } // ForPay - Logistics - Storage - Penalties - Deduction
    public int Rows { get; set; }
}

/// <summary>Строка сводной таблицы аудита по одному SKU.</summary>
public sealed class AuditRow
{
    public long NmId { get; set; }
    public string? VendorCode { get; set; }
    public string? Title { get; set; }
    public string? Subject { get; set; }

    public int? SalesCount { get; set; }
    public decimal? Revenue { get; set; }
    public decimal? FinResult { get; set; }   // realization - comm - logistics - storage - penalties

    public int? OpenCard { get; set; }
    public int? AddToCart { get; set; }
    public int? Orders { get; set; }
    public int? Buyouts { get; set; }
    public double? CrCart { get; set; }       // корзина/показы, %
    public double? CrOrder { get; set; }      // заказ/корзина, %
    public double? BuyoutRate { get; set; }   // выкуп/заказы, %

    public int? Stock { get; set; }
    public Dictionary<string, int>? StockByWarehouse { get; set; }

    public decimal? Price { get; set; }
    public int? DiscountPercent { get; set; }

    public decimal? AdSpend { get; set; }
    public double? Drr { get; set; }          // расход рекламы / выручка, %

    public double? Rating { get; set; }

    public string Bottleneck { get; set; } = "unknown";
    public string? Note { get; set; }
}
