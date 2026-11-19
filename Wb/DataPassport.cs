namespace CustomWB_MCP.Wb;

/// <summary>
/// Статус получения данных из одного источника. Пустой ответ, отказ прав или ошибка
/// НЕ означают "ноль продаж" — именно для этого нужен паспорт данных.
/// </summary>
public enum SourceStatus
{
    Ok,              // получено
    Partial,         // частично (пагинация не завершена / частичный отказ)
    NoAccess,        // нет прав (403) или категория токена не совпадает (401)
    NeedSubscription,// нужна подписка (напр. Джем)
    NoHistory,       // история недоступна
    Error,           // ошибка запроса
    NotApplicable    // не применимо (нет через публичный API)
}

/// <summary>Одна запись паспорта данных по источнику.</summary>
public sealed class DataSource
{
    public string Source { get; init; } = "";
    public string Method { get; init; } = "";
    public string? Period { get; set; }
    public DateTime FetchedAt { get; } = DateTime.UtcNow;
    public int Records { get; set; }
    public bool PaginationComplete { get; set; } = true;
    public string Status { get; set; } = SourceStatus.Ok.ToString().ToLowerInvariant();
    public string? Limitations { get; set; }

    public void SetStatus(SourceStatus s) => Status = s switch
    {
        SourceStatus.Ok => "получено",
        SourceStatus.Partial => "частично",
        SourceStatus.NoAccess => "нет прав",
        SourceStatus.NeedSubscription => "нужна подписка",
        SourceStatus.NoHistory => "история недоступна",
        SourceStatus.Error => "ошибка",
        SourceStatus.NotApplicable => "не применимо",
        _ => "ошибка"
    };
}

/// <summary>Результат обращения к источнику: данные + запись паспорта.</summary>
public sealed class FetchResult<T>
{
    public T? Data { get; init; }
    public DataSource Passport { get; init; } = new();
    public bool Success => Passport.Status is "получено" or "частично";
}
