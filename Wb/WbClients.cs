namespace CustomWB_MCP.Wb;

/// <summary>
/// Имена и базовые адреса WB API по категориям. Домены выверены по официальной документации
/// dev.wildberries.ru. Каждому домену соответствует свой named HttpClient в DI.
/// </summary>
public static class WbClients
{
    public const string Content    = "wb-content";     // https://content-api.wildberries.ru
    public const string Statistics = "wb-statistics";  // https://statistics-api.wildberries.ru
    public const string Finance    = "wb-finance";     // https://finance-api.wildberries.ru
    public const string Analytics  = "wb-analytics";   // https://seller-analytics-api.wildberries.ru
    public const string Advert     = "wb-advert";      // https://advert-api.wildberries.ru
    public const string Prices     = "wb-prices";      // https://discounts-prices-api.wildberries.ru
    public const string Feedbacks  = "wb-feedbacks";   // https://feedbacks-api.wildberries.ru

    public static readonly IReadOnlyDictionary<string, string> BaseUrls = new Dictionary<string, string>
    {
        [Content]    = "https://content-api.wildberries.ru",
        [Statistics] = "https://statistics-api.wildberries.ru",
        [Finance]    = "https://finance-api.wildberries.ru",
        [Analytics]  = "https://seller-analytics-api.wildberries.ru",
        [Advert]     = "https://advert-api.wildberries.ru",
        [Prices]     = "https://discounts-prices-api.wildberries.ru",
        [Feedbacks]  = "https://feedbacks-api.wildberries.ru",
    };
}
